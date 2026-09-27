#!/usr/bin/env python3
"""Explicit normal-mode entry point for the Windows portable Web service."""

from __future__ import annotations

import argparse
import logging
import os
import sys
from typing import Sequence

import uvicorn

from src.portable.web_runtime import (
    PortableWebConfigurationError,
    PortableWebStartupError,
    build_settings,
    create_application,
)


LOOPBACK_HOST = "127.0.0.1"
logger = logging.getLogger(__name__)


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Run the normal portable Web service")
    parser.add_argument("--mode", required=True, choices=("normal",))
    parser.add_argument("--instance-id", required=True)
    parser.add_argument("--port", type=int, required=True)
    return parser


def run(argv: Sequence[str] | None = None, *, environ=None, server_runner=None) -> int:
    args = _parser().parse_args(argv)
    settings = build_settings(
        mode=args.mode,
        instance_id=args.instance_id,
        port=args.port,
        environ=os.environ if environ is None else environ,
    )
    if server_runner is not None:
        application = create_application(settings)
        server_runner(application, host=LOOPBACK_HOST, port=settings.port)
    else:
        server = None

        def request_server_exit() -> None:
            if server is None:
                raise RuntimeError("portable Web server is not available")
            server.should_exit = True

        application = create_application(settings, request_server_exit=request_server_exit)
        server = uvicorn.Server(uvicorn.Config(
            application, host=LOOPBACK_HOST, port=settings.port, access_log=False,
        ))
        server.run()
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    try:
        return run(argv)
    except (PortableWebConfigurationError, PortableWebStartupError) as exc:
        print(f"Portable Web startup error: {exc}", file=sys.stderr)
        return 2
    except Exception:
        logger.error("Portable Web startup failed", extra={"event": "portable_web_start_failed"})
        print("Portable Web startup error: startup failed", file=sys.stderr)
        return 3


if __name__ == "__main__":
    raise SystemExit(main())
