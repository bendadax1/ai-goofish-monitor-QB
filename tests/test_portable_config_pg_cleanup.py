"""Database-free diagnostic tests for PostgreSQL CAS probe cleanup."""

import importlib.util
import os
from pathlib import Path
import sys
import unittest
from unittest.mock import Mock, patch

from tests._ci_guard import needs_postgres


def _load_probe_module():
    probe_path = Path(__file__).resolve(strict=True).with_name("portable_config_pg_case.py")
    spec = importlib.util.spec_from_file_location("portable_config_pg_case_cleanup_test", probe_path)
    if spec is None or spec.loader is None:
        raise RuntimeError("could not load portable configuration probe")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


probe = _load_probe_module()


@needs_postgres
class PortableConfigProbeCleanupTests(unittest.TestCase):
    def test_database_identity_query_normalizes_inet_without_relaxing_loopback_check(self):
        probe_source = Path(probe.__file__).read_text(encoding="utf-8")
        self.assertIn(
            'text("SELECT host(inet_server_addr()), inet_server_port()")',
            probe_source,
        )
        self.assertNotIn("inet_server_addr()::text", probe_source)

        current_step = ["db-identity"]
        with self.assertRaises(RuntimeError):
            probe._validate_database_identity(
                "192.0.2.1", 5432,
                "postgresql://synthetic@127.0.0.1:5432/synthetic", current_step
            )
        self.assertEqual(current_step[0], "db-identity-address")

    def test_database_identity_checks_report_only_fixed_address_or_port_step(self):
        cases = (
            ("192.0.2.1", 5432, "db-identity-address"),
            ("127.0.0.1", 5433, "db-identity-port"),
        )
        for address, port, expected_step in cases:
            with self.subTest(step=expected_step):
                current_step = ["db-identity"]
                with self.assertRaises(RuntimeError) as raised:
                    probe._validate_database_identity(
                        address, port, "postgresql://synthetic@127.0.0.1:5432/synthetic", current_step
                    )
                self.assertEqual(current_step[0], expected_step)
                self.assertIn(str(raised.exception), {
                    "database address identity check failed",
                    "database port identity check failed",
                })

    def test_database_identity_accepts_only_requested_loopback_endpoint(self):
        current_step = ["db-identity"]
        probe._validate_database_identity(
            "::1", 55432, "postgresql://synthetic@[::1]:55432/synthetic", current_step
        )
        self.assertEqual(current_step[0], "db-identity-port")

    def test_success_reports_environment_cleanup_context(self):
        current_step = ["default-switch"]
        dispose_storage = Mock()
        delete_users = Mock()
        dispose_engine = Mock()

        probe._cleanup_probe_resources(
            current_step, None, dispose_storage, delete_users, dispose_engine
        )

        self.assertEqual(current_step[0], "cleanup-environment")
        dispose_storage.assert_called_once_with()
        delete_users.assert_called_once_with()
        dispose_engine.assert_called_once_with()

    def test_business_failure_step_survives_successful_cleanup(self):
        current_step = ["default-switch"]
        calls = []
        primary_failure_step = None

        with self.assertRaisesRegex(RuntimeError, "synthetic business failure"):
            try:
                probe._set_probe_step(current_step, "config-a-create")
                raise RuntimeError("synthetic business failure")
            except Exception:
                primary_failure_step = current_step[0]
                raise
            finally:
                probe._cleanup_probe_resources(
                    current_step,
                    primary_failure_step,
                    lambda: calls.append("storage"),
                    lambda: calls.append("delete"),
                    lambda: calls.append("engine"),
                )

        self.assertEqual(current_step[0], "config-a-create")
        self.assertEqual(calls, ["storage", "delete", "engine"])

    def test_cleanup_failure_reports_its_exact_step(self):
        current_step = ["config-a-create"]
        calls = []

        def delete_users():
            calls.append("delete")
            raise RuntimeError("synthetic cleanup failure")

        with self.assertRaisesRegex(RuntimeError, "synthetic cleanup failure"):
            probe._cleanup_probe_resources(
                current_step,
                "config-a-create",
                lambda: calls.append("storage"),
                delete_users,
                lambda: calls.append("engine"),
            )

        self.assertEqual(current_step[0], "cleanup-delete-users")
        self.assertEqual(calls, ["storage", "delete", "engine"])

    def test_environment_restore_failure_reports_environment_cleanup(self):
        class FailOnceOnEnvironmentRestore(dict):
            armed = False

            def __setitem__(self, key, value):
                if self.armed and key == "GOOFISH_PORTABLE_MODE":
                    self.armed = False
                    raise RuntimeError("synthetic environment restore failure")
                super().__setitem__(key, value)

            def pop(self, key, *default):
                if self.armed and key == "GOOFISH_PORTABLE_MODE":
                    self.armed = False
                    raise RuntimeError("synthetic environment restore failure")
                return super().pop(key, *default)

        original_environment = dict(os.environ)
        environment = FailOnceOnEnvironmentRestore(dict(os.environ))
        current_step = ["config-a-create"]
        original_path = list(sys.path)
        request = {
            "program_root": str(Path(__file__).resolve(strict=True).parents[1]),
            "data_root": "synthetic-data-root",
            "cache_root": "synthetic-data-root/cache",
            "encryption_master_key": "synthetic-probe-encryption-key-2026",
        }

        with patch.object(probe.os, "environ", environment):
            with self.assertRaisesRegex(RuntimeError, "synthetic environment restore failure"):
                with probe._probe_environment(
                    Path(request["program_root"]), request, current_step
                ):
                    environment.armed = True
                    raise ValueError("synthetic business failure")

        self.assertEqual(current_step[0], "cleanup-environment")
        self.assertEqual(sys.path, original_path)
        self.assertEqual(dict(os.environ), original_environment)


if __name__ == "__main__":
    unittest.main()
