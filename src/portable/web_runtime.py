"""Bounded normal-mode portable Web runtime and first-admin gate.

The maintenance service remains a separate, read-only entry point.  This
module is the only supported path that may mount the existing business Web
application in portable normal mode.
"""

from __future__ import annotations

import asyncio
import hashlib
import hmac
import json
import logging
import os
import re
import time
from collections import deque
from contextlib import asynccontextmanager
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Awaitable, Callable, Mapping, Protocol

from fastapi import FastAPI, Header, HTTPException, Request
from fastapi.responses import HTMLResponse, JSONResponse, RedirectResponse, Response
from sqlalchemy import create_engine, text
from sqlalchemy.engine import Engine, URL
from sqlalchemy.pool import NullPool

from src.portable.context import (
    PORTABLE_DATABASE_URL_ENVIRONMENT_VARIABLE,
    PortableConfigurationError,
    portable_mode,
    portable_paths,
)
from src.portable.maintenance import (
    DatabaseProbeResult,
    DatabaseTarget,
    PostgresReadinessProbe,
    validate_database_target,
)
from src.version import VERSION


PROTOCOL_VERSION = 1
LOOPBACK_HOST = "127.0.0.1"
LAUNCHER_TOKEN_ENVIRONMENT_VARIABLE = "GOOFISH_LAUNCHER_TOKEN"
SETUP_TOKEN_ENVIRONMENT_VARIABLE = "GOOFISH_PORTABLE_SETUP_TOKEN"
PROBE_DATABASE_ENVIRONMENT_VARIABLE = "GOOFISH_PORTABLE_PROBE_DATABASE_URL"
SETUP_MAX_BODY_BYTES = 4_096
SETUP_BODY_TIMEOUT_SECONDS = 5.0
SETUP_ATTEMPT_LIMIT = 5
SETUP_ATTEMPT_WINDOW_SECONDS = 60.0
DEFAULT_SHUTDOWN_DRAIN_TIMEOUT_SECONDS = 30.0

_INSTANCE_ID_PATTERN = re.compile(r"^[A-Za-z0-9._-]{1,128}$")
logger = logging.getLogger(__name__)


class PortableWebConfigurationError(ValueError):
    """A portable normal-mode startup setting is missing or unsafe."""


class PortableWebStartupError(RuntimeError):
    """The normal-mode application was not safe to expose."""


class ReadinessProbe(Protocol):
    def check(self) -> DatabaseProbeResult:
        """Return the bounded database/schema readiness state."""


@dataclass(frozen=True)
class PortableWebSettings:
    instance_id: str
    program_root: Path
    data_root: Path
    cache_root: Path
    port: int
    launcher_token: str = field(repr=False)
    setup_token: str = field(repr=False)
    application_target: DatabaseTarget = field(repr=False)
    probe_target: DatabaseTarget = field(repr=False)
    mode: str = "normal"
    app_version: str = VERSION

    @property
    def loopback_origin(self) -> str:
        return f"http://{LOOPBACK_HOST}:{self.port}"


@dataclass
class _RuntimeGate:
    settings: PortableWebSettings
    setup_required: bool = True
    normal_ready: bool = False
    setup_lock: asyncio.Lock = field(default_factory=asyncio.Lock, repr=False)
    shutdown: Any = field(default=None, repr=False)


