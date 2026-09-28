"""Isolated unit tests for the bounded portable normal Web runtime."""

from __future__ import annotations

import tempfile
import os
import subprocess
import sys
import textwrap
import threading
import time
import unittest
from unittest.mock import patch
from pathlib import Path

from fastapi import BackgroundTasks, FastAPI, HTTPException
from fastapi.testclient import TestClient

from src.portable.maintenance import DatabaseProbeResult, validate_database_target
from src.portable.web_runtime import (
    LAUNCHER_TOKEN_ENVIRONMENT_VARIABLE,
    PROBE_DATABASE_ENVIRONMENT_VARIABLE,
    SETUP_TOKEN_ENVIRONMENT_VARIABLE,
    PortableWebStartupError,
    PortableWebSettings,
    build_settings,
    create_application,
    portable_scheduler_start_allowed,
    _SetupBrowserGrants,
)


_TOKEN = "L" * 32
_SETUP_TOKEN = "S" * 32
_DSN = "postgresql://app_role:app-secret@127.0.0.1:55432/goofish"
_PROBE_DSN = "postgresql://probe_role:probe-secret@127.0.0.1:55432/goofish"
_REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
_TEST_TEMP_ROOT = _REPOSITORY_ROOT / ".tmp" / "tests" / "portable-web-runtime"


class _CompatibleProbe:
    def check(self) -> DatabaseProbeResult:
        return DatabaseProbeResult("available", "compatible", 1, None)


class _UninitializedProbe:
    def check(self) -> DatabaseProbeResult:
        return DatabaseProbeResult("available", "uninitialized", None, "schema_uninitialized")


class PortableWebRuntimeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        _TEST_TEMP_ROOT.mkdir(parents=True, exist_ok=True)

    @classmethod
    def tearDownClass(cls) -> None:
        try:
            _TEST_TEMP_ROOT.rmdir()
        except OSError:
            pass

    @staticmethod
    def temporary_directory():
        return tempfile.TemporaryDirectory(dir=_TEST_TEMP_ROOT)

    def _settings(self, root: Path, *, setup_token: str = _SETUP_TOKEN) -> PortableWebSettings:
        program_root = root / "app"
        (program_root / "templates").mkdir(parents=True, exist_ok=True)
        (program_root / "static" / "portable").mkdir(parents=True, exist_ok=True)
        for template in ("login.html", "portable_setup.html"):
            (program_root / "templates" / template).write_bytes((_REPOSITORY_ROOT / "templates" / template).read_bytes())
        (program_root / "static" / "portable" / "setup.css").write_text("", encoding="utf-8")
        (program_root / "static" / "portable" / "setup.js").write_text("", encoding="utf-8")
        (program_root / "static" / "js").mkdir(parents=True, exist_ok=True)
        (program_root / "static" / "js" / "account_policy.js").write_bytes((_REPOSITORY_ROOT / "static/js/account_policy.js").read_bytes())
        return PortableWebSettings(
            instance_id="portable-web-test",
            program_root=program_root,
            data_root=root / "data",
            cache_root=root / "cache",
            port=8000,
            launcher_token=_TOKEN,
            setup_token=setup_token,
            application_target=validate_database_target(_DSN, environ={}),
            probe_target=validate_database_target(_PROBE_DSN, environ={}),
        )

    @staticmethod
    def _business_app() -> FastAPI:
        application = FastAPI()

        @application.get("/")
        def root():
            return {"business": "available"}

        return application

    def test_build_settings_requires_explicit_portable_roots_roles_and_tokens(self):
        with self.temporary_directory() as temporary_directory:
            root = Path(temporary_directory)
            environment = {
                "GOOFISH_PORTABLE_MODE": "portable",
                "GOOFISH_PORTABLE_PROGRAM_ROOT": str(root / "app"),
                "GOOFISH_PORTABLE_DATA_ROOT": str(root / "data"),
                "GOOFISH_PORTABLE_CACHE_ROOT": str(root / "cache"),
                "GOOFISH_PORTABLE_DATABASE_URL": _DSN,
                PROBE_DATABASE_ENVIRONMENT_VARIABLE: _PROBE_DSN,
                LAUNCHER_TOKEN_ENVIRONMENT_VARIABLE: _TOKEN,
                SETUP_TOKEN_ENVIRONMENT_VARIABLE: _SETUP_TOKEN,
            }
            settings = build_settings(
                mode="normal", instance_id="portable-web-test", port=8000, environ=environment
            )
            self.assertEqual(settings.application_target.parameters["user"], "app_role")
            self.assertEqual(settings.probe_target.parameters["user"], "probe_role")

            environment[PROBE_DATABASE_ENVIRONMENT_VARIABLE] = _DSN
            with self.assertRaisesRegex(ValueError, "roles must be distinct"):
                build_settings(mode="normal", instance_id="portable-web-test", port=8000, environ=environment)

            environment[PROBE_DATABASE_ENVIRONMENT_VARIABLE] = _PROBE_DSN.replace(":55432/", ":55433/")
            with self.assertRaisesRegex(ValueError, "targets must match"):
                build_settings(mode="normal", instance_id="portable-web-test", port=8000, environ=environment)

            environment[PROBE_DATABASE_ENVIRONMENT_VARIABLE] = _PROBE_DSN
            environment[SETUP_TOKEN_ENVIRONMENT_VARIABLE] = _TOKEN
            with self.assertRaisesRegex(ValueError, "tokens must be distinct"):
                build_settings(mode="normal", instance_id="portable-web-test", port=8000, environ=environment)

    def test_setup_gate_exposes_only_safe_routes_and_control_ready_requires_token_and_instance(self):
        with self.temporary_directory() as temporary_directory:
            state = {"has_user": False}
            application = create_application(
                self._settings(Path(temporary_directory)),
                probe=_CompatibleProbe(),
                users_exist=lambda _settings: state["has_user"],
                create_admin=lambda _settings, _username, _password: "ignored",
                business_app_factory=self._business_app,
            )
            with TestClient(application) as client:
                host_headers = {"Host": "127.0.0.1:8000"}
                self.assertEqual(client.get("/health", headers=host_headers).status_code, 200)
                root_response = client.get("/", headers=host_headers, follow_redirects=False)
                self.assertEqual(root_response.status_code, 303)
                self.assertEqual(root_response.headers["location"], "/setup")
                self.assertEqual(client.get("/internal/ready", headers=host_headers).status_code, 401)
                self.assertEqual(
                    client.get(
                        "/internal/ready", headers={**host_headers, "Authorization": f"Bearer {_TOKEN}"}
                    ).status_code,
                    403,
                )
                response = client.get(
                    "/internal/ready",
                    headers={
                        **host_headers,
                        "Authorization": f"Bearer {_TOKEN}",
                        "X-Goofish-Instance-Id": "portable-web-test",
                    },
                )
                self.assertEqual(response.status_code, 200)
                self.assertEqual(response.json()["mode"], "normal")
                self.assertTrue(response.json()["setup_required"])
                response = client.get("/setup", headers=host_headers)
                self.assertEqual(response.status_code, 200)
                self.assertEqual(response.headers["cache-control"], "no-store")
                self.assertIn("default-src 'none'", response.headers["content-security-policy"])
                self.assertIn("默认管理员账号", response.text)
                self.assertIn("至少 8 位，允许纯数字", response.text)
                self.assertIn("data-account-policy=", response.text)
                self.assertEqual(client.get("/setup-assets/account-policy.js", headers=host_headers).status_code, 200)
                self.assertEqual(client.get("/static/js/account_policy.js", headers=host_headers).status_code, 503)
                self.assertNotIn('id="setup-token"', response.text)
                self.assertNotIn('id="username"', response.text)
                self.assertNotIn("fonts.googleapis.com", response.text)
                self.assertNotIn('id="loginForm"', response.text)
                self.assertNotIn(_SETUP_TOKEN, response.text)
                self.assertIn("nonce-", response.headers["content-security-policy"])
                self.assertEqual(client.get("/api/tasks", headers=host_headers).status_code, 503)
                self.assertEqual(client.get("/images/private.png", headers=host_headers).status_code, 503)
                self.assertEqual(client.get("/login", headers=host_headers, follow_redirects=False).headers["location"], "/setup")

    def test_browser_setup_requires_launcher_ticket_and_creates_only_admin(self):
        with self.temporary_directory() as temporary_directory:
            state = {"has_user": False, "calls": []}
            def create_admin(_settings, username, password):
                state["calls"].append((username, password))
                state["has_user"] = True
            settings = self._settings(Path(temporary_directory))
            application = create_application(settings, probe=_CompatibleProbe(),
                users_exist=lambda _: state["has_user"], create_admin=create_admin,
                business_app_factory=self._business_app)
            headers = {"Origin": settings.loopback_origin}
            launcher = {"Authorization": f"Bearer {_SETUP_TOKEN}", "X-Goofish-Instance-Id": settings.instance_id}
            with TestClient(application, base_url=settings.loopback_origin) as client:
                self.assertEqual(client.post("/setup", headers=headers, json={"password": "GoodPassword1!"}).status_code, 401)
                self.assertEqual(client.get("/setup/session").status_code, 401)
                self.assertEqual(client.post("/internal/setup-ticket").status_code, 401)
                self.assertEqual(client.post("/internal/setup-ticket", headers={**launcher, "Authorization": f"Bearer {_TOKEN}"}).status_code, 401)
                self.assertEqual(client.post("/internal/setup-ticket", headers={**launcher, "Origin": settings.loopback_origin}).status_code, 403)
                self.assertEqual(client.post("/internal/setup-ticket", headers={**launcher, "X-Goofish-Instance-Id": "wrong"}).status_code, 403)
                issued = client.post("/internal/setup-ticket", headers=launcher)
                self.assertEqual(issued.status_code, 200)
                self.assertEqual(issued.headers["cache-control"], "no-store")
                ticket = issued.json()["ticket"]
                self.assertNotIn(ticket, client.get("/setup").text)
                self.assertEqual(client.post("/setup/authorize", json={"ticket": ticket}).status_code, 403)
                self.assertEqual(client.post("/setup/authorize", headers={"Origin": "http://127.0.0.1:8001"}, json={"ticket": ticket}).status_code, 403)
                granted = client.post("/setup/authorize", headers=headers, json={"ticket": ticket})
                self.assertEqual(granted.status_code, 200)
                self.assertIn("HttpOnly", granted.headers["set-cookie"])
                self.assertIn("SameSite=strict", granted.headers["set-cookie"])
                self.assertIn("Path=/setup", granted.headers["set-cookie"])
                self.assertNotIn("Max-Age", granted.headers["set-cookie"])
                self.assertNotIn("expires=", granted.headers["set-cookie"].lower())
                self.assertNotIn(_SETUP_TOKEN, granted.text + granted.headers["set-cookie"])
                self.assertEqual(client.post("/setup/authorize", headers=headers, json={"ticket": ticket}).status_code, 401)
                self.assertEqual(client.get("/setup/session").status_code, 200)
                self.assertEqual(client.get("/setup").status_code, 200)  # Refresh keeps authorization.
                self.assertEqual(client.post("/setup", headers=headers, json={"username": "other", "password": "GoodPassword1!"}).status_code, 422)
                response = client.post("/setup", headers=headers, json={"password": "GoodPassword1!"})
                self.assertEqual(response.status_code, 201)
                self.assertIn("Max-Age=0", response.headers["set-cookie"])
                self.assertEqual(state["calls"], [("admin", "GoodPassword1!")])
                self.assertEqual(client.get("/setup/session").status_code, 401)
                self.assertEqual(client.post("/internal/setup-ticket", headers=launcher).status_code, 409)
                self.assertEqual(client.post("/setup", headers=headers, json={"password": "DifferentPassword2!"}).status_code, 409)
                self.assertEqual(client.get("/setup", follow_redirects=False).headers["location"], "/login")

    def test_ticket_expires_but_browser_session_lasts_until_setup_or_restart(self):
        with patch("src.portable.web_runtime.time.monotonic", return_value=100) as clock:
            grants = _SetupBrowserGrants()
            ticket = grants.issue_ticket()
            self.assertNotIn(ticket, repr(grants.tickets))
            clock.return_value = 161
            self.assertIsNone(grants.exchange(ticket))
            session = grants.exchange(grants.issue_ticket())
            self.assertTrue(grants.authorized(session))
            self.assertFalse(_SetupBrowserGrants().authorized(session))
            self.assertNotIn(session, repr(grants.sessions))
            clock.return_value = 100_000
            self.assertTrue(grants.authorized(session))
            self.assertEqual(grants.exchange(grants.issue_ticket(), session), session)
            for _ in range(7):
                grants.exchange(grants.issue_ticket())
            with self.assertRaises(HTTPException) as rejected:
                grants.exchange(grants.issue_ticket())
            self.assertEqual(rejected.exception.status_code, 429)
            self.assertTrue(grants.authorized(session))
            self.assertEqual(grants.exchange(grants.issue_ticket(), session), session)
            self.assertEqual(len(grants.sessions), 8)
            for _ in range(12):
                grants.issue_ticket()
            self.assertLessEqual(len(grants.tickets), 8)
            grants.clear()
            self.assertFalse(grants.tickets or grants.sessions)
            self.assertFalse(grants.authorized(session))

    def test_existing_users_never_get_bootstrap_or_default_account(self):
        with self.temporary_directory() as temporary_directory:
            settings = self._settings(Path(temporary_directory), setup_token="")
            calls = []
            app = create_application(settings, probe=_CompatibleProbe(), users_exist=lambda _: True,
                create_admin=lambda *args: calls.append(args), business_app_factory=self._business_app)
            with TestClient(app, base_url=settings.loopback_origin) as client:
                self.assertEqual(client.post("/internal/setup-ticket").status_code, 409)
                self.assertEqual(client.get("/setup", follow_redirects=False).headers["location"], "/login")
                self.assertEqual(client.post("/setup", headers={"Origin": settings.loopback_origin}, json={"password": "GoodPassword1!"}).status_code, 409)
            self.assertEqual(calls, [])

    def test_setup_requires_exact_loopback_json_token_and_closes_after_persisted_admin(self):
        with self.temporary_directory() as temporary_directory:
            state = {"has_user": False, "calls": 0}

            def create_admin(_settings, username, password):
                self.assertEqual((username, password), ("firstadmin", "GoodPassword1!"))
                state["calls"] += 1
                state["has_user"] = True
                return "first-user"

            application = create_application(
                self._settings(Path(temporary_directory)),
                probe=_CompatibleProbe(),
                users_exist=lambda _settings: state["has_user"],
                create_admin=create_admin,
                business_app_factory=self._business_app,
            )
            headers = {"Host": "127.0.0.1:8000", "Origin": "http://127.0.0.1:8000"}
            payload = {"username": "firstadmin", "password": "GoodPassword1!", "setup_token": _SETUP_TOKEN}
            with TestClient(application) as client:
                self.assertEqual(client.post("/setup", headers={"Host": "127.0.0.1:8000"}, json=payload).status_code, 403)
                self.assertEqual(client.post("/setup", headers=headers, data="{}").status_code, 415)
                rejected = dict(payload, setup_token="wrong")
                self.assertEqual(client.post("/setup", headers=headers, json=rejected).status_code, 401)
                unicode_token = dict(payload, setup_token="令牌")
                self.assertEqual(client.post("/setup", headers=headers, json=unicode_token).status_code, 401)
                self.assertEqual(
                    client.post(
                        "/setup",
                        headers={**headers, "Content-Type": "application/json", "Content-Length": "5000"},
                        content=b"{}",
                    ).status_code,
                    413,
                )
                self.assertEqual(state["calls"], 0)
                response = client.post("/setup", headers=headers, json=payload)
                self.assertEqual(response.status_code, 201)
                self.assertEqual(state["calls"], 1)
                self.assertTrue(portable_scheduler_start_allowed())
                self.assertEqual(client.get("/", headers={"Host": "127.0.0.1:8000"}).status_code, 200)
                self.assertEqual(client.post("/setup", headers=headers, json=payload).status_code, 409)

    def test_startup_rejects_incompatible_schema_or_missing_setup_token_before_mounting_business_app(self):
        with self.temporary_directory() as temporary_directory:
            invoked = []
            with self.assertRaisesRegex(PortableWebStartupError, "schema is not ready"):
                create_application(
                    self._settings(Path(temporary_directory)),
                    probe=_UninitializedProbe(),
                    users_exist=lambda _settings: False,
                    business_app_factory=lambda: invoked.append(True),
                )
            self.assertEqual(invoked, [])
            with self.assertRaisesRegex(PortableWebStartupError, "setup token"):
                create_application(
                    self._settings(Path(temporary_directory), setup_token=""),
                    probe=_CompatibleProbe(),
                    users_exist=lambda _settings: False,
                    business_app_factory=self._business_app,
                )

    def test_shutdown_drains_active_request_then_sets_callback_without_task_termination(self):
        with self.temporary_directory() as temporary_directory:
            started = threading.Event()
            release = threading.Event()
            callback = []
            pauses = []
            resumes = []
            disposals = []

            def business() -> FastAPI:
                application = FastAPI()

                @application.get("/work")
                async def work(background_tasks: BackgroundTasks):
                    started.set()
                    while not release.is_set():
                        await __import__("asyncio").sleep(0.01)
                    background_tasks.add_task(lambda: None)
                    return {"done": True}

                return application

            async def pause():
                pauses.append(True)
                return True

            async def resume(was_running):
                resumes.append(was_running)

            application = create_application(
                self._settings(Path(temporary_directory)),
                probe=_CompatibleProbe(),
                users_exist=lambda _settings: True,
                business_app_factory=business,
                request_server_exit=lambda: callback.append(True),
                processes_active=lambda: False,
                pause_scheduler=pause,
                resume_scheduler=resume,
                dispose_storage=lambda: disposals.append(True),
                shutdown_timeout_seconds=1.0,
            )
            headers = {
                "Host": "127.0.0.1:8000", "Authorization": f"Bearer {_TOKEN}",
                "X-Goofish-Instance-Id": "portable-web-test",
            }
            with TestClient(application) as client:
                worker = threading.Thread(
                    target=lambda: client.get("/work", headers={"Host": "127.0.0.1:8000"}), daemon=True
                )
                worker.start()
                self.assertTrue(started.wait(timeout=1.0))
                self.assertEqual(client.post("/internal/shutdown", headers=headers).status_code, 202)
                self.assertEqual(client.get("/internal/shutdown-state", headers=headers).json()["state"], "Draining")
                self.assertEqual(client.get("/work", headers={"Host": "127.0.0.1:8000"}).status_code, 503)
                self.assertEqual(callback, [])
                release.set()
                worker.join(timeout=1.0)
                for _ in range(30):
                    if callback:
                        break
                    time.sleep(0.02)
                self.assertEqual(callback, [True])
                self.assertEqual(disposals, [True])
                self.assertEqual(pauses, [True])
                self.assertEqual(resumes, [])

    def test_shutdown_waits_for_response_background_task_completion(self):
        with self.temporary_directory() as temporary_directory:
            background_started = threading.Event()
            background_release = threading.Event()
            callback = []

            def business() -> FastAPI:
                application = FastAPI()

                @application.get("/background")
                async def background(background_tasks: BackgroundTasks):
                    def block():
                        background_started.set()
                        background_release.wait(timeout=1.0)
                    background_tasks.add_task(block)
                    return {"queued": True}

                return application

            async def pause():
                return False

            async def resume(_was_running):
                return None

            application = create_application(
                self._settings(Path(temporary_directory)), probe=_CompatibleProbe(),
                users_exist=lambda _settings: True, business_app_factory=business,
                request_server_exit=lambda: callback.append(True), processes_active=lambda: False,
                pause_scheduler=pause, resume_scheduler=resume, shutdown_timeout_seconds=1.0,
            )
            headers = {
                "Host": "127.0.0.1:8000", "Authorization": f"Bearer {_TOKEN}",
                "X-Goofish-Instance-Id": "portable-web-test",
            }
            with TestClient(application) as client:
                worker = threading.Thread(
                    target=lambda: client.get("/background", headers={"Host": "127.0.0.1:8000"}), daemon=True
                )
                worker.start()
                self.assertTrue(background_started.wait(timeout=1.0))
                self.assertEqual(client.post("/internal/shutdown", headers=headers).status_code, 202)
                time.sleep(0.08)
                self.assertEqual(callback, [])
                background_release.set()
                worker.join(timeout=1.0)
                for _ in range(30):
                    if callback:
                        break
                    time.sleep(0.02)
                self.assertEqual(callback, [True])

    def test_shutdown_timeout_or_cancel_restores_scheduler_and_never_claims_stopped(self):
        with self.temporary_directory() as temporary_directory:
            active = {"value": True}
            callbacks = []
            resumes = []

            async def pause():
                return True

            async def resume(was_running):
                resumes.append(was_running)

            application = create_application(
                self._settings(Path(temporary_directory)),
                probe=_CompatibleProbe(), users_exist=lambda _settings: True,
                business_app_factory=self._business_app,
                request_server_exit=lambda: callbacks.append(True),
                processes_active=lambda: active["value"],
                pause_scheduler=pause, resume_scheduler=resume,
                shutdown_timeout_seconds=0.08,
            )
            headers = {
                "Host": "127.0.0.1:8000", "Authorization": f"Bearer {_TOKEN}",
                "X-Goofish-Instance-Id": "portable-web-test",
            }
            with TestClient(application) as client:
                self.assertEqual(client.post("/internal/shutdown", headers=headers).status_code, 202)
                time.sleep(0.15)
                state = client.get("/internal/shutdown-state", headers=headers).json()
                self.assertEqual(state["state"], "TimedOut")
                self.assertEqual(state["detail"], "shutdown_timed_out")
                self.assertEqual(callbacks, [])
                self.assertEqual(resumes, [True])
                self.assertEqual(client.post("/internal/shutdown", headers=headers).status_code, 202)
                self.assertEqual(client.post("/internal/cancel-shutdown", headers=headers).json()["state"], "Idle")
                self.assertEqual(resumes, [True, True])

    def test_shutdown_callback_failure_restores_service_instead_of_reporting_stopped(self):
        with self.temporary_directory() as temporary_directory:
            resumes = []

            async def pause():
                return True

            async def resume(was_running):
                resumes.append(was_running)

            application = create_application(
                self._settings(Path(temporary_directory)),
                probe=_CompatibleProbe(), users_exist=lambda _settings: True,
                business_app_factory=self._business_app,
                request_server_exit=lambda: (_ for _ in ()).throw(OSError("not exposed")),
                processes_active=lambda: False,
                pause_scheduler=pause, resume_scheduler=resume,
                shutdown_timeout_seconds=1.0,
            )
            headers = {
                "Host": "127.0.0.1:8000", "Authorization": f"Bearer {_TOKEN}",
                "X-Goofish-Instance-Id": "portable-web-test",
            }
            with TestClient(application) as client:
                self.assertEqual(client.post("/internal/shutdown", headers=headers).status_code, 202)
                for _ in range(30):
                    state = client.get("/internal/shutdown-state", headers=headers).json()
                    if state["state"] == "TimedOut":
                        break
                    time.sleep(0.02)
                self.assertEqual(state["state"], "TimedOut")
                self.assertEqual(state["detail"], "shutdown_callback_failed")
                self.assertEqual(resumes, [True])

    def test_real_business_main_imports_but_portable_lifespan_rejects_missing_outer_gate(self):
        """Importing main is inspectable; only startup requires the outer runtime."""

        repository_root = _REPOSITORY_ROOT
        try:
            with self.temporary_directory() as temporary_directory:
                environment = os.environ.copy()
                environment.update({"PYTHONPATH": str(repository_root), "PYTHONDONTWRITEBYTECODE": "1"})
                source = """
                    import asyncio
                    import os
                    from pathlib import Path

                    root = Path.cwd()
                    program = root / "app"
                    data = root / "data"
                    for directory in (program / "static", program / "images", program / "templates"):
                        directory.mkdir(parents=True)
                    (data / "assets" / "avatars").mkdir(parents=True)
                    os.environ.update({
                        "GOOFISH_PORTABLE_MODE": "portable",
                        "GOOFISH_PORTABLE_PROGRAM_ROOT": str(program),
                        "GOOFISH_PORTABLE_DATA_ROOT": str(data),
                        "GOOFISH_PORTABLE_CACHE_ROOT": str(root / "cache"),
                        "GOOFISH_PORTABLE_DATABASE_URL": "postgresql://app_role:app-secret@127.0.0.1:55432/goofish",
                        "SECRET_KEY": "K" * 32,
                        "ENCRYPTION_MASTER_KEY": "M" * 32,
                    })
                    from fastapi.testclient import TestClient
                    from src.portable.maintenance import DatabaseProbeResult, validate_database_target
                    from src.portable.web_runtime import PortableWebSettings, create_application
                    from src.web.main import app
                    class CompatibleProbe:
                        def check(self):
                            return DatabaseProbeResult("available", "compatible", 1, None)
                    settings = PortableWebSettings(
                        instance_id="main-mount-test",
                        program_root=program,
                        data_root=data,
                        cache_root=root / "cache",
                        port=8000,
                        launcher_token="L" * 32,
                        setup_token="",
                        application_target=validate_database_target(
                            "postgresql://app_role:app-secret@127.0.0.1:55432/goofish", environ={}
                        ),
                        probe_target=validate_database_target(
                            "postgresql://probe_role:probe-secret@127.0.0.1:55432/goofish", environ={}
                        ),
                    )
                    outer = create_application(
                        settings,
                        probe=CompatibleProbe(),
                        users_exist=lambda _settings: True,
                    )
                    with TestClient(outer) as client:
                        assert client.get("/health", headers={"Host": "127.0.0.1:8000"}).status_code == 200
                    async def check():
                        try:
                            async with app.router.lifespan_context(app):
                                raise AssertionError("direct portable main startup unexpectedly succeeded")
                        except RuntimeError as exc:
                            assert "portable_web.py" in str(exc)
                    asyncio.run(check())
                """
                completed = subprocess.run(
                    [sys.executable, "-B", "-c", textwrap.dedent(source)],
                    cwd=temporary_directory,
                    env=environment,
                    capture_output=True,
                    text=True,
                    timeout=30,
                )
                self.assertEqual(
                    completed.returncode,
                    0,
                    msg=f"stdout:\n{completed.stdout}\nstderr:\n{completed.stderr}",
                )
        finally:
            pass


if __name__ == "__main__":
    unittest.main()
