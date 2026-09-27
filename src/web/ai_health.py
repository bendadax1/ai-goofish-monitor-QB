import asyncio
import copy
import hmac
import ipaddress
import json
import secrets
import socket
import time
from collections import deque
from datetime import datetime, timedelta, timezone
from threading import Lock
from typing import Any, Dict, Optional
from urllib.parse import urlsplit

import httpx
import httpcore
from openai import AsyncOpenAI, OpenAI

import src.config
from src.config import STORAGE_BACKEND
from src.logging_config import get_logger
from src.storage import get_storage


logger = get_logger(__name__, service="web")

# 16x16 PNG（透明像素），用于多模态能力探测时避免依赖外部图片服务。
_VISION_TEST_IMAGE_DATA_URL = (
    "data:image/png;base64,"
    "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAEklEQVR42mNgGAWjYBSMAggAAAQQAAGvRYgsAAAAAElFTkSuQmCC"
)

_AI_HEALTH_CACHE: Dict[str, Dict[str, Any]] = {}
_AI_HEALTH_CACHE_LOCK = Lock()
_AI_HEALTH_CACHE_TTL_SECONDS = 5 * 60
_AI_MANUAL_TEST_RESULTS: Dict[str, Dict[str, Any]] = {}
_AI_MANUAL_TEST_INFLIGHT: Dict[str, asyncio.Task] = {}
_AI_MANUAL_TEST_FINGERPRINTS: Dict[str, str] = {}
_AI_MANUAL_TEST_CALLS: Dict[str, deque] = {}
_AI_MANUAL_TEST_LOCK = Lock()
_AI_MANUAL_TEST_RESULT_LIMIT = 256
_AI_MANUAL_TEST_TIMEOUT_SECONDS = 8.0
_AI_MANUAL_TEST_DNS_TIMEOUT_SECONDS = 2.0
_AI_MANUAL_TEST_MAX_RESPONSE_BYTES = 64 * 1024
_AI_MANUAL_TEST_PROMPT = "Reply with OK."
_CONFIG_SNAPSHOT_UNSET = object()
_AI_MANUAL_TEST_FINGERPRINT_KEY = secrets.token_bytes(32)


def _now_text() -> str:
    """返回本地时间文本，便于前端直接展示。"""
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def _parse_bool(value: Any, default: bool = False) -> bool:
    """将任意输入稳健转换为布尔值。"""
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return bool(value)
    if isinstance(value, str):
        normalized = value.strip().lower()
        if normalized in {"1", "true", "yes", "on"}:
            return True
        if normalized in {"0", "false", "no", "off"}:
            return False
    return default


def _resolve_int(value: Any, default: int) -> int:
    """把值转换为正整数，失败时回退默认值。"""
    try:
        parsed = int(value)
        return parsed if parsed > 0 else default
    except (TypeError, ValueError):
        return default


def _resolve_user_id(user: Optional[dict]) -> str:
    """提取当前用户ID。"""
    return str((user or {}).get("user_id") or (user or {}).get("id") or "").strip()


def _cache_key(user: Optional[dict]) -> str:
    """根据后端模式与用户生成缓存键。"""
    backend = (STORAGE_BACKEND() or "local").lower()
    if backend == "postgres":
        return f"postgres:{_resolve_user_id(user) or 'anonymous'}"
    return "local:global"


def _build_probe_result(
    *,
    success: Optional[bool],
    level: str,
    message: str,
    latency_ms: Optional[int] = None,
    checked_at: str = "",
) -> Dict[str, Any]:
    return {
        "success": success,
        "level": level,
        "message": str(message or ""),
        "latency_ms": latency_ms,
        "checked_at": checked_at,
    }


def _build_vision_result(
    *,
    status: str,
    level: str,
    message: str,
    latency_ms: Optional[int] = None,
    checked_at: str = "",
) -> Dict[str, Any]:
    return {
        "status": status,
        "level": level,
        "message": str(message or ""),
        "latency_ms": latency_ms,
        "checked_at": checked_at,
    }


