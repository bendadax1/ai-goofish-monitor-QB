"""Fixed portable defaults manifest and seed-write tests."""

from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
from contextlib import contextmanager
from pathlib import Path
from unittest.mock import patch

from src.portable.seeds import PortableSeedError, apply_system_seeds, load_seed_bundle
from src.portable import schema


class _Result:
    def first(self):
        return None


class _Connection:
    def __init__(self) -> None:
        self.statements = []

    def execute(self, statement, parameters=None):
        self.statements.append((statement, parameters))
        return _Result()


class _Engine:
    def __init__(self, connection) -> None:
        self.connection = connection

    @contextmanager
    def begin(self):
        yield self.connection


class PortableSeedTests(unittest.TestCase):
    @contextmanager
    def temporary_directory(self):
        parent = Path(__file__).resolve().parents[1] / ".tmp/tests/portable-seeds"
        parent.mkdir(parents=True, exist_ok=True)
        try:
            with tempfile.TemporaryDirectory(dir=parent) as directory:
                yield directory
        finally:
            parent.rmdir()
    def _fixture(self, parent: Path) -> Path:
        defaults = parent / "defaults"
        prompt = b"portable base prompt\n"
        bayes = json.dumps({
            "version": "bayes_v1",
            "recommendation_fusion": {"weight": 1},
            "_samples": {
                "可信": [{"name": "preset", "vector": [1, 2], "source": "preset"}],
                "不可信": [{"name": "risk", "vector": [3, 4], "source": "preset"}],
            },
        }).encode("utf-8")
        for relative, raw in (("prompts/base_prompt.txt", prompt), ("prompts/bayes/bayes_v1.json", bayes)):
            path = defaults.joinpath(*relative.split("/"))
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(raw)
        manifest = {
            "format_version": 1,
            "files": [
                {"path": "prompts/base_prompt.txt", "kind": "prompt", "size": len(prompt), "sha256": hashlib.sha256(prompt).hexdigest()},
                {"path": "prompts/bayes/bayes_v1.json", "kind": "bayes", "size": len(bayes), "sha256": hashlib.sha256(bayes).hexdigest()},
            ],
        }
        (defaults / "seed_manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        return defaults

    def test_manifest_only_accepts_the_two_verified_fixed_assets(self) -> None:
        with self.temporary_directory() as temporary:
            defaults = self._fixture(Path(temporary))
            bundle = load_seed_bundle(defaults)
            self.assertEqual(bundle.prompt_content, "portable base prompt\n")
            self.assertEqual(bundle.bayes_payload["version"], "bayes_v1")

            manifest_path = defaults / "seed_manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["files"][0]["sha256"] = "0" * 64
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            with self.assertRaises(PortableSeedError):
                load_seed_bundle(defaults)

    def test_seed_write_adds_exactly_four_groups_and_two_system_assets(self) -> None:
        with self.temporary_directory() as temporary:
            bundle = load_seed_bundle(self._fixture(Path(temporary)))
            connection = _Connection()
            apply_system_seeds(connection, bundle)
            table_names = [statement.table.name for statement, _ in connection.statements if hasattr(statement, "table")]
            self.assertEqual(table_names.count("user_groups"), 4)
            self.assertEqual(table_names.count("group_permissions"), 24)
            self.assertEqual(table_names.count("prompt_templates"), 1)
            self.assertEqual(table_names.count("bayes_profiles"), 1)
            self.assertEqual(table_names.count("bayes_samples"), 2)

    def test_schema_initializer_applies_verified_bundle_only_when_explicitly_supplied(self) -> None:
        with self.temporary_directory() as temporary:
            defaults = self._fixture(Path(temporary))
            connection = _Connection()
            engine = _Engine(connection)

            class _Metadata:
                @staticmethod
                def create_all(*, bind, checkfirst):
                    self.assertIs(bind, connection)
                    self.assertFalse(checkfirst)

            with (
                patch.object(schema, "_assert_empty_public_schema"),
                patch.object(schema, "_portable_metadata", return_value=_Metadata()),
                patch.object(schema, "_acquire_schema_lock"),
                patch.object(schema, "apply_system_seeds") as apply,
            ):
                self.assertEqual(schema.initialize_schema(engine, seed_root=defaults), 1)
            apply.assert_called_once()
            self.assertEqual(apply.call_args.args[0], connection)


if __name__ == "__main__":
    unittest.main()
