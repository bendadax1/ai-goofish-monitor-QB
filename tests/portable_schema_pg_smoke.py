"""初始 schema 的真实、隔离 PostgreSQL 冒烟。

复用 ``portable_pg_smoke`` 已验证的 Windows runtime、隔离目录和受控停机
边界。它不接触项目 .env、既有 PGDATA、业务 Web、抓取、AI 或通知。
"""

from __future__ import annotations

import argparse
import json
import hashlib
import os
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile
import threading
from types import ModuleType
from typing import Any, Sequence
import uuid
from unittest import mock

import psycopg2
from psycopg2 import sql

from src.portable import schema
from src.portable.schema_catalog import V2_TABLES
from src.portable.upstream_ddl import CREATE_STATEMENTS, MIGRATION_CHECKSUM, MIGRATION_ID, ddl_checksum
from src.portable.schema_fingerprint import public_schema_fingerprint
from src.portable.schema_catalog import V1_FINGERPRINT, V2_FINGERPRINT
from src.portable.upstream_migration import UpstreamMigrationError, migrate_upstream_schema
from tests import portable_pg_smoke as pg


def _database_name() -> str:
    return f"schema_{secrets.token_hex(5)}"


def _dsn(*, port: int, database: str, user: str, password: str) -> str:
    return pg._dsn(port=port, database=database, user=user, password=password)


def _create_role_and_database(
    *, port: int, admin: str, password: str, app: str, probe: str, database: str, raw_secrets: list[str] | None = None
) -> tuple[str, str]:
    app_password = secrets.token_urlsafe(28)
    probe_password = secrets.token_urlsafe(28)
    if raw_secrets is not None:
        raw_secrets.extend((app_password, probe_password))
    connection = pg._connect(port=port, database="postgres", user=admin, password=password)
    try:
        connection.autocommit = True
        with connection.cursor() as cursor:
            for role, role_password in ((app, app_password), (probe, probe_password)):
                cursor.execute("SELECT 1 FROM pg_roles WHERE rolname = %s", (role,))
                if cursor.fetchone() is None:
                    cursor.execute(
                        sql.SQL(
                            "CREATE ROLE {} LOGIN PASSWORD %s NOSUPERUSER NOCREATEDB "
                            "NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS"
                        ).format(sql.Identifier(role)),
                        (role_password,),
                    )
            cursor.execute(sql.SQL("CREATE DATABASE {} OWNER {}").format(sql.Identifier(database), sql.Identifier(admin)))
    finally:
        connection.close()
    return app_password, probe_password


def _admin_engine(*, port: int, database: str, user: str, password: str):
    return schema._engine_from_environment(
        {schema.ADMIN_DATABASE_ENVIRONMENT_VARIABLE: _dsn(port=port, database=database, user=user, password=password)}
    )


def _query(port: int, database: str, user: str, password: str, statement: str, parameters: tuple[Any, ...] = ()):
    connection = pg._connect(port=port, database=database, user=user, password=password)
    try:
        with connection.cursor() as cursor:
            cursor.execute(statement, parameters)
            result = cursor.fetchall() if cursor.description is not None else []
        connection.commit()
        return result
    finally:
        connection.close()


def _assert_initialized(
    *, port: int, database: str, admin: str, password: str, app: str, app_password: str, probe: str, probe_password: str
) -> None:
    tables = _query(
        port,
        database,
        admin,
        password,
        "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename",
    )
    expected = sorted((*schema._application_table_names(), schema.VERSION_TABLE_NAME))
    if [row[0] for row in tables] != expected:
        raise pg.SmokeFailure("schema initializer did not create the exact P1 table set")
    if _query(port, database, admin, password, "SELECT version FROM public.app_schema_version") != [(1,)]:
        raise pg.SmokeFailure("schema version 1 was not recorded")
    if _query(port, database, admin, password, "SELECT count(*) FROM public.users") != [(0,)]:
        raise pg.SmokeFailure("schema initializer created an unexpected user")

    _query(
        port, database, app, app_password,
        "INSERT INTO public.users (id, username, password_hash, role, is_active) "
        "VALUES (%s, 'dml_probe', 'x', 'operator', true)",
        (str(uuid.uuid4()),),
    )
    _query(port, database, admin, password, "DELETE FROM public.users WHERE username = 'dml_probe'")
    try:
        _query(port, database, app, app_password, "CREATE TABLE public.forbidden_ddl (id integer)")
    except psycopg2.errors.InsufficientPrivilege:
        pass
    else:
        raise pg.SmokeFailure("app role can create database tables")
    try:
        _query(port, database, probe, probe_password, "SELECT id FROM public.users")
    except psycopg2.errors.InsufficientPrivilege:
        pass
    else:
        raise pg.SmokeFailure("probe role can read business users")
    if _query(port, database, probe, probe_password, "SELECT version FROM public.app_schema_version") != [(1,)]:
        raise pg.SmokeFailure("probe role cannot read schema version")