def _resolve_effective_ai_config(
    user: Optional[dict],
    overrides: Optional[dict] = None,
    *,
    persisted_config: Any = _CONFIG_SNAPSHOT_UNSET,
) -> Dict[str, Any]:
    """按运行模式解析当前应生效的 AI 配置。"""
    payload = overrides if isinstance(overrides, dict) else {}
    backend = (STORAGE_BACKEND() or "local").lower()

    if backend == "postgres":
        user_id = _resolve_user_id(user)
        user_api_config: Dict[str, Any] = {}
        if persisted_config is not _CONFIG_SNAPSHOT_UNSET:
            user_api_config = persisted_config if isinstance(persisted_config, dict) else {}
        elif user_id:
            try:
                storage = get_storage()
                revision_reader = getattr(storage, "get_default_api_config_with_revision", None)
                if callable(revision_reader):
                    user_api_config = revision_reader(user_id) or {}
                else:
                    user_api_config = storage.get_default_api_config(user_id) or {}
            except Exception as exc:
                logger.warning(
                    "读取用户AI配置失败",
                    extra={"event": "ai_health_load_user_config_failed", "owner_id": user_id},
                    exc_info=exc,
                )

        extra_config = user_api_config.get("extra_config") if isinstance(user_api_config.get("extra_config"), dict) else {}
        api_key = str(payload.get("OPENAI_API_KEY") or user_api_config.get("api_key") or "").strip()
        base_url = str(payload.get("OPENAI_BASE_URL") or user_api_config.get("api_base_url") or "").strip()
        model_name = str(payload.get("OPENAI_MODEL_NAME") or user_api_config.get("model") or "").strip()

        proxy_url = payload.get("PROXY_URL")
        if proxy_url in (None, ""):
            proxy_url = str(extra_config.get("PROXY_URL") or "").strip()
        else:
            proxy_url = str(proxy_url).strip()

        proxy_ai_enabled = payload.get("PROXY_AI_ENABLED")
        if proxy_ai_enabled is None:
            proxy_ai_enabled = _parse_bool(extra_config.get("PROXY_AI_ENABLED"), default=False)
        else:
            proxy_ai_enabled = _parse_bool(proxy_ai_enabled, default=False)

        tokens_param_name = str(
            payload.get("AI_MAX_TOKENS_PARAM_NAME") or extra_config.get("AI_MAX_TOKENS_PARAM_NAME") or ""
        ).strip()
        tokens_limit_default = _resolve_int(extra_config.get("AI_MAX_TOKENS_LIMIT"), 20000)
        tokens_limit = _resolve_int(payload.get("AI_MAX_TOKENS_LIMIT"), tokens_limit_default)

        return {
            "backend": backend,
            "source": "postgres_user_config",
            "source_label": "当前用户配置（PostgreSQL）",
            "owner_id": user_id,
            "config_id": str(user_api_config.get("id") or ""),
            "config_revision": _resolve_nonnegative_int(user_api_config.get("config_revision"), 0),
            "api_key": api_key,
            "api_key_set": bool(api_key),
            "base_url": base_url,
            "base_url_set": bool(base_url),
            "model_name": model_name,
            "model_name_set": bool(model_name),
            "proxy_url": proxy_url,
            "proxy_ai_enabled": bool(proxy_ai_enabled),
            "tokens_param_name": tokens_param_name,
            "tokens_limit": tokens_limit,
        }

    api_key = str(payload.get("OPENAI_API_KEY") or src.config.API_KEY() or "").strip()
    base_url = str(payload.get("OPENAI_BASE_URL") or src.config.BASE_URL() or "").strip()
    model_name = str(payload.get("OPENAI_MODEL_NAME") or src.config.MODEL_NAME() or "").strip()

    proxy_url = payload.get("PROXY_URL")
    if proxy_url in (None, ""):
        proxy_url = str(src.config.PROXY_URL() or "").strip()
    else:
        proxy_url = str(proxy_url).strip()

    proxy_ai_enabled = payload.get("PROXY_AI_ENABLED")
    if proxy_ai_enabled is None:
        proxy_ai_enabled = src.config.PROXY_AI_ENABLED()
    else:
        proxy_ai_enabled = _parse_bool(proxy_ai_enabled, default=False)

    tokens_param_name = str(payload.get("AI_MAX_TOKENS_PARAM_NAME") or src.config.AI_MAX_TOKENS_PARAM_NAME() or "").strip()
    tokens_limit = _resolve_int(payload.get("AI_MAX_TOKENS_LIMIT"), src.config.AI_MAX_TOKENS_LIMIT())

    return {
        "backend": backend,
        "source": "env_global_config",
        "source_label": "全局配置（.env）",
        "owner_id": "",
        "api_key": api_key,
        "api_key_set": bool(api_key),
        "base_url": base_url,
        "base_url_set": bool(base_url),
        "model_name": model_name,
        "model_name_set": bool(model_name),
        "proxy_url": proxy_url,
        "proxy_ai_enabled": bool(proxy_ai_enabled),
        "tokens_param_name": tokens_param_name,
        "tokens_limit": tokens_limit,
    }


def _resolve_config_state(config: Dict[str, Any]) -> Dict[str, Any]:
    """生成配置完整性状态。"""
    api_key_set = bool(config.get("api_key_set"))
    base_url_set = bool(config.get("base_url_set"))
    model_name_set = bool(config.get("model_name_set"))

    missing_items = []
    if not api_key_set:
        missing_items.append("API Key")
    if not base_url_set:
        missing_items.append("Base URL")
    if not model_name_set:
        missing_items.append("模型名称")

    config_ready = len(missing_items) == 0
    if config_ready:
        message = "AI 配置完整，可执行连通性检测。"
    else:
        message = f"AI 配置不完整，缺少：{', '.join(missing_items)}。"

    return {
        "ready": config_ready,
        "message": message,
        "api_key_set": api_key_set,
        "base_url_set": base_url_set,
        "model_name_set": model_name_set,
    }


def _build_request_kwargs(config: Dict[str, Any], content: Any) -> Dict[str, Any]:
    """统一构造请求参数，保证 tokens 参数行为一致。"""
    request_kwargs = {
        "model": str(config.get("model_name") or "").strip(),
        "messages": [{"role": "user", "content": content}],
    }

    tokens_param_name = str(config.get("tokens_param_name") or "").strip()
    if tokens_param_name:
        tokens_limit = _resolve_int(config.get("tokens_limit"), 20000)
        request_kwargs[tokens_param_name] = max(1, min(tokens_limit, 10))

    return src.config.get_ai_request_params(**request_kwargs)


