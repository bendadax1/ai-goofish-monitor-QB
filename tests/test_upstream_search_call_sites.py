"""隔离执行 scraper 的实际搜索动作块，不启动浏览器或导入真实业务配置。

此测试验证 8 处接线和点击/等待预算，不冒充完整 scraper 或字段级载荷验收。
"""

import ast
import copy
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock

from src.search_requests import expect_new_search_response
from tests.test_upstream_search_requests import API, EventPage, request


ROOT = Path(__file__).resolve().parents[1]


def search_action_nodes():
    tree = ast.parse((ROOT / "src/scraper.py").read_text(encoding="utf-8"))
    return sorted([
        node for node in ast.walk(tree) if isinstance(node, ast.AsyncWith)
        and any(isinstance(item.context_expr, ast.Call)
                and isinstance(item.context_expr.func, ast.Name)
                and item.context_expr.func.id == "_expect_new_search_response" for item in node.items)
    ], key=lambda node: node.lineno)


class ScraperSearchActionTests(unittest.IsolatedAsyncioTestCase):
    async def test_all_search_actions_bind_new_request_with_original_budgets(self):
        nodes = search_action_nodes()
        self.assertEqual(len(nodes), 8)
        expectations = [
            (30000, [], "goto", ("https://fixture.invalid/search",), {"wait_until": "domcontentloaded", "timeout": 60000}),
            (20000, [(2, 4)], "click", ("text=一天内",), {}),
            (20000, [(4, 7)], "click", ("text=最新",), {}),
            (20000, [(4, 6)], "click", ("text=个人闲置",), {}),
            (20000, [(2, 4)], "click", (), {}),
            (20000, [(2, 4)], "click", (), {}),
            (20000, [(2, 3)], "click", (), {}),
            (12000, [(0.8, 1.2)], "click", (), {"timeout": 3000}),
        ]
        for node, (timeout, sleeps, method, args, kwargs) in zip(nodes, expectations):
            with self.subTest(line=node.lineno):
                page = EventPage()
                old, submitted, parallel = request(), request(), request()
                expected = SimpleNamespace(request=submitted)
                async def action(*_args, **_kwargs):
                    page.emit("response", SimpleNamespace(request=old))
                    page.emit("request", request(method="OPTIONS"))
                    page.emit("request", submitted)
                    page.emit("request", parallel)
                    page.emit("response", SimpleNamespace(request=parallel))
                    page.emit("response", expected)
                click = AsyncMock(side_effect=action)
                goto = AsyncMock(side_effect=action)
                page.click, page.goto = click, goto
                trigger = SimpleNamespace(click=click)
                trigger.first = trigger
                sleep = AsyncMock()
                namespace = {
                    "page": page, "API_URL_PATTERN": API,
                    "_expect_new_search_response": expect_new_search_response,
                    "random_sleep": sleep, "search_url": "https://fixture.invalid/search",
                    "new_publish_option": "一天内", "free_shipping_trigger": trigger,
                    "trigger": trigger, "search_btn": trigger, "chosen": {"node": trigger},
                    "timeout_ms": 12000,
                }
                wrapper = ast.parse("async def run():\n    pass\n").body[0]
                wrapper.body = [copy.deepcopy(node), ast.Return(value=ast.Await(value=ast.Attribute(
                    value=ast.Name(id="response_info", ctx=ast.Load()), attr="value", ctx=ast.Load(),
                )))]
                module = ast.fix_missing_locations(ast.Module(body=[wrapper], type_ignores=[]))
                exec(compile(module, "<isolated scraper action>", "exec"), namespace)
                self.assertIs(await namespace["run"](), expected)
                (goto if method == "goto" else click).assert_awaited_once_with(*args, **kwargs)
                (click if method == "goto" else goto).assert_not_awaited()
                self.assertEqual([call.args for call in sleep.await_args_list], sleeps)
                self.assertEqual(page.timeouts, [timeout])
                self.assertEqual(page.listeners, {"request": [], "response": []})

    def test_no_endpoint_only_search_response_wait_remains(self):
        tree = ast.parse((ROOT / "src/scraper.py").read_text(encoding="utf-8"))
        response_waits = [node for node in ast.walk(tree) if isinstance(node, ast.Call)
                          and isinstance(node.func, ast.Attribute) and node.func.attr == "expect_response"]
        self.assertEqual(len(response_waits), 1)  # 详情请求保持原实现，非本批范围。
        self.assertIn("DETAIL_API_URL_PATTERN", ast.unparse(response_waits[0]))
        self.assertNotIn("is_search_response", ast.unparse(tree))


if __name__ == "__main__":
    unittest.main()
