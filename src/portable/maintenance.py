"""无业务副作用的便携版维护就绪服务。

该模块不导入常规 Web 应用、配置、调度器或存储适配器。它只使用受限的
PostgreSQL 连接执行存活与 schema 版本查询。
"""

from __future__ import annotations

import argparse
import hashlib
import hmac
import ipaddress
import logging
import os
import re
import threading
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable, Mapping, Protocol, Sequence

import psycopg2
from fastapi import FastAPI, Header, HTTPException
from fastapi.responses import JSONResponse
import uvicorn

from src.version import VERSION


PROTOCOL_VERSION = 1
SUPPORTED_SCHEMA_MIN = 1
SUPPORTED_SCHEMA_MAX = 1
LOOPBACK_HOST = "127.0.0.1"
DEFAULT_PORT = 8000
CONNECT_TIMEOUT_SECONDS = 2
STATEMENT_TIMEOUT_MILLISECONDS = 2_000

TOKEN_ENVIRONMENT_VARIABLE = "GOOFISH_LAUNCHER_TOKEN"
DATABASE_ENVIRONMENT_VARIABLE = "GOOFISH_PORTABLE_DATABASE_URL"

_INSTANCE_ID_PATTERN = re.compile(r"^[A-Za-z0-9._-]{1,128}$")
_LIBPQ_SERVICE_ENVIRONMENT_VARIABLES = ("PGSERVICE", "PGSERVICEFILE")
_SCHEMA_TABLE_NAME = "public.app_schema_version"

logger = logging.getLogger(__name__)


class MaintenanceConfigurationError(ValueError):
    """启动前配置错误，信息不包含密钥、DSN 或原始异常。"""


class MaintenanceServerError(RuntimeError):
    """维护服务启动失败，信息不包含原始 I/O 异常。"""


@dataclass(frozen=True)
class DatabaseTarget:
    """经过安全边界验证的 libpq 连接参数。"""

    parameters: Mapping[str, str] = field(repr=False)


@dataclass(frozen=True)
class MaintenanceSettings:
    instance_id: str
    program_root: Path
    data_root: Path
    port: int
    launcher_token: str = field(repr=False)
    database_target: DatabaseTarget = field(repr=False)
    mode: str = "maintenance"
    app_version: str = VERSION


@dataclass(frozen=True)
class DatabaseProbeResult:
    database_status: str
    schema_status: str
    schema_version: int | None
    failure_reason: str | None

    @property
    def ready(self) -> bool:
        return self.database_status == "available" and self.schema_status == "compatible"


class ReadinessProbe(Protocol):
    def check(self) -> DatabaseProbeResult:
        """返回只读就绪结果。"""


def _configuration_error(message: str) -> MaintenanceConfigurationError:
    return MaintenanceConfigurationError(message)


def _absolute_root(value: str, argument_name: str) -> Path:
    if "\x00" in value:
        logger.warning(
            "Portable root path parsing failed",
            extra={"event": "portable_root_parsing_failed"},
        )
        raise _configuration_error(f"{argument_name} is invalid")
    try:
        path = Path(value)
    except (OSError, ValueError, RuntimeError):
        logger.warning(
            "Portable root path parsing failed",
            extra={"event": "portable_root_parsing_failed"},
        )
        raise _configuration_error(f"{argument_name} is invalid") from None
    if not path.is_absolute():
        raise _configuration_error(f"{argument_name} must be an absolute path")
    try:
        return path.resolve(strict=False)
    except (OSError, RuntimeError):
        logger.warning(
            "Portable root path resolution failed",
            extra={"event": "portable_root_resolution_failed"},
        )
        raise _configuration_error(f"{argument_name} could not be resolved") from None


def _is_loopback(value: str) -> bool:
    candidate = value.strip()
    if not candidate or "," in candidate:
        return False
    if candidate.lower() == "localhost":
        return True
    try:
        return ipaddress.ip_address(candidate).is_loopback
    except ValueError:
        return False


