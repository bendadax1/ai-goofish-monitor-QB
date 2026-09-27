"""Explicit provisioning for a newly initialized, Launcher-owned PG cluster.

CREATE DATABASE is not transactional. Partial provisioning is therefore a
recovery condition: this entry refuses to overwrite existing roles/databases.
"""

import argparse
import json
import logging
import os
from pathlib import Path
import uuid

import psycopg2
from psycopg2 import sql

from src.portable.maintenance import validate_database_target
from src.portable.schema import DatabaseRoles, _engine_from_environment, initialize_schema


ADMIN_DSN_ENV = "GOOFISH_PORTABLE_ADMIN_DATABASE_URL"
APP_PASSWORD_ENV = "GOOFISH_PORTABLE_APP_DATABASE_PASSWORD"
PROBE_PASSWORD_ENV = "GOOFISH_PORTABLE_PROBE_DATABASE_PASSWORD"
DATABASE_NAME = "aigoofish"
APP_ROLE = "aigoofish_app"
PROBE_ROLE = "aigoofish_probe"
_LOCK = 0x27474F4F50524F56
logger = logging.getLogger(__name__)


class ProvisionError(RuntimeError):
    pass


def _password(environment, name):
    value = environment.get(name, "")
    if not isinstance(value, str) or not 32 <= len(value) <= 128 or not value.isascii() or any(ord(c) < 33 or ord(c) > 126 for c in value):
        raise ProvisionError("invalid generated database credential")
    return value


def _owned_marker(pgdata: str, instance_id: str):
    path = Path(pgdata)
    if not path.is_absolute():
        raise ProvisionError("PGDATA must be absolute")
    identifier = str(uuid.UUID(instance_id))
    for candidate in (path, *path.parents):
        if candidate.is_symlink() or candidate.is_junction():
            raise ProvisionError("PGDATA must not contain reparse points")
    marker = path / ".aigoofish-cluster.json"
    if marker.is_symlink() or marker.stat().st_size > 65536:
        raise ProvisionError("invalid cluster ownership marker")
    value = json.loads(marker.read_text(encoding="utf-8"))
    if value.get("format_version") != 1 or value.get("instance_id") != identifier or value.get("engine_version") != "17.11":
        raise ProvisionError("cluster ownership marker does not match")
    uuid.UUID(value["cluster_id"])
    return path.resolve(strict=True), identifier


def provision(pgdata: str, instance_id: str, *, environ=None, seed_root: Path | None = None):
    environment = os.environ if environ is None else environ
    connection = None
    try:
        data_root, identifier = _owned_marker(pgdata, instance_id)
        target = validate_database_target(environment.get(ADMIN_DSN_ENV, ""), environ=environment)
        parameters = dict(target.parameters)
        if parameters["dbname"] != "postgres":
            raise ProvisionError("bootstrap connection must target postgres")
        app_password = _password(environment, APP_PASSWORD_ENV)
        probe_password = _password(environment, PROBE_PASSWORD_ENV)
        if len({app_password, probe_password, parameters["password"]}) != 3:
            raise ProvisionError("database credentials must be distinct")
        roles = DatabaseRoles(parameters["user"], APP_ROLE, PROBE_ROLE).validated()
        # Do not inherit arbitrary libpq options from the supplied DSN.
        selected = {key: parameters[key] for key in ("host", "hostaddr", "port", "dbname", "user", "password")}
        connection = psycopg2.connect(**selected, sslmode="disable", connect_timeout=5,
            options="-c statement_timeout=30000 -c lock_timeout=30000")
        connection.autocommit = True
        with connection.cursor() as cursor:
            cursor.execute("SHOW data_directory")
            if Path(cursor.fetchone()[0]).resolve(strict=True) != data_root:
                raise ProvisionError("server data directory does not match owned PGDATA")
            cursor.execute("SELECT pg_try_advisory_lock(%s)", (_LOCK,))
            if cursor.fetchone()[0] is not True:
                raise ProvisionError("cluster provisioning is already in progress")
            cursor.execute("SELECT datname FROM pg_database WHERE datname NOT IN ('postgres','template0','template1') LIMIT 1")
            if cursor.fetchone() is not None:
                raise ProvisionError("cluster has an existing application database; explicit recovery required")
            cursor.execute("SELECT rolname FROM pg_roles WHERE rolname IN (%s,%s)", (APP_ROLE, PROBE_ROLE))
            if cursor.fetchone() is not None:
                raise ProvisionError("application roles already exist; explicit recovery required")
            # All role statements commit together. A later DB/schema failure is
            # deliberately not repaired by dropping roles or data.
            connection.autocommit = False
            for role, password in ((APP_ROLE, app_password), (PROBE_ROLE, probe_password)):
                cursor.execute(sql.SQL("CREATE ROLE {} LOGIN PASSWORD %s NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS").format(sql.Identifier(role)), (password,))
            connection.commit()
            connection.autocommit = True
            cursor.execute(sql.SQL("CREATE DATABASE {} OWNER {} ENCODING 'UTF8' TEMPLATE template0").format(sql.Identifier(DATABASE_NAME), sql.Identifier(roles.admin)))
            cursor.execute(sql.SQL("COMMENT ON DATABASE {} IS %s").format(sql.Identifier(DATABASE_NAME)), ("aigoofish-instance:" + identifier,))
            parameters["dbname"] = DATABASE_NAME
            dsn = psycopg2.extensions.make_dsn(**{key: parameters[key] for key in ("host", "hostaddr", "port", "dbname", "user", "password")})
            engine = _engine_from_environment({ADMIN_DSN_ENV: dsn})
            try:
                initialize_schema(engine, roles=roles, seed_root=seed_root)
            finally:
                engine.dispose()
        return {"database": DATABASE_NAME, "app_role": APP_ROLE, "probe_role": PROBE_ROLE, "schema_version": 1}
    except Exception:
        logger.error("Portable database provisioning failed; no existing data was removed", extra={"event": "portable_provision_failed"})
        raise ProvisionError("database provisioning failed; inspect initialization state before retrying") from None
    finally:
        if connection is not None:
            try:
                connection.close()  # Releases session advisory lock.
            except Exception:
                logger.error("Portable provisioning connection cleanup failed", extra={"event": "portable_provision_close_failed"})


def main(argv=None):
    parser = argparse.ArgumentParser(description="Provision a new Launcher-owned PostgreSQL instance")
    parser.add_argument("--pgdata", required=True)
    parser.add_argument("--instance-id", required=True)
    args = parser.parse_args(argv)
    try:
        program_root = Path(__file__).resolve().parents[2]
        result = provision(args.pgdata, args.instance_id, seed_root=program_root / "defaults")
    except ProvisionError as error:
        print(str(error), file=__import__("sys").stderr)
        return 2
    print(json.dumps(result))
    return 0
