"""离线验证私有通知保存和 HTTP 测试通知去重。"""

import ast
import asyncio
import copy
from pathlib import Path
from types import SimpleNamespace
from typing import Optional
import unittest
from unittest import mock
from urllib.parse import urlparse

from fastapi import APIRouter, Depends, FastAPI, HTTPException, Request, status
from fastapi.testclient import TestClient
from pydantic import BaseModel, Field

from src.web.models import (
    NotificationRequest,
    TestNotificationRequest,
    TestProductNotificationRequest,
    TestTaskCompletionNotificationRequest,
)
from src.web.notification_test_guard import (
    NotificationTestBusy,
    NotificationTestConflict,
    NotificationTestFailed,
    send_test_once,
)
from src.web import notification_test_guard
from tests.test_upstream_notification_compat import notification_namespace


ROOT = Path(__file__).resolve().parents[1]


def load_nodes(path, names, namespace):
    tree = ast.parse((ROOT / path).read_text(encoding="utf-8"))
    nodes = [node for node in tree.body if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)) and node.name in names]
    assert {node.name for node in nodes} == names
    exec(compile(ast.Module(body=nodes, type_ignores=[]), str(path), "exec"), namespace)


class PrivateNotificationApiTests(unittest.TestCase):
    def setUp(self):
        self.user = {"user_id": "a"}
        self.rows = {
            "a": [
                {"id": "ntfy-a", "channel_type": "ntfy", "name": "Ntfy", "config": {
                    "server_url": "https://old.invalid/Private", "topic": "old", "token": "synthetic-old", "bound_task": "task-a",
                }, "is_enabled": True, "notify_on_complete": True},
                {"id": "gotify-a", "channel_type": "gotify", "name": "Gotify", "config": {
                    "url": "https://push.invalid", "token": "synthetic-gotify", "extra": "keep",
                }, "is_enabled": True, "notify_on_complete": True},
                {"id": "legacy-a", "channel_type": "ntfy", "name": "Old", "config": {
                    "topic_url": "https://:synthetic-legacy@host.invalid/proxy/topic",
                }, "is_enabled": True},
            ],
            "b": [{"id": "ntfy-b", "channel_type": "ntfy", "name": "Other", "config": {"token": "synthetic-b"}}],
        }
        self.saved = []
        storage = SimpleNamespace()
        storage.get_user_notification_configs = lambda owner: copy.deepcopy(self.rows[owner])
        def save(owner, payload):
            self.saved.append((owner, copy.deepcopy(payload)))
            row = next(item for item in self.rows[owner] if item["id"] == payload["id"])
            row.update(copy.deepcopy(payload))
            return copy.deepcopy(row)
        storage.save_user_notification_config = save
        runtime, _ = notification_namespace()
        async def current_user():
            return self.user
        ns = {
            "router": APIRouter(prefix="/api/users"), "Request": Request, "Depends": Depends,
            "HTTPException": HTTPException, "status": status, "BaseModel": BaseModel,
            "Field": Field, "Optional": Optional, "urlparse": urlparse,
            "logger": mock.Mock(), "get_current_user_required": current_user,
            "is_multi_user_mode": lambda: True, "get_storage": lambda: storage,
            "log_audit_action": mock.Mock(),
        }
        parse = runtime["parse_legacy_ntfy_url"]
        with mock.patch.dict("sys.modules", {"src.notifier.config": SimpleNamespace(parse_legacy_ntfy_url=parse)}):
            load_nodes("src/web/user_manager.py", {
                "NotificationConfigUpdate", "_normalize_legacy_ntfy_config_item",
                "_ntfy_config_server_url", "_clear_reused_ntfy_token_on_server_change",
                "_merge_notification_config_update", "get_my_notification_configs",
                "update_my_notification_config",
            }, ns)
        self.ns = ns
        self.parse_patch = mock.patch.dict("sys.modules", {"src.notifier.config": SimpleNamespace(parse_legacy_ntfy_url=parse)})
        self.parse_patch.start()
        self.addCleanup(self.parse_patch.stop)
        app = FastAPI()
        app.include_router(ns["router"])
        self.client = TestClient(app)
        self.addCleanup(self.client.close)

    def put(self, config_id, data):
        return self.client.put(f"/api/users/me/notification-configs/{config_id}", json=data)

    def test_partial_update_preserves_secret_task_and_event_flags(self):
        response = self.put("ntfy-a", {"config": {"topic": "new"}})
        self.assertEqual(response.status_code, 200, response.text)
        self.assertEqual(self.rows["a"][0]["config"], {
            "server_url": "https://old.invalid/Private", "topic": "new",
            "token": "synthetic-old", "bound_task": "task-a",
        })
        self.assertIs(self.rows["a"][0]["notify_on_complete"], True)
        self.assertEqual(self.rows["b"][0]["config"]["token"], "synthetic-b")

    def test_server_or_proxy_path_change_clears_reused_token(self):
        for server in ("https://new.invalid", "https://old.invalid/private"):
            with self.subTest(server=server):
                self.rows["a"][0]["config"]["token"] = "synthetic-old"
                response = self.put("ntfy-a", {"config": {"server_url": server}})
                self.assertEqual(response.status_code, 200, response.text)
                self.assertNotIn("token", self.rows["a"][0]["config"])
                self.rows["a"][0]["config"]["server_url"] = "https://old.invalid/Private"

    def test_explicit_empty_secret_clears_and_missing_secret_preserves(self):
        self.assertEqual(self.put("gotify-a", {"config": {"url": "https://updated.invalid"}}).status_code, 200)
        self.assertEqual(self.rows["a"][1]["config"]["token"], "synthetic-gotify")
        self.assertEqual(self.put("gotify-a", {"config": {"token": ""}}).status_code, 200)
        self.assertEqual(self.rows["a"][1]["config"]["token"], "")
        self.assertEqual(self.rows["a"][1]["config"]["extra"], "keep")

    def test_explicit_clear_removes_legacy_alias_and_task_binding(self):
        self.rows["a"][1]["config"].update({"gotify_token": "synthetic-legacy", "bound_task_name": "old-task"})
        response = self.put("gotify-a", {"config": {"token": "", "bound_task": ""}})
        self.assertEqual(response.status_code, 200, response.text)
        saved = self.rows["a"][1]["config"]
        self.assertEqual(saved["token"], "")
        self.assertEqual(saved["bound_task"], "")
        self.assertNotIn("gotify_token", saved)
        self.assertNotIn("bound_task_name", saved)

    def test_legacy_url_update_canonicalizes_and_cannot_resurrect_token(self):
        response = self.client.get("/api/users/me/notification-configs")
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.json()["configs"][2]["config"]["topic"], "topic")
        self.assertEqual(self.put("legacy-a", {"config": {"server_url": "https://new.invalid"}}).status_code, 200)
        saved = self.rows["a"][2]["config"]
        self.assertEqual(saved["topic"], "topic")
        self.assertEqual(saved["server_url"], "https://new.invalid")
        self.assertNotIn("token", saved)
        self.assertNotIn("topic_url", saved)

    def test_other_owner_cannot_update_config(self):
        self.assertEqual(self.put("ntfy-b", {"config": {"token": "synthetic-hijack"}}).status_code, 404)
        self.assertEqual(self.saved, [])


