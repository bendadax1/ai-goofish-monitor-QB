"""停写后的本地任务身份与空 v2 旁文件显式升级；绝不自动扫描用户数据。"""

from __future__ import annotations

import argparse
from pathlib import Path
import sys

from src.storage.upstream_local import upgrade_local_data


_CONFIRMATION = "我已停止所有业务进程并保留配置备份"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data-root", required=True, type=Path)
    parser.add_argument("--config", required=True, type=Path)
    parser.add_argument("--backup", required=True, type=Path)
    parser.add_argument("--backup-sha256", required=True)
    parser.add_argument("--confirm-stopped", required=True)
    args = parser.parse_args(argv)
    if args.confirm_stopped != _CONFIRMATION:
        parser.error("停写确认不匹配；升级未开始")
    try:
        added = upgrade_local_data(args.config, args.data_root, args.backup,
                                   args.backup_sha256, verify_maintenance=lambda: None)
    except Exception:
        print("本地升级未确认成功；配置备份和现有数据已保留，请停止操作并检查。", file=sys.stderr)
        return 1
    print(f"本地 v2 数据结构已核验；新增稳定任务身份 {added} 个。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
