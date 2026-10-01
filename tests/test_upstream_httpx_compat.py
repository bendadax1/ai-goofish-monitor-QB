"""实例级代理兼容验收；实际 HTTPX/SDK 接线，所有发送经 fake transport。

同时支持 httpx 0.28.1 与 httpx2：两者模块名不同，传输构造点也不同，
统一在此解析，避免测试与具体 HTTPX 版本耦合。
"""

import asyncio
import ast
from contextlib import ExitStack
import importlib
import logging
import os
import ssl
import time
from types import SimpleNamespace
from typing import Any, Dict, Optional, Tuple
import unittest
from unittest import mock

# 与 src/httpx_compat 采用同一套解析规则：跟随 openai SDK 的实际依赖，
# 而不是按名字顺序取第一个（双库共存时容易选错）。
from src.httpx_compat import HTTPX_MODULE_NAME as _httpx_name

httpx = importlib.import_module(_httpx_name)
_httpx_client_mod = importlib.import_module(f"{_httpx_name}._client")

from openai import AsyncOpenAI, DefaultAsyncHttpxClient, DefaultHttpxClient, OpenAI

from src.httpx_compat import create_sdk_http_client
from tests.test_upstream_call_sites import ROOT, load_functions
from tests.test_upstream_notification_compat import proxy_environment


class FakeTransport(httpx.BaseTransport, httpx.AsyncBaseTransport):
    instances = []

    def __init__(self, **kwargs):
        self.kwargs = kwargs
        self.requests = []
        self.closed = False
        self.route = str(kwargs["proxy"].url) if kwargs.get("proxy") else "direct"
        self.instances.append(self)

    def handle_request(self, request):
        self.requests.append(request)
        if request.url.path == "/redirect":
            return httpx.Response(302, headers={"Location": "http://[::1]/after"})
        return httpx.Response(200, json={
            "route": self.route, "id": "test", "created": 0, "model": "test",
            "object": "chat.completion", "choices": [{"index": 0, "finish_reason": "stop", "message": {"role": "assistant", "content": "OK"}}],
        })

    async def handle_async_request(self, request):
        await asyncio.sleep(0)
        return self.handle_request(request)

    def close(self):
        self.closed = True

    async def aclose(self):
        self.close()


def _make_fake_transport(*args, **kwargs):
    """httpx2 的 _init_transport / _init_proxy_transport 替身。

    httpx2 的 Client.__init__ 不再引用模块级 HTTPTransport 名字，而是调用
    这两个方法，因此必须做方法级替换。关键差异：

    - 替换后调用形式是 `self._init_transport(...)` / `self._init_proxy_transport(proxy, ...)`；
      `mock.patch.object` 以普通函数替换类属性，**不会**自动绑定 self，
      因此 args[0] 恒为实例自身。
    - `_init_proxy_transport` 的代理对象在 args[1]；`_init_transport` 无代理。

    真实实现会构造并发起网络传输；测试只需一个可观测替身，route 语义
    与 httpx 0.28.1 路径下的 FakeTransport 保持一致。
    """
    proxy = args[1] if len(args) > 1 else None
    proxy = proxy if proxy is not None else kwargs.get("proxy")
    if proxy is not None:
        kwargs["proxy"] = proxy
    else:
        kwargs.pop("proxy", None)
    return FakeTransport(**kwargs)


