"""Fail-closed release directory inspection against the generated bundle manifest."""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import re
import sys

_NOTICE_CONTRACT_SPEC = importlib.util.spec_from_file_location(
    "_portable_notice_contract", Path(__file__).with_name("portable_notice_contract.py")
)
if _NOTICE_CONTRACT_SPEC is None or _NOTICE_CONTRACT_SPEC.loader is None:
    raise RuntimeError("portable notice contract could not be loaded")
_NOTICE_CONTRACT = importlib.util.module_from_spec(_NOTICE_CONTRACT_SPEC)
_NOTICE_CONTRACT_SPEC.loader.exec_module(_NOTICE_CONTRACT)
validate_notice_inventory = _NOTICE_CONTRACT.validate_notice_inventory


_TEXT_SUFFIXES = {".bat", ".cfg", ".conf", ".css", ".csv", ".html", ".ini", ".js", ".json", ".md",
                  ".manifest", ".nuspec", ".ps1", ".py", ".sh", ".sql", ".toml", ".tsv", ".txt", ".xml", ".yaml", ".yml"}
_DATA_NAMES = {"cache", "criteria", "data", "jsonl", "logs", "sessions", "state", "task_stats", "user-data"}
_SENSITIVE_NAMES = {".env", "config.json", "credentials.json", "secrets.json", "tokens.json", "recovery-keys.json"}
_WINDOWS_RESERVED = {"con", "prn", "aux", "nul", *(f"com{i}" for i in range(1, 10)), *(f"lpt{i}" for i in range(1, 10))}
_HASH = re.compile(r"^[0-9a-f]{64}$")
# This one official PostgreSQL 17.11-3 Windows archive notice is hash-locked,
# retained byte-for-byte, and uses a legacy encoding. No other notice is exempt.
_PINNED_LEGACY_NOTICE_HASHES = {
    "postgres/commandlinetools_3rd_party_licenses.txt":
        "67181bbd5ddb5a0094aa9c82b97536a27811461a3b61c3c11588a2731cfc7b3b",
}
_PYTHON_LOCK_PATH = Path(__file__).with_name("requirements-python.lock.txt")


class ScanError(Exception):
    pass


def _safe_path(value: str) -> str:
    if not isinstance(value, str) or not value or "\\" in value or ":" in value or "\x00" in value:
        raise ScanError("manifest contains an unsafe path")
    path = PurePosixPath(value)
    if path.is_absolute() or any(part in {"", ".", ".."} for part in value.split("/")):
        raise ScanError("manifest contains an unsafe path")
    for part in path.parts:
        if part.endswith((".", " ")) or any(ord(char) < 32 for char in part):
            raise ScanError("manifest contains a non-portable path")
        if part.split(".", 1)[0].casefold() in _WINDOWS_RESERVED:
            raise ScanError("manifest contains a Windows reserved path")
    return path.as_posix()


def _is_reparse(path: Path) -> bool:
    try:
        return path.is_symlink() or (hasattr(os.path, "isjunction") and os.path.isjunction(path))
    except OSError as exc:
        raise ScanError("release tree could not be inspected safely") from exc


