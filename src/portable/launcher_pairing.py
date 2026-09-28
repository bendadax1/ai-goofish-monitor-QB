"""Explicit browser approval and narrow, revocable Launcher user sessions.

This is deliberately separate from both the Web cookie session and the
Launcher instance-control credential. Launcher sessions are accepted only by
this module's routes and are never valid ``p1.`` Web sessions.
"""

from __future__ import annotations

import copy
from datetime import datetime, timedelta, timezone
import hashlib
import hmac
import logging
import re
import secrets
import threading
import uuid
from typing import Any
from html import escape
from urllib.parse import parse_qs
from urllib.parse import urlsplit

from fastapi import APIRouter, HTTPException, Request
from fastapi.responses import HTMLResponse, JSONResponse

from src.config import STORAGE_BACKEND
from src.portable.context import portable_mode
from src.storage import get_storage
from src.web.auth import SESSION_COOKIE_NAME, get_current_user


logger = logging.getLogger(__name__)
router = APIRouter(prefix="/api/launcher", tags=["launcher"])

PAIRING_TTL_SECONDS = 5 * 60
LAUNCHER_SESSION_TTL_SECONDS = 8 * 60 * 60
MAX_PENDING_PAIRINGS = 4
MAX_ACTIVE_SESSIONS = 4
_PAIR_CODE_ALPHABET = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"
_PAIR_CODE_LENGTH = 12
_VERIFIER_PATTERN = re.compile(r"^[A-Za-z0-9_-]{43,128}$")
_LAUNCHER_TOKEN_PATTERN = re.compile(r"^l1\.[A-Za-z0-9_-]{43}$")


