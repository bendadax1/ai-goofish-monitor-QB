"""Windows 便携版 PostgreSQL schema 初始基线。

该模块只提供显式调用的初始化和首个管理员事务服务。它不读取
``.env``、不扫描用户目录、不导入业务配置或存储适配器，也不执行
抓取、AI 或通知。

默认仍初始化版本 1。版本 2 的全新空库初始化必须显式选择；已有
schema 的升级只能走独立迁移器，不能用 ``create_all`` 代替。
"""

from __future__ import annotations

import argparse
import logging
import os
import re
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Mapping, Sequence

from sqlalchemy import MetaData, create_engine, text
from sqlalchemy.engine import Connection, Engine, URL
from sqlalchemy.exc import SQLAlchemyError
from sqlalchemy.pool import NullPool
from src.account_policy import validate_new_password, validate_username

from src.portable.maintenance import validate_database_target
from src.portable.schema_catalog import V1_TABLE_COLUMNS
from src.portable.seeds import (
    PortableSeedError,
    apply_system_seeds,
    copy_defaults_to_first_admin,
    load_seed_bundle,
)
from src.storage.models import (
    Base,
    GroupPermission,
    User,
    UserGroup,
    UserGroupMember,
)


SCHEMA_VERSION = 1
SCHEMA_NAME = "public"
VERSION_TABLE_NAME = "app_schema_version"
ADMIN_DATABASE_ENVIRONMENT_VARIABLE = "GOOFISH_PORTABLE_ADMIN_DATABASE_URL"

# 从稳定 ASCII 文本派生的固定 64 位有符号整数。同一数据库中的 schema
# 初始化和 first-admin 都使用这把事务级锁。
SCHEMA_ADVISORY_LOCK_KEY = 0x27474F4F46495348

_ROLE_IDENTIFIER_PATTERN = re.compile(r"^[A-Za-z_][A-Za-z0-9_]{0,62}$")
_RESERVED_ROLE_NAMES = frozenset({"current_role", "current_user", "none", "public", "session_user"})
_PERMISSION_CATEGORIES = ("tasks", "results", "accounts", "notify", "ai", "admin")
_SUPER_ADMIN_GROUP = {
    "code": "super_admin_group",
    "name": "超级管理员组",
    "description": "系统预置：全量权限",
}

logger = logging.getLogger(__name__)


class PortableSchemaError(RuntimeError):
    """便携版 schema 操作失败；消息不包含 DSN 或驱动异常。"""


class SchemaNotEmptyError(PortableSchemaError):
    """public schema 不是可安全初始化的空 schema。"""


class SchemaAlreadyInitializedError(PortableSchemaError):
    """schema 已经位于当前版本。"""


class SchemaInvalidError(PortableSchemaError):
    """schema 版本表结构或内容损坏。"""


class SchemaIncompatibleError(PortableSchemaError):
    """schema 版本不在本基线支持范围内。"""


class RoleGrantError(PortableSchemaError):
    """数据库角色不存在或不满足最小权限边界。"""


class FirstAdminError(PortableSchemaError):
    """首个管理员输入或前置条件不安全。"""


@dataclass(frozen=True)
class DatabaseRoles:
    """便携实例的三类数据库角色。"""

    admin: str
    app: str
    probe: str

    def validated(self) -> "DatabaseRoles":
        normalized = (self.admin, self.app, self.probe)
        for role in normalized:
            if not _ROLE_IDENTIFIER_PATTERN.fullmatch(role) or role.lower() in _RESERVED_ROLE_NAMES:
                raise RoleGrantError("database role name is invalid")
        if len(set(normalized)) != len(normalized):
            raise RoleGrantError("database roles must be distinct")
        return self


def _quoted_identifier(value: str) -> str:
    if not _ROLE_IDENTIFIER_PATTERN.fullmatch(value) or value.lower() in _RESERVED_ROLE_NAMES:
        raise RoleGrantError("database identifier is invalid")
    return f'"{value}"'


