"""Provision preflight rejects foreign ownership before any SQL is sent."""

import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import uuid

from src.portable.provision import provision, ProvisionError


class PortableProvisionTests(unittest.TestCase):
    def test_ownership_and_generated_credentials_fail_before_connection(self):
        parent = Path(__file__).resolve().parents[1] / ".tmp/tests/portable-provision"
        parent.mkdir(parents=True, exist_ok=True)
        try:
            with tempfile.TemporaryDirectory(dir=parent) as temporary:
                pgdata = Path(temporary)
                identity = str(uuid.uuid4())
                (pgdata / ".aigoofish-cluster.json").write_text(json.dumps({
                    "format_version":1, "instance_id":identity,
                    "cluster_id":str(uuid.uuid4()), "engine_version":"17.11",
                }), encoding="utf-8")
                environment = {
                    "GOOFISH_PORTABLE_ADMIN_DATABASE_URL":"postgresql://bootstrap:admin-secret@127.0.0.1:1/postgres",
                    "GOOFISH_PORTABLE_APP_DATABASE_PASSWORD":"A"*32,
                    "GOOFISH_PORTABLE_PROBE_DATABASE_PASSWORD":"B"*32,
                }
                cases = [
                    (str(uuid.uuid4()), environment),
                    (identity, {**environment, "GOOFISH_PORTABLE_APP_DATABASE_PASSWORD":"short"}),
                    (identity, {**environment, "GOOFISH_PORTABLE_PROBE_DATABASE_PASSWORD":"A"*32}),
                    (identity, {**environment, "GOOFISH_PORTABLE_ADMIN_DATABASE_URL":"postgresql://b:secret@203.0.113.1:5432/postgres"}),
                    (identity, {**environment, "GOOFISH_PORTABLE_ADMIN_DATABASE_URL":"postgresql://b:secret@127.0.0.1:5432/existing"}),
                ]
                before = (pgdata / ".aigoofish-cluster.json").read_bytes()
                with patch("src.portable.provision.psycopg2.connect") as connect:
                    for supplied_id, supplied_environment in cases:
                        with self.assertLogs("src.portable.provision", level="ERROR") as logs:
                            with self.assertRaises(ProvisionError) as failure:
                                provision(str(pgdata), supplied_id, environ=supplied_environment)
                        self.assertNotIn("admin-secret", str(failure.exception) + str(logs.output))
                    connect.assert_not_called()
                self.assertEqual((pgdata / ".aigoofish-cluster.json").read_bytes(), before)
        finally:
            parent.rmdir()