def _check_real_prompt_route_ownership(
    *, port: int, database: str, admin: str, password: str,
    app: str, app_password: str, first_owner: str,
) -> None:
    """用真实 PG 适配器和 FastAPI 路由核对私有 Prompt 的所有权。"""
    # 不读取项目 .env：适配器只需要这些导入期符号，本用例不执行配置初始化。
    config = ModuleType("src.config")
    config.PORTABLE_MODE = True
    config.WEB_USERNAME = lambda: "synthetic"
    config.WEB_PASSWORD = lambda: "synthetic"
    with mock.patch.dict(sys.modules, {"src.config": config}):
        from src.storage.postgres_adapter import PostgresAdapter
        from tests.test_upstream_file_safety import load_settings

    second_owner = str(uuid.uuid4())
    suffix = uuid.uuid4().hex
    private_name = f"跨用户验收-{suffix}.txt"
    shared_name = f"系统验收-{suffix}.txt"
    _query(
        port, database, admin, password,
        "INSERT INTO public.users (id, username, password_hash, role, is_active) "
        "VALUES (%s, %s, 'synthetic-fixture', 'operator', true)",
        (second_owner, f"pg_file_{suffix}"),
    )
    storage = PostgresAdapter(_dsn(port=port, database=database, user=app, password=app_password))
    current_user = [{"id": first_owner, "categories": ["ai"]}]
    _, client = load_settings(["postgres"], storage, current_user)

    def status(method: str, url: str, expected: int, **kwargs):
        response = client.request(method, url, **kwargs)
        if response.status_code != expected:
            raise pg.SmokeFailure(f"real Prompt route returned {response.status_code}, expected {expected}")
        return response

    try:
        current_user[0] = None
        status("POST", "/api/prompts", 401, json={"filename": private_name, "content": "blocked"})
        current_user[0] = {"id": first_owner, "categories": []}
        status("POST", "/api/prompts", 403, json={"filename": private_name, "content": "blocked"})
        current_user[0] = {"id": first_owner, "categories": ["ai"]}
        status("POST", "/api/prompts", 200, json={"filename": private_name, "content": "用户甲初版", "owner_id": second_owner})
        assert status("GET", f"/api/prompts/{private_name}", 200).json()["content"] == "用户甲初版"
        assert private_name in status("GET", "/api/prompts", 200).json()
        if _query(port, database, admin, password,
                  "SELECT owner_id::text, content FROM public.prompt_templates WHERE name = %s",
                  (private_name,)) != [(first_owner, "用户甲初版")]:
            raise pg.SmokeFailure("real Prompt create ignored authenticated owner")

        current_user[0] = {"id": second_owner, "categories": ["ai"]}
        assert private_name not in status("GET", "/api/prompts", 200).json()
        status("GET", f"/api/prompts/{private_name}", 404)
        status("PUT", f"/api/prompts/{private_name}", 404, json={"content": "越权改写"})
        status("DELETE", f"/api/prompts/{private_name}", 404)
        status("POST", "/api/prompts", 200, json={"filename": private_name, "content": "用户乙初版"})
        assert status("GET", f"/api/prompts/{private_name}", 200).json()["content"] == "用户乙初版"

        current_user[0] = {"id": first_owner, "categories": ["ai"]}
        status("PUT", f"/api/prompts/{private_name}", 200, json={"content": "用户甲新版"})
        assert status("GET", f"/api/prompts/{private_name}", 200).json()["content"] == "用户甲新版"
        for name in ("CON.txt", "x.txt:stream", "../escape.txt"):
            status("POST", "/api/prompts", 400, json={"filename": name, "content": "blocked"})
        storage.save_prompt_template({"name": shared_name, "content": "共享只读"}, owner_id=None)
        status("DELETE", f"/api/prompts/{shared_name}", 400)
        assert status("GET", f"/api/prompts/{shared_name}", 200).json()["content"] == "共享只读"
        status("DELETE", f"/api/prompts/{private_name}", 200)
        status("GET", f"/api/prompts/{private_name}", 404)

        current_user[0] = {"id": second_owner, "categories": ["ai"]}
        assert status("GET", f"/api/prompts/{private_name}", 200).json()["content"] == "用户乙初版"
        status("DELETE", f"/api/prompts/{private_name}", 200)
        if _query(port, database, admin, password,
                  "SELECT count(*) FROM public.prompt_templates WHERE name = %s",
                  (private_name,)) != [(0,)]:
            raise pg.SmokeFailure("real Prompt owner deletes left a private row")
    finally:
        client.close()
        storage.engine.dispose()
        _query(port, database, admin, password,
               "DELETE FROM public.prompt_templates WHERE name IN (%s, %s)",
               (private_name, shared_name))
        _query(port, database, admin, password,
               "DELETE FROM public.users WHERE id = %s", (second_owner,))