def _application_table_names() -> tuple[str, ...]:
    """v1 固定表清单；新增 ORM 模型不得改变已发行版本的指纹。"""

    return tuple(sorted(V1_TABLE_COLUMNS))


def _portable_metadata() -> MetaData:
    """复制业务表定义到固定的 ``public`` schema，绝不改共享 ORM 元数据。

    现有模型并没有声明 ``MetaData.schema``。初始化连接把 ``pg_catalog``
    排在 ``public`` 前面以避免 public 中未知函数遮蔽系统函数，所以不能依赖
    无 schema 的 DDL 随 search_path 落到 public。每次初始化临时克隆元数据，
    使 DDL、外键和默认值都显式面向唯一允许的 schema。
    """

    metadata = MetaData(schema=SCHEMA_NAME)
    for table_name in _application_table_names():
        Base.metadata.tables[table_name].to_metadata(metadata, schema=SCHEMA_NAME)
    return metadata


def build_role_grant_sql(roles: DatabaseRoles, *, database_name: str) -> tuple[str, ...]:
    """生成仅面向固定 ``public`` schema 的最小权限 SQL。

    所有标识符只允许 PostgreSQL 不需要转义的 ASCII 子集，并且仍用
    双引号定界。不接受 schema 参数，避免把权限计划扩大到未知区域。
    """

    roles.validated()
    database = _quoted_identifier(database_name)
    admin = _quoted_identifier(roles.admin)
    app = _quoted_identifier(roles.app)
    probe = _quoted_identifier(roles.probe)
    tables = ", ".join(f'"{SCHEMA_NAME}"."{name}"' for name in _application_table_names())
    version_table = f'"{SCHEMA_NAME}"."{VERSION_TABLE_NAME}"'

    return (
        f"ALTER ROLE {app} NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS",
        f"ALTER ROLE {probe} NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS",
        f"REVOKE ALL PRIVILEGES ON DATABASE {database} FROM PUBLIC",
        f"REVOKE ALL PRIVILEGES ON DATABASE {database} FROM {app}, {probe}",
        f"GRANT CONNECT ON DATABASE {database} TO {admin}, {app}, {probe}",
        f'REVOKE ALL PRIVILEGES ON SCHEMA "{SCHEMA_NAME}" FROM PUBLIC',
        f'REVOKE ALL PRIVILEGES ON SCHEMA "{SCHEMA_NAME}" FROM {app}, {probe}',
        f'GRANT USAGE, CREATE ON SCHEMA "{SCHEMA_NAME}" TO {admin}',
        f'GRANT USAGE ON SCHEMA "{SCHEMA_NAME}" TO {app}, {probe}',
        f'REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA "{SCHEMA_NAME}" FROM PUBLIC, {app}, {probe}',
        f'REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA "{SCHEMA_NAME}" FROM PUBLIC, {app}, {probe}',
        f"GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE {tables} TO {app}",
        f'GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA "{SCHEMA_NAME}" TO {app}',
        f"GRANT SELECT ON TABLE {version_table} TO {probe}",
        # First-admin transactions run as the application role, not the
        # Launcher database administrator, and must validate compatibility.
        f"GRANT SELECT ON TABLE {version_table} TO {app}",
    )


def _acquire_schema_lock(connection: Connection) -> None:
    connection.execute(
        text("SELECT pg_advisory_xact_lock(:lock_key)"),
        {"lock_key": SCHEMA_ADVISORY_LOCK_KEY},
    )


def _public_schema_exists(connection: Connection) -> bool:
    return bool(
        connection.execute(
            text("SELECT 1 FROM pg_namespace WHERE nspname = :schema_name"),
            {"schema_name": SCHEMA_NAME},
        ).scalar_one_or_none()
    )


