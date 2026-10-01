"""CI 环境守卫。

GitHub Actions 上只跑无需真实 PostgreSQL / Windows 原生运行时的用例。
需要真实数据库、Windows PG 集群或 win32 专用子进程行为的用例，通过本模块
的装饰器在非目标环境上整类跳过，避免 CI 因环境缺失而误报失败。

本地开发不受影响：满足条件时用例照常执行。
"""

from __future__ import annotations

import os
import sys
import unittest


def _env_flag(name: str) -> bool:
    value = os.environ.get(name, "")
    return value.strip().lower() in {"1", "true", "yes", "on"}


def in_ci() -> bool:
    """是否为 GitHub Actions 等无状态 CI 环境。"""
    return _env_flag("CI")


def has_database() -> bool:
    """是否存在可供测试使用的 PostgreSQL DSN。"""
    return bool(os.environ.get("DATABASE_URL", "").strip())


def needs_postgres(test_item):
    """需要真实 PostgreSQL 连接的用例。

    - 显式提供 DATABASE_URL 时运行（例如本机或后续 CI postgres service）。
    - 仅 CI 且无 DSN 时整类跳过；本机保留原行为，避免掩盖问题。
    """
    return unittest.skipUnless(
        not in_ci() or has_database(),
        "需要真实 PostgreSQL（CI 未提供 DATABASE_URL）",
    )(test_item)


def needs_windows(test_item):
    """需要 Windows 原生 PG 集群 / 子进程语义的用例。"""
    return unittest.skipUnless(
        sys.platform == "win32",
        "需要 Windows x64 原生运行时",
    )(test_item)


def needs_windows_dotnet(test_item):
    """需要 .NET SDK 构建 Launcher 的用例。"""
    return unittest.skipUnless(
        sys.platform == "win32" and not in_ci(),
        "需要 Windows + 本地 .NET SDK（CI 不构建 Launcher）",
    )(test_item)
