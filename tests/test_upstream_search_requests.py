"""搜索请求/翻页仿真；不导入真实配置或启动浏览器。"""

import asyncio
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, Mock

from src.search_requests import (
    NEXT_PAGE_SELECTOR, advance_search_page, capture_new_request_response,
    is_search_request, is_search_response,
    expect_new_search_response,
)


API = "h5api.m.goofish.com/h5/mtop.taobao.idlemtopsearch.pc.search"
URL = "https://" + API + "/1.0/?data=fake"


def request(url=URL, method="POST", response=None):
    return SimpleNamespace(url=url, method=method, response=AsyncMock(return_value=response))


class _Expectation:
    def __init__(self, page, predicate):
        self.page = page
        self.predicate = predicate

    async def __aenter__(self):
        self.page.predicate = self.predicate
        self.page.request_future = asyncio.get_running_loop().create_future()
        return SimpleNamespace(value=self.page.request_future)

    async def __aexit__(self, *_args):
        self.page.predicate = None
        return False


class FakePage:
    def __init__(self):
        self.predicate = None
        self.request_future = None
        self.button = SimpleNamespace(
            count=AsyncMock(return_value=1), is_visible=AsyncMock(return_value=True),
            is_enabled=AsyncMock(return_value=True), scroll_into_view_if_needed=AsyncMock(),
            click=AsyncMock(),
        )
        self.button.first = self.button

    def expect_request(self, predicate, timeout):
        self.timeout = timeout
        return _Expectation(self, predicate)

    def emit(self, req):
        if self.predicate and self.predicate(req) and not self.request_future.done():
            self.request_future.set_result(req)

    def locator(self, selector):
        self.selector = selector
        return self.button


class ResponseExpectation:
    """仿真独立于动作耗时的响应截止时间和监听释放。"""
    def __init__(self, page, predicate, timeout):
        self.page = page
        self.predicate = predicate
        self.timeout = timeout

    async def __aenter__(self):
        self.future = asyncio.get_running_loop().create_future()
        self.page.on("response", self.on_response)
        self.timer = asyncio.get_running_loop().call_later(self.timeout / 1000, self.expire)
        return SimpleNamespace(value=self.future)

    def expire(self):
        if not self.future.done():
            self.future.set_exception(asyncio.TimeoutError())

    def on_response(self, response):
        if not self.future.done() and self.predicate(response):
            self.future.set_result(response)

    async def __aexit__(self, exc_type, *_args):
        try:
            if exc_type is None:
                await self.future
            elif self.future.done() and not self.future.cancelled():
                self.future.exception()
            else:
                self.future.cancel()
        finally:
            self.timer.cancel()
            self.page.remove_listener("response", self.on_response)
        return False


class EventPage:
    def __init__(self):
        self.listeners = {"request": [], "response": []}
        self.timeouts = []

    def on(self, event, callback):
        self.listeners[event].append(callback)

    def remove_listener(self, event, callback):
        self.listeners[event].remove(callback)

    def emit(self, event, value):
        for callback in list(self.listeners[event]):
            callback(value)

    def expect_response(self, predicate, timeout):
        self.timeouts.append(timeout)
        return ResponseExpectation(self, predicate, timeout)