def _digest(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


def _as_utc(value: Any) -> datetime | None:
    if isinstance(value, str):
        try:
            value = datetime.fromisoformat(value.replace("Z", "+00:00"))
        except ValueError:
            return None
    if not isinstance(value, datetime) or value.tzinfo is None:
        return None
    return value.astimezone(timezone.utc)


class PairingError(RuntimeError):
    """An expected pairing/session state error with a safe public message."""


class LauncherPairingRegistry:
    """Single-process pending grants and source-session-bound Launcher tokens."""

    def __init__(self) -> None:
        self._lock = threading.RLock()
        self._pending: dict[str, dict[str, Any]] = {}
        # Digest -> binding metadata. The bearer token itself is never retained.
        self._bindings: dict[str, dict[str, Any]] = {}

    def create(self, *, instance_id: str, verifier: str) -> dict[str, Any]:
        if not isinstance(instance_id, str) or not instance_id or len(instance_id) > 128:
            raise PairingError("invalid instance")
        if not isinstance(verifier, str) or not _VERIFIER_PATTERN.fullmatch(verifier):
            raise PairingError("invalid verifier")
        now = _utcnow()
        with self._lock:
            self._expire_locked(now)
            if len(self._pending) >= MAX_PENDING_PAIRINGS:
                raise PairingError("too many pending approvals")
            pairing_id = str(uuid.uuid4())
            code = "".join(secrets.choice(_PAIR_CODE_ALPHABET) for _ in range(_PAIR_CODE_LENGTH))
            self._pending[pairing_id] = {
                "pairing_id": pairing_id,
                "code_digest": _digest(code),
                "verifier_digest": _digest(verifier),
                "instance_id": instance_id,
                "created_at": now,
                "expires_at": now + timedelta(seconds=PAIRING_TTL_SECONDS),
                "approved_user_id": None,
                "approved_username": None,
                "source_session_digest": None,
            }
            return {
                "pairing_id": pairing_id,
                "pairing_code": code,
                "expires_at": self._pending[pairing_id]["expires_at"].isoformat(),
                "expires_in_seconds": PAIRING_TTL_SECONDS,
            }

    def preview(self, *, code: str, username: str, can_write_ai: bool) -> dict[str, Any]:
        with self._lock:
            record = self._find_code_locked(code)
            if record["approved_user_id"] is not None:
                raise PairingError("approval already completed")
            if not can_write_ai:
                raise PairingError("AI configuration permission required")
            remaining = max(0, int((record["expires_at"] - _utcnow()).total_seconds()))
            return {
                "username": username,
                "instance_label": "此设备上的便携 Launcher",
                "instance_id": record["instance_id"],
                "permissions": ["读取当前用户摘要", "读取与编辑当前用户默认 AI 连接"],
                "expires_in_seconds": remaining,
                "session_max_lifetime_seconds": LAUNCHER_SESSION_TTL_SECONDS,
            }

    def approve(
        self,
        *,
        code: str,
        user_id: str,
        username: str,
        source_session_token: str,
        can_write_ai: bool,
    ) -> dict[str, Any]:
        if not can_write_ai:
            raise PairingError("AI configuration permission required")
        if not source_session_token:
            raise PairingError("browser session required")
        with self._lock:
            record = self._find_code_locked(code)
            if record["approved_user_id"] is not None:
                raise PairingError("approval already completed")
            record["approved_user_id"] = str(user_id)
            record["approved_username"] = str(username)
            record["source_session_digest"] = _digest(source_session_token)
            return {"status": "approved", "username": str(username)}

    def exchange(
        self,
        *,
        pairing_id: str,
        verifier: str,
        instance_id: str,
        storage,
    ) -> dict[str, Any] | None:
        if not _VERIFIER_PATTERN.fullmatch(verifier or ""):
            raise PairingError("invalid verifier")
        now = _utcnow()
        with self._lock:
            self._expire_locked(now)
            record = self._pending.get(pairing_id)
            if not record or record["instance_id"] != instance_id:
                raise PairingError("pairing is unavailable")
            if not hmac.compare_digest(record["verifier_digest"], _digest(verifier)):
                raise PairingError("pairing is unavailable")
            if record["approved_user_id"] is None:
                return None

            user_id = record["approved_user_id"]
            source_digest = record["source_session_digest"]
            try:
                source_session = storage.get_session_by_token(source_digest)
            except Exception:
                logger.error("Launcher source session lookup failed", extra={"event": "launcher_source_session_lookup_failed"})
                raise PairingError("browser session is unavailable") from None
            source_expiry = _as_utc((source_session or {}).get("expires_at"))
            if (
                not source_session
                or str(source_session.get("user_id")) != str(user_id)
                or source_expiry is None
                or source_expiry <= now
            ):
                self._pending.pop(pairing_id, None)
                raise PairingError("browser session expired")

            try:
                user = storage.get_user_by_id(user_id)
            except Exception:
                logger.error("Launcher approved-user lookup failed", extra={"event": "launcher_approved_user_lookup_failed"})
                raise PairingError("user is unavailable") from None
            if not user or not user.get("is_active", False):
                self._pending.pop(pairing_id, None)
                raise PairingError("user is unavailable")
            if not _browser_ai_write_allowed(user):
                self._pending.pop(pairing_id, None)
                raise PairingError("AI configuration permission required")

            other_instances = {
                binding["instance_id"]
                for binding in self._bindings.values()
                if binding["instance_id"] != instance_id
            }
            if len(other_instances) >= MAX_ACTIVE_SESSIONS:
                raise PairingError("too many active sessions")

            expires_at = min(
                now + timedelta(seconds=LAUNCHER_SESSION_TTL_SECONDS),
                source_expiry,
            )
            token = "l1." + secrets.token_urlsafe(32)
            token_digest = _digest(token)
            self._bindings[token_digest] = {
                "user_id": str(user_id),
                "instance_id": instance_id,
                "source_session_digest": source_digest,
                "expires_at": expires_at,
                "scopes": ("me:read", "ai:read", "ai:write"),
            }
            self._pending.pop(pairing_id, None)
            for other_digest, other_binding in list(self._bindings.items()):
                if other_digest != token_digest and other_binding["instance_id"] == instance_id:
                    self._revoke_locked(other_digest)
            return {
                "status": "exchanged",
                "access_token": token,
                "token_type": "Bearer",
                "expires_at": expires_at.isoformat(),
                "instance_id": instance_id,
            }

    def authenticate(self, *, token: str, instance_id: str, storage) -> dict[str, Any] | None:
        if not isinstance(token, str) or not _LAUNCHER_TOKEN_PATTERN.fullmatch(token):
            return None
        token_digest = _digest(token)
        now = _utcnow()
        with self._lock:
            binding = self._bindings.get(token_digest)
            if not binding or binding["instance_id"] != instance_id:
                return None
            expiry = binding["expires_at"]
            if expiry <= now:
                self._revoke_locked(token_digest)
                return None
            try:
                source_session = storage.get_session_by_token(binding["source_session_digest"])
                user = storage.get_user_by_id(binding["user_id"])
            except Exception:
                logger.error("Launcher session validation failed", extra={"event": "launcher_session_validation_failed"})
                return None
            if (
                not source_session
                or str(source_session.get("user_id")) != binding["user_id"]
                or not user
                or not user.get("is_active", False)
            ):
                self._revoke_locked(token_digest)
                return None
            return {
                "user_id": str(user.get("id") or binding["user_id"]),
                "username": str(user.get("username") or ""),
                "role": user.get("role", "viewer"),
                "is_active": True,
            }

    def invalidate_source_session(self, source_session_token: str) -> None:
        if not source_session_token:
            return
        source_digest = _digest(source_session_token)
        with self._lock:
            for token_digest, binding in list(self._bindings.items()):
                if hmac.compare_digest(binding["source_session_digest"], source_digest):
                    self._revoke_locked(token_digest)
            for pairing_id, record in list(self._pending.items()):
                if record.get("source_session_digest") and hmac.compare_digest(
                    record["source_session_digest"], source_digest
                ):
                    self._pending.pop(pairing_id, None)

    def revoke(self, *, token: str, instance_id: str) -> None:
        if not isinstance(token, str) or not _LAUNCHER_TOKEN_PATTERN.fullmatch(token):
            return
        token_digest = _digest(token)
        with self._lock:
            binding = self._bindings.get(token_digest)
            if binding and binding["instance_id"] == instance_id:
                self._revoke_locked(token_digest)

    def _find_code_locked(self, code: str) -> dict[str, Any]:
        if not isinstance(code, str) or len(code) != _PAIR_CODE_LENGTH:
            raise PairingError("pairing code is invalid or expired")
        digest = _digest(code.upper())
        self._expire_locked(_utcnow())
        for record in self._pending.values():
            if hmac.compare_digest(record["code_digest"], digest):
                return record
        raise PairingError("pairing code is invalid or expired")

    def _expire_locked(self, now: datetime) -> None:
        for pairing_id, record in list(self._pending.items()):
            if record["expires_at"] <= now:
                self._pending.pop(pairing_id, None)
        for token_digest, binding in list(self._bindings.items()):
            if binding["expires_at"] <= now:
                self._bindings.pop(token_digest, None)

    def _revoke_locked(self, token_digest: str) -> None:
        self._bindings.pop(token_digest, None)


registry = LauncherPairingRegistry()


def invalidate_launcher_sessions_for_browser_token(token: str) -> None:
    """Revoke grants bound to a Web cookie when it logs out or switches user."""
    registry.invalidate_source_session(token)


def _pairing_error(error: PairingError) -> JSONResponse:
    message = str(error)
    if message == "too many pending approvals":
        return JSONResponse(status_code=429, content={"detail": "too many pending approvals"})
    if message == "too many active sessions":
        return JSONResponse(status_code=429, content={"detail": "too many active sessions"})
    if message == "AI configuration permission required":
        return JSONResponse(status_code=403, content={"detail": "AI configuration permission required"})
    if message in {"browser session expired", "browser session is unavailable", "user is unavailable"}:
        return JSONResponse(status_code=401, content={"detail": "authorization session is unavailable"})
    if message == "approval already completed":
        return JSONResponse(status_code=409, content={"detail": "approval already completed"})
    return JSONResponse(status_code=400, content={"detail": "pairing code is invalid or expired"})


def _private_json_response(content: dict[str, Any], status_code: int = 200) -> JSONResponse:
    return JSONResponse(status_code=status_code, content=content, headers={
        "Cache-Control": "no-store",
        "Pragma": "no-cache",
        "Referrer-Policy": "no-referrer",
    })


async def _json_object(request: Request, allowed: set[str]) -> dict[str, Any]:
    if request.headers.get("content-type", "").split(";", 1)[0].strip().lower() != "application/json":
        raise HTTPException(status_code=415, detail="application/json required")
    body = await request.body()
    if len(body) > 8192:
        raise HTTPException(status_code=413, detail="request body too large")
    try:
        import json
        value = json.loads(body)
    except Exception:
        raise HTTPException(status_code=400, detail="invalid JSON body") from None
    if not isinstance(value, dict) or set(value) - allowed:
        raise HTTPException(status_code=422, detail="request fields are invalid")
    return value


def _browser_user(request: Request) -> tuple[dict[str, Any], str]:
    user = get_current_user(request)
    cookie = request.cookies.get(SESSION_COOKIE_NAME, "")
    if not user or not cookie:
        raise HTTPException(status_code=401, detail="未登录，请先登录")
    return user, cookie


async def _form_object(request: Request, allowed: set[str]) -> dict[str, str]:
    if request.headers.get("content-type", "").split(";", 1)[0].strip().lower() != "application/x-www-form-urlencoded":
        raise HTTPException(status_code=415, detail="application/x-www-form-urlencoded required")
    body = await request.body()
    if len(body) > 2048:
        raise HTTPException(status_code=413, detail="request body too large")
    try:
        fields = parse_qs(body.decode("utf-8"), keep_blank_values=True, strict_parsing=True)
    except (UnicodeDecodeError, ValueError):
        raise HTTPException(status_code=400, detail="invalid form body") from None
    if set(fields) - allowed or any(len(values) != 1 for values in fields.values()):
        raise HTTPException(status_code=422, detail="form fields are invalid")
    return {key: values[0] for key, values in fields.items()}


def _approval_page(content: str, *, status_code: int = 200) -> HTMLResponse:
    body = (
        "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\">"
        "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
        "<title>授权便携 Launcher</title><body>"
        f"<main>{content}</main></body></html>"
    )
    return HTMLResponse(body, status_code=status_code, headers={
        "Cache-Control": "no-store",
        "Pragma": "no-cache",
        "Referrer-Policy": "no-referrer",
        "X-Content-Type-Options": "nosniff",
        "Content-Security-Policy": (
            "default-src 'none'; form-action 'self'; base-uri 'none'; "
            "frame-ancestors 'none'; script-src 'none'; style-src 'none'"
        ),
    })


def _browser_ai_write_allowed(user: dict[str, Any]) -> bool:
    from src.web.auth import check_permission, has_category
    return bool(has_category(user, "ai") or check_permission(user, "manage_system"))


def _ensure_portable_postgres() -> None:
    if not portable_mode() or STORAGE_BACKEND() != "postgres":
        raise HTTPException(status_code=404, detail="Launcher user authorization is available only in portable mode")


def _storage_for_auth():
    try:
        return get_storage()
    except Exception:
        logger.error("Launcher authorization storage is unavailable", extra={"event": "launcher_auth_storage_unavailable"})
        raise HTTPException(status_code=503, detail="Launcher authorization service is unavailable") from None


@router.get("/authorize", response_class=HTMLResponse)
async def launcher_authorize_page(request: Request):
    _ensure_portable_postgres()
    user = get_current_user(request)
    if not user:
        return _approval_page(
            "<h1>需要登录</h1><p>请先在此浏览器登录 Web 系统，然后返回 Launcher 再次打开授权页。</p>"
            "<p><a href=\"/login\">前往登录</a></p>", status_code=401,
        )
    if not _browser_ai_write_allowed(user):
        return _approval_page("<h1>无权授权</h1><p>当前账号没有 AI 配置权限。</p>", status_code=403)
    username = escape(str(user.get("username") or ""))
    return _approval_page(
        f"<h1>授权便携 Launcher</h1><p>当前登录账号：<strong>{username}</strong></p>"
        "<p>在 Launcher 中复制 12 位配对码并粘贴到下方。配对码本身不能授予访问权限。</p>"
        "<form method=\"post\" action=\"/api/launcher/authorize/preview\">"
        "<label>配对码 <input name=\"pairing_code\" minlength=\"12\" maxlength=\"12\" required autocomplete=\"off\"></label>"
        "<button type=\"submit\">查看授权内容</button></form>"
    )


@router.post("/authorize/preview", response_class=HTMLResponse)
async def launcher_authorize_preview(request: Request):
    _ensure_portable_postgres()
    user, _ = _browser_user(request)
    if not _browser_ai_write_allowed(user):
        return _approval_page("<h1>无权授权</h1><p>当前账号没有 AI 配置权限。</p>", status_code=403)
    payload = await _form_object(request, {"pairing_code"})
    try:
        preview = registry.preview(
            code=payload.get("pairing_code", ""), username=str(user.get("username") or ""),
            can_write_ai=True,
        )
    except PairingError:
        return _approval_page("<h1>授权不可用</h1><p>配对码无效、已过期或已使用。</p>", status_code=400)
    code = escape(payload["pairing_code"].upper(), quote=True)
    content = (
        "<h1>确认授权</h1>"
        f"<p>当前登录账号：<strong>{escape(preview['username'])}</strong></p>"
        f"<p>设备：{escape(preview['instance_label'])} ({escape(preview['instance_id'])})</p>"
        f"<p>配对请求 {escape(str(preview['expires_in_seconds']))} 秒后过期；Launcher 会话最长 8 小时，且不超过当前 Web 登录会话剩余时间。</p>"
        "<ul><li>读取当前用户摘要</li><li>读取和编辑当前用户默认 AI 连接</li></ul>"
        "<p>此授权不授予管理员权限，也不能访问其他用户的配置。</p>"
        "<form method=\"post\" action=\"/api/launcher/authorize/approve\">"
        f"<input type=\"hidden\" name=\"pairing_code\" value=\"{code}\">"
        "<button type=\"submit\" name=\"confirm\" value=\"approve\">明确授权此设备</button></form>"
        "<p><a href=\"/api/launcher/authorize\">取消</a></p>"
    )
    return _approval_page(content)


@router.post("/authorize/approve", response_class=HTMLResponse)
async def launcher_authorize_approve(request: Request):
    _ensure_portable_postgres()
    user, cookie = _browser_user(request)
    if not _browser_ai_write_allowed(user):
        return _approval_page("<h1>无权授权</h1><p>当前账号没有 AI 配置权限。</p>", status_code=403)
    payload = await _form_object(request, {"pairing_code", "confirm"})
    if payload.get("confirm") != "approve" or "pairing_code" not in payload:
        raise HTTPException(status_code=422, detail="explicit approval is required")
    try:
        registry.approve(
            code=payload["pairing_code"],
            user_id=str(user.get("user_id") or user.get("id") or ""),
            username=str(user.get("username") or ""),
            source_session_token=cookie,
            can_write_ai=True,
        )
    except PairingError:
        return _approval_page("<h1>授权失败</h1><p>配对码无效、已过期或已使用。</p>", status_code=400)
    return _approval_page("<h1>授权完成</h1><p>此设备已绑定到当前账号，请返回 Launcher。</p>")


def register_internal_routes(outer, settings) -> None:
    """Register control-authenticated, JSON-body-only Launcher pairing routes."""

    @outer.post("/internal/launcher/pairing")
    async def create_launcher_pairing(request: Request):
        _ensure_portable_postgres()
        _internal_control(request, settings)
        payload = await _json_object(request, {"instance_id", "verifier"})
        if set(payload) != {"instance_id", "verifier"} or payload.get("instance_id") != settings.instance_id:
            raise HTTPException(status_code=403, detail="control context mismatch")
        try:
            return _private_json_response(registry.create(
                instance_id=settings.instance_id, verifier=payload.get("verifier")
            ))
        except PairingError as exc:
            return _pairing_error(exc)

    @outer.post("/internal/launcher/pairing/exchange")
    async def exchange_launcher_pairing(request: Request):
        _ensure_portable_postgres()
        _internal_control(request, settings)
        payload = await _json_object(request, {"pairing_id", "verifier"})
        if set(payload) != {"pairing_id", "verifier"}:
            raise HTTPException(status_code=422, detail="request fields are invalid")
        try:
            result = registry.exchange(
                pairing_id=payload["pairing_id"],
                verifier=payload["verifier"],
                instance_id=settings.instance_id,
                storage=_storage_for_auth(),
            )
        except PairingError as exc:
            return _pairing_error(exc)
        if result is None:
            return _private_json_response({"status": "pending"}, status_code=202)
        return _private_json_response(result)

    @outer.post("/internal/launcher/session/revoke")
    async def revoke_launcher_session(request: Request):
        _ensure_portable_postgres()
        _internal_control(request, settings)
        payload = await _json_object(request, {"access_token"})
        if set(payload) != {"access_token"}:
            raise HTTPException(status_code=422, detail="request fields are invalid")
        registry.revoke(token=payload["access_token"], instance_id=settings.instance_id)
        return _private_json_response({"status": "revoked"})


def _internal_control(request: Request, settings) -> None:
    from src.portable.web_runtime import _launcher_authenticated
    authorization = request.headers.get("authorization")
    if not _launcher_authenticated(authorization, settings.launcher_token):
        raise HTTPException(status_code=401, detail="unauthorized")
    if (
        request.headers.get("host", "").lower() != f"127.0.0.1:{settings.port}"
        or request.headers.get("x-goofish-instance-id") != settings.instance_id
        or request.headers.get("origin") is not None
    ):
        raise HTTPException(status_code=403, detail="control context mismatch")


def _extract_bearer(request: Request) -> str:
    authorization = request.headers.get("authorization", "")
    if not authorization.startswith("Bearer "):
        raise HTTPException(status_code=401, detail="unauthorized")
    return authorization[7:]


def _launcher_user(request: Request) -> dict[str, Any]:
    _ensure_portable_postgres()
    instance_id = request.headers.get("x-goofish-instance-id", "")
    host = request.headers.get("host", "").lower()
    if not instance_id or request.headers.get("origin") != f"http://{host}":
        raise HTTPException(status_code=403, detail="Launcher context mismatch")
    # The outer host gate ensures exact 127.0.0.1:<port>; requiring the exact
    # same-origin value makes this bearer unusable from an ordinary browser page.
    user = registry.authenticate(token=_extract_bearer(request), instance_id=instance_id, storage=_storage_for_auth())
    if not user:
        raise HTTPException(status_code=401, detail="Launcher authorization expired; authorize again")
    return user


@router.get("/me")
async def launcher_me(request: Request):
    user = _launcher_user(request)
    from src.web.auth import check_permission, has_category
    can_read_ai = bool(has_category(user, "ai") or has_category(user, "tasks") or check_permission(user, "manage_system"))
    can_write_ai = _browser_ai_write_allowed(user)
    if not can_read_ai:
        raise HTTPException(status_code=403, detail="AI configuration permission required")
    return {
        "user_id": user["user_id"],
        "username": user["username"],
        "is_active": True,
        "permissions": {"ai_read": can_read_ai, "ai_write": can_write_ai},
        "instance_id": request.headers.get("x-goofish-instance-id"),
    }


@router.get("/ai")
async def launcher_get_ai(request: Request):
    user = _launcher_user(request)
    from src.web.settings_manager import _require_ai_or_tasks_access, get_ai_settings
    _require_ai_or_tasks_access(user)
    settings = await get_ai_settings(user)
    allowed = {
        "OPENAI_API_KEY_SET", "OPENAI_BASE_URL", "OPENAI_MODEL_NAME",
        "OPENAI_BASE_URL_REDACTED",
        "config_revision", "config_id", "config_source", "effective_state",
        "IS_MULTI_USER_MODE", "NEEDS_SETUP",
        "AI_MAX_TOKENS_PARAM_NAME", "AI_MAX_TOKENS_LIMIT",
    }
    result = {key: value for key, value in settings.items() if key in allowed}
    base_url = str(result.get("OPENAI_BASE_URL") or "").strip()
    try:
        parsed = urlsplit(base_url)
        if parsed.username is not None or parsed.password is not None or parsed.query or parsed.fragment:
            result["OPENAI_BASE_URL"] = ""
            result["OPENAI_BASE_URL_REDACTED"] = True
    except ValueError:
        result["OPENAI_BASE_URL"] = ""
        result["OPENAI_BASE_URL_REDACTED"] = True
    return result


@router.get("/ai/health")
async def launcher_get_ai_health(request: Request):
    """Read only the current user's revision-bound AI health cache."""
    user = _launcher_user(request)
    from src.web.settings_manager import _require_ai_access
    _require_ai_access(user)
    try:
        from src.web.ai_health import get_portable_launcher_ai_health
        result = get_portable_launcher_ai_health(user)
    except Exception as exc:
        logger.warning(
            "Launcher AI health cache is unavailable",
            extra={"event": "launcher_ai_health_cache_unavailable", "error_type": type(exc).__name__},
        )
        raise HTTPException(status_code=503, detail="AI health cache is unavailable") from None
    return _private_json_response(result)


@router.put("/ai")
async def launcher_update_ai(request: Request):
    user = _launcher_user(request)
    from src.web.settings_manager import _require_ai_access, update_ai_settings
    _require_ai_access(user)
    payload = await _json_object(request, {
        "config_revision", "config_id", "OPENAI_BASE_URL", "OPENAI_MODEL_NAME",
        "OPENAI_API_KEY", "remove_api_key",
        "AI_MAX_TOKENS_PARAM_NAME", "AI_MAX_TOKENS_LIMIT",
    })
    if not {"config_revision", "config_id"}.issubset(payload):
        raise HTTPException(status_code=428, detail="config_revision and config_id are required")
    if isinstance(payload["config_revision"], bool) or not isinstance(payload["config_revision"], int):
        raise HTTPException(status_code=428, detail="config_revision is invalid")
    if not isinstance(payload["config_id"], str) or len(payload["config_id"]) > 128:
        raise HTTPException(status_code=428, detail="config_id is invalid")
    for key, maximum in (("OPENAI_BASE_URL", 2048), ("OPENAI_MODEL_NAME", 256), ("OPENAI_API_KEY", 4096)):
        if key in payload and (not isinstance(payload[key], str) or len(payload[key]) > maximum):
            raise HTTPException(status_code=422, detail=f"{key} is invalid")
    if "OPENAI_BASE_URL" in payload:
        parsed = urlsplit(payload["OPENAI_BASE_URL"].strip())
        if parsed.username is not None or parsed.password is not None or parsed.query or parsed.fragment:
            raise HTTPException(status_code=422, detail="OPENAI_BASE_URL must not contain credentials, query, or fragment")
    if "remove_api_key" in payload and not isinstance(payload["remove_api_key"], bool):
        raise HTTPException(status_code=422, detail="remove_api_key is invalid")
    if "AI_MAX_TOKENS_PARAM_NAME" in payload:
        name = payload["AI_MAX_TOKENS_PARAM_NAME"]
        if not isinstance(name, str) or (name and not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", name)):
            raise HTTPException(status_code=422, detail="AI_MAX_TOKENS_PARAM_NAME is invalid")
    if "AI_MAX_TOKENS_LIMIT" in payload:
        limit = payload["AI_MAX_TOKENS_LIMIT"]
        if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 2147483647:
            raise HTTPException(status_code=422, detail="AI_MAX_TOKENS_LIMIT is invalid")

    # Run the existing dependency check explicitly before reusing the same
    # production validation, merge and config_revision CAS service.
    result = await update_ai_settings(payload, user)
    if isinstance(result, dict):
        return {key: result[key] for key in (
            "message", "config_revision", "config_id", "config_source", "effective_state"
        ) if key in result}
    return {"message": "AI configuration saved"}


@router.post("/ai/test")
async def launcher_test_ai(request: Request):
    """Run one explicitly confirmed, user-scoped portable AI test."""
    user = _launcher_user(request)
    if not _browser_ai_write_allowed(user):
        raise HTTPException(status_code=403, detail="AI configuration permission required")

    payload = await _json_object(request, {
        "confirmed", "request_id", "settings", "expected_config_id", "expected_config_revision",
    })
    if set(payload) != {
        "confirmed", "request_id", "settings", "expected_config_id", "expected_config_revision",
    } or payload.get("confirmed") is not True:
        raise HTTPException(status_code=422, detail="explicit AI test confirmation is required")
    expected_config_id = payload.get("expected_config_id")
    expected_config_revision = payload.get("expected_config_revision")
    if not isinstance(expected_config_id, str) or len(expected_config_id) > 128:
        raise HTTPException(status_code=422, detail="expected_config_id is invalid")
    if (
        isinstance(expected_config_revision, bool)
        or not isinstance(expected_config_revision, int)
        or expected_config_revision < 0
        or (not expected_config_id and expected_config_revision != 0)
    ):
        raise HTTPException(status_code=422, detail="expected_config_revision is invalid")
    request_id = payload.get("request_id")
    if not isinstance(request_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]{8,128}", request_id):
        raise HTTPException(status_code=422, detail="request_id is invalid")

    settings = payload.get("settings")
    if not isinstance(settings, dict) or set(settings) - {
        "OPENAI_BASE_URL", "OPENAI_MODEL_NAME", "OPENAI_API_KEY",
    }:
        raise HTTPException(status_code=422, detail="AI test settings are invalid")
    for key, maximum in (
        ("OPENAI_BASE_URL", 2048),
        ("OPENAI_MODEL_NAME", 256),
        ("OPENAI_API_KEY", 4096),
    ):
        value = settings.get(key)
        if value is not None and (not isinstance(value, str) or len(value) > maximum):
            raise HTTPException(status_code=422, detail="AI test settings are invalid")

    # Freeze and compare the current user's default config before any resolver can
    # perform DNS/provider work. The manual runner receives this exact row snapshot
    # and must not re-read a potentially changed target after confirmation.
    try:
        storage = get_storage()
        revision_reader = getattr(storage, "get_default_api_config_with_revision", None)
        if not callable(revision_reader):
            raise RuntimeError("API config revision reader is unavailable")
        current_config = revision_reader(str(user.get("user_id") or ""))
        current_config = copy.deepcopy(current_config) if isinstance(current_config, dict) else {}
        current_config_id = str(current_config.get("id") or "")
        current_config_revision = current_config.get("config_revision", 0)
        if isinstance(current_config_revision, bool):
            raise ValueError("invalid config revision")
        current_config_revision = int(current_config_revision or 0)
    except Exception as exc:
        logger.error(
            "Launcher AI test config snapshot failed",
            extra={"event": "launcher_ai_test_config_snapshot_failed", "error_type": type(exc).__name__},
        )
        raise HTTPException(status_code=503, detail="AI configuration is temporarily unavailable") from None
    if (
        current_config_id != expected_config_id
        or current_config_revision != expected_config_revision
    ):
        raise HTTPException(status_code=409, detail="AI configuration changed; reload and confirm the test again")

    # Reuse the bounded executor and request-ID dedupe used by the portable Web UI.
    # Launcher bearer credentials are never converted into Web cookies/sessions.
    try:
        from src.web.ai_health import run_portable_ai_manual_test
        result = await run_portable_ai_manual_test(
            user, request_id, settings, saved_config_snapshot=current_config or None,
        )
    except Exception as exc:
        logger.error(
            "Launcher manual AI test failed",
            extra={"event": "launcher_ai_manual_test_failed", "error_type": type(exc).__name__},
        )
        raise HTTPException(status_code=500, detail="AI test could not be completed") from None
    if result.get("status") == "conflict":
        raise HTTPException(status_code=409, detail="request_id is already bound to another confirmed AI test")
    return _private_json_response({
        "success": result.get("status") == "success",
        "message": result.get("message") or "AI test result unavailable",
        "test": result,
    })


__all__ = [
    "LAUNCHER_SESSION_TTL_SECONDS",
    "PAIRING_TTL_SECONDS",
    "MAX_ACTIVE_SESSIONS",
    "LauncherPairingRegistry",
    "PairingError",
    "invalidate_launcher_sessions_for_browser_token",
    "register_internal_routes",
    "router",
    "registry",
]
