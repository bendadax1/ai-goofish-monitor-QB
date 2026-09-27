"""Verified, fixed portable first-schema seed assets.

Only the Launcher-created ``app/defaults`` bundle is accepted.  This module
does not inspect working-tree prompt directories or user-owned data.
"""

from __future__ import annotations

import hashlib
import json
import os
import stat
import uuid
from dataclasses import dataclass
from pathlib import Path, PureWindowsPath
from typing import Any

from sqlalchemy import text
from sqlalchemy.engine import Connection

from src.storage.models import BayesProfile, BayesSample, GroupPermission, PromptTemplate, UserGroup


_MANIFEST_NAME = "seed_manifest.json"
_MAX_MANIFEST_BYTES = 64 * 1024
_MAX_SEED_FILE_BYTES = 4 * 1024 * 1024
_EXPECTED_FILES = {
    "prompts/base_prompt.txt": "prompt",
    "prompts/bayes/bayes_v1.json": "bayes",
}
_PERMISSION_CATEGORIES = ("tasks", "results", "accounts", "notify", "ai", "admin")
_SYSTEM_GROUPS = (
    ("super_admin_group", "超级管理员组", "系统预置：全量权限", (True, True, True, True, True, True)),
    ("admin_group", "系统管理员组", "系统预置：管理权限", (True, True, True, True, True, True)),
    ("operator_group", "操作员组", "系统预置：日常运营权限", (True, True, True, True, False, False)),
    ("viewer_group", "查看者组", "系统预置：只读权限", (False, True, False, False, False, False)),
)


class PortableSeedError(RuntimeError):
    """A packaged seed bundle is absent, malformed, or incompatible."""


@dataclass(frozen=True)
class SeedBundle:
    prompt_content: str
    bayes_payload: dict[str, Any]


def _safe_relative_path(value: object) -> str:
    if not isinstance(value, str) or not value or "\x00" in value:
        raise PortableSeedError("seed manifest path is invalid")
    normalized = value.replace("\\", "/")
    path = PureWindowsPath(normalized)
    if path.anchor or normalized.startswith("/") or ".." in path.parts:
        raise PortableSeedError("seed manifest path is invalid")
    return normalized


def _reject_reparse_points(path: Path, *, stop_at: Path | None = None) -> None:
    """Reject symlink/junction components before resolving their targets."""

    current = path
    while True:
        try:
            details = os.lstat(current)
        except OSError as exc:
            raise PortableSeedError("portable seed path is unavailable") from exc
        attributes = getattr(details, "st_file_attributes", 0)
        if stat.S_ISLNK(details.st_mode) or attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
            raise PortableSeedError("portable seed path cannot contain reparse points")
        if stop_at is not None and current == stop_at:
            return
        parent = current.parent
        if parent == current:
            return
        current = parent


def _read_limited(path: Path, maximum: int) -> bytes:
    try:
        if path.stat().st_size > maximum:
            raise PortableSeedError("portable seed file exceeds its size limit")
        with path.open("rb") as handle:
            data = handle.read(maximum + 1)
    except OSError as exc:
        raise PortableSeedError("portable seed file is unavailable") from exc
    if len(data) > maximum:
        raise PortableSeedError("portable seed file exceeds its size limit")
    return data


def load_seed_bundle(seed_root: Path) -> SeedBundle:
    """Load exactly two manifest-verified assets from an explicit defaults root."""

    root = Path(seed_root)
    _reject_reparse_points(root)
    try:
        resolved_root = root.resolve(strict=True)
    except OSError as exc:
        raise PortableSeedError("portable defaults directory is unavailable") from exc
    if not resolved_root.is_dir():
        raise PortableSeedError("portable defaults directory is invalid")
    manifest_path = resolved_root / _MANIFEST_NAME
    try:
        _reject_reparse_points(manifest_path, stop_at=resolved_root)
        manifest = json.loads(_read_limited(manifest_path, _MAX_MANIFEST_BYTES).decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError, RecursionError) as exc:
        raise PortableSeedError("portable seed manifest is unavailable") from exc
    if not isinstance(manifest, dict) or set(manifest) != {"format_version", "files"} or manifest.get("format_version") != 1:
        raise PortableSeedError("portable seed manifest is invalid")
    entries = manifest.get("files")
    if not isinstance(entries, list) or len(entries) != len(_EXPECTED_FILES):
        raise PortableSeedError("portable seed manifest has an unexpected file set")

    content: dict[str, bytes] = {}
    seen: set[str] = set()
    for entry in entries:
        if not isinstance(entry, dict) or set(entry) != {"path", "kind", "size", "sha256"}:
            raise PortableSeedError("portable seed manifest entry is invalid")
        relative = _safe_relative_path(entry.get("path"))
        if relative in seen or _EXPECTED_FILES.get(relative) != entry.get("kind"):
            raise PortableSeedError("portable seed manifest has an unexpected file set")
        size = entry.get("size")
        digest = entry.get("sha256")
        if isinstance(size, bool) or not isinstance(size, int) or not 0 <= size <= _MAX_SEED_FILE_BYTES or not isinstance(digest, str) or len(digest) != 64:
            raise PortableSeedError("portable seed manifest entry is invalid")
        if digest.lower() != digest or any(character not in "0123456789abcdef" for character in digest):
            raise PortableSeedError("portable seed manifest entry is invalid")
        candidate = resolved_root.joinpath(*relative.split("/"))
        try:
            _reject_reparse_points(candidate, stop_at=resolved_root)
            resolved_candidate = candidate.resolve(strict=True)
            resolved_candidate.relative_to(resolved_root)
            raw = _read_limited(resolved_candidate, _MAX_SEED_FILE_BYTES)
        except (OSError, ValueError) as exc:
            raise PortableSeedError("portable seed file is unavailable") from exc
        if len(raw) != size or hashlib.sha256(raw).hexdigest() != digest:
            raise PortableSeedError("portable seed file integrity check failed")
        content[relative] = raw
        seen.add(relative)
    if seen != set(_EXPECTED_FILES):
        raise PortableSeedError("portable seed manifest has an unexpected file set")
    try:
        prompt = content["prompts/base_prompt.txt"].decode("utf-8")
        bayes = json.loads(content["prompts/bayes/bayes_v1.json"].decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError) as exc:
        raise PortableSeedError("portable seed content is invalid") from exc
    if not prompt.strip() or not isinstance(bayes, dict) or str(bayes.get("version") or "").strip() != "bayes_v1":
        raise PortableSeedError("portable seed content is invalid")
    return SeedBundle(prompt_content=prompt, bayes_payload=bayes)


