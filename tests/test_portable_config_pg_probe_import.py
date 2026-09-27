"""Pure import contract for the portable PostgreSQL configuration probe."""

import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest


def _has_reparse_point(path: Path) -> bool:
    try:
        metadata = path.lstat()
    except FileNotFoundError:
        return False
    if stat.S_ISLNK(metadata.st_mode):
        return True
    is_junction = getattr(path, "is_junction", None)
    if is_junction is not None and is_junction():
        return True
    attributes = getattr(metadata, "st_file_attributes", 0)
    return bool(attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400))


def _assert_repository_path_without_reparse(repository: Path, target: Path) -> Path:
    repository = repository.resolve(strict=True)
    candidate = Path(os.path.abspath(target))
    try:
        if os.path.commonpath((str(repository), str(candidate))) != str(repository):
            raise RuntimeError("temporary test path escaped repository")
    except ValueError:
        raise RuntimeError("temporary test path escaped repository") from None

    current = Path(candidate.anchor)
    for part in candidate.parts[1:]:
        current = current / part
        if current.exists() or current.is_symlink():
            if _has_reparse_point(current):
                raise RuntimeError("temporary test path contains a reparse point")
    return candidate


class PortableConfigProbeImportTests(unittest.TestCase):
    def test_auth_rejects_missing_key_and_probe_supplies_scoped_test_key(self):
        repository = Path(__file__).resolve(strict=True).parents[1]
        temporary_parent = repository / ".tmp" / "tests"
        created_parents = []
        for directory in (repository / ".tmp", temporary_parent):
            _assert_repository_path_without_reparse(repository, directory)
            if directory.exists():
                if not directory.is_dir():
                    raise RuntimeError("temporary test parent is not a directory")
                continue
            directory.mkdir()
            created_parents.append(directory)
            _assert_repository_path_without_reparse(repository, directory)

        temporary_root = None
        child_source = r"""
import importlib.util
import os
from pathlib import Path
import sys
from unittest.mock import Mock

repository = Path(sys.argv[1])
temporary_root = Path(sys.argv[2])
for name in ("app", "data", "cache"):
    (temporary_root / name).mkdir()
sys.path.insert(0, str(repository))
import dotenv
dotenv.load_dotenv = Mock(return_value=False)
dotenv.dotenv_values = Mock(return_value={})

os.environ.update({
    "GOOFISH_PORTABLE_MODE": "portable",
    "GOOFISH_PORTABLE_PROGRAM_ROOT": str(temporary_root / "app"),
    "GOOFISH_PORTABLE_DATA_ROOT": str(temporary_root / "data"),
    "GOOFISH_PORTABLE_CACHE_ROOT": str(temporary_root / "cache"),
    "ENCRYPTION_MASTER_KEY": "synthetic-probe-encryption-key-2026",
})
os.environ.pop("SECRET_KEY", None)
try:
    import src.web.auth
except RuntimeError:
    print("CAS_PROBE_AUTH_WITHOUT_KEY=REJECTED_RUNTIMEERROR")
else:
    raise AssertionError("portable auth accepted missing signing key")

probe_path = repository / "tests" / "portable_config_pg_case.py"
spec = importlib.util.spec_from_file_location("portable_config_pg_case", probe_path)
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)
request = {
    "program_root": str(temporary_root / "app"),
    "data_root": str(temporary_root / "data"),
    "cache_root": str(temporary_root / "cache"),
    "encryption_master_key": "synthetic-probe-encryption-key-2026",
}
with probe._probe_environment(repository, request):
    import src.web.auth as auth
    assert len(auth.SECRET_KEY.encode("utf-8")) >= 32
    assert auth.SECRET_KEY != "xianyu-monitor-default-secret-key-change-me"
    dotenv.load_dotenv.assert_not_called()
    print("CAS_PROBE_AUTH_WITH_TEST_KEY=PASS")
assert "SECRET_KEY" not in os.environ

original_environment = {name: os.environ.get(name) for name in probe._PROBE_ENVIRONMENT_KEYS}
original_path = list(sys.path)
try:
    with probe._probe_environment(repository, request):
        raise RuntimeError("synthetic scope exit")
except RuntimeError:
    pass
else:
    raise AssertionError("synthetic scope exit did not raise")
assert all(os.environ.get(name) == value for name, value in original_environment.items())
assert sys.path == original_path
print("CAS_PROBE_ENV_RESTORE_ON_EXCEPTION=PASS")
"""
        try:
            temporary_root = Path(tempfile.mkdtemp(
                prefix="portable-config-pg-import-", dir=temporary_parent
            ))
            _assert_repository_path_without_reparse(repository, temporary_root)
            if temporary_root.resolve(strict=True).parent != temporary_parent.resolve(strict=True):
                raise RuntimeError("temporary test directory resolved outside its parent")
            child_env = {
                key: os.environ[key]
                for key in ("SystemRoot", "WINDIR", "PATH")
                if key in os.environ
            }
            child_env["PYTHONDONTWRITEBYTECODE"] = "1"
            result = subprocess.run(
                [sys.executable, "-I", "-B", "-c", child_source,
                 str(repository), str(temporary_root)],
                cwd=temporary_root,
                env=child_env,
                capture_output=True,
                text=True,
                timeout=45,
                check=False,
            )
            self.assertEqual(result.returncode, 0, "isolated auth import exited unexpectedly")
            self.assertEqual(
                result.stdout.splitlines(),
                [
                    "CAS_PROBE_AUTH_WITHOUT_KEY=REJECTED_RUNTIMEERROR",
                    "CAS_PROBE_AUTH_WITH_TEST_KEY=PASS",
                    "CAS_PROBE_ENV_RESTORE_ON_EXCEPTION=PASS",
                ],
                "isolated auth import markers did not match",
            )
        finally:
            if temporary_root is not None:
                _assert_repository_path_without_reparse(repository, temporary_parent)
                try:
                    temporary_root.lstat()
                except FileNotFoundError:
                    pass
                else:
                    _assert_repository_path_without_reparse(repository, temporary_root)
                    if temporary_root.resolve(strict=True).parent != temporary_parent.resolve(strict=True):
                        raise RuntimeError("refusing to clean temporary test directory outside its parent")
                    shutil.rmtree(temporary_root)
            for directory in reversed(created_parents):
                _assert_repository_path_without_reparse(repository, directory)
                try:
                    directory.rmdir()
                except FileNotFoundError:
                    continue
                except OSError as error:
                    raise RuntimeError("could not clean owned temporary test parent") from error


if __name__ == "__main__":
    unittest.main()