def _run_web_text_probe_sync(config: Dict[str, Any]) -> Dict[str, Any]:
    """执行 Web 进程连通性探测（同步客户端）。"""
    http_client = None
    started = time.perf_counter()
    try:
        client_params: Dict[str, Any] = {
            "api_key": config.get("api_key"),
            "base_url": config.get("base_url"),
            "timeout": httpx.Timeout(30.0),
        }
        if config.get("proxy_ai_enabled") and config.get("proxy_url"):
            http_client = httpx.Client(proxy=str(config.get("proxy_url")), timeout=30.0)
            client_params["http_client"] = http_client

        client = OpenAI(**client_params)
        client.chat.completions.create(
            **_build_request_kwargs(
                config,
                "Health check from web process. Reply with OK.",
            )
        )
        latency_ms = int((time.perf_counter() - started) * 1000)
        return _build_probe_result(
            success=True,
            level="ok",
            message="Web 进程连通成功。",
            latency_ms=latency_ms,
            checked_at=_now_text(),
        )
    except Exception as exc:
        latency_ms = int((time.perf_counter() - started) * 1000)
        return _build_probe_result(
            success=False,
            level="error",
            message=f"Web 进程连通失败：{exc}",
            latency_ms=latency_ms,
            checked_at=_now_text(),
        )
    finally:
        if http_client is not None:
            http_client.close()


async def _run_backend_text_probe_async(config: Dict[str, Any]) -> Dict[str, Any]:
    """执行后端容器连通性探测（异步客户端）。"""
    http_async_client = None
    started = time.perf_counter()
    try:
        client_params: Dict[str, Any] = {
            "api_key": config.get("api_key"),
            "base_url": config.get("base_url"),
            "timeout": httpx.Timeout(30.0),
        }
        if config.get("proxy_ai_enabled") and config.get("proxy_url"):
            http_async_client = httpx.AsyncClient(proxy=str(config.get("proxy_url")), timeout=30.0)
            client_params["http_client"] = http_async_client

        client = AsyncOpenAI(**client_params)
        await client.chat.completions.create(
            **_build_request_kwargs(
                config,
                "Health check from backend process. Reply with OK.",
            )
        )
        latency_ms = int((time.perf_counter() - started) * 1000)
        return _build_probe_result(
            success=True,
            level="ok",
            message="后端进程连通成功。",
            latency_ms=latency_ms,
            checked_at=_now_text(),
        )
    except Exception as exc:
        latency_ms = int((time.perf_counter() - started) * 1000)
        return _build_probe_result(
            success=False,
            level="error",
            message=f"后端进程连通失败：{exc}",
            latency_ms=latency_ms,
            checked_at=_now_text(),
        )
    finally:
        if http_async_client is not None:
            await http_async_client.aclose()


def _classify_vision_error(message: str) -> str:
    """根据错误文本归类图像能力检测结果。"""
    lowered = (message or "").lower()
    unsupported_markers = [
        "does not support image",
        "not support image",
        "unsupported image",
        "unsupported content type",
        "image_url is not supported",
        "vision is not supported",
        "input_image is not supported",
        "多模态",
        "不支持图片",
        "不支持图像",
    ]
    for marker in unsupported_markers:
        if marker in lowered:
            return "unsupported"
    small_image_markers = [
        "image dimensions are too small",
        "minimum allowed dimension",
    ]
    for marker in small_image_markers:
        if marker in lowered:
            return "supported"
    return "unknown"


async def _run_vision_probe_async(config: Dict[str, Any]) -> Dict[str, Any]:
    """执行图像能力探测，判定模型是否支持 image_url 输入。"""
    http_async_client = None
    started = time.perf_counter()
    try:
        client_params: Dict[str, Any] = {
            "api_key": config.get("api_key"),
            "base_url": config.get("base_url"),
            "timeout": httpx.Timeout(30.0),
        }
        if config.get("proxy_ai_enabled") and config.get("proxy_url"):
            http_async_client = httpx.AsyncClient(proxy=str(config.get("proxy_url")), timeout=30.0)
            client_params["http_client"] = http_async_client

        client = AsyncOpenAI(**client_params)
        vision_content = [
            {"type": "text", "text": "Vision capability check. Reply with OK."},
            {"type": "image_url", "image_url": {"url": _VISION_TEST_IMAGE_DATA_URL}},
        ]
        await client.chat.completions.create(**_build_request_kwargs(config, vision_content))

        latency_ms = int((time.perf_counter() - started) * 1000)
        return _build_vision_result(
            status="supported",
            level="ok",
            message="模型支持图像输入（image_url）。",
            latency_ms=latency_ms,
            checked_at=_now_text(),
        )
    except Exception as exc:
        latency_ms = int((time.perf_counter() - started) * 1000)
        status = _classify_vision_error(str(exc))
        if status == "unsupported":
            return _build_vision_result(
                status="unsupported",
                level="warning",
                message=f"模型不支持图像输入：{exc}",
                latency_ms=latency_ms,
                checked_at=_now_text(),
            )
        if status == "supported":
            return _build_vision_result(
                status="supported",
                level="warning",
                message=f"模型支持图像输入，但本次探测图片参数不合规：{exc}",
                latency_ms=latency_ms,
                checked_at=_now_text(),
            )
        return _build_vision_result(
            status="unknown",
            level="warning",
            message=f"图像能力暂时无法判断：{exc}",
            latency_ms=latency_ms,
            checked_at=_now_text(),
        )
    finally:
        if http_async_client is not None:
            await http_async_client.aclose()


