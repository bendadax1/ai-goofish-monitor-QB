"""显式升级私有 stdio 协议的离线边界测试。"""

import io
import json
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import portable_migrate


class PortableMigrateEntryTests(unittest.TestCase):
    def request(self):
        return {
            "type": "migrate", "session": "a" * 64,
            "data_root": "X:/synthetic/data", "pgdata": "X:/synthetic/data/postgres/cluster",
            "instance_id": "11111111-1111-4111-8111-111111111111",
            "admin_dsn": "synthetic-private-admin-dsn", "admin_role": "fixture_admin",
            "app_role": "fixture_app", "probe_role": "fixture_probe",
            "backup_file": "X:/synthetic/backup.gfbk", "backup_sha256": "b" * 64,
        }

    def run_entry(self, request, answers, migration):
        wire = b"".join((json.dumps(frame) + "\n").encode("utf-8")
                        for frame in (request, *answers))
        stdin = io.TextIOWrapper(io.BytesIO(wire), encoding="utf-8")
        output = io.BytesIO()
        stdout = io.TextIOWrapper(output, encoding="utf-8", write_through=True)
        stderr = io.StringIO()
        engine = SimpleNamespace(dispose=lambda: None)
        with patch.object(portable_migrate.sys, "stdin", stdin), \
             patch.object(portable_migrate.sys, "stdout", stdout), \
             patch.object(portable_migrate.sys, "stderr", stderr), \
             patch.object(portable_migrate, "_engine_from_environment", return_value=engine), \
             patch.object(portable_migrate, "_verify_backup") as backup_check, \
             patch.object(portable_migrate, "migrate_upstream_schema", migration):
            code = portable_migrate.run()
        return code, [json.loads(line) for line in output.getvalue().splitlines()], \
            stderr.getvalue(), backup_check.call_count

    def test_three_fresh_maintenance_proofs_and_backup_rechecks(self):
        def migrate(*args, **kwargs):
            for _ in range(3):
                kwargs["verify_maintenance"]()
            for _ in range(2):
                kwargs["verify_backup"]()
            return SimpleNamespace(version=2, status="applied")

        answers = [{"type": "verified", "session": "a" * 64, "counter": n}
                   for n in (1, 2, 3)]
        code, frames, stderr, backup_calls = self.run_entry(self.request(), answers, migrate)
        self.assertEqual(code, 0)
        self.assertEqual([frame["type"] for frame in frames], ["verify"] * 3 + ["complete"])
        self.assertEqual([frame["counter"] for frame in frames[:3]], [1, 2, 3])
        self.assertEqual(frames[-1]["schema_version"], 2)
        self.assertEqual(backup_calls, 2)
        self.assertNotIn("synthetic-private-admin-dsn", json.dumps(frames) + stderr)

    def test_wrong_ack_and_extra_request_field_fail_without_completion(self):
        def migrate(*args, **kwargs):
            kwargs["verify_maintenance"]()
            self.fail("refused proof must stop migration")

        wrong = [{"type": "verified", "session": "a" * 64, "counter": 2}]
        code, frames, stderr, _ = self.run_entry(self.request(), wrong, migrate)
        self.assertEqual(code, 1)
        self.assertEqual([frame["type"] for frame in frames], ["verify"])
        self.assertNotIn("synthetic-private-admin-dsn", stderr)
        invalid = {**self.request(), "extra": "unexpected"}
        code, frames, _, _ = self.run_entry(invalid, [], lambda *a, **k: self.fail("must not migrate"))
        self.assertEqual((code, frames), (1, []))


if __name__ == "__main__":
    unittest.main()
