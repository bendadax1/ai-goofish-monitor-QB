"""Record and verify the exact file set emitted by a fresh Launcher publish."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import sys
from typing import Any


_ACCEPTANCE_ID = re.compile(r"^\d{8}-\d{6}-[0-9a-f]{12}$")
_HASH = re.compile(r"^[0-9a-f]{64}$")
_FORMAT_VERSION = 1


class InventoryError(RuntimeError):
    """A Launcher publish receipt is absent, stale, or inconsistent."""


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
    except OSError as exc:
        raise InventoryError("Launcher publish file could not be read") from exc
    return digest.hexdigest()


def _is_reparse(path: Path) -> bool:
    try:
        return path.is_symlink() or (hasattr(os.path, "isjunction") and os.path.isjunction(path))
    except OSError as exc:
        raise InventoryError("Launcher publish tree could not be inspected safely") from exc


def _assert_no_reparse(path: Path) -> None:
    cursor = Path(os.path.abspath(path))
    for candidate in (cursor, *cursor.parents):
        if _is_reparse(candidate):
            raise InventoryError("Launcher publish path contains a reparse point")


def _canonical_absolute(path: Path) -> str:
    try:
        return os.path.normcase(str(path.resolve(strict=True)))
    except OSError as exc:
        raise InventoryError("Launcher publish path does not exist") from exc


def _inventory_root(root: Path) -> Path:
    _assert_no_reparse(root)
    if not root.is_dir():
        raise InventoryError("Launcher publish root must be a directory")
    try:
        resolved = root.resolve(strict=True)
        entries = sorted(resolved.rglob("*"), key=lambda item: item.relative_to(resolved).as_posix().casefold())
    except OSError as exc:
        raise InventoryError("Launcher publish tree could not be enumerated") from exc
    for entry in entries:
        if _is_reparse(entry):
            raise InventoryError("Launcher publish tree contains a reparse point")
        if not entry.is_file() and not entry.is_dir():
            raise InventoryError("Launcher publish tree contains an unsupported filesystem entry")
    return resolved


def _expected_receipt_path(repository_root: Path, acceptance_id: str) -> Path:
    if not _ACCEPTANCE_ID.fullmatch(acceptance_id):
        raise InventoryError("acceptance id is malformed")
    return repository_root / ".tmp" / "build" / "portable-acceptance" / acceptance_id / "launcher-publish-inventory.json"


def _enumerate_files(root: Path) -> list[dict[str, Any]]:
    files = []
    seen: set[str] = set()
    try:
        candidates = sorted(root.rglob("*"), key=lambda item: item.relative_to(root).as_posix().casefold())
        for path in candidates:
            if not path.is_file():
                continue
            relative = path.relative_to(root).as_posix()
            pure = PurePosixPath(relative)
            if pure.is_absolute() or "\\" in relative or any(part in {"", ".", ".."} for part in relative.split("/")):
                raise InventoryError("Launcher publish tree contains an unsafe relative path")
            folded = relative.casefold()
            if folded in seen:
                raise InventoryError("Launcher publish tree has a case-insensitive path collision")
            seen.add(folded)
            files.append({"path": relative, "size": path.stat().st_size, "sha256": _sha256(path)})
    except OSError as exc:
        raise InventoryError("Launcher publish tree could not be enumerated") from exc
    if not files:
        raise InventoryError("Launcher publish tree is empty")
    return files


def record_inventory(repository_root: Path, launcher_root: Path, acceptance_id: str) -> Path:
    """Write a UTF-8 no-BOM receipt outside the Launcher publish tree."""
    expected_receipt = _expected_receipt_path(repository_root, acceptance_id)
    root = _inventory_root(launcher_root)
    dist_root = (repository_root / "launcher" / "dist").resolve(strict=True)
    try:
        root.relative_to(dist_root)
    except ValueError as exc:
        raise InventoryError("Launcher publish root must be below launcher/dist") from exc
    if root.parent != dist_root or root.name != f"launcher-p1-acceptance-{acceptance_id}":
        raise InventoryError("Launcher publish root does not match this fresh acceptance run")
    receipt_path = expected_receipt
    if receipt_path.exists() or _is_reparse(receipt_path):
        raise InventoryError("Launcher publish receipt already exists; refusing to replace it")
    try:
        receipt_path.parent.mkdir(parents=True, exist_ok=True)
        receipt_path.resolve(strict=False).relative_to((repository_root / ".tmp" / "build" / "portable-acceptance").resolve(strict=True))
        _assert_no_reparse(receipt_path)
        payload = {
            "format_version": _FORMAT_VERSION,
            "acceptance_id": acceptance_id,
            "launcher_root": _canonical_absolute(root),
            "files": _enumerate_files(root),
        }
        serialized = (json.dumps(payload, ensure_ascii=False, sort_keys=True, indent=2) + "\n").encode("utf-8")
        with receipt_path.open("xb") as stream:
            stream.write(serialized)
    except (OSError, ValueError) as exc:
        raise InventoryError("Launcher publish receipt could not be recorded safely") from exc
    return receipt_path


def verify_inventory(repository_root: Path, launcher_root: Path, receipt_path: Path) -> list[dict[str, Any]]:
    """Require a fresh-run receipt and exact path, size, and hash agreement."""
    root = _inventory_root(launcher_root)
    dist_root = (repository_root / "launcher" / "dist").resolve(strict=True)
    try:
        root.relative_to(dist_root)
    except ValueError as exc:
        raise InventoryError("Launcher publish root must be below launcher/dist") from exc
    match = re.fullmatch(r"launcher-p1-acceptance-(\d{8}-\d{6}-[0-9a-f]{12})", root.name)
    if root.parent != dist_root or match is None:
        raise InventoryError("Launcher publish root is not a fresh P1 acceptance output")
    acceptance_id = match.group(1)
    expected_receipt = _expected_receipt_path(repository_root, acceptance_id)
    _assert_no_reparse(receipt_path)
    if _canonical_absolute(receipt_path) != _canonical_absolute(expected_receipt):
        raise InventoryError("Launcher publish receipt is not stored in its acceptance task directory")
    try:
        payload = json.loads(receipt_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise InventoryError("Launcher publish receipt is missing or malformed") from exc
    if not isinstance(payload, dict) or payload.get("format_version") != _FORMAT_VERSION:
        raise InventoryError("Launcher publish receipt format is unsupported")
    if payload.get("acceptance_id") != acceptance_id:
        raise InventoryError("Launcher publish receipt belongs to a different acceptance run")
    if payload.get("launcher_root") != _canonical_absolute(root):
        raise InventoryError("Launcher publish receipt points to a different absolute root")
    recorded = payload.get("files")
    if not isinstance(recorded, list):
        raise InventoryError("Launcher publish receipt has no file inventory")
    seen: set[str] = set()
    for entry in recorded:
        if not isinstance(entry, dict):
            raise InventoryError("Launcher publish receipt entry is malformed")
        relative = entry.get("path")
        if not isinstance(relative, str):
            raise InventoryError("Launcher publish receipt path is malformed")
        pure = PurePosixPath(relative)
        if pure.is_absolute() or "\\" in relative or any(part in {"", ".", ".."} for part in relative.split("/")):
            raise InventoryError("Launcher publish receipt contains an unsafe path")
        folded = relative.casefold()
        if folded in seen:
            raise InventoryError("Launcher publish receipt has duplicate or case-colliding paths")
        seen.add(folded)
        if not isinstance(entry.get("size"), int) or entry["size"] < 0 or not isinstance(entry.get("sha256"), str) or not _HASH.fullmatch(entry["sha256"]):
            raise InventoryError("Launcher publish receipt metadata is malformed")
    actual = _enumerate_files(root)
    actual_by_folded = {entry["path"].casefold(): entry for entry in actual}
    recorded_by_folded = {entry["path"].casefold(): entry for entry in recorded}
    if actual_by_folded.keys() != recorded_by_folded.keys():
        raise InventoryError("Launcher publish files differ from the independently recorded inventory")
    for folded, actual_entry in actual_by_folded.items():
        if actual_entry != recorded_by_folded[folded]:
            raise InventoryError("Launcher publish file bytes differ from the independently recorded inventory")
    return [{"path": entry["path"], "size": entry["size"], "sha256": entry["sha256"]} for entry in actual]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Record an exact Launcher publish inventory outside its output directory")
    parser.add_argument("--repository-root", type=Path, required=True)
    parser.add_argument("--launcher-root", type=Path, required=True)
    parser.add_argument("--acceptance-id", required=True)
    args = parser.parse_args(argv)
    try:
        path = record_inventory(args.repository_root, args.launcher_root, args.acceptance_id)
    except (InventoryError, OSError) as exc:
        print(f"Launcher publish inventory failed: {exc}", file=sys.stderr)
        return 1
    print(f"LAUNCHER_INVENTORY={path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
