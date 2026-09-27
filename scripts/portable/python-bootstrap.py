"""便携 Python 显式应用根启动器。

该脚本必须由内置 Python 通过绝对路径执行；它不读取 `.env`，也不
信任 cwd 或 PYTHONPATH。
"""

from __future__ import annotations

import argparse
import os
import runpy
import sys
from pathlib import Path
from typing import Sequence


_ALLOWED_ENTRIES = {
    "provision": Path("portable_provision.py"),
    "maintenance": Path("portable_server.py"),
    # Schema 初始化只由 Launcher 的显式首次设置阶段调用；它不启动 Web。
    "schema": Path("portable_schema.py"),
    # Normal portable hosting is distinct from the read-only maintenance entry.
    "web": Path("portable_web.py"),
    "collector": Path("collector.py"),
    "login": Path("login.py"),
    "backup": Path("portable_backup.py"),
    "restore": Path("portable_restore.py"),
}


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Run an application through the portable Python runtime")
    parser.add_argument("--app-root", required=True)
    parser.add_argument("--target", required=True, choices=tuple(_ALLOWED_ENTRIES))
    parser.add_argument("arguments", nargs=argparse.REMAINDER)
    return parser


def _app_root(value: str) -> Path:
    if "\x00" in value:
        raise ValueError("app-root is invalid")
    path = Path(value)
    if not path.is_absolute():
        raise ValueError("app-root must be absolute")
    try:
        resolved = path.resolve(strict=True)
    except (OSError, RuntimeError):
        raise ValueError("app-root could not be resolved") from None
    if not resolved.is_dir():
        raise ValueError("app-root must be a directory")
    return resolved


def _entry_path(app_root: Path, value: str) -> Path:
    relative = Path(value)
    if not value or "\x00" in value or relative.is_absolute() or relative.drive:
        raise ValueError("entry must be a relative application path")
    try:
        candidate = (app_root / relative).resolve(strict=True)
    except (OSError, RuntimeError):
        raise ValueError("entry could not be resolved") from None
    try:
        candidate.relative_to(app_root)
    except ValueError:
        raise ValueError("entry escapes app-root") from None
    if not candidate.is_file() or candidate.suffix.lower() != ".py":
        raise ValueError("entry must be an existing Python file")
    return candidate


def run(argv: Sequence[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    try:
        app_root = _app_root(args.app_root)
        entry = _entry_path(app_root, str(_ALLOWED_ENTRIES[args.target]))
    except ValueError as exc:
        print(f"Portable Python bootstrap error: {exc}", file=sys.stderr)
        return 2

    forwarded = list(args.arguments)
    if forwarded and forwarded[0] == "--":
        forwarded.pop(0)
    # `._pth` 保持运行时隔离，只在此经验证的显式入口中加入 app root。
    # All program components are immutable and included in the release hash
    # manifest. A worker's first import must not create __pycache__ there.
    sys.dont_write_bytecode = True
    sys.path.insert(0, str(app_root))
    os.environ.pop("PYTHONPATH", None)
    sys.argv = [str(entry), *forwarded]
    runpy.run_path(str(entry), run_name="__main__")
    return 0


if __name__ == "__main__":
    raise SystemExit(run())