class _ShutdownCoordinator:
    """Observe a bounded graceful drain without task termination side effects."""

    def __init__(
        self,
        *,
        timeout_seconds: float,
        processes_active: Callable[[], bool],
        pause_scheduler: Callable[[], Awaitable[bool]],
        resume_scheduler: Callable[[bool], Awaitable[None]],
        dispose_storage: Callable[[], None],
        request_server_exit: Callable[[], None] | None,
    ) -> None:
        if timeout_seconds <= 0:
            raise ValueError("shutdown drain timeout must be positive")
        self.timeout_seconds = timeout_seconds
        self._processes_active = processes_active
        self._pause_scheduler = pause_scheduler
        self._resume_scheduler = resume_scheduler
        self._dispose_storage = dispose_storage
        self._request_server_exit = request_server_exit
        self.phase = "Idle"
        self.detail: str | None = None
        self.active_business_requests = 0
        self._lock = asyncio.Lock()
        self._task: asyncio.Task[None] | None = None
        self._scheduler_was_running = False

    @staticmethod
    def _is_safe_path(path: str) -> bool:
        return path == "/health" or path.startswith("/internal/")

    def admit_and_track(self, path: str) -> bool | None:
        if self._is_safe_path(path):
            return False
        if self.phase in {"Draining", "Cancelling", "Committing", "Stopped"}:
            return None
        self.active_business_requests += 1
        return True

    def request_finished(self, counted: bool) -> None:
        if counted and self.active_business_requests > 0:
            self.active_business_requests -= 1

    async def begin(self) -> dict[str, Any]:
        async with self._lock:
            if self.phase in {"Stopped", "Committing", "Draining", "Cancelling"}:
                return self.snapshot()
            self.phase = "Draining"
            self.detail = None
            try:
                self._scheduler_was_running = await self._pause_scheduler()
            except Exception:
                logger.error("Portable Web scheduler pause failed", extra={"event": "portable_shutdown_pause_failed"})
                self.phase = "TimedOut"
                self.detail = "shutdown_pause_failed"
                self._scheduler_was_running = False
                return self.snapshot()
            self._task = asyncio.create_task(self._drain(), name="portable-web-shutdown-drain")
            return self.snapshot()

    async def cancel(self) -> dict[str, Any]:
        task: asyncio.Task[None] | None = None
        async with self._lock:
            if self.phase != "Draining" or self._task is None:
                return self.snapshot()
            task = self._task
            self.phase = "Cancelling"
            task.cancel()
            self._task = None
        try:
            await task
        except asyncio.CancelledError:
            pass
        async with self._lock:
            restored = await self._resume_safely()
            self.phase = "Idle" if restored else "TimedOut"
            self.detail = "shutdown_cancelled" if restored else "shutdown_resume_failed"
            return self.snapshot()

    async def _drain(self) -> None:
        deadline = time.monotonic() + self.timeout_seconds
        try:
            while time.monotonic() < deadline:
                if self.active_business_requests == 0 and not self._safe_processes_active():
                    try:
                        async with self._lock:
                            if self.phase != "Draining":
                                return
                            self.phase = "Committing"
                        self._dispose_storage()
                        if self._request_server_exit is None:
                            raise RuntimeError("server exit callback is unavailable")
                        self._request_server_exit()
                    except Exception:
                        logger.error(
                            "Portable Web shutdown completion failed",
                            extra={"event": "portable_shutdown_completion_failed"},
                        )
                        await self._restore_after_failure("shutdown_callback_failed")
                        return
                    async with self._lock:
                        self.phase = "Stopped"
                        self.detail = None
                        self._task = None
                    return
                await asyncio.sleep(0.05)
            await self._restore_after_failure("shutdown_timed_out")
        except asyncio.CancelledError:
            raise
        except Exception:
            logger.error("Portable Web shutdown drain failed", extra={"event": "portable_shutdown_drain_failed"})
            await self._restore_after_failure("shutdown_drain_failed")

    async def _restore_after_failure(self, detail: str) -> None:
        async with self._lock:
            restored = await self._resume_safely()
            self.phase = "TimedOut"
            self.detail = detail if restored else "shutdown_resume_failed"
            self._task = None

    async def _resume_safely(self) -> bool:
        try:
            await self._resume_scheduler(self._scheduler_was_running)
            return True
        except Exception:
            logger.error("Portable Web scheduler restore failed", extra={"event": "portable_shutdown_resume_failed"})
            return False

    async def close(self) -> None:
        """Cancel an uncommitted drain during ASGI lifespan teardown."""

        if self.phase == "Draining":
            await self.cancel()

    def snapshot(self) -> dict[str, Any]:
        return {
            "state": self.phase,
            "detail": self.detail,
            "active_business_requests": self.active_business_requests,
            "workers_active": self._safe_processes_active(),
        }

    def _safe_processes_active(self) -> bool:
        try:
            return bool(self._processes_active())
        except Exception:
            logger.error("Portable Web worker state check failed", extra={"event": "portable_shutdown_worker_check_failed"})
            return True