def _run_schema_cases(
    *, port: int, admin: str, password: str, app: str, probe: str,
    raw_secrets: list[str], test_root: Path, prompt_only: bool = False,
) -> None:
    database = _database_name()
    app_password, probe_password = _create_role_and_database(port=port, admin=admin, password=password, app=app, probe=probe, database=database, raw_secrets=raw_secrets)
    engine = _admin_engine(port=port, database=database, user=admin, password=password)
    roles = schema.DatabaseRoles(admin=admin, app=app, probe=probe)
    try:
        if schema.initialize_schema(engine, roles=roles) != 1:
            raise pg.SmokeFailure("schema initializer did not return version 1")
        _assert_initialized(port=port, database=database, admin=admin, password=password, app=app, app_password=app_password, probe=probe, probe_password=probe_password)
        try:
            schema.initialize_schema(engine, roles=roles)
        except schema.SchemaAlreadyInitializedError:
            pass
        else:
            raise pg.SmokeFailure("second schema initialization was accepted")

        app_engine = _admin_engine(port=port, database=database, user=app, password=app_password)
        try:
            identifier = schema.create_first_admin(
                app_engine,
                username="portable_admin",
                password="StrongPassword1!",
                password_hasher=lambda value: "hash:" + value,
            )
        finally:
            app_engine.dispose()
        if not identifier or _query(port, database, admin, password, "SELECT count(*) FROM public.users") != [(1,)]:
            raise pg.SmokeFailure("first administrator was not created exactly once")
        _check_real_prompt_route_ownership(
            port=port, database=database, admin=admin, password=password,
            app=app, app_password=app_password, first_owner=identifier,
        )
        if prompt_only:
            return
        _check_real_sessions(port, database, app, app_password, test_root, raw_secrets)
        try:
            schema.create_first_admin(
                engine, username="second_admin", password="StrongPassword1!", password_hasher=lambda value: value
            )
        except schema.FirstAdminError:
            pass
        else:
            raise pg.SmokeFailure("second first-admin creation was accepted")
        concurrent_errors: list[Exception] = []

        def concurrent_attempt() -> None:
            try:
                schema.create_first_admin(
                    engine,
                    username="race_admin",
                    password="StrongPassword1!",
                    password_hasher=lambda value: value,
                )
            except Exception as exc:
                concurrent_errors.append(exc)

        workers = [threading.Thread(target=concurrent_attempt) for _ in range(2)]
        for worker in workers:
            worker.start()
        for worker in workers:
            worker.join(timeout=10)
        if len(concurrent_errors) != 2 or not all(isinstance(error, schema.FirstAdminError) for error in concurrent_errors):
            raise pg.SmokeFailure("concurrent first-admin attempts were not both rejected")
        if _query(port, database, admin, password, "SELECT count(*) FROM public.users") != [(1,)]:
            raise pg.SmokeFailure("concurrent first-admin attempts changed user cardinality")
    finally:
        engine.dispose()

    rollback = _database_name()
    _create_role_and_database(port=port, admin=admin, password=password, app=app, probe=probe, database=rollback, raw_secrets=raw_secrets)
    engine = _admin_engine(port=port, database=rollback, user=admin, password=password)
    original_metadata = schema._portable_metadata
    def create_then_fail(**kwargs):
        original_metadata().create_all(**kwargs)
        raise RuntimeError("injected after model DDL")

    schema._portable_metadata = lambda: type(
        "FailingMetadata", (), {"create_all": staticmethod(create_then_fail)}
    )()
    try:
        try:
            schema.initialize_schema(engine)
        except schema.PortableSchemaError:
            pass
        else:
            raise pg.SmokeFailure("injected initialization failure was accepted")
    finally:
        schema._portable_metadata = original_metadata
        engine.dispose()
    if _query(port, rollback, admin, password, "SELECT to_regclass('public.app_schema_version')") != [(None,)]:
        raise pg.SmokeFailure("failed initialization did not roll back version DDL")
    if _query(port, rollback, admin, password, "SELECT count(*) FROM pg_tables WHERE schemaname = 'public'") != [(0,)]:
        raise pg.SmokeFailure("failed initialization left model tables behind")

    race_database = _database_name()
    _create_role_and_database(port=port, admin=admin, password=password, app=app, probe=probe, database=race_database, raw_secrets=raw_secrets)
    engine = _admin_engine(port=port, database=race_database, user=admin, password=password)
    try:
        schema.initialize_schema(engine)
        barrier = threading.Barrier(2)
        outcomes = []
        def race_empty_users():
            barrier.wait(timeout=10)
            try:
                schema.create_first_admin(
                    engine, username="race_admin", password="StrongPassword1!",
                    password_hasher=lambda _: "fixture-hash",
                )
                outcomes.append("created")
            except schema.FirstAdminError:
                outcomes.append("rejected")
        workers = [threading.Thread(target=race_empty_users) for _ in range(2)]
        for worker in workers:
            worker.start()
        for worker in workers:
            worker.join(timeout=40)
        if any(worker.is_alive() for worker in workers) or sorted(outcomes) != ["created", "rejected"]:
            raise pg.SmokeFailure("concurrent empty-table bootstrap did not serialize")
        if _query(port, race_database, admin, password, "SELECT count(*) FROM public.users") != [(1,)]:
            raise pg.SmokeFailure("concurrent bootstrap created multiple administrators")
    finally:
        engine.dispose()

    admin_rollback = _database_name()
    _create_role_and_database(port=port, admin=admin, password=password, app=app, probe=probe, database=admin_rollback, raw_secrets=raw_secrets)
    engine = _admin_engine(port=port, database=admin_rollback, user=admin, password=password)
    try:
        schema.initialize_schema(engine)
    finally:
        engine.dispose()
    _query(
        port,
        admin_rollback,
        admin,
        password,
        "ALTER TABLE public.user_group_members ADD CONSTRAINT injected_admin_failure CHECK (false) NOT VALID",
    )
    engine = _admin_engine(port=port, database=admin_rollback, user=admin, password=password)
    try:
        try:
            schema.create_first_admin(
                engine, username="rollback_admin", password="StrongPassword1!", password_hasher=lambda value: value
            )
        except schema.FirstAdminError:
            pass
        else:
            raise pg.SmokeFailure("injected first-admin failure was accepted")
    finally:
        engine.dispose()
    if _query(port, admin_rollback, admin, password, "SELECT count(*) FROM public.users") != [(0,)]:
        raise pg.SmokeFailure("failed first-admin transaction left a user")
    if _query(port, admin_rollback, admin, password, "SELECT count(*) FROM public.user_groups") != [(0,)]:
        raise pg.SmokeFailure("failed first-admin transaction left a group")

    # Unknown objects and a malformed version table are rejection-only paths: no
    # initializer DDL is allowed to repair or overwrite them.
    unknown = _database_name()
    _create_role_and_database(port=port, admin=admin, password=password, app=app, probe=probe, database=unknown, raw_secrets=raw_secrets)
    _query(port, unknown, admin, password, "CREATE TABLE public.unrecognized_schema_object (id integer)")
    engine = _admin_engine(port=port, database=unknown, user=admin, password=password)
    try:
        try:
            schema.initialize_schema(engine)
        except schema.SchemaNotEmptyError:
            pass
        else:
            raise pg.SmokeFailure("unknown nonempty schema was accepted")
        if _query(port, unknown, admin, password, "SELECT to_regclass('public.app_schema_version')") != [(None,)]:
            raise pg.SmokeFailure("unknown schema rejection modified version state")
    finally:
        engine.dispose()

    damaged = _database_name()
    _create_role_and_database(port=port, admin=admin, password=password, app=app, probe=probe, database=damaged, raw_secrets=raw_secrets)
    engine = _admin_engine(port=port, database=damaged, user=admin, password=password)
    try:
        with engine.begin() as connection:
            schema._portable_metadata().create_all(bind=connection, checkfirst=False)
            connection.execute(schema.text("CREATE TABLE public.app_schema_version (version integer)"))
            connection.execute(schema.text("INSERT INTO public.app_schema_version (version) VALUES (1), (1)"))
        try:
            schema.initialize_schema(engine)
        except schema.SchemaInvalidError:
            pass
        else:
            raise pg.SmokeFailure("damaged version schema was accepted")
    finally:
        engine.dispose()
    if _query(port, damaged, admin, password, "SELECT count(*) FROM public.app_schema_version") != [(2,)]:
        raise pg.SmokeFailure("damaged version rejection modified existing data")


    cli_database = _database_name()
    _create_role_and_database(port=port, admin=admin, password=password, app=app, probe=probe, database=cli_database, raw_secrets=raw_secrets)
    allowed_environment = {name: os.environ[name] for name in ("COMSPEC", "PATH", "SYSTEMROOT", "WINDIR") if name in os.environ}
    allowed_environment[schema.ADMIN_DATABASE_ENVIRONMENT_VARIABLE] = _dsn(
        port=port, database=cli_database, user=admin, password=password
    )
    command = [
        sys.executable,
        "-B",
        str(_schema_program_fixture(test_root) / "portable_schema.py"),
        "--admin-role",
        admin,
        "--app-role",
        app,
        "--probe-role",
        probe,
    ]
    completed = subprocess.run(
        command,
        cwd=test_root,
        env=allowed_environment,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=30,
        check=False,
    )
    if completed.returncode != 0 or "initialized at version 1" not in completed.stdout:
        raise pg.SmokeFailure("schema CLI did not initialize from its environment-only admin DSN")

    web_database = _database_name()
    web_app, web_probe = f"web_app_{secrets.token_hex(4)}", f"web_probe_{secrets.token_hex(4)}"
    web_app_password, web_probe_password = _create_role_and_database(
        port=port, admin=admin, password=password, app=web_app, probe=web_probe,
        database=web_database, raw_secrets=raw_secrets,
    )
    engine = _admin_engine(port=port, database=web_database, user=admin, password=password)
    try:
        schema.initialize_schema(engine, roles=schema.DatabaseRoles(admin, web_app, web_probe))
    finally:
        engine.dispose()
    _check_real_web(port, web_database, web_app, web_app_password, web_probe, web_probe_password, test_root, raw_secrets)


