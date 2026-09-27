import base64
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from src.portable.backup_archive import BackupArchiveError
from src.portable.backup_business import create_business_backup


class BusinessBackupTests(unittest.TestCase):
    def setUp(self):
        self.parent = Path(__file__).resolve().parents[1] / ".tmp/tests/backup-business"
        self.parent.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=self.parent)
        self.root = Path(self.temp.name)
        self.data = self.root / "data"
        self.data.mkdir()
        key = base64.b64encode(b"x" * 32).decode()
        self.args = dict(data_root=self.data, postgres_root=self.root, pgdata=self.root,
            instance_id="fixture", app_version="fixture", admin_dsn="not-used",
            recovery_keys={"encryption_master_key": key, "secret_key": key},
            destination=self.root / "backup.gfbk", passphrase="fixture-password-long",
            verify_quiesced=lambda: None)

    def tearDown(self):
        self.temp.cleanup()
        self.parent.rmdir()

    def test_writers_not_stopped_reject_before_database_or_staging(self):
        def not_stopped():
            raise BackupArchiveError("writers active")
        self.args["verify_quiesced"] = not_stopped
        with patch("src.portable.backup_business.dump_owned_database") as dump:
            with self.assertRaisesRegex(BackupArchiveError, "writers active"):
                create_business_backup(**self.args)
            dump.assert_not_called()
        self.assertEqual({p.name for p in self.root.iterdir()}, {"data"})

    def test_unknown_file_scope_rejects_before_database(self):
        (self.data / "unclassified.file").write_bytes(b"do not omit")
        with patch("src.portable.backup_business.dump_owned_database") as dump:
            with self.assertRaisesRegex(BackupArchiveError, "unknown data paths"):
                create_business_backup(**self.args)
            dump.assert_not_called()

    def test_missing_recovery_key_is_not_a_complete_backup(self):
        self.args["recovery_keys"] = {"secret_key": "incomplete"}
        with self.assertRaisesRegex(BackupArchiveError, "incomplete"):
            create_business_backup(**self.args)
        self.assertFalse(self.args["destination"].exists())

    def test_destination_inside_data_cannot_become_its_own_source(self):
        self.args["destination"] = self.data / "backup.gfbk"
        with self.assertRaisesRegex(BackupArchiveError, "outside the data root"):
            create_business_backup(**self.args)

    def test_plaintext_stage_cleanup_failure_blocks_final_publication(self):
        from src.portable.backup_postgres import DatabaseDump
        (self.data / "assets").mkdir()
        (self.data / "assets" / "fixture.txt").write_bytes(b"fixture")
        def fake_dump(*, destination, **_kwargs):
            destination.write_bytes(b"PGDMP-fixture")
            import hashlib
            return DatabaseDump(destination, hashlib.sha256(destination.read_bytes()).hexdigest(),
                destination.stat().st_size, 1, (("users", 0),))
        with patch("src.portable.backup_business.dump_owned_database", side_effect=fake_dump), \
             patch("src.portable.backup_business._clear_private_stage", side_effect=BackupArchiveError("injected cleanup failure")):
            with self.assertRaises(BackupArchiveError):
                create_business_backup(**self.args)
        self.assertFalse(self.args["destination"].exists())