class SearchActionContextTests(unittest.IsolatedAsyncioTestCase):
    async def test_first_new_request_identity_wins_out_of_order_responses(self):
        page = EventPage()
        old, first, second = request(), request(), request()
        unrelated = request(method="GET")
        expected = SimpleNamespace(request=first)
        page.emit("request", old)
        async with expect_new_search_response(page, API, timeout_ms=100) as info:
            page.emit("response", SimpleNamespace(request=old))
            page.emit("request", unrelated)
            page.emit("response", SimpleNamespace(request=unrelated))
            page.emit("request", first)
            page.emit("request", second)
            page.emit("response", SimpleNamespace(request=second))
            # Same URL/method is not the same request object.
            page.emit("response", SimpleNamespace(request=request()))
            page.emit("response", expected)
            page.emit("response", SimpleNamespace(request=old))
        self.assertIs(await info.value, expected)
        self.assertEqual(page.timeouts, [100])
        self.assertEqual(page.listeners, {"request": [], "response": []})

    async def test_no_new_request_times_out_without_accepting_old_search_response(self):
        page = EventPage()
        old = request()
        with self.assertRaises(asyncio.TimeoutError):
            async with expect_new_search_response(page, API, timeout_ms=20):
                page.emit("response", SimpleNamespace(request=old))
        self.assertEqual(page.listeners, {"request": [], "response": []})

    async def test_new_request_without_response_times_out(self):
        page = EventPage()
        with self.assertRaises(asyncio.TimeoutError):
            async with expect_new_search_response(page, API, timeout_ms=20):
                page.emit("request", request())
        self.assertEqual(page.listeners, {"request": [], "response": []})

    async def test_action_failure_removes_only_own_listener(self):
        page = EventPage()
        existing = Mock()
        page.on("request", existing)
        with self.assertRaisesRegex(RuntimeError, "synthetic click error"):
            async with expect_new_search_response(page, API):
                raise RuntimeError("synthetic click error")
        self.assertEqual(page.listeners, {"request": [existing], "response": []})

    async def test_response_listener_setup_failure_removes_request_listener(self):
        page = EventPage()
        page.expect_response = Mock(side_effect=RuntimeError("page closed"))
        with self.assertRaisesRegex(RuntimeError, "page closed"):
            async with expect_new_search_response(page, API):
                self.fail("action must not run")
        self.assertEqual(page.listeners, {"request": [], "response": []})

    async def test_cancel_during_action_releases_listeners(self):
        page = EventPage()
        entered = asyncio.Event()
        async def run():
            async with expect_new_search_response(page, API):
                entered.set()
                await asyncio.Event().wait()
        task = asyncio.create_task(run())
        await entered.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertEqual(page.listeners, {"request": [], "response": []})

    async def test_cancel_while_waiting_for_response_releases_listeners(self):
        page = EventPage()
        entered = asyncio.Event()
        async def run():
            async with expect_new_search_response(page, API):
                page.emit("request", request())
                entered.set()
        task = asyncio.create_task(run())
        await entered.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertEqual(page.listeners, {"request": [], "response": []})

    async def test_next_action_does_not_reuse_previous_action_request(self):
        page = EventPage()
        previous = request()
        async with expect_new_search_response(page, API) as first:
            page.emit("request", previous)
            page.emit("response", SimpleNamespace(request=previous))
        await first.value
        current = request()
        expected = SimpleNamespace(request=current)
        async with expect_new_search_response(page, API) as second:
            page.emit("response", SimpleNamespace(request=previous))
            page.emit("request", current)
            page.emit("response", expected)
        self.assertIs(await second.value, expected)

    async def test_response_timeout_still_starts_before_action_finishes(self):
        page = EventPage()
        with self.assertRaises(asyncio.TimeoutError):
            async with expect_new_search_response(page, API, timeout_ms=10):
                # 模拟已有导航/睡眠尚未结束，不能退出后再重新给一份响应预算。
                await asyncio.sleep(30 / 1000)
                req = request()
                page.emit("request", req)
                page.emit("response", SimpleNamespace(request=req))
        self.assertEqual(page.listeners, {"request": [], "response": []})

    async def test_successful_response_does_not_cancel_remaining_action_sleep(self):
        page = EventPage()
        action_finished = False
        async with expect_new_search_response(page, API, timeout_ms=10) as info:
            req = request()
            expected = SimpleNamespace(request=req)
            page.emit("request", req)
            page.emit("response", expected)
            await asyncio.sleep(30 / 1000)
            action_finished = True
        self.assertTrue(action_finished)
        self.assertIs(await info.value, expected)

    async def test_invalid_timeout_does_not_register_or_run_action(self):
        page = EventPage()
        with self.assertRaises(ValueError):
            async with expect_new_search_response(page, API, timeout_ms=0):
                self.fail("action must not run")
        self.assertEqual(page.listeners, {"request": [], "response": []})


class SearchMatchingTests(unittest.TestCase):
    def test_matches_version_and_post(self):
        for url in (URL, "https://" + API, "https://" + API + "/1.0/", "https://" + API + "/2.0/"):
            self.assertTrue(is_search_request(request(url), API))

    def test_rejects_wrong_method_host_path_port_and_query_spoofing(self):
        for req in (
            request(method="GET"), request(method="OPTIONS"), request("https://other.test/" + API),
            request("https://h5api.m.goofish.com/analytics?url=" + API),
            request("https://" + API + "Other/1.0/"), request("https://" + API + "/other/"),
            request(URL.replace(".com/", ".com:8443/")), request("https://user:pass@" + API),
            request("not a url"), SimpleNamespace(url=URL), None,
        ):
            with self.subTest(req=req):
                self.assertFalse(is_search_request(req, API))

    def test_response_uses_request_identity(self):
        self.assertTrue(is_search_response(SimpleNamespace(request=request()), API))
        self.assertFalse(is_search_response(SimpleNamespace(url=URL), API))
        self.assertFalse(is_search_response(SimpleNamespace(url=URL, request=request(method="GET")), API))


