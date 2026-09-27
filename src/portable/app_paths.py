"""Read-only bridge from frozen application configuration to runtime paths.

This module intentionally does not import :mod:`src.config` at import time.
Only a business consumer that needs a filesystem location calls
``get_portable_runtime_paths``; pure models and maintenance helpers therefore
do not acquire configuration or filesystem side effects merely by importing
this module.
"""

from __future__ import annotations

import os
import stat
import sys
from pathlib import Path
from typing import Mapping, Optional, Sequence

from src.portable.context import portable_mode
from src.runtime_paths import RuntimePaths


_PORTABLE_WORKER_TARGETS = frozenset({"collector", "login"})
_WORKER_EXCLUDED_ENVIRONMENT_KEYS = frozenset({
    "GOOFISH_LAUNCHER_TOKEN",
    "GOOFISH_PORTABLE_ADMIN_DATABASE_URL",
})
_WORKER_PORTABLE_ENVIRONMENT_KEYS = frozenset({
    "GOOFISH_PORTABLE_MODE",
    "GOOFISH_PORTABLE_PROGRAM_ROOT",
    "GOOFISH_PORTABLE_DATA_ROOT",
    "GOOFISH_PORTABLE_CACHE_ROOT",
    "GOOFISH_PORTABLE_DATABASE_URL",
    "GOOFISH_PORTABLE_BROWSER_ROOT",
})


def get_portable_runtime_paths() -> Optional[RuntimePaths]:
    """Return the frozen portable roots, or ``None`` for legacy execution.

    ``src.config`` freezes the Launcher-controlled root values before legacy
    dotenv loading. Reusing that object prevents a later environment mutation
    from redirecting a business write outside the selected portable data root.
    """

    config = sys.modules.get("src.config")
    if config is None:
        # Legacy consumers historically did not need application configuration
        # just to construct a relative path. Do not trigger dotenv discovery or
        # config's image-directory creation unless portable mode was explicitly
        # selected in the process environment.
        if not portable_mode():
            return None
        from src import config as loaded_config

        config = loaded_config

    if not config.PORTABLE_MODE:
        return None

    paths = getattr(config, "_portable_paths", None)
    if not isinstance(paths, RuntimePaths):
        raise RuntimeError("便携版运行路径未初始化")
    return paths


def _frozen_portable_environment() -> dict[str, str]:
    """Return Launcher inputs captured by config before any dotenv loading."""

    config = sys.modules.get("src.config")
    values = getattr(config, "_PORTABLE_CONTROLLED_ENVIRONMENT", {}) if config is not None else {}
    return {str(key): str(value) for key, value in values.items() if str(key).startswith("GOOFISH_PORTABLE_")}


def portable_worker_command(target: str, arguments: Sequence[str]) -> Optional[list[str]]:
    """Build the fixed portable bootstrap command for a worker target.

    The bootstrap owns the target allow-list.  This caller merely selects one
    of its fixed application targets and never passes a caller-controlled
    script path to an embedded Python process.
    """

    paths = get_portable_runtime_paths()
    if paths is None:
        return None
    if target not in _PORTABLE_WORKER_TARGETS:
        raise ValueError("便携版工作进程目标不受支持")
    bootstrap = paths.program_path("scripts", "portable", "python-bootstrap.py")
    if not bootstrap.is_file():
        raise RuntimeError("便携版 Python 启动器缺失")
    return [
        sys.executable,
        "-I",
        "-B",
        "-u",
        str(bootstrap),
        "--app-root",
        str(paths.program_root),
        "--target",
        target,
        "--",
        *[str(argument) for argument in arguments],
    ]


