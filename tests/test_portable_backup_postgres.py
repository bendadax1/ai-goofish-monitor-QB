import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from src.portable.backup_archive import BackupArchiveError
from src.portable.backup_postgres import _tool_environment, dump_owned_database


class PostgresBackupTests(unittest.TestCase):
    def test_child_environment_cannot_inherit_service_or_proxy(self):
        parameters = dict(host="localhost", hostaddr="127.0.0.1", port=12345,
                          dbname="aigoofish", user="admin", password="fixture-secret")
        with patch.dict("os.environ", {"PGSERVICE": "foreign", "HTTP_PROXY": "foreign"}):
            environment = _tool_environment(parameters, Path("F:/fixture"))
        self.assertNotIn("PGSERVICE", environment)
        self.assertNotIn("HTTP_PROXY", environment)
        self.assertEqual(environment["PGHOSTADDR"], "127.0.0.1")
        self.assertEqual(environment["PGPASSWORD"], "fixture-secret")
        self.assertIn("default_transaction_read_only=on", environment["PGOPTIONS"])

    def test_existing_destination_rejected_before_database_access(self):
        parent = Path(__file__).resolve().parents[1] / ".tmp/tests/backup-postgres"
        parent.mkdir(parents=True, exist_ok=True)
        try:
            with tempfile.TemporaryDirectory(dir=parent) as temporary:
                root = Path(temporary)
                target = root / "existing.dump"
                target.write_bytes(b"original")
                with patch("src.portable.backup_postgres.psycopg2.connect") as connect:
                    with self.assertRaises(BackupArchiveError):
                        dump_owned_database(postgres_root=root, pgdata=root, instance_id="invalid",
                            admin_dsn="invalid", destination=target)
                    connect.assert_not_called()
                self.assertEqual(target.read_bytes(), b"original")
        finally:
            parent.rmdir()
