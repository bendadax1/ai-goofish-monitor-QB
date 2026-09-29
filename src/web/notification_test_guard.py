"""Bounded test-notification idempotency, with a shared durable claim when configured."""

import asyncio
from concurrent.futures import Future
import hashlib
import json
import math
import os
from pathlib import Path
import threading
import time
from typing import Awaitable, Callable

from filelock import FileLock, Timeout

from src.logging_config import get_logger


logger = get_logger(__name__, service="web")
_LOCK = threading.Lock()
_RESULTS: dict[tuple[str, str], tuple[str, float, Future]] = {}
_TTL_SECONDS = 300
_MAX_RESULTS = 512
_MAX_LEDGER_BYTES = 512 * 1024


class NotificationTestConflict(Exception):
    """A request ID was already used for a different test target."""


class NotificationTestBusy(Exception):
    """The bounded idempotency cache or shared ledger is busy."""


class NotificationTestFailed(Exception):
    """The attempted send failed; its result is unknown to a retried caller."""


def _state_root() -> Path:
    """Resolve the durable data root only when the business API handles a test."""
    from src.portable.app_paths import get_portable_runtime_paths
    from src.runtime_paths import RuntimePaths

    return (get_portable_runtime_paths() or RuntimePaths.create()).data_path("state", "notification-tests")


def _ledger_path(root: Path) -> Path:
    from src.portable.backup_archive import _assert_no_reparse_ancestry

    if not root.is_absolute():
        raise NotificationTestFailed()
    try:
        _assert_no_reparse_ancestry(root.parent.parent, Path(root.anchor))
        root.parent.mkdir(exist_ok=True)
        _assert_no_reparse_ancestry(root.parent, Path(root.anchor))
        root.mkdir(exist_ok=True)
        _assert_no_reparse_ancestry(root, Path(root.anchor))
        if not root.is_dir():
            raise NotificationTestFailed()
        path = root / "requests-v1.json"
        if path.exists() or path.is_symlink():
            _assert_no_reparse_ancestry(path, root)
            if not path.is_file():
                raise NotificationTestFailed()
        lock_path = root / "requests-v1.lock"
        if lock_path.exists() or lock_path.is_symlink():
            _assert_no_reparse_ancestry(lock_path, root)
            if not lock_path.is_file():
                raise NotificationTestFailed()
        return path
    except Exception:
        logger.error("测试通知去重目录不可用", extra={"event": "notification_test_ledger_path_failed"})
        raise NotificationTestFailed() from None


def _read_ledger(path: Path) -> dict:
    try:
        if not path.exists():
            return {}
        if path.stat().st_size > _MAX_LEDGER_BYTES:
            raise ValueError("ledger oversized")
        payload = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(payload, dict) or payload.get("format_version") != 1 or set(payload) != {"format_version", "entries"}:
            raise ValueError("ledger format")
        entries = payload["entries"]
        if not isinstance(entries, dict) or len(entries) > _MAX_RESULTS:
            raise ValueError("ledger entries")
        for key, entry in entries.items():
            if (not isinstance(key, str) or len(key) != 64 or set(key) - set("0123456789abcdef")
                or not isinstance(entry, dict) or set(entry) != {"fingerprint", "state", "result", "created"}
                or not isinstance(entry["fingerprint"], str) or len(entry["fingerprint"]) != 64
                or set(entry["fingerprint"]) - set("0123456789abcdef")
                or entry["state"] not in ("pending", "complete", "failed")
                or type(entry["created"]) not in (int, float) or not math.isfinite(entry["created"])
                or (entry["state"] == "complete" and type(entry["result"]) is not bool)
                or (entry["state"] != "complete" and entry["result"] is not None)):
                raise ValueError("ledger record")
        return entries
    except (OSError, UnicodeError, ValueError, TypeError):
        logger.error("测试通知去重记录损坏", extra={"event": "notification_test_ledger_invalid"})
        raise NotificationTestFailed() from None


