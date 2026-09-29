import io
import json
import logging
import os
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stderr
from datetime import datetime, timezone
from pathlib import Path
from unittest import mock

_REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
if str(_REPOSITORY_ROOT) not in sys.path:
    # 允许不信任 cwd/PYTHONPATH 的内置 Python 直接运行本验证文件。
    sys.path.insert(0, str(_REPOSITORY_ROOT))

from fastapi.testclient import TestClient
from src.version import VERSION

from src.portable.maintenance import (
    DATABASE_ENVIRONMENT_VARIABLE,
    TOKEN_ENVIRONMENT_VARIABLE,
    DatabaseProbeResult,
    MaintenanceConfigurationError,
    MaintenanceServerError,
    PostgresReadinessProbe,
    build_settings,
    create_app,
    run,
    validate_database_target,
)


_TOKEN = "0123456789abcdef0123456789abcdef"
_DSN = "postgresql://portable:database-secret@127.0.0.1:55432/goofish"
_AUTHORIZATION = {"Authorization": f"Bearer {_TOKEN}"}
_TEST_TEMP_PARENT = _REPOSITORY_ROOT / ".tmp" / "tests" / "portable-maintenance"
logger = logging.getLogger(__name__)


def _environment(**overrides):
    environment = {
        TOKEN_ENVIRONMENT_VARIABLE: _TOKEN,
        DATABASE_ENVIRONMENT_VARIABLE: _DSN,
    }
    environment.update(overrides)
    return environment


def _settings(**overrides):
    arguments = {
        "mode": "maintenance",
        "instance_id": "portable-test-instance",
        "program_root": str(_REPOSITORY_ROOT),
        "data_root": str(_REPOSITORY_ROOT / ".tmp" / "tests" / "portable-maintenance"),
        "port": 8765,
        "environ": _environment(),
    }
    arguments.update(overrides)
    return build_settings(**arguments)


class _StaticProbe:
    def __init__(self, result):
        self.result = result
        self.calls = 0

    def check(self):
        self.calls += 1
        if isinstance(self.result, BaseException):
            raise self.result
        return self.result


class _FakeCursor:
    def __init__(self, *, table_row=("app_schema_version",), version_rows=((1,),)):
        self.table_row = table_row
        self.version_rows = list(version_rows)
        self.executions = []
        self.closed = False

    def execute(self, query, parameters=None):
        self.executions.append((query, parameters))

    def fetchone(self):
        return self.table_row

    def fetchall(self):
        return self.version_rows

    def close(self):
        self.closed = True


class _FakeConnection:
    def __init__(self, cursor):
        self._cursor = cursor
        self.session_arguments = None
        self.rolled_back = False
        self.closed = False

    def set_session(self, **kwargs):
        self.session_arguments = kwargs

    def cursor(self):
        return self._cursor

    def rollback(self):
        self.rolled_back = True

    def close(self):
        self.closed = True


class PortableMaintenanceImportTests(unittest.TestCase):
    def test_import_does_not_load_business_modules_or_dotenv(self):
        marker = "PORTABLE_MAINTENANCE_DOTENV_MARKER"
        script = """
import json
import os
import sys
from pathlib import Path

repository_root = Path(sys.argv[1])
working_directory = Path(sys.argv[2])
working_directory.mkdir(parents=True, exist_ok=True)
(working_directory / '.env').write_text(
    'PORTABLE_MAINTENANCE_DOTENV_MARKER=must_not_load\\n',
    encoding='utf-8',
)
sys.path.insert(0, str(repository_root))
os.chdir(working_directory)
before = dict(os.environ)
import portable_server
forbidden = sorted(
    name for name in sys.modules
    if name == 'src.config'
    or name == 'src.web'
    or name.startswith('src.web.')
    or name == 'src.storage'
    or name.startswith('src.storage.')
)
print(json.dumps({
    'forbidden': forbidden,
    'environment_changed': dict(os.environ) != before,
    'marker_loaded': os.environ.get('PORTABLE_MAINTENANCE_DOTENV_MARKER'),
}))
"""
        created_parents = []
        try:
            for directory in (
                _REPOSITORY_ROOT / ".tmp",
                _REPOSITORY_ROOT / ".tmp" / "tests",
                _TEST_TEMP_PARENT,
            ):
                if not directory.exists():
                    directory.mkdir()
                    created_parents.append(directory)
            with tempfile.TemporaryDirectory(dir=_TEST_TEMP_PARENT) as temporary_directory:
                temporary_root = Path(temporary_directory)
                environment = dict(os.environ)
                environment.pop(marker, None)
                completed = subprocess.run(
                    [sys.executable, "-B", "-c", script, str(_REPOSITORY_ROOT), str(temporary_root)],
                    cwd=_REPOSITORY_ROOT,
                    env=environment,
                    capture_output=True,
                    text=True,
                    encoding="utf-8",
                    check=True,
                    timeout=10,
                )
                result = json.loads(completed.stdout)
                self.assertEqual(result["forbidden"], [])
                self.assertFalse(result["environment_changed"])
                self.assertIsNone(result["marker_loaded"])
        finally:
            for directory in reversed(created_parents):
                try:
                    directory.rmdir()
                except OSError as exc:
                    logger.warning("Could not remove test temporary parent %s: %s", directory, exc)


