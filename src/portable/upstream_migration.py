"""便携版 002 的显式事务迁移核心；不在 Web 启动时调用。

调用方必须持有 Launcher 实例租约、停止业务写入，并独立校验迁移前
备份。此模块自身再次核对 PGDATA、实例身份、管理员角色和结构指纹。
它不提供面向真实数据的 CLI；Launcher/源码迁移入口及备份证明接线
须在 B2 后续验收中完成。
"""

from __future__ import annotations

import logging
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

from sqlalchemy import text
from sqlalchemy.engine import Connection, Engine

from src.portable.schema import (
    DatabaseRoles, _acquire_schema_lock, _apply_role_grants, _assert_role_boundaries,
)
from src.portable.schema_catalog import V1_FINGERPRINT, V1_TABLE_COLUMNS, V2_BUSINESS_TABLES, V2_FINGERPRINT
from src.portable.schema_fingerprint import public_schema_fingerprint
from src.portable.upstream_ddl import (
    CREATE_STATEMENTS, MIGRATION_CHECKSUM, MIGRATION_ID, ddl_checksum,
)

logger = logging.getLogger(__name__)


class UpstreamMigrationError(RuntimeError):
    """迁移拒绝或失败；对外消息不含连接串及原始数据库异常。"""


@dataclass(frozen=True)
class MigrationResult:
    version: int
    status: str


def _assert_instance(connection: Connection, *, pgdata: Path, instance_id: str,
                     roles: DatabaseRoles) -> None:
    from src.portable.provision import _owned_marker

    owned_data, marker_identity = _owned_marker(str(pgdata), instance_id)
    if marker_identity != instance_id:
        raise UpstreamMigrationError("migration ownership marker differs")
    if int(connection.execute(text("SHOW server_version_num")).scalar_one()) // 10000 != 17:
        raise UpstreamMigrationError("migration requires PostgreSQL 17")
    actual = Path(connection.execute(text("SHOW data_directory")).scalar_one()).resolve(strict=True)
    if actual != owned_data:
        raise UpstreamMigrationError("migration target is not the expected PGDATA")
    comment = connection.execute(text(
        "SELECT shobj_description(oid, 'pg_database') FROM pg_database "
        "WHERE datname = current_database()"
    )).scalar_one()
    if comment != "aigoofish-instance:" + instance_id:
        raise UpstreamMigrationError("migration target instance identity differs")
    _assert_role_boundaries(connection, roles)


def _read_version(connection: Connection) -> int:
    rows = connection.execute(text(
        "SELECT version FROM public.app_schema_version LIMIT 2"
    )).fetchall()
    if len(rows) != 1 or type(rows[0][0]) is not int or rows[0][0] not in (1, 2):
        raise UpstreamMigrationError("schema version is missing or unsupported")
    return rows[0][0]


def _assert_no_unknown_schema(connection: Connection) -> None:
    unknown = connection.execute(text(
        "SELECT nspname FROM pg_namespace "
        "WHERE nspname <> 'public' AND nspname <> 'information_schema' "
        "AND nspname NOT LIKE 'pg\\_%' ESCAPE '\\' LIMIT 1"
    )).scalar_one_or_none()
    if unknown is not None:
        raise UpstreamMigrationError("database contains an unknown application schema")


def _assert_migration_record(connection: Connection) -> None:
    rows = connection.execute(text(
        "SELECT migration_id, checksum FROM public.app_schema_migrations LIMIT 2"
    )).fetchall()
    if rows != [(MIGRATION_ID, MIGRATION_CHECKSUM)]:
        raise UpstreamMigrationError("migration record is missing or differs")


def _grant_v2_tables(connection: Connection, roles: DatabaseRoles) -> None:
    app = '"' + roles.app + '"'
    probe = '"' + roles.probe + '"'
    tables = ", ".join('"public"."' + table + '"' for table in sorted(V2_BUSINESS_TABLES))
    connection.execute(text('REVOKE ALL PRIVILEGES ON TABLE ' + tables + ' FROM PUBLIC, ' + app + ', ' + probe))
    connection.execute(text('GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE ' + tables + ' TO ' + app))
    connection.execute(text(
        'REVOKE ALL PRIVILEGES ON TABLE "public"."app_schema_migrations" FROM PUBLIC, '
        + app + ', ' + probe
    ))


