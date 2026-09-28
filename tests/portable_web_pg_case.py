"""Real Web bootstrap/login/logout against a disposable application database."""

import os
from pathlib import Path
import socket
import sys
import subprocess
from unittest.mock import patch


def main():
    source = Path(sys.argv[1]).resolve(strict=True)
    sys.path.insert(0, str(source))
    root = Path.cwd().resolve()
    app_root = Path(os.environ["GOOFISH_PORTABLE_PROGRAM_ROOT"])
    if app_root != root / "web-app":
        raise RuntimeError("unexpected isolated Web application root")
    for name in ("static", "images", "templates"):
        (app_root / name).mkdir(parents=True)
    for name in ("login.html", "index.html"):
        (app_root / "templates" / name).write_text("<!doctype html><title>Fixture</title>", encoding="utf-8")
    for relative in ("templates/login.html", "templates/index.html", "templates/portable_setup.html", "static/portable/setup.css", "static/portable/setup.js", "static/js/account_policy.js"):
        target = app_root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes((source / relative).read_bytes())

    original_connect = socket.socket.connect
    def local_connect(sock, address):
        if address[0] not in ("127.0.0.1", "::1"):
            raise AssertionError("unexpected non-loopback networking")
        return original_connect(sock, address)

    from fastapi.testclient import TestClient
    from src.portable.web_runtime import build_settings, create_application
    settings = build_settings(mode="normal", instance_id="web-pg-fixture", port=18080)
    with patch.object(socket.socket, "connect", local_connect):
        application = create_application(settings)
        with TestClient(application, base_url="http://127.0.0.1:18080") as client:
            origin = {"Origin": "http://127.0.0.1:18080"}
            control = {
                "Authorization": "Bearer " + os.environ["GOOFISH_LAUNCHER_TOKEN"],
                "X-Goofish-Instance-Id": "web-pg-fixture",
            }
            assert client.get("/health").status_code == 200
            setup_page = client.get("/setup")
            assert setup_page.status_code == 200 and "默认管理员账号" in setup_page.text
            assert setup_page.headers["cache-control"] == "no-store"
            assert "frame-ancestors 'none'" in setup_page.headers["content-security-policy"]
            assert client.get("/setup-assets/setup.js").status_code == 200
            assert client.get("/setup-assets/account-policy.js").status_code == 200
            assert client.get("/api/tasks").status_code == 503
            assert client.get("/internal/ready", headers=control).json()["setup_required"] is True
            payload = {"password": "87654321"}
            assert client.post("/setup", json=payload, headers=origin).status_code == 401
            setup_control = {**control, "Authorization": "Bearer " + os.environ["GOOFISH_PORTABLE_SETUP_TOKEN"]}
            ticket_response = client.post("/internal/setup-ticket", headers=setup_control)
            assert ticket_response.status_code == 200
            assert client.post("/setup/authorize", json=ticket_response.json(), headers=origin).status_code == 200
            assert client.post("/setup", json={"password": "1234567"}, headers=origin).status_code == 422
            assert client.post("/setup", json=payload, headers=origin).status_code == 201
            assert client.post("/setup", json=payload, headers=origin).status_code == 409
            assert client.get("/internal/ready", headers=control).json()["setup_required"] is False
            assert client.get("/api/tasks").status_code == 401
            login = client.post("/login", data={"username": "admin", "password": "87654321"}, headers=origin, follow_redirects=False)
            assert login.status_code == 302
            token = client.cookies.get("session_token")
            assert token and token.startswith("p1.")
            assert client.get("/auth/status").json()["authenticated"] is True
            assert client.get("/api/tasks").status_code == 200
            index = client.get("/")
            assert index.status_code == 200 and 'data-account-policy=' in index.text
            assert index.text.index('/static/js/account_policy.js') < index.text.index('/static/js/modules/users_view.js')
            from src.storage import get_storage
            storage = get_storage()
            viewer_group = next(group for group in storage.list_user_groups() if group['code'] == 'viewer_group')
            new_user = {"username": "numeric_user", "password": "1234567", "group_ids": [viewer_group['id']]}
            assert client.post("/api/users", json=new_user, headers=origin).status_code == 422
            assert storage.get_user_by_username("numeric_user") is None
            new_user['password'] = "98765432"
            created = client.post("/api/users", json=new_user, headers=origin)
            assert created.status_code == 200
            user_id = created.json()['user']['id']
            assert client.put(f"/api/users/{user_id}/password", json={"new_password": "1234567"}, headers=origin).status_code == 422
            assert client.put(f"/api/users/{user_id}/password", json={"new_password": "23456789"}, headers=origin).status_code == 200
            from src.storage.utils import verify_password
            assert verify_password("23456789", storage.get_user_by_username("numeric_user")['password_hash'])
            assert client.put("/api/users/me/password", json={"old_password": "87654321", "new_password": "1234567"}, headers=origin).status_code == 422
            assert client.put("/api/users/me/password", json={"old_password": "87654321", "new_password": "98765432"}, headers=origin).status_code == 200
            # Existing portable revocation behavior remains effective after changing a password.
            assert client.get("/auth/status").json()["authenticated"] is False
            assert client.post("/login", data={"username": "admin", "password": "98765432"}, headers=origin, follow_redirects=False).status_code == 302
            token = client.cookies.get("session_token")
            assert client.get("/logout", follow_redirects=False).status_code == 302
            client.cookies.set("session_token", token)
            assert client.get("/api/tasks").status_code == 401

        import src.web.main as business
        from src.storage import get_storage
        assert not business.fetcher_processes
        assert not business.scheduler.running
        get_storage().engine.dispose()
        verify_http_exit(source, root)
        # New hosting lifecycle with no setup token must remain permanently set up.
        os.environ.pop("GOOFISH_PORTABLE_SETUP_TOKEN")
        second = create_application(build_settings(mode="normal", instance_id="web-pg-fixture", port=18080))
        with TestClient(second, base_url="http://127.0.0.1:18080") as client:
            assert client.get("/internal/ready", headers=control).json()["setup_required"] is False
            assert client.get("/api/tasks").status_code == 401
        get_storage().engine.dispose()
    import logging
    logging.shutdown()
    print("PORTABLE_WEB_DATABASE_FLOW=PASS")


