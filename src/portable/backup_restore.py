"""Isolated schema-v1 business restore into a newly owned PostgreSQL cluster.

The trusted Launcher prepares and starts an empty PG17 cluster under a new
instance lease. This module never selects an old cluster, starts workers, or
switches the active instance. SQL in a logical dump is executable: callers must
obtain the archive from a trusted source, even though its bytes are authenticated.
"""
from __future__ import annotations

import base64
import hashlib
import json
import logging
import os
import re
import secrets
import shutil
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

import psycopg2
from psycopg2 import sql
from sqlalchemy import text as sa_text
from cryptography.fernet import Fernet
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.kdf.pbkdf2 import PBKDF2HMAC

from src.portable.backup_archive import (
    BackupArchiveError, _assert_no_reparse_ancestry, _private_work_directory,
    _require_disk_space, _restrict_private_path, _safe_relative,
    restore_backup_archive,
)
from src.portable.backup_business import KEY_FIELDS, _clear_private_stage, _recovery_material
from src.portable.maintenance import validate_database_target
from src.portable.provision import APP_ROLE, DATABASE_NAME, PROBE_ROLE, _owned_marker
from src.portable.schema import DatabaseRoles, configure_role_grants
from src.portable.schema_catalog import V1_INDEXES, V2_FINGERPRINT, expected_tables
from src.portable.schema_fingerprint import public_schema_fingerprint
from src.portable.upstream_ddl import MIGRATION_CHECKSUM, MIGRATION_ID, V2_INDEXES

logger = logging.getLogger(__name__)
_HEX = frozenset("0123456789abcdef")
_RESTORE_TIMEOUT = 600
_TOC_LINE = re.compile(r"^\d+; \d+ \d+ (.+)$")
_TOC_OBJECTS = ("TABLE DATA", "SEQUENCE SET", "FK CONSTRAINT", "SEQUENCE OWNED BY",
    "TABLE", "SEQUENCE", "DEFAULT", "CONSTRAINT", "INDEX", "SCHEMA", "ACL")


@dataclass(frozen=True)
class BusinessRestoreResult:
    instance_id: str
    source_instance_id: str
    archive_sha256: str
    table_counts: dict[str, int]
    file_count: int
    revoked_sessions: int
    private_handoff: Path
    schema_version: int = 1
    status: str = "awaiting_target_user_dpapi_and_switch_confirmation"


def _manifest(root: Path, archive_files: tuple[str, ...], metadata: dict) -> dict:
    try:
        value = json.loads((root / "backup.json").read_text(encoding="utf-8"))
        keys = json.loads((root / "recovery-keys.json").read_text(encoding="utf-8"))
        database = value["database"]
        assets = value["files"]
        expected = {"database.dump", "backup.json", "recovery-keys.json"}
        if not isinstance(assets, list) or len(assets) > 10000:
            raise ValueError
        paths: set[str] = set()
        for item in assets:
            name = item["path"]
            digest, size = item["sha256"], item["size"]
            if (not isinstance(name, str) or _safe_relative(name) != name
                or not name.startswith(("state/", "assets/", "results/", "config/"))
                or name.startswith("config/") and name != "config/app.env"
                or name.casefold() in paths or not isinstance(digest, str)
                or len(digest) != 64 or set(digest) - _HEX
                or type(size) is not int or size < 0):
                raise ValueError
            paths.add(name.casefold())
            expected.add("files/" + name)
        counts = database["table_counts"]
        version = value["schema_version"]
        if type(version) is not int or version not in (1, 2):
            raise ValueError
        tables = expected_tables(version)
        if (value["format_version"] != 1 or value["kind"] != "business-backup"
            or metadata.get("schema_version") != version
            or value["instance_id"] != metadata.get("instance_id")
            or value["restore"] != {"postgres_major": 17, "database": DATABASE_NAME,
                "new_instance_required": True, "fresh_database_passwords_required": True,
                "dpapi_reprotection_required": True, "app_role": APP_ROLE,
                "probe_role": PROBE_ROLE, "grant_policy": f"portable-schema-v{version}"}
            or not isinstance(counts, dict) or set(counts) != tables
            or any(type(count) is not int or count < 0 for count in counts.values())
            or type(database["size"]) is not int or database["size"] < 5
            or len(database["sha256"]) != 64 or set(database["sha256"]) - _HEX
            or set(archive_files) != expected
            or keys["format_version"] != 1 or set(keys["keys"]) != KEY_FIELDS):
            raise ValueError
        _recovery_material(keys["keys"])
        for item in assets:
            candidate = root / "files" / item["path"]
            if candidate.stat().st_size != item["size"] or _digest(candidate) != item["sha256"]:
                raise ValueError
        dump = root / "database.dump"
        if dump.stat().st_size != database["size"] or _digest(dump) != database["sha256"]:
            raise ValueError
        with dump.open("rb") as stream:
            if stream.read(5) != b"PGDMP":
                raise ValueError
        return value
    except (OSError, KeyError, TypeError, ValueError, json.JSONDecodeError, BackupArchiveError):
        raise BackupArchiveError("business backup manifest or payload is invalid") from None