def validate_database_target(
    dsn: str,
    *,
    environ: Mapping[str, str] | None = None,
) -> DatabaseTarget:
    """解析 DSN 并拒绝隐式或非本机 PostgreSQL 目标。"""

    environment = os.environ if environ is None else environ
    if not dsn or "\x00" in dsn:
        raise _configuration_error("portable database configuration is missing or invalid")
    if any(environment.get(name, "").strip() for name in _LIBPQ_SERVICE_ENVIRONMENT_VARIABLES):
        raise _configuration_error("libpq service configuration is not allowed in portable mode")

    try:
        parsed = psycopg2.extensions.parse_dsn(dsn)
    except Exception:
        raise _configuration_error("portable database configuration is missing or invalid") from None

    if parsed.get("service") or parsed.get("servicefile"):
        raise _configuration_error("libpq service configuration is not allowed in portable mode")

    required = ("host", "port", "dbname", "user", "password")
    if any(not str(parsed.get(name, "")).strip() for name in required):
        raise _configuration_error("portable database target must be fully explicit")

    host = str(parsed["host"])
    hostaddr = str(parsed.get("hostaddr", "")).strip()
    if not _is_loopback(host) or (hostaddr and not _is_loopback(hostaddr)):
        raise _configuration_error("portable database target must use an explicit loopback address")

    port_text = str(parsed["port"])
    if "," in port_text:
        raise _configuration_error("portable database port must be a single valid port")
    try:
        port = int(port_text)
    except ValueError:
        raise _configuration_error("portable database port must be a single valid port") from None
    if not 1 <= port <= 65_535:
        raise _configuration_error("portable database port must be a single valid port")

    # 传递解析后的显式参数，并固定本机便携集群的传输选项，不把原 DSN
    # 交给日志或异常格式化链路。
    parameters = dict(parsed)
    parameters["port"] = str(port)
    parameters["hostaddr"] = hostaddr or (host if host.lower() != "localhost" else "127.0.0.1")
    parameters["sslmode"] = "disable"
    return DatabaseTarget(parameters=parameters)


def build_settings(
    *,
    mode: str,
    instance_id: str,
    program_root: str,
    data_root: str,
    port: int,
    environ: Mapping[str, str] | None = None,
) -> MaintenanceSettings:
    environment = os.environ if environ is None else environ
    if mode != "maintenance":
        raise _configuration_error("only maintenance mode is supported by this entry point")
    if not _INSTANCE_ID_PATTERN.fullmatch(instance_id):
        raise _configuration_error("instance-id must contain only letters, digits, dot, underscore, or hyphen")
    if not 1 <= port <= 65_535:
        raise _configuration_error("port must be between 1 and 65535")

    token = environment.get(TOKEN_ENVIRONMENT_VARIABLE, "")
    try:
        token_bytes = token.encode("ascii")
    except UnicodeEncodeError:
        raise _configuration_error("launcher token must be ASCII and at least 32 bytes") from None
    if len(token_bytes) < 32 or any(character.isspace() for character in token):
        raise _configuration_error("launcher token must be ASCII and at least 32 bytes")

    database_target = validate_database_target(
        environment.get(DATABASE_ENVIRONMENT_VARIABLE, ""),
        environ=environment,
    )
    return MaintenanceSettings(
        instance_id=instance_id,
        program_root=_absolute_root(program_root, "program-root"),
        data_root=_absolute_root(data_root, "data-root"),
        port=port,
        launcher_token=token,
        database_target=database_target,
    )