def _check_v2_ddl_case(*, port: int, admin: str, password: str, app: str, probe: str,
                       raw_secrets: list[str], instance_id: str,
                       fingerprint_only: bool = False) -> None:
    """独立 v1 库中解析固定 DDL，事务回滚后核对 v1 完整保留。"""
    ddl_database = _database_name()
    app_password, probe_password = _create_role_and_database(
        port=port, admin=admin, password=password, app=app, probe=probe,
        database=ddl_database, raw_secrets=raw_secrets)
    engine = _admin_engine(port=port, database=ddl_database, user=admin, password=password)
    class _ExpectedDdlRollback(Exception):
        pass
    try:
        roles = schema.DatabaseRoles(admin, app, probe)
        schema.initialize_schema(engine, roles=roles)
        if ddl_checksum() != MIGRATION_CHECKSUM:
            raise pg.SmokeFailure("v2 DDL checksum differs from frozen input")
        with engine.begin() as connection:
            connection.execute(schema.text('SET LOCAL search_path = pg_catalog, "public"'))
            v1_fingerprint = public_schema_fingerprint(connection)
        try:
            with engine.begin() as connection:
                connection.execute(schema.text('SET LOCAL search_path = pg_catalog, "public"'))
                for statement in CREATE_STATEMENTS:
                    connection.execute(schema.text(statement))
                connection.execute(schema.text(
                    'ALTER TABLE public.app_schema_version DROP CONSTRAINT app_schema_version_version_check'
                ))
                connection.execute(schema.text(
                    'ALTER TABLE public.app_schema_version ADD CONSTRAINT app_schema_version_version_check '
                    'CHECK (version IN (1, 2))'
                ))
                connection.execute(schema.text('UPDATE public.app_schema_version SET version = 2'))
                connection.execute(schema.text(
                    "INSERT INTO public.app_schema_migrations (migration_id, checksum) "
                    "VALUES ('002_upstream_features', :checksum)"
                ), {"checksum": MIGRATION_CHECKSUM})
                tables = {row[0] for row in connection.execute(schema.text(
                    "SELECT tablename FROM pg_tables WHERE schemaname = 'public'"
                )).fetchall()}
                if tables != V2_TABLES:
                    raise pg.SmokeFailure("v2 DDL did not create the exact table set")
                v2_fingerprint = public_schema_fingerprint(connection)
                raise _ExpectedDdlRollback()
        except _ExpectedDdlRollback:
            pass
    finally:
        engine.dispose()
    if _query(port, ddl_database, admin, password,
              "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename") != [
                  (name,) for name in sorted(schema._application_table_names() + (schema.VERSION_TABLE_NAME,))
              ]:
        raise pg.SmokeFailure("rolled-back v2 DDL altered the v1 database")
    if fingerprint_only:
        print(f"UPSTREAM_SCHEMA_FINGERPRINTS v1={v1_fingerprint} v2={v2_fingerprint}")
        return
    if (v1_fingerprint, v2_fingerprint) != (V1_FINGERPRINT, V2_FINGERPRINT):
        raise pg.SmokeFailure("frozen schema fingerprints differ from isolated PostgreSQL")
    identity = instance_id
    _query(port, ddl_database, admin, password,
           f'COMMENT ON DATABASE "{ddl_database}" IS %s', ("aigoofish-instance:" + identity,))
    pgdata = Path(_query(port, ddl_database, admin, password, "SHOW data_directory")[0][0])
    engine = _admin_engine(port=port, database=ddl_database, user=admin, password=password)
    try:
        checks = []
        def maintenance():
            checks.append("maintenance")
        def backup():
            checks.append("backup")
        result = migrate_upstream_schema(engine, roles, pgdata=pgdata, instance_id=identity,
                                         verify_maintenance=maintenance, verify_backup=backup)
        if (result.version, result.status) != (2, "applied") or checks != [
            "maintenance", "backup", "maintenance", "backup", "maintenance",
        ]:
            raise pg.SmokeFailure("v2 migration did not verify its safety inputs")
        repeat = migrate_upstream_schema(engine, roles, pgdata=pgdata, instance_id=identity,
                                         verify_maintenance=maintenance, verify_backup=backup)
        if (repeat.version, repeat.status) != (2, "already_applied"):
            raise pg.SmokeFailure("v2 migration was not idempotent")
        if _query(port, ddl_database, admin, password,
                  "SELECT version FROM public.app_schema_version") != [(2,)]:
            raise pg.SmokeFailure("v2 migration did not update version")
        try:
            migrate_upstream_schema(engine, roles, pgdata=pgdata.parent, instance_id=identity,
                                    verify_maintenance=maintenance, verify_backup=backup)
        except UpstreamMigrationError:
            pass
        else:
            raise pg.SmokeFailure("migration accepted the wrong PGDATA")
    finally:
        engine.dispose()
    if _query(port, ddl_database, app, app_password,
              "SELECT count(*) FROM public.result_hidden_items") != [(0,)]:
        raise pg.SmokeFailure("app cannot read v2 business tables")
    if _query(port, ddl_database, probe, probe_password,
              "SELECT version FROM public.app_schema_version") != [(2,)]:
        raise pg.SmokeFailure("probe cannot read v2 version")
    for role, role_password, statement in (
        (app, app_password, "CREATE TABLE public.forbidden_v2_ddl (id integer)"),
        (app, app_password, "SELECT migration_id FROM public.app_schema_migrations"),
        (probe, probe_password, "SELECT owner_id FROM public.result_hidden_items"),
    ):
        try:
            _query(port, ddl_database, role, role_password, statement)
        except psycopg2.errors.InsufficientPrivilege:
            pass
        else:
            raise pg.SmokeFailure("v2 database role exceeded its grant boundary")

    # 已升级结构的权限或审计行漂移也不能被当作幂等成功。
    _query(port, ddl_database, admin, password,
           f'GRANT SELECT ON public.app_schema_migrations TO "{app}"')
    engine = _admin_engine(port=port, database=ddl_database, user=admin, password=password)
    try:
        try:
            migrate_upstream_schema(engine, roles, pgdata=pgdata, instance_id=identity,
                                    verify_maintenance=lambda: None, verify_backup=lambda: None)
        except UpstreamMigrationError:
            pass
        else:
            raise pg.SmokeFailure("migration accepted elevated app audit privileges")
        _query(port, ddl_database, admin, password,
               f'REVOKE SELECT ON public.app_schema_migrations FROM "{app}"')
        _query(port, ddl_database, admin, password,
               "UPDATE public.app_schema_migrations SET checksum = %s",
               ("0" * 64,))
        try:
            migrate_upstream_schema(engine, roles, pgdata=pgdata, instance_id=identity,
                                    verify_maintenance=lambda: None, verify_backup=lambda: None)
        except UpstreamMigrationError:
            pass
        else:
            raise pg.SmokeFailure("migration accepted a changed audit checksum")
    finally:
        engine.dispose()

    def fresh_v1_database():
        name = _database_name()
        _create_role_and_database(port=port, admin=admin, password=password, app=app,
                                  probe=probe, database=name, raw_secrets=raw_secrets)
        target_engine = _admin_engine(port=port, database=name, user=admin, password=password)
        schema.initialize_schema(target_engine, roles=roles)
        target_identity = instance_id
        _query(port, name, admin, password, f'COMMENT ON DATABASE "{name}" IS %s',
               ("aigoofish-instance:" + target_identity,))
        return name, target_identity, target_engine

    # 未知对象必须在任何迁移 DDL 前拒绝，且不修补原库。
    unknown_name, unknown_id, unknown_engine = fresh_v1_database()
    try:
        _query(port, unknown_name, admin, password,
               "CREATE TABLE public.unknown_before_migration (id integer)")
        try:
            migrate_upstream_schema(unknown_engine, roles, pgdata=pgdata,
                                    instance_id=unknown_id,
                                    verify_maintenance=lambda: None, verify_backup=lambda: None)
        except UpstreamMigrationError:
            pass
        else:
            raise pg.SmokeFailure("migration accepted unknown v1 structure")
        if _query(port, unknown_name, admin, password,
                  "SELECT version FROM public.app_schema_version") != [(1,)]:
            raise pg.SmokeFailure("unknown schema rejection changed version")
    finally:
        unknown_engine.dispose()

    extra_name, extra_id, extra_engine = fresh_v1_database()
    try:
        _query(port, extra_name, admin, password,
               "CREATE SCHEMA unexpected_application_schema")
        try:
            migrate_upstream_schema(extra_engine, roles, pgdata=pgdata,
                                    instance_id=extra_id,
                                    verify_maintenance=lambda: None, verify_backup=lambda: None)
        except UpstreamMigrationError:
            pass
        else:
            raise pg.SmokeFailure("migration accepted an unknown application schema")
        if _query(port, extra_name, admin, password,
                  "SELECT version FROM public.app_schema_version") != [(1,)]:
            raise pg.SmokeFailure("unknown schema rejection changed version")
    finally:
        extra_engine.dispose()

    # 在建表途中注入 SQL 错误，全部空表和版本变更必须回滚。
    failed_name, failed_id, failed_engine = fresh_v1_database()
    try:
        from src.portable import upstream_migration
        with mock.patch.object(upstream_migration, "CREATE_STATEMENTS",
                               CREATE_STATEMENTS[:2] + ("INVALID INJECTED DDL",)):
            try:
                migrate_upstream_schema(failed_engine, roles, pgdata=pgdata,
                                        instance_id=failed_id,
                                        verify_maintenance=lambda: None, verify_backup=lambda: None)
            except UpstreamMigrationError:
                pass
            else:
                raise pg.SmokeFailure("injected migration failure was accepted")
        if _query(port, failed_name, admin, password,
                  "SELECT version FROM public.app_schema_version") != [(1,)]:
            raise pg.SmokeFailure("failed migration changed version")
        if _query(port, failed_name, admin, password,
                  "SELECT to_regclass('public.result_hidden_items')") != [(None,)]:
            raise pg.SmokeFailure("failed migration left new tables")
    finally:
        failed_engine.dispose()

    # 两个管理员连接竞争同一版本；事务 advisory lock 只能放行一次建表。
    race_name, race_id, race_engine = fresh_v1_database()
    try:
        barrier = threading.Barrier(2)
        outcomes = []
        def migrate_race():
            barrier.wait(timeout=10)
            try:
                result = migrate_upstream_schema(race_engine, roles, pgdata=pgdata,
                    instance_id=race_id, verify_maintenance=lambda: None,
                    verify_backup=lambda: None)
                outcomes.append(result.status)
            except Exception as error:
                outcomes.append(type(error).__name__)
        workers = [threading.Thread(target=migrate_race) for _ in range(2)]
        for worker in workers:
            worker.start()
        for worker in workers:
            worker.join(timeout=30)
        if any(worker.is_alive() for worker in workers) or sorted(outcomes) != ["already_applied", "applied"]:
            raise pg.SmokeFailure("concurrent v2 migrations did not serialize")
        if _query(port, race_name, admin, password,
                  "SELECT count(*) FROM public.app_schema_migrations") != [(1,)]:
            raise pg.SmokeFailure("concurrent migration duplicated audit record")
    finally:
        race_engine.dispose()
    fresh_database = _database_name()
    fresh_app, fresh_probe = f"fresh_app_{secrets.token_hex(4)}", f"fresh_probe_{secrets.token_hex(4)}"
    fresh_app_password, fresh_probe_password = _create_role_and_database(
        port=port, admin=admin, password=password, app=fresh_app, probe=fresh_probe,
        database=fresh_database, raw_secrets=raw_secrets,
    )
    fresh_engine = _admin_engine(port=port, database=fresh_database, user=admin, password=password)
    try:
        fresh_roles = schema.DatabaseRoles(admin, fresh_app, fresh_probe)
        if schema.initialize_schema(fresh_engine, roles=fresh_roles, target_version=2) != 2:
            raise pg.SmokeFailure("new database did not initialize schema v2")
        with fresh_engine.begin() as connection:
            connection.execute(schema.text('SET LOCAL search_path = pg_catalog, "public"'))
            if public_schema_fingerprint(connection) != V2_FINGERPRINT:
                raise pg.SmokeFailure("new schema v2 differs from migrated schema v2")
        schema.configure_role_grants(fresh_engine, fresh_roles)
        if _query(port, fresh_database, admin, password,
                  "SELECT migration_id, checksum FROM public.app_schema_migrations") != [
                      (MIGRATION_ID, MIGRATION_CHECKSUM)
                  ]:
            raise pg.SmokeFailure("new schema v2 migration audit differs")
        if _query(port, fresh_database, fresh_app, fresh_app_password,
                  "SELECT count(*) FROM public.price_observations") != [(0,)]:
            raise pg.SmokeFailure("new schema v2 app role cannot read business tables")
        if _query(port, fresh_database, fresh_probe, fresh_probe_password,
                  "SELECT version FROM public.app_schema_version") != [(2,)]:
            raise pg.SmokeFailure("new schema v2 probe cannot read version")
    finally:
        fresh_engine.dispose()
    print(f"UPSTREAM_SCHEMA_FINGERPRINTS v1={v1_fingerprint} v2={v2_fingerprint}")

