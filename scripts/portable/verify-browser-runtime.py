"""Verify a version-locked portable Chromium runtime and run a local-only smoke test."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import zipfile
from typing import Any


_MANIFEST_NAME = "browser-runtime-manifest.json"
_SCHEMA_VERSION = 1


class BrowserRuntimeVerificationError(RuntimeError):
    """The browser archive, extraction, or identity marker is not trustworthy."""


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
    except OSError as exc:
        raise BrowserRuntimeVerificationError(f"could not read {path.name}") from exc
    return digest.hexdigest()


def _load_lock(path: Path) -> dict[str, Any]:
    try:
        lock = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise BrowserRuntimeVerificationError("browser runtime lock could not be read") from exc
    required = (
        "playwright_version",
        "chromium_revision",
        "browser_version",
        "archive_bytes",
        "sha256",
        "archive_root",
        "executable_relative_path",
    )
    if (
        lock.get("schema_version") != 1
        or lock.get("component") != "browser-runtime"
        or lock.get("platform") != "win64"
        or lock.get("verification") != "playwright_cdn_https_local_sha256"
        or any(not lock.get(key) for key in required)
    ):
        raise BrowserRuntimeVerificationError("browser runtime lock is unsupported")
    return lock


def _safe_relative_path(name: str) -> Path:
    candidate = Path(name)
    if not name or candidate.is_absolute() or ".." in candidate.parts or ":" in name:
        raise BrowserRuntimeVerificationError("browser archive contains an unsafe path")
    return candidate


def _archive_files(runtime_root: Path, archive: Path, lock: dict[str, Any]) -> dict[str, str]:
    if archive.stat().st_size != int(lock["archive_bytes"]) or _sha256(archive) != lock["sha256"]:
        raise BrowserRuntimeVerificationError("browser archive does not match the lock")
    files: dict[str, str] = {}
    try:
        with zipfile.ZipFile(archive) as source:
            for entry in source.infolist():
                if entry.is_dir():
                    continue
                relative = _safe_relative_path(entry.filename)
                if relative.parts[0].casefold() != str(lock["archive_root"]).casefold():
                    raise BrowserRuntimeVerificationError("browser archive has an unexpected root")
                destination = runtime_root / relative
                if not destination.is_file():
                    raise BrowserRuntimeVerificationError("browser runtime archive file is missing")
                digest = hashlib.sha256()
                with source.open(entry) as archived, destination.open("rb") as installed:
                    while True:
                        archive_block = archived.read(1024 * 1024)
                        installed_block = installed.read(1024 * 1024)
                        if archive_block != installed_block:
                            raise BrowserRuntimeVerificationError("browser runtime archive file differs from the verified ZIP")
                        if not archive_block:
                            break
                        digest.update(archive_block)
                files[relative.as_posix()] = digest.hexdigest()
    except BrowserRuntimeVerificationError:
        raise
    except (OSError, zipfile.BadZipFile) as exc:
        raise BrowserRuntimeVerificationError("browser archive file verification failed") from exc
    if not files:
        raise BrowserRuntimeVerificationError("browser archive has no files")
    return dict(sorted(files.items()))


def _runtime_files(runtime_root: Path) -> set[str]:
    try:
        return {
            candidate.relative_to(runtime_root).as_posix()
            for candidate in runtime_root.rglob("*")
            if candidate.is_file()
        }
    except OSError as exc:
        raise BrowserRuntimeVerificationError("browser runtime files could not be enumerated") from exc


def _identity(runtime_root: Path, archive: Path, lock: dict[str, Any]) -> dict[str, Any]:
    archive_files = _archive_files(runtime_root, archive, lock)
    executable = runtime_root / str(lock["executable_relative_path"])
    if not executable.is_file():
        raise BrowserRuntimeVerificationError("browser executable is missing")
    actual_files = _runtime_files(runtime_root)
    expected_files = set(archive_files) | {_MANIFEST_NAME}
    unexpected = actual_files - expected_files
    missing = expected_files - actual_files
    if unexpected or missing - {_MANIFEST_NAME}:
        raise BrowserRuntimeVerificationError("browser runtime file set differs from the archive manifest")
    return {
        "schema_version": _SCHEMA_VERSION,
        "playwright_version": lock["playwright_version"],
        "chromium_revision": lock["chromium_revision"],
        "browser_version": lock["browser_version"],
        "archive_sha256": lock["sha256"],
        "archive_bytes": lock["archive_bytes"],
        "executable_relative_path": lock["executable_relative_path"],
        "archive_files": archive_files,
    }


def write_manifest(runtime_root: Path, archive: Path, lock_path: Path) -> Path:
    lock = _load_lock(lock_path)
    identity = _identity(runtime_root, archive, lock)
    marker = runtime_root / _MANIFEST_NAME
    try:
        marker.write_text(json.dumps(identity, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    except OSError as exc:
        raise BrowserRuntimeVerificationError("browser runtime marker could not be written") from exc
    return marker


def verify_manifest(runtime_root: Path, archive: Path, lock_path: Path) -> None:
    marker = runtime_root / _MANIFEST_NAME
    if not marker.is_file():
        raise BrowserRuntimeVerificationError("browser runtime marker is missing")
    try:
        manifest = json.loads(marker.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise BrowserRuntimeVerificationError("browser runtime marker could not be read") from exc
    lock = _load_lock(lock_path)
    current = _identity(runtime_root, archive, lock)
    if manifest != current:
        raise BrowserRuntimeVerificationError("browser runtime marker differs from verified runtime identity")


def smoke(runtime_root: Path, lock_path: Path, *, headed: bool) -> None:
    lock = _load_lock(lock_path)
    try:
        from playwright._repo_version import version as installed_playwright_version
        from playwright.sync_api import sync_playwright
    except Exception as exc:
        raise BrowserRuntimeVerificationError("locked Playwright Python package could not be imported") from exc
    if installed_playwright_version != lock["playwright_version"]:
        raise BrowserRuntimeVerificationError("installed Playwright version differs from browser lock")
    executable = runtime_root / str(lock["executable_relative_path"])
    arguments = [
        "--disable-background-networking",
        "--disable-component-update",
        "--disable-domain-reliability",
        "--disable-sync",
        "--metrics-recording-only",
        "--no-default-browser-check",
        "--no-first-run",
    ]
    try:
        with sync_playwright() as api:
            browser = api.chromium.launch(executable_path=str(executable), headless=not headed, args=arguments)
            try:
                page = browser.new_page()
                page.goto("data:text/html,<title>Portable Chromium</title><main id=probe>local-only</main>")
                if page.title() != "Portable Chromium" or page.locator("#probe").inner_text() != "local-only":
                    raise BrowserRuntimeVerificationError("browser local data-page smoke assertion failed")
            finally:
                browser.close()
    except BrowserRuntimeVerificationError:
        raise
    except Exception as exc:
        raise BrowserRuntimeVerificationError("browser local smoke launch failed") from exc


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Verify portable Chromium runtime")
    parser.add_argument("--runtime-root", type=Path, required=True)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--lock", dest="lock_path", type=Path, required=True)
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument("--write-manifest", action="store_true")
    action.add_argument("--verify-manifest", action="store_true")
    action.add_argument("--smoke", action="store_true")
    parser.add_argument("--headed", action="store_true", help="Run the local-only smoke in a visible browser window")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    try:
        if args.write_manifest:
            print(f"BROWSER_RUNTIME_MANIFEST={write_manifest(args.runtime_root, args.archive, args.lock_path)}")
        elif args.verify_manifest:
            verify_manifest(args.runtime_root, args.archive, args.lock_path)
            print("BROWSER_RUNTIME_MANIFEST=VERIFIED")
        else:
            smoke(args.runtime_root, args.lock_path, headed=args.headed)
            print("BROWSER_RUNTIME_SMOKE=PASS")
    except BrowserRuntimeVerificationError as exc:
        print(f"Portable browser runtime verification failed: {exc}", file=__import__("sys").stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
