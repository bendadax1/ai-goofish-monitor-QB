"""Private Launcher/backup-entry handshake, without a database or business I/O."""
import io
import json
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import portable_backup


class BackupEntryTests(unittest.TestCase):
    def _request(self):
        return {
            "type": "backup", "data_root": "X:/data", "postgres_root": "X:/pg",
            "pgdata": "X:/data/postgres/cluster", "instance_id": "fixture-id",
            "app_version": "fixture", "admin_dsn": "fixture-dsn",
            "recovery_keys": {"encryption_master_key": "fixture", "secret_key": "fixture"},
            "destination": "Y:/new.backup", "passphrase": "fixture-passphrase",
            "session": "a" * 64,
        }

    def _run(self, responses, backup):
        wire = b"".join((json.dumps(frame) + "\n").encode("utf-8") for frame in [self._request(), *responses])
        stdin = io.TextIOWrapper(io.BytesIO(wire), encoding="utf-8")
        output_bytes = io.BytesIO()
        stdout = io.TextIOWrapper(output_bytes, encoding="utf-8", write_through=True)
        with patch.object(portable_backup.sys, "stdin", stdin), patch.object(portable_backup.sys, "stdout", stdout), \
             patch.object(portable_backup.sys, "stderr", io.StringIO()), \
             patch.object(portable_backup, "create_business_backup", backup):
            code = portable_backup.run()
        return code, [json.loads(line) for line in output_bytes.getvalue().splitlines()]

    def test_each_backend_callback_requires_fresh_launcher_ack(self):
        observed = []

        def backup(**kwargs):
            for _ in range(3):
                kwargs["verify_quiesced"]()
                observed.append("verified")
            return SimpleNamespace(sha256="b" * 64, files=("database.dump", "backup.json", "recovery-keys.json"))

        responses = [{"type": "verified", "session": "a" * 64, "counter": counter} for counter in (1, 2, 3)]
        code, frames = self._run(responses, backup)
        self.assertEqual(code, 0)
        self.assertEqual(len(observed), 3)
        self.assertEqual([frame["type"] for frame in frames], ["verify", "verify", "verify", "complete"])
        self.assertEqual([frame["counter"] for frame in frames[:3]], [1, 2, 3])

    def test_missing_ack_fails_closed(self):
        def backup(**kwargs):
            kwargs["verify_quiesced"]()
            raise AssertionError("must not pass first verifier")

        code, frames = self._run([], backup)
        self.assertEqual(code, 1)
        self.assertEqual([frame["type"] for frame in frames], ["verify"])

    def test_wrong_counter_fails_closed(self):
        def backup(**kwargs):
            kwargs["verify_quiesced"]()
            raise AssertionError("must not pass first verifier")

        code, frames = self._run([{"type": "verified", "session": "a" * 64, "counter": 2}], backup)
        self.assertEqual(code, 1)
        self.assertEqual([frame["type"] for frame in frames], ["verify"])


if __name__ == "__main__":
    unittest.main()
