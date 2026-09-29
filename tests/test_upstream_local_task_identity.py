"""合成 config.json 的显式稳定任务身份升级，不读取真实配置。"""

import json
import hashlib
import os
import subprocess
import sys
import asyncio
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import tempfile
import unittest
import uuid
from unittest.mock import patch

from fastapi import HTTPException
from src.storage.local_adapter import LocalStorageAdapter
from src.task import Task, add_task, update_task
from src.web.task_manager import _load_local_tasks, _save_local_tasks

from src.storage.upstream_local import (
    LocalTaskUpgradeError, LocalUpstreamStateError, LocalUpstreamStore, upgrade_local_task_ids,
    upgrade_local_data,
)


class LocalTaskIdentityUpgradeTests(unittest.TestCase):
    def setUp(self):
        self.parent = Path(__file__).resolve().parents[1] / ".tmp/tests/upstream-local-identity"
        self.parent.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=self.parent)
        self.config = Path(self.temp.name) / "config.json"

    def tearDown(self):
        self.temp.cleanup()
        self.parent.rmdir()

    def test_explicit_upgrade_is_idempotent_and_preserves_existing_identity(self):
        known = str(uuid.uuid4())
        self.config.write_text(json.dumps([
            {"task_name": "无人机", "enabled": True},
            {"task_name": "xbox", "stable_task_id": known, "enabled": False},
        ], ensure_ascii=False), encoding="utf-8")
        calls = []
        def maintenance():
            calls.append("maintenance")
        def backup():
            calls.append("backup")
        self.assertEqual(upgrade_local_task_ids(self.config,
            verify_maintenance=maintenance, verify_backup=backup), 1)
        tasks = json.loads(self.config.read_text(encoding="utf-8"))
        self.assertEqual(str(uuid.UUID(tasks[0]["stable_task_id"])), tasks[0]["stable_task_id"])
        self.assertEqual(tasks[1]["stable_task_id"], known)
        before = self.config.read_bytes()
        self.assertEqual(upgrade_local_task_ids(self.config,
            verify_maintenance=maintenance, verify_backup=backup), 0)
        self.assertEqual(self.config.read_bytes(), before)
        self.assertEqual(calls, ["maintenance", "backup", "maintenance", "maintenance", "backup"])

    def test_corrupt_or_duplicate_identity_never_changes_config(self):
        duplicated = str(uuid.uuid4())
        for tasks in (
            [{"task_name": "a", "stable_task_id": "not-a-uuid"}],
            [{"task_name": "a", "stable_task_id": duplicated},
             {"task_name": "b", "stable_task_id": duplicated}],
            {"task_name": "not-a-list"},
        ):
            self.config.write_text(json.dumps(tasks), encoding="utf-8")
            before = self.config.read_bytes()
            with self.assertRaises(LocalTaskUpgradeError):
                upgrade_local_task_ids(self.config, verify_maintenance=lambda: None,
                                       verify_backup=lambda: None)
            self.assertEqual(self.config.read_bytes(), before)

    def test_missing_proof_or_refused_backup_never_changes_config(self):
        self.config.write_text('[{"task_name":"a"}]', encoding="utf-8")
        before = self.config.read_bytes()
        with self.assertRaises(LocalTaskUpgradeError):
            upgrade_local_task_ids(self.config, verify_maintenance=lambda: None,
                                   verify_backup=None)
        self.assertEqual(self.config.read_bytes(), before)

    def test_side_files_are_versioned_atomic_and_restart_safe(self):
        root = Path(self.temp.name)
        store = LocalUpstreamStore(root)
        store.initialize()
        self.assertEqual(store.read("hidden_items")["records"], [])
        revision = store.update("hidden_items", expected_revision=0,
            transform=lambda records: records + [{"item_id": "synthetic-item"}])
        self.assertEqual(revision, 1)
        reopened = LocalUpstreamStore(root)
        self.assertEqual(reopened.read("hidden_items")["records"],
                         [{"item_id": "synthetic-item"}])
        with self.assertRaisesRegex(LocalUpstreamStateError, "revision conflict"):
            reopened.update("hidden_items", expected_revision=0,
                            transform=lambda records: records)
        with self.assertRaisesRegex(LocalUpstreamStateError, "already exists"):
            store.initialize()

    def test_side_file_concurrent_update_allows_only_one_revision(self):
        store = LocalUpstreamStore(Path(self.temp.name))
        store.initialize()
        def update(value):
            try:
                store.update("view_preferences", expected_revision=0,
                    transform=lambda records: records + [{"value": value}])
                return "written"
            except LocalUpstreamStateError:
                return "conflict"
        with ThreadPoolExecutor(max_workers=2) as pool:
            self.assertEqual(sorted(pool.map(update, (1, 2))), ["conflict", "written"])
        self.assertEqual(len(store.read("view_preferences")["records"]), 1)

    def test_side_file_corruption_fails_closed(self):
        store = LocalUpstreamStore(Path(self.temp.name))
        store.initialize()
        (store.root / "hidden_items.json").write_bytes(b"not-json")
        with self.assertRaises(LocalUpstreamStateError):
            store.read("hidden_items")
        with self.assertRaises(LocalUpstreamStateError):
            store.update("hidden_items", expected_revision=0,
                         transform=lambda records: records)

    def test_price_log_is_idempotent_atomic_and_restart_safe(self):
        store = LocalUpstreamStore(Path(self.temp.name))
        store.initialize()
        observation = {
            "owner_id": "local_admin", "task_ref": str(uuid.uuid4()),
            "item_id": "synthetic-item", "run_id": str(uuid.uuid4()),
            "raw_price": "10.00", "amount": "10.00",
        }
        self.assertTrue(store.append_price_observation(observation))
        self.assertFalse(LocalUpstreamStore(Path(self.temp.name)).append_price_observation(observation))
        self.assertEqual(store.read_price_observations(), [observation])
        next_run = {**observation, "run_id": str(uuid.uuid4()), "amount": "9.00"}
        with ThreadPoolExecutor(max_workers=2) as pool:
            self.assertEqual(sorted(pool.map(store.append_price_observation,
                                             (next_run, next_run))), [False, True])
        self.assertEqual(store.read_price_observations(), [observation, next_run])

    def test_price_log_corrupt_tail_and_wrong_owner_fail_closed(self):
        store = LocalUpstreamStore(Path(self.temp.name))
        store.initialize()
        observation = {
            "owner_id": "local_admin", "task_ref": str(uuid.uuid4()),
            "item_id": "synthetic-item", "run_id": str(uuid.uuid4()),
        }
        with self.assertRaises(LocalUpstreamStateError):
            store.append_price_observation({**observation, "owner_id": "other"})
        log = store.root / "price_observations.jsonl"
        log.write_bytes(b'{"owner_id":"local_admin"')
        before = log.read_bytes()
        with self.assertRaises(LocalUpstreamStateError):
            store.append_price_observation(observation)
        self.assertEqual(log.read_bytes(), before)

    def test_refused_backup_proof_never_changes_config(self):
        self.config.write_text('[{"task_name":"a"}]', encoding="utf-8")
        before = self.config.read_bytes()

        def refuse():
            raise LocalTaskUpgradeError("backup proof absent")

        with self.assertRaises(LocalTaskUpgradeError):
            upgrade_local_task_ids(self.config, verify_maintenance=lambda: None,
                                   verify_backup=refuse)
        self.assertEqual(self.config.read_bytes(), before)

    def test_adapter_concurrent_creation_and_edit_preserve_stable_ids(self):
        adapter = LocalStorageAdapter(self.temp.name)
        with ThreadPoolExecutor(max_workers=2) as pool:
            created = list(pool.map(adapter.save_task,
                                    ({"task_name": "无人机"}, {"task_name": "xbox"})))
        self.assertEqual(len({item["stable_task_id"] for item in created}), 2)
        tasks = adapter.get_tasks()
        self.assertEqual(len(tasks), 2)
        original = {item["task_name"]: item["stable_task_id"] for item in tasks}
        adapter.save_task({"task_name": "无人机", "stable_task_id": str(uuid.uuid4()),
                           "enabled": False})
        adapter.update_task_order(["xbox", "无人机"])
        current = {item["task_name"]: item["stable_task_id"] for item in adapter.get_tasks()}
        self.assertEqual(current, original)

    def test_legacy_web_task_update_keeps_identity_under_shared_lock(self):
        task = Task(task_name="无人机", enabled=True, keyword="无人机", description="synthetic",
                    max_pages=1, personal_only=False, ai_prompt_base_file="base.txt",
                    ai_prompt_criteria_file="criteria.txt")
        with patch("src.task.CONFIG_FILE", str(self.config)):
            self.assertTrue(asyncio.run(add_task(task)))
            identity = task.stable_task_id
            self.assertTrue(asyncio.run(update_task(0, {"task_name": "无人机改名",
                                                         "stable_task_id": str(uuid.uuid4())})))
        self.assertEqual(json.loads(self.config.read_text(encoding="utf-8"))[0]
                         ["stable_task_id"], identity)

    def test_web_snapshot_rejects_stale_overwrite_after_other_writer(self):
        identity = str(uuid.uuid4())
        self.config.write_text(json.dumps([{"task_name": "无人机", "stable_task_id": identity}]),
                               encoding="utf-8")
        with patch("src.web.task_manager.CONFIG_FILE", str(self.config)):
            stale = asyncio.run(_load_local_tasks())
            adapter = LocalStorageAdapter(self.temp.name)
            adapter.save_task({"task_name": "xbox"})
            stale[0]["enabled"] = False
            with self.assertRaises(HTTPException) as error:
                asyncio.run(_save_local_tasks(stale))
        self.assertEqual(error.exception.status_code, 409)
        tasks = json.loads(self.config.read_text(encoding="utf-8"))
        self.assertEqual(len(tasks), 2)
        self.assertEqual(tasks[0]["stable_task_id"], identity)

    def test_explicit_local_upgrade_requires_matching_external_backup_and_is_repeatable(self):
        self.config.write_text('[{"task_name":"无人机"}]', encoding="utf-8")
        backup = self.parent / (Path(self.temp.name).name + ".config.backup")
        try:
            before = self.config.read_bytes()
            backup.write_bytes(before)
            digest = hashlib.sha256(before).hexdigest()
            checks = []
            self.assertEqual(upgrade_local_data(self.config, Path(self.temp.name), backup, digest,
                verify_maintenance=lambda: checks.append("stopped")), 1)
            self.assertEqual(len(checks), 3)
            adapter = LocalStorageAdapter(self.temp.name)
            self.assertEqual(adapter.upstream_store(owner_id="local_admin").read("hidden_items")["records"], [])
            with self.assertRaises(PermissionError):
                adapter.upstream_store(owner_id="other")
            # A repeat needs a fresh pre-upgrade snapshot of the now-current config.
            current = self.config.read_bytes()
            backup.write_bytes(current)
            self.assertEqual(upgrade_local_data(self.config, Path(self.temp.name), backup,
                hashlib.sha256(current).hexdigest(), verify_maintenance=lambda: None), 0)
            self.assertEqual(self.config.read_bytes(), current)
        finally:
            backup.unlink(missing_ok=True)

    def test_local_upgrade_refuses_wrong_backup_without_creating_side_files(self):
        self.config.write_text('[{"task_name":"无人机"}]', encoding="utf-8")
        backup = self.parent / (Path(self.temp.name).name + ".config.backup")
        try:
            backup.write_bytes(b"wrong copy")
            before = self.config.read_bytes()
            with self.assertRaises(LocalTaskUpgradeError):
                upgrade_local_data(self.config, Path(self.temp.name), backup,
                    hashlib.sha256(backup.read_bytes()).hexdigest(), verify_maintenance=lambda: None)
            self.assertEqual(self.config.read_bytes(), before)
            self.assertFalse(LocalUpstreamStore(Path(self.temp.name)).root.exists())
        finally:
            backup.unlink(missing_ok=True)

    def test_local_upgrade_cli_uses_only_explicit_synthetic_root(self):
        self.config.write_text('[{"task_name":"xbox"}]', encoding="utf-8")
        backup = self.parent / (Path(self.temp.name).name + ".cli.backup")
        try:
            backup.write_bytes(self.config.read_bytes())
            environment = dict(os.environ)
            if "SYSTEMROOT" in os.environ:
                environment["SYSTEMROOT"] = os.environ["SYSTEMROOT"]
            command = [sys.executable, "-B", "-m", "scripts.upstream.migrate_local",
                       "--data-root", self.temp.name, "--config", str(self.config),
                       "--backup", str(backup), "--backup-sha256",
                       hashlib.sha256(backup.read_bytes()).hexdigest(),
                       "--confirm-stopped", "我已停止所有业务进程并保留配置备份"]
            completed = subprocess.run(command, cwd=Path(__file__).resolve().parents[1],
                                       env=environment, stdin=subprocess.DEVNULL,
                                       capture_output=True, text=True, encoding="utf-8", timeout=30)
            self.assertEqual(completed.returncode, 0, completed.stderr)
            self.assertEqual(len(json.loads(self.config.read_text(encoding="utf-8"))), 1)
            self.assertEqual(LocalUpstreamStore(Path(self.temp.name)).read("hidden_items")["revision"], 0)
        finally:
            backup.unlink(missing_ok=True)


if __name__ == "__main__":
    unittest.main()
