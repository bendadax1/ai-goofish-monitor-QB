#!/usr/bin/env python3
"""便携版独立维护服务入口；不启动常规业务 Web 应用。"""

from src.portable.maintenance import main


if __name__ == "__main__":
    raise SystemExit(main())
