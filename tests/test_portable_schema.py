"""便携 schema 的隔离单元测试。

父测试进程从不导入仓库代码：子进程在临时 cwd 下以 ``-I`` 显式加入仓库
根目录，并用审计钩子断言没有打开临时的 ``.env``。这样测试不会意外读取
开发机真实配置，也覆盖 schema 模块的无配置导入契约。
"""

from __future__ import annotations

import contextlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


_REPOSITORY_ROOT = Path(__file__).resolve().parents[1]


def _isolated_environment() -> dict[str, str]:
    """保留 Windows 创建子进程所需最小环境，拒绝传入真实应用配置。"""

    allowed = ("COMSPEC", "PATH", "SYSTEMROOT", "WINDIR")
    return {name: os.environ[name] for name in allowed if name in os.environ}


def _run_isolated_case(case: str) -> None:
    parent = _REPOSITORY_ROOT / ".tmp" / "tests" / "portable-schema-unit"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="case-", dir=parent) as temporary:
        result = subprocess.run(
            [sys.executable, "-I", "-B", str(Path(__file__).resolve()), "--isolated-case", case],
            cwd=temporary,
            env=_isolated_environment(),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=20,
            check=False,
        )
    parent.rmdir()
    if result.returncode != 0:
        self_message = {
            "case": case,
            "stdout": result.stdout[-2_000:],
            "stderr": result.stderr[-2_000:],
        }
        raise AssertionError(f"isolated schema case failed: {json.dumps(self_message, ensure_ascii=False)}")


class PortableSchemaIsolationTests(unittest.TestCase):
    def test_role_grant_plan(self):
        _run_isolated_case("role_grants")

    def test_initializer_transaction_and_existing_schema_classification(self):
        _run_isolated_case("initializer")

    def test_first_admin_transaction_and_hasher_injection(self):
        _run_isolated_case("first_admin")


