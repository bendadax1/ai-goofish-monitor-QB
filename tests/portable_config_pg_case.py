"""Private-stdio real PostgreSQL probe for portable per-user AI config CAS.

The test host must provide its own disposable, loopback-only PostgreSQL cluster
and an application DSN. This probe never starts/stops PostgreSQL or touches live
instance configuration, and issues no AI, scraping, or notification requests.
"""

from __future__ import annotations

import asyncio
from contextlib import contextmanager
import json
import os
from pathlib import Path
import sys
import threading
import uuid
from concurrent.futures import ThreadPoolExecutor
from urllib.parse import urlparse
from unittest import mock


PROBE_STEPS = frozenset({
    "arguments", "request-validate", "import-fastapi", "import-sqlalchemy",
    "import-storage-adapter", "import-settings-manager", "engine-create", "storage-create",
    "db-identity", "db-identity-address", "db-identity-port", "seed-users",
    "config-a-create", "config-b-create",
    "delete-recreate", "concurrent-cas", "settings-update", "host-change",
    "user-isolation", "default-switch", "cleanup-dispose", "cleanup-delete-users",
    "cleanup-engine-dispose", "cleanup-environment",
})
PROBE_EXCEPTION_CLASSES = frozenset({
    "AssertionError", "AttributeError", "DBAPIError", "FileNotFoundError", "HTTPException",
    "ImportError",
    "IntegrityError", "InterfaceError", "JSONDecodeError", "ModuleNotFoundError",
    "OperationalError", "OSError", "PermissionError", "ProgrammingError",
    "RuntimeError", "SyntaxError", "TimeoutError", "TypeError", "ValueError",
})
_PROBE_SECRET_KEY = "portable-cas-probe-session-key-2026-09-24"
_PROBE_ENVIRONMENT_KEYS = (
    "GOOFISH_PORTABLE_MODE",
    "GOOFISH_PORTABLE_PROGRAM_ROOT",
    "GOOFISH_PORTABLE_DATA_ROOT",
    "GOOFISH_PORTABLE_CACHE_ROOT",
    "ENCRYPTION_MASTER_KEY",
    "SECRET_KEY",
)


def _set_probe_step(current_step: list[str], step: str) -> None:
    current_step[0] = step if step in PROBE_STEPS else "arguments"


def _safe_exception_class(exception: Exception) -> str:
    name = type(exception).__name__
    return name if name in PROBE_EXCEPTION_CLASSES else "Other"


def _validate_database_identity(
    address: object, port: object, database_url: str, current_step: list[str]
) -> None:
    """Fail closed while reporting only which fixed identity check failed."""
    _set_probe_step(current_step, "db-identity-address")
    if address not in {"127.0.0.1", "::1"}:
        raise RuntimeError("database address identity check failed")
    _set_probe_step(current_step, "db-identity-port")
    if int(port) != urlparse(database_url).port:
        raise RuntimeError("database port identity check failed")


def _cleanup_probe_resources(
    current_step: list[str],
    primary_failure_step: str | None,
    dispose_storage,
    delete_users,
    dispose_engine,
) -> None:
    """Run every cleanup action and retain the step for the propagated failure."""
    cleanup_failure: tuple[Exception, str] | None = None
    cleanup_actions = (
        ("cleanup-dispose", dispose_storage),
        ("cleanup-delete-users", delete_users),
        ("cleanup-engine-dispose", dispose_engine),
    )
    for step, action in cleanup_actions:
        _set_probe_step(current_step, step)
        try:
            action()
        except Exception as exception:
            # Match finally semantics: the last cleanup exception takes
            # precedence, while still attempting the remaining cleanup steps.
            cleanup_failure = (exception, step)

    if cleanup_failure is not None:
        exception, step = cleanup_failure
        _set_probe_step(current_step, step)
        raise exception
    if primary_failure_step is not None:
        _set_probe_step(current_step, primary_failure_step)
    else:
        # The environment/context-manager exit and stdout serialization occur
        # after this function and should be reported under this step.
        _set_probe_step(current_step, "cleanup-environment")


