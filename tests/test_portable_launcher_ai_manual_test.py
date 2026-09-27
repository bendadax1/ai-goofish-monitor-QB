"""Launcher manual-AI route authorization and bounded-dispatch tests."""

from datetime import datetime, timedelta, timezone
import unittest
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
        self.default_ai_config = {
            "id": "config-1",
            "config_revision": 4,
            "api_key": "fixture-secret",
            "api_base_url": "https://provider.example/v1",
            "model": "fixture-model",
            "extra_config": {},
        }

    def get_session_by_token(self, token_hash):
        session = self.sessions.get(token_hash)
        return dict(session) if session else None

    def get_user_by_id(self, user_id):
        return self.users.get(user_id)

    def get_user_groups(self, _user_id):
        if not self.allow_ai:
            return []
        return [{"permissions": [{"category": "ai", "enabled": True}]}]

    def get_default_api_config_with_revision(self, _user_id):
        return dict(self.default_ai_config) if self.default_ai_config is not None else None


class PortableLauncherAiManualTestRouteTests(unittest.TestCase):
    instance_id = "instance-a"
    token = "l1." + ("T" * 43)

    def setUp(self):
        self.registry = LauncherPairingRegistry()
        self.storage = _FakeStorage()
        self.registry._bindings[_digest(self.token)] = {
            "user_id": "user-1",
            "instance_id": self.instance_id,
            "source_session_digest": "source-session",
            "expires_at": datetime.now(timezone.utc) + timedelta(minutes=30),
            "scopes": ("me:read", "ai:read", "ai:write"),
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

    def _headers(self, *, token=None, instance_id=None, origin="http://127.0.0.1:43127"):
        headers = {"X-Goofish-Instance-Id": instance_id or self.instance_id}
        if token is not None:
            headers["Authorization"] = f"Bearer {token}"
        if origin is not None:
            headers["Origin"] = origin
        return headers

    def _post(self, payload, *, headers=None):
        return self.client.post(
            "/api/launcher/ai/test",
            headers=headers or self._headers(token=self.token),
            json=payload,
        )

    def _payload(self, request_id, *, settings=None, config_id=None, revision=None):
        return {
            "confirmed": True,
            "request_id": request_id,
            "expected_config_id": self.storage.default_ai_config["id"] if config_id is None else config_id,
            "expected_config_revision": self.storage.default_ai_config["config_revision"] if revision is None else revision,
            "settings": settings or {},
        }

    def test_missing_bad_expired_and_cross_instance_tokens_are_rejected(self):
        payload = self._payload("launcher-test-001")
        for headers in (
            self._headers(),
            self._headers(token="not-a-token"),
            self._headers(token="l1." + ("X" * 43)),
        ):
            response = self._post(payload, headers=headers)
            self.assertEqual(response.status_code, 401)

        self.registry._bindings[_digest(self.token)]["expires_at"] = datetime.now(timezone.utc) - timedelta(seconds=1)
        expired = self._post(payload)
        self.assertEqual(expired.status_code, 401)

        wrong_instance = self._post(payload, headers=self._headers(token=self.token, instance_id="instance-b"))
        self.assertEqual(wrong_instance.status_code, 401)

    def test_wrong_origin_and_source_session_user_mismatch_are_rejected(self):
        payload = self._payload("launcher-test-002")
        response = self._post(payload, headers=self._headers(token=self.token, origin="http://attacker.example"))
        self.assertEqual(response.status_code, 403)

        self.storage.sessions["source-session"]["user_id"] = "user-2"
        response = self._post(payload)
        self.assertEqual(response.status_code, 401)

    def test_ai_permission_is_rechecked_at_test_time(self):
        self.storage.allow_ai = False
        with patch.object(ai_health, "run_portable_ai_manual_test") as runner:
            response = self._post(self._payload("launcher-test-003"))
        self.assertEqual(response.status_code, 403)
        runner.assert_not_called()

    def test_confirmation_schema_and_ai_override_whitelist_are_strict(self):
        base = self._payload("launcher-test-004")
        for payload in (
            {key: value for key, value in base.items() if key != "expected_config_id"},
            {key: value for key, value in base.items() if key != "expected_config_revision"},
            {**base, "expected_config_id": "", "expected_config_revision": 4},
            {**base, "expected_config_revision": True},
            {**base, "unexpected": "field"},
            {**base, "settings": {"proxy_enabled": True}},
            {**base, "settings": {"OPENAI_API_KEY": 123}},
        ):
            response = self._post(payload)
            self.assertEqual(response.status_code, 422)

    def test_confirmed_request_dispatches_only_whitelisted_settings(self):
        payload = {
            "confirmed": True,
            "request_id": "launcher-test-005",
            "expected_config_id": "config-1",
            "expected_config_revision": 4,
            "settings": {"OPENAI_BASE_URL": "https://provider.example/v1", "OPENAI_MODEL_NAME": "fake"},
        }
        with patch.object(ai_health, "run_portable_ai_manual_test", return_value={
            "status": "success", "message": "fixture success", "latency_ms": 1, "checked_at": "fixture",
        }) as runner:
            response = self._post(payload)
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.headers.get("cache-control"), "no-store")
        self.assertTrue(response.json()["success"])
        runner.assert_called_once_with(
            {"user_id": "user-1", "username": "alice", "role": "viewer", "is_active": True},
            "launcher-test-005",
            payload["settings"],
            saved_config_snapshot=self.storage.default_ai_config,
        )

    def test_request_id_dedupes_and_unknown_timeout_result_is_preserved(self):
        ai_health._AI_MANUAL_TEST_RESULTS.clear()
        ai_health._AI_MANUAL_TEST_INFLIGHT.clear()
        ai_health._AI_MANUAL_TEST_FINGERPRINTS.clear()
        ai_health._AI_MANUAL_TEST_CALLS.clear()
        ai_health._AI_HEALTH_CACHE.clear()
        calls = []

        async def fake_provider(_user, _settings, *, saved_config_snapshot=None):
            calls.append("attempt")
            return {"status": "unknown", "message": "超时，是否处理未知", "latency_ms": 5, "checked_at": "fixture"}

        payload = self._payload("launcher-test-006")
        with patch.object(ai_health, "_perform_portable_ai_manual_test", fake_provider):
            first = self._post(payload)
            second = self._post(payload)
        self.assertEqual(first.status_code, 200)
        self.assertFalse(first.json()["success"])
        self.assertEqual(first.json()["test"]["status"], "unknown")
        self.assertEqual(first.json(), second.json())
        self.assertEqual(calls, ["attempt"])
        self.assertEqual(ai_health._AI_HEALTH_CACHE, {})

    def test_same_launcher_request_id_with_different_draft_returns_409_without_second_provider(self):
        ai_health._AI_MANUAL_TEST_RESULTS.clear()
        ai_health._AI_MANUAL_TEST_INFLIGHT.clear()
        ai_health._AI_MANUAL_TEST_FINGERPRINTS.clear()
        ai_health._AI_MANUAL_TEST_CALLS.clear()
        calls = []

        async def fake_provider(_user, settings, *, saved_config_snapshot=None):
            calls.append(dict(settings or {}))
            return {"status": "success", "message": "fixture"}

        first_payload = self._payload(
            "launcher-same-id-01",
            settings={"OPENAI_BASE_URL": "https://provider.example/v1", "OPENAI_MODEL_NAME": "first-model", "OPENAI_API_KEY": "first-secret"},
        )
        changed_payload = self._payload(
            "launcher-same-id-01",
            settings={"OPENAI_BASE_URL": "https://changed.example/v1", "OPENAI_MODEL_NAME": "second-model", "OPENAI_API_KEY": "second-secret"},
        )
        with patch.object(ai_health, "_perform_portable_ai_manual_test", fake_provider):
            first = self._post(first_payload)
            second = self._post(changed_payload)
        self.assertEqual(first.status_code, 200)
        self.assertEqual(second.status_code, 409)
        self.assertNotIn("first-secret", second.text)
        self.assertNotIn("second-secret", second.text)
        self.assertEqual(calls, [first_payload["settings"]])

    def test_stale_confirmations_reject_before_runner_for_every_config_identity_change(self):
        original = dict(self.storage.default_ai_config)
        changed_configs = (
            {**original, "api_base_url": "https://changed.example/v1", "config_revision": 5},
            {**original, "model": "changed-model", "config_revision": 5},
            {**original, "api_key": "changed-secret", "config_revision": 5},
            {**original, "id": "recreated-config", "config_revision": 4},
            {**original, "id": "new-default", "config_revision": 9},
            None,
        )
        expected_id = original["id"]
        expected_revision = original["config_revision"]
        for index, changed in enumerate(changed_configs):
            with self.subTest(changed=changed):
                self.storage.default_ai_config = changed
                payload = self._payload(
                    f"launcher-stale-{index:02d}", config_id=expected_id, revision=expected_revision,
                )
                with patch.object(ai_health, "run_portable_ai_manual_test") as runner, \
                     patch.object(ai_health.socket, "getaddrinfo") as dns, \
                     patch.object(ai_health.httpx, "AsyncClient") as http:
                    response = self._post(payload)
                self.assertEqual(response.status_code, 409)
                self.assertNotIn("fixture-secret", response.text)
                runner.assert_not_called()
                dns.assert_not_called()
                http.assert_not_called()

    def test_no_config_sentinel_conflicts_if_config_is_created_after_confirmation(self):
        self.storage.default_ai_config = None
        payload = self._payload("launcher-unconfigured-01", config_id="", revision=0)
        self.storage.default_ai_config = {
            "id": "created-after-preview", "config_revision": 1,
            "api_key": "new-secret", "api_base_url": "https://provider.example/v1", "model": "fixture-model",
        }
        with patch.object(ai_health, "run_portable_ai_manual_test") as runner:
            response = self._post(payload)
        self.assertEqual(response.status_code, 409)
        runner.assert_not_called()

    def test_confirmed_snapshot_is_the_only_saved_config_used_by_runner(self):
        frozen = dict(self.storage.default_ai_config)
        self.storage.default_ai_config = {
            **frozen, "id": "later-config", "config_revision": 5,
            "api_key": "later-secret", "api_base_url": "https://later.example/v1", "model": "later-model",
        }
        settings = {"OPENAI_MODEL_NAME": "confirmed-draft-model"}

        async def run():
            with patch.object(ai_health, "get_storage", side_effect=AssertionError("snapshot must be used")), \
                 patch.object(ai_health, "_send_manual_ai_test", return_value={"status": "success"}) as sender:
                result = await ai_health._perform_portable_ai_manual_test(
                    {"user_id": "user-1"}, settings, saved_config_snapshot=frozen,
                )
            return result, sender

        result, sender = __import__("asyncio").run(run())
        self.assertEqual(result["status"], "success")
        sender.assert_called_once()
        target = sender.call_args.args[0]
        self.assertEqual(target["base_url"], "https://provider.example/v1")
        self.assertEqual(target["model_name"], "confirmed-draft-model")
        self.assertEqual(target["api_key"], "fixture-secret")

    def test_launcher_token_cannot_reach_web_ai_test_endpoint(self):
        response = self.client.post(
            "/api/settings/ai/test",
            headers=self._headers(token=self.token),
            json=self._payload("launcher-test-007"),
        )
        self.assertNotEqual(response.status_code, 200)


if __name__ == "__main__":
    unittest.main()
