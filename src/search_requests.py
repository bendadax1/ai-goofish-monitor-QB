"""搜索请求识别与有界绑定，不导入业务配置或启动浏览器。"""

import asyncio
from contextlib import asynccontextmanager
import json
import re
from urllib.parse import parse_qs, urlsplit


NEXT_PAGE_SELECTOR = (
    "button[class*='search-pagination-arrow-container']"
    ":has([class*='search-pagination-arrow-right'])"
    ":not([disabled]):not([aria-disabled='true']):not([class*='disabled'])"
    ":not(:has([class*='disabled']))"
)

_FILTER_FIELDS = (
    "sortField", "sortValue", "propValueStr", "extraFilterValue",
    "customDistance", "customGps", "priceStart", "priceEnd",
    "minPrice", "maxPrice",
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


def search_request_data(request):
    """解析实测表单里的 data JSON；失败时返回 None，不记录原始载荷。"""
    try:
        body = getattr(request, "post_data", None)
        if not isinstance(body, str) or not body or len(body) > 1_000_000:
            return None
        form = parse_qs(body, keep_blank_values=True, max_num_fields=100)
        values = form.get("data")
        if not values or len(values) != 1:
            return None
        data = json.loads(values[0])
        return data if isinstance(data, dict) else None
    except (AttributeError, TypeError, ValueError, json.JSONDecodeError):
        return None


def _filter_semantics(data):
    return {field: data.get(field) for field in _FILTER_FIELDS if field in data}


def _matches_search_intent(request, expected_keyword=None, expected_page_number=None,
                           filters_changed_from=None, filters_same_as=None):
    if all(value is None for value in (
        expected_keyword, expected_page_number, filters_changed_from, filters_same_as,
    )):
        return True
    data = search_request_data(request)
    if data is None:
        return False
    if expected_keyword is not None and data.get("keyword") != expected_keyword:
        return False
    if expected_page_number is not None and str(data.get("pageNumber")) != str(expected_page_number):
        return False
    if filters_changed_from is not None:
        previous = search_request_data(filters_changed_from)
        if previous is None or _filter_semantics(data) == _filter_semantics(previous):
            return False
    if filters_same_as is not None:
        previous = search_request_data(filters_same_as)
        if previous is None or _filter_semantics(data) != _filter_semantics(previous):
            return False
    return True


@asynccontextmanager
async def expect_new_search_response(page, url_pattern: str, timeout_ms: int = 20000,
                                     expected_keyword=None, expected_page_number=None,
                                     filters_changed_from=None):
    """保持原 expect_response 的时限，排除动作前已在途的搜索响应。

    只认监听开启后第一个符合关键词、页码、筛选变化的新 Request 的对象身份，
    后续并行请求即使更快返回也不抢占。不重试或修改请求。
    导航/点击及睡眠仍由调用体负责；退出时仅移除本助手自己的监听。
    """
    if timeout_ms <= 0:
        raise ValueError("搜索响应等待上限必须大于零")
    submitted_request = None

    def on_request(request):
        nonlocal submitted_request
        if submitted_request is None and is_search_request(request, url_pattern) and _matches_search_intent(
            request, expected_keyword, expected_page_number, filters_changed_from,
        ):
            submitted_request = request

    def matches_response(response):
        return submitted_request is not None and getattr(response, "request", None) is submitted_request

    page.on("request", on_request)
    try:
        async with page.expect_response(matches_response, timeout=timeout_ms) as response_info:
            yield response_info
    finally:
        page.remove_listener("request", on_request)


async def capture_new_request_response(page, url_pattern: str, action, timeout_ms: int = 12000,
                                       expected_keyword=None, expected_page_number=None,
                                       filters_changed_from=None, filters_same_as=None):
    """只取动作之后新请求的响应，整个动作/请求/响应等待共享同一上限。"""
    if timeout_ms <= 0:
        raise ValueError("请求等待上限必须大于零")

    async def capture():
        async with page.expect_request(
            lambda request: is_search_request(request, url_pattern) and _matches_search_intent(
                request, expected_keyword, expected_page_number, filters_changed_from, filters_same_as,
            ), timeout=timeout_ms,
        ) as request_info:
            await action()
        submitted_request = await request_info.value
        return await submitted_request.response()

    return await asyncio.wait_for(capture(), timeout=timeout_ms / 1000)


async def advance_search_page(page, url_pattern: str, wait_after_click, timeout_ms: int = 20000,
                              expected_keyword=None, expected_page_number=None, previous_request=None):
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

        return await capture_new_request_response(
            page, url_pattern, click_and_wait, timeout_ms,
            expected_keyword=expected_keyword, expected_page_number=expected_page_number,
            filters_same_as=previous_request,
        )

    return await asyncio.wait_for(advance(), timeout=timeout_ms / 1000)
