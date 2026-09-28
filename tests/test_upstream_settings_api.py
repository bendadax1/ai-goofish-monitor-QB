"""真实设置路由 + 合成配置/CAS，禁止加载真实 .env 或发送通知。"""

import ast
import copy
from pathlib import Path
import sys
from types import SimpleNamespace
from typing import Optional
import unittest
from unittest import mock
from urllib.parse import urlparse

from fastapi import APIRouter, Depends, FastAPI, HTTPException
from fastapi.testclient import TestClient
from src.web.models import NotificationSettings
from tests.test_upstream_notification_compat import notification_namespace


ROOT = Path(__file__).resolve().parents[1]


class RevisionConflict(Exception):
    def __init__(self, revision):
        self.current_revision = revision


class SettingsApiTests(unittest.TestCase):
    def setUp(self):
        self.backend = "local"
        self.portable = False
        self.user = {"id": "a", "categories": ["notify"], "admin": True}
        self.runtime, self.values = notification_namespace()
        self.reload_notifier = mock.Mock(side_effect=self.runtime["config"].reload)
        self.reload_config = mock.Mock()
        patcher = mock.patch.dict(sys.modules, {
            "src.notifier": SimpleNamespace(config=SimpleNamespace(reload=self.reload_notifier)),
            "src.notifier.config": SimpleNamespace(parse_legacy_ntfy_url=self.runtime["parse_legacy_ntfy_url"]),
            "src.config": SimpleNamespace(reload_config=self.reload_config),
        })
        patcher.start()
        self.addCleanup(patcher.stop)
        self.saved = []
        def save(settings, keys):
            self.saved.append((dict(settings), list(keys)))
            self.values.update(settings)
        async def require_auth():
            return self.user
        self.storage = mock.Mock()
        self.configs = {owner: {
            "id": "cfg-" + owner, "config_revision": 7, "api_key": "synthetic-" + owner,
            "api_base_url": "https://fixture.invalid", "model": "fixture", "extra_config": {
                "PROXY_URL": "http://proxy.invalid", "PROXY_NTFY_ENABLED": True,
                "AI_PARAMETER_FALLBACK_ENABLED": True, "unrelated": "keep",
            },
        } for owner in ("a", "b")}
        self.storage.get_default_api_config.side_effect = lambda owner: copy.deepcopy(self.configs[owner])
        self.storage.get_default_api_config_with_revision.side_effect = self.storage.get_default_api_config.side_effect
        def cas(owner, revision, config_id, updates):
            config = self.configs[owner]
            if revision != config["config_revision"] or config_id != config["id"]:
                raise RevisionConflict(config["config_revision"])
            if updates:
                config["extra_config"].update(updates.get("extra_config", {}))
                config["config_revision"] += 1
            return copy.deepcopy(config)
        self.storage.update_default_api_config_fields.side_effect = cas
        self.storage.save_user_api_config.return_value = {"id": "cfg-a"}
        self.ns = {
            "router": APIRouter(), "Depends": Depends, "HTTPException": HTTPException,
            "Optional": Optional, "urlparse": urlparse, "NotificationSettings": NotificationSettings,
            "require_auth": require_auth, "check_permission": lambda user, name: user.get("admin", False),
            "has_category": lambda user, name: name in user.get("categories", []),
            "get_env_value": lambda key, default=None: self.values.get(key, default),
            "get_bool_env_value": lambda key, default=False: str(self.values.get(key, default)).lower() == "true",
            "STORAGE_BACKEND": lambda: self.backend, "portable_mode": lambda: self.portable,
            "save_env_settings": mock.Mock(side_effect=save), "logger": mock.Mock(),
            "get_storage": lambda: self.storage, "ApiConfigRevisionConflict": RevisionConflict,
            "invalidate_ai_health_snapshot": mock.Mock(),
        }
        names = {
            "_require_notify_access", "_require_settings_admin", "_resolve_current_user_id",
            "_normalize_ntfy_server_url", "_current_ntfy_server_url", "_apply_ntfy_legacy_settings",
            "_prepare_notification_settings_update", "_preserve_secret_on_empty",
            "get_notification_settings", "update_notification_settings", "_to_bool_value",
            "_build_proxy_settings_from_extra_config", "get_proxy_settings", "update_proxy_settings",
        }
        constants = {"_NOTIFICATION_SECRET_KEYS", "_NTFY_DEFAULT_SERVER", "_PROXY_SETTING_KEYS", "_PROXY_BOOL_KEYS"}
        tree = ast.parse((ROOT / "src/web/settings_manager.py").read_text(encoding="utf-8"))
        nodes = [node for node in tree.body if (
            isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name in names
        ) or (isinstance(node, ast.Assign) and any(isinstance(target, ast.Name) and target.id in constants for target in node.targets))]
        self.assertEqual({node.name for node in nodes if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef))}, names)
        exec(compile(ast.Module(body=nodes, type_ignores=[]), "<isolated settings routes>", "exec"), self.ns)
        app = FastAPI()
        app.include_router(self.ns["router"])
        self.client = TestClient(app)
        self.addCleanup(self.client.close)

    def put_notifications(self, data):
        response = self.client.put("/api/settings/notifications", json=data)
        self.assertEqual(response.status_code, 200, response.text)
        return response

    def test_partial_notification_save_keeps_other_channels_and_flags(self):
        self.values.update({"GOTIFY_ENABLED": True, "NOTIFY_AFTER_TASK_COMPLETE": False, "WEBHOOK_METHOD": "GET"})
        self.put_notifications({"NTFY_TOPIC": "updated"})
        self.assertIs(self.values["GOTIFY_ENABLED"], True)
        self.assertIs(self.values["NOTIFY_AFTER_TASK_COMPLETE"], False)
        self.assertEqual(self.values["WEBHOOK_METHOD"], "GET")
        self.assertNotIn("GOTIFY_ENABLED", self.saved[0][0])

    def test_server_change_without_token_clears_old_secret_before_reload(self):
        self.values.update({"NTFY_SERVER_URL": "https://old.invalid", "NTFY_TOKEN": "synthetic-old"})
        self.put_notifications({"NTFY_SERVER_URL": "https://new.invalid"})
        self.assertEqual(self.values["NTFY_TOKEN"], "")
        self.assertEqual(self.runtime["_ntfy_publish_target"](), ("https://new.invalid/local", None))
        self.reload_notifier.assert_called_once()
        self.reload_config.assert_called_once()

    def test_legacy_readback_has_no_embedded_token_and_no_save(self):
        self.values.update({"NTFY_TOPIC": "", "NTFY_TOPIC_URL": "https://:synthetic-legacy@host.invalid/proxy/topic"})
        result = self.client.get("/api/settings/notifications")
        self.assertEqual(result.status_code, 200)
        self.assertNotIn("synthetic-legacy", result.text)
        self.assertEqual(result.json()["NTFY_TOPIC_URL"], "https://host.invalid/proxy/topic")
        self.assertIs(result.json()["NTFY_TOKEN_SET"], True)
        self.assertEqual(self.saved, [])

    def test_explicit_clear_cannot_resurrect_token_from_legacy_url(self):
        self.values.update({"NTFY_TOPIC": "", "NTFY_TOPIC_URL": "https://:synthetic-legacy@host.invalid/proxy/topic"})
        self.put_notifications({"NTFY_TOKEN_CLEAR": True})
        self.assertEqual(self.runtime["_ntfy_publish_target"](), ("https://host.invalid/proxy/topic", None))

    def test_legacy_read_save_roundtrip_preserves_effective_target_and_secret(self):
        self.values.update({"NTFY_TOPIC": "", "NTFY_TOPIC_URL": "https://:synthetic-legacy@host.invalid/proxy/topic"})
        response = self.client.get("/api/settings/notifications")
        self.assertEqual(response.status_code, 200)
        self.put_notifications(response.json())
        self.assertEqual(self.values["NTFY_TOPIC_URL"], "")
        self.assertEqual(self.runtime["_ntfy_publish_target"](), ("https://host.invalid/proxy/topic", "synthetic-legacy"))

    def test_unrelated_channel_update_does_not_rewrite_legacy_settings(self):
        legacy = "https://:synthetic-legacy@host.invalid/proxy/topic"
        self.values.update({"NTFY_TOPIC": "", "NTFY_TOPIC_URL": legacy})
        self.put_notifications({"GOTIFY_ENABLED": True})
        self.assertEqual(self.saved[0][0], {"GOTIFY_ENABLED": True})
        self.assertEqual(self.values["NTFY_TOPIC_URL"], legacy)

    def test_empty_legacy_address_disables_target_without_resurrection(self):
        self.values.update({"NTFY_TOPIC": "", "NTFY_TOPIC_URL": "https://:synthetic-legacy@host.invalid/proxy/topic"})
        self.put_notifications({"NTFY_TOPIC_URL": ""})
        self.assertEqual(self.values["NTFY_TOPIC"], "")
        self.assertEqual(self.values["NTFY_TOPIC_URL"], "")
        self.assertIsNone(self.runtime["_ntfy_publish_target"]())

    def test_malformed_legacy_readback_never_echoes_raw_credentials(self):
        for legacy in ("https://:synthetic-legacy@host.invalid", "https://:synthetic-legacy@host.invalid:bad/topic", "https://[broken/synthetic-legacy"):
            with self.subTest(legacy=legacy):
                self.values.update({"NTFY_TOPIC": "", "NTFY_TOPIC_URL": legacy})
                response = self.client.get("/api/settings/notifications")
                self.assertEqual(response.status_code, 200)
                self.assertNotIn("synthetic-legacy", response.text)
                self.assertEqual(response.json()["NTFY_TOPIC_URL"], "")
        self.assertEqual(self.saved, [])

    def test_same_server_empty_secret_keeps_value_and_new_token_replaces(self):
        self.values.update({"NTFY_SERVER_URL": "https://host.invalid/proxy", "NTFY_TOKEN": "synthetic-old", "GOTIFY_TOKEN": "synthetic-gotify"})
        self.put_notifications({"NTFY_SERVER_URL": "https://HOST.invalid/proxy/", "NTFY_TOKEN": "", "GOTIFY_TOKEN": ""})
        self.assertEqual(self.values["NTFY_TOKEN"], "synthetic-old")
        self.assertEqual(self.values["GOTIFY_TOKEN"], "synthetic-gotify")
        self.put_notifications({"NTFY_SERVER_URL": "https://new.invalid", "NTFY_TOKEN": "synthetic-new"})
        self.assertEqual(self.values["NTFY_TOKEN"], "synthetic-new")

    def test_reverse_proxy_path_case_change_does_not_reuse_token(self):
        self.values.update({"NTFY_SERVER_URL": "https://host.invalid/Private", "NTFY_TOKEN": "synthetic-old"})
        self.put_notifications({"NTFY_SERVER_URL": "https://host.invalid/private"})
        self.assertEqual(self.values["NTFY_TOKEN"], "")

    def test_legacy_url_submission_keeps_target_and_explicit_credential(self):
        self.put_notifications({"NTFY_TOPIC_URL": "http://:synthetic-new@[::1]:8080/ntfy/topic", "NTFY_ENABLED": True})
        self.assertEqual(self.runtime["_ntfy_publish_target"](), ("http://[::1]:8080/ntfy/topic", "synthetic-new"))

    def test_pg_rejects_global_notification_read_write(self):
        self.backend = "postgres"
        self.assertEqual(self.client.get("/api/settings/notifications").status_code, 400)
        self.assertEqual(self.client.put("/api/settings/notifications", json={"NTFY_TOPIC": "x"}).status_code, 400)
        self.assertEqual(self.saved, [])
        self.reload_notifier.assert_not_called()

    def test_settings_authentication_and_permissions_precede_storage(self):
        for user, expected in ((None, 401), ({"id": "a", "categories": [], "admin": False}, 403)):
            self.user = user
            for section in ("notifications", "proxy"):
                self.assertEqual(self.client.get(f"/api/settings/{section}").status_code, expected)
                self.assertEqual(self.client.put(f"/api/settings/{section}", json={}).status_code, expected)
        self.assertEqual(self.saved, [])
        self.assertEqual(self.storage.mock_calls, [])

    def test_local_proxy_save_reloads_both_configs_without_sending(self):
        response = self.client.put("/api/settings/proxy", json={"PROXY_URL": "http://new.invalid", "PROXY_NTFY_ENABLED": True})
        self.assertEqual(response.status_code, 200, response.text)
        self.reload_config.assert_called_once()
        self.reload_notifier.assert_called_once()
        self.assertEqual(self.runtime["_get_channel_proxies"]("PROXY_NTFY_ENABLED"), {"http": "http://new.invalid", "https": "http://new.invalid"})

    def test_private_proxy_merge_keeps_other_fields_and_authenticated_owner(self):
        self.backend = "postgres"
        response = self.client.put("/api/settings/proxy", json={"PROXY_NTFY_ENABLED": False, "owner_id": "b"})
        self.assertEqual(response.status_code, 200, response.text)
        owner, payload = self.storage.save_user_api_config.call_args.args
        self.assertEqual(owner, "a")
        self.assertEqual(payload["extra_config"], {**self.configs["a"]["extra_config"], "PROXY_NTFY_ENABLED": False})
        self.assertEqual(self.saved, [])
        self.reload_notifier.assert_not_called()

    def test_portable_stale_revision_and_wrong_identity_never_overwrite(self):
        self.backend, self.portable = "postgres", True
        request = {"config_revision": 7, "config_id": "cfg-a", "PROXY_URL": "http://updated.invalid"}
        self.assertEqual(self.client.put("/api/settings/proxy", json=request).status_code, 200)
        self.assertEqual(self.client.put("/api/settings/proxy", json={**request, "PROXY_URL": "http://stale.invalid"}).status_code, 409)
        self.assertEqual(self.client.put("/api/settings/proxy", json={**request, "config_revision": 8, "config_id": "cfg-b"}).status_code, 409)
        self.assertEqual(self.configs["a"]["extra_config"]["PROXY_URL"], "http://updated.invalid")
        self.assertEqual(self.configs["b"]["config_revision"], 7)
        self.ns["invalidate_ai_health_snapshot"].assert_called_once()
        self.storage.save_user_api_config.assert_not_called()

    def test_portable_noop_and_missing_revision(self):
        self.backend, self.portable = "postgres", True
        self.assertEqual(self.client.put("/api/settings/proxy", json={"PROXY_URL": "x"}).status_code, 428)
        response = self.client.put("/api/settings/proxy", json={"config_revision": 7, "config_id": "cfg-a", "PROXY_URL": "http://proxy.invalid"})
        self.assertEqual(response.status_code, 200, response.text)
        self.assertEqual(response.json()["config_revision"], 7)
        self.ns["invalidate_ai_health_snapshot"].assert_not_called()

    def test_private_proxy_read_does_not_inherit_global_or_other_owner(self):
        self.backend, self.portable = "postgres", True
        self.values["PROXY_URL"] = "http://global.invalid"
        self.configs["b"]["extra_config"] = {}
        self.user = {"id": "b", "admin": True}
        response = self.client.get("/api/settings/proxy")
        self.assertEqual(response.json()["PROXY_URL"], "")
        self.assertIs(response.json()["PROXY_NTFY_ENABLED"], False)
        self.assertEqual(response.json()["config_id"], "cfg-b")


if __name__ == "__main__":
    unittest.main()