def _public_schema_objects(connection: Connection) -> list[tuple[str, str]]:
    """有界读取 public 中任何可疑的用户对象。

    表的自动复合类型和数组类型不重复报告；独立基础、域、枚举、
    范围类型仍会被拒绝。结果上限是已知 P1 表数量加一，足以判定严格
    的已初始化集合，同时避免在未知数据库上无界加载。
    """

    rows = connection.execute(
        text(
            """
            SELECT object_kind, object_name
            FROM (
                SELECT 'relation'::text AS object_kind, c.relname::text AS object_name
                FROM pg_class AS c
                JOIN pg_namespace AS n ON n.oid = c.relnamespace
                WHERE n.nspname = :schema_name
                  AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f', 'c')
                UNION ALL
                SELECT 'function', p.proname::text
                FROM pg_proc AS p
                JOIN pg_namespace AS n ON n.oid = p.pronamespace
                WHERE n.nspname = :schema_name
                UNION ALL
                SELECT 'type', t.typname::text
                FROM pg_type AS t
                JOIN pg_namespace AS n ON n.oid = t.typnamespace
                WHERE n.nspname = :schema_name
                  AND t.typrelid = 0
                  AND t.typelem = 0
                  AND t.typtype IN ('b', 'c', 'd', 'e', 'r', 'm')
                UNION ALL
                SELECT 'collation', c.collname::text
                FROM pg_collation AS c
                JOIN pg_namespace AS n ON n.oid = c.collnamespace
                WHERE n.nspname = :schema_name
                UNION ALL
                SELECT 'conversion', c.conname::text
                FROM pg_conversion AS c
                JOIN pg_namespace AS n ON n.oid = c.connamespace
                WHERE n.nspname = :schema_name
                UNION ALL
                SELECT 'operator', o.oprname::text
                FROM pg_operator AS o
                JOIN pg_namespace AS n ON n.oid = o.oprnamespace
                WHERE n.nspname = :schema_name
                UNION ALL
                SELECT 'operator_class', o.opcname::text
                FROM pg_opclass AS o
                JOIN pg_namespace AS n ON n.oid = o.opcnamespace
                WHERE n.nspname = :schema_name
                UNION ALL
                SELECT 'operator_family', o.opfname::text
                FROM pg_opfamily AS o
                JOIN pg_namespace AS n ON n.oid = o.opfnamespace
                WHERE n.nspname = :schema_name
                UNION ALL
                SELECT 'text_search_configuration', c.cfgname::text
                FROM pg_ts_config AS c
                JOIN pg_namespace AS n ON n.oid = c.cfgnamespace
                WHERE n.nspname = :schema_name
                UNION ALL
                SELECT 'text_search_dictionary', d.dictname::text
                FROM pg_ts_dict AS d
                JOIN pg_namespace AS n ON n.oid = d.dictnamespace
                WHERE n.nspname = :schema_name
                UNION ALL
                SELECT 'text_search_parser', p.prsname::text
                FROM pg_ts_parser AS p
                JOIN pg_namespace AS n ON n.oid = p.prsnamespace
                WHERE n.nspname = :schema_name
                UNION ALL
                SELECT 'text_search_template', t.tmplname::text
                FROM pg_ts_template AS t
                JOIN pg_namespace AS n ON n.oid = t.tmplnamespace
                WHERE n.nspname = :schema_name
            ) AS public_objects
            ORDER BY object_kind, object_name
            LIMIT :object_limit
            """
        ),
        {
            "schema_name": SCHEMA_NAME,
            "object_limit": len(_application_table_names()) + 2,
        },
    ).fetchall()
    return [(str(row[0]), str(row[1])) for row in rows]


def _read_version_rows(connection: Connection) -> list[int]:
    rows = connection.execute(
        text(f'SELECT version FROM "{SCHEMA_NAME}"."{VERSION_TABLE_NAME}" LIMIT 2')
    ).fetchall()
    return [row[0] for row in rows]


