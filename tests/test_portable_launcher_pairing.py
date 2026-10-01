from datetime import datetime, timedelta, timezone
from concurrent.futures import ThreadPoolExecutor
from types import SimpleNamespace
import unittest

from tests._ci_guard import needs_windows
from unittest.mock import patch, AsyncMock
from fastapi import HTTPException
from fastapi import FastAPI
from fastapi.testclient import TestClient
from starlette.requests import Request

from src.portable.launcher_pairing import (
    LAUNCHER_SESSION_TTL_SECONDS,
    LauncherPairingRegistry,
    PairingError,
    _digest,
)
from src.portable.sessions import read_session
import src.portable.launcher_pairing as pairing_module
from src.portable.web_runtime import _setup_request_origin_is_valid


class FakeStorage:
    def __init__(self):
        self.sessions = {}
        self.users = {
            "user-1": {"id": "user-1", "username": "alice", "is_active": True}
        }
        self.next_id = 0
        self.allow_ai = True

    def get_session_by_token(self, token_hash):
        item = self.sessions.get(token_hash)
        if not item or item["expires_at"] <= datetime.now(timezone.utc):
            return None
        return dict(item)

    def create_session(self, session_data):
        self.next_id += 1
        item = {**session_data, "id": f"session-{self.next_id}"}
        self.sessions[item["token_hash"]] = item
        return dict(item)

    def get_user_by_id(self, user_id):
        return self.users.get(user_id)

    def get_user_groups(self, user_id):
        if not self.allow_ai:
            return []
        return [{"permissions": [{"category": "ai", "enabled": True}]}]

    def delete_session(self, session_id):
        for token_hash, item in list(self.sessions.items()):
            if item["id"] == session_id:
                del self.sessions[token_hash]
                return True
        return False


