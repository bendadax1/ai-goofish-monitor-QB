"""Bounded encrypted-backup round-trip and hostile framing regressions."""
import io
import os
import struct
import tempfile
from pathlib import Path
import unittest
from collections import namedtuple
from unittest.mock import patch

from src.portable import backup_archive as backup


class PortableBackupArchiveTests(unittest.TestCase):
    def setUp(self):
        self.parent = Path(__file__).resolve().parents[1] / ".tmp/tests/portable-backup"
        self.parent.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=self.parent)
        self.root = Path(self.temporary.name)

    def tearDown(self):
        self.temporary.cleanup()
        self.parent.rmdir()

    def test_roundtrip_wrong_password_tamper_and_nonoverwrite(self):
        source = self.root / "source"
        source.mkdir()
        (source / "db.dump").write_bytes(b"fixture-database-only")
        archive = self.root / "backup.gfbk"
        password = "Test backup password 7!"
        backup.create_backup_archive(source, ["db.dump"], archive, password, {"schema_version":1})
        target = self.root / "restore"
        backup.restore_backup_archive(archive, target, password)
        self.assertEqual((target / "db.dump").read_bytes(), b"fixture-database-only")
        with self.assertRaises(backup.BackupArchiveError):
            backup.restore_backup_archive(archive, target, password)
        with self.assertRaises(backup.BackupArchiveError):
            backup.restore_backup_archive(archive, self.root / "wrong", "wrong password long")
        self.assertFalse((self.root / "wrong").exists())
        damaged = self.root / "damaged.gfbk"
        raw = bytearray(archive.read_bytes())
        raw[-1] ^= 1
        damaged.write_bytes(raw)
        with self.assertRaises(backup.BackupArchiveError):
            backup.restore_backup_archive(damaged, self.root / "bad", password)
        self.assertFalse((self.root / "bad").exists())

    def test_header_size_rejected_before_allocating_or_deriving_key(self):
        archive = self.root / "hostile.gfbk"
        archive.write_bytes(backup.MAGIC + struct.pack(">I", 0xffffffff))
        with patch.object(backup, "_derive_key") as derive:
            with self.assertRaises(backup.BackupArchiveError):
                backup._decrypt_to_zip(archive, "unused-password", io.BytesIO(), 1024)
            derive.assert_not_called()

    def test_reserved_and_anchored_paths_rejected(self):
        for value in ("../x", "/x", "C:x", "file:stream", "NUL", "COM¹.txt", "LPT²", "x?y"):
            with self.assertRaises(backup.BackupArchiveError):
                backup._safe_relative(value)

    def test_reparse_check_fails_closed_when_metadata_cannot_be_read(self):
        with patch.object(Path, "stat", side_effect=OSError("denied")):
            with self.assertRaises(backup.BackupArchiveError):
                backup._is_reparse(self.root)

    def test_changed_source_is_not_hashed_separately_from_zip_payload(self):
        source = self.root / "source"
        source.mkdir()
        payload = source / "db.dump"
        payload.write_bytes(b"stable fixture")
        original_fstat = backup.os.fstat
        descriptor = os.open(payload, os.O_RDONLY)
        try:
            before = original_fstat(descriptor)
        finally:
            os.close(descriptor)
        # `_write_zip_entry` observes the same open descriptor before and
        # after writing.  A changed identity must abort before publication.
        changed = type("Status", (), {
            "st_mode": before.st_mode,
            "st_dev": before.st_dev,
            "st_ino": before.st_ino,
            "st_size": before.st_size,
            "st_mtime_ns": before.st_mtime_ns + 1,
        })()
        with patch.object(backup.os, "fstat", side_effect=[before, changed]):
            with self.assertRaises(backup.BackupArchiveError):
                backup._build_zip(source, ["db.dump"], io.BytesIO())

    def test_highly_repetitive_valid_file_remains_restorable(self):
        source = self.root / "source"
        source.mkdir()
        (source / "logical.dump").write_bytes(b"\0" * (256 * 1024))
        archive = self.root / "compressed-source.gfbk"
        backup.create_backup_archive(source, ["logical.dump"], archive, "Test backup password 7!", {})
        target = self.root / "restored-repetitive"
        backup.restore_backup_archive(archive, target, "Test backup password 7!")
        self.assertEqual((target / "logical.dump").stat().st_size, 256 * 1024)

    def test_create_rejects_an_entry_count_restore_would_reject(self):
        source = self.root / "source"
        source.mkdir()
        (source / "one").write_bytes(b"1")
        (source / "two").write_bytes(b"2")
        archive = self.root / "too-many.gfbk"
        with patch.object(backup, "MAX_ENTRIES", 2):
            with self.assertRaises(backup.BackupArchiveError):
                backup.create_backup_archive(source, ["one", "two"], archive, "Test backup password 7!", {})
        self.assertFalse(archive.exists())

    def test_create_never_publishes_an_archive_over_its_configured_limit(self):
        source = self.root / "source"
        source.mkdir()
        (source / "db.dump").write_bytes(b"fixture")
        archive = self.root / "too-large.gfbk"
        with patch.object(backup, "MAX_ARCHIVE_BYTES", 64):
            with self.assertRaises(backup.BackupArchiveError):
                backup.create_backup_archive(source, ["db.dump"], archive, "Test backup password 7!", {})
        self.assertFalse(archive.exists())

    def test_hash_failure_does_not_publish_target(self):
        source = self.root / "source"
        source.mkdir()
        (source / "db.dump").write_bytes(b"fixture")
        archive = self.root / "hash-failure.gfbk"
        with patch.object(backup, "_sha256_file", side_effect=OSError("injected read failure")):
            with self.assertRaises(backup.BackupArchiveError):
                backup.create_backup_archive(source, ["db.dump"], archive, "Test backup password 7!", {})
        self.assertFalse(archive.exists())

    def test_plaintext_cleanup_failure_prevents_publication(self):
        source = self.root / "source"
        source.mkdir()
        (source / "db.dump").write_bytes(b"fixture")
        archive = self.root / "plain-cleanup-failure.gfbk"
        original_unlink = Path.unlink
        def fail_plain(path, *args, **kwargs):
            if path.name.startswith("plain-"):
                raise OSError("injected sharing error")
            return original_unlink(path, *args, **kwargs)
        with patch.object(Path, "unlink", fail_plain):
            with self.assertRaises(backup.BackupArchiveError):
                backup.create_backup_archive(source, ["db.dump"], archive, "Test backup password 7!", {})
        self.assertFalse(archive.exists())

    def test_committed_target_is_success_even_if_staging_link_cleanup_fails(self):
        source = self.root / "source"
        source.mkdir()
        (source / "db.dump").write_bytes(b"fixture")
        archive = self.root / "link-cleanup.gfbk"
        original_unlink = Path.unlink
        def fail_temporary(path, *args, **kwargs):
            if path.name.startswith("encrypted-"):
                raise OSError("injected sharing error")
            return original_unlink(path, *args, **kwargs)
        with patch.object(Path, "unlink", fail_temporary):
            result = backup.create_backup_archive(source, ["db.dump"], archive, "Test backup password 7!", {})
        self.assertEqual(result.archive_path, archive)
        self.assertTrue(archive.is_file())

    def test_cleanup_failure_does_not_replace_the_restore_failure(self):
        source = self.root / "source"
        source.mkdir()
        (source / "db.dump").write_bytes(b"fixture")
        archive = self.root / "backup.gfbk"
        backup.create_backup_archive(source, ["db.dump"], archive, "Test backup password 7!", {})
        with patch.object(Path, "unlink", side_effect=OSError("locked")):
            with self.assertRaises(backup.BackupArchiveError):
                backup.restore_backup_archive(archive, self.root / "wrong", "wrong password long")

    def test_decrypted_zip_cleanup_failure_prevents_restore_publication(self):
        source = self.root / "source"
        source.mkdir()
        (source / "db.dump").write_bytes(b"fixture")
        archive = self.root / "backup.gfbk"
        backup.create_backup_archive(source, ["db.dump"], archive, "Test backup password 7!", {})
        target = self.root / "restore-cleanup-failure"
        original_unlink = Path.unlink

        def fail_decrypted(path, *args, **kwargs):
            if path.name.startswith("decrypt-"):
                raise OSError("injected sharing error")
            return original_unlink(path, *args, **kwargs)

        with patch.object(Path, "unlink", fail_decrypted):
            with self.assertRaisesRegex(backup.BackupArchiveError, "cleanup failed"):
                backup.restore_backup_archive(archive, target, "Test backup password 7!")
        self.assertFalse(target.exists())

    def test_restore_digest_failure_keeps_destination_absent(self):
        source = self.root / "source"
        source.mkdir()
        (source / "db.dump").write_bytes(b"fixture")
        archive = self.root / "backup.gfbk"
        backup.create_backup_archive(source, ["db.dump"], archive, "Test backup password 7!", {})
        target = self.root / "restore-hash-failure"
        with patch.object(backup, "_sha256_file", side_effect=OSError("injected restore read failure")):
            with self.assertRaises(backup.BackupArchiveError):
                backup.restore_backup_archive(archive, target, "Test backup password 7!")
        self.assertFalse(target.exists())

    def test_low_disk_rejects_before_plaintext_staging(self):
        source = self.root / "source"
        source.mkdir()
        (source / "db.dump").write_bytes(b"fixture")
        usage = namedtuple("Disk", "total used free")(1000 * 1024**3, 951 * 1024**3, 49 * 1024**3)
        with patch.object(backup.shutil, "disk_usage", return_value=usage), patch.object(backup, "_private_work_directory") as staging:
            with self.assertRaisesRegex(backup.BackupArchiveError, "disk free-space"):
                backup.create_backup_archive(source, ["db.dump"], self.root / "low-disk.gfbk", "Test backup password 7!", {})
            staging.assert_not_called()

    def test_destination_volume_is_checked_when_source_is_elsewhere(self):
        source = self.root / "source"
        destination_parent = self.root / "destination"
        source.mkdir()
        destination_parent.mkdir()
        (source / "db.dump").write_bytes(b"fixture")
        with patch.object(backup, "_require_disk_space") as check:
            backup.create_backup_archive(source, ["db.dump"], destination_parent / "backup.gfbk", "Test backup password 7!", {})
        self.assertTrue(check.call_args_list)
        self.assertTrue(all(call.args[0] == destination_parent for call in check.call_args_list))

    def test_growing_source_aborts_before_writing_over_budget(self):
        source = self.root / "source"
        source.mkdir()
        (source / "db.dump").write_bytes(b"123456789")
        import zipfile
        with zipfile.ZipFile(io.BytesIO(), "w") as archive:
            with self.assertRaisesRegex(backup.BackupArchiveError, "configured limit"):
                backup._write_zip_entry(archive, "db.dump", source / "db.dump", 8, None)