def _check_real_sessions(port, database, app, app_password, root, raw_secrets):
    environment = {name: os.environ[name] for name in ("COMSPEC", "PATH", "SYSTEMROOT", "WINDIR") if name in os.environ}
    master = secrets.token_urlsafe(32)
    raw_secrets.extend((master, "TestSessionPassword7!"))
    environment.update({
        "GOOFISH_PORTABLE_MODE": "portable",
        "GOOFISH_PORTABLE_PROGRAM_ROOT": str(pg._REPOSITORY_ROOT / "src"),
        "GOOFISH_PORTABLE_DATA_ROOT": str(root / "session-data"),
        "GOOFISH_PORTABLE_CACHE_ROOT": str(root / "session-cache"),
        "GOOFISH_PORTABLE_DATABASE_URL": _dsn(port=port, database=database, user=app, password=app_password),
        "ENCRYPTION_MASTER_KEY": master,
        "TEMP": str(root), "TMP": str(root),
    })
    completed = subprocess.run(
        [sys.executable, "-I", "-B", str(pg._REPOSITORY_ROOT / "tests" / "portable_session_pg_case.py"), str(pg._REPOSITORY_ROOT)],
        cwd=root, env=environment, stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=30,
    )
    if completed.returncode != 0 or b"PORTABLE_DATABASE_SESSIONS=PASS" not in completed.stdout:
        diagnostic = completed.stderr.decode("utf-8", errors="replace")
        for value in raw_secrets:
            diagnostic = diagnostic.replace(value, "[REDACTED]")
        (root / "diagnostics" / "session-failure.txt").write_text(diagnostic[-4000:], encoding="utf-8")
        raise pg.SmokeFailure("real application-role session verification failed")


