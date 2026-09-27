"""Verify the identity and installed-wheel integrity of a portable Python runtime.

The build script writes the marker only in a newly populated staging runtime.  A
runtime without that marker is deliberately not eligible for reuse: its original
archive and lock identity cannot be reconstructed safely after the fact.
"""

from __future__ import annotations

import argparse
import base64
import csv
import hashlib
import importlib.metadata
import json
import os
import re
import stat
import zipfile
from pathlib import Path, PureWindowsPath
from typing import Any, Iterable


_MANIFEST_NAME = "python-runtime-manifest.json"
_SCHEMA_VERSION = 1


class RuntimeVerificationError(RuntimeError):
    """A portable runtime is stale, incomplete, or lacks build identity."""


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
    except OSError as exc:
        raise RuntimeVerificationError(f"could not read {path.name}") from exc
    return digest.hexdigest()


def _normalise_name(value: str) -> str:
    return re.sub(r"[-_.]+", "-", value).lower()


def _load_json(path: Path, *, label: str) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RuntimeVerificationError(f"{label} could not be read") from exc
    if not isinstance(value, dict):
        raise RuntimeVerificationError(f"{label} is invalid")
    return value


def _expected_distributions(requirements_lock: Path) -> dict[str, str]:
    try:
        lines = requirements_lock.read_text(encoding="utf-8").splitlines()
    except (OSError, UnicodeError) as exc:
        raise RuntimeVerificationError("requirements lock could not be read") from exc
    packages: dict[str, str] = {}
    for line in lines:
        match = re.match(r"^([A-Za-z0-9_.-]+)==([^\\\s]+)", line)
        if match is None:
            continue
        name = _normalise_name(match.group(1))
        version = match.group(2)
        if name in packages and packages[name] != version:
            raise RuntimeVerificationError("requirements lock contains conflicting package versions")
        packages[name] = version
    if not packages:
        raise RuntimeVerificationError("requirements lock has no pinned distributions")
    return dict(sorted(packages.items()))


def _installed_distributions(site_packages: Path) -> dict[str, str]:
    if not site_packages.is_dir():
        raise RuntimeVerificationError("runtime site-packages is missing")
    installed: dict[str, str] = {}
    try:
        distributions = importlib.metadata.distributions(path=[str(site_packages)])
        for distribution in distributions:
            name = distribution.metadata.get("Name")
            if not name:
                raise RuntimeVerificationError("installed wheel METADATA is missing or invalid")
            normalised = _normalise_name(name)
            if normalised in installed:
                raise RuntimeVerificationError("runtime has duplicate installed distributions")
            installed[normalised] = distribution.version
    except RuntimeVerificationError:
        raise
    except Exception as exc:
        raise RuntimeVerificationError("installed distribution metadata could not be read") from exc
    return dict(sorted(installed.items()))


def _record_entries(distribution: importlib.metadata.Distribution) -> Iterable[tuple[Path, str, int]]:
    record = distribution.read_text("RECORD")
    if record is None:
        raise RuntimeVerificationError("installed wheel RECORD is missing")
    for row in csv.reader(record.splitlines()):
        if len(row) != 3:
            raise RuntimeVerificationError("installed wheel RECORD is malformed")
        relative, digest, size = row
        if not relative:
            raise RuntimeVerificationError("installed wheel RECORD contains an empty path")
        try:
            parsed_size = int(size) if size else -1
        except ValueError as exc:
            raise RuntimeVerificationError("installed wheel RECORD size is invalid") from exc
        if parsed_size < -1:
            raise RuntimeVerificationError("installed wheel RECORD size is invalid")
        yield Path(relative), digest, parsed_size