def _validate_request(app_root: Path, request: object) -> dict[str, str]:
    expected_keys = {
        "database_url", "encryption_master_key", "program_root", "data_root", "cache_root",
    }
    if not isinstance(request, dict) or set(request) != expected_keys:
        raise ValueError("invalid request")
    if not app_root.is_dir():
        raise ValueError("invalid app root")
    if any(not isinstance(request.get(name), str) for name in expected_keys):
        raise ValueError("invalid fixture values")
    dsn = request["database_url"]
    master_key = request["encryption_master_key"]
    program_root = Path(request["program_root"]).resolve(strict=True)
    data_root = Path(request["data_root"]).resolve(strict=True)
    cache_root = Path(request["cache_root"]).resolve(strict=True)
    if len(master_key) < 32 or program_root != app_root.resolve(strict=True):
        raise ValueError("invalid fixture credentials")
    if data_root == program_root or cache_root == program_root:
        raise ValueError("program and writable roots must be isolated")
    try:
        cache_root.relative_to(data_root)
    except ValueError:
        raise ValueError("cache root must be scoped to the test data root") from None
    parsed = urlparse(dsn)
    if parsed.scheme not in {"postgres", "postgresql"} or parsed.hostname not in {"127.0.0.1", "::1", "localhost"}:
        raise ValueError("database must be loopback PostgreSQL")
    return {
        "database_url": dsn,
        "encryption_master_key": master_key,
        "program_root": str(program_root),
        "data_root": str(data_root),
        "cache_root": str(cache_root),
    }


@contextmanager
def _probe_environment(
    app_root: Path, request: dict[str, str], current_step: list[str] | None = None
):
    """Apply fixed test-only portable auth inputs and restore the host process."""
    previous = {name: os.environ.get(name) for name in _PROBE_ENVIRONMENT_KEYS}
    app_path = str(app_root)
    sys.path.insert(0, app_path)
    try:
        os.environ.update({
            "GOOFISH_PORTABLE_MODE": "portable",
            "GOOFISH_PORTABLE_PROGRAM_ROOT": request["program_root"],
            "GOOFISH_PORTABLE_DATA_ROOT": request["data_root"],
            "GOOFISH_PORTABLE_CACHE_ROOT": request["cache_root"],
            "ENCRYPTION_MASTER_KEY": request["encryption_master_key"],
            # Probe imports the normal auth module. Its portable-mode guard requires
            # a non-default signing key just like the real Launcher environment.
            "SECRET_KEY": _PROBE_SECRET_KEY,
        })
        yield
    finally:
        restore_failure = None
        for name, value in previous.items():
            try:
                if value is None:
                    os.environ.pop(name, None)
                else:
                    os.environ[name] = value
            except Exception as exception:
                restore_failure = exception
        try:
            if sys.path and sys.path[0] == app_path:
                sys.path.pop(0)
        except Exception as exception:
            restore_failure = exception
        if restore_failure is not None:
            if current_step is not None:
                _set_probe_step(current_step, "cleanup-environment")
            raise restore_failure


def _run_probe(
    app_root: Path, request: dict[str, str], current_step: list[str]
) -> dict[str, object]:
    with _probe_environment(app_root, request, current_step):
        return _run_probe_with_environment(app_root, request, current_step)