def _write_ledger(path: Path, entries: dict) -> None:
    import tempfile

    pending: Path | None = None
    try:
        content = (json.dumps({"format_version": 1, "entries": entries},
                              ensure_ascii=False, separators=(",", ":"), allow_nan=False) + "\n").encode("utf-8")
        if len(content) > _MAX_LEDGER_BYTES:
            raise NotificationTestBusy()
        descriptor, name = tempfile.mkstemp(prefix=".requests-", suffix=".tmp", dir=path.parent)
        pending = Path(name)
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(pending, path)
        pending = None
    except NotificationTestBusy:
        raise
    except Exception:
        logger.error("测试通知去重记录写入失败", extra={"event": "notification_test_ledger_write_failed"})
        raise NotificationTestFailed() from None
    finally:
        if pending is not None:
            try:
                pending.unlink(missing_ok=True)
            except OSError:
                logger.warning("测试通知去重暂存文件清理失败", extra={"event": "notification_test_ledger_temp_cleanup_failed"})


def _ledger_transition(root: Path, key: tuple[str, str], fingerprint: str,
                       *, complete: bool = False, result: bool | None = None) -> tuple[str, bool | None]:
    path = _ledger_path(root)
    ledger_key = hashlib.sha256(json.dumps(key, separators=(",", ":")).encode("utf-8")).hexdigest()
    try:
        with FileLock(str(root / "requests-v1.lock"), timeout=10):
            entries = _read_ledger(path)
            now = time.time()
            if not complete:
                entries = {name: value for name, value in entries.items()
                           if value["state"] == "pending" or now - value["created"] < _TTL_SECONDS}
            entry = entries.get(ledger_key)
            if entry is not None and entry["fingerprint"] != fingerprint:
                raise NotificationTestConflict()
            if complete:
                if entry is None or entry["state"] != "pending":
                    raise NotificationTestFailed()
                entry["state"] = "failed" if result is None else "complete"
                entry["result"] = result
                entry["created"] = now
                _write_ledger(path, entries)
                return entry["state"], result
            if entry is not None:
                return entry["state"], entry["result"]
            if len(entries) >= _MAX_RESULTS:
                raise NotificationTestBusy()
            entries[ledger_key] = {"fingerprint": fingerprint, "state": "pending",
                                   "result": None, "created": now}
            _write_ledger(path, entries)
            return "claimed", None
    except (NotificationTestConflict, NotificationTestBusy, NotificationTestFailed):
        raise
    except Timeout:
        raise NotificationTestBusy() from None
    except Exception:
        logger.error("测试通知去重协调失败", extra={"event": "notification_test_ledger_failed"})
        raise NotificationTestFailed() from None


def _prune(now: float) -> None:
    for key, (_, created, result) in list(_RESULTS.items()):
        if result.done() and now - created >= _TTL_SECONDS:
            _RESULTS.pop(key, None)
    if len(_RESULTS) >= _MAX_RESULTS:
        raise NotificationTestBusy()


def _finish(key: tuple[str, str], task: asyncio.Task, result: Future,
            durable_root: Path | None = None, fingerprint: str | None = None) -> None:
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
    if durable_root is not None and fingerprint is not None:
        try:
            _ledger_transition(durable_root, key, fingerprint, complete=True,
                               result=None if failed else value)
        except (NotificationTestBusy, NotificationTestConflict, NotificationTestFailed):
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
    durable_root: Path | None = None,
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
            if durable_root is not None:
                state, prior_result = _ledger_transition(durable_root, key, fingerprint)
                if state == "complete":
                    return bool(prior_result)
                if state in ("pending", "failed"):
                    raise NotificationTestFailed()
            result = Future()
            _RESULTS[key] = (fingerprint, now, result)
            task = asyncio.create_task(sender())
            task.add_done_callback(lambda finished: _finish(
                key, finished, result, durable_root, fingerprint))
    return bool(await asyncio.shield(asyncio.wrap_future(result)))