def _verify_wheel_records(site_packages: Path) -> None:
    try:
        distributions = list(importlib.metadata.distributions(path=[str(site_packages)]))
    except Exception as exc:
        raise RuntimeVerificationError("installed wheel RECORD metadata could not be read") from exc
    expected_paths: set[str] = set()
    for distribution in distributions:
        own_record = {str(f).replace("\\", "/") for f in (distribution.files or ())
                      if str(f).replace("\\", "/").endswith(".dist-info/RECORD")}
        for relative, expected_digest, expected_size in _record_entries(distribution):
            if relative.is_absolute() or PureWindowsPath(str(relative)).anchor or ".." in relative.parts or ":" in str(relative):
                raise RuntimeVerificationError("installed wheel RECORD escapes site-packages")
            key = relative.as_posix().casefold()
            if key in expected_paths:
                raise RuntimeVerificationError("installed wheel RECORD has duplicate file ownership")
            expected_paths.add(key)
            candidate = site_packages / relative
            try:
                actual_size = candidate.stat().st_size
            except OSError as exc:
                raise RuntimeVerificationError("installed wheel RECORD file is missing") from exc
            if expected_size >= 0 and actual_size != expected_size:
                raise RuntimeVerificationError("installed wheel RECORD size mismatch")
            if not expected_digest:
                if relative.as_posix() not in own_record or expected_size != -1:
                    raise RuntimeVerificationError("installed wheel file has no integrity digest")
                continue
            try:
                algorithm, encoded = expected_digest.split("=", 1)
            except ValueError as exc:
                raise RuntimeVerificationError("installed wheel RECORD digest is malformed") from exc
            if algorithm != "sha256":
                raise RuntimeVerificationError("installed wheel RECORD uses an unsupported digest")
            padding = "=" * (-len(encoded) % 4)
            try:
                expected_bytes = base64.b64decode(encoded + padding, altchars=b"-_", validate=True)
                if len(expected_bytes) != 32:
                    raise ValueError("wrong SHA-256 length")
                expected_hash = expected_bytes.hex()
            except (ValueError, UnicodeError) as exc:
                raise RuntimeVerificationError("installed wheel RECORD digest is invalid") from exc
            if _sha256(candidate) != expected_hash:
                raise RuntimeVerificationError("installed wheel RECORD digest mismatch")
    try:
        actual_paths = {p.relative_to(site_packages).as_posix().casefold()
                        for p in site_packages.rglob("*") if p.is_file()}
        # uv leaves an empty installer lock; it is not an importable asset.
        installer_lock = site_packages / ".lock"
        if installer_lock.is_file() and installer_lock.stat().st_size == 0:
            actual_paths.discard(".lock")
        if actual_paths != expected_paths:
            raise RuntimeVerificationError("runtime contains files not covered by wheel RECORDs")
    except OSError as exc:
        raise RuntimeVerificationError("runtime file inventory could not be read") from exc


def _reject_reparse_tree(root: Path) -> None:
    try:
        for candidate in (root, *root.parents, *root.rglob("*")):
            info = candidate.lstat()
            if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
                raise RuntimeVerificationError("runtime reparse points are not permitted")
    except OSError as exc:
        raise RuntimeVerificationError("runtime path attributes could not be inspected") from exc


def _expected_pth(lock: dict[str, Any]) -> str:
    dll_name = lock.get("python_dll")
    if not isinstance(dll_name, str) or not dll_name.lower().endswith(".dll"):
        raise RuntimeVerificationError("runtime lock has no Python DLL name")
    return f"{dll_name[:-4]}.zip\n.\nsite-packages\nimport site\n"


def _safe_archive_relative_path(name: str) -> Path:
    candidate = Path(name)
    if not name or candidate.is_absolute() or ".." in candidate.parts or ":" in name:
        raise RuntimeVerificationError("runtime archive contains an unsafe path")
    return candidate


def _verify_archive_native_files(runtime_root: Path, archive: Path, pth_name: str) -> dict[str, str]:
    """Verify every original archive file except the intentionally rewritten ._pth."""
    native_files: dict[str, str] = {}
    try:
        with zipfile.ZipFile(archive) as source:
            for entry in source.infolist():
                if entry.is_dir():
                    continue
                relative = _safe_archive_relative_path(entry.filename)
                if relative.as_posix().casefold() == pth_name.casefold():
                    continue
                destination = runtime_root / relative
                if not destination.is_file():
                    raise RuntimeVerificationError("runtime original archive file is missing")
                digest = hashlib.sha256()
                with source.open(entry, "r") as archived, destination.open("rb") as installed:
                    while True:
                        archived_block = archived.read(1024 * 1024)
                        installed_block = installed.read(1024 * 1024)
                        if archived_block != installed_block:
                            raise RuntimeVerificationError("runtime original archive file differs from the verified ZIP")
                        if not archived_block:
                            break
                        digest.update(archived_block)
                native_files[relative.as_posix()] = digest.hexdigest()
    except RuntimeVerificationError:
        raise
    except (OSError, zipfile.BadZipFile) as exc:
        raise RuntimeVerificationError("runtime archive native files could not be verified") from exc
    if not native_files:
        raise RuntimeVerificationError("runtime archive contains no native files")
    return dict(sorted(native_files.items()))