class _DrainRequestTrackingMiddleware:
    """ASGI-level tracking includes response streaming and BackgroundTasks."""

    def __init__(self, app, *, coordinator: _ShutdownCoordinator) -> None:
        self.app = app
        self.coordinator = coordinator

    async def __call__(self, scope, receive, send) -> None:
        if scope["type"] != "http":
            await self.app(scope, receive, send)
            return
        counted = self.coordinator.admit_and_track(scope.get("path", ""))
        if counted is None:
            response = JSONResponse(
                status_code=503,
                content={"detail": "portable service is draining"},
                headers={"Retry-After": "1"},
            )
            await response(scope, receive, send)
            return
        try:
            await self.app(scope, receive, send)
        finally:
            self.coordinator.request_finished(counted)


_runtime_gate: _RuntimeGate | None = None
_portable_background_tasks: set[asyncio.Task[Any]] = set()


def register_portable_background_task(task: asyncio.Task[Any]) -> asyncio.Task[Any]:
    """Track an already-scheduled worker monitor or scheduler coroutine.

    The tracker changes no business behavior and never cancels the task; it
    only prevents graceful portable shutdown from racing its final cleanup.
    """

    _portable_background_tasks.add(task)
    task.add_done_callback(_portable_background_tasks.discard)
    return task


def portable_background_tasks_active() -> bool:
    """Return whether registered non-ASGI worker cleanup is still running."""

    return any(not task.done() for task in tuple(_portable_background_tasks))


def _validate_ascii_token(value: str, name: str) -> str:
    try:
        encoded = value.encode("ascii")
    except (AttributeError, UnicodeEncodeError):
        raise PortableWebConfigurationError(f"{name} must be printable ASCII and at least 32 bytes") from None
    if len(encoded) < 32 or any(byte < 33 or byte > 126 for byte in encoded):
        raise PortableWebConfigurationError(f"{name} must be printable ASCII and at least 32 bytes")
    return value


def build_settings(
    *,
    mode: str,
    instance_id: str,
    port: int,
    environ: Mapping[str, str] | None = None,
) -> PortableWebSettings:
    """Validate explicit normal-mode settings without loading dotenv files."""

    environment = os.environ if environ is None else environ
    if mode != "normal":
        raise PortableWebConfigurationError("only normal mode is supported by this entry point")
    if not portable_mode(environ=environment):
        raise PortableWebConfigurationError("portable mode must be explicitly enabled")
    if not _INSTANCE_ID_PATTERN.fullmatch(instance_id):
        raise PortableWebConfigurationError("instance-id is invalid")
    if not 1 <= port <= 65_535:
        raise PortableWebConfigurationError("port must be between 1 and 65535")

    try:
        paths = portable_paths(environ=environment)
        application_url = str(environment.get(PORTABLE_DATABASE_URL_ENVIRONMENT_VARIABLE, ""))
        application_target = validate_database_target(application_url, environ=environment)
        probe_target = validate_database_target(
            str(environment.get(PROBE_DATABASE_ENVIRONMENT_VARIABLE, "")),
            environ=environment,
        )
    except (PortableConfigurationError, ValueError) as exc:
        raise PortableWebConfigurationError("portable roots or database configuration are invalid") from exc

    # The normal application uses the canonical app-role DSN.  Its readiness
    # probe must use the independently provisioned least-privilege role.
    if application_target.parameters.get("user") == probe_target.parameters.get("user"):
        raise PortableWebConfigurationError("application and probe database roles must be distinct")
    if any(
        application_target.parameters.get(name) != probe_target.parameters.get(name)
        for name in ("hostaddr", "port", "dbname")
    ):
        raise PortableWebConfigurationError("application and probe database targets must match")

    launcher_token = _validate_ascii_token(
        str(environment.get(LAUNCHER_TOKEN_ENVIRONMENT_VARIABLE, "")),
        LAUNCHER_TOKEN_ENVIRONMENT_VARIABLE,
    )
    setup_token = (
        _validate_ascii_token(
            str(environment.get(SETUP_TOKEN_ENVIRONMENT_VARIABLE, "")),
            SETUP_TOKEN_ENVIRONMENT_VARIABLE,
        )
        if environment.get(SETUP_TOKEN_ENVIRONMENT_VARIABLE, "")
        else ""
    )
    if setup_token and hmac.compare_digest(launcher_token, setup_token):
        raise PortableWebConfigurationError("launcher and setup tokens must be distinct")

    return PortableWebSettings(
        instance_id=instance_id,
        program_root=paths.program_root,
        data_root=paths.data_root,
        cache_root=paths.cache_root,
        port=port,
        launcher_token=launcher_token,
        setup_token=setup_token,
        application_target=application_target,
        probe_target=probe_target,
    )