def _run_probe_with_environment(
    app_root: Path, request: dict[str, str], current_step: list[str]
) -> dict[str, object]:
    _set_probe_step(current_step, "import-fastapi")
    from fastapi import HTTPException
    _set_probe_step(current_step, "import-sqlalchemy")
    from sqlalchemy import create_engine, text
    _set_probe_step(current_step, "import-storage-adapter")
    from src.storage.postgres_adapter import ApiConfigRevisionConflict, PostgresAdapter
    _set_probe_step(current_step, "import-settings-manager")
    from src.web import settings_manager

    database_url = request["database_url"]
    _set_probe_step(current_step, "engine-create")
    engine = create_engine(database_url, pool_size=5, max_overflow=4, pool_pre_ping=True)
    _set_probe_step(current_step, "storage-create")
    storage = PostgresAdapter(database_url)
    users = [str(uuid.uuid4()), str(uuid.uuid4())]
    usernames = ["portable_cas_" + uuid.uuid4().hex, "portable_cas_" + uuid.uuid4().hex]
    primary_failure_step = None
    try:
        _set_probe_step(current_step, "db-identity")
        with engine.begin() as connection:
            address, port = connection.execute(text("SELECT host(inet_server_addr()), inet_server_port()")).one()
            _validate_database_identity(address, port, database_url, current_step)
            _set_probe_step(current_step, "seed-users")
            connection.execute(text(
                "INSERT INTO users (id, username, password_hash, role, is_active) "
                "VALUES (:id_a, :name_a, 'synthetic-fixture', 'admin', true), "
                "(:id_b, :name_b, 'synthetic-fixture', 'admin', true)"
            ), {"id_a": users[0], "name_a": usernames[0], "id_b": users[1], "name_b": usernames[1]})

        _set_probe_step(current_step, "config-a-create")
        first = storage.save_user_api_config(users[0], {
            "provider": "openai", "name": "fixture-default", "is_default": True,
            "api_base_url": "https://old.example.invalid/v1", "model": "model-a",
            "api_key": "synthetic-old-key", "extra_config": {
                "ADVANCED_RETRY_COUNT": 5, "AI_MAX_TOKENS_LIMIT": 8192,
                "PROXY_URL": "http://proxy.example.invalid:8080",
            },
        })
        _set_probe_step(current_step, "config-b-create")
        storage.save_user_api_config(users[1], {
            "provider": "openai", "name": "other-user", "is_default": True,
            "api_base_url": "https://user-b.example.invalid/v1", "model": "private-b",
            "api_key": "synthetic-user-b-key", "extra_config": {"PRIVATE": "user-b-only"},
        })

        _set_probe_step(current_step, "delete-recreate")
        before_delete = storage.get_default_api_config_with_revision(users[0])
        assert before_delete["config_revision"] == 1
        assert storage.delete_user_api_config(first["id"], users[0])
        replacement = storage.save_user_api_config(users[0], {
            "provider": "openai", "name": "replacement", "is_default": True,
            "api_base_url": "https://old.example.invalid/v1", "model": "replacement-model",
            "api_key": "synthetic-replacement-key", "extra_config": {"ADVANCED_RETRY_COUNT": 9},
        })
        replacement_revision = storage.get_default_api_config_with_revision(users[0])
        assert replacement_revision["config_revision"] == 1
        assert replacement_revision["config_revision"] == before_delete["config_revision"]
        assert replacement_revision["id"] == replacement["id"]
        stale_recreate_rejected = False
        try:
            storage.update_default_api_config_fields(
                users[0], replacement_revision["config_revision"], before_delete["id"], {"model": "stale"}
            )
        except ApiConfigRevisionConflict:
            stale_recreate_rejected = True
        assert stale_recreate_rejected and replacement["id"] != before_delete["id"]

        _set_probe_step(current_step, "concurrent-cas")
        snapshot = storage.get_default_api_config_with_revision(users[0])
        gate = threading.Barrier(2)

        def writer(model: str) -> str:
            gate.wait(timeout=10)
            try:
                storage.update_default_api_config_fields(
                    users[0], snapshot["config_revision"], snapshot["id"], {"model": model}
                )
                return "committed"
            except ApiConfigRevisionConflict:
                return "conflict"

        with ThreadPoolExecutor(max_workers=2) as pool:
            futures = (pool.submit(writer, "race-one"), pool.submit(writer, "race-two"))
            outcomes = sorted(future.result(timeout=20) for future in futures)
        assert outcomes == ["committed", "conflict"], outcomes

        user = {"id": users[0], "user_id": users[0]}
        _set_probe_step(current_step, "settings-update")
        with mock.patch.object(settings_manager, "STORAGE_BACKEND", return_value="postgres"), \
             mock.patch.object(settings_manager, "portable_mode", return_value=True), \
             mock.patch.object(settings_manager, "get_storage", return_value=storage), \
             mock.patch.object(settings_manager, "invalidate_ai_health_snapshot"):
            current = storage.get_default_api_config_with_revision(users[0])
            asyncio.run(settings_manager.update_ai_settings({
                "config_revision": current["config_revision"], "config_id": current["id"],
                "OPENAI_MODEL_NAME": "configured-model", "OPENAI_API_KEY": "",
                "AI_MAX_TOKENS_LIMIT": 12000,
            }, user=user))
            patched = storage.get_default_api_config_with_revision(users[0])
            assert patched["model"] == "configured-model"
            assert patched["extra_config"]["AI_MAX_TOKENS_LIMIT"] == 12000
            assert patched["extra_config"]["ADVANCED_RETRY_COUNT"] == 9
            assert patched["api_key"] == "synthetic-replacement-key"

            _set_probe_step(current_step, "host-change")
            host_change_rejected = False
            try:
                asyncio.run(settings_manager.update_ai_settings({
                    "config_revision": patched["config_revision"], "config_id": patched["id"],
                    "OPENAI_BASE_URL": "https://new.example.invalid/v1",
                }, user=user))
            except HTTPException as exc:
                assert exc.status_code == 400
                host_change_rejected = True
            assert host_change_rejected

            patched = storage.get_default_api_config_with_revision(users[0])
            asyncio.run(settings_manager.update_ai_settings({
                "config_revision": patched["config_revision"], "config_id": patched["id"],
                "OPENAI_BASE_URL": "https://new.example.invalid/v1",
                "OPENAI_API_KEY": "synthetic-new-host-key",
            }, user=user))

        _set_probe_step(current_step, "user-isolation")
        user_a_configs = storage.get_user_api_configs(users[0])
        user_b_configs = storage.get_user_api_configs(users[1])
        assert len(user_a_configs) == 1 and user_a_configs[0]["api_key"] == "synthetic-new-host-key"
        assert len(user_b_configs) == 1 and user_b_configs[0]["api_key"] == "synthetic-user-b-key"
        assert user_b_configs[0]["extra_config"]["PRIVATE"] == "user-b-only"

        _set_probe_step(current_step, "default-switch")
        previous_default = storage.get_default_api_config_with_revision(users[0])
        secondary = storage.save_user_api_config(users[0], {
            "provider": "openai", "name": "secondary", "is_default": False,
            "api_base_url": "https://secondary.example.invalid/v1", "model": "secondary-model",
            "extra_config": {},
        })
        secondary["is_default"] = True
        storage.save_user_api_config(users[0], secondary)
        with engine.connect() as connection:
            default_count = connection.execute(text(
                "SELECT count(*) FROM user_api_configs WHERE user_id = :user_id AND is_default = true"
            ), {"user_id": users[0]}).scalar_one()
        selected_default = storage.get_default_api_config_with_revision(users[0])
        assert default_count == 1 and selected_default["id"] == secondary["id"]
        old_default_rejected = False
        try:
            storage.update_default_api_config_fields(
                users[0], previous_default["config_revision"], previous_default["id"], {"model": "stale-default"}
            )
        except ApiConfigRevisionConflict:
            old_default_rejected = True
        assert old_default_rejected

        return {
            "simultaneous_writers": outcomes,
            "delete_recreate_same_revision_rejected": stale_recreate_rejected,
            "default_switch_unique_and_stale_identity_rejected": old_default_rejected,
            "advanced_fields_preserved": True,
            "empty_key_preserved": True,
            "host_change_requires_replacement_or_removal": host_change_rejected,
            "new_host_key_saved": True,
            "user_isolation": True,
            "external_actions": "none",
        }
    except Exception:
        primary_failure_step = current_step[0]
        raise
    finally:
        def delete_fixture_users() -> None:
            with engine.begin() as connection:
                connection.execute(
                    text("DELETE FROM users WHERE id IN (:a, :b)"),
                    {"a": users[0], "b": users[1]},
                )

        _cleanup_probe_resources(
            current_step,
            primary_failure_step,
            storage.engine.dispose,
            delete_fixture_users,
            engine.dispose,
        )


def main() -> int:
    current_step = ["arguments"]
    try:
        if len(sys.argv) != 3 or sys.argv[1] != "--app-root":
            raise ValueError("app root is required")
        _set_probe_step(current_step, "request-validate")
        app_root = Path(sys.argv[2]).resolve(strict=True)
        request = json.loads(sys.stdin.buffer.read(16 * 1024 + 1).decode("utf-8"))
        result = _run_probe(app_root, _validate_request(app_root, request), current_step)
        sys.stdout.write(json.dumps(result, ensure_ascii=False, separators=(",", ":")) + "\n")
        return 0
    except Exception as exception:
        # Never echo a DSN, key, exception text, or raw SQL into diagnostics.
        exception_class = _safe_exception_class(exception)
        sys.stderr.write(
            f"CONFIG_PG_CAS_PROBE_FAILURE step={current_step[0]} exception={exception_class}\n"
        )
        sys.stderr.write("portable configuration PostgreSQL probe failed\n")
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
