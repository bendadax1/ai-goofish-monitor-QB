"""无真实配置/数据库/网络的参数回退与私有设置接线回归。"""

import asyncio
from functools import wraps
import json
import sys
from types import SimpleNamespace
from typing import Dict, Optional
from urllib.parse import urlparse
import unittest
from unittest import mock

from fastapi import Depends, HTTPException
import httpx
from openai import APIStatusError, APITimeoutError, AsyncOpenAI, BadRequestError
from requests.exceptions import HTTPError

from src.ai_parameter_fallback import unsupported_optional_parameter
from tests import test_upstream_call_sites as call_sites


FLAG = "AI_PARAMETER_FALLBACK_ENABLED"
RUNTIME_FLAG = "GOOFISH_" + FLAG


def bad_parameter(param="temperature", code="unsupported_parameter", body=None, status=400):
    return BadRequestError(
        "synthetic error",
        response=httpx.Response(status, request=httpx.Request("POST", "https://fixture.invalid/v1")),
        body=body if body is not None else {"code": code, "param": param},
    )


class ClassifierTests(unittest.TestCase):
    def test_explicit_structured_errors_only(self):
        sent = {"temperature": 0.1, "response_format": {"type": "json_object"}}
        for param in sent:
            for nested in (False, True):
                body = {"code": "unsupported_parameter", "param": param}
                error = bad_parameter(body={"error": body} if nested else body)
                self.assertEqual(unsupported_optional_parameter(error, sent), param)
        self.assertEqual(sent["temperature"], 0.1)

    def test_ambiguous_and_protected_errors_are_rejected(self):
        sent = {key: 1 for key in ("temperature", "response_format", "max_tokens", "model", "messages", "extra_body")}
        errors = [
            ValueError("Unsupported parameter: temperature"),
            APITimeoutError(request=httpx.Request("POST", "https://fixture.invalid")),
            bad_parameter(body="Unsupported parameter: temperature"),
            bad_parameter(body={"error": "unsupported_parameter"}),
            bad_parameter(body={"error": {"code": "unsupported_parameter", "param": "temperature"}, "code": "unauthorized"}),
            bad_parameter(body={"message": "Unsupported parameter: temperature"}),
            bad_parameter(body={"code": "unsupported_parameter", "param": ["temperature"]}),
        ]
        errors += [bad_parameter(status=status) for status in (401, 403, 429, 500)]
        errors += [bad_parameter(code=code) for code in ("unsupported_value", "invalid_value", "insufficient_quota", None)]
        errors += [bad_parameter(param=param) for param in ("max_tokens", "model", "messages", "extra_body", "temperature.value", None)]
        for error in errors:
            with self.subTest(error=error.body if isinstance(error, BadRequestError) else type(error)):
                self.assertIsNone(unsupported_optional_parameter(error, sent))
        self.assertIsNone(unsupported_optional_parameter(bad_parameter(), {}))
        self.assertIsNone(unsupported_optional_parameter(bad_parameter(), sent, protected_parameter="temperature"))