def _build_identity(runtime_root: Path, archive: Path, lock_path: Path, requirements_lock: Path) -> dict[str, Any]:
    _reject_reparse_tree(runtime_root)
    lock = _load_json(lock_path, label="runtime lock")
    if lock.get("schema_version") != 1 or lock.get("component") != "python-runtime":
        raise RuntimeVerificationError("runtime lock is unsupported")
    expected_archive_hash = lock.get("sha256")
    if not isinstance(expected_archive_hash, str) or _sha256(archive) != expected_archive_hash:
        raise RuntimeVerificationError("runtime archive SHA-256 does not match the lock")
    pth_name = lock.get("pth_file")
    if not isinstance(pth_name, str) or not pth_name:
        raise RuntimeVerificationError("runtime lock has no ._pth name")
    pth_path = runtime_root / pth_name
    try:
        actual_pth = pth_path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as exc:
        raise RuntimeVerificationError("runtime ._pth could not be read") from exc
    if actual_pth != _expected_pth(lock):
        raise RuntimeVerificationError("runtime ._pth does not match the isolation contract")
    if not (runtime_root / "python.exe").is_file() or not (runtime_root / str(lock.get("python_dll", ""))).is_file():
        raise RuntimeVerificationError("runtime interpreter or Python DLL is missing")
    native_files = _verify_archive_native_files(runtime_root, archive, pth_name)
    expected_root_files = set(native_files) | {pth_name, _MANIFEST_NAME}
    for candidate in runtime_root.rglob("*"):
        relative = candidate.relative_to(runtime_root).as_posix()
        if candidate.is_file() and not relative.startswith("site-packages/") and relative not in expected_root_files:
            raise RuntimeVerificationError("runtime contains an unrecognized file outside site-packages")
    site_packages = runtime_root / "site-packages"
    expected_packages = _expected_distributions(requirements_lock)
    installed_packages = _installed_distributions(site_packages)
    if installed_packages != expected_packages:
        raise RuntimeVerificationError("runtime installed distributions do not exactly match the requirements lock")
    _verify_wheel_records(site_packages)
    return {
        "schema_version": _SCHEMA_VERSION,
        "python_version": lock.get("version"),
        "archive_sha256": expected_archive_hash,
        "requirements_lock_sha256": _sha256(requirements_lock),
        "requirements_lock_name": requirements_lock.name,
        "pth_sha256": _sha256(pth_path),
        "native_archive_files": native_files,
        "expected_distributions": expected_packages,
        "installed_distributions": installed_packages,
    }


def write_manifest(runtime_root: Path, archive: Path, lock_path: Path, requirements_lock: Path) -> Path:
    identity = _build_identity(runtime_root, archive, lock_path, requirements_lock)
    manifest_path = runtime_root / _MANIFEST_NAME
    try:
        manifest_path.write_text(
            json.dumps(identity, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
    except OSError as exc:
        raise RuntimeVerificationError("runtime identity marker could not be written") from exc
    return manifest_path


def verify_manifest(runtime_root: Path, archive: Path, lock_path: Path, requirements_lock: Path) -> None:
    manifest_path = runtime_root / _MANIFEST_NAME
    if not manifest_path.is_file():
        raise RuntimeVerificationError("runtime identity marker is missing; refusing unverifiable reuse")
    manifest = _load_json(manifest_path, label="runtime identity marker")
    if manifest.get("schema_version") != _SCHEMA_VERSION:
        raise RuntimeVerificationError("runtime identity marker schema is unsupported")
    current = _build_identity(runtime_root, archive, lock_path, requirements_lock)
    for key in (
        "python_version",
        "archive_sha256",
        "requirements_lock_sha256",
        "requirements_lock_name",
        "pth_sha256",
        "native_archive_files",
        "expected_distributions",
        "installed_distributions",
    ):
        if manifest.get(key) != current[key]:
            raise RuntimeVerificationError(f"runtime identity marker differs for {key}; refusing stale reuse")


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Verify an isolated portable Python runtime")
    parser.add_argument("--runtime-root", type=Path, required=True)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--lock", dest="lock_path", type=Path, required=True)
    parser.add_argument("--requirements-lock", type=Path, required=True)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--write-manifest", action="store_true")
    mode.add_argument("--verify-manifest", action="store_true")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    try:
        if args.write_manifest:
            manifest_path = write_manifest(args.runtime_root, args.archive, args.lock_path, args.requirements_lock)
            print(f"PYTHON_RUNTIME_MANIFEST={manifest_path}")
        else:
            verify_manifest(args.runtime_root, args.archive, args.lock_path, args.requirements_lock)
            print("PYTHON_RUNTIME_MANIFEST=VERIFIED")
    except RuntimeVerificationError as exc:
        print(f"Portable Python runtime verification failed: {exc}", file=__import__("sys").stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