def verify_http_exit(source, root):
    from tests import portable_pg_smoke as pg
    port = pg._reserve_loopback_port()
    state = pg.SmokeState(root=root, data_root=root / "unused-pgdata", pg_log=root / "unused-pg.log")
    state.http_stdout = (root / "web-http.stdout.log").open("wb")
    state.http_stderr = (root / "web-http.stderr.log").open("wb")
    try:
        command = [sys.executable, "-I", "-B", "-c",
            "import sys,runpy;sys.path.insert(0," + repr(str(source)) + ");runpy.run_path(" + repr(str(source / "portable_web.py")) + ",run_name='__main__')",
            "--mode", "normal", "--instance-id", "web-pg-fixture", "--port", str(port)]
        state.http_process = subprocess.Popen(command, cwd=root, env=dict(os.environ),
            stdin=subprocess.DEVNULL, stdout=state.http_stdout, stderr=state.http_stderr,
            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
        pg._wait_for_http(port, state.http_process)
        token = os.environ["GOOFISH_LAUNCHER_TOKEN"]
        status, body = pg._request_json(port, "/internal/ready", token, instance_id="web-pg-fixture")
        assert status == 200 and body["mode"] == "normal"
        status, _ = pg._request_json(port, "/internal/shutdown", token, method="POST", instance_id="web-pg-fixture")
        assert status == 202
        assert state.http_process.wait(timeout=12) == 0
    finally:
        # Only the exact disposable Popen child may be stopped on test failure.
        if not pg._stop_http(state):
            raise RuntimeError("disposable Web process cleanup failed")


if __name__ == "__main__":
    main()