class PortableMaintenanceSettingsTests(unittest.TestCase):
    def test_missing_or_short_token_fails_without_exposing_database_secret(self):
        for token in ("", "short"):
            with self.subTest(token=token):
                environment = _environment(**{TOKEN_ENVIRONMENT_VARIABLE: token})
                with self.assertRaises(MaintenanceConfigurationError) as context:
                    _settings(environ=environment)
                self.assertNotIn("database-secret", str(context.exception))

    def test_mode_port_instance_and_roots_are_validated(self):
        invalid_cases = (
            {"mode": "normal"},
            {"port": 0},
            {"port": 65_536},
            {"instance_id": "contains space"},
            {"program_root": "relative/program"},
            {"data_root": "relative/data"},
        )
        for overrides in invalid_cases:
            with self.subTest(overrides=overrides):
                with self.assertRaises(MaintenanceConfigurationError):
                    _settings(**overrides)

    def test_cli_rejects_normal_mode_explicitly(self):
        error_output = io.StringIO()
        with redirect_stderr(error_output):
            with self.assertRaises(SystemExit) as context:
                run(["--mode", "normal"], environ={})
        self.assertEqual(context.exception.code, 2)
        self.assertIn("invalid choice", error_output.getvalue())
        self.assertIn("maintenance", error_output.getvalue())

    def test_database_target_must_be_fully_explicit_and_loopback(self):
        invalid_dsns = (
            "postgresql://portable:secret@example.com:5432/goofish",
            "host=localhost hostaddr=203.0.113.2 port=5432 dbname=goofish user=portable password=secret",
            "host=localhost port=5432 dbname=goofish user=portable",
            "service=shared-production",
            "host=localhost,127.0.0.1 port=5432 dbname=goofish user=portable password=secret",
        )
        for dsn in invalid_dsns:
            with self.subTest(dsn=dsn):
                with self.assertRaises(MaintenanceConfigurationError) as context:
                    validate_database_target(dsn, environ={})
                self.assertNotIn("secret", str(context.exception))

        with self.assertRaisesRegex(MaintenanceConfigurationError, "service configuration"):
            validate_database_target(_DSN, environ={"PGSERVICE": "implicit"})

    def test_valid_target_repr_does_not_expose_credentials(self):
        target = validate_database_target(_DSN, environ={})
        self.assertNotIn("database-secret", repr(target))
        settings = _settings()
        self.assertNotIn(_TOKEN, repr(settings))
        self.assertNotIn("database-secret", repr(settings))

    def test_path_resolution_error_is_fixed_and_sanitized(self):
        secret = "sensitive-path-from-os-error"
        with mock.patch("src.portable.maintenance.Path.resolve", side_effect=OSError(secret)):
            with self.assertLogs("src.portable.maintenance", level="WARNING") as captured:
                with self.assertRaises(MaintenanceConfigurationError) as context:
                    _settings()
        serialized = str(context.exception) + "\n" + "\n".join(captured.output)
        self.assertNotIn(secret, serialized)
        self.assertIn("could not be resolved", str(context.exception))

    def test_invalid_path_value_is_fixed_and_sanitized(self):
        with self.assertLogs("src.portable.maintenance", level="WARNING") as captured:
            with self.assertRaises(MaintenanceConfigurationError) as context:
                _settings(program_root="F:\\invalid\x00path")
        serialized = str(context.exception) + "\n" + "\n".join(captured.output)
        self.assertNotIn("invalid\x00path", serialized)
        self.assertEqual(str(context.exception), "program-root is invalid")

    def test_cli_runner_binds_only_loopback_and_receives_no_secrets_as_arguments(self):
        captured = {}

        def server_runner(app, **kwargs):
            captured["app"] = app
            captured.update(kwargs)

        arguments = [
            "--mode",
            "maintenance",
            "--instance-id",
            "portable-test-instance",
            "--program-root",
            str(_REPOSITORY_ROOT),
            "--data-root",
            str(_REPOSITORY_ROOT / ".tmp" / "data"),
            "--port",
            "8765",
        ]
        self.assertNotIn(_TOKEN, arguments)
        self.assertNotIn(_DSN, arguments)
        self.assertEqual(run(arguments, environ=_environment(), server_runner=server_runner), 0)
        self.assertEqual(captured["host"], "127.0.0.1")
        self.assertEqual(captured["port"], 8765)

    def test_server_start_error_is_fixed_and_sanitized(self):
        secret = "socket-error-with-sensitive-command-context"

        def server_runner(app, **kwargs):
            raise OSError(secret)

        arguments = [
            "--mode",
            "maintenance",
            "--instance-id",
            "portable-test-instance",
            "--program-root",
            str(_REPOSITORY_ROOT),
            "--data-root",
            str(_REPOSITORY_ROOT / ".tmp" / "data"),
        ]
        with self.assertLogs("src.portable.maintenance", level="ERROR") as captured:
            with self.assertRaises(MaintenanceServerError) as context:
                run(arguments, environ=_environment(), server_runner=server_runner)
        serialized = str(context.exception) + "\n" + "\n".join(captured.output)
        self.assertNotIn(secret, serialized)
        self.assertEqual(str(context.exception), "portable maintenance server failed to start")