def _digest(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(1024 * 1024):
            digest.update(block)
    return digest.hexdigest()


def _tool_environment(parameters: dict, work: Path, database: str) -> dict[str, str]:
    environment = {name: os.environ[name] for name in ("SYSTEMROOT", "WINDIR") if name in os.environ}
    environment.update({"PGHOST": parameters["host"], "PGHOSTADDR": parameters["hostaddr"],
        "PGPORT": str(parameters["port"]), "PGDATABASE": database,
        "PGUSER": parameters["user"], "PGPASSWORD": parameters["password"],
        "PGCONNECT_TIMEOUT": "5", "PGSSLMODE": "disable",
        "TEMP": str(work), "TMP": str(work)})
    return environment


def _inspect_dump_toc(executable: Path, dump: Path, work: Path, *, schema_version: int = 1) -> None:
    """Reject objects outside the archive's exact schema version before SQL."""
    tables_expected = expected_tables(schema_version)
    indexes_expected = V1_INDEXES if schema_version == 1 else V1_INDEXES | V2_INDEXES
    with tempfile.TemporaryFile(dir=work) as listing, tempfile.TemporaryFile(dir=work) as diagnostics:
        completed = subprocess.run([str(executable), "--list", str(dump)],
            stdin=subprocess.DEVNULL, stdout=listing, stderr=diagnostics,
            cwd=work, env={name: os.environ[name] for name in ("SYSTEMROOT", "WINDIR") if name in os.environ},
            timeout=30, check=False, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        if completed.returncode or listing.tell() > 2 * 1024 * 1024 or diagnostics.tell() > 65536:
            raise BackupArchiveError("database archive catalogue could not be checked")
        listing.seek(0)
        try:
            lines = listing.read().decode("utf-8").splitlines()
        except UnicodeDecodeError:
            raise BackupArchiveError("database archive catalogue is invalid") from None
    tables: set[str] = set()
    for line in lines:
        if not line or line.startswith(";"):
            continue
        match = _TOC_LINE.fullmatch(line)
        if not match:
            raise BackupArchiveError("database archive catalogue is invalid")
        body = match.group(1)
        kind = next((item for item in _TOC_OBJECTS if body.startswith(item + " ")), None)
        if kind is None:
            token = body.split(" ", 1)[0]
            if not re.fullmatch(r"[A-Z_]{1,24}", token):
                token = "invalid"
            raise BackupArchiveError("database archive contains unsupported SQL object type " + token)
        fields = body[len(kind) + 1:].split()
        if kind == "ACL" and len(fields) >= 3 and fields[:3] == ["-", "SCHEMA", "public"]:
            continue  # --no-acl prevents this source grant from being replayed.
        if len(fields) < 2 or fields[0] != "public":
            raise BackupArchiveError("database archive contains an unknown schema in " + kind)
        if kind == "SCHEMA":
            raise BackupArchiveError("database archive must not replace public schema")
        if kind == "ACL":
            if len(fields) < 4 or fields[1] != "TABLE" or fields[2] not in tables_expected:
                raise BackupArchiveError("database archive contains unknown privileges")
            continue
        if kind in ("TABLE", "TABLE DATA"):
            if fields[1] not in tables_expected:
                raise BackupArchiveError("database archive contains an unknown table")
            if kind == "TABLE":
                tables.add(fields[1])
        elif kind in ("DEFAULT", "CONSTRAINT", "FK CONSTRAINT"):
            if fields[1] not in tables_expected:
                raise BackupArchiveError("database archive references an unknown table")
        elif kind == "INDEX":
            if fields[1] not in indexes_expected:
                raise BackupArchiveError("database archive contains an unknown index")
        elif kind in ("SEQUENCE", "SEQUENCE SET", "SEQUENCE OWNED BY"):
            if not any(fields[1].startswith(table + "_") for table in tables_expected):
                raise BackupArchiveError("database archive contains an unknown " + kind.lower())
    if tables != tables_expected:
        raise BackupArchiveError("database archive table set differs from its schema version")


def _restore_dump(executable: Path, dump: Path, parameters: dict, work: Path) -> None:
    environment = _tool_environment(parameters, work, DATABASE_NAME)
    with tempfile.TemporaryFile(dir=work) as diagnostics:
        child = subprocess.Popen([str(executable), "--no-password", "--no-owner", "--no-acl",
            "--no-comments", "--no-security-labels", "--single-transaction",
            "--exit-on-error", "--dbname=" + DATABASE_NAME, str(dump)],
            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=diagnostics,
            cwd=work, env=environment, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        try:
            try:
                status = child.wait(timeout=_RESTORE_TIMEOUT)
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait(timeout=5)
                raise BackupArchiveError("database restore timed out") from None
            if status or diagnostics.tell() > 65536:
                raise BackupArchiveError("database restore failed; target remains isolated")
        finally:
            if child.poll() is None:
                child.kill()
                child.wait(timeout=5)


def _assert_new_target(target_data_root: Path, pgdata: Path, staging_parent: Path,
                       handoff_parent: Path, identifier: str) -> Path:
    if not all(path.is_absolute() and path.is_dir() for path in
               (target_data_root, pgdata, staging_parent, handoff_parent)):
        raise BackupArchiveError("restore requires existing absolute target and private-stage parents")
    for path in (target_data_root, pgdata, staging_parent, handoff_parent):
        _assert_no_reparse_ancestry(path, Path(path.anchor))
    target = target_data_root.resolve(strict=True)
    owned, _ = _owned_marker(str(pgdata), identifier)
    if owned != target / "postgres" / "cluster":
        raise BackupArchiveError("restore PGDATA is not below the new target root")
    for parent in (staging_parent, handoff_parent):
        if parent.resolve(strict=True) == target or target in parent.resolve(strict=True).parents:
            raise BackupArchiveError("plaintext recovery stages must remain outside target data")
    return owned


def _assert_new_business_file(destination: Path, target_root: Path) -> None:
    try:
        destination.relative_to(target_root)
    except ValueError:
        raise BackupArchiveError("restore business file escapes target data root") from None
    if destination.exists() or destination.is_symlink():
        raise BackupArchiveError("target business file already exists")
    current = destination.parent
    while True:
        if current.exists() or current.is_symlink():
            _assert_no_reparse_ancestry(current, target_root)
            if not current.is_dir():
                raise BackupArchiveError("target business parent is not a directory")
        if current == target_root:
            break
        current = current.parent


def _connect(parameters: dict, database: str):
    selected = {key: parameters[key] for key in ("host", "hostaddr", "port", "user", "password")}
    return psycopg2.connect(**selected, dbname=database, sslmode="disable", connect_timeout=5,
        options="-c statement_timeout=120000 -c lock_timeout=5000")


def _prepare_empty_database(connection, parameters: dict, pgdata: Path, identifier: str,
                            app_password: str, probe_password: str) -> None:
    with connection.cursor() as cursor:
        cursor.execute("SHOW data_directory")
        if Path(cursor.fetchone()[0]).resolve(strict=True) != pgdata:
            raise BackupArchiveError("restore server does not match owned PGDATA")
        cursor.execute("SHOW server_version_num")
        if not 170000 <= int(cursor.fetchone()[0]) < 180000:
            raise BackupArchiveError("restore requires PostgreSQL 17")
        cursor.execute("SELECT rolsuper FROM pg_roles WHERE rolname=current_user")
        if cursor.fetchone() != (True,):
            raise BackupArchiveError("restore admin is not the new cluster administrator")
        cursor.execute("SELECT pg_try_advisory_lock(%s)", (0x27474F4F50524F56,))
        if cursor.fetchone()[0] is not True:
            raise BackupArchiveError("target cluster is being provisioned")
        cursor.execute("SELECT datname FROM pg_database WHERE datname NOT IN ('postgres','template0','template1') LIMIT 1")
        if cursor.fetchone() is not None:
            raise BackupArchiveError("restore target cluster already has an application database")
        cursor.execute("SELECT rolname FROM pg_roles WHERE rolname IN (%s,%s)", (APP_ROLE, PROBE_ROLE))
        if cursor.fetchone() is not None:
            raise BackupArchiveError("restore target roles already exist")
        cursor.execute("SELECT count(*) FROM pg_stat_activity WHERE datname='postgres' AND pid<>pg_backend_pid()")
        if cursor.fetchone()[0]:
            raise BackupArchiveError("restore target has another active client")
        connection.autocommit = False
        for role, password in ((APP_ROLE, app_password), (PROBE_ROLE, probe_password)):
            cursor.execute(sql.SQL("CREATE ROLE {} LOGIN PASSWORD %s NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS").format(sql.Identifier(role)), (password,))
        connection.commit()
        connection.autocommit = True
        cursor.execute(sql.SQL("CREATE DATABASE {} OWNER {} ENCODING 'UTF8' TEMPLATE template0").format(
            sql.Identifier(DATABASE_NAME), sql.Identifier(parameters["user"])))
        cursor.execute(sql.SQL("COMMENT ON DATABASE {} IS %s").format(sql.Identifier(DATABASE_NAME)),
            ("aigoofish-instance:" + identifier,))


def _reconcile(parameters: dict, pgdata: Path, identifier: str, counts: dict[str, int],
               schema_version: int) -> int:
    connection = _connect(parameters, DATABASE_NAME)
    try:
        with connection.cursor() as cursor:
            cursor.execute("SHOW data_directory")
            if Path(cursor.fetchone()[0]).resolve(strict=True) != pgdata:
                raise BackupArchiveError("restored database is on a different cluster")
            cursor.execute("SELECT shobj_description(oid, 'pg_database') FROM pg_database WHERE datname=current_database()")
            if cursor.fetchone()[0] != "aigoofish-instance:" + identifier:
                raise BackupArchiveError("restored database identity mismatch")
            cursor.execute("SELECT nspname FROM pg_namespace WHERE nspname <> 'public' AND nspname <> 'information_schema' AND nspname NOT LIKE 'pg\\_%' ESCAPE '\\' LIMIT 1")
            if cursor.fetchone():
                raise BackupArchiveError("restored database contains an unknown schema")
            cursor.execute("SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY tablename")
            if {row[0] for row in cursor.fetchall()} != expected_tables(schema_version):
                raise BackupArchiveError("restored database table set differs from its schema version")
            cursor.execute("SELECT version FROM public.app_schema_version LIMIT 2")
            if cursor.fetchall() != [(schema_version,)]:
                raise BackupArchiveError("restored schema version is incompatible")
            if schema_version == 2:
                cursor.execute("SELECT migration_id, checksum FROM public.app_schema_migrations LIMIT 2")
                if cursor.fetchall() != [(MIGRATION_ID, MIGRATION_CHECKSUM)]:
                    raise BackupArchiveError("restored migration audit differs")
            for table, expected in sorted(counts.items()):
                cursor.execute(sql.SQL("SELECT count(*) FROM public.{}").format(sql.Identifier(table)))
                if cursor.fetchone()[0] != expected:
                    raise BackupArchiveError("restored database row counts do not reconcile")
            # A copied web cookie must not retain authorization in a new instance.
            cursor.execute("DELETE FROM public.sessions")
            revoked = cursor.rowcount
        connection.commit()
        return revoked
    finally:
        connection.close()


def _synthetic_key_check(master: str) -> None:
    user_id = "restore-synthetic-fixture"
    key = PBKDF2HMAC(algorithm=hashes.SHA256(), length=32,
        salt=user_id.encode("utf-8"), iterations=100000).derive(hashlib.sha256(master.encode("utf-8")).digest())
    cipher = Fernet(base64.urlsafe_b64encode(key))
    fixture = cipher.encrypt(b"isolated-restore-key-check")
    if cipher.decrypt(fixture) != b"isolated-restore-key-check":
        raise BackupArchiveError("recovered business key failed synthetic decrypt check")


def restore_business_backup(*, archive_file: Path, passphrase: str, postgres_root: Path,
    pgdata: Path, instance_id: str, admin_dsn: str, target_data_root: Path,
    staging_parent: Path, handoff_parent: Path, verify_maintenance: Callable[[], None]) -> BusinessRestoreResult:
    """Restore into a new, running owned cluster; never publish or start business work.

    The returned handoff contains plaintext keys and fresh role passwords under
    current-user ACL outside the target data root. Launcher must DPAPI-protect it
    for the destination user, securely remove it, and obtain switch confirmation.
    A failed restore leaves the new target for diagnosis; it is never retried in
    place and no pre-existing data is removed.
    """
    if not callable(verify_maintenance):
        raise BackupArchiveError("restore requires an instance lease and maintenance verifier")
    stage = None
    handoff = None
    connection = None
    try:
        owned = _assert_new_target(target_data_root, pgdata, staging_parent, handoff_parent, instance_id)
        target_data_root = target_data_root.resolve(strict=True)
        if not postgres_root.is_absolute():
            raise BackupArchiveError("PostgreSQL tool root must be absolute")
        executable = postgres_root / "bin" / "pg_restore.exe"
        _assert_no_reparse_ancestry(executable, Path(executable.anchor))
        if not executable.is_file():
            raise BackupArchiveError("matching PostgreSQL restore tool is missing")
        target = validate_database_target(admin_dsn, environ={})
        parameters = dict(target.parameters)
        if parameters["dbname"] != "postgres":
            raise BackupArchiveError("restore admin connection must target postgres")
        verify_maintenance()
        stage = staging_parent / ("restore-" + secrets.token_hex(12))
        restored = restore_backup_archive(archive_file, stage, passphrase)
        manifest = _manifest(stage, restored.files, dict(restored.metadata))
        schema_version = manifest["schema_version"]
        _inspect_dump_toc(executable, stage / "database.dump", stage,
                          schema_version=schema_version)
        if manifest["instance_id"] == instance_id:
            raise BackupArchiveError("restore requires a fresh instance identity")
        _require_disk_space(target_data_root,
            manifest["database"]["size"] * 4
            + sum(item["size"] for item in manifest["files"]) + 32 * 1024**2)
        for item in manifest["files"]:
            destination = target_data_root / item["path"]
            _assert_new_business_file(destination, target_data_root)
        verify_maintenance()
        connection = _connect(parameters, "postgres")
        connection.autocommit = True
        app_password, probe_password = secrets.token_urlsafe(36), secrets.token_urlsafe(36)
        _prepare_empty_database(connection, parameters, owned, instance_id, app_password, probe_password)
        verify_maintenance()
        _restore_dump(executable, stage / "database.dump", parameters, stage)
        revoked = _reconcile(parameters, owned, instance_id,
                             manifest["database"]["table_counts"], schema_version)
        dsn = psycopg2.extensions.make_dsn(**{**{key: parameters[key] for key in
            ("host", "hostaddr", "port", "user", "password")}, "dbname": DATABASE_NAME})
        from src.portable.schema import _engine_from_environment, ADMIN_DATABASE_ENVIRONMENT_VARIABLE
        engine = _engine_from_environment({ADMIN_DATABASE_ENVIRONMENT_VARIABLE: dsn})
        try:
            if schema_version == 2:
                with engine.begin() as validation:
                    validation.execute(sa_text('SET LOCAL search_path = pg_catalog, "public"'))
                    if public_schema_fingerprint(validation) != V2_FINGERPRINT:
                        raise BackupArchiveError("restored schema v2 structure differs")
            configure_role_grants(engine, DatabaseRoles(parameters["user"], APP_ROLE, PROBE_ROLE))
        finally:
            engine.dispose()
        keys = json.loads((stage / "recovery-keys.json").read_text(encoding="utf-8"))["keys"]
        _synthetic_key_check(keys["encryption_master_key"])
        verify_maintenance()
        for item in manifest["files"]:
            source = stage / "files" / item["path"]
            destination = target_data_root / item["path"]
            _assert_new_business_file(destination, target_data_root)
            destination.parent.mkdir(parents=True, exist_ok=True)
            with source.open("rb") as reader, destination.open("xb") as writer:
                shutil.copyfileobj(reader, writer, length=1024 * 1024)
                writer.flush()
                os.fsync(writer.fileno())
            if _digest(destination) != item["sha256"]:
                raise BackupArchiveError("restored business file hash mismatch")
        handoff = _private_work_directory(handoff_parent, ".restore-handoff-")
        payload = handoff / "secrets.json"
        with payload.open("xb") as writer:
            _restrict_private_path(payload, directory=False)
            writer.write((json.dumps({"format_version": 1, "instance_id": instance_id,
                "encryption_master_key": keys["encryption_master_key"],
                "secret_key": keys["secret_key"],
                "app_database_password": app_password,
                "probe_database_password": probe_password}, ensure_ascii=False) + "\n").encode("utf-8"))
            writer.flush()
            os.fsync(writer.fileno())
        _clear_private_stage(stage, staging_parent)
        stage = None
        return BusinessRestoreResult(instance_id, manifest["instance_id"], restored.sha256,
            dict(manifest["database"]["table_counts"]), len(manifest["files"]), revoked, handoff,
            schema_version=schema_version)
    except BackupArchiveError:
        raise
    except Exception:
        logger.error("Isolated portable business restore failed", extra={"event": "portable_business_restore_failed"})
        raise BackupArchiveError("isolated business restore failed; inspect new target before retrying") from None
    finally:
        if connection is not None:
            try:
                connection.close()
            except Exception:
                logger.error("Restore cluster connection cleanup failed")
        if stage is not None and stage.exists():
            try:
                _clear_private_stage(stage, staging_parent)
            except (OSError, BackupArchiveError):
                logger.error("Private restore stage cleanup failed at %s", stage)
        if handoff is not None and stage is not None:
            try:
                _clear_private_stage(handoff, handoff_parent)
            except (OSError, BackupArchiveError):
                logger.error("Failed restore handoff cleanup failed at %s", handoff)
        elif handoff is not None and not (handoff / "secrets.json").exists():
            try:
                handoff.rmdir()
            except OSError:
                logger.error("Empty restore handoff cleanup failed at %s", handoff)