class AnalysisFallbackTests(unittest.IsolatedAsyncioTestCase):
    setUp = call_sites.AnalysisCallSiteTests.setUp
    analyze = call_sites.AnalysisCallSiteTests.analyze

    def enable(self):
        self.namespace[FLAG] = lambda: True
        sys.modules["src.config"].get_ai_request_params = lambda **kwargs: {
            **kwargs, "max_tokens": 4321, "extra_body": {"enable_thinking": False},
        }

    async def test_two_unsupported_parameters_use_only_remaining_attempts(self):
        self.enable()
        self.create.side_effect = [bad_parameter(), bad_parameter("response_format"), call_sites.response(json.dumps(call_sites.valid_analysis()))]
        result = await self.analyze()
        requests = [call.kwargs for call in self.create.call_args_list]
        self.assertEqual(len(requests), 3)
        self.assertIn("temperature", requests[0])
        self.assertNotIn("temperature", requests[1])
        self.assertIn("response_format", requests[1])
        self.assertNotIn("response_format", requests[2])
        for params in requests:
            self.assertEqual(params["max_tokens"], 4321)
            self.assertEqual(params["extra_body"], {"enable_thinking": False})
            self.assertEqual(params["messages"], requests[0]["messages"])
            self.assertEqual(params["model"], "fake-model")
        self.assertEqual(self.namespace["ai_call_failure_count"], 2)
        self.assertEqual(result["recommendation_score_v2"]["recommendation_score"], 74)
        self.scorer.assert_called_once_with(owner_id="owner-a", bayes_profile="fixture-bayes")

    async def test_default_off_keeps_original_parameters(self):
        self.create.side_effect = [bad_parameter(), call_sites.response(json.dumps(call_sites.valid_analysis()))]
        await self.analyze()
        self.assertIn("temperature", self.create.call_args.kwargs)
        self.assertEqual(self.create.await_count, 2)

    async def test_sdk_400_has_no_hidden_retry_and_keeps_completion_budget(self):
        self.enable()
        requests = []
        def respond(request):
            requests.append(json.loads(request.content))
            if len(requests) == 1:
                return httpx.Response(400, json={"error": {
                    "code": "unsupported_parameter", "param": "temperature",
                    "message": "synthetic unsupported parameter", "type": "invalid_request_error",
                }})
            return httpx.Response(200, json={
                "id": "fixture", "created": 0, "object": "chat.completion", "model": "fixture",
                "choices": [{"index": 0, "finish_reason": "stop", "message": {
                    "role": "assistant", "content": json.dumps(call_sites.valid_analysis()),
                }}],
            })
        async with httpx.AsyncClient(transport=httpx.MockTransport(respond), trust_env=False) as transport:
            async with AsyncOpenAI(api_key="synthetic", base_url="https://fixture.invalid/v1", http_client=transport) as client:
                self.namespace["client"] = client
                await self.analyze()
        self.assertEqual(len(requests), 2)
        self.assertNotIn("temperature", requests[1])
        self.assertEqual(requests[1]["max_tokens"], 4321)

    async def test_custom_token_field_is_never_removed(self):
        self.enable()
        self.namespace["AI_MAX_TOKENS_PARAM_NAME"] = lambda: "temperature"
        self.create.side_effect = [bad_parameter(), call_sites.response(json.dumps(call_sites.valid_analysis()))]
        await self.analyze()
        self.assertIn("temperature", self.create.call_args.kwargs)

    async def test_fallback_does_not_bypass_business_schema(self):
        self.enable()
        self.create.side_effect = [bad_parameter(), call_sites.response("{}"), call_sites.response("{}")]
        with self.assertRaises(self.namespace["AICallFailureException"]):
            await self.analyze()
        self.assertEqual(self.create.await_count, 3)
        self.scorer.assert_not_called()

    async def test_unsupported_value_does_not_remove_parameter(self):
        self.enable()
        self.create.side_effect = [bad_parameter(code="unsupported_value"), call_sites.response(json.dumps(call_sites.valid_analysis()))]
        await self.analyze()
        self.assertIn("temperature", self.create.call_args.kwargs)

    async def test_global_injection_cannot_readd_removed_parameter(self):
        self.enable()
        sys.modules["src.config"].get_ai_request_params = lambda **kwargs: {**kwargs, "temperature": 0.2}
        self.create.side_effect = [bad_parameter(), call_sites.response(json.dumps(call_sites.valid_analysis()))]
        await self.analyze()
        self.assertNotIn("temperature", self.create.call_args.kwargs)

    async def test_failure_threshold_is_not_reset_or_bypassed(self):
        self.enable()
        self.namespace["ai_call_failure_count"] = 2
        self.create.side_effect = bad_parameter()
        with self.assertRaises(self.namespace["AICallFailureException"]):
            await self.analyze()
        self.assertEqual(self.create.await_count, 1)
        self.assertEqual(self.namespace["ai_call_failure_count"], 3)

    async def test_last_attempt_does_not_spawn_fallback_request(self):
        self.enable()
        self.create.side_effect = [call_sites.response("{}"), call_sites.response("{}"), bad_parameter()]
        with self.assertRaises(BadRequestError):
            await self.analyze()
        self.assertEqual(self.create.await_count, 3)
        self.scorer.assert_not_called()

    async def test_concurrent_calls_and_next_call_do_not_share_omissions(self):
        self.enable()
        observed = {"a": [], "b": [], "c": []}
        async def create(**params):
            content = params["messages"][0]["content"][0]["text"]
            owner = next(key for key in observed if f"owner-{key}" in content)
            observed[owner].append(params)
            await asyncio.sleep(0)
            if owner == "a" and len(observed[owner]) == 1:
                raise bad_parameter()
            return call_sites.response(json.dumps(call_sites.valid_analysis()))
        self.create.side_effect = create
        async def run(owner):
            return await self.namespace["get_ai_analysis"](self.product, prompt_text=f"owner-{owner}", owner_id=owner)
        await asyncio.gather(run("a"), run("b"))
        await run("c")
        self.assertNotIn("temperature", observed["a"][1])
        self.assertIn("temperature", observed["b"][0])
        self.assertIn("temperature", observed["c"][0])

    async def test_existing_outer_retry_budget_unchanged(self):
        retry_namespace = call_sites.load_functions("src/utils.py", {"retry_on_failure"}, {
            "wraps": wraps, "asyncio": SimpleNamespace(sleep=mock.AsyncMock()), "json": json,
            "APIStatusError": APIStatusError, "HTTPError": HTTPError, "print": mock.Mock(),
        })
        counts = []
        for enabled in (False, True):
            self.namespace[FLAG] = lambda: enabled
            self.namespace["ai_call_failure_count"] = 0
            self.create.reset_mock()
            self.create.side_effect = bad_parameter()
            wrapped = retry_namespace["retry_on_failure"](retries=3, delay=5)(self.namespace["get_ai_analysis"])
            self.assertIsNone(await wrapped(self.product, prompt_text="fixture"))
            counts.append(self.create.await_count)
        self.assertEqual(counts, [5, 5])  # 既有全局失败阈值叠加外层重试的实际行为。


