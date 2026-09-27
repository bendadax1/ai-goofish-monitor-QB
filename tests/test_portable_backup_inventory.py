"""Read-only backup scope tests using synthetic data only."""
import os
import json
import uuid
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from src.portable.backup_archive import BackupArchiveError
from src.portable.backup_inventory import inventory_business_files


class BackupInventoryTests(unittest.TestCase):
    def setUp(self):
        self.parent = Path(__file__).resolve().parents[1] / ".tmp/tests/backup-inventory"
        self.parent.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=self.parent)
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()
        self.parent.rmdir()

    def put(self, relative, content=b"fixture"):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
        return path

    def test_scope_keeps_business_state_but_never_descends_into_pgdata(self):
        for path in ("assets/中文.txt", "state/user_files/u/prompt.txt", "results/jsonl/r.jsonl", "config/app.env",
                     "postgres/cluster/PG_VERSION", "cache/temp/a", "logs/log.txt", "config/instance-secrets.dpapi",
                     "config/postgres-runtime.json", ".launcher.instance.lock"):
            self.put(path)
        original = os.scandir
        def guarded(path):
            if "postgres" in Path(path).parts:
                raise AssertionError("PGDATA was traversed")
            return original(path)
        with patch("src.portable.backup_inventory.os.scandir", side_effect=guarded):
            result = inventory_business_files(self.root)
        self.assertTrue(result.file_scope_ready)
        self.assertEqual(len(result.files), 4)
        self.assertEqual(result.total_bytes, 4 * len(b"fixture"))
        self.assertIn("protected-business-key-export", result.required_separate_payloads)

    def test_unknown_config_and_root_block_instead_of_silent_omission(self):
        self.put("unknown/new.txt")
        self.put("config/custom.env")
        result = inventory_business_files(self.root)
        self.assertFalse(result.file_scope_ready)
        self.assertEqual(result.unknown, ("config/custom.env", "unknown"))

    def test_cancelled_restore_candidates_are_not_old_instance_business_data(self):
        name = "restore-" + uuid.uuid4().hex
        metadata = {"format_version": 1, "instance_id": str(uuid.uuid4()),
                    "relative_root": f"instances\\{name}", "postgres_port": 55432,
                    "web_port": 58000, "state": "Validated"}
        self.put(f"instances/{name}/launcher/restore-target.json", json.dumps(metadata).encode("utf-8"))
        self.put(f"instances/{name}/postgres/cluster/PG_VERSION")
        self.put("assets/original.txt")
        original = os.scandir
        def guarded(path):
            if Path(path).name == name:
                raise AssertionError("candidate data must not be traversed")
            return original(path)
        with patch("src.portable.backup_inventory.os.scandir", side_effect=guarded):
            inventory = inventory_business_files(self.root)
        self.assertTrue(inventory.file_scope_ready)
        self.assertEqual([file.path for file in inventory.files], ["assets/original.txt"])
        self.assertEqual(inventory.excluded[0][0], "instances")

    def test_foreign_or_incomplete_instances_directory_still_blocks_backup(self):
        self.put("instances/user-data.txt")
        self.assertEqual(inventory_business_files(self.root).unknown, ("instances",))

    def test_candidate_metadata_with_wrong_relative_root_is_not_omitted(self):
        name = "restore-" + uuid.uuid4().hex
        self.put(f"instances/{name}/launcher/restore-target.json", json.dumps({
            "format_version": 1, "instance_id": str(uuid.uuid4()), "relative_root": "../outside",
            "postgres_port": 55432, "web_port": 58000, "state": "Preparing",
        }).encode("utf-8"))
        self.assertEqual(inventory_business_files(self.root).unknown, ("instances",))

    def test_unreadable_subdirectory_is_not_treated_as_empty(self):
        self.put("state/fixture.txt")
        original = os.scandir
        def denied(path):
            if Path(path).name == "state":
                raise PermissionError("fixture")
            return original(path)
        with patch("src.portable.backup_inventory.os.scandir", side_effect=denied):
            with self.assertRaises(BackupArchiveError):
                inventory_business_files(self.root)

    def test_entry_limit_is_bounded(self):
        self.put("state/one")
        self.put("state/two")
        with self.assertRaises(BackupArchiveError):
            inventory_business_files(self.root, max_entries=2)

    def test_inventory_does_not_read_content_or_write_files(self):
        file = self.put("assets/fixture")
        before = file.stat()
        with patch.object(Path, "open", side_effect=AssertionError("content IO")):
            result = inventory_business_files(self.root)
        self.assertEqual(result.files[0].size, before.st_size)
        self.assertEqual(file.stat().st_mtime_ns, before.st_mtime_ns)

    def test_reparse_attribute_is_rejected_without_traversal(self):
        original = Path.lstat
        self.put("state/file")
        def fake(path):
            value = original(path)
            if path.name == "state":
                return type("Stat", (), {"st_mode": value.st_mode, "st_file_attributes": 0x400})()
            return value
        with patch.object(Path, "lstat", fake):
            with self.assertRaises(BackupArchiveError):
                inventory_business_files(self.root)