class PortableMaintenanceRouteTests(unittest.TestCase):
    def test_public_health_contains_only_liveness(self):
        probe = _StaticProbe(DatabaseProbeResult("available", "compatible", 1, None))
        with TestClient(create_app(_settings(), probe=probe)) as client:
            response = client.get("/health")
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.json(), {"status": "alive"})
        self.assertEqual(probe.calls, 0)

    def test_unauthorized_request_does_not_call_database_probe(self):
        probe = _StaticProbe(DatabaseProbeResult("available", "compatible", 1, None))
        with TestClient(create_app(_settings(), probe=probe)) as client:
            missing = client.get("/internal/ready")
            incorrect = client.get("/internal/ready", headers={"Authorization": "Bearer wrong"})
        self.assertEqual(missing.status_code, 401)
        self.assertEqual(incorrect.status_code, 401)
        self.assertEqual(probe.calls, 0)

    def test_authenticated_compatible_response_has_protocol_and_identity(self):
        probe = _StaticProbe(DatabaseProbeResult("available", "compatible", 1, None))
        clock = lambda: datetime(2026, 9, 12, 1, 2, 3, tzinfo=timezone.utc)
        with TestClient(create_app(_settings(), probe=probe, clock=clock)) as client:
            response = client.get("/internal/ready", headers=_AUTHORIZATION)

        self.assertEqual(response.status_code, 200)
        self.assertEqual(
            response.json(),
            {
                "protocol_version": 1,
                "instance_id": "portable-test-instance",
                "app_version": VERSION,
                "observed_at": "2026-09-12T01:02:03Z",
                "mode": "maintenance",
                "ready": True,
                "database": {"status": "available"},
                "schema": {
                    "status": "compatible",
                    "version": 1,
                    "supported_min": 1,
                    "supported_max": 2,
                },
                "failure_reason": None,
            },
        )
        self.assertEqual(probe.calls, 1)

    def test_authenticated_not_ready_states_are_fixed_and_return_503(self):
        cases = (
            (
                DatabaseProbeResult("unavailable", "unknown", None, "database_unavailable"),
                "database_unavailable",
            ),
            (
                DatabaseProbeResult("available", "uninitialized", None, "schema_uninitialized"),
                "schema_uninitialized",
            ),
            (
                DatabaseProbeResult("available", "incompatible", 2, "schema_incompatible"),
                "schema_incompatible",
            ),
            (
                DatabaseProbeResult("available", "invalid", None, "schema_invalid"),
                "schema_invalid",
            ),
        )
        for result, expected_reason in cases:
            with self.subTest(expected_reason=expected_reason):
                with TestClient(create_app(_settings(), probe=_StaticProbe(result))) as client:
                    response = client.get("/internal/ready", headers=_AUTHORIZATION)
                self.assertEqual(response.status_code, 503)
                self.assertFalse(response.json()["ready"])
                self.assertEqual(response.json()["failure_reason"], expected_reason)

    def test_probe_exception_is_sanitized_in_response_and_log(self):
        secret = "postgresql://user:password-never-disclose@server/database"
        probe = _StaticProbe(RuntimeError(secret))
        with self.assertLogs("src.portable.maintenance", level="WARNING") as captured:
            with TestClient(create_app(_settings(), probe=probe)) as client:
                response = client.get("/internal/ready", headers=_AUTHORIZATION)
        serialized = response.text + "\n" + "\n".join(captured.output)
        self.assertEqual(response.status_code, 503)
        self.assertEqual(response.json()["failure_reason"], "database_unavailable")
        self.assertNotIn("password-never-disclose", serialized)
        self.assertNotIn("postgresql://", serialized)

    def test_only_expected_routes_are_exposed(self):
        probe = _StaticProbe(DatabaseProbeResult("available", "compatible", 1, None))
        with TestClient(create_app(_settings(), probe=probe)) as client:
            self.assertEqual(client.get("/docs").status_code, 404)
            self.assertEqual(client.get("/openapi.json").status_code, 404)
            self.assertEqual(client.post("/internal/ready", headers=_AUTHORIZATION).status_code, 405)