class RevisionConflict(Exception):
    current_revision = 8


class PrivateSettingsTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.config = {
            "id": "cfg-a", "config_revision": 7, "api_key": "synthetic-key",
            "api_base_url": "https://fixture.invalid", "model": "fixture",
            "extra_config": {FLAG: True, "PROXY_URL": "http://proxy.invalid", "other": "keep"},
        }
        self.storage = mock.Mock()
        self.storage.get_default_api_config.return_value = self.config
        self.storage.get_default_api_config_with_revision.return_value = self.config
        self.storage.save_user_api_config.return_value = self.config
        self.storage.update_default_api_config_fields.return_value = {**self.config, "config_revision": 8}
        self.namespace = call_sites.load_functions("src/web/settings_manager.py", {
            "get_ai_settings", "update_ai_settings", "_build_ai_settings_from_user_api_config",
            "_portable_ai_config_response", "_ai_url_origin",
        }, {
            "Depends": Depends, "_require_ai_access": lambda: None, "_require_ai_or_tasks_access": lambda: None,
            "HTTPException": HTTPException, "Optional": Optional, "urlparse": urlparse,
            "STORAGE_BACKEND": lambda: "postgres", "portable_mode": lambda: False,
            "get_storage": lambda: self.storage, "_resolve_current_user_id": lambda user: user["id"],
            "logger": mock.Mock(), "invalidate_ai_health_snapshot": mock.Mock(),
            "ApiConfigRevisionConflict": RevisionConflict, "_AI_SECRET_KEYS": {"OPENAI_API_KEY"},
            "_preserve_secret_on_empty": lambda settings, keys: dict(settings), "save_env_settings": mock.Mock(),
            "get_env_value": lambda key, default=None: default,
        })
        self.user = {"id": "owner-a"}

    async def test_pg_save_false_preserves_other_extras_and_current_owner(self):
        await self.namespace["update_ai_settings"]({FLAG: False}, self.user)
        owner, saved = self.storage.save_user_api_config.call_args.args
        self.assertEqual(owner, "owner-a")
        self.assertEqual(saved["extra_config"], {**self.config["extra_config"], FLAG: False})
        self.assertTrue(self.config["extra_config"][FLAG])

    async def test_omitted_flag_is_not_cleared(self):
        await self.namespace["update_ai_settings"]({"OPENAI_MODEL_NAME": "new"}, self.user)
        self.assertTrue(self.storage.save_user_api_config.call_args.args[1]["extra_config"][FLAG])

    async def test_invalid_flags_rejected_before_storage_for_all_backends(self):
        for backend in ("postgres", "file"):
            self.namespace["STORAGE_BACKEND"] = lambda: backend
            for value in (None, "true", "false", "on", "", 0, 1, [], {}):
                with self.subTest(backend=backend, value=value), self.assertRaises(HTTPException) as caught:
                    await self.namespace["update_ai_settings"]({FLAG: value}, self.user)
                self.assertEqual(caught.exception.status_code, 400)
        self.storage.get_default_api_config.assert_not_called()
        self.namespace["save_env_settings"].assert_not_called()

    async def test_portable_cas_preserves_revision_identity_and_false(self):
        self.namespace["portable_mode"] = lambda: True
        result = await self.namespace["update_ai_settings"]({FLAG: False, "config_revision": 7, "config_id": "cfg-a"}, self.user)
        self.storage.update_default_api_config_fields.assert_called_once_with(
            "owner-a", 7, "cfg-a", {"extra_config": {FLAG: False}},
        )
        self.assertEqual(result["config_revision"], 8)
        self.namespace["save_env_settings"].assert_not_called()

    async def test_portable_noop_does_not_invalidate_health(self):
        self.namespace["portable_mode"] = lambda: True
        await self.namespace["update_ai_settings"]({FLAG: True, "config_revision": 7, "config_id": "cfg-a"}, self.user)
        self.assertEqual(self.storage.update_default_api_config_fields.call_args.args[3], {})
        self.namespace["invalidate_ai_health_snapshot"].assert_not_called()

    async def test_portable_conflict_does_not_fall_back_to_legacy_save(self):
        self.namespace["portable_mode"] = lambda: True
        self.storage.update_default_api_config_fields.side_effect = RevisionConflict()
        with self.assertRaises(HTTPException) as caught:
            await self.namespace["update_ai_settings"]({FLAG: False, "config_revision": 7, "config_id": "cfg-a"}, self.user)
        self.assertEqual(caught.exception.status_code, 409)
        self.storage.save_user_api_config.assert_not_called()
        self.namespace["invalidate_ai_health_snapshot"].assert_not_called()

    async def test_portable_flag_write_still_requires_identity_and_revision(self):
        self.namespace["portable_mode"] = lambda: True
        for settings in ({FLAG: True}, {FLAG: True, "config_revision": 7}):
            with self.assertRaises(HTTPException) as caught:
                await self.namespace["update_ai_settings"](settings, self.user)
            self.assertEqual(caught.exception.status_code, 428)
        self.storage.update_default_api_config_fields.assert_not_called()

    async def test_readback_old_private_config_defaults_false_not_global(self):
        self.namespace["get_env_value"] = lambda key, default=None: "true"
        for portable in (False, True):
            self.namespace["portable_mode"] = lambda: portable
            for value in (True, False, "true", None):
                self.config["extra_config"] = {} if value is None else {FLAG: value}
                result = await self.namespace["get_ai_settings"](self.user)
                self.assertIs(result[FLAG], value is True)
                self.assertEqual(result["OPENAI_API_KEY"], "")

    async def test_file_save_false_and_boolean_readback(self):
        self.namespace["STORAGE_BACKEND"] = lambda: "file"
        reload_config = mock.Mock()
        with mock.patch.dict(sys.modules, {"src.config": SimpleNamespace(reload_config=reload_config)}):
            await self.namespace["update_ai_settings"]({FLAG: False}, self.user)
        saved, allowed = self.namespace["save_env_settings"].call_args.args
        self.assertIs(saved[FLAG], False)
        self.assertIn(FLAG, allowed)
        reload_config.assert_called_once()
        for value in ("true", "false", None):
            self.namespace["get_env_value"] = lambda key, default=None: value if key == FLAG and value is not None else default
            self.assertIs((await self.namespace["get_ai_settings"](self.user))[FLAG], value == "true")