def _assert_v2_grants(connection: Connection, roles: DatabaseRoles) -> None:
    def allowed(role: str, table: str, privilege: str) -> bool:
        return bool(connection.execute(text(
            "SELECT has_table_privilege(:role, :table, :privilege)"
        ), {"role": role, "table": "public." + table, "privilege": privilege}).scalar_one())

    privileges = ("SELECT", "INSERT", "UPDATE", "DELETE")
    for table in sorted(V2_BUSINESS_TABLES | V1_TABLE_COLUMNS.keys()):
        if any(not allowed(roles.app, table, privilege) for privilege in privileges):
            raise UpstreamMigrationError("app role is missing a business grant")
        if any(allowed(roles.probe, table, privilege) for privilege in privileges):
            raise UpstreamMigrationError("probe role has a business grant")
    if any(allowed(role, "app_schema_migrations", privilege)
           for role in (roles.app, roles.probe) for privilege in privileges):
        raise UpstreamMigrationError("migration audit grants exceed the role boundary")
    if not all(allowed(role, "app_schema_version", "SELECT")
               for role in (roles.app, roles.probe)):
        raise UpstreamMigrationError("version visibility differs from the role policy")
    if any(allowed(role, "app_schema_version", privilege)
           for role in (roles.app, roles.probe) for privilege in privileges[1:]):
        raise UpstreamMigrationError("version table is writable by a runtime role")
    for role in (roles.app, roles.probe):
        if connection.execute(text(
            "SELECT has_schema_privilege(:role, 'public', 'CREATE')"
        ), {"role": role}).scalar_one():
            raise UpstreamMigrationError("application role can create public objects")


def _apply_in_transaction(connection: Connection, roles: DatabaseRoles) -> MigrationResult:
    _assert_no_unknown_schema(connection)
    version = _read_version(connection)
    fingerprint = public_schema_fingerprint(connection)
    if version == 2:
        if fingerprint != V2_FINGERPRINT:
            raise UpstreamMigrationError("schema v2 fingerprint differs")
        _assert_migration_record(connection)
        _assert_v2_grants(connection, roles)
        return MigrationResult(2, "already_applied")
    if fingerprint != V1_FINGERPRINT:
        raise UpstreamMigrationError("schema v1 fingerprint differs")

    for statement in CREATE_STATEMENTS:
        connection.execute(text(statement))
    connection.execute(text(
        "ALTER TABLE public.app_schema_version "
        "DROP CONSTRAINT app_schema_version_version_check"
    ))
    connection.execute(text(
        "ALTER TABLE public.app_schema_version "
        "ADD CONSTRAINT app_schema_version_version_check CHECK (version IN (1, 2))"
    ))
    connection.execute(text("UPDATE public.app_schema_version SET version = 2"))
    connection.execute(text(
        "INSERT INTO public.app_schema_migrations (migration_id, checksum) "
        "VALUES (:migration_id, :checksum)"
    ), {"migration_id": MIGRATION_ID, "checksum": MIGRATION_CHECKSUM})
    _apply_role_grants(connection, roles)
    _grant_v2_tables(connection, roles)
    if public_schema_fingerprint(connection) != V2_FINGERPRINT:
        raise UpstreamMigrationError("schema v2 post-migration fingerprint differs")
    _assert_migration_record(connection)
    _assert_v2_grants(connection, roles)
    return MigrationResult(2, "applied")


def migrate_upstream_schema(
    engine: Engine, roles: DatabaseRoles, *, pgdata: Path, instance_id: str,
    verify_maintenance: Callable[[], None], verify_backup: Callable[[], None],
) -> MigrationResult:
    """显式迁移核心；前置维护租约与备份验证由调用方提供并复核。"""

    roles.validated()
    try:
        instance_id = str(uuid.UUID(instance_id))
    except (TypeError, ValueError, AttributeError):
        raise UpstreamMigrationError("migration instance identity is invalid") from None
    if not pgdata.is_absolute() or not callable(verify_maintenance) or not callable(verify_backup):
        raise UpstreamMigrationError("migration requires explicit owned target and safety checks")
    if ddl_checksum() != MIGRATION_CHECKSUM:
        raise UpstreamMigrationError("migration DDL checksum differs")
    try:
        verify_maintenance()
        verify_backup()
        with engine.begin() as connection:
            connection.execute(text('SET LOCAL search_path = pg_catalog, "public"'))
            _acquire_schema_lock(connection)
            verify_maintenance()
            _assert_instance(connection, pgdata=pgdata, instance_id=instance_id, roles=roles)
            result = _apply_in_transaction(connection, roles)
            verify_backup()
            verify_maintenance()
            return result
    except UpstreamMigrationError:
        raise
    except Exception:
        logger.error("Portable upstream migration failed", extra={"event": "upstream_migration_failed"})
        raise UpstreamMigrationError("portable upstream migration failed safely") from None