class NotificationTestApiTests(unittest.TestCase):
    def setUp(self):
        self.user = {"user_id": "a", "categories": ["notify"]}
        self.sent = {name: mock.AsyncMock(return_value=True) for name in ("standard", "completion", "product")}
        async def require_auth():
            return self.user
        ns = {
            "router": APIRouter(), "Depends": Depends, "HTTPException": HTTPException,
            "require_auth": require_auth, "has_category": lambda user, category: category in user.get("categories", []),
            "check_permission": lambda user, permission: False,
            "is_multi_user_mode": lambda: True, "logger": mock.Mock(),
            "NotificationRequest": NotificationRequest,
            "TestNotificationRequest": TestNotificationRequest,
            "TestTaskCompletionNotificationRequest": TestTaskCompletionNotificationRequest,
            "TestProductNotificationRequest": TestProductNotificationRequest,
            "CHANNEL_NAME_MAP": {"ntfy": "Ntfy"},
            "send_test_once": send_test_once,
            "NotificationTestConflict": NotificationTestConflict,
            "NotificationTestBusy": NotificationTestBusy,
            "NotificationTestFailed": NotificationTestFailed,
            "send_test_notification": self.sent["standard"],
            "send_test_task_completion_notification": self.sent["completion"],
            "send_test_product_notification": self.sent["product"],
        }
        load_nodes("src/web/notification_manager_v2.py", {
            "_require_notify_access", "_resolve_owner_id", "send_test_notification_api",
            "send_test_task_completion_notification_api", "send_test_product_notification_api",
        }, ns)
        app = FastAPI()
        app.include_router(ns["router"])
        self.client = TestClient(app)
        self.addCleanup(self.client.close)

    def post(self, path="test", **fields):
        return self.client.post(f"/api/notifications/{path}", json={"channel": "ntfy", **fields})

    def test_repeated_http_request_reuses_result_for_each_test_type(self):
        for path, kind in (("test", "standard"), ("test-product", "product"), ("test-task-completion", "completion")):
            with self.subTest(kind=kind):
                data = {"request_id": "repeat-" + kind, "config_id": "cfg-a", "bound_task": "task-a"}
                self.assertEqual(self.post(path, **data).status_code, 200)
                self.assertEqual(self.post(path, **data).status_code, 200)
                self.assertEqual(self.sent[kind].await_count, 1)
                self.assertEqual(self.sent[kind].await_args.kwargs["owner_id"], "a")

    def test_conflict_and_owner_isolation(self):
        self.assertEqual(self.post(request_id="owner-scope", config_id="cfg-a").status_code, 200)
        self.assertEqual(self.post(request_id="owner-scope", config_id="cfg-b").status_code, 409)
        self.assertEqual(self.post("test-product", request_id="owner-scope", config_id="cfg-a").status_code, 409)
        self.user = {"user_id": "b", "categories": ["notify"]}
        self.assertEqual(self.post(request_id="owner-scope", config_id="cfg-b").status_code, 200)
        self.assertEqual(self.sent["standard"].await_count, 2)

    def test_legacy_clients_and_invalid_ids(self):
        self.assertEqual(self.post().status_code, 200)
        self.assertEqual(self.post().status_code, 200)
        self.assertEqual(self.sent["standard"].await_count, 2)
        self.assertEqual(self.post(request_id="bad id").status_code, 422)
        self.assertEqual(self.sent["standard"].await_count, 2)

    def test_authentication_precedes_sender(self):
        self.user = None
        self.assertEqual(self.post(request_id="denied-a").status_code, 401)
        self.user = {"user_id": "a", "categories": []}
        self.assertEqual(self.post(request_id="denied-b").status_code, 403)
        self.sent["standard"].assert_not_awaited()

    def test_failed_send_result_is_reused_without_a_second_send(self):
        self.sent["standard"].side_effect = RuntimeError("synthetic secret failure")
        first = self.post(request_id="failure-cache")
        second = self.post(request_id="failure-cache")
        self.assertEqual((first.status_code, second.status_code), (500, 500))
        self.assertNotIn("synthetic secret", second.text)
        self.assertEqual(self.sent["standard"].await_count, 1)

    def test_legacy_send_failure_does_not_echo_secret(self):
        self.sent["standard"].side_effect = RuntimeError("synthetic secret failure")
        response = self.post()
        self.assertEqual(response.status_code, 500)
        self.assertNotIn("synthetic secret", response.text)


