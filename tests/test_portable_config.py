"""Subprocess-isolated checks for bounded normal portable configuration."""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path


_REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
_TEST_TEMP_ROOT = _REPOSITORY_ROOT / ".tmp" / "tests" / "portable-config"
_SUBPROCESS_STUBS = r'''
import os
import sys
import types
from pathlib import Path

def _dotenv_values(path=".env"):
    values = {}
    candidate = Path(path)
    if candidate.exists():
        for line in candidate.read_text(encoding="utf-8").splitlines():
            if "=" in line and not line.lstrip().startswith("#"):
                key, value = line.split("=", 1)
                values[key.strip()] = value
    return values

def _load_dotenv(path=None, override=False):
    for key, value in _dotenv_values(path or ".env").items():
        if override or key not in os.environ:
            os.environ[key] = value

dotenv = types.ModuleType("dotenv")
dotenv.dotenv_values = _dotenv_values
dotenv.load_dotenv = _load_dotenv
sys.modules["dotenv"] = dotenv

openai = types.ModuleType("openai")
openai.AsyncOpenAI = type("AsyncOpenAI", (), {"__init__": lambda self, **kwargs: None})
sys.modules["openai"] = openai
httpx = types.ModuleType("httpx")
httpx.AsyncClient = type("AsyncClient", (), {"__init__": lambda self, **kwargs: None})
sys.modules["httpx"] = httpx

import logging
logging_config = types.ModuleType("src.logging_config")
logging_config.get_logger = lambda *args, **kwargs: logging.getLogger(args[0])
sys.modules["src.logging_config"] = logging_config

sqlalchemy = types.ModuleType("sqlalchemy")
sqlalchemy.__path__ = []
sqlalchemy_engine = types.ModuleType("sqlalchemy.engine")
sqlalchemy_engine.__path__ = []
sqlalchemy_engine_url = types.ModuleType("sqlalchemy.engine.url")
def _make_url(value):
    if not value:
        raise ValueError("empty dsn")
    return value
sqlalchemy_engine_url.make_url = _make_url
sys.modules["sqlalchemy"] = sqlalchemy
sys.modules["sqlalchemy.engine"] = sqlalchemy_engine
sys.modules["sqlalchemy.engine.url"] = sqlalchemy_engine_url
'''


class PortableConfigTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        _TEST_TEMP_ROOT.mkdir(parents=True, exist_ok=True)

    @classmethod
    def tearDownClass(cls) -> None:
        try:
            _TEST_TEMP_ROOT.rmdir()
        except OSError:
            # A non-empty shared test root is intentionally preserved for its
            # owner; each case cleans only its TemporaryDirectory child.
            pass

    def _run(self, source: str, environment: dict[str, str] | None = None) -> None:
        with tempfile.TemporaryDirectory(dir=_TEST_TEMP_ROOT) as temporary_directory:
            temporary_root = Path(temporary_directory)
            process_environment = {
                "PATH": os.environ.get("PATH", ""),
                "PYTHONPATH": str(_REPOSITORY_ROOT),
                "PYTHONDONTWRITEBYTECODE": "1",
            }
            if "SYSTEMROOT" in os.environ:
                process_environment["SYSTEMROOT"] = os.environ["SYSTEMROOT"]
            process_environment.update(environment or {})
            completed = subprocess.run(
                [sys.executable, "-B", "-c", _SUBPROCESS_STUBS + "\n" + textwrap.dedent(source)],
                cwd=temporary_root,
                env=process_environment,
                capture_output=True,
                text=True,
                timeout=30,
            )
            self.assertEqual(
                completed.returncode,
                0,
                msg=f"stdout:\n{completed.stdout}\nstderr:\n{completed.stderr}",
            )

    def test_portable_paths_are_explicit_and_ignore_cwd_parent_and_program_env_files(self) -> None:
        self._run(
            """
            import os
            from pathlib import Path

            root = Path.cwd()
            program = root / "program"
            data = root / "data"
            cache = root / "cache"
            nested = root / "different-cwd" / "child"
            nested.mkdir(parents=True)
            (root / ".env").write_text("OPENAI_API_KEY=parent-secret\\n", encoding="utf-8")
            program.mkdir()
            (program / ".env").write_text("DATABASE_URL=program-secret\\n", encoding="utf-8")
            (data / "config").mkdir(parents=True)
            (data / "config" / "app.env").write_text("RUN_HEADLESS=false\\nLOG_LEVEL=debug\\n", encoding="utf-8")
            os.environ.update({
                "GOOFISH_PORTABLE_MODE": "portable",
                "GOOFISH_PORTABLE_PROGRAM_ROOT": str(program),
                "GOOFISH_PORTABLE_DATA_ROOT": str(data),
                "GOOFISH_PORTABLE_CACHE_ROOT": str(cache),
                "GOOFISH_PORTABLE_DATABASE_URL": "postgresql://portable:controlled@127.0.0.1:55432/portable",
            })
            os.chdir(nested)

            import src.config as config

            assert config.IMAGE_SAVE_DIR == str(data / "results" / "images")
            assert config.CONFIG_FILE == str(data / "config" / "config.json")
            assert config.LOG_DIR() == str(data / "logs")
            assert os.environ["RUN_HEADLESS"] == "false"
            assert config.LOG_LEVEL() == "DEBUG"
            assert os.getenv("OPENAI_API_KEY") is None
            assert config.DATABASE_URL().endswith("/portable")
            """,
        )

    def test_portable_launcher_values_win_and_file_values_refresh_or_clear_on_reload(self) -> None:
        self._run(
            """
            import os
            from pathlib import Path

            root = Path.cwd()
            program, data, cache = root / "program", root / "data", root / "cache"
            (data / "config").mkdir(parents=True)
            env_file = data / "config" / "app.env"
            env_file.write_text("RUN_HEADLESS=false\\nLOG_LEVEL=INFO\\n", encoding="utf-8")
            os.environ.update({
                "GOOFISH_PORTABLE_MODE": "portable",
                "GOOFISH_PORTABLE_PROGRAM_ROOT": str(program),
                "GOOFISH_PORTABLE_DATA_ROOT": str(data),
                "GOOFISH_PORTABLE_CACHE_ROOT": str(cache),
                "GOOFISH_PORTABLE_DATABASE_URL": "postgresql://portable:controlled@127.0.0.1:55432/portable",
                "RUN_HEADLESS": "true",
            })

            import src.config as config
            assert os.environ["RUN_HEADLESS"] == "true"
            assert os.environ["LOG_LEVEL"] == "INFO"
            env_file.write_text("RUN_HEADLESS=false\\nLOG_LEVEL=DEBUG\\n", encoding="utf-8")
            config.reload_config()
            assert os.environ["RUN_HEADLESS"] == "true"
            assert os.environ["LOG_LEVEL"] == "DEBUG"
            env_file.write_text("RUN_HEADLESS=false\\n", encoding="utf-8")
            config.reload_config()
            assert os.environ["RUN_HEADLESS"] == "true"
            assert "LOG_LEVEL" not in os.environ
            """
        )

    def test_portable_rejects_invalid_writes_without_changing_file_or_environment(self) -> None:
        self._run(
            """
            import os
            from pathlib import Path

            root = Path.cwd()
            program, data, cache = root / "program", root / "data", root / "cache"
            (data / "config").mkdir(parents=True)
            env_file = data / "config" / "app.env"
            env_file.write_text("LOG_LEVEL=INFO\\n", encoding="utf-8")
            os.environ.update({
                "GOOFISH_PORTABLE_MODE": "portable",
                "GOOFISH_PORTABLE_PROGRAM_ROOT": str(program),
                "GOOFISH_PORTABLE_DATA_ROOT": str(data),
                "GOOFISH_PORTABLE_CACHE_ROOT": str(cache),
                "GOOFISH_PORTABLE_DATABASE_URL": "postgresql://portable:controlled@127.0.0.1:55432/portable",
            })
            import src.config as config

            before_file = env_file.read_text(encoding="utf-8")
            before_environment = dict(os.environ)
            for settings, keys in (
                ({"OPENAI_API_KEY": "must-not-persist"}, ["OPENAI_API_KEY"]),
                ({"LOG_LEVEL": "INFO\\nDATABASE_URL=bad"}, ["LOG_LEVEL"]),
                ({"LOG_MAX_BYTES": 0}, ["LOG_MAX_BYTES"]),
            ):
                try:
                    config.save_env_settings(settings, keys)
                except ValueError:
                    pass
                else:
                    raise AssertionError("invalid portable preference write succeeded")
                assert env_file.read_text(encoding="utf-8") == before_file
                assert dict(os.environ) == before_environment
            """
        )

    def test_legacy_dotenv_override_remains_and_cannot_supply_portable_controls(self) -> None:
        self._run(
            """
            import os
            import sys
            import types
            from pathlib import Path

            def values(path=".env"):
                result = {}
                candidate = Path(path)
                if candidate.exists():
                    for line in candidate.read_text(encoding="utf-8").splitlines():
                        if "=" in line:
                            key, value = line.split("=", 1)
                            result[key] = value
                return result

            def load(path=None, override=False):
                for key, value in values(path or ".env").items():
                    if override or key not in os.environ:
                        os.environ[key] = value

            fake_dotenv = types.ModuleType("dotenv")
            fake_dotenv.dotenv_values = values
            fake_dotenv.load_dotenv = load
            sys.modules["dotenv"] = fake_dotenv
            Path(".env").write_text(
                "LEGACY_PRIORITY=file\\nGOOFISH_PORTABLE_MODE=portable\\nGOOFISH_PORTABLE_DATABASE_URL=bad\\n",
                encoding="utf-8",
            )
            os.environ["LEGACY_PRIORITY"] = "process"
            import src.config as config
            assert config.PORTABLE_MODE is False
            assert os.environ["LEGACY_PRIORITY"] == "file"
            assert "GOOFISH_PORTABLE_MODE" not in os.environ
            assert "GOOFISH_PORTABLE_DATABASE_URL" not in os.environ
            assert config.STORAGE_BACKEND() == "local"
            """
        )

    def test_context_rejects_unsafe_mode_and_program_writable_root_overlap(self) -> None:
        self._run(
            """
            from pathlib import Path
            from src.portable.context import PortableConfigurationError, portable_mode, portable_paths

            try:
                portable_mode(environ={"GOOFISH_PORTABLE_MODE": "typo"})
            except PortableConfigurationError:
                pass
            else:
                raise AssertionError("invalid mode selected legacy")

            root = Path.cwd()
            for data, cache in ((root / "program" / "data", root / "cache"), (root / "data", root / "program" / "cache")):
                try:
                    portable_paths(environ={
                        "GOOFISH_PORTABLE_PROGRAM_ROOT": str(root / "program"),
                        "GOOFISH_PORTABLE_DATA_ROOT": str(data),
                        "GOOFISH_PORTABLE_CACHE_ROOT": str(cache),
                    })
                except PortableConfigurationError:
                    pass
                else:
                    raise AssertionError("overlapping program and writable root accepted")
            """
        )

    def test_portable_storage_uses_controlled_dsn_without_schema_creation(self) -> None:
        self._run(
            """
            import os
            import sys
            import types
            from pathlib import Path

            root = Path.cwd()
            program, data, cache = root / "program", root / "data", root / "cache"
            os.environ.update({
                "GOOFISH_PORTABLE_MODE": "portable",
                "GOOFISH_PORTABLE_PROGRAM_ROOT": str(program),
                "GOOFISH_PORTABLE_DATA_ROOT": str(data),
                "GOOFISH_PORTABLE_CACHE_ROOT": str(cache),
                "GOOFISH_PORTABLE_DATABASE_URL": "postgresql://portable:controlled@127.0.0.1:55432/portable",
                "DATABASE_URL": "postgresql://wrong:wrong@127.0.0.1:55432/wrong",
                "STORAGE_BACKEND": "local",
            })
            import src.storage as storage
            observed = {}
            class Adapter:
                def __init__(self, dsn):
                    observed["dsn"] = dsn
                def create_tables(self):
                    raise AssertionError("portable get_storage must not create tables")
            fake_adapter = types.ModuleType("src.storage.postgres_adapter")
            fake_adapter.PostgresAdapter = Adapter
            sys.modules["src.storage.postgres_adapter"] = fake_adapter
            selected = storage.get_storage()
            assert isinstance(selected, Adapter)
            assert observed["dsn"].endswith("/portable")
            assert "wrong" not in observed["dsn"]
            """
        )

    def test_portable_master_key_rejects_known_sentinels(self) -> None:
        self._run(
            """
            import hashlib
            import os
            from pathlib import Path

            root = Path.cwd()
            program, data, cache = root / "program", root / "data", root / "cache"
            os.environ.update({
                "GOOFISH_PORTABLE_MODE": "portable",
                "GOOFISH_PORTABLE_PROGRAM_ROOT": str(program),
                "GOOFISH_PORTABLE_DATA_ROOT": str(data),
                "GOOFISH_PORTABLE_CACHE_ROOT": str(cache),
                "GOOFISH_PORTABLE_DATABASE_URL": "postgresql://portable:controlled@127.0.0.1:55432/portable",
            })
            from src.storage.utils import get_master_key
            for value in ("", "changeme", "change-this-in-production", "default-encryption-key-change-in-production"):
                os.environ["ENCRYPTION_MASTER_KEY"] = value
                try:
                    get_master_key()
                except RuntimeError:
                    pass
                else:
                    raise AssertionError("portable accepted a rejected master-key sentinel")

            """
        )

    def test_legacy_master_key_derivation_stays_stable(self) -> None:
        self._run(
            """
            import hashlib
            import os
            os.environ["ENCRYPTION_MASTER_KEY"] = "legacy-test-key"
            from src.storage.utils import get_master_key
            assert get_master_key() == hashlib.sha256(b"legacy-test-key").digest()
            """
        )

    def test_portable_default_admin_helper_has_no_session_side_effect(self) -> None:
        self._run(
            """
            import os
            from pathlib import Path

            root = Path.cwd()
            program, data, cache = root / "program", root / "data", root / "cache"
            os.environ.update({
                "GOOFISH_PORTABLE_MODE": "portable",
                "GOOFISH_PORTABLE_PROGRAM_ROOT": str(program),
                "GOOFISH_PORTABLE_DATA_ROOT": str(data),
                "GOOFISH_PORTABLE_CACHE_ROOT": str(cache),
                "GOOFISH_PORTABLE_DATABASE_URL": "postgresql://portable:controlled@127.0.0.1:55432/portable",
            })
            import sys
            import types
            storage_package = types.ModuleType("src.storage")
            storage_package.__path__ = [str(Path.cwd().parents[3] / "src" / "storage")]
            # Use the repository package path supplied by PYTHONPATH rather
            # than importing storage.__init__, which is outside this helper.
            import src
            storage_package.__path__ = [str(Path(src.__file__).resolve().parent / "storage")]
            sys.modules["src.storage"] = storage_package
            sqlalchemy = sys.modules["sqlalchemy"]
            sqlalchemy.create_engine = lambda *args, **kwargs: object()
            sqlalchemy.and_ = lambda *args: args
            sqlalchemy.or_ = lambda *args: args
            dialects = types.ModuleType("sqlalchemy.dialects")
            dialects.__path__ = []
            postgresql = types.ModuleType("sqlalchemy.dialects.postgresql")
            postgresql.insert = lambda *args, **kwargs: None
            orm = types.ModuleType("sqlalchemy.orm")
            orm.sessionmaker = lambda **kwargs: None
            orm.Session = object
            sys.modules["sqlalchemy.dialects"] = dialects
            sys.modules["sqlalchemy.dialects.postgresql"] = postgresql
            sys.modules["sqlalchemy.orm"] = orm
            interface = types.ModuleType("src.storage.interface")
            interface.StorageInterface = object
            sys.modules["src.storage.interface"] = interface
            models = types.ModuleType("src.storage.models")
            for name in ("Base", "User", "Session", "Task", "MonitoringResult", "BayesProfile", "BayesSample", "UserFeedback", "AiCriteria", "PromptTemplate", "UserApiConfig", "UserNotificationConfig", "UserPlatformAccount", "AuditLog", "UserGroup", "UserGroupMember", "GroupPermission"):
                setattr(models, name, type(name, (), {}))
            sys.modules["src.storage.models"] = models
            utilities = types.ModuleType("src.storage.utils")
            for name in ("hash_password", "verify_password", "hash_token", "generate_uuid", "encrypt_sensitive", "decrypt_sensitive"):
                setattr(utilities, name, lambda *args, **kwargs: None)
            sys.modules["src.storage.utils"] = utilities
            configuration = types.ModuleType("src.config")
            configuration.PORTABLE_MODE = True
            configuration.WEB_USERNAME = lambda: "admin"
            configuration.WEB_PASSWORD = lambda: "admin123"
            sys.modules["src.config"] = configuration
            from src.storage.postgres_adapter import PostgresAdapter
            class ForbiddenSession:
                def query(self, *args, **kwargs):
                    raise AssertionError("portable default-admin helper queried the database")
            adapter = object.__new__(PostgresAdapter)
            assert adapter._ensure_default_super_admin(ForbiddenSession()) is None
            """
        )


if __name__ == "__main__":
    unittest.main()
