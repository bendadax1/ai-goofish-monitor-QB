"""Revocable portable user sessions, separate from Launcher control tokens."""

from datetime import datetime, timedelta, timezone
import hashlib
import logging
import re
import secrets


logger = logging.getLogger(__name__)
_TOKEN_PATTERN = re.compile(r"^p1\.[A-Za-z0-9_-]{43}$")


def _digest(token: str) -> str:
    return hashlib.sha256(token.encode("ascii")).hexdigest()


def issue_session(storage, user_id: str, lifetime_seconds: int) -> str:
    if not 1 <= lifetime_seconds <= 31 * 86400:
        raise ValueError("portable session lifetime is out of range")
    try:
        user = storage.get_user_by_id(user_id)
        if not user or not user.get("is_active", False):
            raise ValueError("user is not active")
        token = "p1." + secrets.token_urlsafe(32)
        storage.create_session({
            "user_id": user_id,
            "token_hash": _digest(token),
            "expires_at": datetime.now(timezone.utc) + timedelta(seconds=lifetime_seconds),
        })
        return token
    except Exception:
        logger.error("Portable session creation failed", extra={"event": "portable_session_issue_failed"})
        raise RuntimeError("无法创建用户会话") from None


def read_session(storage, token: str):
    if not isinstance(token, str) or not _TOKEN_PATTERN.fullmatch(token):
        return None
    try:
        session = storage.get_session_by_token(_digest(token))
        if not session:
            return None
        expiry = session.get("expires_at")
        if isinstance(expiry, str):
            expiry = datetime.fromisoformat(expiry.replace("Z", "+00:00"))
        if not isinstance(expiry, datetime) or expiry.tzinfo is None:
            return None
        if expiry <= datetime.now(timezone.utc):
            return None
        user = storage.get_user_by_id(session["user_id"])
        if not user or not user.get("is_active", False):
            return None
        # Always read current authorization; old cookies never retain a role.
        return {
            "user_id": str(user["id"]), "username": user["username"],
            "role": user.get("role", "viewer"), "email": user.get("email"),
            "is_active": True,
        }
    except Exception:
        logger.warning("Portable session validation failed", extra={"event": "portable_session_read_failed"})
        return None


def revoke_session(storage, token: str) -> None:
    if not isinstance(token, str) or not _TOKEN_PATTERN.fullmatch(token):
        return
    try:
        session = storage.get_session_by_token(_digest(token))
        if session:
            storage.delete_session(session["id"])
    except Exception:
        logger.error("Portable session revocation failed", extra={"event": "portable_session_revoke_failed"})
        raise RuntimeError("无法撤销用户会话，请重试退出登录") from None
