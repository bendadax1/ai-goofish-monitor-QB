"""搜索请求识别与有界绑定，不导入业务配置或启动浏览器。"""

import asyncio
from contextlib import asynccontextmanager
import re
from urllib.parse import urlsplit


NEXT_PAGE_SELECTOR = (
    "button[class*='search-pagination-arrow-container']"
    ":has([class*='search-pagination-arrow-right'])"
    ":not([disabled]):not([aria-disabled='true']):not([class*='disabled'])"
    ":not(:has([class*='disabled']))"
)


def build_extra_headers(raw_headers):
    """保留既有浏览器自管头过滤规则；独立导入便于无配置回归。"""
    if not raw_headers:
        return {}
    excluded = {
        "cookie", "content-length", "host", "sec-fetch-site", "sec-fetch-mode",
        "sec-fetch-dest", "sec-fetch-user",
    }
    return {
        key: value for key, value in raw_headers.items()
        if key and key.lower() not in excluded and value is not None
    }


def is_search_request(request, url_pattern: str) -> bool:
    """匹配 POST 的 API host/path，不接受查询字符串或相似名称冒充端点。"""
    if getattr(request, "method", None) != "POST":
        return False
    try:
        actual = urlsplit(request.url)
        expected = urlsplit(url_pattern if "://" in url_pattern else "//" + url_pattern)
        if not expected.hostname or actual.hostname != expected.hostname:
            return False
        if actual.scheme not in ("http", "https") or actual.username is not None or actual.password is not None:
            return False
        if expected.scheme and actual.scheme != expected.scheme:
            return False
        actual_port = actual.port or (443 if actual.scheme == "https" else 80)
        expected_port = expected.port or (443 if (expected.scheme or actual.scheme) == "https" else 80)
        if actual_port != expected_port:
            return False
        expected_path = expected.path.rstrip("/")
        actual_path = actual.path.rstrip("/")
        if not expected_path:
            return False
        return actual_path == expected_path or (
            actual_path.startswith(expected_path + "/")
            and re.fullmatch(r"\d+(?:\.\d+)*", actual_path[len(expected_path) + 1:]) is not None
        )
    except (AttributeError, TypeError, ValueError):
        return False


def is_search_response(response, url_pattern: str) -> bool:
    return is_search_request(getattr(response, "request", None), url_pattern)


@asynccontextmanager
async def expect_new_search_response(page, url_pattern: str, timeout_ms: int = 20000):
    """保持原 expect_response 的时限，排除动作前已在途的搜索响应。

    只认监听开启后第一个匹配的新 Request 的对象身份，后续并行请求即使
    更快返回也不抢占。此处不推断 POST 页码/筛选字段，不重试或修改请求。
    导航/点击及睡眠仍由调用体负责；退出时仅移除本助手自己的监听。
    """
    if timeout_ms <= 0:
        raise ValueError("搜索响应等待上限必须大于零")
    submitted_request = None

    def on_request(request):
        nonlocal submitted_request
        if submitted_request is None and is_search_request(request, url_pattern):
            submitted_request = request

    def matches_response(response):
        return submitted_request is not None and getattr(response, "request", None) is submitted_request

    page.on("request", on_request)
    try:
        async with page.expect_response(matches_response, timeout=timeout_ms) as response_info:
            yield response_info
    finally:
        page.remove_listener("request", on_request)


async def capture_new_request_response(page, url_pattern: str, action, timeout_ms: int = 12000):
    """只取动作之后新请求的响应，整个动作/请求/响应等待共享同一上限。"""
    if timeout_ms <= 0:
        raise ValueError("请求等待上限必须大于零")

    async def capture():
        async with page.expect_request(
            lambda request: is_search_request(request, url_pattern), timeout=timeout_ms,
        ) as request_info:
            await action()
        submitted_request = await request_info.value
        return await submitted_request.response()

    return await asyncio.wait_for(capture(), timeout=timeout_ms / 1000)


async def advance_search_page(page, url_pattern: str, wait_after_click, timeout_ms: int = 20000):
    """返回新请求的响应；无可用按钮返回 None，超时/取消交给调用方处理。

    每次至多一次点击，不重试；保留业务原有 5–8 秒点击后等待。
    """
    if timeout_ms <= 0:
        raise ValueError("翻页等待上限必须大于零")

    async def advance():
        button = page.locator(NEXT_PAGE_SELECTOR).first
        if not await button.count():
            return None
        if not await button.is_visible() or not await button.is_enabled():
            return None
        await button.scroll_into_view_if_needed(timeout=timeout_ms)

        async def click_and_wait():
            await button.click(timeout=timeout_ms)
            await wait_after_click(5, 8)

        return await capture_new_request_response(page, url_pattern, click_and_wait, timeout_ms)

    return await asyncio.wait_for(advance(), timeout=timeout_ms / 1000)
