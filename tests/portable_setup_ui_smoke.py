"""Real-browser first-run regression with an isolated, in-memory business fixture.

No production database, AI, notifications, saved user credentials or external
networking. Run with python -B -m tests.portable_setup_ui_smoke.
"""

import os
from contextlib import contextmanager
from pathlib import Path
import socket
import tempfile
import threading
import time

import httpx
from fastapi import FastAPI, Form
from fastapi.responses import HTMLResponse, JSONResponse, RedirectResponse
from playwright.sync_api import sync_playwright, expect
import uvicorn

from src.portable.maintenance import DatabaseProbeResult, validate_database_target
from src.portable.web_runtime import PortableWebSettings, create_application
from src.account_policy import validate_new_password


ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / ".tmp/tests/account-policy-20260928"
PASSWORD = "87654321"


@contextmanager
def fixture_server(root, *, fail_login=False, reject_password_once=False):
    state = {"created": False, "creates": 0, "logins": 0, "reject": reject_password_once}
    business = FastAPI()

    @business.get("/")
    def home():
        return HTMLResponse("<!doctype html><h1>Fixture dashboard</h1>")

    @business.get("/login")
    def login_page():
        return HTMLResponse("<!doctype html><h1>Fixture login</h1>")

    @business.post("/login")
    def login(username: str = Form(...), password: str = Form(...)):
        assert username == "admin" and password == PASSWORD and state["created"]
        state["logins"] += 1
        if fail_login:
            return JSONResponse({"message": "fixture failure"}, status_code=503)
        response = RedirectResponse("/", status_code=303)
        response.set_cookie("fixture_session", "fixture", httponly=True, samesite="strict")
        return response

    class Probe:
        def check(self):
            return DatabaseProbeResult("available", "compatible", 1, None)

    def create_admin(_settings, username, password):
        validate_new_password(password)
        assert username == "admin" and password == PASSWORD
        if state["reject"]:
            state["reject"] = False
            raise ValueError("fixture validation rejection")
        state["creates"] += 1
        state["created"] = True

    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    port = listener.getsockname()[1]
    settings = PortableWebSettings(
        instance_id="browser-fixture", program_root=ROOT, data_root=root / "data",
        cache_root=root / "cache", port=port, launcher_token="L" * 32, setup_token="S" * 32,
        application_target=validate_database_target("postgresql://app:unused@127.0.0.1:1/fixture", environ={}),
        probe_target=validate_database_target("postgresql://probe:unused@127.0.0.1:1/fixture", environ={}),
    )
    app = create_application(settings, probe=Probe(), users_exist=lambda _: state["created"],
                             create_admin=create_admin, business_app_factory=lambda: business)
    server = uvicorn.Server(uvicorn.Config(app, access_log=False, log_level="error"))
    worker = threading.Thread(target=lambda: server.run(sockets=[listener]), daemon=True)
    worker.start()
    try:
        deadline = time.monotonic() + 10
        while not server.started:
            if time.monotonic() > deadline or not worker.is_alive():
                raise RuntimeError("fixture server did not start")
            time.sleep(0.02)
        with httpx.Client(base_url=settings.loopback_origin, trust_env=False, timeout=5) as client:
            def ticket_url():
                response = client.post("/internal/setup-ticket", headers={
                    "Authorization": "Bearer " + settings.setup_token,
                    "X-Goofish-Instance-Id": settings.instance_id,
                })
                response.raise_for_status()
                return settings.loopback_origin + "/setup#ticket=" + response.json()["ticket"]
            yield settings.loopback_origin, ticket_url, state
    finally:
        server.should_exit = True
        worker.join(timeout=10)
        listener.close()
        if worker.is_alive():
            raise RuntimeError("fixture server did not stop")