def apply_system_seeds(connection: Connection, bundle: SeedBundle) -> None:
    """Insert fixed system groups and asset defaults into a fresh schema."""

    for code, name, description, permissions in _SYSTEM_GROUPS:
        group_id = uuid.uuid4()
        connection.execute(UserGroup.__table__.insert().values(
            id=group_id, code=code, name=name, description=description, is_system=True,
        ))
        for category, enabled in zip(_PERMISSION_CATEGORIES, permissions, strict=True):
            connection.execute(GroupPermission.__table__.insert().values(
                id=uuid.uuid4(), group_id=group_id, category=category, enabled=enabled,
            ))
    connection.execute(PromptTemplate.__table__.insert().values(
        id=uuid.uuid4(), owner_id=None, name="base_prompt.txt",
        content=bundle.prompt_content, is_default=True,
    ))
    bayes = bundle.bayes_payload
    profile_id = uuid.uuid4()
    connection.execute(BayesProfile.__table__.insert().values(
        id=profile_id, owner_id=None, version="bayes_v1",
        display_name=str(bayes.get("display_name") or "bayes_v1"),
        recommendation_fusion=bayes.get("recommendation_fusion"),
        bayes_feature_rules=bayes.get("bayes_feature_rules"),
        is_default=bool(bayes.get("is_default", True)),
    ))
    raw_samples = bayes.get("_samples")
    if isinstance(raw_samples, dict):
        for bucket, label in (("可信", 1), ("不可信", 0)):
            samples = raw_samples.get(bucket)
            if not isinstance(samples, list):
                continue
            for item in samples:
                if not isinstance(item, dict) or not isinstance(item.get("vector"), list):
                    continue
                try:
                    vector = [float(value) for value in item["vector"]]
                except (TypeError, ValueError):
                    continue
                connection.execute(BayesSample.__table__.insert().values(
                    id=uuid.uuid4(), owner_id=None, profile_id=profile_id, profile_version="bayes_v1",
                    name=item.get("name") or "导入样本", vector=vector, label=label,
                    source=str(item.get("source") or "preset"), item_id=item.get("item_id"), note=item.get("note"),
                ))


def copy_defaults_to_first_admin(connection: Connection, user_id: uuid.UUID) -> None:
    """Copy existing system prompt/Bayes defaults once for the first user."""

    prompt = connection.execute(text(
        'SELECT name, content, is_default FROM "public"."prompt_templates" '
        'WHERE owner_id IS NULL AND name = :name FOR UPDATE'
    ), {"name": "base_prompt.txt"}).first()
    if prompt is not None:
        connection.execute(PromptTemplate.__table__.insert().values(
            id=uuid.uuid4(), owner_id=user_id, name=prompt[0], content=prompt[1], is_default=bool(prompt[2]),
        ))
    bayes = connection.execute(text(
        'SELECT version, display_name, recommendation_fusion, bayes_feature_rules, is_default '
        'FROM "public"."bayes_profiles" WHERE owner_id IS NULL AND version = :version FOR UPDATE'
    ), {"version": "bayes_v1"}).first()
    if bayes is not None:
        profile_id = uuid.uuid4()
        connection.execute(BayesProfile.__table__.insert().values(
            id=profile_id, owner_id=user_id, version=bayes[0], display_name=bayes[1],
            recommendation_fusion=bayes[2], bayes_feature_rules=bayes[3], is_default=bool(bayes[4]),
        ))
        samples = connection.execute(text(
            'SELECT name, vector, label, source, item_id, note FROM "public"."bayes_samples" '
            'WHERE owner_id IS NULL AND profile_version = :version FOR UPDATE'
        ), {"version": "bayes_v1"}).fetchall()
        for sample in samples:
            connection.execute(BayesSample.__table__.insert().values(
                id=uuid.uuid4(), owner_id=user_id, profile_id=profile_id, profile_version="bayes_v1",
                name=sample[0], vector=list(sample[1] or []), label=int(sample[2]), source=sample[3] or "preset",
                item_id=sample[4], note=sample[5],
            ))


__all__ = ["PortableSeedError", "SeedBundle", "apply_system_seeds", "copy_defaults_to_first_admin", "load_seed_bundle"]
