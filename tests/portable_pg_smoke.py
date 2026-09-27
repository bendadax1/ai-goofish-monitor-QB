"""PostgreSQL Windows x64 便携 runtime 与维护入口的隔离集成冒烟。

该脚本只在 `.tmp/tests/portable-pg/<unique-id>` 创建一次性集群，不接受现有
PGDATA，不注册服务，不触发业务、AI 或通知。
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import secrets
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import time
import unittest
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Sequence
from unittest import mock

import psycopg2
from psycopg2 import sql

from src.portable.maintenance import PostgresReadinessProbe, validate_database_target


_REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
_LOCK_PATH = _REPOSITORY_ROOT / "scripts" / "portable" / "postgresql-win-x64.lock.json"
_TEST_PARENT = _REPOSITORY_ROOT / ".tmp" / "tests" / "portable-pg"
_MINIMUM_FREE_BYTES = 10 * 1024**3
_MINIMUM_FREE_RATIO = 0.05
_HTTP_TIMEOUT_SECONDS = 3

logger = logging.getLogger("portable_pg_smoke")


class SmokeFailure(RuntimeError):
    """不携带凭据的集成冒烟失败。"""


@dataclass
class SmokeState:
    root: Path
    data_root: Path
    pg_log: Path
    postgres_start_attempted: bool = False
    postgres_pid: int | None = None
    postgres_port: int | None = None
    postgres_start_epoch: float | None = None
    http_process: subprocess.Popen[bytes] | None = None
    http_stdout: Any = None
    http_stderr: Any = None
    raw_secrets: list[str] = field(default_factory=list, repr=False)


@dataclass(frozen=True)
class PostmasterIdentity:
    pid: int
    data_root: Path
    start_epoch: int
    port: int


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Run the isolated portable PostgreSQL smoke test")
    parser.add_argument(
        "--postgres-root",
        type=Path,
        help="Prepared PostgreSQL runtime root; defaults to the version-locked cache path",
    )
    parser.add_argument(
        "--python-executable",
        type=Path,
        help="Absolute portable Python executable for the maintenance HTTP child process",
    )
    parser.add_argument(
        "--python-bootstrap",
        type=Path,
        help="Absolute portable Python bootstrap paired with --python-executable",
    )
    return parser


def _load_lock() -> dict[str, Any]:
    try:
        lock = json.loads(_LOCK_PATH.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError):
        raise SmokeFailure("PostgreSQL component lock could not be read") from None
    if lock.get("schema_version") != 1 or lock.get("verification") != "official_https_local_sha256":
        raise SmokeFailure("PostgreSQL component lock is unsupported")
    return lock


def _runtime_root(lock: dict[str, Any], explicit_root: Path | None) -> Path:
    if explicit_root is not None:
        if not explicit_root.is_absolute():
            raise SmokeFailure("postgres-root must be absolute")
        return explicit_root.resolve(strict=False)
    return (
        _REPOSITORY_ROOT
        / ".tmp"
        / "dependencies"
        / "portable-pg"
        / f"postgresql-{lock['version']}-{lock['package_revision']}-windows-x64"
    )


def _assert_disk_floor(path: Path) -> tuple[int, int]:
    usage = shutil.disk_usage(path)
    if usage.free < _MINIMUM_FREE_BYTES or usage.free / usage.total < _MINIMUM_FREE_RATIO:
        raise SmokeFailure("disk free space is below the 10 GiB or 5% safety floor")
    return usage.free, usage.total


def _reserve_loopback_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        return int(listener.getsockname()[1])


def _run_tool(
    arguments: Sequence[str | Path],
    *,
    stage: str,
    diagnostic_root: Path,
    timeout: int = 30,
    direct_output: bool = False,
) -> subprocess.CompletedProcess[str]:
    stdout_path = diagnostic_root / f"{stage}.stdout.log"
    stderr_path = diagnostic_root / f"{stage}.stderr.log"
    try:
        if direct_output:
            with stdout_path.open("w", encoding="utf-8") as stdout_stream:
                with stderr_path.open("w", encoding="utf-8") as stderr_stream:
                    raw_completed = subprocess.run(
                        [str(argument) for argument in arguments],
                        stdin=subprocess.DEVNULL,
                        stdout=stdout_stream,
                        stderr=stderr_stream,
                        timeout=timeout,
                        check=False,
                    )
            completed = subprocess.CompletedProcess(
                raw_completed.args,
                raw_completed.returncode,
                "",
                "",
            )
        else:
            completed = subprocess.run(
                [str(argument) for argument in arguments],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=timeout,
                check=False,
            )
    except (OSError, subprocess.SubprocessError):
        raise SmokeFailure(f"{stage} tool execution failed") from None

    diagnostic_path = diagnostic_root / f"{stage}.log"
    try:
        diagnostic_path.write_text(
            f"exit_code={completed.returncode}\nstdout:\n{completed.stdout}\nstderr:\n{completed.stderr}",
            encoding="utf-8",
        )
    except OSError:
        raise SmokeFailure(f"{stage} diagnostic output could not be written") from None
    if completed.returncode != 0:
        raise SmokeFailure(f"{stage} failed with exit code {completed.returncode}")
    return completed


def _restrict_secret_file(path: Path) -> None:
    try:
        identity = subprocess.run(
            ["whoami.exe"],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=10,
            check=False,
        )
    except (OSError, subprocess.SubprocessError):
        raise SmokeFailure("current Windows user could not be identified for secret-file ACL") from None
    principal = identity.stdout.strip()
    if identity.returncode != 0 or not principal:
        raise SmokeFailure("current Windows user could not be identified for secret-file ACL")
    completed = subprocess.run(
        ["icacls.exe", str(path), "/inheritance:r", "/grant:r", f"{principal}:(F)"],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        timeout=10,
        check=False,
    )
    if completed.returncode != 0:
        raise SmokeFailure("initdb password file ACL could not be restricted")


def _append_server_configuration(data_root: Path, port: int) -> None:
    configuration = (
        "\n# Isolated portable smoke settings\n"
        "listen_addresses = '127.0.0.1'\n"
        f"port = {port}\n"
        "password_encryption = 'scram-sha-256'\n"
        "log_connections = on\n"
        "log_disconnections = on\n"
        "log_min_error_statement = 'panic'\n"
        "log_parameter_max_length_on_error = 0\n"
    )
    try:
        with (data_root / "postgresql.conf").open("a", encoding="utf-8", newline="\n") as stream:
            stream.write(configuration)
    except OSError:
        raise SmokeFailure("isolated PostgreSQL configuration could not be written") from None


def _assert_scram_hba(data_root: Path) -> None:
    try:
        active_lines = [
            line.strip()
            for line in (data_root / "pg_hba.conf").read_text(encoding="utf-8").splitlines()
            if line.strip() and not line.lstrip().startswith("#")
        ]
    except (OSError, UnicodeError):
        raise SmokeFailure("isolated pg_hba.conf could not be verified") from None
    if not active_lines or any("trust" in line.lower().split() for line in active_lines):
        raise SmokeFailure("isolated pg_hba.conf unexpectedly allows trust authentication")
    if any(line.split()[-1].lower() != "scram-sha-256" for line in active_lines):
        raise SmokeFailure("isolated pg_hba.conf is not uniformly SCRAM authenticated")


def _read_owned_postmaster_identity(state: SmokeState) -> PostmasterIdentity:
    try:
        lines = (state.data_root / "postmaster.pid").read_text(encoding="utf-8").splitlines()
        identity = PostmasterIdentity(
            pid=int(lines[0]),
            data_root=Path(lines[1]).resolve(strict=True),
            start_epoch=int(lines[2]),
            port=int(lines[3]),
        )
    except (OSError, UnicodeError, ValueError, IndexError):
        raise SmokeFailure("owned PostgreSQL postmaster identity could not be verified") from None
    expected_root = state.data_root.resolve(strict=True)
    if identity.pid <= 0 or os.path.normcase(str(identity.data_root)) != os.path.normcase(str(expected_root)):
        raise SmokeFailure("owned PostgreSQL postmaster data identity is invalid")
    if state.postgres_port is None or identity.port != state.postgres_port:
        raise SmokeFailure("owned PostgreSQL postmaster port identity is invalid")
    if (
        state.postgres_start_epoch is None
        or identity.start_epoch < int(state.postgres_start_epoch) - 2
        or identity.start_epoch > int(time.time()) + 5
    ):
        raise SmokeFailure("owned PostgreSQL postmaster start-time identity is invalid")
    return identity


def _connect(*, port: int, database: str, user: str, password: str):
    return psycopg2.connect(
        host="127.0.0.1",
        hostaddr="127.0.0.1",
        port=port,
        dbname=database,
        user=user,
        password=password,
        sslmode="disable",
        connect_timeout=3,
        options="-c statement_timeout=3000",
    )


def _dsn(*, port: int, database: str, user: str, password: str) -> str:
    return (
        "postgresql://"
        f"{urllib.parse.quote(user, safe='')}:{urllib.parse.quote(password, safe='')}"
        f"@127.0.0.1:{port}/{urllib.parse.quote(database, safe='')}"
    )


def _initialize_roles_and_database(
    *,
    state: SmokeState,
    port: int,
    admin_user: str,
    admin_password: str,
    probe_user: str,
    probe_password: str,
    database: str,
) -> None:
    connection = _connect(port=port, database="postgres", user=admin_user, password=admin_password)
    try:
        connection.autocommit = True
        with connection.cursor() as cursor:
            cursor.execute(
                sql.SQL(
                    "CREATE ROLE {} LOGIN PASSWORD %s NOSUPERUSER NOCREATEDB "
                    "NOCREATEROLE NOINHERIT NOREPLICATION"
                ).format(sql.Identifier(probe_user)),
                (probe_password,),
            )
            cursor.execute(
                sql.SQL("ALTER ROLE {} SET default_transaction_read_only = on").format(
                    sql.Identifier(probe_user)
                )
            )
            cursor.execute(sql.SQL("CREATE DATABASE {}").format(sql.Identifier(database)))
            cursor.execute(sql.SQL("REVOKE ALL ON DATABASE {} FROM PUBLIC").format(sql.Identifier(database)))
            cursor.execute(
                sql.SQL("GRANT CONNECT ON DATABASE {} TO {}").format(
                    sql.Identifier(database), sql.Identifier(probe_user)
                )
            )
    finally:
        connection.close()

    connection = _connect(port=port, database=database, user=admin_user, password=admin_password)
    try:
        with connection.cursor() as cursor:
            cursor.execute("REVOKE CREATE ON SCHEMA public FROM PUBLIC")
            cursor.execute(sql.SQL("GRANT USAGE ON SCHEMA public TO {}").format(sql.Identifier(probe_user)))
            cursor.execute(
                "SELECT current_setting('data_directory'), current_setting('server_encoding'), "
                "(SELECT datcollate FROM pg_database WHERE datname = current_database()), "
                "current_setting('listen_addresses'), "
                "current_setting('port'), current_setting('password_encryption')"
            )
            data_directory, encoding, collate, listen_addresses, actual_port, password_encryption = (
                cursor.fetchone()
            )
            if os.path.normcase(str(Path(data_directory).resolve())) != os.path.normcase(
                str(state.data_root.resolve())
            ):
                raise SmokeFailure("connected PostgreSQL data directory is not the owned test cluster")
            if encoding != "UTF8" or not collate.upper().startswith("C"):
                raise SmokeFailure("isolated PostgreSQL UTF8/C locale verification failed")
            if listen_addresses != "127.0.0.1" or int(actual_port) != port:
                raise SmokeFailure("isolated PostgreSQL listen identity verification failed")
            if password_encryption != "scram-sha-256":
                raise SmokeFailure("isolated PostgreSQL password encryption is not SCRAM")
            cursor.execute(
                "SELECT rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolcanlogin "
                "FROM pg_roles WHERE rolname = %s",
                (probe_user,),
            )
            role_flags = cursor.fetchone()
            if role_flags != (False, False, False, False, True):
                raise SmokeFailure("readiness probe role privileges are not minimal")
        connection.commit()
    finally:
        connection.close()


def _set_schema_fixture(
    *,
    port: int,
    admin_user: str,
    admin_password: str,
    probe_user: str,
    database: str,
    present: bool,
) -> None:
    connection = _connect(port=port, database=database, user=admin_user, password=admin_password)
    try:
        with connection.cursor() as cursor:
            cursor.execute("DROP TABLE IF EXISTS public.app_schema_version")
            if present:
                cursor.execute("CREATE TABLE public.app_schema_version (version integer NOT NULL)")
                cursor.execute("INSERT INTO public.app_schema_version (version) VALUES (%s)", (1,))
                cursor.execute(
                    sql.SQL("GRANT SELECT ON public.app_schema_version TO {}").format(
                        sql.Identifier(probe_user)
                    )
                )
        connection.commit()
    finally:
        connection.close()


def _request_json(
    port: int, path: str, token: str | None = None, *,
    method: str = "GET", instance_id: str | None = None,
) -> tuple[int, dict[str, Any]]:
    headers = {"Authorization": f"Bearer {token}"} if token is not None else {}
    if instance_id is not None:
        headers["X-Goofish-Instance-Id"] = instance_id
    request = urllib.request.Request(f"http://127.0.0.1:{port}{path}", headers=headers, method=method)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(request, timeout=_HTTP_TIMEOUT_SECONDS) as response:
            return response.status, json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        try:
            body = json.loads(exc.read().decode("utf-8"))
        except (UnicodeError, json.JSONDecodeError):
            raise SmokeFailure("maintenance HTTP error response was not JSON") from None
        return exc.code, body
    except (OSError, urllib.error.URLError):
        raise SmokeFailure("maintenance HTTP request failed") from None


def _wait_for_http(port: int, process: subprocess.Popen[bytes]) -> None:
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise SmokeFailure("maintenance HTTP process exited before liveness")
        try:
            status, body = _request_json(port, "/health")
            if status == 200 and body == {"status": "alive"}:
                return
        except SmokeFailure:
            pass
        time.sleep(0.1)
    raise SmokeFailure("maintenance HTTP liveness timed out")


def _start_http(
    *,
    state: SmokeState,
    port: int,
    database_dsn: str,
    launcher_token: str,
    python_executable: Path | None,
    python_bootstrap: Path | None,
) -> None:
    environment = dict(os.environ)
    environment["GOOFISH_PORTABLE_DATABASE_URL"] = database_dsn
    environment["GOOFISH_LAUNCHER_TOKEN"] = launcher_token
    state.http_stdout = (state.root / "maintenance.stdout.log").open("wb")
    state.http_stderr = (state.root / "maintenance.stderr.log").open("wb")
    arguments = [
        str(python_executable or Path(sys.executable)),
        "-B",
    ]
    if python_bootstrap is not None:
        arguments.extend(
            [
                str(python_bootstrap),
                "--app-root",
                str(_REPOSITORY_ROOT),
                "--target",
                "maintenance",
                "--",
            ]
        )
    else:
        arguments.append(str(_REPOSITORY_ROOT / "portable_server.py"))
    arguments.extend(
        [
        "--mode",
        "maintenance",
        "--instance-id",
        "portable-pg-smoke",
        "--program-root",
        str(_REPOSITORY_ROOT),
        "--data-root",
        str(state.root),
        "--port",
        str(port),
        ]
    )
    try:
        state.http_process = subprocess.Popen(
            arguments,
            cwd=_REPOSITORY_ROOT,
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=state.http_stdout,
            stderr=state.http_stderr,
            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP,
        )
    except OSError:
        raise SmokeFailure("maintenance HTTP process could not be started") from None
    _wait_for_http(port, state.http_process)


def _stop_http(state: SmokeState) -> bool:
    process = state.http_process
    stopped = True
    if process is not None and process.poll() is None:
        try:
            process.send_signal(signal.CTRL_BREAK_EVENT)
            process.wait(timeout=10)
        except (OSError, subprocess.TimeoutExpired):
            # 只退出本脚本持有 Popen 句柄的确切子进程，不按名称查找。
            if process.poll() is None:
                try:
                    process.terminate()
                    process.wait(timeout=5)
                except (OSError, subprocess.TimeoutExpired):
                    stopped = False
    for stream_name in ("http_stdout", "http_stderr"):
        stream = getattr(state, stream_name)
        if stream is not None:
            try:
                stream.close()
            except OSError:
                stopped = False
    return stopped


def _stop_postgres(pg_ctl: Path, state: SmokeState) -> bool:
    if not state.postgres_start_attempted:
        return True
    try:
        identity = _read_owned_postmaster_identity(state)
    except SmokeFailure:
        try:
            status = subprocess.run(
                [str(pg_ctl), "status", "-D", str(state.data_root)],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=10,
                check=False,
            )
        except (OSError, subprocess.SubprocessError):
            return False
        # pg_ctl 官方状态码 3 才表示未运行；4 是数据目录不可访问。
        return status.returncode == 3
    if state.postgres_pid is None:
        # 启动工具可能在服务已创建 owned postmaster.pid 后超时；
        # 该 PGDATA 是本次在唯一临时目录中新建，可以在精确数据目录上安全停止。
        state.postgres_pid = identity.pid
    if identity.pid != state.postgres_pid:
        logger.error("Refusing to stop PostgreSQL because the owned PID changed")
        return False

    for _ in range(2):
        try:
            identity = _read_owned_postmaster_identity(state)
        except SmokeFailure:
            logger.error("Refusing to stop PostgreSQL because its identity became unverifiable")
            return False
        if identity.pid != state.postgres_pid:
            logger.error("Refusing to stop PostgreSQL because the owned PID changed")
            return False
        try:
            completed = subprocess.run(
                [str(pg_ctl), "stop", "-D", str(state.data_root), "-m", "fast", "-w", "-t", "20"],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=30,
                check=False,
            )
            status = subprocess.run(
                [str(pg_ctl), "status", "-D", str(state.data_root)],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=10,
                check=False,
            )
        except (OSError, subprocess.SubprocessError):
            logger.error("Owned PostgreSQL bounded stop/status command failed")
            return False
        if status.returncode == 3:
            state.postgres_start_attempted = False
            return True
        time.sleep(0.5)
    logger.error("Owned PostgreSQL cluster did not stop after bounded fast-stop attempts")
    return False


def _has_reparse_point(root: Path) -> bool:
    for candidate in (root, *root.rglob("*")):
        if candidate.is_symlink() or (hasattr(os.path, "isjunction") and os.path.isjunction(candidate)):
            return True
    return False


def _assert_repository_path_without_reparse(target: Path) -> None:
    repository = Path(os.path.abspath(_REPOSITORY_ROOT))
    candidate = Path(os.path.abspath(target))
    try:
        if os.path.commonpath((repository, candidate)) != str(repository):
            raise SmokeFailure("path is outside the repository boundary")
    except ValueError:
        raise SmokeFailure("path is outside the repository boundary") from None

    current = repository
    for part in candidate.relative_to(repository).parts:
        current = current / part
        if not current.exists():
            continue
        if current.is_symlink() or (hasattr(os.path, "isjunction") and os.path.isjunction(current)):
            raise SmokeFailure("repository test path contains a reparse point")


def _cleanup_owned_root(root: Path, parent: Path) -> bool:
    try:
        expected_parent = _REPOSITORY_ROOT / ".tmp" / "tests" / "portable-pg"
        if Path(os.path.abspath(parent)) != Path(os.path.abspath(expected_parent)):
            raise SmokeFailure("refusing to clean through an unexpected smoke-test parent")
        if Path(os.path.abspath(root)).parent != Path(os.path.abspath(parent)):
            raise SmokeFailure("refusing to clean an unexpected smoke-test path")
        _assert_repository_path_without_reparse(parent)
        _assert_repository_path_without_reparse(root)
        resolved_root = root.resolve(strict=True)
        resolved_parent = parent.resolve(strict=True)
        if resolved_root.parent != resolved_parent or not resolved_root.name.startswith("集成 冒烟-"):
            raise SmokeFailure("refusing to clean an unexpected smoke-test path")
        if _has_reparse_point(resolved_root):
            raise SmokeFailure("refusing to clean a smoke-test tree containing reparse points")
        shutil.rmtree(resolved_root)
        return True
    except (OSError, SmokeFailure):
        logger.error("Could not safely clean owned smoke-test root: %s", root)
        return False


def _require_success_cleanup(root: Path, parent: Path) -> None:
    if not _cleanup_owned_root(root, parent):
        raise SmokeFailure("owned smoke-test root could not be cleaned")


def _redact_owned_raw_secrets(root: Path, raw_secrets: Sequence[str]) -> bool:
    secret_bytes = [value.encode("utf-8") for value in raw_secrets if value]
    if not secret_bytes:
        return True
    try:
        if Path(os.path.abspath(root)).parent != Path(os.path.abspath(_TEST_PARENT)):
            return False
        _assert_repository_path_without_reparse(_TEST_PARENT)
        _assert_repository_path_without_reparse(root)
        resolved_root = root.resolve(strict=True)
        if resolved_root.parent != _TEST_PARENT.resolve(strict=True) or _has_reparse_point(resolved_root):
            return False
        for candidate in resolved_root.rglob("*"):
            if not candidate.is_file():
                continue
            content = candidate.read_bytes()
            updated = content
            for secret in secret_bytes:
                if secret in updated:
                    replacement = b"*" * len(secret)
                    updated = updated.replace(secret, replacement)
            if updated != content:
                candidate.write_bytes(updated)
        for candidate in resolved_root.rglob("*"):
            if candidate.is_file():
                content = candidate.read_bytes()
                if any(secret in content for secret in secret_bytes):
                    return False
        return True
    except OSError:
        return False


def _portable_python_paths(
    python_executable: Path | None, python_bootstrap: Path | None
) -> tuple[Path | None, Path | None]:
    if (python_executable is None) != (python_bootstrap is None):
        raise SmokeFailure("python-executable and python-bootstrap must be supplied together")
    if python_executable is None:
        return None, None
    if not python_executable.is_absolute() or not python_bootstrap.is_absolute():
        raise SmokeFailure("portable Python paths must be absolute")
    try:
        executable = python_executable.resolve(strict=True)
        bootstrap = python_bootstrap.resolve(strict=True)
    except OSError:
        raise SmokeFailure("portable Python executable or bootstrap could not be resolved") from None
    if not executable.is_file() or not bootstrap.is_file():
        raise SmokeFailure("portable Python executable or bootstrap is not a file")
    return executable, bootstrap


def run_smoke(
    postgres_root: Path | None,
    python_executable: Path | None = None,
    python_bootstrap: Path | None = None,
) -> dict[str, Any]:
    lock = _load_lock()
    runtime_root = _runtime_root(lock, postgres_root)
    postgres = runtime_root / "bin" / "postgres.exe"
    initdb = runtime_root / "bin" / "initdb.exe"
    pg_ctl = runtime_root / "bin" / "pg_ctl.exe"
    for executable in (postgres, initdb, pg_ctl):
        if not executable.is_file():
            raise SmokeFailure("prepared PostgreSQL runtime is incomplete")
    if os.environ.get("PGSERVICE", "").strip() or os.environ.get("PGSERVICEFILE", "").strip():
        raise SmokeFailure("libpq service environment is not allowed during portable smoke")
    portable_python, bootstrap = _portable_python_paths(python_executable, python_bootstrap)

    created_parents: list[Path] = []
    state: SmokeState | None = None
    success = False
    failure_category: str | None = None
    try:
        _assert_repository_path_without_reparse(_TEST_PARENT)
        for directory in (_REPOSITORY_ROOT / ".tmp", _REPOSITORY_ROOT / ".tmp" / "tests", _TEST_PARENT):
            if not directory.exists():
                directory.mkdir()
                created_parents.append(directory)
            _assert_repository_path_without_reparse(directory)
        free_before, total = _assert_disk_floor(_TEST_PARENT)
        root = Path(tempfile.mkdtemp(prefix="集成 冒烟-", dir=_TEST_PARENT))
        state = SmokeState(root=root, data_root=root / "pgdata", pg_log=root / "postgres.log")
        diagnostics = root / "diagnostics"
        diagnostics.mkdir()

        version = _run_tool(
            [postgres, "--version"],
            stage="postgres-version",
            diagnostic_root=diagnostics,
        ).stdout.strip()
        if version != f"postgres (PostgreSQL) {lock['version']}":
            raise SmokeFailure("prepared PostgreSQL runtime version does not match the lock")

        port = _reserve_loopback_port()
        http_port = _reserve_loopback_port()
        while http_port == port:
            http_port = _reserve_loopback_port()
        admin_user = f"pgadmin_{secrets.token_hex(4)}"
        probe_user = f"pgprobe_{secrets.token_hex(4)}"
        database = f"smoke_{secrets.token_hex(4)}"
        admin_password = secrets.token_urlsafe(32)
        probe_password = secrets.token_urlsafe(32)
        launcher_token = secrets.token_urlsafe(32)
        state.raw_secrets.extend((admin_password, probe_password, launcher_token))

        password_file = root / "initdb-password.txt"
        try:
            password_file.touch(exist_ok=False)
            _restrict_secret_file(password_file)
            password_file.write_text(admin_password + "\n", encoding="utf-8")
            _run_tool(
                [
                    initdb,
                    "-D",
                    state.data_root,
                    "--username",
                    admin_user,
                    "--pwfile",
                    password_file,
                    "--encoding=UTF8",
                    "--locale=C",
                    "--auth-local=scram-sha-256",
                    "--auth-host=scram-sha-256",
                    "--no-instructions",
                ],
                stage="initdb",
                diagnostic_root=diagnostics,
                timeout=60,
            )
        finally:
            if password_file.exists():
                try:
                    password_file.unlink()
                except OSError:
                    raise SmokeFailure("initdb password file could not be removed") from None

        _assert_scram_hba(state.data_root)
        _append_server_configuration(state.data_root, port)
        state.postgres_start_attempted = True
        state.postgres_port = port
        state.postgres_start_epoch = time.time()
        _run_tool(
            [
                pg_ctl,
                "start",
                "-D",
                state.data_root,
                "-l",
                state.pg_log,
                "-o",
                f"-p {port}",
                "-w",
                "-t",
                "30",
            ],
            stage="pg-start",
            diagnostic_root=diagnostics,
            timeout=45,
            direct_output=True,
        )
        state.postgres_pid = _read_owned_postmaster_identity(state).pid

        _initialize_roles_and_database(
            state=state,
            port=port,
            admin_user=admin_user,
            admin_password=admin_password,
            probe_user=probe_user,
            probe_password=probe_password,
            database=database,
        )
        probe_dsn = _dsn(
            port=port,
            database=database,
            user=probe_user,
            password=probe_password,
        )
        state.raw_secrets.append(probe_dsn)
        target = validate_database_target(probe_dsn, environ={})
        direct_probe = PostgresReadinessProbe(target)
        uninitialized = direct_probe.check()
        if (
            uninitialized.database_status != "available"
            or uninitialized.schema_status != "uninitialized"
            or uninitialized.failure_reason != "schema_uninitialized"
        ):
            raise SmokeFailure("direct probe did not distinguish an uninitialized schema")
        _set_schema_fixture(
            port=port,
            admin_user=admin_user,
            admin_password=admin_password,
            probe_user=probe_user,
            database=database,
            present=True,
        )
        compatible = direct_probe.check()
        if not compatible.ready or compatible.schema_version != 1:
            raise SmokeFailure("direct probe did not accept the compatible schema fixture")
        _set_schema_fixture(
            port=port,
            admin_user=admin_user,
            admin_password=admin_password,
            probe_user=probe_user,
            database=database,
            present=False,
        )

        _start_http(
            state=state,
            port=http_port,
            database_dsn=probe_dsn,
            launcher_token=launcher_token,
            python_executable=portable_python,
            python_bootstrap=bootstrap,
        )
        log_offset = state.pg_log.stat().st_size
        unauthorized_status, _ = _request_json(http_port, "/internal/ready")
        bad_token_status, _ = _request_json(http_port, "/internal/ready", "incorrect-token")
        if unauthorized_status != 401 or bad_token_status != 401:
            raise SmokeFailure("maintenance HTTP authentication boundary failed")
        time.sleep(0.2)
        with state.pg_log.open("rb") as stream:
            stream.seek(log_offset)
            unauthenticated_log_tail = stream.read().lower()
        if b"connection received" in unauthenticated_log_tail:
            raise SmokeFailure("unauthenticated HTTP readiness unexpectedly opened a database connection")

        missing_status, missing_body = _request_json(http_port, "/internal/ready", launcher_token)
        if (
            missing_status != 503
            or missing_body.get("failure_reason") != "schema_uninitialized"
            or missing_body.get("database", {}).get("status") != "available"
        ):
            raise SmokeFailure("maintenance HTTP did not report the missing schema fixture")
        _set_schema_fixture(
            port=port,
            admin_user=admin_user,
            admin_password=admin_password,
            probe_user=probe_user,
            database=database,
            present=True,
        )
        ready_status, ready_body = _request_json(http_port, "/internal/ready", launcher_token)
        if (
            ready_status != 200
            or ready_body.get("ready") is not True
            or ready_body.get("protocol_version") != 1
            or ready_body.get("instance_id") != "portable-pg-smoke"
            or ready_body.get("mode") != "maintenance"
            or ready_body.get("schema", {}).get("version") != 1
        ):
            raise SmokeFailure("maintenance HTTP compatible readiness identity failed")

        shutdown_status, _ = _request_json(
            http_port, "/internal/shutdown", launcher_token,
            method="POST", instance_id="portable-pg-smoke",
        )
        if shutdown_status != 202:
            raise SmokeFailure("maintenance graceful shutdown was not accepted")
        try:
            exit_code = state.http_process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            raise SmokeFailure("maintenance did not exit after authenticated shutdown") from None
        if exit_code != 0:
            raise SmokeFailure("maintenance graceful shutdown returned a nonzero exit code")

        success = True
        free_after, _ = _assert_disk_floor(_TEST_PARENT)
        return {
            "postgres_version": lock["version"],
            "postgres_root": str(runtime_root),
            "server_encoding": "UTF8",
            "locale": "C",
            "authentication": "scram-sha-256",
            "listen_address": "127.0.0.1",
            "direct_probe": ["schema_uninitialized", "compatible"],
            "http_statuses": [401, 401, 503, 200],
            "http_shutdown": "authenticated_202_then_exit_0",
            "free_bytes_before": free_before,
            "free_bytes_after": free_after,
            "total_bytes": total,
        }
    except Exception as exc:
        failure_category = type(exc).__name__
        raise
    finally:
        cleanup_allowed = True
        if state is not None:
            if not _stop_http(state):
                logger.error("Owned maintenance HTTP process did not stop")
                cleanup_allowed = False
            if not _stop_postgres(pg_ctl, state):
                cleanup_allowed = False
            if not success:
                try:
                    (state.root / "FAILURE.txt").write_text(
                        "portable_pg_smoke failed\n"
                        f"category={failure_category or 'unknown'}\n"
                        "retention=up to 7 days\n"
                        "raw random test secrets are redacted before retention when all owned processes stop\n"
                        "the disposable PGDATA can still contain SCRAM credential verifiers\n",
                        encoding="utf-8",
                    )
                except OSError:
                    logger.error("Could not write sanitized smoke failure marker: %s", state.root)
            if cleanup_allowed and success:
                _require_success_cleanup(state.root, _TEST_PARENT)
            elif success:
                logger.error("Successful smoke diagnostics retained because an owned process may still be running: %s", state.root)
                raise SmokeFailure("owned smoke-test processes did not stop cleanly")
            else:
                if cleanup_allowed and _redact_owned_raw_secrets(state.root, state.raw_secrets):
                    logger.error("Redacted failure diagnostics retained at: %s", state.root)
                elif cleanup_allowed and _cleanup_owned_root(state.root, _TEST_PARENT):
                    logger.error("Failure diagnostics were removed because raw secrets could not be safely redacted")
                else:
                    logger.error(
                        "Failure root could not be redacted while an owned process may still run; manual review required: %s",
                        state.root,
                    )

        for directory in reversed(created_parents):
            try:
                directory.rmdir()
            except OSError as exc:
                logger.warning("Could not remove newly created empty parent %s: %s", directory, exc)


def main(argv: Sequence[str] | None = None) -> int:
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")
    args = _parser().parse_args(argv)
    try:
        result = run_smoke(args.postgres_root, args.python_executable, args.python_bootstrap)
    except Exception as exc:
        if isinstance(exc, SmokeFailure):
            logger.error("PORTABLE_PG_SMOKE=FAILED stage=%s", exc)
        else:
            logger.error("PORTABLE_PG_SMOKE=FAILED stage=unexpected_%s", type(exc).__name__)
        return 1
    print("PORTABLE_PG_SMOKE=PASS")
    print(json.dumps(result, ensure_ascii=False, sort_keys=True))
    return 0


class PortablePgSafetyTests(unittest.TestCase):
    def test_http_requests_disable_environment_proxy(self):
        class _Response:
            status = 200

            def __enter__(self):
                return self

            def __exit__(self, exc_type, exc_value, traceback):
                return False

            def read(self):
                return b'{"status":"alive"}'

        class _Opener:
            def __init__(self):
                self.request = None

            def open(self, request, timeout):
                self.request = request
                return _Response()

        opener = _Opener()
        with mock.patch("urllib.request.build_opener", return_value=opener) as build_opener:
            status, body = _request_json(54321, "/health", "local-token")
        self.assertEqual(status, 200)
        self.assertEqual(body, {"status": "alive"})
        proxy_handler = build_opener.call_args.args[0]
        self.assertIsInstance(proxy_handler, urllib.request.ProxyHandler)
        self.assertEqual(proxy_handler.proxies, {})
        self.assertEqual(opener.request.full_url, "http://127.0.0.1:54321/health")
        self.assertEqual(opener.request.get_header("Authorization"), "Bearer local-token")

    def test_start_timeout_with_owned_identity_is_stopped(self):
        state = SmokeState(
            root=Path("F:/tmp/owned"),
            data_root=Path("F:/tmp/owned/pgdata"),
            pg_log=Path("F:/tmp/owned/postgres.log"),
            postgres_start_attempted=True,
            postgres_port=54321,
            postgres_start_epoch=100.0,
        )
        identity = PostmasterIdentity(1234, state.data_root, 100, 54321)
        results = [subprocess.CompletedProcess([], 0), subprocess.CompletedProcess([], 3)]
        with mock.patch.object(sys.modules[__name__], "_read_owned_postmaster_identity", return_value=identity):
            with mock.patch("subprocess.run", side_effect=results):
                self.assertTrue(_stop_postgres(Path("pg_ctl.exe"), state))
        self.assertEqual(state.postgres_pid, 1234)

    def test_status_four_is_not_treated_as_stopped(self):
        state = SmokeState(
            root=Path("F:/tmp/owned"),
            data_root=Path("F:/tmp/owned/pgdata"),
            pg_log=Path("F:/tmp/owned/postgres.log"),
            postgres_start_attempted=True,
        )
        with mock.patch.object(
            sys.modules[__name__],
            "_read_owned_postmaster_identity",
            side_effect=SmokeFailure("unverifiable"),
        ):
            with mock.patch("subprocess.run", return_value=subprocess.CompletedProcess([], 4)):
                self.assertFalse(_stop_postgres(Path("pg_ctl.exe"), state))

    def test_cleanup_failure_makes_success_invalid(self):
        with mock.patch.object(sys.modules[__name__], "_cleanup_owned_root", return_value=False):
            with self.assertRaisesRegex(SmokeFailure, "could not be cleaned"):
                _require_success_cleanup(Path("F:/tmp/owned"), Path("F:/tmp"))


if __name__ == "__main__":
    raise SystemExit(main())
