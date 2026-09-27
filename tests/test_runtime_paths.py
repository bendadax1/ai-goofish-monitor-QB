import importlib
import logging
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import src.runtime_paths as runtime_paths


RuntimePaths = runtime_paths.RuntimePaths
_REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
_TEST_TEMP_ROOT = _REPOSITORY_ROOT / ".tmp" / "tests" / "runtime_paths"
logger = logging.getLogger(__name__)


class _MisleadingAbsolutePathLike:
    def __init__(self, path: Path):
        self.path = path

    def __fspath__(self):
        return str(self.path)

    def __str__(self):
        return "apparently-relative.txt"


class RuntimePathsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls._created_temp_parents = []
        for directory in (
            _REPOSITORY_ROOT / ".tmp",
            _REPOSITORY_ROOT / ".tmp" / "tests",
            _TEST_TEMP_ROOT,
        ):
            if not directory.exists():
                directory.mkdir()
                cls._created_temp_parents.append(directory)

    @classmethod
    def tearDownClass(cls):
        for directory in reversed(cls._created_temp_parents):
            try:
                directory.rmdir()
            except OSError as exc:
                # Leave an existing/non-empty shared parent untouched.
                logger.warning("Could not remove test temporary parent %s: %s", directory, exc)
                break

    def temporary_directory(self):
        return tempfile.TemporaryDirectory(dir=_TEST_TEMP_ROOT)

    def test_source_layout_defaults_are_absolute_and_share_program_root(self):
        paths = RuntimePaths.create()
        expected_root = Path(__file__).resolve().parents[1]

        self.assertEqual(paths.program_root, expected_root)
        self.assertEqual(paths.data_root, expected_root)
        self.assertEqual(paths.cache_root, expected_root)
        self.assertTrue(paths.program_path("static", "china", "index.json").is_absolute())

    def test_explicit_roots_and_chinese_space_paths_do_not_create_directories(self):
        with self.temporary_directory() as temporary_directory:
            base = Path(temporary_directory)
            program_root = base / "程序 资源"
            data_root = base / "用户 数据"
            cache_root = base / "运行 缓存"

            paths = RuntimePaths.create(
                program_root=program_root,
                data_root=data_root,
                cache_root=cache_root,
            )

            self.assertEqual(paths.program_path("模板", "首页.html"), program_root / "模板" / "首页.html")
            self.assertEqual(paths.data_path("账号 状态", "用户一.json"), data_root / "账号 状态" / "用户一.json")
            self.assertEqual(paths.cache_path("任务 图片", "图一.png"), cache_root / "任务 图片" / "图一.png")
            self.assertFalse(program_root.exists())
            self.assertFalse(data_root.exists())
            self.assertFalse(cache_root.exists())

    def test_resolution_is_independent_of_current_working_directory(self):
        with self.temporary_directory() as temporary_directory:
            base = Path(temporary_directory)
            program_root = base / "program"
            paths = RuntimePaths.create(program_root=program_root)
            original_cwd = Path.cwd()
            try:
                os.chdir(base)
                self.assertEqual(paths.data_path("config.json"), program_root / "config.json")
                self.assertEqual(paths.cache_path("logs"), program_root / "logs")
                recreated = RuntimePaths.create(program_root=program_root)
                defaults_after_chdir = RuntimePaths.create()
                self.assertEqual(recreated.program_root, program_root)
                self.assertEqual(defaults_after_chdir.program_root, _REPOSITORY_ROOT)
            finally:
                os.chdir(original_cwd)

    def test_direct_construction_normalizes_and_rejects_relative_roots(self):
        with self.temporary_directory() as temporary_directory:
            base = Path(temporary_directory)
            paths = RuntimePaths(base / "a" / ".." / "program", base / "data", base / "cache")

            self.assertEqual(paths.program_root, (base / "program").resolve())

        with self.assertRaisesRegex(ValueError, "program_root must be an absolute path"):
            RuntimePaths(Path("program"), Path("data"), Path("cache"))

    def test_rooted_and_drive_relative_parts_cannot_replace_selected_root(self):
        with self.temporary_directory() as temporary_directory:
            base = Path(temporary_directory)
            paths = RuntimePaths(base / "program", base / "data", base / "cache")

            for invalid_part in (
                str(base / "outside.txt"),
                r"\outside.txt",
                r"C:\outside.txt",
                "C:outside.txt",
                _MisleadingAbsolutePathLike(base / "pathlike-outside.txt"),
            ):
                with self.subTest(invalid_part=invalid_part):
                    with self.assertRaisesRegex(ValueError, "path part must be relative"):
                        paths.data_path(invalid_part)

    def test_import_and_create_do_not_load_dotenv_or_mutate_environment(self):
        with self.temporary_directory() as temporary_directory:
            base = Path(temporary_directory)
            env_file = base / ".env"
            env_file.write_text("RUNTIME_PATHS_DOTENV_MARKER=must_not_load\n", encoding="utf-8")
            original_cwd = Path.cwd()
            try:
                os.chdir(base)
                with mock.patch.dict(os.environ, {}, clear=True):
                    before = dict(os.environ)
                    reloaded = importlib.reload(runtime_paths)
                    paths = reloaded.RuntimePaths.create(program_root=base / "program")
                    self.assertEqual(dict(os.environ), before)
                    self.assertNotIn("RUNTIME_PATHS_DOTENV_MARKER", os.environ)
                    self.assertEqual(paths.data_root, (base / "program").resolve())
            finally:
                os.chdir(original_cwd)

            self.assertEqual(env_file.read_text(encoding="utf-8"), "RUNTIME_PATHS_DOTENV_MARKER=must_not_load\n")


if __name__ == "__main__":
    unittest.main()
