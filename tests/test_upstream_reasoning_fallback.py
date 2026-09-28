"""兼容网关 reasoning_content 显式回退：仅完整 JSON 能进入原评分链。"""

import asyncio
import json
import sys
from typing import Dict, Optional
from types import SimpleNamespace
import unittest
from unittest import mock

import httpx
from openai import AsyncOpenAI

from tests import test_upstream_call_sites as call_sites
from tests import test_upstream_parameter_fallback as parameter_tests


def reasoning_response(reasoning, *, content=None, finish_reason="stop", **fields):
    return SimpleNamespace(choices=[SimpleNamespace(
        finish_reason=finish_reason,
        message=SimpleNamespace(content=content, reasoning_content=reasoning, **fields),
    )])


def complete_analysis():
    payload = call_sites.valid_analysis()
    payload["veto_flags"] = []
    payload["missing_evidence"] = []
    payload["criteria_analysis"]["seller_type"] = {
        "status": "PASS", "persona": "普通个人卖家", "comment": "合成卖家信息一致",
        "analysis_details": {
            key: {"comment": "合成分析", "evidence": "合成证据"}
            for key in (
                "temporal_analysis", "selling_behavior", "buying_behavior",
                "timeline_consistency", "communication_style", "behavioral_summary",
            )
        },
    }
    return payload


def visual_summary():
    return {
        "image_quality": "PASS", "defect_visibility": "WARNING",
        "image_authenticity": "PASS", "detail_completeness": "WARNING",
        "text_image_consistency": "PASS", "comparison_copy": "合成对比",
        "appearance_grade": "B", "appearance_basis": "合成外观证据",
    }