def _raise_for_existing_schema(connection: Connection, objects: list[tuple[str, str]]) -> None:
    expected_relations = set(_application_table_names()) | {VERSION_TABLE_NAME}
    actual_relations = {name for kind, name in objects if kind == "relation"}
    if (
        len(objects) != len(expected_relations)
        or any(kind != "relation" for kind, _ in objects)
        or actual_relations != expected_relations
    ):
        raise SchemaNotEmptyError("public schema contains existing or unknown objects")
    try:
        rows = _read_version_rows(connection)
    except SQLAlchemyError:
        raise SchemaInvalidError("schema version table is invalid") from None
    if len(rows) != 1 or isinstance(rows[0], bool) or not isinstance(rows[0], int) or rows[0] <= 0:
        raise SchemaInvalidError("schema version table must contain exactly one positive integer")
    if rows[0] == SCHEMA_VERSION:
        raise SchemaAlreadyInitializedError("schema is already initialized")
    raise SchemaIncompatibleError("schema version is incompatible with this initializer")


def _assert_empty_public_schema(connection: Connection) -> None:
    if not _public_schema_exists(connection):
        raise SchemaNotEmptyError("required public schema does not exist")
    unknown_schema = connection.execute(
        text(
            "SELECT nspname FROM pg_namespace "
            "WHERE nspname <> :schema_name "
            "AND nspname <> 'information_schema' "
            "AND nspname NOT LIKE 'pg\\_%' ESCAPE '\\' "
            "ORDER BY nspname LIMIT 1"
        ),
        {"schema_name": SCHEMA_NAME},
    ).scalar_one_or_none()
    if unknown_schema is not None:
        raise SchemaNotEmptyError("database contains an unknown application schema")
    objects = _public_schema_objects(connection)
    if objects:
        _raise_for_existing_schema(connection, objects)


def _assert_role_boundaries(connection: Connection, roles: DatabaseRoles) -> None:
    current_user = connection.execute(text("SELECT current_user")).scalar_one()
    if current_user != roles.admin:
        raise RoleGrantError("initializer connection must use the declared admin role")

    for role in (roles.admin, roles.app, roles.probe):
        exists = connection.execute(
            text("SELECT 1 FROM pg_roles WHERE rolname = :role_name"),
            {"role_name": role},
        ).scalar_one_or_none()
        if not exists:
            raise RoleGrantError("a declared database role does not exist")

    for role in (roles.app, roles.probe):
        has_membership = connection.execute(
            text(
                "SELECT EXISTS ("
                "SELECT 1 FROM pg_auth_members AS m "
                "JOIN pg_roles AS r ON r.oid = m.member "
                "WHERE r.rolname = :role_name"
                ")"
            ),
            {"role_name": role},
        ).scalar_one()
        if has_membership:
            raise RoleGrantError("app and probe roles must not inherit or assume other roles")

    owners = connection.execute(
        text(
            "SELECT d.datdba::regrole::text, n.nspowner::regrole::text "
            "FROM pg_database AS d "
            "JOIN pg_namespace AS n ON n.nspname = :schema_name "
            "WHERE d.datname = current_database()"
        ),
        {"schema_name": SCHEMA_NAME},
    ).one()
    if roles.app in owners or roles.probe in owners:
        raise RoleGrantError("app and probe roles must not own the database or public schema")

    owned_objects = connection.execute(
        text(
            "SELECT EXISTS ("
            "SELECT 1 FROM pg_class AS c JOIN pg_roles AS r ON r.oid = c.relowner "
            "WHERE r.rolname IN (:app_role, :probe_role) "
            "UNION ALL "
            "SELECT 1 FROM pg_proc AS p JOIN pg_roles AS r ON r.oid = p.proowner "
            "WHERE r.rolname IN (:app_role, :probe_role) "
            "UNION ALL "
            "SELECT 1 FROM pg_type AS t JOIN pg_roles AS r ON r.oid = t.typowner "
            "WHERE r.rolname IN (:app_role, :probe_role)"
            ")"
        ),
        {"app_role": roles.app, "probe_role": roles.probe},
    ).scalar_one()
    if owned_objects:
        raise RoleGrantError("app and probe roles must not own database objects")


