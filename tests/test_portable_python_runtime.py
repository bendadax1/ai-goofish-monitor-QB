from __future__ import annotations

import base64
import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from zipfile import ZipFile


_REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
_VERIFIER_PATH = _REPOSITORY_ROOT / "scripts" / "portable" / "verify-python-runtime.py"
_TEST_PARENT = _REPOSITORY_ROOT / ".tmp" / "tests" / "portable-python-runtime"
_SPEC = importlib.util.spec_from_file_location("portable_python_runtime_verifier", _VERIFIER_PATH)
assert _SPEC is not None and _SPEC.loader is not None
_VERIFIER = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(_VERIFIER)


def _record_digest(content: bytes) -> str:
    return "sha256=" + base64.urlsafe_b64encode(hashlib.sha256(content).digest()).decode("ascii").rstrip("=")


class PortablePythonRuntimeTests(unittest.TestCase):
    _created_parents: list[Path] = []

    @classmethod
    def setUpClass(cls):
        for directory in (
            _REPOSITORY_ROOT / ".tmp",
            _REPOSITORY_ROOT / ".tmp" / "tests",
            _TEST_PARENT,
        ):
            if not directory.exists():
                directory.mkdir()
                cls._created_parents.append(directory)

    @classmethod
    def tearDownClass(cls):
        for directory in reversed(cls._created_parents):
            directory.rmdir()

    def _fixture(self, root: Path) -> tuple[Path, Path, Path, Path]:
        archive = root / "python.zip"
        archive_entries = {
            "python.exe": b"python",
            "python313.dll": b"dll",
            "python313.zip": b"stdlib",
            "python313._pth": b"# original pth\r\n",
            "DLLs/native.pyd": b"native module",
        }
        with ZipFile(archive, "w") as zip_file:
            for name, content in archive_entries.items():
                zip_file.writestr(name, content)
        lock = root / "python-runtime.lock.json"
        lock.write_text(
            json.dumps(
                {
                    "schema_version": 1,
                    "component": "python-runtime",
                    "version": "3.13.15",
                    "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
                    "python_dll": "python313.dll",
                    "pth_file": "python313._pth",
                }
            ),
            encoding="utf-8",
        )
        requirements = root / "requirements.lock.txt"
        requirements.write_text("demo==1.0 \\\n+    --hash=sha256:deadbeef\n", encoding="utf-8")
        runtime = root / "runtime"
        site_packages = runtime / "site-packages"
        dist_info = site_packages / "demo-1.0.dist-info"
        dist_info.mkdir(parents=True)
        (runtime / "python.exe").write_bytes(archive_entries["python.exe"])
        (runtime / "python313.dll").write_bytes(archive_entries["python313.dll"])
        (runtime / "python313.zip").write_bytes(archive_entries["python313.zip"])
        (runtime / "DLLs").mkdir()
        (runtime / "DLLs" / "native.pyd").write_bytes(archive_entries["DLLs/native.pyd"])
        (runtime / "python313._pth").write_text(
            "python313.zip\n.\nsite-packages\nimport site\n", encoding="utf-8"
        )
        metadata = b"Metadata-Version: 2.1\nName: demo\nVersion: 1.0\n"
        (dist_info / "METADATA").write_bytes(metadata)
        (dist_info / "RECORD").write_text(
            f"demo-1.0.dist-info/METADATA,{_record_digest(metadata)},{len(metadata)}\n"
            "demo-1.0.dist-info/RECORD,,\n",
            encoding="utf-8",
        )
        return runtime, archive, lock, requirements

    @staticmethod
    def _add_distribution(site_packages: Path, name: str, version: str) -> None:
        dist_info = site_packages / f"{name}-{version}.dist-info"
        dist_info.mkdir()
        metadata = f"Metadata-Version: 2.1\nName: {name}\nVersion: {version}\n".encode("utf-8")
        (dist_info / "METADATA").write_bytes(metadata)
        (dist_info / "RECORD").write_text(
            f"{name}-{version}.dist-info/METADATA,{_record_digest(metadata)},{len(metadata)}\n"
            f"{name}-{version}.dist-info/RECORD,,\n",
            encoding="utf-8",
        )

    def test_runtime_without_marker_is_not_reusable(self):
        with tempfile.TemporaryDirectory(dir=_TEST_PARENT) as temporary_directory:
            runtime, archive, lock, requirements = self._fixture(Path(temporary_directory))
            with self.assertRaisesRegex(_VERIFIER.RuntimeVerificationError, "marker is missing"):
                _VERIFIER.verify_manifest(runtime, archive, lock, requirements)

    def test_identity_marker_verifies_unchanged_runtime(self):
        with tempfile.TemporaryDirectory(dir=_TEST_PARENT) as temporary_directory:
            runtime, archive, lock, requirements = self._fixture(Path(temporary_directory))
            marker = _VERIFIER.write_manifest(runtime, archive, lock, requirements)
            self.assertTrue(marker.is_file())
            _VERIFIER.verify_manifest(runtime, archive, lock, requirements)

    def test_requirements_lock_change_rejects_stale_runtime(self):
        with tempfile.TemporaryDirectory(dir=_TEST_PARENT) as temporary_directory:
            runtime, archive, lock, requirements = self._fixture(Path(temporary_directory))
            _VERIFIER.write_manifest(runtime, archive, lock, requirements)
            requirements.write_text("demo==1.0\\n# lock content changed\\n", encoding="utf-8")
            with self.assertRaisesRegex(_VERIFIER.RuntimeVerificationError, "requirements_lock_sha256"):
                _VERIFIER.verify_manifest(runtime, archive, lock, requirements)

    def test_original_archive_file_damage_rejects_runtime(self):
        with tempfile.TemporaryDirectory(dir=_TEST_PARENT) as temporary_directory:
            runtime, archive, lock, requirements = self._fixture(Path(temporary_directory))
            _VERIFIER.write_manifest(runtime, archive, lock, requirements)
            (runtime / "python313.dll").write_bytes(b"modified dll")
            with self.assertRaisesRegex(_VERIFIER.RuntimeVerificationError, "differs from the verified ZIP"):
                _VERIFIER.verify_manifest(runtime, archive, lock, requirements)

    def test_extra_unknown_package_rejects_runtime(self):
        with tempfile.TemporaryDirectory(dir=_TEST_PARENT) as temporary_directory:
            runtime, archive, lock, requirements = self._fixture(Path(temporary_directory))
            _VERIFIER.write_manifest(runtime, archive, lock, requirements)
            self._add_distribution(runtime / "site-packages", "unexpected", "9.9")
            with self.assertRaisesRegex(_VERIFIER.RuntimeVerificationError, "do not exactly match"):
                _VERIFIER.verify_manifest(runtime, archive, lock, requirements)

    def test_missing_wheel_record_file_rejects_incomplete_runtime(self):
        with tempfile.TemporaryDirectory(dir=_TEST_PARENT) as temporary_directory:
            runtime, archive, lock, requirements = self._fixture(Path(temporary_directory))
            _VERIFIER.write_manifest(runtime, archive, lock, requirements)
            (runtime / "site-packages" / "demo-1.0.dist-info" / "METADATA").unlink()
            with self.assertRaisesRegex(_VERIFIER.RuntimeVerificationError, "wheel METADATA"):
                _VERIFIER.verify_manifest(runtime, archive, lock, requirements)

    def test_unrecorded_importable_files_are_rejected(self):
        for relative in ("site-packages/sitecustomize.py", "sitecustomize.py"):
            with self.subTest(relative=relative), tempfile.TemporaryDirectory(dir=_TEST_PARENT) as temporary:
                runtime, archive, lock, requirements = self._fixture(Path(temporary))
                _VERIFIER.write_manifest(runtime, archive, lock, requirements)
                (runtime / relative).write_text("raise RuntimeError('unexpected')", encoding="utf-8")
                with self.assertRaises(_VERIFIER.RuntimeVerificationError):
                    _VERIFIER.verify_manifest(runtime, archive, lock, requirements)

    def test_record_cannot_omit_importable_file_digest(self):
        with tempfile.TemporaryDirectory(dir=_TEST_PARENT) as temporary:
            runtime, archive, lock, requirements = self._fixture(Path(temporary))
            record = runtime / "site-packages/demo-1.0.dist-info/RECORD"
            (runtime / "site-packages/demo.py").write_bytes(b"pass")
            record.write_text(record.read_text(encoding="utf-8") + "demo.py,,\n", encoding="utf-8")
            with self.assertRaisesRegex(_VERIFIER.RuntimeVerificationError, "no integrity digest"):
                _VERIFIER.write_manifest(runtime, archive, lock, requirements)