@needs_windows
class LauncherPairingRegistryTests(unittest.TestCase):
    def setUp(self):
        self.registry = LauncherPairingRegistry()
        self.storage = FakeStorage()
        self.source_token = "p1." + ("S" * 43)
        self.source_digest = _digest(self.source_token)
        self.storage.sessions[self.source_digest] = {
            "id": "source-session",
            "user_id": "user-1",
            "token_hash": self.source_digest,
            "user_agent": "browser",
            "expires_at": datetime.now(timezone.utc) + timedelta(days=1),
        }
        self.verifier = "V" * 43
        storage_patch = patch("src.web.auth.get_storage", return_value=self.storage)
        storage_patch.start()
        self.addCleanup(storage_patch.stop)
        multi_user_patch = patch("src.web.auth.is_multi_user_mode", return_value=True)
        multi_user_patch.start()
        self.addCleanup(multi_user_patch.stop)

    def _approved_pairing(self, instance_id="instance-a"):
        created = self.registry.create(instance_id=instance_id, verifier=self.verifier)
        self.registry.preview(code=created["pairing_code"], username="alice", can_write_ai=True)
        self.registry.approve(
            code=created["pairing_code"],
            user_id="user-1",
            username="alice",
            source_session_token=self.source_token,
            can_write_ai=True,
        )
        return created

    def test_pairing_code_alone_cannot_exchange_and_verifier_is_one_time(self):
        created = self._approved_pairing()
        with self.assertRaises(PairingError):
            self.registry.exchange(
                pairing_id=created["pairing_id"], verifier="W" * 43,
                instance_id="instance-a", storage=self.storage,
            )
        exchanged = self.registry.exchange(
            pairing_id=created["pairing_id"], verifier=self.verifier,
            instance_id="instance-a", storage=self.storage,
        )
        self.assertEqual(exchanged["status"], "exchanged")
        self.assertEqual(exchanged["token_type"], "Bearer")
        self.assertTrue(exchanged["access_token"].startswith("l1."))
        self.assertNotIn(exchanged["access_token"], repr(self.registry._bindings))
        self.assertIsNone(read_session(self.storage, exchanged["access_token"]))
        with self.assertRaises(PairingError):
            self.registry.exchange(
                pairing_id=created["pairing_id"], verifier=self.verifier,
                instance_id="instance-a", storage=self.storage,
            )

    def test_scoped_token_is_instance_bound_and_source_session_bound(self):
        created = self._approved_pairing()
        token = self.registry.exchange(
            pairing_id=created["pairing_id"], verifier=self.verifier,
            instance_id="instance-a", storage=self.storage,
        )["access_token"]
        self.assertEqual(
            self.registry.authenticate(token=token, instance_id="instance-a", storage=self.storage)["username"],
            "alice",
        )
        self.assertIsNone(self.registry.authenticate(token=token, instance_id="instance-b", storage=self.storage))
        self.registry.invalidate_source_session(self.source_token)
        self.assertIsNone(self.registry.authenticate(token=token, instance_id="instance-a", storage=self.storage))

    def test_unbound_token_and_disabled_user_are_rejected(self):
        unbound = "l1." + ("A" * 43)
        self.assertIsNone(self.registry.authenticate(token=unbound, instance_id="instance-a", storage=self.storage))
        created = self._approved_pairing()
        token = self.registry.exchange(
            pairing_id=created["pairing_id"], verifier=self.verifier,
            instance_id="instance-a", storage=self.storage,
        )["access_token"]
        self.storage.users["user-1"]["is_active"] = False
        self.assertIsNone(self.registry.authenticate(token=token, instance_id="instance-a", storage=self.storage))

    def test_source_session_expiry_invalidates_launcher_session(self):
        created = self._approved_pairing()
        token = self.registry.exchange(
            pairing_id=created["pairing_id"], verifier=self.verifier,
            instance_id="instance-a", storage=self.storage,
        )["access_token"]
        self.storage.sessions[self.source_digest]["expires_at"] = datetime.now(timezone.utc) - timedelta(seconds=1)
        self.assertIsNone(self.registry.authenticate(token=token, instance_id="instance-a", storage=self.storage))

    def test_exchange_rechecks_current_user_and_never_uses_requested_identity(self):
        created = self._approved_pairing()
        self.storage.users["user-1"]["username"] = "renamed"
        token = self.registry.exchange(
            pairing_id=created["pairing_id"], verifier=self.verifier,
            instance_id="instance-a", storage=self.storage,
        )["access_token"]
        self.assertEqual(
            self.registry.authenticate(token=token, instance_id="instance-a", storage=self.storage)["username"],
            "renamed",
        )

    def test_explicit_revoke_removes_scoped_session(self):
        created = self._approved_pairing()
        token = self.registry.exchange(
            pairing_id=created["pairing_id"], verifier=self.verifier,
            instance_id="instance-a", storage=self.storage,
        )["access_token"]
        self.registry.revoke(token=token, instance_id="instance-a")
        self.assertIsNone(self.registry.authenticate(token=token, instance_id="instance-a", storage=self.storage))

    def test_exchange_rechecks_permission_after_browser_approval(self):
        created = self._approved_pairing()
        self.storage.allow_ai = False
        with self.assertRaises(PairingError):
            self.registry.exchange(
                pairing_id=created["pairing_id"], verifier=self.verifier,
                instance_id="instance-a", storage=self.storage,
            )

    def test_narrow_auth_requires_exact_loopback_origin_and_bound_token(self):
        created = self._approved_pairing()
        token = self.registry.exchange(
            pairing_id=created["pairing_id"], verifier=self.verifier,
            instance_id="instance-a", storage=self.storage,
        )["access_token"]

        def request_for(origin, bearer=token, instance="instance-a"):
            return Request({
                "type": "http", "method": "GET", "scheme": "http",
                "server": ("127.0.0.1", 43127), "client": ("127.0.0.1", 50100),
                "path": "/api/launcher/me", "query_string": b"",
                "headers": [
                    (b"host", b"127.0.0.1:43127"),
                    (b"origin", origin.encode("ascii")),
                    (b"authorization", f"Bearer {bearer}".encode("ascii")),
                    (b"x-goofish-instance-id", instance.encode("ascii")),
                ],
            })

        with patch.object(pairing_module, "registry", self.registry), \
             patch.object(pairing_module, "portable_mode", return_value=True), \
             patch.object(pairing_module, "STORAGE_BACKEND", return_value="postgres"), \
             patch.object(pairing_module, "get_storage", return_value=self.storage):
            self.assertEqual(
                pairing_module._launcher_user(request_for("http://127.0.0.1:43127"))["username"],
                "alice",
            )
            with self.assertRaises(HTTPException) as wrong_origin:
                pairing_module._launcher_user(request_for("http://attacker.example"))
            self.assertEqual(wrong_origin.exception.status_code, 403)
            with self.assertRaises(HTTPException) as wrong_instance:
                pairing_module._launcher_user(request_for(
                    "http://127.0.0.1:43127", instance="instance-b"
                ))
            self.assertEqual(wrong_instance.exception.status_code, 401)

    def test_expiry_is_bounded_by_browser_session_and_launcher_ttl(self):
        created = self.registry.create(instance_id="instance-a", verifier=self.verifier)
        near_expiry = datetime.now(timezone.utc) + timedelta(minutes=2)
        self.storage.sessions[self.source_digest]["expires_at"] = near_expiry
        self.registry.approve(
            code=created["pairing_code"], user_id="user-1", username="alice",
            source_session_token=self.source_token, can_write_ai=True,
        )
        issued = self.registry.exchange(
            pairing_id=created["pairing_id"], verifier=self.verifier,
            instance_id="instance-a", storage=self.storage,
        )
        self.assertLessEqual(datetime.fromisoformat(issued["expires_at"]), near_expiry)
        self.assertLessEqual(
            datetime.fromisoformat(issued["expires_at"]),
            datetime.now(timezone.utc) + timedelta(seconds=LAUNCHER_SESSION_TTL_SECONDS + 1),
        )

    def test_preview_and_approval_require_ai_write_permission(self):
        created = self.registry.create(instance_id="instance-a", verifier=self.verifier)
        with self.assertRaises(PairingError):
            self.registry.preview(code=created["pairing_code"], username="alice", can_write_ai=False)
        with self.assertRaises(PairingError):
            self.registry.approve(
                code=created["pairing_code"], user_id="user-1", username="alice",
                source_session_token=self.source_token, can_write_ai=False,
            )

    def test_web_restart_invalidates_l1_and_token_is_not_in_storage(self):
        created = self._approved_pairing()
        token = self.registry.exchange(
            pairing_id=created["pairing_id"], verifier=self.verifier,
            instance_id="instance-a", storage=self.storage,
        )["access_token"]
        restarted_registry = LauncherPairingRegistry()
        self.assertIsNone(restarted_registry.authenticate(token=token, instance_id="instance-a", storage=self.storage))
        self.assertNotIn(_digest(token), self.storage.sessions)

    def test_reauthorization_replaces_existing_session_and_stays_bounded(self):
        first = self._approved_pairing()
        first_token = self.registry.exchange(
            pairing_id=first["pairing_id"], verifier=self.verifier,
            instance_id="instance-a", storage=self.storage,
        )["access_token"]
        second = self._approved_pairing()
        second_token = self.registry.exchange(
            pairing_id=second["pairing_id"], verifier=self.verifier,
            instance_id="instance-a", storage=self.storage,
        )["access_token"]
        self.assertNotEqual(first_token, second_token)
        self.assertIsNone(self.registry.authenticate(token=first_token, instance_id="instance-a", storage=self.storage))
        self.assertIsNotNone(self.registry.authenticate(token=second_token, instance_id="instance-a", storage=self.storage))
        self.assertEqual(len(self.registry._bindings), 1)
        self.assertEqual(set(self.storage.sessions), {self.source_digest})

    def test_concurrent_exchange_is_one_time(self):
        created = self._approved_pairing()
        def exchange_once():
            try:
                return self.registry.exchange(
                    pairing_id=created["pairing_id"], verifier=self.verifier,
                    instance_id="instance-a", storage=self.storage,
                )
            except PairingError:
                return None

        with ThreadPoolExecutor(max_workers=8) as pool:
            results = list(pool.map(lambda _index: exchange_once(), range(8)))
        successful = [item for item in results if item is not None]
        self.assertEqual(len(successful), 1)

    def test_global_active_session_bound_rejects_fifth_instance(self):
        for index in range(pairing_module.MAX_ACTIVE_SESSIONS):
            instance_id = f"instance-{index}"
            created = self._approved_pairing(instance_id)
            self.registry.exchange(
                pairing_id=created["pairing_id"], verifier=self.verifier,
                instance_id=instance_id, storage=self.storage,
            )
        fifth = self._approved_pairing("instance-overflow")
        with self.assertRaises(PairingError):
            self.registry.exchange(
                pairing_id=fifth["pairing_id"], verifier=self.verifier,
                instance_id="instance-overflow", storage=self.storage,
            )
        self.assertEqual(len(self.registry._bindings), pairing_module.MAX_ACTIVE_SESSIONS)

    def test_expired_registry_entries_are_cleaned_on_next_operation(self):
        created = self._approved_pairing()
        token = self.registry.exchange(
            pairing_id=created["pairing_id"], verifier=self.verifier,
            instance_id="instance-a", storage=self.storage,
        )["access_token"]
        token_digest = _digest(token)
        self.registry._bindings[token_digest]["expires_at"] = datetime.now(timezone.utc) - timedelta(seconds=1)
        self.registry.create(instance_id="instance-a", verifier="W" * 43)
        self.assertNotIn(token_digest, self.registry._bindings)

    def test_internal_routes_require_control_context_and_exchange_in_body(self):
        app = FastAPI()
        settings = SimpleNamespace(
            instance_id="instance-a", port=43127, launcher_token="control-secret"
        )
        pairing_module.register_internal_routes(app, settings)
        control_headers = {
            "Authorization": "Bearer control-secret",
            "X-Goofish-Instance-Id": "instance-a",
        }
        with patch.object(pairing_module, "registry", self.registry), \
             patch.object(pairing_module, "portable_mode", return_value=True), \
             patch.object(pairing_module, "STORAGE_BACKEND", return_value="postgres"), \
             patch.object(pairing_module, "get_storage", return_value=self.storage), \
             TestClient(app, base_url="http://127.0.0.1:43127") as client:
            denied = client.post("/internal/launcher/pairing", json={
                "instance_id": "instance-a", "verifier": self.verifier,
            })
            self.assertEqual(denied.status_code, 401)
            wrong_origin = client.post("/internal/launcher/pairing", headers={
                **control_headers, "Origin": "http://127.0.0.1:43127",
            }, json={"instance_id": "instance-a", "verifier": self.verifier})
            self.assertEqual(wrong_origin.status_code, 403)
            wrong_host = client.post("/internal/launcher/pairing", headers={
                **control_headers, "Host": "attacker.example:43127",
            }, json={"instance_id": "instance-a", "verifier": self.verifier})
            self.assertEqual(wrong_host.status_code, 403)
            created = client.post(
                "/internal/launcher/pairing", headers=control_headers,
                json={"instance_id": "instance-a", "verifier": self.verifier},
            )
            self.assertEqual(created.status_code, 200)
            self.assertEqual(created.headers.get("cache-control"), "no-store")
            pairing = created.json()
            self.registry.approve(
                code=pairing["pairing_code"], user_id="user-1", username="alice",
                source_session_token=self.source_token, can_write_ai=True,
            )
            exchanged = client.post(
                "/internal/launcher/pairing/exchange", headers=control_headers,
                json={"pairing_id": pairing["pairing_id"], "verifier": self.verifier},
            )
            self.assertEqual(exchanged.status_code, 200)
            self.assertTrue(exchanged.json()["access_token"].startswith("l1."))

    def test_ai_tokens_route_uses_authenticated_user_and_existing_cas_service(self):
        app = FastAPI()
        app.include_router(pairing_module.router)
        user = {"user_id": "user-1", "username": "alice"}
        settings = {"AI_MAX_TOKENS_PARAM_NAME": "max_completion_tokens", "AI_MAX_TOKENS_LIMIT": 8192,
                    "OPENAI_API_KEY": "never-return-secret", "PROXY_URL": "private", "ENABLE_THINKING": True}
        saved = {"config_id": "config-a", "config_revision": 8, "OPENAI_API_KEY": "never-return-secret"}
        with patch.object(pairing_module, "_launcher_user", return_value=user), \
             patch("src.web.settings_manager._require_ai_access"), \
             patch("src.web.settings_manager._require_ai_or_tasks_access"), \
             patch("src.web.settings_manager.get_ai_settings", new_callable=AsyncMock, return_value=settings) as read, \
             patch("src.web.settings_manager.update_ai_settings", new_callable=AsyncMock, return_value=saved) as update, \
             TestClient(app) as client:
            response = client.get("/api/launcher/ai")
            self.assertEqual(response.json(), {key: settings[key] for key in ("AI_MAX_TOKENS_PARAM_NAME", "AI_MAX_TOKENS_LIMIT")})
            read.assert_awaited_once_with(user)
            payload = {"config_id": "config-a", "config_revision": 7,
                       "AI_MAX_TOKENS_PARAM_NAME": "max_completion_tokens", "AI_MAX_TOKENS_LIMIT": 8192}
            response = client.put("/api/launcher/ai", json=payload)
            self.assertEqual(response.status_code, 200)
            self.assertNotIn("OPENAI_API_KEY", response.json())
            update.assert_awaited_once_with(payload, user)
            for field, value in (("AI_MAX_TOKENS_LIMIT", True), ("AI_MAX_TOKENS_LIMIT", 0),
                                 ("AI_MAX_TOKENS_LIMIT", "8192"), ("AI_MAX_TOKENS_LIMIT", 2147483648),
                                 ("AI_MAX_TOKENS_PARAM_NAME", "bad field"), ("AI_MAX_TOKENS_PARAM_NAME", "x" * 65),
                                 ("AI_MAX_TOKENS_PARAM_NAME", "valid\n"), ("AI_MAX_TOKENS_PARAM_NAME", None)):
                with self.subTest(field=field, value=value):
                    self.assertEqual(client.put("/api/launcher/ai", json={**payload, field: value}).status_code, 422)
            self.assertEqual(update.await_count, 1)
            for forbidden in ("ENABLE_THINKING", "ENABLE_RESPONSE_FORMAT", "AI_VISION_ENABLED", "user_id"):
                self.assertEqual(client.put("/api/launcher/ai", json={**payload, forbidden: True}).status_code, 422)
            self.assertEqual(update.await_count, 1)

    def test_browser_approval_page_displays_scope_and_requires_explicit_post(self):
        app = FastAPI()
        app.include_router(pairing_module.router)
        created = self.registry.create(instance_id="instance-a", verifier=self.verifier)
        with patch.object(pairing_module, "registry", self.registry), \
             patch.object(pairing_module, "_ensure_portable_postgres"), \
             patch.object(pairing_module, "get_current_user", return_value={
                 "user_id": "user-1", "username": "alice", "role": "operator", "is_active": True,
             }), \
             TestClient(app, base_url="http://127.0.0.1:43127") as client:
            client.cookies.set("session_token", self.source_token)
            page = client.get("/api/launcher/authorize")
            self.assertEqual(page.status_code, 200)
            self.assertEqual(page.headers.get("cache-control"), "no-store")
            self.assertIn("alice", page.text)
            self.assertEqual(page.url.query, b"")
            preview = client.post("/api/launcher/authorize/preview", data={
                "pairing_code": created["pairing_code"],
            })
            self.assertEqual(preview.status_code, 200)
            self.assertIn("instance-a", preview.text)
            self.assertIn("读取和编辑当前用户默认 AI 连接", preview.text)
            rejected = client.post("/api/launcher/authorize/approve", data={
                "pairing_code": created["pairing_code"],
            })
            self.assertEqual(rejected.status_code, 422)
            approved = client.post("/api/launcher/authorize/approve", data={
                "pairing_code": created["pairing_code"], "confirm": "approve",
            })
            self.assertEqual(approved.status_code, 200)

    def test_browser_csrf_origin_requires_exact_loopback_host_and_origin(self):
        settings = SimpleNamespace(port=43127, loopback_origin="http://127.0.0.1:43127")

        def request_for(host, origin):
            headers = [(b"host", host.encode("ascii"))]
            if origin is not None:
                headers.append((b"origin", origin.encode("ascii")))
            return Request({
                "type": "http", "method": "POST", "scheme": "http",
                "server": ("127.0.0.1", 43127), "client": ("127.0.0.1", 50100),
                "path": "/api/launcher/authorize/approve", "query_string": b"",
                "headers": headers,
            })

        self.assertTrue(_setup_request_origin_is_valid(
            request_for("127.0.0.1:43127", "http://127.0.0.1:43127"), settings
        ))
        self.assertFalse(_setup_request_origin_is_valid(
            request_for("127.0.0.1:43127", "http://attacker.example"), settings
        ))
        self.assertFalse(_setup_request_origin_is_valid(
            request_for("127.0.0.1:43127", None), settings
        ))
        self.assertFalse(_setup_request_origin_is_valid(
            request_for("attacker.example:43127", "http://127.0.0.1:43127"), settings
        ))


if __name__ == "__main__":
    unittest.main()
