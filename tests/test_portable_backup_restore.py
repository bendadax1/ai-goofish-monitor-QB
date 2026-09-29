"""Fail-closed checks for an isolated portable business recovery."""
import base64
import hashlib
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from src.portable.backup_archive import BackupArchiveError
from src.portable.backup_restore import _assert_new_business_file, _inspect_dump_toc, _manifest
from src.portable.schema import _application_table_names
from src.portable.schema_catalog import V2_TABLES


class BusinessRestoreTests(unittest.TestCase):
    def setUp(self):
        self.parent = Path(__file__).resolve().parents[1] / ".tmp/tests/backup-restore"
        self.parent.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=self.parent)
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()
        self.parent.rmdir()

    def _payload(self):
        (self.root / "files/assets").mkdir(parents=True)
        (self.root / "files/assets/中文.txt").write_text("fixture", encoding="utf-8")
        (self.root / "database.dump").write_bytes(b"PGDMP-fixture")
        digest = lambda data: hashlib.sha256(data).hexdigest()
        key = base64.b64encode(b"x" * 32).decode("ascii")
        (self.root / "recovery-keys.json").write_text(json.dumps({
            "format_version": 1, "keys": {"encryption_master_key": key, "secret_key": key}}), encoding="utf-8")
        value = {"format_version": 1, "kind": "business-backup", "schema_version": 1,
            "instance_id": "source-id", "app_version": "fixture", "excluded": [],
            "database": {"sha256": digest(b"PGDMP-fixture"), "size": 13,
                "table_counts": {name: 0 for name in (*_application_table_names(), "app_schema_version")}},
            "files": [{"path": "assets/中文.txt", "sha256": digest(b"fixture"), "size": 7}],
            "restore": {"postgres_major": 17, "database": "aigoofish",
                "new_instance_required": True, "fresh_database_passwords_required": True,
                "dpapi_reprotection_required": True, "app_role": "aigoofish_app",
                "probe_role": "aigoofish_probe", "grant_policy": "portable-schema-v1"}}
        (self.root / "backup.json").write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
        return value

    def test_manifest_accepts_exact_files_and_rejects_unknown_table(self):
        value = self._payload()
        names = ("database.dump", "backup.json", "recovery-keys.json", "files/assets/中文.txt")
        self.assertEqual(_manifest(self.root, names, {"schema_version": 1, "instance_id": "source-id"}), value)
        value["database"]["table_counts"]["injected"] = 0
        (self.root / "backup.json").write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
        with self.assertRaisesRegex(BackupArchiveError, "manifest"):
            _manifest(self.root, names, {"schema_version": 1, "instance_id": "source-id"})

    def test_database_only_manifest_has_zero_business_files(self):
        value = self._payload()
        value["files"] = []
        (self.root / "backup.json").write_text(json.dumps(value), encoding="utf-8")
        names = ("database.dump", "backup.json", "recovery-keys.json")
        result = _manifest(self.root, names, {"schema_version": 1, "instance_id": "source-id"})
        self.assertEqual(len(result["files"]), 0)

    def test_v2_manifest_requires_matching_version_policy_and_table_set(self):
        value = self._payload()
        value["schema_version"] = 2
        value["database"]["table_counts"] = {name: 0 for name in V2_TABLES}
        value["restore"]["grant_policy"] = "portable-schema-v2"
        names = ("database.dump", "backup.json", "recovery-keys.json", "files/assets/中文.txt")
        manifest = self.root / "backup.json"
        manifest.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
        self.assertEqual(_manifest(self.root, names,
            {"schema_version": 2, "instance_id": "source-id"}), value)
        with self.assertRaisesRegex(BackupArchiveError, "manifest"):
            _manifest(self.root, names, {"schema_version": 1, "instance_id": "source-id"})
        value["database"]["table_counts"].pop("price_observations")
        manifest.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
        with self.assertRaisesRegex(BackupArchiveError, "manifest"):
            _manifest(self.root, names, {"schema_version": 2, "instance_id": "source-id"})

    def test_manifest_rejects_path_and_hash_tampering(self):
        value = self._payload()
        names = ("database.dump", "backup.json", "recovery-keys.json", "files/assets/中文.txt")
        value["files"][0]["path"] = "../outside"
        (self.root / "backup.json").write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
        with self.assertRaises(BackupArchiveError):
            _manifest(self.root, names, {"schema_version": 1, "instance_id": "source-id"})

    def test_toc_rejects_executable_or_unknown_objects(self):
        all_tables = "".join(f"{index}; 1259 1 TABLE public {table} owner\n"
            for index, table in enumerate((*_application_table_names(), "app_schema_version"), 1))
        for hostile in ("99; 1255 1 FUNCTION public run_me() owner\n",
                        "99; 1259 1 TABLE public unknown owner\n",
                        "99; 1259 1 TABLE private users owner\n"):
            class Result:
                returncode = 0
            def fake_run(_args, **kwargs):
                kwargs["stdout"].write((all_tables + hostile).encode("utf-8"))
                return Result()
            with patch("src.portable.backup_restore.subprocess.run", side_effect=fake_run):
                with self.assertRaises(BackupArchiveError):
                    _inspect_dump_toc(self.root / "pg_restore.exe", self.root / "database.dump", self.root)

    def test_toc_accepts_v1_public_index_but_rejects_unknown_index(self):
        tables = "".join(f"{number}; 1259 1 TABLE public {table} owner\n"
            for number, table in enumerate((*_application_table_names(), "app_schema_version"), 1))

        class Result:
            returncode = 0

        def check_index(name):
            def fake_run(_args, **kwargs):
                kwargs["stdout"].write((tables + f"99; 1259 1 INDEX public {name} owner\n").encode("utf-8"))
                return Result()
            with patch("src.portable.backup_restore.subprocess.run", side_effect=fake_run):
                _inspect_dump_toc(self.root / "pg_restore.exe", self.root / "database.dump", self.root)

        check_index("ix_public_users_username")
        with self.assertRaisesRegex(BackupArchiveError, "unknown index"):
            check_index("ix_public_unknown_index")

    def test_v2_toc_accepts_new_index_only_for_v2(self):
        tables = "".join(f"{number}; 1259 1 TABLE public {table} owner\n"
            for number, table in enumerate(sorted(V2_TABLES), 1))

        class Result:
            returncode = 0

        def fake_run(_args, **kwargs):
            kwargs["stdout"].write((tables +
                "99; 1259 1 INDEX public idx_price_owner_item_observed owner\n").encode("utf-8"))
            return Result()

        with patch("src.portable.backup_restore.subprocess.run", side_effect=fake_run):
            _inspect_dump_toc(self.root / "pg_restore.exe", self.root / "database.dump",
                              self.root, schema_version=2)
            with self.assertRaises(BackupArchiveError):
                _inspect_dump_toc(self.root / "pg_restore.exe", self.root / "database.dump",
                                  self.root, schema_version=1)

    def test_existing_business_file_is_never_replaced(self):
        target = self.root / "target"
        (target / "assets").mkdir(parents=True)
        existing = target / "assets" / "fixture.txt"
        existing.write_text("retain", encoding="utf-8")
        with self.assertRaisesRegex(BackupArchiveError, "already exists"):
            _assert_new_business_file(existing, target)
        self.assertEqual(existing.read_text(encoding="utf-8"), "retain")


if __name__ == "__main__":
    unittest.main()
