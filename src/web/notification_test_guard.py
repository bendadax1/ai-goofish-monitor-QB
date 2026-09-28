"""Bounded, process-local idempotency for user-triggered test notifications."""

import asyncio
from concurrent.futures import Future
import hashlib
import json
import threading
import time
from typing import Awaitable, Callable

from src.logging_config import get_logger


logger = get_logger(__name__, service="web")
_LOCK = threading.Lock()
_RESULTS: dict[tuple[str, str], tuple[str, float, Future]] = {}
_TTL_SECONDS = 300
_MAX_RESULTS = 512


class NotificationTestConflict(Exception):
    """A request ID was already used for a different test target."""


class NotificationTestBusy(Exception):
    """The process-local idempotency cache is full of active sends."""


class NotificationTestFailed(Exception):
    """The attempted send failed; its result is unknown to a retried caller."""


def _prune(now: float) -> None:
    for key, (_, created, result) in list(_RESULTS.items()):
        if result.done() and now - created >= _TTL_SECONDS:
            _RESULTS.pop(key, None)
    if len(_RESULTS) >= _MAX_RESULTS:
        raise NotificationTestBusy()


def _finish(key: tuple[str, str], task: asyncio.Task, result: Future) -> None:
    failed = False
    try:
        value = bool(task.result())
    except asyncio.CancelledError:
        logger.warning("测试通知任务被取消", extra={"event": "notification_test_cancelled"})
        failed = True
    except Exception as exc:
        logger.error(
            "测试通知任务异常",
            extra={"event": "notification_test_send_failed", "error_type": type(exc).__name__},
        )
        failed = True
    with _LOCK:
        entry = _RESULTS.get(key)
        if entry is not None and entry[2] is result:
            _RESULTS[key] = (entry[0], time.monotonic(), result)
        if failed:
            result.set_exception(NotificationTestFailed())
        else:
            result.set_result(value)


async def send_test_once(
    *,
    owner_id: str | None,
    request_id: str | None,
    test_type: str,
    channel: str,
    config_id: str | None,
    bound_task: str | None,
    sender: Callable[[], Awaitable[bool]],
) -> bool:
    """Reuse a test result for repeated IDs while preserving distinct user clicks."""
    if not request_id:
        return bool(await sender())

    fingerprint = hashlib.sha256(json.dumps(
        [test_type, channel, config_id or "", bound_task or ""],
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")).hexdigest()
    key = (f"user:{owner_id}" if owner_id is not None else "local", request_id)
    now = time.monotonic()
    with _LOCK:
        entry = _RESULTS.get(key)
        if entry is not None and entry[2].done() and now - entry[1] >= _TTL_SECONDS:
            _RESULTS.pop(key, None)
            entry = None
        if entry is not None:
            if entry[0] != fingerprint:
                raise NotificationTestConflict()
            result = entry[2]
        else:
            _prune(now)
            result = Future()
            _RESULTS[key] = (fingerprint, now, result)
            task = asyncio.create_task(sender())
            task.add_done_callback(lambda finished: _finish(key, finished, result))
    return bool(await asyncio.shield(asyncio.wrap_future(result)))