def portable_worker_environment(
    environment: Mapping[str, str] | None = None,
) -> Optional[dict[str, str]]:
    """Return a child environment pinned to frozen portable roots.

    Task-specific ``GOOFISH_*`` overrides supplied by the caller remain
    available, except Launcher-maintenance credentials. ``PYTHONHOME`` and
    ``PYTHONPATH`` cannot redirect the isolated runtime, and temporary files
    stay beneath the rebuildable cache root.
    """

    paths = get_portable_runtime_paths()
    if paths is None:
        return None

    result = dict(os.environ if environment is None else environment)
    result.pop("PYTHONHOME", None)
    result.pop("PYTHONPATH", None)
    for key in _WORKER_EXCLUDED_ENVIRONMENT_KEYS:
        result.pop(key, None)
    for key in tuple(result):
        if key.startswith("GOOFISH_PORTABLE_") or key.startswith("GOOFISH_LAUNCHER_"):
            result.pop(key, None)
    result.update({
        key: value
        for key, value in _frozen_portable_environment().items()
        if key in _WORKER_PORTABLE_ENVIRONMENT_KEYS
    })
    result.update({
        "GOOFISH_PORTABLE_MODE": "portable",
        "GOOFISH_PORTABLE_PROGRAM_ROOT": str(paths.program_root),
        "GOOFISH_PORTABLE_DATA_ROOT": str(paths.data_root),
        "GOOFISH_PORTABLE_CACHE_ROOT": str(paths.cache_root),
    })
    temp_directory = paths.cache_path("workers")
    try:
        temp_directory.mkdir(parents=True, exist_ok=True)
    except OSError as exc:
        raise RuntimeError("无法创建便携版工作进程临时目录") from exc
    result["TEMP"] = str(temp_directory)
    result["TMP"] = str(temp_directory)
    result["PYTHONIOENCODING"] = "utf-8"
    result["PYTHONUTF8"] = "1"
    result["PYTHONDONTWRITEBYTECODE"] = "1"
    return result


def _paths_overlap(first: Path, second: Path) -> bool:
    try:
        first.relative_to(second)
        return True
    except ValueError:
        try:
            second.relative_to(first)
            return True
        except ValueError:
            return False


def _has_reparse_point(path: Path) -> bool:
    """Return whether an existing component contains a Windows reparse point."""

    current = path
    while True:
        try:
            attributes = os.lstat(current).st_file_attributes
        except (AttributeError, OSError):
            return True
        if attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            return True
        parent = current.parent
        if parent == current:
            return False
        current = parent


def portable_browser_executable() -> Optional[Path]:
    """Return a verified bundled Chromium executable in portable mode.

    The portable process must never silently fall back to system Edge/Chrome or
    let Playwright download a browser. The Launcher supplies one frozen,
    independently versioned component root; it may be a sibling of ``app/``.
    """

    paths = get_portable_runtime_paths()
    if paths is None:
        return None

    frozen_environment = _frozen_portable_environment()
    raw_root = frozen_environment.get("GOOFISH_PORTABLE_BROWSER_ROOT")
    if not raw_root or "\x00" in raw_root:
        raise RuntimeError("便携版浏览器目录未配置")
    root = Path(raw_root)
    if not root.is_absolute():
        raise RuntimeError("便携版浏览器目录必须是绝对路径")
    try:
        if _has_reparse_point(root):
            raise RuntimeError("便携版浏览器目录不能包含重解析点")
        resolved_root = root.resolve(strict=True)
    except (OSError, RuntimeError, ValueError) as exc:
        raise RuntimeError("便携版浏览器目录不可用") from exc
    if not resolved_root.is_dir():
        raise RuntimeError("便携版浏览器目录不可用")
    if _paths_overlap(resolved_root, paths.data_root) or _paths_overlap(resolved_root, paths.cache_root):
        raise RuntimeError("便携版浏览器目录不能与数据或缓存目录重叠")

    # Playwright 1.57.0 Windows x64 Chromium layout. The Launcher selects an
    # exact browsers/<browser-id> component root, not an open-ended cache.
    executable = resolved_root / "chrome-win64" / "chrome.exe"
    try:
        if _has_reparse_point(executable):
            raise RuntimeError("便携版 Chromium 不能包含重解析点")
        resolved_executable = executable.resolve(strict=True)
        resolved_executable.relative_to(resolved_root)
    except (OSError, RuntimeError, ValueError) as exc:
        raise RuntimeError("便携版 Chromium 可执行文件缺失") from exc
    if not resolved_executable.is_file():
        raise RuntimeError("便携版 Chromium 可执行文件缺失")
    return resolved_executable


__all__ = [
    "get_portable_runtime_paths",
    "portable_browser_executable",
    "portable_worker_command",
    "portable_worker_environment",
]
