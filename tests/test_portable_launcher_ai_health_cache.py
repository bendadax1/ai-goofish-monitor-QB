"""Read-only Launcher AI health cache contract; all provider access is mocked out."""

import asyncio
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import unittest

from tests._ci_guard import needs_postgres
from unittest.mock import patch

from fastapi import FastAPI
from fastapi.testclient import TestClient

from src.portable.launcher_pairing import LauncherPairingRegistry, _digest
import src.portable.launcher_pairing as pairing_module
from src.web import ai_health


class _FakeStorage:
    def __init__(self):
        self.sessions = {
            "source-session": {
                "user_id": "user-1",
                "expires_at": datetime.now(timezone.utc) + timedelta(hours=1),
            }
        }
        self.users = {
            "user-1": {"id": "user-1", "username": "alice", "is_active": True},
            "user-2": {"id": "user-2", "username": "bob", "is_active": True},
        }
        self.allow_ai = True
        self.allow_tasks = False
        self.configs = {
            "user-1": {
                "id": "config-1", "config_revision": 4, "api_key": "do-not-return-this-key",
                "api_base_url": "https://provider.example/v1", "model": "fixture-model",
            },
            "user-2": {
                "id": "config-2", "config_revision": 9, "api_key": "other-secret",
                "api_base_url": "https://other.example/v1", "model": "other-model",
            },
        }
        self.read_revisions = []

    def get_session_by_token(self, token_hash):
        session = self.sessions.get(token_hash)
        return dict(session) if session else None

    def get_user_by_id(self, user_id):
        return self.users.get(user_id)

    def get_user_groups(self, _user_id):
        permissions = []
        if self.allow_ai:
            permissions.append({"category": "ai", "enabled": True})
        if self.allow_tasks:
            permissions.append({"category": "tasks", "enabled": True})
        if not permissions:
            return []
        return [{"permissions": permissions}]

    def get_default_api_config_with_revision(self, user_id):
        config = self.configs.get(user_id)
        if not config:
            return None
        result = deepcopy(config)
        if self.read_revisions:
            result["config_revision"] = self.read_revisions.pop(0)
        return result

    def get_default_api_config(self, user_id):
        return deepcopy(self.configs.get(user_id))