def _check_real_web(port, database, app, app_password, probe, probe_password, root, raw_secrets):
    environment = {name: os.environ[name] for name in ("COMSPEC", "PATH", "SYSTEMROOT", "WINDIR") if name in os.environ}
    control, setup, master, signing = (secrets.token_urlsafe(32) for _ in range(4))
    raw_secrets.extend((control, setup, master, signing, "87654321", "98765432", "23456789"))
    environment.update({
        "GOOFISH_PORTABLE_MODE": "portable",
        "GOOFISH_PORTABLE_PROGRAM_ROOT": str(root / "web-app"),
        "GOOFISH_PORTABLE_DATA_ROOT": str(root / "web-data"),
        "GOOFISH_PORTABLE_CACHE_ROOT": str(root / "web-cache"),
        "GOOFISH_PORTABLE_DATABASE_URL": _dsn(port=port, database=database, user=app, password=app_password),
        "GOOFISH_PORTABLE_PROBE_DATABASE_URL": _dsn(port=port, database=database, user=probe, password=probe_password),
        "GOOFISH_LAUNCHER_TOKEN": control, "GOOFISH_PORTABLE_SETUP_TOKEN": setup,
        "SECRET_KEY": signing, "ENCRYPTION_MASTER_KEY": master,
        "TEMP": str(root), "TMP": str(root),
    })
    completed = subprocess.run(
        [sys.executable, "-I", "-B", str(pg._REPOSITORY_ROOT / "tests" / "portable_web_pg_case.py"), str(pg._REPOSITORY_ROOT)],
        cwd=root, env=environment, stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=40,
    )
    if completed.returncode != 0 or b"PORTABLE_WEB_DATABASE_FLOW=PASS" not in completed.stdout:
        diagnostic = completed.stderr.decode("utf-8", errors="replace")
        for value in raw_secrets:
            diagnostic = diagnostic.replace(value, "[REDACTED]")
        (root / "diagnostics" / "web-failure.txt").write_text(diagnostic[-6000:], encoding="utf-8")
        raise pg.SmokeFailure("real portable Web bootstrap/login flow failed")


def _schema_program_fixture(test_root):
    program = test_root / "schema-app"
    if program.exists():
        return program
    sources = (
        "portable_schema.py", "src/__init__.py", "src/version.py", "src/logging_config.py", "src/log_formatters.py", "src/log_retention.py", "src/account_policy.py",
        "src/portable/__init__.py", "src/portable/schema.py", "src/portable/schema_catalog.py", "src/portable/seeds.py", "src/portable/maintenance.py",
        "src/storage/__init__.py", "src/storage/models.py",
    )
    for relative in sources:
        target = program / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes((pg._REPOSITORY_ROOT / relative).read_bytes())
    entries = []
    for relative, kind in (("prompts/base_prompt.txt", "prompt"), ("prompts/bayes/bayes_v1.json", "bayes")):
        result = subprocess.run(["git", "show", "HEAD:" + relative], cwd=pg._REPOSITORY_ROOT,
            capture_output=True, check=True, timeout=15)
        target = program / "defaults" / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(result.stdout)
        entries.append({"path":relative,"kind":kind,"size":len(result.stdout),"sha256":hashlib.sha256(result.stdout).hexdigest()})
    (program / "defaults/seed_manifest.json").write_text(json.dumps({"format_version":1,"files":entries}), encoding="utf-8")
    return program