class NotificationTestConcurrencyTests(unittest.IsolatedAsyncioTestCase):
    async def test_concurrent_retry_and_request_cancellation_do_not_resend(self):
        calls = 0
        ready = asyncio.Event()
        release = asyncio.Event()
        async def sender():
            nonlocal calls
            calls += 1
            ready.set()
            await release.wait()
            return True
        def send():
            return send_test_once(owner_id="concurrent-owner", request_id="concurrent-id", test_type="product", channel="ntfy", config_id="cfg", bound_task=None, sender=sender)
        first = asyncio.create_task(send())
        await ready.wait()
        second = asyncio.create_task(send())
        first.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await first
        release.set()
        self.assertTrue(await second)
        self.assertTrue(await send())
        self.assertEqual(calls, 1)

    async def test_expired_key_can_be_used_for_a_new_attempt(self):
        calls = 0
        async def sender():
            nonlocal calls
            calls += 1
            return True
        kwargs = {"owner_id": "expiry-owner", "request_id": "expiry-id", "test_type": "standard", "channel": "ntfy", "config_id": None, "bound_task": None, "sender": sender}
        with mock.patch.object(notification_test_guard, "_TTL_SECONDS", 0):
            self.assertTrue(await send_test_once(**kwargs))
            self.assertTrue(await send_test_once(**kwargs))
        self.assertEqual(calls, 2)


if __name__ == "__main__":
    unittest.main()