class ReasoningAnalysisTests(unittest.IsolatedAsyncioTestCase):
    setUp = call_sites.AnalysisCallSiteTests.setUp
    analyze = call_sites.AnalysisCallSiteTests.analyze

    def enable(self):
        self.namespace["AI_REASONING_FALLBACK_ENABLED"] = lambda: True

    async def test_opt_in_complete_json_reaches_existing_scorer_once(self):
        self.enable()
        expected = complete_analysis()
        self.create.return_value = reasoning_response(json.dumps(expected, ensure_ascii=False))
        result = await self.analyze()
        self.assertEqual({key: result[key] for key in expected}, expected)
        self.assertEqual(result["recommendation_score_v2"]["recommendation_score"], 74)
        self.scorer.assert_called_once_with(owner_id="owner-a", bayes_profile="fixture-bayes")
        self.create.assert_awaited_once()

    async def test_default_off_keeps_failure_budget(self):
        self.create.return_value = reasoning_response(json.dumps(complete_analysis()))
        with self.assertRaises(self.namespace["AICallFailureException"]):
            await self.analyze()
        self.assertEqual(self.create.await_count, 3)
        self.scorer.assert_not_called()

    async def test_final_content_wins_even_when_reasoning_looks_valid(self):
        self.enable()
        expected = complete_analysis()
        self.create.return_value = reasoning_response(
            json.dumps({**expected, "reason": "来自推理"}, ensure_ascii=False),
            content=json.dumps(expected, ensure_ascii=False),
        )
        result = await self.analyze()
        self.assertEqual(result["reason"], "测试证据")
        self.create.assert_awaited_once()

    async def test_invalid_final_content_does_not_fall_back(self):
        self.enable()
        self.create.return_value = reasoning_response(json.dumps(complete_analysis()), content="{}")
        with self.assertRaises(self.namespace["AICallFailureException"]):
            await self.analyze()
        self.assertEqual(self.create.await_count, 3)
        self.scorer.assert_not_called()

    async def test_incomplete_and_ambiguous_payloads_never_reach_scorer(self):
        self.enable()
        expected = complete_analysis()
        missing = dict(expected)
        missing.pop("confidence_score")
        inconsistent = {**expected, "is_recommended": False}
        incomplete_seller = json.loads(json.dumps(expected))
        incomplete_seller["criteria_analysis"]["seller_type"]["analysis_details"].pop("buying_behavior")
        invalid = [
            json.dumps(missing), json.dumps(inconsistent),
            json.dumps({**expected, "criteria_analysis": {"seller_type": {}}}),
            json.dumps(incomplete_seller),
            json.dumps({**expected, "veto_flags": None}),
            json.dumps({**expected, "veto_flags": ["质量否决"]}),
            json.dumps({**expected, "confidence_score": 0.95}),
            json.dumps({**expected, "missing_evidence": [{"field": "保修"}]}),
            json.dumps({**expected, "confidence_score": float("nan")}),
            json.dumps(expected) + '\n{"is_recommended":false}',
            "```json\n" + json.dumps(expected) + "\n```",
            '{"prompt_version":"a","prompt_version":"b"}',
            "普通推理文字",
        ]
        for value in invalid:
            with self.subTest(value=value[:40]):
                self.namespace["ai_call_failure_count"] = 0
                self.create.reset_mock()
                self.scorer.reset_mock()
                self.create.return_value = reasoning_response(value)
                with self.assertRaises(self.namespace["AICallFailureException"]):
                    await self.analyze()
                self.assertEqual(self.create.await_count, 3)
                self.scorer.assert_not_called()

    async def test_truncated_or_refused_response_never_uses_reasoning(self):
        self.enable()
        content = json.dumps(complete_analysis())
        for response in (reasoning_response(content, finish_reason="length"), reasoning_response(content, refusal="refused")):
            with self.subTest(response=response):
                self.namespace["ai_call_failure_count"] = 0
                self.create.reset_mock()
                self.create.return_value = response
                with self.assertRaises(self.namespace["AICallFailureException"]):
                    await self.analyze()
                self.scorer.assert_not_called()

    async def test_debug_mode_does_not_print_reasoning_text(self):
        self.enable()
        self.namespace["AI_DEBUG_MODE"] = lambda: True
        secret = "private-reasoning-marker"
        expected = {**complete_analysis(), "reason": secret}
        self.create.return_value = reasoning_response(json.dumps(expected))
        await self.analyze()
        displayed = "\n".join(str(call.args[0]) for call in self.namespace["safe_print"].call_args_list if call.args)
        self.assertNotIn(secret, displayed)

    async def test_concurrent_owners_do_not_share_switch(self):
        self.enable()
        self.create.return_value = reasoning_response(json.dumps(complete_analysis()))
        results = await asyncio.gather(self.analyze("owner-a"), self.analyze("owner-b"))
        self.assertEqual(len(results), 2)
        self.assertEqual({call.kwargs["owner_id"] for call in self.scorer.call_args_list}, {"owner-a", "owner-b"})

    async def test_images_require_full_visual_summary_before_scoring(self):
        self.enable()
        self.product["商品信息"]["商品图片列表"] = ["https://fixture.invalid/image.jpg"]
        self.create.return_value = reasoning_response(json.dumps(complete_analysis()))
        with self.assertRaises(self.namespace["AICallFailureException"]):
            await self.analyze()
        self.scorer.assert_not_called()
        self.namespace["ai_call_failure_count"] = 0
        expected = {**complete_analysis(), "visual_summary": visual_summary()}
        self.create.return_value = reasoning_response(json.dumps(expected))
        self.assertEqual((await self.analyze())["visual_summary"], visual_summary())
        self.scorer.assert_called_once()

    async def test_sdk_preserves_gateway_extension_in_mock_response(self):
        self.enable()
        requests = []
        expected = complete_analysis()

        def respond(request):
            requests.append(json.loads(request.content))
            return httpx.Response(200, json={
                "id": "fixture", "created": 0, "object": "chat.completion", "model": "fixture",
                "choices": [{"index": 0, "finish_reason": "stop", "message": {
                    "role": "assistant", "content": None,
                    "reasoning_content": json.dumps(expected, ensure_ascii=False),
                }}],
            })

        async with httpx.AsyncClient(transport=httpx.MockTransport(respond), trust_env=False) as transport:
            async with AsyncOpenAI(api_key="synthetic", base_url="https://fixture.invalid/v1", http_client=transport) as client:
                self.namespace["client"] = client
                result = await self.analyze()
        self.assertEqual(result["reason"], expected["reason"])
        self.assertEqual(len(requests), 1)
        self.scorer.assert_called_once()


