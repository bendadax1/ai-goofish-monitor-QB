"""Isolated portable worker command, environment, and browser-path tests."""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path


_REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
_TEST_TEMP_ROOT = _REPOSITORY_ROOT / ".tmp" / "tests" / "portable-workers"


class PortableWorkerPathTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        _TEST_TEMP_ROOT.mkdir(parents=True, exist_ok=True)

    @classmethod
    def tearDownClass(cls) -> None:
        try:
            _TEST_TEMP_ROOT.rmdir()
        except OSError:
            # Another test run may own the shared parent.
            pass

    def _run(self, source: str) -> None:
        with tempfile.TemporaryDirectory(dir=_TEST_TEMP_ROOT) as temporary_directory:
            completed = subprocess.run(
                [sys.executable, "-B", "-c", textwrap.dedent(source)],
                cwd=temporary_directory,
                env={
                    "PATH": os.environ.get("PATH", ""),
                    "PYTHONPATH": str(_REPOSITORY_ROOT),
                    "PYTHONDONTWRITEBYTECODE": "1",
                },
                capture_output=True,
                text=True,
                timeout=30,
            )
            self.assertEqual(
                completed.returncode,
                0,
                msg=f"stdout:\n{completed.stdout}\nstderr:\n{completed.stderr}",
            )

    def test_portable_worker_command_environment_and_browser_are_root_pinned(self) -> None:
        self._run(
            """
            import os
            import sys
            import types
            from pathlib import Path
            import src
            from src.runtime_paths import RuntimePaths

            root = Path.cwd()
            program = root / "app" / "version"
            data = root / "data"
            cache = root / "cache"
            bootstrap = program / "scripts" / "portable" / "python-bootstrap.py"
            bootstrap.parent.mkdir(parents=True)
            bootstrap.write_text("# bootstrap", encoding="utf-8")
            browser_root = root / "browsers" / "chromium-1200"
            executable = browser_root / "chrome-win64" / "chrome.exe"
            executable.parent.mkdir(parents=True)
            executable.write_bytes(b"not-run")

            paths = RuntimePaths.create(program_root=program, data_root=data, cache_root=cache)
            config = types.ModuleType("src.config")
            config.PORTABLE_MODE = True
            config._portable_paths = paths
            config._PORTABLE_CONTROLLED_ENVIRONMENT = {
                "GOOFISH_PORTABLE_MODE": "portable",
                "GOOFISH_PORTABLE_PROGRAM_ROOT": str(program),
                "GOOFISH_PORTABLE_DATA_ROOT": str(data),
                "GOOFISH_PORTABLE_CACHE_ROOT": str(cache),
                "GOOFISH_PORTABLE_DATABASE_URL": "postgresql://launcher-controlled/portable",
                "GOOFISH_PORTABLE_BROWSER_ROOT": str(browser_root),
                "GOOFISH_PORTABLE_PROBE_TOKEN": "frozen-probe-secret",
            }
            sys.modules["src.config"] = config
            src.config = config

            from src.portable.app_paths import (
                portable_browser_executable,
                portable_worker_command,
                portable_worker_environment,
            )

            command = portable_worker_command("collector", ["--task-name", "中文 task"])
            assert command[0] == sys.executable
            assert command[1:6] == ["-I", "-B", "-u", str(bootstrap), "--app-root"]
            assert command[6] == str(program)
            assert command[7:10] == ["--target", "collector", "--"]
            assert command[10:] == ["--task-name", "中文 task"]

            environment = portable_worker_environment({
                "PYTHONPATH": "untrusted-project",
                "PYTHONHOME": "untrusted-runtime",
                "GOOFISH_OWNER_ID": "owner-1",
                "ENCRYPTION_MASTER_KEY": "needed-by-application",
                "GOOFISH_PORTABLE_DATA_ROOT": "wrong",
                "GOOFISH_LAUNCHER_TOKEN": "maintenance-secret",
                "GOOFISH_LAUNCHER_PROBE_TOKEN": "probe-secret",
                "GOOFISH_PORTABLE_ADMIN_DATABASE_URL": "postgresql://admin-secret",
                "GOOFISH_PORTABLE_PROBE_TOKEN": "portable-probe-secret",
                "UNRELATED": "kept",
            })
            assert "PYTHONPATH" not in environment
            assert "PYTHONHOME" not in environment
            assert "GOOFISH_LAUNCHER_TOKEN" not in environment
            assert "GOOFISH_LAUNCHER_PROBE_TOKEN" not in environment
            assert "GOOFISH_PORTABLE_ADMIN_DATABASE_URL" not in environment
            assert "GOOFISH_PORTABLE_PROBE_TOKEN" not in environment
            assert environment["GOOFISH_OWNER_ID"] == "owner-1"
            assert environment["ENCRYPTION_MASTER_KEY"] == "needed-by-application"
            assert environment["UNRELATED"] == "kept"
            assert environment["PYTHONDONTWRITEBYTECODE"] == "1"
            assert environment["GOOFISH_PORTABLE_DATA_ROOT"] == str(data)
            assert environment["GOOFISH_PORTABLE_DATABASE_URL"].endswith("/portable")
            assert environment["TEMP"] == environment["TMP"] == str(cache / "workers")
            assert (cache / "workers").is_dir()
            assert portable_browser_executable() == executable.resolve()

            os.environ["GOOFISH_PORTABLE_BROWSER_ROOT"] = str(root / "outside")
            os.environ["PLAYWRIGHT_BROWSERS_PATH"] = str(root / "also-outside")
            assert portable_browser_executable() == executable.resolve()

            try:
                portable_worker_command("maintenance", [])
            except ValueError:
                pass
            else:
                raise AssertionError("non-worker bootstrap target accepted")
            """
        )

    def test_portable_browser_rejects_missing_or_data_cache_overlap(self) -> None:
        self._run(
            """
            import sys
            import types
            from pathlib import Path
            import src
            from src.runtime_paths import RuntimePaths

            root = Path.cwd()
            program, data, cache = root / "app" / "version", root / "data", root / "cache"
            program.mkdir(parents=True)
            paths = RuntimePaths.create(program_root=program, data_root=data, cache_root=cache)
            config = types.ModuleType("src.config")
            config.PORTABLE_MODE = True
            config._portable_paths = paths
            config._PORTABLE_CONTROLLED_ENVIRONMENT = {
                "GOOFISH_PORTABLE_BROWSER_ROOT": str(data),
            }
            sys.modules["src.config"] = config
            src.config = config
            from src.portable.app_paths import portable_browser_executable
            try:
                portable_browser_executable()
            except RuntimeError:
                pass
            else:
                raise AssertionError("data-overlapping browser root accepted")

            config._PORTABLE_CONTROLLED_ENVIRONMENT["GOOFISH_PORTABLE_BROWSER_ROOT"] = str(cache)
            try:
                portable_browser_executable()
            except RuntimeError:
                pass
            else:
                raise AssertionError("cache-overlapping browser root accepted")
            """
        )

    def test_resolved_portable_reference_path_is_not_virtual_path_resolved_twice(self) -> None:
        """The task-manager-to-prompt chain passes an internal resolved Path once."""

        self._run(
            """
            import builtins
            import logging
            import sys
            import types
            from pathlib import Path
            import src
            from src.runtime_paths import RuntimePaths

            root = Path.cwd()
            paths = RuntimePaths.create(
                program_root=root / "app" / "version",
                data_root=root / "data",
                cache_root=root / "cache",
            )
            config = types.ModuleType("src.config")
            config.PORTABLE_MODE = True
            config._portable_paths = paths
            config.STORAGE_BACKEND = lambda: "local"
            config.client = object()
            config.MODEL_NAME = lambda: "unused"
            sys.modules["src.config"] = config
            src.config = config
            logging_config = types.ModuleType("src.logging_config")
            logging_config.get_logger = lambda *args, **kwargs: logging.getLogger(args[0])
            sys.modules["src.logging_config"] = logging_config
            asyncio = types.ModuleType("asyncio")
            async def _sleep(*args, **kwargs):
                return None
            asyncio.sleep = _sleep
            sys.modules["asyncio"] = asyncio
            sys.modules["aiofiles"] = types.ModuleType("aiofiles")
            httpx = types.ModuleType("httpx")
            httpx.Timeout = lambda *args, **kwargs: None
            httpx.AsyncClient = object
            sys.modules["httpx"] = httpx
            openai = types.ModuleType("openai")
            openai.APITimeoutError = type("APITimeoutError", (Exception,), {})
            openai.AsyncOpenAI = type("AsyncOpenAI", (), {})
            sys.modules["openai"] = openai

            from src.user_file_store import resolve_virtual_task_file
            import src.prompt_utils as prompts

            resolved_path = resolve_virtual_task_file("prompts/base_prompt.txt", "owner")
            assert resolved_path.is_absolute()
            observed = []
            original_open = builtins.open
            def stop_after_observing(path, *args, **kwargs):
                observed.append(Path(path))
                raise OSError("stop after path observation")
            builtins.open = stop_after_observing
            try:
                coroutine = prompts.generate_criteria("need", str(resolved_path), owner_id="owner")
                try:
                    coroutine.send(None)
                except OSError:
                    pass
                else:
                    raise AssertionError("reference load unexpectedly continued")
            finally:
                builtins.open = original_open
            assert observed == [resolved_path]
            """
        )

    def test_worker_consumers_keep_fixed_bootstrap_targets_and_portable_cwd(self) -> None:
        """Check bounded source contracts without importing service dependencies."""

        expected = {
            "src/web/task_manager.py": (
                'portable_worker_command("collector", collector_arguments)',
                "cwd=str(_portable_runtime_paths.data_root)",
                "reference_file_path = resolve_virtual_task_file(reference_file, owner_id=owner_id, for_write=False)",
                "reference_file_path=str(reference_file_path)",
            ),
            "src/web/scheduler.py": (
                'portable_worker_command("collector", collector_arguments)',
                "cwd=str(_portable_runtime_paths.data_root)",
            ),
            "src/web/settings_manager.py": (
                'portable_worker_command("login", [])',
                "cwd=str(portable_paths.data_root)",
                'program_path("prompts", "guide", "bayes_guide.md")',
            ),
            "login.py": (
                'data_path("state")',
                "portable_browser_executable()",
                "executable_path=str(portable_browser)",
            ),
            "src/scraper.py": (
                'data_path("state", "task_stats")',
                'data_path("results", "jsonl")',
                "portable_browser_executable()",
                "executable_path=str(portable_browser)",
            ),
        }
        for relative_path, required_fragments in expected.items():
            with self.subTest(path=relative_path):
                source = (_REPOSITORY_ROOT / relative_path).read_text(encoding="utf-8")
                for fragment in required_fragments:
                    self.assertIn(fragment, source)

    def test_bootstrap_import_does_not_modify_program_component(self) -> None:
        """Run only harmless fixture code, never the real collector/login."""
        with tempfile.TemporaryDirectory(dir=_TEST_TEMP_ROOT) as temporary:
            root = Path(temporary)
            app = root / "app"
            app.mkdir()
            (app / "fixture_module.py").write_text("value = 42\n", encoding="utf-8")
            (app / "collector.py").write_text("import fixture_module\nassert fixture_module.value == 42\n", encoding="utf-8")
            environment = {key: value for key, value in os.environ.items()
                           if not key.startswith("PYTHON")}
            # Deliberately omit -B: bootstrap must enforce immutability too.
            completed = subprocess.run([sys.executable, "-I", str(_REPOSITORY_ROOT / "scripts/portable/python-bootstrap.py"),
                "--app-root", str(app), "--target", "collector"], cwd=root, env=environment,
                capture_output=True, text=True, timeout=15)
            self.assertEqual(completed.returncode, 0, completed.stderr)
            self.assertEqual({path.name for path in app.iterdir()}, {"fixture_module.py", "collector.py"})


if __name__ == "__main__":
    unittest.main()