def main():
    browser_root = ROOT / ".tmp/dependencies/portable-browser/chromium-1.57.0-r1200-4b4d412c65ff-win64"
    OUTPUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=OUTPUT) as temporary:
        previous = {key: os.environ.get(key) for key in ("TEMP", "TMP")}
        os.environ.update(TEMP=temporary, TMP=temporary)
        try:
            with sync_playwright() as api:
                browser = api.chromium.launch(executable_path=str(browser_root / "chrome-win64/chrome.exe"), headless=True,
                    args=["--disable-background-networking", "--disable-component-update", "--disable-sync", "--no-first-run"])
                try:
                    for fail_login in (False, True):
                        with fixture_server(Path(temporary), fail_login=fail_login, reject_password_once=not fail_login) as (origin, ticket_url, state):
                            context = browser.new_context(viewport={"width": 1440, "height": 1000}, reduced_motion="reduce")
                            external = []
                            errors = []
                            def guard(route):
                                if route.request.url.startswith(origin + "/"):
                                    route.continue_()
                                else:
                                    external.append(route.request.url)
                                    route.abort()
                            context.route("**/*", guard)
                            page = context.new_page()
                            page.on("pageerror", lambda error: errors.append(str(error)))
                            page.on("console", lambda message: errors.append(message.text) if "Content Security Policy" in message.text else None)
                            page.goto(origin + "/setup")
                            expect(page.locator("#summary")).to_contain_text("请回到启动器")
                            expect(page.locator("#submit")).to_be_disabled()
                            url = ticket_url()
                            page.goto(url)
                            expect(page.locator("#submit")).to_be_enabled()
                            assert page.url == origin + "/setup"
                            expect(page.locator("#password-hint")).to_have_text("至少 8 位，允许纯数字")
                            assert page.locator("#setup-token").count() == page.locator("#username").count() == 0
                            assert page.evaluate("localStorage.length + sessionStorage.length") == 0
                            assert "goofish_setup" not in page.evaluate("document.cookie")
                            assert any(cookie["httpOnly"] and cookie["sameSite"] == "Strict" for cookie in context.cookies() if cookie["name"].startswith("goofish_setup"))
                            page.reload()
                            expect(page.locator("#submit")).to_be_enabled()
                            if not fail_login:
                                expect(page.locator(".login-header img")).to_be_visible()
                                assert page.locator(".login-header img").evaluate("element => element.naturalWidth > 0")
                                assert "login-bg.png" in page.locator("body").evaluate("element => getComputedStyle(element).backgroundImage")
                                page.screenshot(path=str(OUTPUT / "setup-desktop.png"), full_page=True)
                                page.set_viewport_size({"width": 375, "height": 812})
                                assert page.evaluate("document.documentElement.scrollWidth <= innerWidth")
                                page.screenshot(path=str(OUTPUT / "setup-mobile.png"), full_page=True)
                                page.get_by_role("button", name="设置密码并进入").click()
                                expect(page.locator("#summary")).to_contain_text("请检查密码")
                                assert state["creates"] == 0
                                page.get_by_label("设置密码", exact=True).fill("1234567")
                                page.get_by_label("确认密码", exact=True).fill("1234567")
                                page.get_by_role("button", name="设置密码并进入").click()
                                expect(page.locator("#password-error")).to_contain_text("至少 8 位")
                                assert state["creates"] == 0
                                page.get_by_label("设置密码", exact=True).fill(PASSWORD)
                                page.get_by_label("确认密码", exact=True).fill(PASSWORD + "x")
                                page.get_by_role("button", name="设置密码并进入").click()
                                expect(page.locator("#confirm-error")).to_contain_text("不一致")
                                assert state["creates"] == 0
                            def fill():
                                page.get_by_label("设置密码", exact=True).fill(PASSWORD)
                                page.get_by_label("确认密码", exact=True).fill(PASSWORD)
                            if not fail_login:
                                fill()
                                page.get_by_role("button", name="设置密码并进入").click()
                                expect(page.locator("#summary")).to_contain_text("密码未通过检查")
                                expect(page.locator("#submit")).to_be_enabled()
                                assert page.locator("#password").input_value() == ""
                            fill()
                            page.get_by_role("button", name="设置密码并进入").click()
                            if fail_login:
                                expect(page.locator("#summary")).to_contain_text("密码已设置成功")
                                expect(page.get_by_role("link", name="前往登录")).to_be_visible()
                                expect(page.locator("#submit")).to_be_disabled()
                                assert page.locator("#password").input_value() == ""
                                page.get_by_role("link", name="前往登录").click()
                                expect(page.get_by_role("heading", name="Fixture login")).to_be_visible()
                            else:
                                expect(page.get_by_role("heading", name="Fixture dashboard")).to_be_visible()
                                assert page.url == origin + "/"
                            assert state["creates"] == state["logins"] == 1
                            assert not any(cookie["name"].startswith("goofish_setup") for cookie in context.cookies())
                            assert not external, "external network request"
                            assert not errors, errors
                            context.close()
                    print("PORTABLE_SETUP_UI=PASS")
                    print("RENDERS=" + str(OUTPUT))
                finally:
                    browser.close()
        finally:
            for key, value in previous.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value


if __name__ == "__main__":
    main()