def _compute_overall_level(
    config_ready: bool,
    web_test: Dict[str, Any],
    backend_test: Dict[str, Any],
    run_web: bool,
    run_backend: bool,
) -> Dict[str, str]:
    """综合配置完整性与连通性结果生成总状态。"""
    if not config_ready:
        return {"level": "error", "label": "异常", "message": "AI 配置不完整。"}

    executed = []
    if run_web:
        executed.append(web_test.get("success") is True)
    if run_backend:
        executed.append(backend_test.get("success") is True)

    if not executed:
        return {"level": "unknown", "label": "未知", "message": "尚未执行连通性检测。"}
    if all(executed):
        return {"level": "ok", "label": "正常", "message": "AI API 连通性正常。"}
    if any(executed):
        return {"level": "warning", "label": "警告", "message": "AI API 部分可用，请检查失败链路。"}
    return {"level": "error", "label": "异常", "message": "AI API 连通性不可用。"}


def _default_health_snapshot(config: Dict[str, Any], config_state: Dict[str, Any]) -> Dict[str, Any]:
    """返回默认（未检测）健康快照。"""
    overall = _compute_overall_level(
        config_state.get("ready", False),
        _build_probe_result(success=None, level="unknown", message="尚未检测。"),
        _build_probe_result(success=None, level="unknown", message="尚未检测。"),
        run_web=False,
        run_backend=False,
    )
    return {
        "source": config.get("source"),
        "source_label": config.get("source_label"),
        "owner_id": config.get("owner_id") or "",
        "config": {
            "api_key_set": bool(config_state.get("api_key_set")),
            "base_url_set": bool(config_state.get("base_url_set")),
            "model_name_set": bool(config_state.get("model_name_set")),
            "ready": bool(config_state.get("ready")),
            "message": str(config_state.get("message") or ""),
        },
        "web_test": _build_probe_result(success=None, level="unknown", message="尚未检测。"),
        "backend_test": _build_probe_result(success=None, level="unknown", message="尚未检测。"),
        "vision_capability": _build_vision_result(status="unknown", level="unknown", message="尚未检测。"),
        "overall_level": overall["level"],
        "overall_label": overall["label"],
        "overall_message": overall["message"],
        "checked_at": "",
    }


def _resolve_nonnegative_int(value: Any, default: int) -> int:
    try:
        parsed = int(value)
        return parsed if parsed >= 0 else default
    except (TypeError, ValueError):
        return default


