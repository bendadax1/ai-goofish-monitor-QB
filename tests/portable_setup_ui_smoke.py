"""First-run page regression against intercepted local fixtures, never business APIs."""

import json
import os
from pathlib import Path
import tempfile

from playwright.sync_api import sync_playwright


ROOT = Path(__file__).resolve().parents[1]


def main():
    browser_root = ROOT / ".tmp/dependencies/portable-browser/chromium-1.57.0-r1200-4b4d412c65ff-win64"
    parent = ROOT / ".tmp/tests/portable-setup-ui"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=parent) as temporary:
        previous = {key: os.environ.get(key) for key in ("TEMP", "TMP")}
        os.environ.update(TEMP=temporary, TMP=temporary)
        try:
            with sync_playwright() as api:
                browser = api.chromium.launch(executable_path=str(browser_root / "chrome-win64/chrome.exe"), headless=True,
                    args=["--disable-background-networking", "--disable-component-update", "--disable-sync", "--no-first-run"])
                try:
                    page = browser.new_page(viewport={"width":1120, "height":960}, reduced_motion="reduce")
                    observed = {"posts":0, "reply":201}
                    def route_request(route):
                        request = route.request
                        assert request.url.startswith("http://127.0.0.1:18080/"), "unexpected external URL"
                        path = request.url.split(":18080",1)[1]
                        resources = {
                            "/setup": (ROOT / "templates/portable_setup.html", "text/html"),
                            "/setup-assets/setup.css": (ROOT / "static/portable/setup.css", "text/css"),
                            "/setup-assets/setup.js": (ROOT / "static/portable/setup.js", "text/javascript"),
                        }
                        if request.method == "POST":
                            assert path == "/setup"
                            assert set(request.post_data_json) == {"username", "password", "setup_token"}
                            observed["posts"] += 1
                            route.fulfill(status=observed["reply"], content_type="application/json", body=json.dumps({"status":"fixture"}))
                        elif path in resources:
                            file, content_type = resources[path]
                            route.fulfill(status=200, content_type=content_type, body=file.read_text(encoding="utf-8"))
                        else:
                            route.fulfill(status=404, body="fixture not found")
                    page.route("**/*", route_request)
                    page.goto("http://127.0.0.1:18080/setup")
                    page.get_by_role("heading", name="创建你的管理员账户").wait_for()
                    output = ROOT / "launcher/dist/launcher-p0-verification"
                    output.mkdir(parents=True, exist_ok=True)
                    page.screenshot(path=str(output / "setup-1120.png"), full_page=True)
                    page.set_viewport_size({"width":375,"height":812})
                    assert page.evaluate("document.documentElement.scrollWidth <= innerWidth")
                    page.screenshot(path=str(output / "setup-375.png"), full_page=True)
                    page.get_by_role("button", name="创建管理员账户").click()
                    assert observed["posts"] == 0
                    assert page.locator("#summary").is_visible()
                    assert page.locator("#summary").evaluate("element => element === document.activeElement")
                    def fill():
                        page.get_by_label("一次性设置码", exact=True).fill("S"*32)
                        page.get_by_label("管理员用户名", exact=True).fill("fixture_admin")
                        page.get_by_label("登录密码", exact=True).fill("FixturePassword7!")
                        page.get_by_label("确认密码", exact=True).fill("FixturePassword7!")
                    observed["reply"] = 401
                    fill()
                    page.get_by_role("button", name="创建管理员账户").click()
                    page.get_by_text("设置码不正确，请返回 Launcher 核对。").wait_for()
                    assert page.locator("#password").input_value() == ""
                    assert page.locator("#setup-token").input_value() == ""
                    assert page.get_by_role("button", name="创建管理员账户").is_enabled()
                    observed["reply"] = 201
                    fill()
                    page.get_by_role("button", name="创建管理员账户").click()
                    page.get_by_role("heading", name="账户已创建").wait_for()
                    assert observed["posts"] == 2
                    assert page.locator("#setup-token").input_value() == ""
                    assert page.locator("#password").input_value() == ""
                    assert page.get_by_role("link", name="进入登录页面").get_attribute("href") == "/login"
                    assert page.evaluate("localStorage.length + sessionStorage.length") == 0
                    print("PORTABLE_SETUP_UI=PASS")
                    print("RENDERS=" + str(output))
                finally:
                    browser.close()
        finally:
            for key, value in previous.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value
    parent.rmdir()


if __name__ == "__main__":
    main()