def _apply_role_grants(connection: Connection, roles: DatabaseRoles) -> None:
    roles.validated()
    _assert_role_boundaries(connection, roles)
    database_name = connection.execute(text("SELECT current_database()")).scalar_one()
    for statement in build_role_grant_sql(roles, database_name=str(database_name)):
        connection.execute(text(statement))

    for role in (roles.app, roles.probe):
        flags = connection.execute(
            text(
                "SELECT rolsuper, rolcreatedb, rolcreaterole, rolinherit, "
                "rolreplication, rolbypassrls "
                "FROM pg_roles WHERE rolname = :role_name"
            ),
            {"role_name": role},
        ).one()
        if tuple(flags) != (False, False, False, False, False, False):
            raise RoleGrantError("app or probe role retains elevated attributes")


def initialize_schema(
    engine: Engine,
    *,
    roles: DatabaseRoles | None = None,
    seed_root: os.PathLike[str] | str | None = None,
    target_version: int = 1,
) -> int:
    """在单个事务中建立空 public schema 的指定版本基线。

    ``app_schema_version`` 在所有 ORM 表之后创建并写入。任何异常由
    ``Engine.begin()`` 回滚；已有或不兼容数据库不执行 ``create_all``。
    """

    if target_version not in (1, 2):
        raise PortableSchemaError("portable schema target version is unsupported")
    try:
        bundle = load_seed_bundle(Path(seed_root)) if seed_root is not None else None
    except PortableSeedError:
        raise PortableSchemaError("portable seed bundle is invalid") from None
    try:
        with engine.begin() as connection:
            connection.execute(text(f'SET LOCAL search_path = pg_catalog, "{SCHEMA_NAME}"'))
            _acquire_schema_lock(connection)
            _assert_empty_public_schema(connection)
            _portable_metadata().create_all(bind=connection, checkfirst=False)
            version_constraint = "version = 1" if target_version == 1 else "version IN (1, 2)"
            connection.execute(
                text(
                    f'CREATE TABLE "{SCHEMA_NAME}"."{VERSION_TABLE_NAME}" '
                    f"(version integer PRIMARY KEY CHECK ({version_constraint}))"
                )
            )
            connection.execute(
                text(
                    f'INSERT INTO "{SCHEMA_NAME}"."{VERSION_TABLE_NAME}" (version) '
                    "VALUES (:version)"
                ),
                {"version": target_version},
            )
            if target_version == 2:
                from src.portable.upstream_ddl import CREATE_STATEMENTS, MIGRATION_CHECKSUM, MIGRATION_ID, ddl_checksum

                if ddl_checksum() != MIGRATION_CHECKSUM:
                    raise PortableSchemaError("portable schema v2 DDL checksum differs")

                for statement in CREATE_STATEMENTS:
                    connection.execute(text(statement))
                connection.execute(text(
                    "INSERT INTO public.app_schema_migrations (migration_id, checksum) "
                    "VALUES (:migration_id, :checksum)"
                ), {"migration_id": MIGRATION_ID, "checksum": MIGRATION_CHECKSUM})
            if bundle is not None:
                apply_system_seeds(connection, bundle)
            if roles is not None:
                _apply_role_grants(connection, roles)
                if target_version == 2:
                    from src.portable.upstream_migration import _grant_v2_tables, _assert_v2_grants

                    _grant_v2_tables(connection, roles)
                    _assert_v2_grants(connection, roles)
            if target_version == 2:
                from src.portable.schema_catalog import V2_FINGERPRINT
                from src.portable.schema_fingerprint import public_schema_fingerprint

                if public_schema_fingerprint(connection) != V2_FINGERPRINT:
                    raise PortableSchemaError("portable schema v2 structure differs")
        return target_version
    except PortableSchemaError:
        raise
    except Exception:
        logger.error(
            "Portable schema initialization failed",
            extra={"event": "portable_schema_initialization_failed"},
        )
        raise PortableSchemaError("portable schema initialization failed") from None