class SearchBindingTests(unittest.IsolatedAsyncioTestCase):
    async def test_binds_first_matching_new_request_not_old_response(self):
        page = FakePage()
        expected = object()
        old = request(response=object())
        submitted = request(response=expected)

        async def action():
            # 旧 response 的到达不产生新的 request，不能抢占。
            await old.response()
            page.emit(request(method="OPTIONS"))
            page.emit(request("https://other.test/?url=" + API))
            page.emit(submitted)
            page.emit(request(response=object()))

        self.assertIs(await capture_new_request_response(page, API, action), expected)
        submitted.response.assert_awaited_once()
        self.assertIsNone(page.predicate)

    async def test_timeout_includes_wait_for_response(self):
        page = FakePage()
        req = request()
        cancelled = asyncio.Event()

        async def hanging_response():
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.set()

        req.response.side_effect = hanging_response

        async def action():
            page.emit(req)

        with self.assertRaises(asyncio.TimeoutError):
            await capture_new_request_response(page, API, action, timeout_ms=30)
        self.assertTrue(cancelled.is_set())
        self.assertIsNone(page.predicate)

    async def test_no_request_has_bounded_wait(self):
        with self.assertRaises(asyncio.TimeoutError):
            await capture_new_request_response(FakePage(), API, AsyncMock(), timeout_ms=30)

    async def test_timeout_includes_action(self):
        page = FakePage()

        async def action():
            await asyncio.Event().wait()

        with self.assertRaises(asyncio.TimeoutError):
            await capture_new_request_response(page, API, action, timeout_ms=30)
        self.assertIsNone(page.predicate)

    async def test_request_response_failure_is_not_retried(self):
        page = FakePage()
        req = request()
        req.response.side_effect = RuntimeError("模拟响应失败")

        async def action():
            page.emit(req)

        with self.assertRaisesRegex(RuntimeError, "模拟响应失败"):
            await capture_new_request_response(page, API, action)
        self.assertEqual(req.response.await_count, 1)

    async def test_cancellation_releases_listener_without_retry(self):
        page = FakePage()
        entered = asyncio.Event()

        async def action():
            entered.set()
            await asyncio.Event().wait()

        task = asyncio.create_task(capture_new_request_response(page, API, action))
        await entered.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertIsNone(page.predicate)

    async def test_invalid_timeout_never_calls_action(self):
        action = AsyncMock()
        with self.assertRaises(ValueError):
            await capture_new_request_response(FakePage(), API, action, timeout_ms=0)
        action.assert_not_awaited()

    async def test_pagination_binds_new_request_and_preserves_sleep(self):
        page = FakePage()
        response = object()

        async def click(**_kwargs):
            page.emit(request(response=response))

        page.button.click.side_effect = click
        wait = AsyncMock()
        self.assertIs(await advance_search_page(page, API, wait), response)
        self.assertEqual(page.selector, NEXT_PAGE_SELECTOR)
        page.button.click.assert_awaited_once_with(timeout=20000)
        page.button.scroll_into_view_if_needed.assert_awaited_once()
        wait.assert_awaited_once_with(5, 8)

    async def test_last_page_hidden_or_disabled_does_not_click(self):
        for name, value in (("count", 0), ("is_visible", False), ("is_enabled", False)):
            with self.subTest(name=name):
                page = FakePage()
                getattr(page.button, name).return_value = value
                wait = AsyncMock()
                self.assertIsNone(await advance_search_page(page, API, wait))
                page.button.click.assert_not_awaited()
                wait.assert_not_awaited()

    async def test_click_timeout_does_not_click_twice(self):
        page = FakePage()
        page.button.click.side_effect = asyncio.TimeoutError
        wait = AsyncMock()
        with self.assertRaises(asyncio.TimeoutError):
            await advance_search_page(page, API, wait)
        page.button.click.assert_awaited_once()
        wait.assert_not_awaited()

    async def test_scroll_failure_does_not_click(self):
        page = FakePage()
        page.button.scroll_into_view_if_needed.side_effect = asyncio.TimeoutError
        with self.assertRaises(asyncio.TimeoutError):
            await advance_search_page(page, API, AsyncMock())
        page.button.click.assert_not_awaited()


if __name__ == "__main__":
    unittest.main()
