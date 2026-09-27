"""Bounded portable manual AI test behavior without provider network calls."""

import asyncio
import unittest
from unittest import mock

import httpx
from fastapi import HTTPException

from src.web import ai_health, ai_manager


class _FakeResponse:
    status_code = 200

    async def aiter_bytes(self):
        yield b'{"choices":[{"message":{"content":"OK"}}]}'


class _FakeStream:
    async def __aenter__(self):
        return _FakeResponse()

    async def __aexit__(self, *_args):
        return False


class _FakeClient:
    instances = []

    def __init__(self, **kwargs):
        self.kwargs = kwargs
        self.calls = []
        self.instances.append(self)

    async def __aenter__(self):
        return self

    async def __aexit__(self, *_args):
        return False

    def stream(self, method, url, **kwargs):
        self.calls.append((method, url, kwargs))
        return _FakeStream()


class _TimeoutStream:
    async def __aenter__(self):
        raise httpx.ReadTimeout("private test detail")

    async def __aexit__(self, *_args):
        return False


class _TimeoutClient(_FakeClient):
    def stream(self, method, url, **kwargs):
        self.calls.append((method, url, kwargs))
        return _TimeoutStream()


class PortableAiManualTestTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        ai_health._AI_MANUAL_TEST_RESULTS.clear()
        ai_health._AI_MANUAL_TEST_INFLIGHT.clear()
        ai_health._AI_MANUAL_TEST_FINGERPRINTS.clear()
        ai_health._AI_MANUAL_TEST_CALLS.clear()
        ai_health._AI_HEALTH_CACHE.clear()
        _FakeClient.instances.clear()
        self.user = {"id": "portable-test-user", "user_id": "portable-test-user"}
        self.config = {
            "backend": "postgres", "source": "test", "owner_id": "portable-test-user",
            "api_key": "test-secret", "base_url": "https://8.8.8.8/v1",
            "model_name": "fake-model", "proxy_ai_enabled": False,
        }
        self.resolver = mock.patch.object(ai_health, "_resolve_effective_ai_config", side_effect=self._resolve_config)
        self.resolver.start()
        self.addCleanup(self.resolver.stop)

    def _resolve_config(self, _user, overrides=None, **_kwargs):
        config = dict(self.config)
        overrides = overrides or {}
        for key, source in (("api_key", "OPENAI_API_KEY"), ("base_url", "OPENAI_BASE_URL"), ("model_name", "OPENAI_MODEL_NAME")):
            if overrides.get(source):
                config[key] = overrides[source]
        return config

    async def test_confirmed_test_is_single_fixed_request_and_does_not_touch_health_cache(self):
        with mock.patch.object(ai_manager.src.config, "PORTABLE_MODE", True), mock.patch.object(
            ai_health, "_current_postgres_ai_config", return_value={},
        ), mock.patch.object(
            ai_health.httpx, "AsyncClient", _FakeClient
        ), mock.patch.object(ai_health.socket, "getaddrinfo", return_value=[(None, None, None, None, ("8.8.8.8", 443))]):
            result = await ai_manager.test_ai_settings({
                "confirmed": True, "request_id": "stable-id-0001", "settings": {},
            }, user=self.user)

        self.assertTrue(result["success"])
        self.assertEqual(len(_FakeClient.instances[0].calls), 1)
        call = _FakeClient.instances[0].calls[0]
        self.assertEqual(call[0], "POST")
        self.assertEqual(call[2]["json"]["messages"], [{"role": "user", "content": "Reply with OK."}])
        self.assertEqual(call[2]["json"]["max_tokens"], 10)
        self.assertEqual(call[2]["json"]["stream"], False)
        self.assertFalse(_FakeClient.instances[0].kwargs["follow_redirects"])
        self.assertIsInstance(_FakeClient.instances[0].kwargs["transport"], httpx.AsyncHTTPTransport)
        self.assertEqual(_FakeClient.instances[0].kwargs["transport"]._pool._retries, 0)
        pinned = _FakeClient.instances[0].kwargs["transport"]._pool._network_backend
        self.assertIsInstance(pinned, ai_health._PinnedNetworkBackend)
        self.assertEqual(pinned._address, "8.8.8.8")
        self.assertFalse(ai_health._AI_HEALTH_CACHE)

    async def test_duplicate_request_id_returns_saved_result_without_second_request(self):
        with mock.patch.object(ai_health.httpx, "AsyncClient", _FakeClient), mock.patch.object(
            ai_health.socket, "getaddrinfo", return_value=[(None, None, None, None, ("8.8.8.8", 443))]
        ):
            first = await ai_health.run_portable_ai_manual_test(self.user, "stable-id-0002", {})
            second = await ai_health.run_portable_ai_manual_test(self.user, "stable-id-0002", {})
        self.assertEqual(first, second)
        self.assertEqual(len(_FakeClient.instances), 1)
        self.assertEqual(len(_FakeClient.instances[0].calls), 1)

    async def test_same_request_id_with_changed_draft_target_or_key_is_rejected_before_second_send(self):
        changed_drafts = (
            {"OPENAI_BASE_URL": "https://1.1.1.1/v1", "OPENAI_MODEL_NAME": "draft-model", "OPENAI_API_KEY": "draft-key"},
            {"OPENAI_BASE_URL": "https://8.8.8.8/v1", "OPENAI_MODEL_NAME": "changed-model", "OPENAI_API_KEY": "draft-key"},
            {"OPENAI_BASE_URL": "https://8.8.8.8/v1", "OPENAI_MODEL_NAME": "draft-model", "OPENAI_API_KEY": "changed-key"},
            {"OPENAI_BASE_URL": "   ", "OPENAI_MODEL_NAME": "draft-model", "OPENAI_API_KEY": "draft-key"},
            {"OPENAI_BASE_URL": "https://8.8.8.8/v1", "OPENAI_MODEL_NAME": "   ", "OPENAI_API_KEY": "draft-key"},
            {"OPENAI_BASE_URL": "https://8.8.8.8/v1", "OPENAI_MODEL_NAME": "draft-model", "OPENAI_API_KEY": "   "},
        )
        original = {"OPENAI_BASE_URL": "https://8.8.8.8/v1", "OPENAI_MODEL_NAME": "draft-model", "OPENAI_API_KEY": "draft-key"}
        for index, changed in enumerate(changed_drafts):
            with self.subTest(changed=changed):
                ai_health._AI_MANUAL_TEST_RESULTS.clear()
                ai_health._AI_MANUAL_TEST_INFLIGHT.clear()
                ai_health._AI_MANUAL_TEST_FINGERPRINTS.clear()
                ai_health._AI_MANUAL_TEST_CALLS.clear()
                _FakeClient.instances.clear()
                with mock.patch.object(ai_health.httpx, "AsyncClient", _FakeClient):
                    first = await ai_health.run_portable_ai_manual_test(
                        self.user, f"draft-target-{index:02d}", original,
                    )
                    second = await ai_health.run_portable_ai_manual_test(
                        self.user, f"draft-target-{index:02d}", changed,
                    )
                self.assertEqual(first["status"], "success")
                self.assertEqual(second["status"], "conflict")
                self.assertEqual(len(_FakeClient.instances), 1)
                self.assertEqual(len(_FakeClient.instances[0].calls), 1)

    async def test_absent_empty_and_null_override_presence_cannot_reuse_a_request_id(self):
        changed_overrides = (
            {"OPENAI_API_KEY": ""},
            {"OPENAI_API_KEY": None},
            {"OPENAI_BASE_URL": ""},
            {"OPENAI_MODEL_NAME": None},
        )
        for index, changed in enumerate(changed_overrides):
            with self.subTest(changed=changed):
                ai_health._AI_MANUAL_TEST_RESULTS.clear()
                ai_health._AI_MANUAL_TEST_INFLIGHT.clear()
                ai_health._AI_MANUAL_TEST_FINGERPRINTS.clear()
                ai_health._AI_MANUAL_TEST_CALLS.clear()
                _FakeClient.instances.clear()
                with mock.patch.object(ai_health.httpx, "AsyncClient", _FakeClient):
                    first = await ai_health.run_portable_ai_manual_test(
                        self.user, f"override-presence-{index:02d}", {},
                    )
                    second = await ai_health.run_portable_ai_manual_test(
                        self.user, f"override-presence-{index:02d}", changed,
                    )
                self.assertEqual(first["status"], "success")
                self.assertEqual(second["status"], "conflict")
                self.assertEqual(len(_FakeClient.instances), 1)
                self.assertEqual(len(_FakeClient.instances[0].calls), 1)

    async def test_fingerprint_is_user_scoped_and_binds_config_identity(self):
        calls = []

        async def fake_provider(_user, _settings, *, saved_config_snapshot=None):
            calls.append("provider")
            return {"status": "success", "message": "fixture"}

        first_snapshot = {"id": "config-a", "config_revision": 7}
        replacement_snapshot = {"id": "config-b", "config_revision": 7}
        with mock.patch.object(ai_health, "_perform_portable_ai_manual_test", fake_provider):
            first = await ai_health.run_portable_ai_manual_test(
                self.user, "same-id-scope-01", {}, saved_config_snapshot=first_snapshot,
            )
            same_user_changed_config = await ai_health.run_portable_ai_manual_test(
                self.user, "same-id-scope-01", {}, saved_config_snapshot=replacement_snapshot,
            )
            other_user = {"id": "other-user", "user_id": "other-user"}
            other_user_result = await ai_health.run_portable_ai_manual_test(
                other_user, "same-id-scope-01", {}, saved_config_snapshot=first_snapshot,
            )
        self.assertEqual(first["status"], "success")
        self.assertEqual(same_user_changed_config["status"], "conflict")
        self.assertEqual(other_user_result["status"], "success")
        self.assertEqual(calls, ["provider", "provider"])

    async def test_concurrent_same_id_different_payload_dispatches_only_first(self):
        entered = asyncio.Event()
        release = asyncio.Event()
        calls = []

        async def fake_provider(_user, _settings, *, saved_config_snapshot=None):
            calls.append("provider")
            entered.set()
            await release.wait()
            return {"status": "success", "message": "fixture"}

        with mock.patch.object(ai_health, "_perform_portable_ai_manual_test", fake_provider):
            first_task = asyncio.create_task(
                ai_health.run_portable_ai_manual_test(self.user, "concurrent-same-id", {"OPENAI_API_KEY": "first"})
            )
            await entered.wait()
            second = await ai_health.run_portable_ai_manual_test(
                self.user, "concurrent-same-id", {"OPENAI_API_KEY": "second"},
            )
            release.set()
            first = await first_task
        self.assertEqual(first["status"], "success")
        self.assertEqual(second["status"], "conflict")
        self.assertEqual(calls, ["provider"])

    async def test_rate_limited_request_id_is_not_bound_until_it_is_queued(self):
        calls = []

        async def fake_provider(_user, _settings, *, saved_config_snapshot=None):
            calls.append("provider")
            return {"status": "success", "message": "fixture"}

        with mock.patch.object(ai_health, "_perform_portable_ai_manual_test", fake_provider):
            for index in range(5):
                await ai_health.run_portable_ai_manual_test(self.user, f"rate-seed-{index:02d}", {})
            rate_limited = await ai_health.run_portable_ai_manual_test(
                self.user, "rate-retry-01", {"OPENAI_MODEL_NAME": "first"},
            )
            ai_health._AI_MANUAL_TEST_CALLS["portable-test-user"].clear()
            retried = await ai_health.run_portable_ai_manual_test(
                self.user, "rate-retry-01", {"OPENAI_MODEL_NAME": "second"},
            )
        self.assertEqual(rate_limited["status"], "rate_limited")
        self.assertEqual(retried["status"], "success")
        self.assertEqual(len(calls), 6)

    async def test_timeout_is_unknown_and_deduplicated(self):
        with mock.patch.object(ai_health.httpx, "AsyncClient", _TimeoutClient), mock.patch.object(
            ai_health.socket, "getaddrinfo", return_value=[(None, None, None, None, ("8.8.8.8", 443))]
        ):
            first = await ai_health.run_portable_ai_manual_test(self.user, "stable-id-0003", {})
            second = await ai_health.run_portable_ai_manual_test(self.user, "stable-id-0003", {})
        self.assertEqual(first["status"], "unknown")
        self.assertIn("未知", first["message"])
        self.assertEqual(first, second)
        self.assertNotIn("private test detail", first["message"])
        self.assertEqual(len(_TimeoutClient.instances[0].calls), 1)

    async def test_http_cancellation_keeps_same_provider_attempt_and_request_id(self):
        entered = asyncio.Event()
        release = asyncio.Event()
        calls = []

        async def fake_provider(_user, _settings):
            calls.append("sent")
            entered.set()
            await release.wait()
            return {"status": "success", "message": "fixture", "latency_ms": 1, "checked_at": "fixture"}

        with mock.patch.object(ai_health, "_perform_portable_ai_manual_test", fake_provider):
            first = asyncio.create_task(ai_health.run_portable_ai_manual_test(self.user, "stable-id-cancel", {}))
            await entered.wait()
            first.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await first
            second = asyncio.create_task(ai_health.run_portable_ai_manual_test(self.user, "stable-id-cancel", {}))
            await asyncio.sleep(0)
            self.assertEqual(calls, ["sent"])
            release.set()
            result = await second
            await asyncio.sleep(0)
            repeated = await ai_health.run_portable_ai_manual_test(self.user, "stable-id-cancel", {})
        self.assertEqual(result, repeated)
        self.assertEqual(calls, ["sent"])

    async def test_host_change_without_new_key_does_not_reuse_saved_key(self):
        with mock.patch.object(ai_health.httpx, "AsyncClient", _FakeClient):
            result = await ai_health.run_portable_ai_manual_test(self.user, "stable-id-0004", {
                "OPENAI_BASE_URL": "https://new-host.example/v1",
            })
        self.assertEqual(result["status"], "rejected")
        self.assertFalse(_FakeClient.instances)

    async def test_enabled_ai_proxy_is_rejected_without_dns_or_http_egress(self):
        self.config["proxy_ai_enabled"] = True
        self.config["proxy_url"] = "http://user:secret@127.0.0.1:8080"
        with mock.patch.object(ai_health.socket, "getaddrinfo", return_value=[
            (None, None, None, None, ("8.8.8.8", 443)),
        ]) as resolver, mock.patch.object(ai_health.httpx, "AsyncClient", _FakeClient):
            result = await ai_health.run_portable_ai_manual_test(self.user, "proxy-enabled-0001", {})
        self.assertEqual(result["status"], "rejected")
        self.assertIn("未发送请求", result["message"])
        self.assertNotIn("127.0.0.1", result["message"])
        self.assertNotIn("secret", result["message"])
        resolver.assert_not_called()
        self.assertFalse(_FakeClient.instances)

    async def test_manual_test_still_uses_direct_pinned_request_when_proxy_disabled(self):
        self.assertFalse(self.config["proxy_ai_enabled"])
        with mock.patch.object(ai_health.httpx, "AsyncClient", _FakeClient), mock.patch.object(
            ai_health.socket, "getaddrinfo", return_value=[(None, None, None, None, ("8.8.8.8", 443))]
        ):
            result = await ai_health.run_portable_ai_manual_test(self.user, "direct-pinned-0001", {})
        self.assertEqual(result["status"], "success")
        self.assertEqual(len(_FakeClient.instances), 1)
        self.assertIsInstance(
            _FakeClient.instances[0].kwargs["transport"]._pool._network_backend,
            ai_health._PinnedNetworkBackend,
        )

    async def test_private_address_is_rejected_without_request(self):
        self.config["base_url"] = "https://127.0.0.1:9000/v1"
        result = await ai_health._send_manual_ai_test(self.config)
        self.assertEqual(result["status"], "rejected")

    async def test_validated_address_is_pinned_for_tcp_connection(self):
        backend = ai_health._PinnedNetworkBackend("8.8.8.8")

        class _FakeBackend:
            async def connect_tcp(self, host, port, **kwargs):
                self.connected = (host, port, kwargs)
                return object()

        fake = _FakeBackend()
        backend._backend = fake
        await backend.connect_tcp("rebound.example", 443, timeout=1.0)
        self.assertEqual(fake.connected[0:2], ("8.8.8.8", 443))

    async def test_dns_timeout_rejects_before_http_client(self):
        async def blocked_resolver(*_args, **_kwargs):
            await asyncio.sleep(0.05)

        with mock.patch.object(ai_health, "_AI_MANUAL_TEST_DNS_TIMEOUT_SECONDS", 0.001), mock.patch.object(
            ai_health.asyncio, "to_thread", blocked_resolver
        ), mock.patch.object(ai_health.httpx, "AsyncClient", _FakeClient):
            result = await ai_health._send_manual_ai_test(self.config)
        self.assertEqual(result["status"], "rejected")
        self.assertFalse(_FakeClient.instances)

    async def test_unconfirmed_or_invalid_request_id_is_rejected(self):
        with mock.patch.object(ai_manager.src.config, "PORTABLE_MODE", True):
            with self.assertRaises(HTTPException):
                await ai_manager.test_ai_settings({"request_id": "stable-id-0005"}, user=self.user)
            with self.assertRaises(HTTPException):
                await ai_manager.test_ai_settings({"confirmed": True, "request_id": "bad id"}, user=self.user)

    async def test_legacy_multi_probe_routes_are_closed_in_portable_mode(self):
        with mock.patch.object(ai_manager.src.config, "PORTABLE_MODE", True), mock.patch.object(
            ai_manager, "run_ai_health_check"
        ) as health_probe:
            with self.assertRaises(HTTPException) as health_error:
                await ai_manager.run_ai_health({}, user=self.user)
            with self.assertRaises(HTTPException) as backend_error:
                await ai_manager.test_ai_settings_backend(user=self.user)
        self.assertEqual(health_error.exception.status_code, 403)
        self.assertEqual(backend_error.exception.status_code, 403)
        health_probe.assert_not_called()


if __name__ == "__main__":
    unittest.main()