def portable_web_runtime_active() -> bool:
    """Whether normal portable Web hosting installed this process gate."""

    return _runtime_gate is not None and _runtime_gate.normal_ready


def portable_scheduler_start_allowed() -> bool:
    """Schedulers can start only after normal readiness and first-admin setup."""

    return bool(_runtime_gate and _runtime_gate.normal_ready and not _runtime_gate.setup_required
                and (_runtime_gate.shutdown is None or _runtime_gate.shutdown.phase in {"Idle", "TimedOut"}))


def _install_runtime_gate(settings: PortableWebSettings, *, setup_required: bool) -> _RuntimeGate:
    global _runtime_gate
    _runtime_gate = _RuntimeGate(settings=settings, setup_required=setup_required, normal_ready=True)
    return _runtime_gate


def _application_engine(settings: PortableWebSettings) -> Engine:
    """Create an app-role engine without serializing its DSN into diagnostics."""

    parameters = settings.application_target.parameters
    database_url = URL.create(
        "postgresql+psycopg2",
        username=parameters["user"],
        password=parameters["password"],
        host=parameters["host"],
        port=int(parameters["port"]),
        database=parameters["dbname"],
    )
    return create_engine(
        database_url,
        poolclass=NullPool,
        connect_args={
            "hostaddr": parameters["hostaddr"],
            "sslmode": "disable",
            "connect_timeout": 5,
            "application_name": "goofish-portable-web",
            "options": "-c statement_timeout=30000 -c lock_timeout=30000",
        },
    )


def _users_exist(settings: PortableWebSettings) -> bool:
    engine: Engine | None = None
    try:
        engine = _application_engine(settings)
        with engine.connect() as connection:
            return connection.execute(text('SELECT 1 FROM public.users LIMIT 1')).first() is not None
    except Exception:
        logger.error("Portable first-admin state check failed", extra={"event": "portable_setup_state_check_failed"})
        raise PortableWebStartupError("unable to determine first-admin state") from None
    finally:
        if engine is not None:
            engine.dispose()


def _create_first_admin(settings: PortableWebSettings, username: str, password: str) -> str:
    # These business dependencies are intentionally delayed until the explicit
    # setup request.  No credentials are logged or persisted by this module.
    from src.portable.schema import create_first_admin
    from src.storage.utils import hash_password

    engine: Engine | None = None
    try:
        engine = _application_engine(settings)
        return create_first_admin(
            engine,
            username=username,
            password=password,
            password_hasher=hash_password,
        )
    finally:
        if engine is not None:
            engine.dispose()


