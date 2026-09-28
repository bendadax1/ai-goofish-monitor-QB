"""离线通知兼容/代理诊断：真实函数体 + fake 配置和发送端，无 .env。"""

import ast
import asyncio
from contextlib import contextmanager
from contextvars import ContextVar
import json
import logging
import os
from pathlib import Path
from types import SimpleNamespace
from typing import Any, Dict, List, Optional
import unittest
from unittest import mock
from urllib.parse import urlparse

import httpx
from src.httpx_compat import create_sdk_http_client


ROOT = Path(__file__).resolve().parents[1]


def proxy_environment(values):
    # 保留 Windows SystemRoot 等运行时变量，只隔离可能影响代理/TLS 的配置。
    environment = {key: value for key, value in os.environ.items() if not key.lower().endswith("_proxy") and key not in {"SSL_CERT_FILE", "SSL_CERT_DIR"}}
    environment.update(values)
    return mock.patch.dict(os.environ, environment, clear=True)


def load(relative, names, namespace):
    path = ROOT / relative
    tree = ast.parse(path.read_text(encoding="utf-8"))
    nodes = [node for node in tree.body if isinstance(node, (ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)) and node.name in names]
    assert {node.name for node in nodes} == names
    exec(compile(ast.Module(body=nodes, type_ignores=[]), str(path), "exec"), namespace)


def notification_namespace():
    values = {"NTFY_TOPIC": "local", "NTFY_ENABLED": True}
    ns = {
        "Any": Any, "Dict": Dict, "List": List, "Optional": Optional,
        "json": json, "os": os, "urlparse": urlparse, "asyncio": asyncio,
        "ContextVar": ContextVar, "contextmanager": contextmanager,
        "logger": logging.getLogger(__name__),
        "get_env_value": lambda key, default="": values.get(key, default),
        "get_bool_env_value": lambda key, default=False: values.get(key, default),
        "_NTFY_DEFAULT_SERVER": "https://ntfy.sh",
    }
    load("src/notifier/config.py", {
        "NotificationConfig", "_notification_get_effective_config", "_notification_apply_overrides",
        "_notification_get", "_notification_getitem", "_notification_contains", "parse_legacy_ntfy_url",
    }, ns)
    cls = ns["NotificationConfig"]
    cls.apply_overrides = ns["_notification_apply_overrides"]
    cls.get = ns["_notification_get"]
    cls.__getitem__ = ns["_notification_getitem"]
    cls.__contains__ = ns["_notification_contains"]
    ns["config"] = cls()
    ns["STORAGE_BACKEND"] = lambda: "file"
    load("src/notifier/__init__.py", {
        "_notifier_normalize_text", "_notifier_to_bool", "_notifier_parse_headers",
        "_notifier_build_overrides", "_notifier_is_postgres_mode", "_notifier_resolve_owner_id",
        "_notifier_resolve_bound_task", "_notifier_extract_bound_task", "_notifier_load_user_targets",
        "_notifier_dispatch_targets", "_notifier_build_local_targets", "_notifier_send_test_notification",
    }, ns)
    load("src/notifier/channels.py", {"_ntfy_publish_target", "_get_channel_proxies"}, ns)
    return ns, values


class NotificationCompatibilityTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.ns, self.values = notification_namespace()
        self.config = self.ns["config"]
        self.sent = []

        async def fake_send():
            before = self.ns["_ntfy_publish_target"]()
            await asyncio.sleep(0)
            after = self.ns["_ntfy_publish_target"]()
            self.assertEqual(before, after)
            self.sent.append(after)
            return True

        self.ntfy = SimpleNamespace(send_test_notification=mock.AsyncMock(side_effect=fake_send))
        self.other = SimpleNamespace(send_test_notification=mock.AsyncMock(side_effect=AssertionError("不能发送其他渠道")))
        self.notifier = SimpleNamespace(channels={"ntfy": self.ntfy, "gotify": self.other}, channel_name_map={"ntfy": "Ntfy", "gotify": "Gotify"})

    def test_ntfy_legacy_ipv6_path_and_token(self):
        parse = self.ns["parse_legacy_ntfy_url"]
        self.assertEqual(parse("http://[::1]:8080/topic"), ("http://[::1]:8080", "topic", ""))
        self.assertEqual(parse('"https://:fake-token@host.test/ntfy/topic"'), ("https://host.test/ntfy", "topic", "fake-token"))
        overrides = self.ns["_notifier_build_overrides"]("ntfy", {"topic_url": "https://:fake-token@host.test/ntfy/topic"})
        with self.config.apply_overrides(overrides):
            self.assertEqual(self.ns["_ntfy_publish_target"](), ("https://host.test/ntfy/topic", "fake-token"))
        self.assertEqual(self.ns["_ntfy_publish_target"](), ("https://ntfy.sh/local", None))

    async def test_single_channel_only_and_reload_next_call(self):
        send = self.ns["_notifier_send_test_notification"]
        self.assertTrue(await send(self.notifier, "ntfy"))
        self.values["NTFY_TOPIC"] = "updated"
        self.assertTrue(await send(self.notifier, "ntfy"))
        self.assertEqual(self.sent, [("https://ntfy.sh/local", None), ("https://ntfy.sh/updated", None)])
        self.assertEqual(self.ntfy.send_test_notification.await_count, 2)
        self.other.send_test_notification.assert_not_called()

    async def test_unknown_channel_does_not_send(self):
        self.assertFalse(await self.ns["_notifier_send_test_notification"](self.notifier, "unknown"))
        self.ntfy.send_test_notification.assert_not_called()

    async def test_private_configs_concurrent_owner_and_config_id_isolation(self):
        self.ns["STORAGE_BACKEND"] = lambda: "postgres"
        rows = {
            owner: [{"id": owner + "-id", "channel_type": "ntfy", "config": {"topic": owner, "token": owner + "-token"}}]
            for owner in ("a", "b")
        }
        storage = SimpleNamespace(get_user_notification_configs=mock.Mock(side_effect=lambda owner: rows[owner]))
        self.ns["get_storage"] = lambda: storage
        send = self.ns["_notifier_send_test_notification"]
        self.assertEqual(await asyncio.gather(send(self.notifier, "ntfy", owner_id="a"), send(self.notifier, "ntfy", owner_id="b")), [True, True])
        self.assertCountEqual(self.sent, [("https://ntfy.sh/a", "a-token"), ("https://ntfy.sh/b", "b-token")])
        self.assertFalse(await send(self.notifier, "ntfy", owner_id="a", config_id="b-id"))
        self.assertEqual(len(self.sent), 2)
        self.assertEqual(self.ns["_ntfy_publish_target"](), ("https://ntfy.sh/local", None))

    async def test_failed_dispatch_restores_context_and_does_not_retry(self):
        self.ntfy.send_test_notification.side_effect = RuntimeError("synthetic failure")
        result = await self.ns["_notifier_dispatch_targets"](self.notifier, [{"channel": "ntfy", "display_name": "Ntfy", "overrides": {"NTFY_TOPIC": "private"}}], "send_test_notification")
        self.assertEqual(result, {"Ntfy": False})
        self.ntfy.send_test_notification.assert_awaited_once()
        self.assertEqual(self.config.get("NTFY_TOPIC"), "local")

    def test_channel_proxy_switch_does_not_modify_environment(self):
        before = dict(os.environ)
        proxies = self.ns["_get_channel_proxies"]
        with self.config.apply_overrides({"PROXY_URL": "http://proxy.test:8080", "PROXY_NTFY_ENABLED": True}):
            self.assertEqual(proxies("PROXY_NTFY_ENABLED"), {"http": "http://proxy.test:8080", "https": "http://proxy.test:8080"})
            self.assertIsNone(proxies("PROXY_GOTIFY_ENABLED"))
        self.assertIsNone(proxies("PROXY_NTFY_ENABLED"))
        self.assertEqual(dict(os.environ), before)


class HttpxProxyCharacterizationTests(unittest.TestCase):
    """锁定版本行为与实例级兼容入口回归，不进行网络发送。"""

    def test_normal_no_proxy_variants_construct_without_network(self):
        for key in ("NO_PROXY", "no_proxy"):
            for value in ("", "localhost,127.0.0.1,::1", "127.0.0.0/8", "*"):
                with self.subTest(key=key, value=value), proxy_environment({key: value, "HTTPS_PROXY": "http://proxy.invalid:8080"}):
                    with httpx.Client():
                        pass

    def test_ipv6_cidr_environment_proxy_construction(self):
        # B1b 已记录原生构造失败；B1c 应通过兼容客户端。
        with proxy_environment({"NO_PROXY": "::1/128", "HTTPS_PROXY": "http://proxy.invalid:8080"}):
            with create_sdk_http_client(asynchronous=False):
                pass

    def test_explicit_proxy_and_trust_env_false_do_not_parse_cidr(self):
        with proxy_environment({"NO_PROXY": "::1/128", "HTTPS_PROXY": "http://proxy.invalid:8080"}):
            with httpx.Client(proxy="http://proxy.invalid:8080"):
                pass
            with httpx.Client(trust_env=False):
                pass


if __name__ == "__main__":
    unittest.main()