class WorkerAndConfigTests(unittest.TestCase):
    def test_both_workers_override_inherited_flags_and_accessor_reads_them(self):
        for path in ("src/web/task_manager.py", "src/web/scheduler.py"):
            storage = mock.Mock()
            configs = {
                owner: {"api_key": "fixture", "api_base_url": "https://fixture.invalid", "model": "fixture", "extra_config": extra}
                for owner, extra in (("a", {FLAG: True}), ("b", {}), ("c", {FLAG: False}), ("d", {FLAG: "true"}))
            }
            storage.get_default_api_config.side_effect = lambda owner: configs[owner]
            namespace = call_sites.load_functions(path, {"_apply_owner_ai_env_overrides", "_parse_bool_for_env"}, {
                "Dict": Dict, "Optional": Optional, "is_multi_user_mode": lambda: True,
                "get_storage": lambda: storage, "logger": mock.Mock(),
            })
            inherited = {FLAG: "true", RUNTIME_FLAG: "true"}
            for owner in configs:
                child = dict(inherited)
                namespace["_apply_owner_ai_env_overrides"](child, owner)
                self.assertNotIn(FLAG, child)
                self.assertEqual(child[RUNTIME_FLAG], "true" if owner == "a" else "false")
                config_namespace = call_sites.load_functions("src/config.py", {FLAG, "_get_runtime_override_value"}, {
                    "os": SimpleNamespace(environ=child), "get_env_value": lambda key, default: "true",
                })
                self.assertIs(config_namespace[FLAG](), owner == "a")
            self.assertEqual(inherited, {FLAG: "true", RUNTIME_FLAG: "true"})
            storage.get_default_api_config.side_effect = RuntimeError("synthetic storage failure")
            with self.assertRaises(RuntimeError):
                namespace["_apply_owner_ai_env_overrides"](dict(inherited), "a")
            namespace["logger"].error.assert_called_once()

    def test_file_environment_serializes_boolean_and_preserves_omitted_value(self):
        for settings in ({FLAG: False}, {}):
            fake_open = mock.mock_open(read_data=f"{FLAG}=true\nOTHER=keep\n")
            fake_environment = {}
            namespace = call_sites.load_functions("src/config.py", {"save_env_settings"}, {
                "PORTABLE_MODE": False, "open": fake_open,
                "os": SimpleNamespace(path=SimpleNamespace(exists=lambda path: True), environ=fake_environment),
            })
            namespace["save_env_settings"](settings, [FLAG])
            written = "".join(call.args[0] for call in fake_open().write.call_args_list)
            expected = "false" if settings else "true"
            self.assertIn(f"{FLAG}={expected}\n", written)
            self.assertIn("OTHER=keep\n", written)
            self.assertTrue(all(call.kwargs["encoding"] == "utf-8" for call in fake_open.call_args_list if call.args))

    def test_config_accessor_defaults_off_and_rejects_ambiguous_values(self):
        for value in (None, "", "false", "yes", "1", "true", " TRUE "):
            namespace = call_sites.load_functions("src/config.py", {FLAG}, {
                "_get_runtime_override_value": lambda key: None,
                "get_env_value": lambda key, default: value if value is not None else default,
            })
            self.assertIs(namespace[FLAG](), value in ("true", " TRUE "))


if __name__ == "__main__":
    unittest.main()
