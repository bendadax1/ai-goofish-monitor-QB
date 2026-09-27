#!/usr/bin/env python3
"""便携版独立 schema 初始化入口；不启动常规业务 Web 应用。"""

from src.portable.schema import main


if __name__ == "__main__":
    raise SystemExit(main())