def configure_role_grants(engine: Engine, roles: DatabaseRoles) -> None:
    """在独立事务中重申 admin/app/probe 的当前对象权限。"""

    try:
        with engine.begin() as connection:
            connection.execute(text(f'SET LOCAL search_path = pg_catalog, "{SCHEMA_NAME}"'))
            _acquire_schema_lock(connection)
            version = _assert_compatible_schema(connection)
            _apply_role_grants(connection, roles)
            if version == 2:
                from src.portable.upstream_migration import _grant_v2_tables, _assert_v2_grants

                _grant_v2_tables(connection, roles)
                _assert_v2_grants(connection, roles)
    except PortableSchemaError:
        raise
    except Exception:
        logger.error(
            "Portable database role configuration failed",
            extra={"event": "portable_role_configuration_failed"},
        )
        raise RoleGrantError("portable database role configuration failed") from None


def _assert_compatible_schema(connection: Connection) -> int:
    relation = connection.execute(
        text("SELECT to_regclass(:qualified_name)"),
        {"qualified_name": f"{SCHEMA_NAME}.{VERSION_TABLE_NAME}"},
    ).scalar_one_or_none()
    if relation is None:
        raise SchemaInvalidError("schema version table is missing")
    try:
        rows = _read_version_rows(connection)
    except SQLAlchemyError:
        raise SchemaInvalidError("schema version table is invalid") from None
    if len(rows) != 1 or isinstance(rows[0], bool) or not isinstance(rows[0], int) or rows[0] <= 0:
        raise SchemaInvalidError("schema version table must contain exactly one positive integer")
    if rows[0] not in (1, 2):
        raise SchemaIncompatibleError("schema version is incompatible")
    return rows[0]


def _validate_first_admin(username: str, password: str) -> tuple[str, str]:
    try:
        return validate_username(username), validate_new_password(password)
    except ValueError as exc:
        raise FirstAdminError(str(exc)) from None


def create_first_admin(
    engine: Engine,
    *,
    username: str,
    password: str,
    password_hasher: Callable[[str], str],
) -> str:
    """仅在 users 为空时创建显式的首个超级管理员。

    ``password_hasher`` 必须由受保护的首次设置通道显式传入现有
    ``src.storage.utils.hash_password``。本模块不直接导入它，因为该旧模块会连带
    导入业务配置并可能读取 ``.env``。Launcher control token 不是用户密码，
    本函数不接受该 token。
    """

    normalized_username, normalized_password = _validate_first_admin(username, password)
    if not callable(password_hasher):
        raise FirstAdminError("an explicit password hasher is required")
    try:
        password_hash = password_hasher(normalized_password)
    except Exception:
        logger.error(
            "First-admin password hashing failed",
            extra={"event": "portable_first_admin_hash_failed"},
        )
        raise FirstAdminError("first-admin password hashing failed") from None
    if not isinstance(password_hash, str) or not password_hash:
        raise FirstAdminError("password hasher returned an invalid value")

    user_id = uuid.uuid4()
    try:
        with engine.begin() as connection:
            connection.execute(text(f'SET LOCAL search_path = pg_catalog, "{SCHEMA_NAME}"'))
            _acquire_schema_lock(connection)
            _assert_compatible_schema(connection)
            existing_user = connection.execute(
                text(f'SELECT id FROM "{SCHEMA_NAME}"."users" LIMIT 1 FOR UPDATE')
            ).first()
            if existing_user is not None:
                raise FirstAdminError("first-admin creation requires an empty users table")

            group_row = connection.execute(
                text(
                    f'SELECT id, is_system FROM "{SCHEMA_NAME}"."user_groups" '
                    "WHERE code = :code FOR UPDATE"
                ),
                {"code": _SUPER_ADMIN_GROUP["code"]},
            ).first()
            if group_row is not None and not bool(group_row[1]):
                raise FirstAdminError("super-admin group code collides with a non-system group")

            if group_row is None:
                group_id = uuid.uuid4()
                connection.execute(
                    UserGroup.__table__.insert().values(
                        id=group_id,
                        code=_SUPER_ADMIN_GROUP["code"],
                        name=_SUPER_ADMIN_GROUP["name"],
                        description=_SUPER_ADMIN_GROUP["description"],
                        is_system=True,
                    )
                )
            else:
                group_id = group_row[0]

            connection.execute(
                User.__table__.insert().values(
                    id=user_id,
                    username=normalized_username,
                    password_hash=password_hash,
                    role="super_admin",
                    is_active=True,
                )
            )
            for category in _PERMISSION_CATEGORIES:
                existing_permission = connection.execute(
                    text(
                        f'SELECT id FROM "{SCHEMA_NAME}"."group_permissions" '
                        "WHERE group_id = :group_id AND category = :category FOR UPDATE"
                    ),
                    {"group_id": group_id, "category": category},
                ).first()
                if existing_permission is None:
                    connection.execute(
                        GroupPermission.__table__.insert().values(
                            id=uuid.uuid4(),
                            group_id=group_id,
                            category=category,
                            enabled=True,
                        )
                    )
                else:
                    connection.execute(
                        GroupPermission.__table__.update()
                        .where(GroupPermission.id == existing_permission[0])
                        .values(enabled=True)
                    )
            connection.execute(
                UserGroupMember.__table__.insert().values(
                    id=uuid.uuid4(),
                    user_id=user_id,
                    group_id=group_id,
                )
            )
            copy_defaults_to_first_admin(connection, user_id)
        return str(user_id)
    except PortableSchemaError:
        raise
    except Exception:
        logger.error(
            "Portable first-admin transaction failed",
            extra={"event": "portable_first_admin_failed"},
        )
        raise FirstAdminError("portable first-admin transaction failed") from None