def scan(root: Path) -> dict[str, object]:
    lexical_root = Path(os.path.abspath(root))
    cursor = lexical_root
    while cursor != cursor.parent:
        if _is_reparse(cursor):
            raise ScanError("release root or one of its ancestors is a reparse point")
        cursor = cursor.parent
    root = lexical_root.resolve(strict=True)
    manifest_path = root / "bundle-manifest.json"
    current_path = root / "current.json"
    findings: list[dict[str, str]] = []
    if not manifest_path.is_file() or not current_path.is_file():
        raise ScanError("release directory requires bundle-manifest.json and current.json")
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        current = json.loads(current_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ScanError("release manifest or current descriptor is unreadable") from exc
    if not isinstance(manifest, dict) or not isinstance(current, dict):
        raise ScanError("release manifest and current descriptor must be JSON objects")
    if manifest.get("format_version") != 1 or current.get("format_version") != 1:
        raise ScanError("unsupported release manifest format")
    if manifest.get("current") != current:
        raise ScanError("current.json differs from the descriptor frozen in the bundle manifest")
    expected: dict[str, tuple[int, str]] = {}
    folded: set[str] = set()
    file_entries = manifest.get("files")
    if not isinstance(file_entries, list):
        raise ScanError("manifest files must be an array")
    for entry in file_entries:
        if not isinstance(entry, dict):
            raise ScanError("manifest file entry is malformed")
        relative = _safe_path(entry.get("path"))
        key = relative.casefold()
        if key in folded:
            raise ScanError("manifest contains a case-insensitive path collision")
        folded.add(key)
        size, digest = entry.get("size"), entry.get("sha256")
        if not isinstance(size, int) or size < 0 or not isinstance(digest, str) or not _HASH.fullmatch(digest):
            raise ScanError("manifest file metadata is malformed")
        expected[relative] = (size, digest)
    for component in ("app", "runtime", "browser", "postgres", "launcher"):
        descriptor = current.get(component)
        if not isinstance(descriptor, dict):
            raise ScanError("current descriptor is missing a component")
        relative_root = descriptor.get("relative_dir")
        if relative_root == "." and component == "launcher":
            continue
        safe_root = _safe_path(relative_root)
        if not any(path == safe_root or path.startswith(safe_root + "/") for path in expected):
            raise ScanError("current descriptor points to an absent component")

    actual: set[str] = set()
    try:
        for path in root.rglob("*"):
            if _is_reparse(path):
                findings.append({"code": "reparse-point", "path": path.relative_to(root).as_posix()})
                continue
            if not path.is_file():
                parts = [part.casefold() for part in path.relative_to(root).parts]
                if parts and parts[0] in _DATA_NAMES:
                    findings.append({"code": "user-data-path", "path": path.relative_to(root).as_posix()})
                continue
            relative = path.relative_to(root).as_posix()
            if relative in {"bundle-manifest.json", "current.json"}:
                continue
            try:
                relative = _safe_path(relative)
            except ScanError:
                findings.append({"code": "unsafe-path", "path": relative})
                continue
            actual.add(relative)
            name = path.name.casefold()
            if name in _SENSITIVE_NAMES or name.startswith(".env."):
                findings.append({"code": "sensitive-file", "path": relative})
            try:
                with path.open("rb") as stream:
                    prefix = stream.read(4)
            except OSError as exc:
                raise ScanError("release file could not be read") from exc
            if prefix.startswith((b"\xef\xbb\xbf", b"\xff\xfe", b"\xfe\xff", b"\x00\x00\xfe\xff")):
                findings.append({"code": "bom", "path": relative})
            pinned_legacy_notice = _PINNED_LEGACY_NOTICE_HASHES.get(relative)
            if (path.suffix.casefold() in _TEXT_SUFFIXES or name in {"license", "about"}) and pinned_legacy_notice is None:
                try:
                    path.read_bytes().decode("utf-8", errors="strict")
                except (OSError, UnicodeError):
                    findings.append({"code": "invalid-utf8", "path": relative})
            parts = [part.casefold() for part in PurePosixPath(relative).parts]
            if parts and parts[0] in _DATA_NAMES:
                findings.append({"code": "user-data-path", "path": relative})
            elif len(parts) >= 2 and parts[0] == "app" and parts[1] in _DATA_NAMES:
                findings.append({"code": "user-data-path", "path": relative})
            recorded = expected.get(relative)
            if recorded is None:
                findings.append({"code": "unknown-file", "path": relative})
                continue
            size, digest = recorded
            actual_digest = _sha256(path)
            if pinned_legacy_notice is not None and actual_digest != pinned_legacy_notice:
                findings.append({"code": "third-party-encoding-exception-hash-mismatch", "path": relative})
            if path.stat().st_size != size or actual_digest != digest:
                findings.append({"code": "manifest-mismatch", "path": relative})
    except OSError as exc:
        raise ScanError("release tree enumeration failed") from exc

    for relative in sorted(expected.keys() - actual):
        findings.append({"code": "missing-file", "path": relative})

    inventory_path = root / "third-party-notices" / "inventory.json"
    try:
        inventory = json.loads(inventory_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError):
        findings.append({"code": "license-inventory-missing", "path": "third-party-notices/inventory.json"})
    else:
        if not isinstance(inventory, dict) or not isinstance(inventory.get("components"), list):
            findings.append({"code": "license-inventory-malformed", "path": "third-party-notices/inventory.json"})
        else:
            gaps = [item.get("component", "unknown") for item in inventory["components"]
                    if isinstance(item, dict) and item.get("is_gap")]
            if inventory.get("notice_complete") is not True or gaps:
                findings.append({"code": "license-gap", "path": ",".join(gaps) or "notice_complete=false"})
            packaged_notices = {
                path for path in actual
                if path.startswith("third-party-notices/python-wheel-")
                and (root / path).is_file()
            }
            packaged_hashes = {
                path: digest for path, (_, digest) in expected.items()
            }
            try:
                validate_notice_inventory(
                    inventory,
                    _PYTHON_LOCK_PATH,
                    packaged_notice_paths=packaged_notices,
                    packaged_notice_hashes=packaged_hashes,
                )
            except ValueError as exc:
                findings.append({"code": "license-inventory-invalid", "path": str(exc)})
    if (root / "THIRD_PARTY_LICENSE_GAPS.md").exists():
        findings.append({"code": "license-gap-marker", "path": "THIRD_PARTY_LICENSE_GAPS.md"})

    payload_bytes = sum((root / relative).stat().st_size for relative in actual if (root / relative).is_file())
    metadata_bytes = manifest_path.stat().st_size + current_path.stat().st_size
    return {"status": "PASS" if not findings else "BLOCKED", "root": str(root),
            "file_count": len(actual) + 2, "payload_file_count": len(actual),
            "bytes": payload_bytes + metadata_bytes, "manifest_entries": len(expected), "findings": findings}


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Fail-closed portable release preflight")
    parser.add_argument("release_root", type=Path)
    arguments = parser.parse_args(argv)
    try:
        result = scan(arguments.release_root)
    except (ScanError, OSError) as exc:
        print(json.dumps({"status": "BLOCKED", "error": str(exc)}, ensure_ascii=False))
        return 2
    print(json.dumps(result, ensure_ascii=False, sort_keys=True))
    return 0 if result["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