class PostgresReadinessProbeTests(unittest.TestCase):
    def _run_probe(self, *, table_row=("app_schema_version",), version_rows=((1,),)):
        cursor = _FakeCursor(table_row=table_row, version_rows=version_rows)
        connection = _FakeConnection(cursor)
        connector_arguments = {}

        def connector(**kwargs):
            connector_arguments.update(kwargs)
            return connection

        target = validate_database_target(_DSN, environ={})
        result = PostgresReadinessProbe(target, connector=connector).check()
        return result, cursor, connection, connector_arguments

    def test_probe_uses_readonly_transaction_and_bounded_timeouts(self):
        result, cursor, connection, arguments = self._run_probe()
        self.assertTrue(result.ready)
        self.assertEqual(connection.session_arguments, {"readonly": True, "autocommit": False})
        self.assertEqual(arguments["connect_timeout"], 2)
        self.assertIn("statement_timeout=2000", arguments["options"])
        self.assertIn("default_transaction_read_only=on", arguments["options"])
        self.assertEqual(arguments["host"], "127.0.0.1")
        self.assertEqual(arguments["hostaddr"], "127.0.0.1")
        self.assertEqual(arguments["sslmode"], "disable")
        self.assertTrue(connection.rolled_back)
        self.assertTrue(connection.closed)
        self.assertTrue(cursor.closed)
        self.assertEqual(
            cursor.executions,
            [
                ("SELECT to_regclass(%s)", ("public.app_schema_version",)),
                ("SELECT version FROM public.app_schema_version LIMIT 2", None),
            ],
        )

    def test_schema_table_missing_or_empty_is_uninitialized(self):
        for table_row, rows in ((None, ()), ((None,), ()), (("app_schema_version",), ())):
            with self.subTest(table_row=table_row, rows=rows):
                result, _, _, _ = self._run_probe(table_row=table_row, version_rows=rows)
                self.assertEqual(result.schema_status, "uninitialized")
                self.assertEqual(result.failure_reason, "schema_uninitialized")
                self.assertFalse(result.ready)

    def test_schema_requires_exactly_one_positive_integer_version(self):
        invalid_rows = (
            ((1,), (1,)),
            (("1",),),
            ((0,),),
            ((True,),),
            ((1, "extra"),),
        )
        for rows in invalid_rows:
            with self.subTest(rows=rows):
                result, _, _, _ = self._run_probe(version_rows=rows)
                self.assertEqual(result.schema_status, "invalid")
                self.assertEqual(result.failure_reason, "schema_invalid")
                self.assertFalse(result.ready)

    def test_schema_version_outside_supported_range_is_incompatible(self):
        result, _, _, _ = self._run_probe(version_rows=((3,),))
        self.assertEqual(result.schema_status, "incompatible")
        self.assertEqual(result.schema_version, 3)
        self.assertEqual(result.failure_reason, "schema_incompatible")

    def test_connection_error_is_sanitized_and_does_not_escape(self):
        secret = "database-secret-from-driver"

        def connector(**kwargs):
            raise RuntimeError(secret)

        target = validate_database_target(_DSN, environ={})
        with self.assertLogs("src.portable.maintenance", level="WARNING") as captured:
            result = PostgresReadinessProbe(target, connector=connector).check()
        self.assertEqual(result.failure_reason, "database_unavailable")
        self.assertNotIn(secret, "\n".join(captured.output))

    def test_service_environment_added_after_validation_prevents_connection(self):
        connector_calls = []

        def connector(**kwargs):
            connector_calls.append(kwargs)
            raise AssertionError("connector must not be called")

        target = validate_database_target(_DSN, environ={})
        with mock.patch.dict(os.environ, {"PGSERVICE": "unexpected"}, clear=False):
            with self.assertLogs("src.portable.maintenance", level="WARNING"):
                result = PostgresReadinessProbe(target, connector=connector).check()
        self.assertEqual(connector_calls, [])
        self.assertEqual(result.failure_reason, "database_unavailable")


if __name__ == "__main__":
    unittest.main()