@needs_postgres
class PortableLauncherAiHealthCacheTests(unittest.TestCase):
    instance_id = "instance-a"
    token = "l1." + ("H" * 43)

    def setUp(self):
        ai_health._AI_HEALTH_CACHE.clear()
        self.registry = LauncherPairingRegistry()
        self.storage = _FakeStorage()
        self.registry._bindings[_digest(self.token)] = {
            "user_id": "user-1", "instance_id": self.instance_id,
            "source_session_digest": "source-session",
            "expires_at": datetime.now(timezone.utc) + timedelta(minutes=30),
            "scopes": ("me:read", "ai:read"),
        }
        self.app = FastAPI()
        self.app.include_router(pairing_module.router)
        self.client_context = TestClient(self.app, base_url="http://127.0.0.1:43127")
        self.client = self.client_context.__enter__()
        self.patches = [
            patch.object(pairing_module, "registry", self.registry),
            patch.object(pairing_module, "portable_mode", return_value=True),
            patch.object(pairing_module, "STORAGE_BACKEND", return_value="postgres"),
            patch.object(pairing_module, "get_storage", return_value=self.storage),
            patch.object(ai_health, "STORAGE_BACKEND", return_value="postgres"),
            patch.object(ai_health, "get_storage", return_value=self.storage),
            patch("src.web.auth.is_multi_user_mode", return_value=True),
            patch("src.web.auth.get_storage", return_value=self.storage),
        ]
        for item in self.patches:
            item.start()
        self.addCleanup(self._close)

    def _close(self):
        self.client_context.__exit__(None, None, None)
        for item in reversed(self.patches):
            item.stop()

    def _headers(self, *, origin="http://127.0.0.1:43127", token=None):
        return {
            "X-Goofish-Instance-Id": self.instance_id,
            "Authorization": f"Bearer {token or self.token}",
            "Origin": origin,
        }

    def _get(self, **kwargs):
        return self.client.get("/api/launcher/ai/health", headers=self._headers(**kwargs))

    def _snapshot(self, *, overall="ok", success=True, message="fixture"):
        return {
            "overall_level": overall,
            "web_test": {"success": success, "latency_ms": 12, "message": message},
            "backend_test": {"success": success, "latency_ms": 20, "message": message},
            "vision_capability": {"status": "supported"},
            "owner_id": "must-not-leak-user-id",
            "source_label": "must-not-leak-label",
        }

    def test_fresh_snapshot_is_read_only_redacted_and_current(self):
        ai_health._set_cached_snapshot(
            {"user_id": "user-1"}, self._snapshot(message="provider detail do-not-return-this-key"),
            config_identity=("config-1", 4),
        )
        with patch.object(ai_health.socket, "getaddrinfo") as dns, \
             patch.object(ai_health.httpx, "AsyncClient") as http, \
             patch.object(ai_health, "OpenAI") as openai, \
             patch.object(ai_health, "AsyncOpenAI") as async_openai:
            response = self._get()
        self.assertEqual(response.status_code, 200)
        body = response.json()
        self.assertEqual(body["status"], "current")
        self.assertEqual(body["category"], "healthy")
        self.assertEqual(body["config_id"], "config-1")
        self.assertEqual(body["config_revision"], 4)
        self.assertEqual(body["latency_ms"], 20)
        self.assertTrue(body["observed_at"].endswith("Z"))
        self.assertTrue(body["expires_at"].endswith("Z"))
        self.assertEqual(set(body), {
            "status", "observed_at", "expires_at", "config_id", "config_revision",
            "source", "category", "latency_ms",
        })
        self.assertNotIn("do-not-return-this-key", response.text)
        self.assertNotIn("provider detail", response.text)
        self.assertNotIn("must-not-leak", response.text)
        dns.assert_not_called()
        http.assert_not_called()
        openai.assert_not_called()
        async_openai.assert_not_called()

    def test_never_checked_and_unconfigured_are_explicit(self):
        response = self._get()
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.json()["status"], "never_checked")
        self.assertIsNone(response.json()["observed_at"])

        self.storage.configs["user-1"]["api_key"] = ""
        unconfigured = self._get()
        self.assertEqual(unconfigured.json()["status"], "unconfigured")
        self.assertEqual(unconfigured.json()["category"], "unconfigured")

    def test_config_revision_and_config_id_changes_invalidate_same_cache(self):
        ai_health._set_cached_snapshot(
            {"user_id": "user-1"}, self._snapshot(), config_identity=("config-1", 3),
        )
        changed_revision = self._get().json()
        self.assertEqual(changed_revision["status"], "config_changed")
        self.assertEqual(changed_revision["category"], "unknown")

        self.storage.configs["user-1"]["id"] = "replacement-config"
        self.storage.configs["user-1"]["config_revision"] = 3
        changed_id = self._get().json()
        self.assertEqual(changed_id["status"], "config_changed")
        self.assertEqual(changed_id["config_id"], "replacement-config")

    def test_expired_snapshot_is_stale_and_failure_is_not_healthy(self):
        ai_health._set_cached_snapshot(
            {"user_id": "user-1"}, self._snapshot(overall="error", success=False, message="timeout"),
            config_identity=("config-1", 4),
        )
        with patch.object(ai_health.time, "monotonic", return_value=ai_health.time.monotonic() + 301):
            response = self._get()
        self.assertEqual(response.json()["status"], "stale")
        self.assertEqual(response.json()["category"], "timeout")

    def test_fresh_failed_snapshot_is_current_but_not_healthy(self):
        ai_health._set_cached_snapshot(
            {"user_id": "user-1"}, self._snapshot(overall="error", success=False, message="ReadTimeout"),
            config_identity=("config-1", 4),
        )
        response = self._get().json()
        self.assertEqual(response["status"], "current")
        self.assertEqual(response["category"], "timeout")

    def test_ai_read_permission_is_required(self):
        self.storage.allow_ai = False
        self.storage.allow_tasks = True
        with patch.object(ai_health, "get_portable_launcher_ai_health") as reader:
            response = self._get()
        self.assertEqual(response.status_code, 403)
        reader.assert_not_called()

    def test_cache_is_user_scoped(self):
        ai_health._set_cached_snapshot(
            {"user_id": "user-1"}, self._snapshot(), config_identity=("config-1", 4),
        )
        user_two = ai_health.get_portable_launcher_ai_health({"user_id": "user-2"})
        self.assertEqual(user_two["status"], "never_checked")
        self.assertEqual(user_two["config_id"], "config-2")

    def test_probe_result_is_not_bound_to_a_revision_changed_during_probe(self):
        self.storage.read_revisions = [4, 5]

        async def fake_backend_probe(_config):
            return {"success": True, "level": "ok", "message": "fake", "latency_ms": 1}

        async def run():
            with patch.object(ai_health, "_run_web_text_probe_sync", return_value={
                "success": True, "level": "ok", "message": "fake", "latency_ms": 1,
            }), patch.object(ai_health, "_run_backend_text_probe_async", fake_backend_probe), patch.object(
                ai_health, "_run_vision_probe_async", fake_backend_probe
            ):
                return await ai_health.run_ai_health_check(
                    {"user_id": "user-1"}, run_web=True, run_backend=False, check_vision=False,
                )

        result = asyncio.run(run())
        self.assertEqual(result["overall_level"], "ok")
        self.assertFalse(ai_health._AI_HEALTH_CACHE)


if __name__ == "__main__":
    unittest.main()