def _check_provision(state, port, admin, password, runtime_root, export_fixture=None):
    from src.portable.provision import provision, ProvisionError
    identifier = str(uuid.uuid4())
    marker = state.data_root / ".aigoofish-cluster.json"
    marker.write_text(json.dumps({
        "format_version": 1, "instance_id": identifier,
        "cluster_id": str(uuid.uuid4()), "engine_version": "17.11",
    }), encoding="utf-8")
    app_password, probe_password = secrets.token_urlsafe(32), secrets.token_urlsafe(32)
    state.raw_secrets.extend((app_password, probe_password))
    environment = {
        "GOOFISH_PORTABLE_ADMIN_DATABASE_URL": _dsn(port=port, database="postgres", user=admin, password=password),
        "GOOFISH_PORTABLE_APP_DATABASE_PASSWORD": app_password,
        "GOOFISH_PORTABLE_PROBE_DATABASE_PASSWORD": probe_password,
    }
    defaults = _schema_program_fixture(state.root) / "defaults"
    result = provision(str(state.data_root), identifier, environ=environment, seed_root=defaults)
    if result["schema_version"] != 1:
        raise pg.SmokeFailure("new-instance provisioning did not initialize schema")
    _assert_initialized(port=port, database="aigoofish", admin=admin, password=password,
        app="aigoofish_app", app_password=app_password, probe="aigoofish_probe", probe_password=probe_password)
    if _query(port,"aigoofish",admin,password,"SELECT count(*) FROM public.user_groups") != [(4,)]:
        raise pg.SmokeFailure("system groups seed count mismatch")
    engine = _admin_engine(port=port,database="aigoofish",user="aigoofish_app",password=app_password)
    try:
        schema.create_first_admin(engine,username="seed_admin",password="StrongPassword1!",password_hasher=lambda _:"fixture")
    finally:
        engine.dispose()
    for table in ("prompt_templates","bayes_profiles"):
        if _query(port,"aigoofish",admin,password,f"SELECT count(*) FROM public.{table} WHERE owner_id IS NOT NULL") != [(1,)]:
            raise pg.SmokeFailure("first user did not receive seeded resources")
    samples = _query(port,"aigoofish",admin,password,"SELECT (owner_id IS NULL), count(*) FROM public.bayes_samples GROUP BY owner_id IS NULL")
    if samples and (len(samples) != 2 or samples[0][1] != samples[1][1]):
        raise pg.SmokeFailure("first user Bayes sample cardinality mismatch")
    try:
        provision(str(state.data_root), identifier, environ=environment)
    except ProvisionError:
        pass
    else:
        raise pg.SmokeFailure("provisioner replaced an existing database")
    if _query(port, "aigoofish", admin, password, "SELECT version FROM public.app_schema_version") != [(1,)]:
        raise pg.SmokeFailure("rejected provision changed existing schema")
    from tests.portable_backup_pg_case import check_backup_roundtrip
    check_backup_roundtrip(state, port, admin, password, identifier, runtime_root,
                           export_fixture=export_fixture)


def _check_v2_provision_and_backup(state, port, admin, password, runtime_root):
    from src.portable.provision import provision
    from tests.portable_backup_pg_case import check_backup_roundtrip

    identifier = str(uuid.uuid4())
    (state.data_root / ".aigoofish-cluster.json").write_text(json.dumps({
        "format_version": 1, "instance_id": identifier,
        "cluster_id": str(uuid.uuid4()), "engine_version": "17.11",
    }), encoding="utf-8")
    app_password, probe_password = secrets.token_urlsafe(32), secrets.token_urlsafe(32)
    state.raw_secrets.extend((app_password, probe_password))
    environment = {
        "GOOFISH_PORTABLE_ADMIN_DATABASE_URL": _dsn(port=port, database="postgres", user=admin, password=password),
        "GOOFISH_PORTABLE_APP_DATABASE_PASSWORD": app_password,
        "GOOFISH_PORTABLE_PROBE_DATABASE_PASSWORD": probe_password,
    }
    defaults = _schema_program_fixture(state.root) / "defaults"
    result = provision(str(state.data_root), identifier, environ=environment,
                       seed_root=defaults, schema_version=2)
    if result["schema_version"] != 2:
        raise pg.SmokeFailure("new instance did not provision schema v2")
    _check_real_web(port, "aigoofish", "aigoofish_app", app_password,
                    "aigoofish_probe", probe_password, state.root, state.raw_secrets)
    owners = (str(uuid.uuid4()), str(uuid.uuid4()))
    for index, owner in enumerate(owners):
        _query(port, "aigoofish", admin, password,
               "INSERT INTO public.users (id, username, password_hash, role, is_active) "
               "VALUES (%s, %s, 'synthetic-fixture', 'operator', true)",
               (owner, f"v2_owner_{index}"))
        _query(port, "aigoofish", admin, password,
               "INSERT INTO public.result_hidden_items (owner_id, item_id) VALUES (%s, %s)",
               (owner, f"item_{index}"))
    owner = owners[0]
    task_ref = str(uuid.uuid4())
    _query(port, "aigoofish", admin, password,
           "INSERT INTO public.result_blacklist_rules "
           "(id, owner_id, scope, task_ref, task_name_snapshot, kind, pattern) "
           "VALUES (%s, %s, 'task', %s, 'fixture', 'keyword', 'synthetic')",
           (str(uuid.uuid4()), owner, task_ref))
    _query(port, "aigoofish", admin, password,
           "INSERT INTO public.result_view_preferences (owner_id, page_key) VALUES (%s, 'results')",
           (owner,))
    _query(port, "aigoofish", admin, password,
           "INSERT INTO public.price_observations "
           "(id, owner_id, task_ref, task_name_snapshot, item_id, run_id, observed_at, "
           "currency, raw_price, amount, source) "
           "VALUES (%s, %s, %s, 'fixture', 'item_0', %s, now(), 'CNY', '10.00', 10.00, 'fixture')",
           (str(uuid.uuid4()), owner, task_ref, str(uuid.uuid4())))
    _query(port, "aigoofish", admin, password,
           "INSERT INTO public.criteria_generation_jobs "
           "(id, owner_id, task_ref, task_name_snapshot, input_digest, idempotency_key, status, stage) "
           "VALUES (%s, %s, %s, 'fixture', %s, 'synthetic-job', 'succeeded', 'complete')",
           (str(uuid.uuid4()), owner, task_ref, "a" * 64))
    from src.storage.postgres_adapter import PostgresAdapter
    storage = PostgresAdapter(_dsn(port=port, database="aigoofish",
                                  user="aigoofish_app", password=app_password))
    try:
        for table in ("result_hidden_items", "result_blacklist_rules",
                      "result_view_preferences", "price_observations",
                      "criteria_generation_jobs"):
            first = storage.list_upstream_records(table, owner_id=owners[0])
            second = storage.list_upstream_records(table, owner_id=owners[1])
            if len(first) != 1 or any(str(row["owner_id"]) != owners[0] for row in first):
                raise pg.SmokeFailure("v2 application query did not isolate first owner")
            if len(second) != (1 if table == "result_hidden_items" else 0) or any(
                    str(row["owner_id"]) != owners[1] for row in second):
                raise pg.SmokeFailure("v2 application query leaked another owner")
        try:
            storage.list_upstream_records("app_schema_migrations", owner_id=owners[0])
        except ValueError:
            pass
        else:
            raise pg.SmokeFailure("v2 application query accepted an audit table")
    finally:
        storage.engine.dispose()
    check_backup_roundtrip(state, port, admin, password, identifier, runtime_root,
                           schema_version=2, owners=owners)


