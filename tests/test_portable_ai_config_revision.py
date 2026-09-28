"""Focused portable AI-config revision and field-patch contract checks."""

import unittest
from copy import deepcopy
from unittest import mock

from fastapi import HTTPException

from src.storage.postgres_adapter import ApiConfigRevisionConflict
from src.web import settings_manager


class _FakeStorage:
    def __init__(self, config=None):
        self.config = deepcopy(config)
        self.update_calls = []

    def get_default_api_config_with_revision(self, user_id):
        return deepcopy(self.config)

    def get_default_api_config(self, user_id):
        return deepcopy(self.config)

    def save_user_api_config(self, user_id, payload):
        self.config = deepcopy(payload)
        self.config["config_revision"] = int((self.config or {}).get("config_revision", 0)) + 1
        return deepcopy(self.config)

    def update_default_api_config_fields(self, user_id, expected_revision, expected_config_id, field_updates):
        self.update_calls.append((user_id, expected_revision, expected_config_id, deepcopy(field_updates)))
        current_revision = int((self.config or {}).get("config_revision", 0))
        current_config_id = str((self.config or {}).get("id") or "")
        if expected_revision != current_revision or expected_config_id != current_config_id:
            raise ApiConfigRevisionConflict(current_revision)
        if not field_updates:
            return deepcopy(self.config) or {"config_revision": current_revision}
        config = deepcopy(self.config) or {
            "provider": "openai", "name": "默认AI配置", "extra_config": {},
        }
        for key, value in field_updates.items():
            if key == "extra_config":
                extra_config = config.setdefault("extra_config", {})
                for extra_key, extra_value in value.items():
                    if extra_value is None:
                        extra_config.pop(extra_key, None)
                    else:
                        extra_config[extra_key] = extra_value
            elif key == "api_key":
                config["api_key"] = value
            elif key == "remove_api_key":
                config["api_key"] = ""
            else:
                config[key] = value
        config["config_revision"] = current_revision + 1
        self.config = config
        return deepcopy(config)


class PortableAiConfigRevisionTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.user = {"id": "user-1", "user_id": "user-1"}
        self.config = {
            "id": "config-a",
            "config_revision": 7,
            "api_key": "secret-value",
            "api_base_url": "https://api.example.test/v1",
            "model": "model-a",
            "extra_config": {
                "PROXY_URL": "http://proxy.local:8080",
                "AI_MAX_TOKENS_LIMIT": 8192,
                "ADVANCED_RETRY_COUNT": 5,
            },
        }
        self.storage = _FakeStorage(self.config)
        self.patches = [
            mock.patch.object(settings_manager, "STORAGE_BACKEND", return_value="postgres"),
            mock.patch.object(settings_manager, "portable_mode", return_value=True),
            mock.patch.object(settings_manager, "get_storage", return_value=self.storage),
            mock.patch.object(settings_manager, "_require_ai_or_tasks_access", return_value=self.user),
            mock.patch.object(settings_manager, "_require_settings_admin", return_value=self.user),
        ]
        for item in self.patches:
            item.start()
        self.addCleanup(self._stop_patches)

    def _stop_patches(self):
        for item in reversed(self.patches):
            item.stop()

    async def test_generic_portable_headless_update_uses_narrow_persistence_contract(self):
        from src.web.models import GenericSettings
        with mock.patch.object(settings_manager, "save_env_settings") as save, mock.patch("src.config.reload_config"):
            result = await settings_manager.update_generic_settings(GenericSettings(RUN_HEADLESS=False), _user=self.user)
        save.assert_called_once_with({"RUN_HEADLESS": False}, ["RUN_HEADLESS"])
        self.assertIn("新启动", result["message"])

    async def test_generic_portable_rejects_unsupported_fields_before_writing(self):
        from src.web.models import GenericSettings
        for payload in ({"WEB_PASSWORD": "fixture"}, {"SERVER_PORT": 8001}, {"AI_VISION_ENABLED": False}):
            with mock.patch.object(settings_manager, "save_env_settings") as save:
                with self.assertRaises(HTTPException) as raised:
                    await settings_manager.update_generic_settings(GenericSettings(**payload), _user=self.user)
            self.assertEqual(raised.exception.status_code, 400)
            save.assert_not_called()

    async def test_get_returns_revision_source_and_state_without_key(self):
        result = await settings_manager.get_ai_settings(user=self.user)
        self.assertEqual(result["config_revision"], 7)
        self.assertEqual(result["config_id"], "config-a")
        self.assertEqual(result["config_source"], "user_default_api_config")
        self.assertEqual(result["effective_state"], "applies_to_new_requests")
        self.assertTrue(result["OPENAI_API_KEY_SET"])
        self.assertEqual(result["OPENAI_API_KEY"], "")
        self.assertNotIn("secret-value", repr(result))

    async def test_field_patch_preserves_advanced_config_and_empty_key(self):
        invalidation = mock.patch.object(settings_manager, "invalidate_ai_health_snapshot")
        invalidate = invalidation.start()
        self.addCleanup(invalidation.stop)
        result = await settings_manager.update_ai_settings({
            "config_revision": 7,
            "config_id": "config-a",
            "OPENAI_MODEL_NAME": "model-b",
            "OPENAI_API_KEY": "",
        }, user=self.user)
        self.assertEqual(result["config_revision"], 8)
        self.assertEqual(self.storage.config["model"], "model-b")
        self.assertEqual(self.storage.config["api_key"], "secret-value")
        self.assertEqual(self.storage.config["extra_config"]["ADVANCED_RETRY_COUNT"], 5)
        self.assertEqual(len(self.storage.update_calls), 1)
        invalidate.assert_called_once_with(self.user)

    async def test_tokens_patch_keeps_user_scope_and_other_extra_fields(self):
        with mock.patch.object(settings_manager, "invalidate_ai_health_snapshot"):
            await settings_manager.update_ai_settings({
                "config_revision": 7, "config_id": "config-a",
                "AI_MAX_TOKENS_PARAM_NAME": "max_completion_tokens", "AI_MAX_TOKENS_LIMIT": 4096,
            }, user=self.user)
        call = self.storage.update_calls[0]
        self.assertEqual(call[:3], ("user-1", 7, "config-a"))
        self.assertEqual(self.storage.config["extra_config"]["AI_MAX_TOKENS_LIMIT"], 4096)
        self.assertEqual(self.storage.config["extra_config"]["AI_MAX_TOKENS_PARAM_NAME"], "max_completion_tokens")
        self.assertEqual(self.storage.config["extra_config"]["ADVANCED_RETRY_COUNT"], 5)
        self.assertEqual(self.storage.config["api_key"], "secret-value")

    async def test_stale_revision_returns_conflict_and_does_not_write(self):
        with self.assertRaises(HTTPException) as raised:
            await settings_manager.update_ai_settings({
                "config_revision": 6,
                "config_id": "config-a",
                "OPENAI_MODEL_NAME": "stale-model",
            }, user=self.user)
        self.assertEqual(raised.exception.status_code, 409)
        self.assertEqual(len(self.storage.update_calls), 1)
        self.assertEqual(self.storage.config["model"], "model-a")

    async def test_replaced_config_with_same_revision_returns_conflict(self):
        with self.assertRaises(HTTPException) as raised:
            await settings_manager.update_ai_settings({
                "config_revision": 7,
                "config_id": "deleted-config",
                "OPENAI_MODEL_NAME": "stale-model",
            }, user=self.user)
        self.assertEqual(raised.exception.status_code, 409)
        self.assertEqual(self.storage.config["model"], "model-a")

    async def test_host_change_requires_replacement_or_explicit_removal(self):
        with self.assertRaises(HTTPException) as raised:
            await settings_manager.update_ai_settings({
                "config_revision": 7,
                "config_id": "config-a",
                "OPENAI_BASE_URL": "https://other.example.test/v1",
            }, user=self.user)
        self.assertEqual(raised.exception.status_code, 400)
        self.assertEqual(len(self.storage.update_calls), 0)

        result = await settings_manager.update_ai_settings({
            "config_revision": 7,
            "config_id": "config-a",
            "OPENAI_BASE_URL": "https://other.example.test/v1",
            "remove_api_key": True,
        }, user=self.user)
        self.assertEqual(result["config_revision"], 8)
        self.assertEqual(self.storage.config["api_key"], "")

    async def test_missing_revision_is_rejected(self):
        with self.assertRaises(HTTPException) as raised:
            await settings_manager.update_ai_settings({"OPENAI_MODEL_NAME": "new"}, user=self.user)
        self.assertEqual(raised.exception.status_code, 428)

    async def test_proxy_field_patch_uses_same_revision_and_preserves_other_advanced_values(self):
        invalidation = mock.patch.object(settings_manager, "invalidate_ai_health_snapshot")
        invalidate = invalidation.start()
        self.addCleanup(invalidation.stop)
        result = await settings_manager.update_proxy_settings({
            "config_revision": 7,
            "config_id": "config-a",
            "PROXY_AI_ENABLED": True,
        }, user=self.user)
        self.assertEqual(result["config_revision"], 8)
        self.assertTrue(self.storage.config["extra_config"]["PROXY_AI_ENABLED"])
        self.assertEqual(self.storage.config["extra_config"]["ADVANCED_RETRY_COUNT"], 5)
        self.assertEqual(self.storage.config["model"], "model-a")
        self.assertEqual(self.storage.update_calls[-1][1], 7)
        invalidate.assert_called_once_with(self.user)

    async def test_proxy_stale_revision_returns_conflict(self):
        with self.assertRaises(HTTPException) as raised:
            await settings_manager.update_proxy_settings({
                "config_revision": 6,
                "config_id": "config-a",
                "PROXY_AI_ENABLED": True,
            }, user=self.user)
        self.assertEqual(raised.exception.status_code, 409)
        self.assertEqual(self.storage.config["extra_config"].get("PROXY_AI_ENABLED"), None)

    async def test_legacy_postgres_ai_put_still_accepts_legacy_payload(self):
        with mock.patch.object(settings_manager, "portable_mode", return_value=False), mock.patch.object(
            settings_manager, "invalidate_ai_health_snapshot"
        ):
            result = await settings_manager.update_ai_settings({
                "OPENAI_BASE_URL": "https://api.example.test/v2",
                "OPENAI_MODEL_NAME": "model-legacy",
                "OPENAI_API_KEY": "",
            }, user=self.user)
        self.assertEqual(result["message"], "AI模型设置已成功更新并保存到当前用户配置。")
        self.assertEqual(self.storage.config["api_key"], "secret-value")
        self.assertEqual(self.storage.config["model"], "model-legacy")


if __name__ == "__main__":
    unittest.main()
