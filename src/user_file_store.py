"""用户文件作用域工具。

统一管理 prompts/criteria/requirement/bayes 的“虚拟路径 -> 实际路径”映射：
- 单用户模式：继续使用仓库根目录下的原有共享目录
- 多用户模式：优先读取 state/user_files/{owner_id}/...，不存在时回退共享目录
"""

from __future__ import annotations

import os
import re
import logging
import stat
from pathlib import Path, PureWindowsPath
from typing import Dict, List, Optional

from src.portable.app_paths import get_portable_runtime_paths
from src.file_safety import UnsafeFilePathError, check_scoped_path, validate_filename

logger = logging.getLogger(__name__)

_KIND_CONFIG: Dict[str, Dict[str, object]] = {
    "prompts": {
        "shared_dir": Path("prompts"),
        "user_subdir": "prompts",
        "ext": ".txt",
    },
    "criteria": {
        "shared_dir": Path("criteria"),
        "user_subdir": "criteria",
        "ext": ".txt",
    },
    "requirement": {
        "shared_dir": Path("requirement"),
        "user_subdir": "requirement",
        "ext": ".txt",
    },
    "bayes": {
        "shared_dir": Path("prompts") / "bayes",
        "user_subdir": "bayes",
        "ext": ".json",
    },
}


def _user_file_root() -> Path:
    """Return the private user-file root for the selected execution mode."""

    paths = get_portable_runtime_paths()
    if paths is not None:
        return paths.data_path("state", "user_files")
    return Path("state") / "user_files"


def _shared_dir(kind: str) -> Path:
    """Return the writable shared directory for ``kind``."""

    config = _KIND_CONFIG[kind]
    paths = get_portable_runtime_paths()
    if paths is None:
        return Path(str(config["shared_dir"]))
    if kind == "bayes":
        return paths.data_path("assets", "prompts", "bayes")
    return paths.data_path("assets", str(config["user_subdir"]))


def _default_dir(kind: str) -> Optional[Path]:
    """Return an optional read-only portable seed directory.

    Defaults are deliberately limited to the future distribution layout. The
    source repository's live prompts and criteria are never treated as portable
    seeds before they are explicitly packaged under ``program/defaults``.
    """

    paths = get_portable_runtime_paths()
    if paths is None:
        return None
    if kind == "bayes":
        return paths.program_path("defaults", "prompts", "bayes")
    config = _KIND_CONFIG[kind]
    return paths.program_path("defaults", str(config["user_subdir"]))


def normalize_owner_id(owner_id: Optional[str]) -> Optional[str]:
    """标准化 owner_id。"""
    text = str(owner_id or "").strip()
    return text or None


def _sanitize_owner_fragment(owner_id: str) -> str:
    """将 owner_id 转为可用于目录名的片段。"""
    return re.sub(r"[^0-9a-zA-Z_-]", "_", owner_id)


def _validate_kind(kind: str) -> str:
    normalized = str(kind or "").strip().lower()
    if normalized not in _KIND_CONFIG:
        raise ValueError(f"不支持的文件类型: {kind}")
    return normalized


def _safe_filename(filename: str) -> str:
    text = str(filename or "").strip().replace("\\", "/")
    if get_portable_runtime_paths() is None:
        # 仅保留历史内部 basename 兼容；Web 入口必须先校验完整输入。
        text = text.split("/")[-1]
    return validate_filename(text)


def _checked_path(path: Path, *, default: bool = False, directory: bool = False) -> Path:
    paths = get_portable_runtime_paths()
    root = (paths.program_root if default else paths.data_root) if paths else Path.cwd()
    return check_scoped_path(path, root, directory=directory)


def get_user_scoped_path(kind: str, filename: str, owner_id: Optional[str]) -> Path:
    """返回用户私有路径（不做存在性判断）。"""
    normalized_owner = normalize_owner_id(owner_id)
    if not normalized_owner:
        raise ValueError("缺少 owner_id，无法构建用户私有路径")
    normalized_kind = _validate_kind(kind)
    config = _KIND_CONFIG[normalized_kind]
    safe_owner = _sanitize_owner_fragment(normalized_owner)
    safe_name = _safe_filename(filename)
    return _checked_path(_user_file_root() / safe_owner / str(config["user_subdir"]) / safe_name)


def get_shared_path(kind: str, filename: str) -> Path:
    """返回共享目录路径。"""
    normalized_kind = _validate_kind(kind)
    safe_name = _safe_filename(filename)
    return _checked_path(_shared_dir(normalized_kind) / safe_name)


def get_scoped_read_candidates(kind: str, filename: str, owner_id: Optional[str]) -> List[Path]:
    """返回读取候选路径（按优先级）。"""
    candidates: List[Path] = []
    normalized_owner = normalize_owner_id(owner_id)
    if normalized_owner:
        candidates.append(get_user_scoped_path(kind, filename, normalized_owner))
    candidates.append(get_shared_path(kind, filename))
    default_dir = _default_dir(_validate_kind(kind))
    if default_dir is not None:
        candidates.append(_checked_path(default_dir / _safe_filename(filename), default=True))
    return candidates