class ProxyFixture(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.stack = ExitStack()
        self.addCleanup(self.stack.close)
        self.stack.enter_context(proxy_environment({
            "HTTP_PROXY": "http://proxy.test:8080", "HTTPS_PROXY": "http://secure-proxy.test:8080",
            "NO_PROXY": "::1/128,2001:db8::/32,localhost,127.0.0.1,example.test,.sub.test,port.test:8443",
        }))
        FakeTransport.instances = []
        # 替换 HTTPX 的传输构造点，保留代理匹配及重定向主链。
        # 解析到当前活跃的 HTTPX 库（httpx 或 httpx2），避免绑定具体版本。
        self.stack.enter_context(mock.patch.object(_httpx_client_mod, "HTTPTransport", FakeTransport))
        self.stack.enter_context(mock.patch.object(_httpx_client_mod, "AsyncHTTPTransport", FakeTransport))
        self._install_httpx2_transport_patches()

    def _install_httpx2_transport_patches(self):
        """httpx2 不再在构造期引用模块级 HTTPTransport 名字。

        httpx2 的 Client.__init__ 改为调用 `_init_transport` /
        `_init_proxy_transport` 方法，因此模块级 patch 不生效。仅在检测到
        该实现时补上方法级 patch；httpx 0.28.1 没有这两个方法（或语义不同），
        不做任何改动，避免破坏既有回归。
        """
        if _httpx_name != "httpx2":
            return
        for cls_name in ("Client", "AsyncClient"):
            cls = getattr(_httpx_client_mod, cls_name, None)
            if cls is None:
                continue
            for meth in ("_init_transport", "_init_proxy_transport"):
                if not hasattr(cls, meth):
                    continue
                self.stack.enter_context(mock.patch.object(cls, meth, _make_fake_transport))

    def assert_route(self, response, expected):
        self.assertEqual(response.json()["route"], expected)


class ProxyTests(ProxyFixture):
    async def test_async_ipv6_network_boundaries_and_mixed_bypass(self):
        before = dict(os.environ)
        async with create_sdk_http_client() as client:
            cases = {
                "http://[::1]/": "direct", "https://[2001:db8::1]/": "direct",
                "https://[2001:db8:ffff:ffff:ffff:ffff:ffff:ffff]/": "direct",
                "https://[2001:db9::1]/": "http://secure-proxy.test:8080",
                "http://[::2]/": "http://proxy.test:8080", "http://127.0.0.1/": "direct",
                "http://localhost/": "direct", "http://example.test/": "direct",
                "https://a.example.test/": "direct", "http://notexample.test/": "http://proxy.test:8080",
                "http://a.sub.test/": "direct", "http://sub.test/": "http://proxy.test:8080",
                "https://port.test:8443/": "direct", "https://port.test:443/": "http://secure-proxy.test:8080",
            }
            for url, route in cases.items():
                with self.subTest(url=url):
                    self.assert_route(await client.get(url), route)
        self.assertTrue(all(transport.closed for transport in FakeTransport.instances))
        self.assertEqual(dict(os.environ), before)

    def test_sync_uses_same_cidr_bypass(self):
        with create_sdk_http_client(asynchronous=False) as client:
            self.assert_route(client.get("http://[::1]/"), "direct")
            self.assert_route(client.get("http://[::2]/"), "http://proxy.test:8080")

    async def test_redirect_rechecks_target_without_dns(self):
        with mock.patch("socket.getaddrinfo", side_effect=AssertionError("不得额外做 DNS")):
            async with create_sdk_http_client() as client:
                result = await client.get("http://external.test/redirect")
        self.assert_route(result, "direct")
        self.assertEqual(len(result.history), 1)
        routes = {transport.route: [str(request.url) for request in transport.requests] for transport in FakeTransport.instances}
        self.assertEqual(routes["http://proxy.test:8080"], ["http://external.test/redirect"])
        self.assertEqual(routes["direct"], ["http://[::1]/after"])

    async def test_explicit_proxy_retains_existing_precedence(self):
        async with create_sdk_http_client(proxy="http://explicit.test:8080") as client:
            self.assert_route(await client.get("http://[::1]/"), "http://explicit.test:8080")
            self.assertEqual(client._no_proxy_ipv6_networks, ())

    async def test_trust_env_false_is_direct(self):
        async with create_sdk_http_client(trust_env=False) as client:
            self.assert_route(await client.get("http://external.test/"), "direct")
            self.assertEqual(client._no_proxy_ipv6_networks, ())

    async def test_lowercase_variable_and_wildcard(self):
        for value, expected in (("::1/128", "http://proxy.test:8080"), ("*", "direct")):
            with proxy_environment({"http_proxy": "http://proxy.test:8080", "no_proxy": value}):
                async with create_sdk_http_client() as client:
                    self.assert_route(await client.get("http://[::1]/"), "direct")
                    self.assert_route(await client.get("http://external.test/"), expected)

    async def test_environment_snapshot_does_not_cross_client_instances(self):
        with proxy_environment({"HTTP_PROXY": "http://a.test:8080", "NO_PROXY": "::1/128"}):
            first = create_sdk_http_client()
        with proxy_environment({"HTTP_PROXY": "http://b.test:8080", "NO_PROXY": "::2/128"}):
            second = create_sdk_http_client()
        async with first, second:
            a, b = await asyncio.gather(first.get("http://[::1]/"), second.get("http://[::1]/"))
            self.assert_route(a, "direct")
            self.assert_route(b, "http://b.test:8080")

    def test_invalid_cidr_is_not_silently_dropped(self):
        """非法 no_proxy CIDR 不得被静默当作有效网络。

        两个 HTTPX 版本的处理不同，断言需分版本：
        - httpx 0.28.1 把 CIDR 当域名/端口解析，非法前缀触发 InvalidURL；
        - httpx2 已能正确识别 CIDR 语法，非法前缀不抛错，但必须**不被**
          当成有效旁路网络（`_no_proxy_ipv6_networks` 为空）。
        共同底线：非法输入绝不进入旁路集合。
        """
        with proxy_environment({"HTTP_PROXY": "http://proxy.test:8080", "NO_PROXY": "::1/129"}):
            if _httpx_name == "httpx2":
                client = create_sdk_http_client(asynchronous=False)
                with client:
                    self.assertEqual(client._no_proxy_ipv6_networks, ())
            else:
                with self.assertRaises(httpx.InvalidURL):
                    create_sdk_http_client(asynchronous=False)

    async def test_tls_options_and_sdk_defaults_are_preserved(self):
        context = ssl.create_default_context()
        async with create_sdk_http_client(verify=context, timeout=900) as client:
            self.assertTrue(client.follow_redirects)
            self.assertEqual(client.timeout, httpx.Timeout(900))
            for transport in FakeTransport.instances:
                self.assertIs(transport.kwargs["verify"], context)
                self.assertTrue(transport.kwargs["trust_env"])
        with proxy_environment({}):
            async with DefaultAsyncHttpxClient() as original, create_sdk_http_client() as patched:
                self.assertEqual(original.timeout, patched.timeout)
                self.assertEqual(original.follow_redirects, patched.follow_redirects)
                self.assertEqual(original._transport.kwargs["limits"], patched._transport.kwargs["limits"])
                self.assertTrue(patched._transport.kwargs["verify"])
            with DefaultHttpxClient() as original, create_sdk_http_client(asynchronous=False) as patched:
                self.assertEqual(original.timeout, patched.timeout)
                self.assertEqual(original._transport.kwargs["limits"], patched._transport.kwargs["limits"])

    async def test_real_sdk_completion_and_no_added_retry(self):
        transport = create_sdk_http_client()
        async with AsyncOpenAI(api_key="synthetic", base_url="http://[::1]/v1", http_client=transport, max_retries=0) as client:
            result = await client.chat.completions.create(model="fixture", messages=[{"role": "user", "content": "synthetic"}])
            self.assertEqual(result.choices[0].message.content, "OK")
            self.assertEqual(client.max_retries, 0)
        self.assertEqual(sum(len(item.requests) for item in FakeTransport.instances), 1)
        self.assertTrue(transport.is_closed)

    async def test_best_effort_cleanup_matches_sdk_wrapper(self):
        client = create_sdk_http_client()
        client.__del__()
        await asyncio.sleep(0)
        self.assertTrue(client.is_closed)
        synchronous = create_sdk_http_client(asynchronous=False)
        synchronous.__del__()
        self.assertTrue(synchronous.is_closed)


class SDKCallSiteTests(ProxyFixture):
    # 以下用真实函数体接线，隔离模块初始化和业务配置，不启动服务。
    async def test_global_config_client_initializes_with_cidr(self):
        ns = load_functions("src/config.py", {"initialize_ai_client"}, {
            "API_KEY": lambda: "synthetic", "BASE_URL": lambda: "http://[::1]/v1",
            "MODEL_NAME": lambda: "fixture", "PROXY_URL": lambda: "", "PROXY_AI_ENABLED": lambda: False,
            "STORAGE_BACKEND": lambda: "file", "httpx": httpx, "AsyncOpenAI": AsyncOpenAI,
            "create_sdk_http_client": create_sdk_http_client, "logger": logging.getLogger(__name__),
        })
        self.assertTrue(ns["initialize_ai_client"]())
        client = ns["client"]
        try:
            self.assertTrue(client._client._no_proxy_ipv6_networks)
            self.assertEqual(client._client._transport_for_url(httpx.URL("http://[::1]")), client._client._transport)
        finally:
            await client.close()

    async def test_private_criteria_config_and_original_timeout(self):
        ns = load_functions("src/prompt_utils.py", {"_resolve_criteria_ai_runtime", "_parse_bool_setting"}, {
            "Optional": Optional, "Tuple": Tuple, "AsyncOpenAI": AsyncOpenAI, "httpx": httpx,
            "config": SimpleNamespace(STORAGE_BACKEND=lambda: "postgres"),
            "_get_owner_default_api_config": lambda owner: {"api_key": "synthetic-" + owner, "api_base_url": "http://[::1]/v1", "model": owner},
            "CRITERIA_REQUEST_TIMEOUT_SECONDS": 900, "create_sdk_http_client": create_sdk_http_client,
        })
        for owner in ("owner-a", "owner-b"):
            client, model, transport = ns["_resolve_criteria_ai_runtime"](owner)
            try:
                self.assertEqual(model, owner)
                self.assertEqual(client.api_key, "synthetic-" + owner)
                self.assertEqual(client.timeout, httpx.Timeout(900))
                self.assertIs(client._client, transport)
                self.assertTrue(transport._no_proxy_ipv6_networks)
            finally:
                await client.close()

    async def test_three_legacy_health_probes_use_compat_and_close(self):
        ns = load_functions("src/web/ai_health.py", {
            "_run_web_text_probe_sync", "_run_backend_text_probe_async", "_run_vision_probe_async",
        }, {
            "Dict": Dict, "Any": Any, "time": time, "httpx": httpx,
            "AsyncOpenAI": AsyncOpenAI, "OpenAI": OpenAI,
            "create_sdk_http_client": create_sdk_http_client,
            "_build_request_kwargs": lambda config, content: {"model": "test", "messages": [{"role": "user", "content": content}]},
            "_build_probe_result": lambda **kwargs: kwargs, "_build_vision_result": lambda **kwargs: kwargs,
            "_classify_vision_error": lambda _: "unknown", "_now_text": lambda: "synthetic",
            "_VISION_TEST_IMAGE_DATA_URL": "data:image/png;base64,synthetic",
        })
        config = {"api_key": "synthetic", "base_url": "http://[::1]/v1"}
        self.assertTrue(ns["_run_web_text_probe_sync"](config)["success"])
        self.assertTrue((await ns["_run_backend_text_probe_async"](config))["success"])
        self.assertEqual((await ns["_run_vision_probe_async"](config))["status"], "supported")
        self.assertTrue(all(item.closed for item in FakeTransport.instances))
        self.assertEqual(sum(len(item.requests) for item in FakeTransport.instances), 3)

    def test_portable_security_probe_keeps_explicit_transport_policy(self):
        source = (ROOT / "src/web/ai_health.py").read_text(encoding="utf-8")
        function = next(node for node in ast.parse(source).body if getattr(node, "name", None) == "_send_manual_ai_test")
        clients = [node for node in ast.walk(function) if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) and node.func.attr == "AsyncClient"]
        self.assertEqual(len(clients), 1)
        options = {option.arg: option.value for option in clients[0].keywords}
        self.assertFalse(ast.literal_eval(options["trust_env"]))
        self.assertFalse(ast.literal_eval(options["follow_redirects"]))
        self.assertEqual(ast.dump(options["transport"]), "Name(id='transport', ctx=Load())")
        self.assertNotIn("create_sdk_http_client", ast.unparse(function))


if __name__ == "__main__":
    unittest.main()