def _engine_from_environment(environ: Mapping[str, str]) -> Engine:
    raw_dsn = environ.get(ADMIN_DATABASE_ENVIRONMENT_VARIABLE, "")
    target = validate_database_target(raw_dsn, environ=environ)
    parameters = target.parameters
    url = URL.create(
        "postgresql+psycopg2",
        username=parameters["user"],
        password=parameters["password"],
        host=parameters["host"],
        port=int(parameters["port"]),
        database=parameters["dbname"],
    )
    return create_engine(
        url,
        poolclass=NullPool,
        connect_args={
            "hostaddr": parameters["hostaddr"],
            "sslmode": "disable",
            "connect_timeout": 5,
            "application_name": "goofish-portable-schema-initializer",
            "options": "-c statement_timeout=30000 -c lock_timeout=30000",
        },
    )


def _argument_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Initialize the isolated portable PostgreSQL schema")
    parser.add_argument("--admin-role", required=True)
    parser.add_argument("--app-role", required=True)
    parser.add_argument("--probe-role", required=True)
    return parser


def run(
    argv: Sequence[str] | None = None,
    *,
    environ: Mapping[str, str] | None = None,
) -> int:
    args = _argument_parser().parse_args(argv)
    roles = DatabaseRoles(admin=args.admin_role, app=args.app_role, probe=args.probe_role).validated()
    engine = _engine_from_environment(os.environ if environ is None else environ)
    try:
        program_root = Path(__file__).resolve().parents[2]
        initialize_schema(engine, roles=roles, seed_root=program_root / "defaults")
    finally:
        engine.dispose()
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    try:
        exit_code = run(argv)
    except (PortableSchemaError, ValueError) as exc:
        print(f"Portable schema initialization error: {exc}", file=os.sys.stderr)
        return 2
    except Exception:
        logger.error(
            "Portable schema entry failed",
            extra={"event": "portable_schema_entry_failed"},
        )
        print("Portable schema initialization error: initialization failed", file=os.sys.stderr)
        return 3
    print(f"Portable schema initialized at version {SCHEMA_VERSION}")
    return exit_code


__all__ = [
    "ADMIN_DATABASE_ENVIRONMENT_VARIABLE",
    "DatabaseRoles",
    "FirstAdminError",
    "PortableSchemaError",
    "RoleGrantError",
    "SCHEMA_ADVISORY_LOCK_KEY",
    "SCHEMA_VERSION",
    "SchemaAlreadyInitializedError",
    "SchemaIncompatibleError",
    "SchemaInvalidError",
    "SchemaNotEmptyError",
    "build_role_grant_sql",
    "configure_role_grants",
    "create_first_admin",
    "initialize_schema",
    "main",
    "run",
]