def resolve_scoped_path(kind: str, filename: str, owner_id: Optional[str], for_write: bool = False) -> Path:
    """解析作用域路径。

    for_write=True 时：
    - 有 owner_id：返回用户私有路径（并创建父目录）
    - 无 owner_id：返回共享路径（并创建父目录）
    """
    normalized_owner = normalize_owner_id(owner_id)
    if for_write:
        if normalized_owner:
            target = get_user_scoped_path(kind, filename, normalized_owner)
        else:
            target = get_shared_path(kind, filename)
        try:
            target.parent.mkdir(parents=True, exist_ok=True)
        except OSError:
            logger.warning("创建作用域文件目录失败")
            raise
        return _checked_path(target)

    for path in get_scoped_read_candidates(kind, filename, normalized_owner):
        if path.exists():
            return path
    if normalized_owner:
        return get_user_scoped_path(kind, filename, normalized_owner)
    return get_shared_path(kind, filename)


def list_scoped_files(kind: str, owner_id: Optional[str], include_shared: bool = True) -> List[str]:
    """列出作用域内文件名。"""
    normalized_kind = _validate_kind(kind)
    config = _KIND_CONFIG[normalized_kind]
    expected_ext = str(config.get("ext") or "").lower()

    names = set()
    normalized_owner = normalize_owner_id(owner_id)

    directories = []
    if normalized_owner:
        directories.append((get_user_scoped_path(normalized_kind, "__placeholder__", normalized_owner).parent, False))
    if include_shared:
        directories.append((_shared_dir(normalized_kind), False))
        default_dir = _default_dir(normalized_kind)
        if default_dir is not None:
            directories.append((default_dir, True))
    for folder, is_default in directories:
        _checked_path(folder, default=is_default, directory=True)
        try:
            if not folder.exists():
                continue
            for entry in folder.iterdir():
                info = entry.lstat()
                if stat.S_ISDIR(info.st_mode) and not getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                    continue
                try:
                    validate_filename(entry.name)
                    _checked_path(entry, default=is_default)
                except UnsafeFilePathError:
                    logger.warning("文件列表已跳过不安全或非普通文件条目")
                    continue
                if entry.is_file() and (not expected_ext or entry.suffix.lower() == expected_ext):
                    names.add(entry.name)
        except OSError:
            logger.warning("读取作用域文件列表失败")
            raise

    return sorted(names)


def build_virtual_prompt_path(reference_file: Optional[str]) -> str:
    """将参考模板字段标准化为 prompts/{filename}.txt 形式。"""
    text = str(reference_file or "").strip().replace("\\", "/")
    if not text:
        return "prompts/base_prompt.txt"
    if text.startswith("prompts/"):
        return text
    if "/" not in text:
        return f"prompts/{text}"
    return text


def resolve_virtual_task_file(raw_path: str, owner_id: Optional[str], for_write: bool = False) -> Path:
    """把任务中的虚拟路径解析为实际文件路径。"""
    text = str(raw_path or "").strip()
    if not text:
        return Path(text)
    normalized = text.replace("\\", "/")

    portable_paths = get_portable_runtime_paths()
    if portable_paths is not None:
        path_parts = Path(normalized).parts
        if (
            os.path.isabs(normalized)
            or Path(normalized).drive
            or PureWindowsPath(normalized).anchor
            or ".." in path_parts
        ):
            raise ValueError("便携版任务文件路径必须是受支持的相对虚拟路径")

    # Legacy absolute paths are retained for historical task compatibility.
    if os.path.isabs(normalized) or Path(normalized).drive:
        return Path(normalized)

    if normalized.startswith("prompts/bayes/"):
        filename = normalized.split("/", 2)[-1]
        return resolve_scoped_path("bayes", filename, owner_id=owner_id, for_write=for_write)
    if normalized.startswith("prompts/"):
        filename = normalized.split("/", 1)[-1]
        return resolve_scoped_path("prompts", filename, owner_id=owner_id, for_write=for_write)
    if normalized.startswith("criteria/"):
        filename = normalized.split("/", 1)[-1]
        return resolve_scoped_path("criteria", filename, owner_id=owner_id, for_write=for_write)
    if normalized.startswith("requirement/"):
        filename = normalized.split("/", 1)[-1]
        return resolve_scoped_path("requirement", filename, owner_id=owner_id, for_write=for_write)

    # 兼容老数据：裸文件名默认按 prompts 处理
    if "/" not in normalized and normalized.lower().endswith(".txt"):
        return resolve_scoped_path("prompts", normalized, owner_id=owner_id, for_write=for_write)

    if portable_paths is not None:
        raise ValueError("便携版任务文件路径必须使用受支持的虚拟目录")

    return Path(normalized)