def _utc_text(value: Optional[datetime] = None) -> str:
    return (value or datetime.now(timezone.utc)).astimezone(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def _cache_identity(config: Dict[str, Any]) -> tuple[str, int]:
    return (
        str(config.get("config_id") or ""),
        _resolve_nonnegative_int(config.get("config_revision"), 0),
    )


def _current_postgres_ai_config(user: Optional[dict]) -> Optional[Dict[str, Any]]:
    """Read the current user's persisted API config and revision without network access."""
    user_id = _resolve_user_id(user)
    if not user_id:
        return None
    storage = get_storage()
    reader = getattr(storage, "get_default_api_config_with_revision", None)
    if not callable(reader):
        raise RuntimeError("API config revision reader is unavailable")
    return reader(user_id) or None


def _set_cached_snapshot(
    user: Optional[dict],
    snapshot: Dict[str, Any],
    *,
    config_identity: Optional[tuple[str, int]] = None,
) -> None:
    """Write a TTL-bound cache entry only when its config identity was verified."""
    if (STORAGE_BACKEND() or "local").lower() != "postgres":
        with _AI_HEALTH_CACHE_LOCK:
            _AI_HEALTH_CACHE[_cache_key(user)] = copy.deepcopy(snapshot)
        return
    if not config_identity or not config_identity[0]:
        return
    observed_at = datetime.now(timezone.utc)
    entry = {
        "_launcher_cache": True,
        "config_id": config_identity[0],
        "config_revision": config_identity[1],
        "observed_at": _utc_text(observed_at),
        "expires_at": _utc_text(observed_at + timedelta(seconds=_AI_HEALTH_CACHE_TTL_SECONDS)),
        "observed_monotonic": time.monotonic(),
        "snapshot": copy.deepcopy(snapshot),
    }
    with _AI_HEALTH_CACHE_LOCK:
        _AI_HEALTH_CACHE[_cache_key(user)] = entry


def invalidate_ai_health_snapshot(user: Optional[dict]) -> None:
    """失效当前用户的 AI 健康缓存。"""
    with _AI_HEALTH_CACHE_LOCK:
        _AI_HEALTH_CACHE.pop(_cache_key(user), None)


def get_ai_health_snapshot(user: Optional[dict]) -> Dict[str, Any]:
    """读取最新 AI 健康状态（仅读缓存，不触发网络探测）。"""
    config = _resolve_effective_ai_config(user)
    config_state = _resolve_config_state(config)

    with _AI_HEALTH_CACHE_LOCK:
        cached = copy.deepcopy(_AI_HEALTH_CACHE.get(_cache_key(user)))

    if not cached:
        return _default_health_snapshot(config, config_state)

    if cached.get("_launcher_cache"):
        current_identity = _cache_identity(config)
        if (
            cached.get("config_id") != current_identity[0]
            or cached.get("config_revision") != current_identity[1]
            or time.monotonic() - float(cached.get("observed_monotonic") or 0) >= _AI_HEALTH_CACHE_TTL_SECONDS
        ):
            return _default_health_snapshot(config, config_state)
        cached = copy.deepcopy(cached.get("snapshot") or {})
    elif config.get("backend") == "postgres":
        # Older unbound snapshots cannot safely be shown as current after upgrade.
        return _default_health_snapshot(config, config_state)

    # 每次读取都覆盖来源与配置完整性，避免配置变更后缓存误导。
    cached["source"] = config.get("source")
    cached["source_label"] = config.get("source_label")
    cached["owner_id"] = config.get("owner_id") or ""
    cached["config"] = {
        "api_key_set": bool(config_state.get("api_key_set")),
        "base_url_set": bool(config_state.get("base_url_set")),
        "model_name_set": bool(config_state.get("model_name_set")),
        "ready": bool(config_state.get("ready")),
        "message": str(config_state.get("message") or ""),
    }
    if not config_state.get("ready"):
        cached["overall_level"] = "error"
        cached["overall_label"] = "异常"
        cached["overall_message"] = "AI 配置不完整。"
    return cached


async def run_ai_health_check(
    user: Optional[dict],
    *,
    overrides: Optional[dict] = None,
    run_web: bool = True,
    run_backend: bool = True,
    check_vision: bool = True,
) -> Dict[str, Any]:
    """执行 AI 健康检查并更新缓存。"""
    config = _resolve_effective_ai_config(user, overrides=overrides)
    config_state = _resolve_config_state(config)
    snapshot = _default_health_snapshot(config, config_state)

    if not config_state.get("ready"):
        return snapshot

    start_identity = _cache_identity(config)

    web_result = _build_probe_result(success=None, level="unknown", message="本次未执行。")
    backend_result = _build_probe_result(success=None, level="unknown", message="本次未执行。")

    tasks = []
    task_types = []
    if run_web:
        tasks.append(asyncio.to_thread(_run_web_text_probe_sync, config))
        task_types.append("web")
    if run_backend:
        tasks.append(_run_backend_text_probe_async(config))
        task_types.append("backend")

    if tasks:
        probe_results = await asyncio.gather(*tasks)
        for probe_type, probe_result in zip(task_types, probe_results):
            if probe_type == "web":
                web_result = probe_result
            elif probe_type == "backend":
                backend_result = probe_result

    vision_result = _build_vision_result(status="unknown", level="unknown", message="本次未执行。")
    if check_vision:
        if (web_result.get("success") is True) or (backend_result.get("success") is True):
            vision_result = await _run_vision_probe_async(config)
        else:
            vision_result = _build_vision_result(
                status="unknown",
                level="warning",
                message="文本连通性未通过，暂未执行图像能力检测。",
            )

    overall = _compute_overall_level(config_state.get("ready", False), web_result, backend_result, run_web, run_backend)
    checked_at = _now_text()
    snapshot = {
        "source": config.get("source"),
        "source_label": config.get("source_label"),
        "owner_id": config.get("owner_id") or "",
        "config": {
            "api_key_set": bool(config_state.get("api_key_set")),
            "base_url_set": bool(config_state.get("base_url_set")),
            "model_name_set": bool(config_state.get("model_name_set")),
            "ready": bool(config_state.get("ready")),
            "message": str(config_state.get("message") or ""),
        },
        "web_test": web_result,
        "backend_test": backend_result,
        "vision_capability": vision_result,
        "overall_level": overall["level"],
        "overall_label": overall["label"],
        "overall_message": overall["message"],
        "checked_at": checked_at,
    }
    if config.get("backend") != "postgres":
        _set_cached_snapshot(user, snapshot)
    elif start_identity[0] and overrides is None:
        try:
            latest_config = _current_postgres_ai_config(user)
            if latest_config is not None:
                latest_identity = (
                    str(latest_config.get("id") or ""),
                    _resolve_nonnegative_int(latest_config.get("config_revision"), 0),
                )
                if latest_identity == start_identity:
                    _set_cached_snapshot(user, snapshot, config_identity=start_identity)
        except Exception as exc:
            logger.warning(
                "AI 健康快照配置版本复核失败，未缓存",
                extra={"event": "ai_health_cache_identity_check_failed", "error_type": type(exc).__name__},
            )
    return snapshot


def _launcher_health_category(snapshot: Dict[str, Any]) -> str:
    if snapshot.get("overall_level") == "ok":
        return "healthy"
    if snapshot.get("overall_level") == "warning":
        return "partial"

    messages = []
    for name in ("web_test", "backend_test"):
        result = snapshot.get(name)
        if isinstance(result, dict) and result.get("success") is False:
            messages.append(str(result.get("message") or "").lower())
    message = " ".join(messages)
    if any(marker in message for marker in ("timeout", "timed out", "超时")):
        return "timeout"
    if any(marker in message for marker in ("401", "403", "unauthorized", "forbidden", "认证失败")):
        return "auth_failed"
    if any(marker in message for marker in ("429", "rate limit", "too many requests", "限流")):
        return "rate_limited"
    if messages:
        return "unavailable"
    return "unknown"


def get_portable_launcher_ai_health(user: Optional[dict]) -> Dict[str, Any]:
    """Return a redacted current-user health-cache view; never perform a probe."""
    if (STORAGE_BACKEND() or "local").lower() != "postgres":
        raise RuntimeError("portable launcher AI health requires PostgreSQL")
    current = _current_postgres_ai_config(user)
    config_id = str((current or {}).get("id") or "")
    config_revision = _resolve_nonnegative_int((current or {}).get("config_revision"), 0)
    required_configured = bool(
        current
        and str(current.get("api_key") or "").strip()
        and str(current.get("api_base_url") or "").strip()
        and str(current.get("model") or "").strip()
        and not current.get("api_key_invalid")
    )

    response = {
        "status": "never_checked",
        "observed_at": None,
        "expires_at": None,
        "config_id": config_id,
        "config_revision": config_revision,
        "source": "postgres_user_config",
        "category": "unknown",
        "latency_ms": None,
    }
    if not required_configured:
        response["status"] = "unconfigured"
        response["category"] = "unconfigured"
        return response

    with _AI_HEALTH_CACHE_LOCK:
        cached = copy.deepcopy(_AI_HEALTH_CACHE.get(_cache_key(user)))
    if not cached:
        return response
    if not cached.get("_launcher_cache"):
        # An old cache entry has no config identity or verified timestamp.
        response["status"] = "config_changed"
        return response

    response["observed_at"] = cached.get("observed_at")
    response["expires_at"] = cached.get("expires_at")
    cache_identity = (str(cached.get("config_id") or ""), _resolve_nonnegative_int(cached.get("config_revision"), -1))
    if cache_identity != (config_id, config_revision):
        response["status"] = "config_changed"
        return response

    snapshot = cached.get("snapshot") if isinstance(cached.get("snapshot"), dict) else {}
    response["category"] = _launcher_health_category(snapshot)
    latencies = []
    for name in ("web_test", "backend_test"):
        result = snapshot.get(name)
        if isinstance(result, dict) and isinstance(result.get("latency_ms"), int) and not isinstance(result.get("latency_ms"), bool):
            latencies.append(max(0, result["latency_ms"]))
    response["latency_ms"] = max(latencies) if latencies else None
    age = time.monotonic() - float(cached.get("observed_monotonic") or 0)
    response["status"] = "stale" if age >= _AI_HEALTH_CACHE_TTL_SECONDS else "current"
    return response


class _PinnedNetworkBackend(httpcore.AsyncNetworkBackend):
    """Dial only the validated IP while HTTP Host and TLS SNI retain the URL host."""

    def __init__(self, address: str):
        self._address = address
        self._backend = httpcore.AnyIOBackend()

    async def connect_tcp(self, host: str, port: int, timeout: float | None = None, local_address: str | None = None, socket_options=None):
        return await self._backend.connect_tcp(
            self._address, port, timeout=timeout, local_address=local_address, socket_options=socket_options
        )

    async def connect_unix_socket(self, path: str, timeout: float | None = None):
        return await self._backend.connect_unix_socket(path, timeout=timeout)

    async def sleep(self, seconds: float):
        await self._backend.sleep(seconds)


def _validate_manual_test_url(value: str) -> tuple[str, str]:
    """Reject URL forms and resolved addresses that could redirect a test into a private network."""
    try:
        value = str(value or "").strip()
        if len(value) > 2048 or any(ord(char) < 32 for char in value):
            raise ValueError("unsupported_url")
        parsed = urlsplit(value)
        if parsed.scheme not in {"https", "http"} or not parsed.hostname:
            raise ValueError("unsupported_url")
        if parsed.username is not None or parsed.password is not None or parsed.query or parsed.fragment:
            raise ValueError("unsupported_url")
        host = parsed.hostname.rstrip(".").lower()
        if parsed.port is not None and not (1 <= parsed.port <= 65535):
            raise ValueError("unsupported_url")
        if parsed.scheme != "https":
            raise ValueError("unsupported_url")

        try:
            addresses = [ipaddress.ip_address(host)]
        except ValueError:
            if host.endswith((".localhost", ".local", ".internal", ".test")):
                raise ValueError("unsafe_address")
            else:
                try:
                    addresses = [
                        ipaddress.ip_address(item[4][0])
                        for item in socket.getaddrinfo(host, parsed.port or (443 if parsed.scheme == "https" else 80), type=socket.SOCK_STREAM)
                    ]
                except (OSError, ValueError):
                    raise ValueError("unsafe_address") from None
        if not addresses:
            raise ValueError("unsafe_address")
        if any(
            not address.is_global or address.is_private or address.is_loopback or address.is_link_local
            or address.is_multicast or address.is_reserved or address.is_unspecified
            for address in addresses
        ):
            raise ValueError("unsafe_address")
        return value.strip().rstrip("/") + "/chat/completions", str(addresses[0])
    except (ValueError, UnicodeError):
        raise ValueError("unsafe_url") from None


def _manual_test_result(status: str, message: str, latency_ms: Optional[int] = None) -> Dict[str, Any]:
    return {"status": status, "message": message, "latency_ms": latency_ms, "checked_at": _now_text()}


def _manual_test_fingerprint(
    *,
    backend: str,
    settings: Optional[dict],
    saved_config_snapshot: Any,
) -> str:
    """Bind an idempotency key to the confirmed target without retaining overrides."""
    overrides = settings if isinstance(settings, dict) else {}
    canonical_overrides = {}
    for name, value in overrides.items():
        if not isinstance(name, str):
            raise ValueError("invalid_manual_test_settings")
        # Keep presence, JSON type and exact string content. Resolution can apply
        # truthiness before trimming, so folding whitespace/empty/null into absence
        # could bind different provider targets or credentials to one request ID.
        canonical_overrides[name] = {
            "present": True,
            "type": type(value).__name__,
            "value": value,
        }

    if isinstance(saved_config_snapshot, dict):
        config_id = str(saved_config_snapshot.get("id") or "")
        config_revision = _resolve_nonnegative_int(saved_config_snapshot.get("config_revision"), 0)
    else:
        config_id = ""
        config_revision = 0
    payload = json.dumps(
        {
            "backend": backend,
            "config_id": config_id,
            "config_revision": config_revision,
            "overrides": canonical_overrides,
        },
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        allow_nan=False,
    ).encode("utf-8")
    return hmac.new(_AI_MANUAL_TEST_FINGERPRINT_KEY, payload, "sha256").hexdigest()


async def _send_manual_ai_test(config: Dict[str, Any]) -> Dict[str, Any]:
    """Send one fixed, bounded, non-sensitive chat completion request without SDK retries."""
    started = time.perf_counter()
    try:
        endpoint, pinned_address = await asyncio.wait_for(
            asyncio.to_thread(_validate_manual_test_url, str(config.get("base_url") or "")),
            timeout=_AI_MANUAL_TEST_DNS_TIMEOUT_SECONDS,
        )
    except asyncio.TimeoutError:
        return _manual_test_result("rejected", "目标地址解析超时，已阻止请求。", int((time.perf_counter() - started) * 1000))
    except ValueError:
        return _manual_test_result("rejected", "目标 URL 不符合安全策略。", int((time.perf_counter() - started) * 1000))
    if not config.get("api_key") or not config.get("model_name"):
        return _manual_test_result("rejected", "AI 配置不完整。")
    if len(str(config.get("api_key"))) > 4096 or len(str(config.get("model_name"))) > 200:
        return _manual_test_result("rejected", "AI 配置字段超出长度限制。")

    timeout = httpx.Timeout(_AI_MANUAL_TEST_TIMEOUT_SECONDS)
    try:
        async def request_once():
            transport = httpx.AsyncHTTPTransport(retries=0)
            # HTTPX keeps the URL hostname for Host/SNI; pin only the TCP dial.
            transport._pool._network_backend = _PinnedNetworkBackend(pinned_address)
            async with httpx.AsyncClient(
                timeout=timeout, follow_redirects=False, trust_env=False, transport=transport
            ) as client:
                async with client.stream(
                    "POST",
                    endpoint,
                    headers={"Authorization": f"Bearer {config['api_key']}", "Content-Type": "application/json"},
                    json={
                        "model": str(config["model_name"]),
                        "messages": [{"role": "user", "content": _AI_MANUAL_TEST_PROMPT}],
                        "max_tokens": 10,
                        "stream": False,
                    },
                ) as response:
                    body = bytearray()
                    async for chunk in response.aiter_bytes():
                        body.extend(chunk)
                        if len(body) > _AI_MANUAL_TEST_MAX_RESPONSE_BYTES:
                            return _manual_test_result("error", "服务响应超出大小限制。", int((time.perf_counter() - started) * 1000))
                    latency = int((time.perf_counter() - started) * 1000)
                    if 300 <= response.status_code < 400:
                        return _manual_test_result("error", "服务返回重定向，已阻止。", latency)
                    if response.status_code == 401 or response.status_code == 403:
                        return _manual_test_result("auth_failed", "认证失败。", latency)
                    if response.status_code == 429:
                        return _manual_test_result("rate_limited", "服务限流。", latency)
                    if response.status_code == 404:
                        return _manual_test_result("model_unavailable", "模型或接口不可用。", latency)
                    if response.status_code < 200 or response.status_code >= 300:
                        return _manual_test_result("error", "服务请求失败。", latency)
                    try:
                        payload = json.loads(body)
                        choices = payload.get("choices") if isinstance(payload, dict) else None
                        if not isinstance(choices, list) or not choices:
                            raise ValueError("invalid_response")
                    except (ValueError, UnicodeError):
                        return _manual_test_result("error", "服务响应格式无效。", latency)
                    return _manual_test_result("success", "AI 模型连接测试成功。", latency)

        return await asyncio.wait_for(request_once(), timeout=_AI_MANUAL_TEST_TIMEOUT_SECONDS)
    except (httpx.TimeoutException, asyncio.TimeoutError):
        return _manual_test_result("unknown", "请求超时，服务端是否处理或计费未知。", int((time.perf_counter() - started) * 1000))
    except Exception as exc:
        logger.warning("手动AI测试请求失败", extra={"event": "ai_manual_test_failed", "error_type": type(exc).__name__})
        return _manual_test_result("unknown", "网络请求结果未知。", int((time.perf_counter() - started) * 1000))


async def run_portable_ai_manual_test(
    user: Optional[dict],
    request_id: str,
    settings: Optional[dict],
    *,
    saved_config_snapshot: Any = _CONFIG_SNAPSHOT_UNSET,
) -> Dict[str, Any]:
    """Run/deduplicate portable manual tests without touching the saved health snapshot."""
    owner_id = _resolve_user_id(user) or "anonymous"
    cache_id = f"{owner_id}:{request_id}"
    backend = (STORAGE_BACKEND() or "local").lower()
    if saved_config_snapshot is _CONFIG_SNAPSHOT_UNSET and backend == "postgres":
        try:
            saved_config_snapshot = _current_postgres_ai_config(user) or {}
        except Exception as exc:
            logger.error(
                "Manual AI test config snapshot failed",
                extra={"event": "ai_manual_test_config_snapshot_failed", "error_type": type(exc).__name__},
            )
            return _manual_test_result("error", "AI 配置暂时不可用，请重新读取后再试。")
    if saved_config_snapshot is _CONFIG_SNAPSHOT_UNSET:
        # Non-PostgreSQL Web mode has no persisted per-user row identity.
        fingerprint_snapshot = None
    else:
        fingerprint_snapshot = saved_config_snapshot
    try:
        fingerprint = _manual_test_fingerprint(
            backend=backend,
            settings=settings,
            saved_config_snapshot=fingerprint_snapshot,
        )
    except (TypeError, ValueError, OverflowError):
        return _manual_test_result("rejected", "AI 测试设置无效，未发送请求。")
    with _AI_MANUAL_TEST_LOCK:
        bound_fingerprint = _AI_MANUAL_TEST_FINGERPRINTS.get(cache_id)
        if bound_fingerprint is not None and not hmac.compare_digest(bound_fingerprint, fingerprint):
            return _manual_test_result("conflict", "此请求 ID 已绑定到另一组测试设置，请重新确认后再试。")
        previous = _AI_MANUAL_TEST_RESULTS.get(cache_id)
        if previous is not None:
            if bound_fingerprint is None:
                _AI_MANUAL_TEST_FINGERPRINTS[cache_id] = fingerprint
            return copy.deepcopy(previous)
        task = _AI_MANUAL_TEST_INFLIGHT.get(cache_id)
        if task is None:
            calls = _AI_MANUAL_TEST_CALLS.setdefault(owner_id, deque())
            now = time.monotonic()
            while calls and now - calls[0] >= 60:
                calls.popleft()
            if len(calls) >= 5:
                return _manual_test_result("rate_limited", "手动测试频率过高，请稍后再试。")
            calls.append(now)
            if bound_fingerprint is None:
                _AI_MANUAL_TEST_FINGERPRINTS[cache_id] = fingerprint
            if saved_config_snapshot is _CONFIG_SNAPSHOT_UNSET:
                task = asyncio.create_task(_perform_portable_ai_manual_test(user, settings))
            else:
                task = asyncio.create_task(
                    _perform_portable_ai_manual_test(
                        user, settings, saved_config_snapshot=saved_config_snapshot,
                    )
                )
            _AI_MANUAL_TEST_INFLIGHT[cache_id] = task
            task.add_done_callback(lambda finished: _remember_manual_test_result(cache_id, finished))
        elif bound_fingerprint is None:
            _AI_MANUAL_TEST_FINGERPRINTS[cache_id] = fingerprint
    # Disconnecting/cancelling the HTTP request must not cancel the one provider
    # attempt or erase its request ID while the provider may already have billed it.
    try:
        return copy.deepcopy(await asyncio.shield(task))
    except asyncio.CancelledError:
        if not task.cancelled():
            raise
        return _manual_test_result("unknown", "测试任务被取消，服务端是否处理或计费未知。")


def _remember_manual_test_result(cache_id: str, task: asyncio.Task) -> None:
    try:
        result = task.result()
    except asyncio.CancelledError:
        result = _manual_test_result("unknown", "测试任务被取消，服务端是否处理或计费未知。")
    except Exception as exc:
        logger.error("手动AI测试任务异常", extra={"event": "ai_manual_test_task_failed", "error_type": type(exc).__name__})
        result = _manual_test_result("unknown", "测试请求结果未知。")
    with _AI_MANUAL_TEST_LOCK:
        _AI_MANUAL_TEST_RESULTS[cache_id] = copy.deepcopy(result)
        if _AI_MANUAL_TEST_INFLIGHT.get(cache_id) is task:
            _AI_MANUAL_TEST_INFLIGHT.pop(cache_id, None)
        while len(_AI_MANUAL_TEST_RESULTS) > _AI_MANUAL_TEST_RESULT_LIMIT:
            evicted_id = next(iter(_AI_MANUAL_TEST_RESULTS))
            _AI_MANUAL_TEST_RESULTS.pop(evicted_id)
            _AI_MANUAL_TEST_FINGERPRINTS.pop(evicted_id, None)


async def _perform_portable_ai_manual_test(
    user: Optional[dict],
    settings: Optional[dict],
    *,
    saved_config_snapshot: Any = _CONFIG_SNAPSHOT_UNSET,
) -> Dict[str, Any]:
    overrides = settings if isinstance(settings, dict) else {}
    if saved_config_snapshot is _CONFIG_SNAPSHOT_UNSET:
        config = _resolve_effective_ai_config(user, overrides=overrides)
    else:
        config = _resolve_effective_ai_config(
            user, overrides=overrides, persisted_config=saved_config_snapshot,
        )

    # The manual path pins the provider hostname to its validated public IP.
    # The configured proxy path cannot currently preserve that destination pin,
    # so never silently bypass an enabled proxy or weaken the SSRF protection.
    if config.get("proxy_ai_enabled"):
        return _manual_test_result(
            "rejected",
            "当前配置启用了 AI 代理；手动测试暂不支持代理，未发送请求。",
        )

    # A changed target requires a replacement key in this request; do not silently reuse
    # credentials that were saved for a different origin.
    if saved_config_snapshot is _CONFIG_SNAPSHOT_UNSET:
        saved_config = _resolve_effective_ai_config(user)
    else:
        saved_config = _resolve_effective_ai_config(user, persisted_config=saved_config_snapshot)
    if _origin(config.get("base_url")) != _origin(saved_config.get("base_url")):
        if not str(overrides.get("OPENAI_API_KEY") or "").strip():
            return _manual_test_result("rejected", "目标地址已更改，请为新目标重新输入 API Key。")
    return await _send_manual_ai_test(config)


def _origin(value: Any) -> tuple:
    try:
        parsed = urlsplit(str(value or "").strip())
        return (parsed.scheme.lower(), (parsed.hostname or "").lower(), parsed.port)
    except ValueError:
        return ()