def _run_child(case: str) -> None:
    sentinel = (Path.cwd() / ".env").resolve()
    sentinel.write_text("GOOFISH_PORTABLE_ADMIN_DATABASE_URL=must-not-be-read\n", encoding="utf-8")
    opened_sentinel = False

    def audit(event: str, arguments: tuple[object, ...]) -> None:
        nonlocal opened_sentinel
        if event != "open" or not arguments:
            return
        try:
            candidate = Path(str(arguments[0])).resolve(strict=False)
        except (OSError, ValueError):
            return
        if candidate == sentinel:
            opened_sentinel = True

    sys.addaudithook(audit)
    sys.path.insert(0, str(_REPOSITORY_ROOT))

    from sqlalchemy.sql.dml import Insert
    from src.portable import schema

    class Result:
        def __init__(self, *, scalar=None, rows=(), first=None, one=None):
            self.scalar = scalar
            self.rows = list(rows)
            self.first_value = first
            self.one_value = one

        def scalar_one_or_none(self):
            return self.scalar

        def scalar_one(self):
            return self.scalar

        def fetchall(self):
            return list(self.rows)

        def first(self):
            return self.first_value

        def one(self):
            return self.one_value

    class Connection:
        def __init__(self, handler=None):
            self.handler = handler or (lambda statement, parameters: Result())
            self.events = []

        def execute(self, statement, parameters=None):
            self.events.append((statement, parameters))
            return self.handler(statement, parameters)

    class Engine:
        def __init__(self, connection):
            self.connection = connection
            self.committed = False
            self.rolled_back = False

        @contextlib.contextmanager
        def begin(self):
            try:
                yield self.connection
            except Exception:
                self.rolled_back = True
                raise
            else:
                self.committed = True

    def sql(statement) -> str:
        return " ".join(str(statement).split())

    if case == "role_grants":
        invalid = (
            schema.DatabaseRoles("admin", "app;DROP TABLE users", "probe"),
            schema.DatabaseRoles("admin", "app", "app"),
            schema.DatabaseRoles("admin", "PUBLIC", "probe"),
        )
        for roles in invalid:
            try:
                roles.validated()
            except schema.RoleGrantError:
                pass
            else:
                raise AssertionError("unsafe role name was accepted")
        statements = schema.build_role_grant_sql(
            schema.DatabaseRoles("launcher_admin", "runtime_app", "ready_probe"),
            database_name="portable_db",
        )
        joined = "\n".join(statements)
        assert 'GRANT SELECT ON TABLE "public"."app_schema_version" TO "ready_probe"' in joined
        assert '"users"' not in "\n".join(line for line in statements if '"ready_probe"' in line)
        assert " CREATE ON SCHEMA " not in "\n".join(line for line in statements if '"runtime_app"' in line)
        assert "NOSUPERUSER" in joined and "NOBYPASSRLS" in joined

    elif case == "initializer":
        connection = Connection()
        engine = Engine(connection)

        def create_models(*, bind, checkfirst):
            assert bind is connection and checkfirst is False
            connection.events.append(("CREATE_MODELS", None))

        original_empty = schema._assert_empty_public_schema
        original_metadata = schema._portable_metadata
        schema._assert_empty_public_schema = lambda value: None
        schema._portable_metadata = lambda: type(
            "Metadata", (), {"create_all": staticmethod(create_models)}
        )()
        try:
            assert schema.initialize_schema(engine) == 1
        finally:
            schema._assert_empty_public_schema = original_empty
            schema._portable_metadata = original_metadata
        events = [item if isinstance(item, str) else sql(item) for item, _ in connection.events]
        assert any("SET LOCAL search_path = pg_catalog" in item for item in events)
        assert events.index("CREATE_MODELS") < next(i for i, item in enumerate(events) if "INSERT INTO" in item)
        assert engine.committed and not engine.rolled_back

        expected = [("relation", name) for name in (*schema._application_table_names(), schema.VERSION_TABLE_NAME)]
        original_rows = schema._read_version_rows
        schema._read_version_rows = lambda value: [1]
        try:
            try:
                schema._raise_for_existing_schema(connection, expected)
            except schema.SchemaAlreadyInitializedError:
                pass
            else:
                raise AssertionError("complete initialized schema was not distinguished")
            try:
                schema._raise_for_existing_schema(connection, [("relation", schema.VERSION_TABLE_NAME)])
            except schema.SchemaNotEmptyError:
                pass
            else:
                raise AssertionError("partial version-only schema was accepted")
        finally:
            schema._read_version_rows = original_rows

        cloned = schema._portable_metadata()
        assert all(table.schema == schema.SCHEMA_NAME for table in cloned.tables.values())
        assert all(table.schema is None for table in schema.Base.metadata.tables.values())

    elif case == "first_admin":
        def handler(statement, parameters):
            rendered = sql(statement)
            if "to_regclass" in rendered:
                return Result(scalar="app_schema_version")
            if "SELECT version" in rendered:
                return Result(rows=[(1,)])
            if 'FROM "public"."users"' in rendered:
                return Result(first=None)
            if 'FROM "public"."user_groups"' in rendered:
                return Result(first=None)
            if 'FROM "public"."group_permissions"' in rendered:
                return Result(first=None)
            if isinstance(statement, Insert) and statement.table.name == "group_permissions":
                raise RuntimeError("injected write failure")
            return Result()

        engine = Engine(Connection(handler))
        try:
            schema.create_first_admin(
                engine,
                username="portable_admin",
                password="StrongPassword1!",
                password_hasher=lambda _: "$2b$explicit-existing-hasher",
            )
        except schema.FirstAdminError:
            pass
        else:
            raise AssertionError("injected first-admin failure was not converted")
        assert engine.rolled_back and not engine.committed

        for password in ("1234567", "", "密" * 22 + "Aa1!xyz"):
            try:
                schema._validate_first_admin("portable_admin", password)
            except schema.FirstAdminError:
                pass
            else:
                raise AssertionError("unsafe password was accepted")
        for password in ("12345678", "alllowercase", "密" * 8):
            assert schema._validate_first_admin("admin", password) == ("admin", password)
    else:
        raise ValueError("unknown isolated case")

    assert not opened_sentinel, "schema import unexpectedly opened temporary .env"
    assert "src.config" not in sys.modules, "schema import loaded application configuration"


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--isolated-case":
        _run_child(sys.argv[2])
    else:
        unittest.main()