class PostgresReadinessProbe:
    """PostgreSQL 短连接、只读的 schema 就绪探测。"""

    def __init__(
        self,
        target: DatabaseTarget,
        *,
        connector: Callable[..., Any] = psycopg2.connect,
    ) -> None:
        self._target = target
        self._connector = connector

    def check(self) -> DatabaseProbeResult:
        connection: Any = None
        cursor: Any = None
        try:
            # 避免验证后才被注入的 libpq service 环境配置改变连接目标。
            if any(os.environ.get(name, "").strip() for name in _LIBPQ_SERVICE_ENVIRONMENT_VARIABLES):
                raise MaintenanceConfigurationError("libpq service configuration is not allowed")
            parameters = dict(self._target.parameters)
            parameters.update(
                connect_timeout=CONNECT_TIMEOUT_SECONDS,
                application_name="goofish-portable-maintenance",
                options=(
                    f"-c statement_timeout={STATEMENT_TIMEOUT_MILLISECONDS} "
                    "-c default_transaction_read_only=on"
                ),
            )
            connection = self._connector(**parameters)
            connection.set_session(readonly=True, autocommit=False)
            cursor = connection.cursor()
            cursor.execute("SELECT to_regclass(%s)", (_SCHEMA_TABLE_NAME,))
            table_row = cursor.fetchone()
            if not table_row or table_row[0] is None:
                return DatabaseProbeResult("available", "uninitialized", None, "schema_uninitialized")

            # LIMIT 2 既能判定“恰好一行”，也避免在损坏表上无界加载。
            cursor.execute("SELECT version FROM public.app_schema_version LIMIT 2")
            rows = cursor.fetchall()
            if not rows:
                return DatabaseProbeResult("available", "uninitialized", None, "schema_uninitialized")
            if len(rows) != 1 or len(rows[0]) != 1:
                return DatabaseProbeResult("available", "invalid", None, "schema_invalid")

            version = rows[0][0]
            if isinstance(version, bool) or not isinstance(version, int) or version <= 0:
                return DatabaseProbeResult("available", "invalid", None, "schema_invalid")
            if SUPPORTED_SCHEMA_MIN <= version <= SUPPORTED_SCHEMA_MAX:
                return DatabaseProbeResult("available", "compatible", version, None)
            return DatabaseProbeResult("available", "incompatible", version, "schema_incompatible")
        except Exception:
            logger.warning(
                "Portable database readiness probe failed",
                extra={"event": "portable_database_probe_failed"},
            )
            return DatabaseProbeResult("unavailable", "unknown", None, "database_unavailable")
        finally:
            if connection is not None:
                try:
                    connection.rollback()
                except Exception:
                    logger.warning(
                        "Portable database probe rollback failed",
                        extra={"event": "portable_database_probe_rollback_failed"},
                    )
            if cursor is not None:
                try:
                    cursor.close()
                except Exception:
                    logger.warning(
                        "Portable database probe cursor close failed",
                        extra={"event": "portable_database_probe_cursor_close_failed"},
                    )
            if connection is not None:
                try:
                    connection.close()
                except Exception:
                    logger.warning(
                        "Portable database probe connection close failed",
                        extra={"event": "portable_database_probe_connection_close_failed"},
                    )


def _authenticated(authorization: str | None, expected_token_digest: bytes) -> bool:
    supplied = authorization or ""
    token = supplied[7:] if supplied.startswith("Bearer ") else ""
    supplied_digest = hashlib.sha256(token.encode("utf-8")).digest()
    return hmac.compare_digest(supplied_digest, expected_token_digest)


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


