"""Isolated checks for bounded portable business-path wiring."""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path


_REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
_TEST_TEMP_ROOT = _REPOSITORY_ROOT / ".tmp" / "tests" / "portable-app-paths"


class PortableAppPathsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        _TEST_TEMP_ROOT.mkdir(parents=True, exist_ok=True)

    @classmethod
    def tearDownClass(cls) -> None:
        try:
            _TEST_TEMP_ROOT.rmdir()
        except OSError:
            # This shared parent might have been created by another test run.
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

    def test_portable_user_files_are_data_owned_and_seed_reads_are_program_only(self) -> None:
        self._run(
            """
            import sys
            import types
            from pathlib import Path
            import src
            from src.runtime_paths import RuntimePaths

            root = Path.cwd()
            paths = RuntimePaths.create(
                program_root=root / "program",
                data_root=root / "data",
                cache_root=root / "cache",
            )
            config = types.ModuleType("src.config")
            config.PORTABLE_MODE = True
            config._portable_paths = paths
            sys.modules["src.config"] = config
            src.config = config

            import src.user_file_store as store

            private_path = store.get_user_scoped_path("prompts", "sample.txt", "owner/a")
            shared_path = store.get_shared_path("criteria", "rules.txt")
            seed_path = paths.program_path("defaults", "criteria", "rules.txt")
            assert private_path == paths.data_path("state", "user_files", "owner_a", "prompts", "sample.txt")
            assert shared_path == paths.data_path("assets", "criteria", "rules.txt")
            assert store.get_scoped_read_candidates("criteria", "rules.txt", None) == [shared_path, seed_path]

            seed_path.parent.mkdir(parents=True)
            seed_path.write_text("seed", encoding="utf-8")
            assert store.resolve_scoped_path("criteria", "rules.txt", None) == seed_path
            target = store.resolve_scoped_path("criteria", "rules.txt", None, for_write=True)
            assert target == shared_path
            assert target.parent.exists()
            assert not (paths.program_root / "assets").exists()

            for value in (
                "/outside.txt",
                "../outside.txt",
                r"C:\\outside.txt",
                "unknown/../outside.txt",
                "unknown/outside.txt",
            ):
                try:
                    store.resolve_virtual_task_file(value, "owner", for_write=True)
                except ValueError:
                    pass
                else:
                    raise AssertionError(f"unsafe portable virtual path accepted: {value}")

            for filename in (
                "..",
                ".",
                "bad\\x00name.txt",
                "report.txt:stream",
                r"C:\\outside.txt",
                "nested/file.txt",
            ):
                for resolver in (
                    store.get_shared_path,
                    lambda kind, name: store.get_user_scoped_path(kind, name, "owner"),
                ):
                    try:
                        resolver("prompts", filename)
                    except ValueError:
                        pass
                    else:
                        raise AssertionError(f"unsafe portable filename accepted: {filename}")
            """
        )

    def test_legacy_paths_remain_cwd_relative_and_absolute_task_paths_are_retained(self) -> None:
        self._run(
            """
            import sys
            import types
            from pathlib import Path
            import src

            config = types.ModuleType("src.config")
            config.PORTABLE_MODE = False
            sys.modules["src.config"] = config
            src.config = config
            import src.user_file_store as store

            assert store.get_shared_path("prompts", "sample.txt") == Path("prompts/sample.txt")
            assert store.get_shared_path("prompts", "nested/sample.txt") == Path("prompts/sample.txt")
            assert store.get_user_scoped_path("bayes", "model.json", "owner") == Path("state/user_files/owner/bayes/model.json")
            legacy_absolute = Path.cwd() / "old-task.txt"
            assert store.resolve_virtual_task_file(str(legacy_absolute), "owner") == legacy_absolute
            """
        )

    def test_legacy_user_file_paths_do_not_import_config_or_dotenv(self) -> None:
        self._run(
            """
            import importlib.abc
            import sys
            from pathlib import Path

            class ForbiddenConfigurationImport(importlib.abc.MetaPathFinder):
                def find_spec(self, fullname, path=None, target=None):
                    if fullname in {"src.config", "dotenv"}:
                        raise AssertionError(f"legacy user-file access imported {fullname}")
                    return None

            sys.meta_path.insert(0, ForbiddenConfigurationImport())
            import src.user_file_store as store
            assert store.get_shared_path("criteria", "rules.txt") == Path("criteria/rules.txt")
            assert store.get_user_scoped_path("prompts", "template.txt", "owner") == Path("state/user_files/owner/prompts/template.txt")
            assert not (Path.cwd() / "state").exists()
            """
        )

    def test_results_and_logs_use_portable_data_defaults_without_real_configuration(self) -> None:
        self._run(
            """
            import logging
            import sys
            import types
            from pathlib import Path
            import src
            from src.runtime_paths import RuntimePaths

            root = Path.cwd()
            paths = RuntimePaths.create(
                program_root=root / "program",
                data_root=root / "data",
                cache_root=root / "cache",
            )
            config = types.ModuleType("src.config")
            config.PORTABLE_MODE = True
            config._portable_paths = paths
            config.JSONL_FALLBACK_ON_DB_ERROR = lambda: False
            sys.modules["src.config"] = config
            src.config = config

            logging_config = types.ModuleType("src.logging_config")
            logging_config.get_logger = lambda *args, **kwargs: logging.getLogger(args[0])
            sys.modules["src.logging_config"] = logging_config
            openai = types.ModuleType("openai")
            openai.APIStatusError = type("APIStatusError", (Exception,), {})
            sys.modules["openai"] = openai
            requests = types.ModuleType("requests")
            request_exceptions = types.ModuleType("requests.exceptions")
            request_exceptions.HTTPError = type("HTTPError", (Exception,), {})
            requests.exceptions = request_exceptions
            sys.modules["requests"] = requests
            sys.modules["requests.exceptions"] = request_exceptions
            asyncio = types.ModuleType("asyncio")
            async def _sleep(*args, **kwargs):
                return None
            asyncio.sleep = _sleep
            sys.modules["asyncio"] = asyncio

            import src.utils as utils
            result = utils.save_to_jsonl({"商品信息": {}}, "portable keyword")
            try:
                result.send(None)
            except StopIteration as completed:
                assert completed.value is True
            else:
                raise AssertionError("save_to_jsonl unexpectedly awaited")
            result_file = paths.data_path("results", "jsonl", "portable_keyword_full_data.jsonl")
            assert result_file.exists()
            utils.write_log("portable log")
            assert paths.data_path("logs", "fetcher.log").read_text(encoding="utf-8") == "portable log\\n"

            import src.log_exporter as exporter
            log_dir = paths.data_path("logs")
            output_dir = paths.data_path("logs", "exports")
            exported = exporter.export_logs_package(days=1)
            assert exported is not None
            assert Path(exported).parent == output_dir
            assert output_dir.exists()
            explicit = Path("caller-selected")
            assert exporter._default_log_path(explicit, "logs", "logs") == explicit
            """
        )

    def test_prompt_guide_is_program_owned_in_portable_mode(self) -> None:
        self._run(
            """
            import sys
            import types
            from pathlib import Path
            import src
            from src.runtime_paths import RuntimePaths

            root = Path.cwd()
            paths = RuntimePaths.create(
                program_root=root / "program",
                data_root=root / "data",
                cache_root=root / "cache",
            )
            config = types.ModuleType("src.config")
            config.PORTABLE_MODE = True
            config._portable_paths = paths
            config.STORAGE_BACKEND = lambda: "local"
            config.client = None
            config.MODEL_NAME = lambda: ""
            sys.modules["src.config"] = config
            src.config = config
            logging_config = types.ModuleType("src.logging_config")
            import logging
            logging_config.get_logger = lambda *args, **kwargs: logging.getLogger(args[0])
            sys.modules["src.logging_config"] = logging_config
            aiofiles = types.ModuleType("aiofiles")
            sys.modules["aiofiles"] = aiofiles
            asyncio = types.ModuleType("asyncio")
            async def _sleep(*args, **kwargs):
                return None
            asyncio.sleep = _sleep
            sys.modules["asyncio"] = asyncio
            httpx = types.ModuleType("httpx")
            httpx.Timeout = lambda *args, **kwargs: None
            httpx.AsyncClient = object
            sys.modules["httpx"] = httpx
            openai = types.ModuleType("openai")
            openai.APITimeoutError = type("APITimeoutError", (Exception,), {})
            openai.AsyncOpenAI = type("AsyncOpenAI", (), {})
            sys.modules["openai"] = openai

            import src.prompt_utils as prompts
            assert prompts.WEIGHT_GUIDE_PATH == paths.program_path("prompts", "guide", "weight_framework_guide.md")
            """
        )

    def test_local_account_state_is_data_owned_in_portable_mode(self) -> None:
        self._run(
            """
            import logging
            import sys
            import types
            from pathlib import Path
            import src
            from src.runtime_paths import RuntimePaths

            root = Path.cwd()
            paths = RuntimePaths.create(
                program_root=root / "program",
                data_root=root / "data",
                cache_root=root / "cache",
            )
            config = types.ModuleType("src.config")
            config.PORTABLE_MODE = True
            config._portable_paths = paths
            sys.modules["src.config"] = config
            src.config = config
            aiofiles = types.ModuleType("aiofiles")
            sys.modules["aiofiles"] = aiofiles
            fastapi = types.ModuleType("fastapi")
            class Router:
                def __getattr__(self, name):
                    return lambda *args, **kwargs: (lambda function: function)
            fastapi.APIRouter = Router
            fastapi.HTTPException = type("HTTPException", (Exception,), {})
            fastapi.Request = object
            sys.modules["fastapi"] = fastapi
            pydantic = types.ModuleType("pydantic")
            pydantic.BaseModel = object
            sys.modules["pydantic"] = pydantic
            storage = types.ModuleType("src.storage")
            storage.get_storage = lambda: None
            sys.modules["src.storage"] = storage
            auth = types.ModuleType("src.web.auth")
            auth.get_current_user = lambda request: None
            auth.is_multi_user_mode = lambda: False
            sys.modules["src.web.auth"] = auth
            logging_config = types.ModuleType("src.logging_config")
            logging_config.get_logger = lambda *args, **kwargs: logging.getLogger(args[0])
            sys.modules["src.logging_config"] = logging_config

            import src.web.account_manager as accounts
            assert accounts.STATE_DIR == str(paths.data_path("state"))
            assert accounts.ACTIVE_ACCOUNT_FILE == str(paths.data_path("state", "_active.json"))
            accounts.ensure_state_dir()
            assert paths.data_path("state").is_dir()
            assert not (paths.program_root / "state").exists()
            """
        )


if __name__ == "__main__":
    unittest.main()
