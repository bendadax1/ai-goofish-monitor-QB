from __future__ import annotations

import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from zipfile import ZipFile


_ROOT = Path(__file__).resolve().parents[1]
_VERIFIER_PATH = _ROOT / "scripts" / "portable" / "verify-browser-runtime.py"
_SPEC = importlib.util.spec_from_file_location("portable_browser_runtime_verifier", _VERIFIER_PATH)
assert _SPEC is not None and _SPEC.loader is not None
_VERIFIER = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(_VERIFIER)
_TEST_PARENT = _ROOT / ".tmp" / "tests" / "portable-browser-runtime"


class PortableBrowserRuntimeTests(unittest.TestCase):
    _created_parents: list[Path] = []

    @classmethod
    def setUpClass(cls):
        for directory in (_ROOT / ".tmp", _ROOT / ".tmp" / "tests", _TEST_PARENT):
            if not directory.exists():
                directory.mkdir()
                cls._created_parents.append(directory)

    @classmethod
    def tearDownClass(cls):
        for directory in reversed(cls._created_parents):
            directory.rmdir()

    def _fixture(self, root: Path) -> tuple[Path, Path, Path]:
        archive = root / "chromium.zip"
        entries = {"chrome-win64/chrome.exe": b"browser", "chrome-win64/icudtl.dat": b"icu"}
        with ZipFile(archive, "w") as stream:
            for name, content in entries.items():
                stream.writestr(name, content)
        lock = root / "browser.lock.json"
        lock.write_text(json.dumps({
            "schema_version": 1, "component": "browser-runtime", "platform": "win64",
            "verification": "playwright_cdn_https_local_sha256", "playwright_version": "1.57.0",
            "chromium_revision": "1200", "browser_version": "143.0.7499.4",
            "archive_bytes": archive.stat().st_size, "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
            "archive_root": "chrome-win64", "executable_relative_path": "chrome-win64/chrome.exe",
        }), encoding="utf-8")
        runtime = root / "runtime"
        for name, content in entries.items():
            target = runtime / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
        return runtime, archive, lock

    def test_manifest_verifies_unchanged_runtime(self):
        with tempfile.TemporaryDirectory(dir=_TEST_PARENT) as temporary:
            runtime, archive, lock = self._fixture(Path(temporary))
            _VERIFIER.write_manifest(runtime, archive, lock)
            _VERIFIER.verify_manifest(runtime, archive, lock)

    def test_runtime_without_marker_is_not_reusable(self):
        with tempfile.TemporaryDirectory(dir=_TEST_PARENT) as temporary:
            runtime, archive, lock = self._fixture(Path(temporary))
            with self.assertRaisesRegex(_VERIFIER.BrowserRuntimeVerificationError, "marker is missing"):
                _VERIFIER.verify_manifest(runtime, archive, lock)

    def test_damaged_native_file_is_rejected(self):
        with tempfile.TemporaryDirectory(dir=_TEST_PARENT) as temporary:
            runtime, archive, lock = self._fixture(Path(temporary))
            _VERIFIER.write_manifest(runtime, archive, lock)
            (runtime / "chrome-win64" / "chrome.exe").write_bytes(b"tampered")
            with self.assertRaisesRegex(_VERIFIER.BrowserRuntimeVerificationError, "differs from the verified ZIP"):
                _VERIFIER.verify_manifest(runtime, archive, lock)

    def test_extra_file_is_rejected(self):
        with tempfile.TemporaryDirectory(dir=_TEST_PARENT) as temporary:
            runtime, archive, lock = self._fixture(Path(temporary))
            _VERIFIER.write_manifest(runtime, archive, lock)
            (runtime / "chrome-win64" / "extra.dll").write_bytes(b"unexpected")
            with self.assertRaisesRegex(_VERIFIER.BrowserRuntimeVerificationError, "file set differs"):
                _VERIFIER.verify_manifest(runtime, archive, lock)