def run_smoke(postgres_root: Path | None = None, *, prompt_only: bool = False,
              migration_only: bool = False, fingerprint_only: bool = False,
              v2_backup_only: bool = False, export_v1_fixture: Path | None = None) -> dict[str, str]:
    lock = pg._load_lock()
    runtime_root = pg._runtime_root(lock, postgres_root)
    postgres, initdb, pg_ctl = (runtime_root / "bin" / name for name in ("postgres.exe", "initdb.exe", "pg_ctl.exe"))
    if not all(path.is_file() for path in (postgres, initdb, pg_ctl)):
        raise pg.SmokeFailure("prepared PostgreSQL runtime is incomplete")
    created_parents: list[Path] = []
    state = None
    success = False
    try:
        for directory in (pg._REPOSITORY_ROOT / ".tmp", pg._REPOSITORY_ROOT / ".tmp" / "tests", pg._TEST_PARENT):
            if not directory.exists():
                directory.mkdir()
                created_parents.append(directory)
            pg._assert_repository_path_without_reparse(directory)
        pg._assert_disk_floor(pg._TEST_PARENT)
        root = Path(tempfile.mkdtemp(prefix="集成 冒烟-schema-", dir=pg._TEST_PARENT))
        state = pg.SmokeState(root=root, data_root=root / "pgdata", pg_log=root / "postgres.log")
        diagnostics = root / "diagnostics"
        diagnostics.mkdir()
        port = pg._reserve_loopback_port()
        admin = f"schema_admin_{secrets.token_hex(4)}"
        password = secrets.token_urlsafe(32)
        app, probe = f"schema_app_{secrets.token_hex(4)}", f"schema_probe_{secrets.token_hex(4)}"
        state.raw_secrets.extend((password, "StrongPassword1!"))
        password_file = root / "initdb-password.txt"
        try:
            password_file.touch(exist_ok=False)
            pg._restrict_secret_file(password_file)
            password_file.write_text(password + "\n", encoding="utf-8")
            pg._run_tool([initdb, "-D", state.data_root, "--username", admin, "--pwfile", password_file, "--encoding=UTF8", "--locale=C", "--auth-local=scram-sha-256", "--auth-host=scram-sha-256", "--no-instructions"], stage="schema-initdb", diagnostic_root=diagnostics, timeout=60)
        finally:
            if password_file.exists():
                password_file.unlink()
        pg._assert_scram_hba(state.data_root)
        pg._append_server_configuration(state.data_root, port)
        state.postgres_start_attempted, state.postgres_port, state.postgres_start_epoch = True, port, __import__("time").time()
        pg._run_tool([pg_ctl, "start", "-D", state.data_root, "-l", state.pg_log, "-o", f"-p {port}", "-w", "-t", "30"], stage="schema-pg-start", diagnostic_root=diagnostics, timeout=45, direct_output=True)
        state.postgres_pid = pg._read_owned_postmaster_identity(state).pid
        if migration_only or fingerprint_only:
            marker_id = str(uuid.uuid4())
            (state.data_root / ".aigoofish-cluster.json").write_text(json.dumps({
                "format_version": 1, "instance_id": marker_id,
                "cluster_id": str(uuid.uuid4()), "engine_version": "17.11",
            }), encoding="utf-8")
            _check_v2_ddl_case(port=port, admin=admin, password=password, app=app,
                               probe=probe, raw_secrets=state.raw_secrets,
                               instance_id=marker_id, fingerprint_only=fingerprint_only)
        elif v2_backup_only:
            _check_v2_provision_and_backup(state, port, admin, password, runtime_root)
        else:
            _check_provision(state, port, admin, password, runtime_root, export_v1_fixture)
            _run_schema_cases(
                port=port, admin=admin, password=password, app=app, probe=probe,
                raw_secrets=state.raw_secrets, test_root=state.root,
                prompt_only=prompt_only,
            )
        success = True
        scope = "v2-fingerprint" if fingerprint_only else "v2-migration" if migration_only else "v2-backup" if v2_backup_only else "full"
        return {"postgres_version": str(lock["version"]), "schema_version": "2" if migration_only or v2_backup_only else "1",
                "result": "PASS", "scope": scope}
    finally:
        clean = True
        if state is not None:
            clean = pg._stop_postgres(pg_ctl, state)
            if success:
                if not clean:
                    raise pg.SmokeFailure("owned PostgreSQL cluster did not stop cleanly")
                pg._require_success_cleanup(state.root, pg._TEST_PARENT)
            elif clean:
                if not pg._redact_owned_raw_secrets(state.root, state.raw_secrets):
                    raise pg.SmokeFailure("failed smoke diagnostics could not be redacted")
            else:
                raise pg.SmokeFailure("failed smoke cluster could not be stopped safely")
        for directory in reversed(created_parents):
            try:
                directory.rmdir()
            except OSError:
                pass


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Run isolated portable schema PostgreSQL smoke")
    parser.add_argument("--postgres-root", type=Path)
    parser.add_argument("--migration-only", action="store_true", help="check v2 migration in an isolated database")
    parser.add_argument("--fingerprint-only", action="store_true", help="measure v1/v2 structures in a rolled-back isolated database")
    parser.add_argument("--v2-backup-only", action="store_true", help="check new schema v2 encrypted backup and isolated restore")
    parser.add_argument("--export-v1-fixture", type=Path,
                        help="retain a verified synthetic v1 archive and protected passphrase for Launcher E2E")
    arguments = parser.parse_args(argv)
    try:
        if sum((arguments.migration_only, arguments.fingerprint_only, arguments.v2_backup_only)) > 1:
            raise ValueError("choose one isolated test scope")
        if arguments.export_v1_fixture and (arguments.migration_only or arguments.fingerprint_only or arguments.v2_backup_only):
            raise ValueError("v1 export requires the full isolated scope")
        result = run_smoke(arguments.postgres_root, migration_only=arguments.migration_only,
                           fingerprint_only=arguments.fingerprint_only,
                           v2_backup_only=arguments.v2_backup_only,
                           export_v1_fixture=arguments.export_v1_fixture)
    except Exception as exc:
        print(f"PORTABLE_SCHEMA_PG_SMOKE=FAILED stage={type(exc).__name__}", file=sys.stderr)
        return 1
    print("PORTABLE_SCHEMA_PG_SMOKE=PASS")
    print(result)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