class ReasoningSettingsTests(unittest.IsolatedAsyncioTestCase):
    setUp = parameter_tests.PrivateSettingsTests.setUp

    def flag_name(self):
        return "AI_REASONING_FALLBACK_ENABLED"

    async def test_private_and_portable_roundtrip(self):
        flag = self.flag_name()
        await self.namespace["update_ai_settings"]({flag: True}, self.user)
        owner, payload = self.storage.save_user_api_config.call_args.args
        self.assertEqual(owner, "owner-a")
        self.assertIs(payload["extra_config"][flag], True)
        self.assertEqual(payload["extra_config"]["other"], "keep")
        self.namespace["portable_mode"] = lambda: True
        self.storage.save_user_api_config.reset_mock()
        result = await self.namespace["update_ai_settings"]({flag: False, "config_revision": 7, "config_id": "cfg-a"}, self.user)
        self.assertEqual(self.storage.update_default_api_config_fields.call_args.args[3], {"extra_config": {flag: False}})
        self.assertEqual(result["config_revision"], 8)
        self.storage.save_user_api_config.assert_not_called()

    async def test_rejects_ambiguous_values_before_storage(self):
        for backend in ("postgres", "file"):
            self.namespace["STORAGE_BACKEND"] = lambda: backend
            for value in ("true", 1, None, "", []):
                with self.subTest(backend=backend, value=value), self.assertRaises(Exception) as caught:
                    await self.namespace["update_ai_settings"]({self.flag_name(): value}, self.user)
                self.assertEqual(caught.exception.status_code, 400)

    async def test_old_private_config_defaults_off(self):
        self.config["extra_config"].pop(self.flag_name(), None)
        for portable in (False, True):
            self.namespace["portable_mode"] = lambda: portable
            self.assertIs((await self.namespace["get_ai_settings"](self.user))[self.flag_name()], False)

    async def test_file_save_and_boolean_readback(self):
        flag = self.flag_name()
        self.namespace["STORAGE_BACKEND"] = lambda: "file"
        reload_config = mock.Mock()
        with mock.patch.dict(sys.modules, {"src.config": SimpleNamespace(reload_config=reload_config)}):
            await self.namespace["update_ai_settings"]({flag: False}, self.user)
        saved, allowed = self.namespace["save_env_settings"].call_args.args
        self.assertIs(saved[flag], False)
        self.assertIn(flag, allowed)
        reload_config.assert_called_once()
        for value in ("true", "false", "yes", None):
            self.namespace["get_env_value"] = lambda key, default=None: value if key == flag and value is not None else default
            self.assertIs((await self.namespace["get_ai_settings"](self.user))[flag], value == "true")

    def test_env_serialization_preserves_omitted_value(self):
        flag = self.flag_name()
        for settings in ({flag: False}, {}):
            fake_open = mock.mock_open(read_data=f"{flag}=true\nOTHER=keep\n")
            namespace = call_sites.load_functions("src/config.py", {"save_env_settings"}, {
                "PORTABLE_MODE": False, "open": fake_open,
                "os": SimpleNamespace(path=SimpleNamespace(exists=lambda path: True), environ={}),
            })
            namespace["save_env_settings"](settings, [flag])
            written = "".join(call.args[0] for call in fake_open().write.call_args_list)
            self.assertIn(f"{flag}={'false' if settings else 'true'}\n", written)
            self.assertIn("OTHER=keep\n", written)
            self.assertTrue(all(call.kwargs["encoding"] == "utf-8" for call in fake_open.call_args_list if call.args))


class ReasoningWorkerTests(unittest.TestCase):
    def test_accessor_defaults_off_and_accepts_only_true(self):
        flag = "AI_REASONING_FALLBACK_ENABLED"
        for value in (None, "", "false", "yes", "1", "true", " TRUE "):
            namespace = call_sites.load_functions("src/config.py", {flag}, {
                "_get_runtime_override_value": lambda key: None,
                "get_env_value": lambda key, default: default if value is None else value,
            })
            self.assertIs(namespace[flag](), value in ("true", " TRUE "))

    def test_manual_and_scheduled_workers_isolate_owner_switch(self):
        flag = "AI_REASONING_FALLBACK_ENABLED"
        runtime_flag = "GOOFISH_" + flag
        for path in ("src/web/task_manager.py", "src/web/scheduler.py"):
            storage = mock.Mock()
            configs = {
                "enabled": {"api_key": "fixture", "api_base_url": "https://fixture.invalid", "model": "fixture", "extra_config": {flag: True}},
                "disabled": {"api_key": "fixture", "api_base_url": "https://fixture.invalid", "model": "fixture", "extra_config": {}},
            }
            storage.get_default_api_config.side_effect = lambda owner: configs[owner]
            namespace = call_sites.load_functions(path, {"_apply_owner_ai_env_overrides", "_parse_bool_for_env"}, {
                "Dict": Dict, "Optional": Optional, "is_multi_user_mode": lambda: True,
                "get_storage": lambda: storage, "logger": mock.Mock(),
            })
            for owner, expected in (("enabled", "true"), ("disabled", "false")):
                environment = {flag: "true", runtime_flag: "true"}
                namespace["_apply_owner_ai_env_overrides"](environment, owner)
                self.assertNotIn(flag, environment)
                self.assertEqual(environment[runtime_flag], expected)
                accessor = call_sites.load_functions("src/config.py", {flag, "_get_runtime_override_value"}, {
                    "os": SimpleNamespace(environ=environment),
                    "get_env_value": lambda key, default: "true",
                })
                self.assertIs(accessor[flag](), owner == "enabled")


if __name__ == "__main__":
    unittest.main()
