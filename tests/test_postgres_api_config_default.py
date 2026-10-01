"""Default-row ownership rules for portable PostgreSQL API configuration writes."""

from contextlib import contextmanager
from types import SimpleNamespace
import unittest
import uuid
from unittest import mock

from tests._ci_guard import needs_postgres
from src.storage import postgres_adapter
from src.storage.models import UserApiConfig


class _Query:
    def __init__(self, rows=(), first=None):
        self.rows = list(rows)
        self.first_row = first
        self.filters = []
        self.locked = False

    def filter(self, *expressions):
        self.filters.extend(expressions)
        return self

    def with_for_update(self):
        self.locked = True
        return self

    def all(self):
        return self.rows

    def first(self):
        return self.first_row


@needs_postgres
class PortablePostgresDefaultConfigTests(unittest.TestCase):
    def _save(self, *, portable, defaults, existing=None, payload=None):
        adapter = postgres_adapter.PostgresAdapter.__new__(postgres_adapter.PostgresAdapter)
        session = mock.MagicMock()
        previous_defaults = _Query(rows=defaults)
        existing_query = _Query(first=existing)
        session.query.side_effect = [previous_defaults, existing_query] if portable and payload.get("is_default") is True else [existing_query]
        session.add.side_effect = lambda config: setattr(config, "id", uuid.uuid4())

        @contextmanager
        def get_session():
            yield session

        adapter.get_session = get_session
        with mock.patch.object(postgres_adapter, "PORTABLE_MODE", portable):
            result = adapter.save_user_api_config("user-fixture", payload)
        return result, previous_defaults, existing_query, session

    def test_portable_insert_default_clears_previous_default_in_locked_transaction(self):
        old = SimpleNamespace(is_default=True)
        result, defaults_query, _, session = self._save(
            portable=True,
            defaults=[old],
            payload={
                "provider": "openai", "name": "new-default", "is_default": True,
                "api_base_url": "https://new.example.invalid/v1", "model": "model-new",
                "extra_config": {},
            },
        )

        self.assertFalse(old.is_default)
        self.assertTrue(result["is_default"])
        self.assertIsNotNone(result["id"])
        self.assertTrue(defaults_query.locked)
        session.execute.assert_called_once()

    def test_portable_promotion_excludes_target_and_clears_other_default(self):
        target_id = uuid.uuid4()
        old = SimpleNamespace(is_default=True, id=uuid.uuid4())
        target = UserApiConfig(
            id=target_id,
            user_id="user-fixture",
            provider="openai",
            name="secondary",
            api_base_url="https://secondary.example.invalid/v1",
            model="secondary-model",
            extra_config={},
            is_default=False,
        )
        result, defaults_query, _, _ = self._save(
            portable=True,
            defaults=[old],
            existing=target,
            payload={"id": str(target_id), "is_default": True},
        )

        self.assertFalse(old.is_default)
        self.assertTrue(target.is_default)
        self.assertEqual(result["id"], str(target_id))
        self.assertTrue(defaults_query.locked)
        self.assertEqual(len(defaults_query.filters), 3)

    def test_nonportable_save_preserves_legacy_default_behavior(self):
        old = SimpleNamespace(is_default=True)
        result, defaults_query, _, session = self._save(
            portable=False,
            defaults=[old],
            payload={
                "provider": "openai", "name": "docker-default", "is_default": True,
                "api_base_url": "https://docker.example.invalid/v1", "model": "legacy-model",
                "extra_config": {},
            },
        )

        self.assertTrue(old.is_default)
        self.assertTrue(result["is_default"])
        self.assertFalse(defaults_query.locked)
        self.assertEqual(session.query.call_count, 0)


if __name__ == "__main__":
    unittest.main()
