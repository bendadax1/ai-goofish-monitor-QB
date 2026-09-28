"""执行真实业务函数体，隔离模块初始化、文件 I/O 和外部模型。

AST 只跳过顶层配置初始化和原有外层重试装饰器；因此这些是调用接线
与单次业务循环测试，不冒充全服务或 SDK 网络重试的整体验收。
"""

import ast
import asyncio
import copy
from datetime import datetime
import json
import logging
import os
from pathlib import Path
import sys
from types import SimpleNamespace
from typing import Optional
import unittest
from unittest import mock

from src.ai_response import AIResponseContentError, extract_analysis_candidate, extract_final_text
from src.ai_parameter_fallback import unsupported_optional_parameter


ROOT = Path(__file__).resolve().parents[1]


def load_functions(relative_path, names, namespace):
    path = ROOT / relative_path
    tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    nodes = [node for node in tree.body if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)) and node.name in names]
    if {node.name for node in nodes} != set(names):
        raise AssertionError("缺少业务函数，不能静默跳过接线测试")
    for node in nodes:
        node.decorator_list = []
    exec(compile(ast.Module(body=nodes, type_ignores=[]), str(path), "exec"), namespace)
    return namespace


def response(content, finish_reason="stop"):
    return SimpleNamespace(choices=[SimpleNamespace(
        finish_reason=finish_reason, message=SimpleNamespace(content=content),
    )])


def valid_analysis():
    return {
        "prompt_version": "test", "recommendation_level": "CAUTIOUS_BUY",
        "confidence_score": 0.75, "is_recommended": True, "reason": "测试证据",
        "action_required": [], "risk_tags": [], "criteria_analysis": {"seller_type": {"value": "个人"}},
    }


class AnalysisCallSiteTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.create = mock.AsyncMock()
        self.scorer = mock.Mock()
        self.scorer.return_value.calculate.return_value = {
            "recommendation_score": 74, "bayesian": {"score": 0.8},
            "visual_ai": {"score": 0.7}, "fusion": {"ai_score": 75},
        }
        self.namespace = load_functions("src/ai_handler.py", {
            "get_ai_analysis", "validate_ai_response_format", "_backfill_and_normalize_ai_response",
            "_is_recommended_level", "_parse_strict_reasoning_payload", "AICallFailureException",
        }, {
            "asyncio": asyncio, "copy": copy, "json": json, "datetime": datetime,
            "os": SimpleNamespace(path=os.path, makedirs=mock.Mock()), "open": mock.mock_open(),
            "safe_print": mock.Mock(), "MAX_PRODUCT_IMAGE_COUNT": 9,
            "AI_DEBUG_MODE": lambda: False, "AI_VISION_ENABLED": lambda: False,
            "ENABLE_RESPONSE_FORMAT": lambda: True, "MODEL_NAME": lambda: "fake-model",
            "AI_PARAMETER_FALLBACK_ENABLED": lambda: False,
            "AI_REASONING_FALLBACK_ENABLED": lambda: False,
            "AI_MAX_TOKENS_PARAM_NAME": lambda: "max_tokens",
            "unsupported_optional_parameter": unsupported_optional_parameter,
            "client": SimpleNamespace(chat=SimpleNamespace(completions=SimpleNamespace(create=self.create))),
            "extract_analysis_candidate": extract_analysis_candidate,
            "AIResponseContentError": AIResponseContentError, "ai_call_failure_count": 0,
            "AI_CALL_FAILURE_THRESHOLD": 3,
            "RECOMMENDATION_LEVELS": {"STRONG_BUY", "CAUTIOUS_BUY", "CONDITIONAL_BUY", "NOT_RECOMMENDED"},
            "RECOMMENDED_LEVELS": {"STRONG_BUY", "CAUTIOUS_BUY", "CONDITIONAL_BUY"},
        })
        config = SimpleNamespace(get_ai_request_params=lambda **kwargs: kwargs)
        patcher = mock.patch.dict(sys.modules, {
            "src.config": config,
            "src.recommendation_scorer": SimpleNamespace(RecommendationScorer=self.scorer),
        })
        patcher.start()
        self.addCleanup(patcher.stop)
        self.product = {"商品信息": {"商品ID": "fixture-1", "商品标题": "合成商品"}}

    async def analyze(self, owner="owner-a"):
        return await self.namespace["get_ai_analysis"](
            self.product, prompt_text="fixture JSON", owner_id=owner, bayes_profile="fixture-bayes",
        )

    async def test_standard_answer_preserves_score_and_owner(self):
        original = valid_analysis()
        self.create.return_value = response(json.dumps(original, ensure_ascii=False))
        result = await self.analyze()
        self.assertEqual({key: result[key] for key in original}, original)
        self.assertEqual(result["recommendation_score_v2"]["recommendation_score"], 74)
        self.scorer.assert_called_once_with(owner_id="owner-a", bayes_profile="fixture-bayes")
        self.create.assert_awaited_once()
        self.assertEqual(self.create.call_args.kwargs["temperature"], 0.1)
        self.assertEqual(self.create.call_args.kwargs["response_format"], {"type": "json_object"})

    async def test_text_parts_reach_existing_score_validation(self):
        text = json.dumps(valid_analysis(), ensure_ascii=False)
        self.create.return_value = response([
            {"type": "text", "text": text[:20]}, {"type": "text", "text": text[20:]},
        ])
        self.assertTrue((await self.analyze())["is_recommended"])
        self.scorer.return_value.calculate.assert_called_once()

    async def test_markdown_still_uses_existing_json_cleanup(self):
        self.create.return_value = response("```json\n" + json.dumps(valid_analysis()) + "\n```")
        self.assertEqual((await self.analyze())["recommendation_score_v2"]["recommendation_score"], 74)
        self.create.assert_awaited_once()

    async def test_empty_then_valid_uses_existing_retry(self):
        self.create.side_effect = [response(None), response(json.dumps(valid_analysis()))]
        self.assertTrue((await self.analyze())["is_recommended"])
        self.assertEqual(self.create.await_count, 2)
        self.assertEqual(self.namespace["ai_call_failure_count"], 1)
        self.assertEqual(self.create.call_args.kwargs["temperature"], 0.05)

    async def test_all_empty_stops_at_existing_inner_budget(self):
        self.create.return_value = response(None)
        with self.assertRaises(self.namespace["AICallFailureException"]):
            await self.analyze()
        self.assertEqual(self.create.await_count, 3)
        self.scorer.assert_not_called()

    async def test_truncated_valid_json_never_reaches_scorer(self):
        self.create.return_value = response(json.dumps(valid_analysis()), finish_reason="length")
        with self.assertRaises(self.namespace["AICallFailureException"]):
            await self.analyze()
        self.assertEqual(self.create.await_count, 3)
        self.scorer.assert_not_called()

    async def test_missing_schema_field_never_reaches_scorer(self):
        invalid = valid_analysis()
        invalid["criteria_analysis"] = {"other": "not seller_type"}
        self.create.return_value = response(json.dumps(invalid))
        with self.assertRaises(self.namespace["AICallFailureException"]):
            await self.analyze()
        self.scorer.assert_not_called()

    async def test_multiple_json_objects_are_not_selected_for_recommendation(self):
        self.create.return_value = response(json.dumps(valid_analysis()) + '\n{"is_recommended":false}')
        with self.assertRaises(json.JSONDecodeError):
            await self.analyze()
        self.assertEqual(self.create.await_count, 3)
        self.scorer.assert_not_called()

    async def test_concurrent_success_does_not_change_owner_forwarding(self):
        self.create.return_value = response(json.dumps(valid_analysis()))
        results = await asyncio.gather(self.analyze("owner-a"), self.analyze("owner-b"))
        self.assertEqual(len(results), 2)
        self.assertEqual({call.kwargs["owner_id"] for call in self.scorer.call_args_list}, {"owner-a", "owner-b"})
        self.assertEqual(self.create.await_count, 2)


class CriteriaCallSiteTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.create = mock.AsyncMock()
        self.client = SimpleNamespace(with_options=mock.Mock(return_value=SimpleNamespace(
            chat=SimpleNamespace(completions=SimpleNamespace(create=self.create)),
        )))
        self.transport = SimpleNamespace(aclose=mock.AsyncMock())
        self.namespace = load_functions("src/prompt_utils.py", {"generate_criteria"}, {
            "Optional": Optional, "logger": logging.getLogger("upstream-test"),
            "_resolve_criteria_ai_runtime": lambda _owner: (self.client, "fixture", self.transport),
            "STORAGE_BACKEND": lambda: "local", "open": mock.mock_open(read_data="合成模板"),
            "get_weight_framework_guide": lambda: "合成指南",
            "META_PROMPT_TEMPLATE": "{reference_text}\n{user_description}\n{weight_instruction}",
            "CRITERIA_REQUEST_TIMEOUT_SECONDS": 900,
            "config": SimpleNamespace(get_ai_request_params=lambda **kwargs: kwargs),
            "httpx": SimpleNamespace(Timeout=lambda value: value, TimeoutException=type("TimeoutException", (Exception,), {})),
            "APITimeoutError": type("APITimeoutError", (Exception,), {}),
            "extract_final_text": extract_final_text, "sanitize_generated_criteria": lambda text: text.strip(),
        })

    async def test_generated_text_parts_keep_single_request_and_close_transport(self):
        self.create.return_value = response([{"type": "text", "text": "生成标准"}])
        result = await self.namespace["generate_criteria"]("需求", "fixture.txt", "owner")
        self.assertEqual(result, "生成标准")
        self.create.assert_awaited_once()
        self.client.with_options.assert_called_once_with(timeout=900, max_retries=0)
        self.transport.aclose.assert_awaited_once()

    async def test_invalid_response_closes_transport_without_retry(self):
        self.create.return_value = response(None)
        with self.assertLogs("upstream-test", level="ERROR"), self.assertRaises(AIResponseContentError):
            await self.namespace["generate_criteria"]("需求", "fixture.txt", "owner")
        self.create.assert_awaited_once()
        self.transport.aclose.assert_awaited_once()


class ModuleWiringTests(unittest.TestCase):
    def test_real_sdk_completion_object(self):
        from openai.types.chat import ChatCompletion

        completion = ChatCompletion.model_validate({
            "id": "fixture", "created": 0, "object": "chat.completion", "model": "fixture",
            "choices": [{"index": 0, "finish_reason": "stop", "message": {"role": "assistant", "content": "正常答案"}}],
        })
        self.assertEqual(extract_final_text(completion), "正常答案")

    def test_scraper_imports_pure_helpers_and_no_longer_substring_matches_search(self):
        tree = ast.parse((ROOT / "src/scraper.py").read_text(encoding="utf-8"))
        names = {
            alias.asname or alias.name
            for node in tree.body if isinstance(node, ast.ImportFrom) and node.module == "src.search_requests"
            for alias in node.names
        }
        self.assertTrue({"_capture_new_request_response", "_build_extra_headers", "advance_search_page", "_expect_new_search_response"} <= names)
        for node in ast.walk(tree):
            if isinstance(node, ast.Compare) and isinstance(node.left, ast.Name) and node.left.id == "API_URL_PATTERN":
                self.assertFalse(any(isinstance(op, ast.In) for op in node.ops))


if __name__ == "__main__":
    unittest.main()