async def _read_limited_json(request: Request) -> dict[str, Any]:
    content_type = request.headers.get("content-type", "")
    if content_type.split(";", 1)[0].strip().lower() != "application/json":
        raise HTTPException(status_code=415, detail="setup requires application/json")

    declared_length = request.headers.get("content-length")
    try:
        if declared_length is not None and int(declared_length) > SETUP_MAX_BODY_BYTES:
            raise HTTPException(status_code=413, detail="setup request is too large")
    except ValueError:
        raise HTTPException(status_code=400, detail="invalid setup request") from None

    async def collect() -> bytes:
        size = 0
        chunks: list[bytes] = []
        async for chunk in request.stream():
            size += len(chunk)
            if size > SETUP_MAX_BODY_BYTES:
                raise HTTPException(status_code=413, detail="setup request is too large")
            chunks.append(chunk)
        return b"".join(chunks)

    try:
        payload = await asyncio.wait_for(collect(), timeout=SETUP_BODY_TIMEOUT_SECONDS)
    except HTTPException:
        raise
    except TimeoutError:
        raise HTTPException(status_code=408, detail="setup request timed out") from None
    except Exception:
        logger.warning("Portable setup request body read failed", extra={"event": "portable_setup_body_read_failed"})
        raise HTTPException(status_code=400, detail="invalid setup request") from None
    try:
        value = json.loads(payload.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError):
        raise HTTPException(status_code=400, detail="invalid setup JSON") from None
    if not isinstance(value, dict) or set(value) != {"username", "password", "setup_token"}:
        raise HTTPException(status_code=422, detail="setup request fields are invalid")
    if not all(isinstance(value[name], str) for name in ("username", "password", "setup_token")):
        raise HTTPException(status_code=422, detail="setup request fields are invalid")
    return value


class _SetupRateLimiter:
    def __init__(self) -> None:
        self._attempts: deque[float] = deque()
        self._lock = asyncio.Lock()

    async def permit(self) -> bool:
        now = time.monotonic()
        async with self._lock:
            while self._attempts and now - self._attempts[0] >= SETUP_ATTEMPT_WINDOW_SECONDS:
                self._attempts.popleft()
            if len(self._attempts) >= SETUP_ATTEMPT_LIMIT:
                return False
            self._attempts.append(now)
            return True


def _launcher_authenticated(authorization: str | None, token: str) -> bool:
    supplied = authorization[7:] if authorization and authorization.startswith("Bearer ") else ""
    return hmac.compare_digest(
        hashlib.sha256(supplied.encode("utf-8")).digest(),
        hashlib.sha256(token.encode("utf-8")).digest(),
    )


def _setup_authenticated(supplied: str, expected: str) -> bool:
    """Compare token digests so arbitrary JSON Unicode cannot raise a 500."""

    return hmac.compare_digest(
        hashlib.sha256(supplied.encode("utf-8")).digest(),
        hashlib.sha256(expected.encode("utf-8")).digest(),
    )


def _setup_request_origin_is_valid(request: Request, settings: PortableWebSettings) -> bool:
    return (
        request.headers.get("host", "").lower() == f"{LOOPBACK_HOST}:{settings.port}"
        and request.headers.get("origin") == settings.loopback_origin
    )


def _host_is_valid(request: Request, settings: PortableWebSettings) -> bool:
    return request.headers.get("host", "").lower() == f"{LOOPBACK_HOST}:{settings.port}"


_SETUP_RESPONSE_HEADERS = {
    "Cache-Control": "no-store",
    "Content-Security-Policy": (
        "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; "
        "form-action 'self'; base-uri 'none'; frame-ancestors 'none'"
    ),
    "X-Content-Type-Options": "nosniff",
    "Referrer-Policy": "no-referrer",
}


def _setup_asset(settings: PortableWebSettings, relative_path: tuple[str, ...], media_type: str) -> Response:
    """Serve only the fixed first-admin page assets from the app component."""

    asset_path = settings.program_root.joinpath(*relative_path)
    try:
        content = asset_path.read_bytes()
    except OSError:
        logger.error("Portable setup asset is unavailable", extra={"event": "portable_setup_asset_unavailable"})
        raise HTTPException(status_code=500, detail="portable setup asset is unavailable") from None
    return Response(content=content, media_type=media_type, headers=_SETUP_RESPONSE_HEADERS)