def create_app(
    settings: MaintenanceSettings,
    *,
    probe: ReadinessProbe | None = None,
    clock: Callable[[], datetime] = _utc_now,
    request_shutdown: Callable[[], None] | None = None,
) -> FastAPI:
    """创建维护应用；可选控制回调只允许停止此维护进程。"""

    readiness_probe = probe or PostgresReadinessProbe(settings.database_target)
    expected_digest = hashlib.sha256(settings.launcher_token.encode("utf-8")).digest()
    app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)
    shutdown_lock = threading.Lock()
    shutdown_requested = False

    @app.post("/internal/shutdown")
    def shutdown(
        authorization: str | None = Header(default=None),
        x_goofish_instance_id: str | None = Header(default=None),
        origin: str | None = Header(default=None),
    ):
        nonlocal shutdown_requested
        if not _authenticated(authorization, expected_digest):
            raise HTTPException(status_code=401, detail="unauthorized")
        # This endpoint is native Launcher IPC, not a browser business API.
        if origin is not None or x_goofish_instance_id != settings.instance_id:
            raise HTTPException(status_code=403, detail="control context mismatch")
        if request_shutdown is None:
            raise HTTPException(status_code=503, detail="shutdown control unavailable")
        with shutdown_lock:
            if not shutdown_requested:
                try:
                    request_shutdown()
                except Exception:
                    logger.error("Portable maintenance shutdown request failed",
                                 extra={"event": "portable_shutdown_failed"})
                    raise HTTPException(status_code=503, detail="shutdown request failed") from None
                shutdown_requested = True
        # Accepted is not evidence the process has exited. The owner must wait.
        return JSONResponse(status_code=202, content={"status": "shutdown_requested"})

    @app.get("/health")
    def health() -> dict[str, str]:
        return {"status": "alive"}

    @app.get("/internal/ready")
    def ready(authorization: str | None = Header(default=None)):
        if not _authenticated(authorization, expected_digest):
            raise HTTPException(status_code=401, detail="unauthorized")

        try:
            result = readiness_probe.check()
        except Exception:
            logger.warning(
                "Portable readiness probe implementation failed",
                extra={"event": "portable_readiness_probe_failed"},
            )
            result = DatabaseProbeResult("unavailable", "unknown", None, "database_unavailable")

        observed_at = clock().astimezone(timezone.utc).isoformat().replace("+00:00", "Z")
        body = {
            "protocol_version": PROTOCOL_VERSION,
            "instance_id": settings.instance_id,
            "app_version": settings.app_version,
            "observed_at": observed_at,
            "mode": settings.mode,
            "ready": result.ready,
            "database": {"status": result.database_status},
            "schema": {
                "status": result.schema_status,
                "version": result.schema_version,
                "supported_min": SUPPORTED_SCHEMA_MIN,
                "supported_max": SUPPORTED_SCHEMA_MAX,
            },
            "failure_reason": result.failure_reason,
        }
        return JSONResponse(status_code=200 if result.ready else 503, content=body)

    return app


def _argument_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Run the isolated portable maintenance server")
    parser.add_argument("--mode", required=True, choices=("maintenance",))
    parser.add_argument("--instance-id", required=True)
    parser.add_argument("--program-root", required=True)
    parser.add_argument("--data-root", required=True)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    return parser


def run(
    argv: Sequence[str] | None = None,
    *,
    environ: Mapping[str, str] | None = None,
    server_runner: Callable[..., Any] | None = None,
) -> int:
    args = _argument_parser().parse_args(argv)
    settings = build_settings(
        mode=args.mode,
        instance_id=args.instance_id,
        program_root=args.program_root,
        data_root=args.data_root,
        port=args.port,
        environ=environ,
    )
    try:
        if server_runner is not None:
            server_runner(create_app(settings), host=LOOPBACK_HOST, port=settings.port)
        else:
            server = None

            def request_shutdown() -> None:
                if server is None:
                    raise RuntimeError("maintenance server is not available")
                server.should_exit = True

            app = create_app(settings, request_shutdown=request_shutdown)
            server = uvicorn.Server(uvicorn.Config(
                app, host=LOOPBACK_HOST, port=settings.port, access_log=False,
            ))
            server.run()
    except Exception:
        logger.error(
            "Portable maintenance server failed to start",
            extra={"event": "portable_server_start_failed"},
        )
        raise MaintenanceServerError("portable maintenance server failed to start") from None
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    try:
        return run(argv)
    except MaintenanceConfigurationError as exc:
        print(f"Portable maintenance configuration error: {exc}", file=os.sys.stderr)
        return 2
    except MaintenanceServerError as exc:
        print(f"Portable maintenance server error: {exc}", file=os.sys.stderr)
        return 3


__all__ = [
    "DATABASE_ENVIRONMENT_VARIABLE",
    "PROTOCOL_VERSION",
    "SUPPORTED_SCHEMA_MAX",
    "SUPPORTED_SCHEMA_MIN",
    "TOKEN_ENVIRONMENT_VARIABLE",
    "DatabaseProbeResult",
    "DatabaseTarget",
    "MaintenanceConfigurationError",
    "MaintenanceServerError",
    "MaintenanceSettings",
    "PostgresReadinessProbe",
    "build_settings",
    "create_app",
    "main",
    "run",
    "validate_database_target",
]
