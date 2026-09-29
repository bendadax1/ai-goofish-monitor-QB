"""Private Launcher/restore-entry handshake without a PostgreSQL process."""
import io
import json
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import portable_restore


class RestoreEntryTests(unittest.TestCase):
    def _request(self):
        return {
            "type": "restore", "archive_file": "X:/trusted/backup.agb",
            "passphrase": "secret-passphrase", "postgres_root": "X:/pg",
            "pgdata": "X:/new/postgres/cluster", "instance_id": "new-instance",
            "admin_dsn": "secret-admin-dsn", "target_data_root": "X:/new",
            "staging_parent": "X:/private/stage", "handoff_parent": "X:/private/handoff",
            "session": "a" * 64,
        }

    def _run(self, responses, restore, request=None):
        frames = [request or self._request(), *responses]
        wire = b"".join((json.dumps(frame) + "\n").encode("utf-8") for frame in frames)
        stdin = io.TextIOWrapper(io.BytesIO(wire), encoding="utf-8")
        output_bytes = io.BytesIO()
        stdout = io.TextIOWrapper(output_bytes, encoding="utf-8", write_through=True)
        stderr = io.StringIO()
        with patch.object(portable_restore.sys, "stdin", stdin), \
             patch.object(portable_restore.sys, "stdout", stdout), \
             patch.object(portable_restore.sys, "stderr", stderr), \
             patch.object(portable_restore, "restore_business_backup", restore):
            code = portable_restore.run()
        return code, [json.loads(line) for line in output_bytes.getvalue().splitlines()], stderr.getvalue()

    def test_each_maintenance_check_requires_fresh_ack(self):
        def restore(**kwargs):
            for _ in range(4):
                kwargs["verify_maintenance"]()
            self.assertEqual(kwargs["passphrase"], "secret-passphrase")
            self.assertEqual(kwargs["admin_dsn"], "secret-admin-dsn")
            return SimpleNamespace(
                instance_id="new-instance", source_instance_id="old-instance",
                archive_sha256="b" * 64, schema_version=1,
                table_counts={"sessions": 0},
                file_count=3, revoked_sessions=2,
                private_handoff=Path("X:/private/handoff/.restore-handoff-fixture"),
                status="awaiting_target_user_dpapi_and_switch_confirmation",
            )

        replies = [{"type": "verified", "session": "a" * 64, "counter": n} for n in range(1, 5)]
        code, frames, stderr = self._run(replies, restore)
        self.assertEqual(code, 0)
        self.assertEqual([frame["type"] for frame in frames], ["verify"] * 4 + ["complete"])
        self.assertEqual([frame["counter"] for frame in frames[:4]], [1, 2, 3, 4])
        self.assertEqual(frames[-1]["status"], "awaiting_target_user_dpapi_and_switch_confirmation")
        self.assertEqual(frames[-1]["schema_version"], 1)
        self.assertNotIn("secret-passphrase", json.dumps(frames) + stderr)
        self.assertNotIn("secret-admin-dsn", json.dumps(frames) + stderr)

    def test_wrong_ack_fails_without_completion_or_secret_echo(self):
        def restore(**kwargs):
            kwargs["verify_maintenance"]()
            self.fail("refused proof must stop restore")

        code, frames, stderr = self._run(
            [{"type": "verified", "session": "a" * 64, "counter": 2}], restore)
        self.assertEqual(code, 1)
        self.assertEqual([frame["type"] for frame in frames], ["verify"])
        self.assertNotIn("secret-passphrase", stderr)

    def test_extra_request_field_is_rejected(self):
        request = self._request()
        request["unexpected"] = "value"
        code, frames, _ = self._run([], lambda **_: self.fail("must not restore"), request)
        self.assertEqual(code, 1)
        self.assertEqual(frames, [])

    def test_proof_count_is_bounded(self):
        def restore(**kwargs):
            for _ in range(5):
                kwargs["verify_maintenance"]()

        replies = [{"type": "verified", "session": "a" * 64, "counter": n} for n in range(1, 5)]
        code, frames, _ = self._run(replies, restore)
        self.assertEqual(code, 1)
        self.assertEqual([frame["counter"] for frame in frames], [1, 2, 3, 4])


if __name__ == "__main__":
    unittest.main()