def _assert_business_config_matches(settings: PortableWebSettings) -> None:
    """Reject a normal host whose frozen config differs from its CLI settings."""

    from src import config

    if not config.PORTABLE_MODE:
        raise PortableWebStartupError("portable business configuration was not enabled")
    paths = getattr(config, "_portable_paths", None)
    controls = getattr(config, "_PORTABLE_CONTROLLED_ENVIRONMENT", {})
    # validate_database_target doesn't retain a raw DSN.  Compare parsed
    # targets, but keep all rejection messages free of secret-bearing values.
    try:
        configured_target = validate_database_target(
            str(controls.get(PORTABLE_DATABASE_URL_ENVIRONMENT_VARIABLE, "")),
            environ=controls,
        )
    except Exception:
        configured_target = None
    if (
        paths is None
        or paths.program_root != settings.program_root
        or paths.data_root != settings.data_root
        or paths.cache_root != settings.cache_root
        or configured_target is None
        or configured_target.parameters != settings.application_target.parameters
    ):
        raise PortableWebStartupError("portable business configuration does not match runtime settings")


def create_application(
    settings: PortableWebSettings,
    *,
    probe: ReadinessProbe | None = None,
    users_exist: Callable[[PortableWebSettings], bool] = _users_exist,
    create_admin: Callable[[PortableWebSettings, str, str], str] = _create_first_admin,
    business_app_factory: Callable[[], FastAPI] | None = None,
    shutdown_timeout_seconds: float = DEFAULT_SHUTDOWN_DRAIN_TIMEOUT_SECONDS,
    request_server_exit: Callable[[], None] | None = None,
    processes_active: Callable[[], bool] | None = None,
    pause_scheduler: Callable[[], Awaitable[bool]] | None = None,
    resume_scheduler: Callable[[bool], Awaitable[None]] | None = None,
    dispose_storage: Callable[[], None] | None = None,
) -> FastAPI:
    """Create the normal portable outer application and mount the business app.

    The outer layer owns readiness and setup gating.  It has no legacy
    lifespan and never invokes storage auto-initialization or schema writes.
    """

    global _runtime_gate

    readiness_probe = probe or PostgresReadinessProbe(settings.probe_target)
    try:
        readiness = readiness_probe.check()
    except Exception:
        logger.error("Portable Web readiness probe failed", extra={"event": "portable_web_probe_failed"})
        raise PortableWebStartupError("portable schema readiness could not be verified") from None
    if not readiness.ready:
        raise PortableWebStartupError("portable schema is not ready for normal mode")

    setup_required = not users_exist(settings)
    if setup_required and not settings.setup_token:
        raise PortableWebStartupError("portable first-admin setup token is required")
    gate = _install_runtime_gate(settings, setup_required=setup_required)
    business_after_setup: Callable[[], Awaitable[None]] | None = None
    try:
        if business_app_factory is None:
            from src.web import main as business_main
            _assert_business_config_matches(settings)
            business_app = business_main.app
            business_after_setup = business_main.portable_runtime_startup
            processes_active = processes_active or business_main.portable_runtime_managed_processes_active
            pause_scheduler = pause_scheduler or business_main.portable_runtime_pause_scheduler
            resume_scheduler = resume_scheduler or business_main.portable_runtime_resume_scheduler
            dispose_storage = dispose_storage or business_main.portable_runtime_dispose_storage_engine
        else:
            business_app = business_app_factory()
    except Exception:
        _runtime_gate = None
        logger.error("Portable business Web application could not be loaded", extra={"event": "portable_web_load_failed"})
        raise PortableWebStartupError("portable business Web application could not be loaded") from None

    if processes_active is None:
        processes_active = lambda: False
    if pause_scheduler is None:
        async def pause_scheduler() -> bool:
            return False
    if resume_scheduler is None:
        async def resume_scheduler(_was_running: bool) -> None:
            return None
    if dispose_storage is None:
        dispose_storage = lambda: None

    coordinator = _ShutdownCoordinator(
        timeout_seconds=shutdown_timeout_seconds,
        processes_active=processes_active,
        pause_scheduler=pause_scheduler,
        resume_scheduler=resume_scheduler,
        dispose_storage=dispose_storage,
        request_server_exit=request_server_exit,
    )
    gate.shutdown = coordinator

    @asynccontextmanager
    async def normal_lifespan(_application: FastAPI):
        global _runtime_gate
        try:
            # Starlette does not start a mounted sub-application lifespan.
            # Explicitly running it invokes main's portable-only branch, never
            # its legacy migration/reset/terminate behavior.
            async with business_app.router.lifespan_context(business_app):
                yield
        finally:
            await coordinator.close()
            if _runtime_gate is gate:
                _runtime_gate = None

    outer = FastAPI(docs_url=None, redoc_url=None, openapi_url=None, lifespan=normal_lifespan)
    outer.add_middleware(_DrainRequestTrackingMiddleware, coordinator=coordinator)
    limiter = _SetupRateLimiter()

    @outer.middleware("http")
    async def setup_gate(request: Request, call_next):
        if not _host_is_valid(request, settings):
            return JSONResponse(status_code=400, content={"detail": "host is not allowed"})
        if (
            request.method in {"POST", "PUT", "PATCH", "DELETE"}
            and request.url.path not in {"/setup"}
            and not request.url.path.startswith("/internal/")
            and not _setup_request_origin_is_valid(request, settings)
        ):
            return JSONResponse(status_code=403, content={"detail": "origin is not allowed"})
        if gate.setup_required and request.url.path not in {
            "/health", "/internal/ready", "/internal/shutdown",
            "/internal/cancel-shutdown", "/internal/shutdown-state", "/setup",
        }:
            if request.url.path == "/":
                return RedirectResponse(url="/setup", status_code=303, headers=_SETUP_RESPONSE_HEADERS)
            if request.url.path in {"/setup-assets/setup.css", "/setup-assets/setup.js"}:
                return await call_next(request)
            return JSONResponse(
                status_code=503,
                content={"detail": "first-admin setup is required"},
                headers={"Retry-After": "1"},
            )
        return await call_next(request)

    @outer.get("/health")
    async def health() -> dict[str, str]:
        return {"status": "alive"}

    @outer.get("/setup", response_class=HTMLResponse)
    async def setup_page():
        if not gate.setup_required:
            return RedirectResponse(url="/login", status_code=303, headers=_SETUP_RESPONSE_HEADERS)
        return _setup_asset(settings, ("templates", "portable_setup.html"), "text/html; charset=utf-8")

    @outer.get("/setup-assets/setup.css")
    async def setup_css():
        return _setup_asset(settings, ("static", "portable", "setup.css"), "text/css; charset=utf-8")

    @outer.get("/setup-assets/setup.js")
    async def setup_js():
        return _setup_asset(settings, ("static", "portable", "setup.js"), "application/javascript; charset=utf-8")

    @outer.get("/internal/ready")
    async def ready(
        authorization: str | None = Header(default=None),
        x_goofish_instance_id: str | None = Header(default=None),
        origin: str | None = Header(default=None),
    ):
        if not _launcher_authenticated(authorization, settings.launcher_token):
            raise HTTPException(status_code=401, detail="unauthorized")
        if x_goofish_instance_id != settings.instance_id or origin is not None:
            raise HTTPException(status_code=403, detail="control context mismatch")
        try:
            result = await asyncio.to_thread(readiness_probe.check)
        except Exception:
            logger.warning("Portable Web readiness probe failed", extra={"event": "portable_web_ready_probe_failed"})
            result = DatabaseProbeResult("unavailable", "unknown", None, "database_unavailable")
        body = {
            "protocol_version": PROTOCOL_VERSION,
            "instance_id": settings.instance_id,
            "app_version": settings.app_version,
            "mode": "normal",
            "ready": result.ready,
            "setup_required": gate.setup_required,
            "database": {"status": result.database_status},
            "schema": {
                "status": result.schema_status,
                "version": result.schema_version,
            },
            "failure_reason": result.failure_reason,
        }
        return JSONResponse(status_code=200 if result.ready else 503, content=body)

    def _control_authorized(
        authorization: str | None,
        instance_id: str | None,
        origin: str | None,
    ) -> None:
        if not _launcher_authenticated(authorization, settings.launcher_token):
            raise HTTPException(status_code=401, detail="unauthorized")
        if instance_id != settings.instance_id or origin is not None:
            raise HTTPException(status_code=403, detail="control context mismatch")

    @outer.post("/internal/shutdown")
    async def shutdown(
        authorization: str | None = Header(default=None),
        x_goofish_instance_id: str | None = Header(default=None),
        origin: str | None = Header(default=None),
    ):
        _control_authorized(authorization, x_goofish_instance_id, origin)
        snapshot = await coordinator.begin()
        return JSONResponse(status_code=202, content=snapshot)

    @outer.post("/internal/cancel-shutdown")
    async def cancel_shutdown(
        authorization: str | None = Header(default=None),
        x_goofish_instance_id: str | None = Header(default=None),
        origin: str | None = Header(default=None),
    ):
        _control_authorized(authorization, x_goofish_instance_id, origin)
        return JSONResponse(status_code=200, content=await coordinator.cancel())

    @outer.get("/internal/shutdown-state")
    async def shutdown_state(
        authorization: str | None = Header(default=None),
        x_goofish_instance_id: str | None = Header(default=None),
        origin: str | None = Header(default=None),
    ):
        _control_authorized(authorization, x_goofish_instance_id, origin)
        return coordinator.snapshot()

    @outer.post("/setup")
    async def setup(request: Request):
        if not _setup_request_origin_is_valid(request, settings):
            raise HTTPException(status_code=403, detail="setup origin is not allowed")
        if not gate.setup_required:
            raise HTTPException(status_code=409, detail="first-admin setup is already complete")
        if not await limiter.permit():
            raise HTTPException(status_code=429, detail="setup attempt limit exceeded", headers={"Retry-After": "60"})
        payload = await _read_limited_json(request)
        if not _setup_authenticated(payload["setup_token"], settings.setup_token):
            raise HTTPException(status_code=401, detail="unauthorized")
        async with gate.setup_lock:
            if not gate.setup_required:
                raise HTTPException(status_code=409, detail="first-admin setup is already complete")
            try:
                # Another process may have committed first-admin setup after
                # this host started.  Recheck before any second transaction.
                if await asyncio.to_thread(users_exist, settings):
                    gate.setup_required = False
                    raise HTTPException(status_code=409, detail="first-admin setup is already complete")
                await asyncio.to_thread(create_admin, settings, payload["username"], payload["password"])
                persisted = await asyncio.to_thread(users_exist, settings)
                if not persisted:
                    raise PortableWebStartupError("first-admin state did not persist")
            except HTTPException:
                raise
            except PortableWebStartupError:
                logger.error("Portable first-admin state did not persist", extra={"event": "portable_setup_state_not_persisted"})
                raise HTTPException(status_code=503, detail="first-admin setup did not complete") from None
            except Exception:
                logger.warning("Portable first-admin setup failed", extra={"event": "portable_setup_failed"})
                raise HTTPException(status_code=422, detail="first-admin setup failed") from None
            gate.setup_required = False
            if business_after_setup is not None:
                try:
                    await business_after_setup()
                except Exception:
                    # The first-admin transaction already committed.  Report
                    # that fact accurately instead of making callers repeat
                    # an irreversible setup request because scheduler loading
                    # failed after it.
                    logger.error(
                        "Portable scheduler start after first-admin setup failed",
                        extra={"event": "portable_setup_scheduler_start_failed"},
                    )
        return JSONResponse(status_code=201, content={"status": "first_admin_created"})

    from src.portable.launcher_pairing import register_internal_routes
    register_internal_routes(outer, settings)
    outer.mount("/", business_app)
    return outer


__all__ = [
    "LAUNCHER_TOKEN_ENVIRONMENT_VARIABLE",
    "PROBE_DATABASE_ENVIRONMENT_VARIABLE",
    "PROTOCOL_VERSION",
    "PortableWebConfigurationError",
    "PortableWebSettings",
    "PortableWebStartupError",
    "SETUP_TOKEN_ENVIRONMENT_VARIABLE",
    "build_settings",
    "create_application",
    "portable_scheduler_start_allowed",
    "portable_web_runtime_active",
]
