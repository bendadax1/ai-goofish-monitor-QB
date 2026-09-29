"""Explicit, owned PostgreSQL logical snapshot; never copies or modifies PGDATA.

This is the database part of a backup, not a full business backup. The caller
must hold the Launcher instance lease and quiesce business writers when combining
the returned dump with files and protected key export.
"""
from __future__ import annotations

import hashlib
import logging
import os
import subprocess
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path

import psycopg2
from psycopg2 import sql

from src.portable.backup_archive import (
    BackupArchiveError, _assert_no_reparse_ancestry, _commit_new_file,
    _private_work_directory, _require_disk_space,
)
from src.portable.maintenance import validate_database_target
from src.portable.provision import _owned_marker
from src.portable.schema_catalog import expected_tables

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class DatabaseDump:
    path: Path
    sha256: str
    size: int
    schema_version: int
    table_counts: tuple[tuple[str, int], ...]


def _tool_environment(parameters: dict, temporary: Path) -> dict[str, str]:
    environment = {name: os.environ[name] for name in ("SYSTEMROOT", "WINDIR") if name in os.environ}
    environment.update({
        "PGHOST": parameters["host"], "PGHOSTADDR": parameters["hostaddr"],
        "PGPORT": str(parameters["port"]), "PGDATABASE": parameters["dbname"],
        "PGUSER": parameters["user"], "PGPASSWORD": parameters["password"],
        "PGSSLMODE": "disable", "PGCONNECT_TIMEOUT": "5",
        "PGOPTIONS": "-c default_transaction_read_only=on -c statement_timeout=120000 -c lock_timeout=5000",
        "TEMP": str(temporary), "TMP": str(temporary),
    })
    return environment


def _dump_process(executable: Path, snapshot: str, output: Path, environment: dict,
                  max_bytes: int, timeout: float) -> None:
    """Bound output/diagnostics; abort only this newly-created read-only helper."""
    with output.open("xb") as destination, tempfile.TemporaryFile(dir=output.parent) as diagnostics:
        child = subprocess.Popen([str(executable), "--format=custom", "--no-password",
            "--compress=0", "--lock-wait-timeout=5000", "--snapshot=" + snapshot],
            stdin=subprocess.DEVNULL, stdout=destination, stderr=diagnostics,
            cwd=output.parent, env=environment,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        deadline = time.monotonic() + timeout
        try:
            while child.poll() is None:
                if time.monotonic() >= deadline:
                    raise BackupArchiveError("database backup timed out")
                if os.fstat(destination.fileno()).st_size > max_bytes or os.fstat(diagnostics.fileno()).st_size > 65536:
                    raise BackupArchiveError("database backup output exceeded limits")
                _require_disk_space(output.parent, 1024 * 1024)
                time.sleep(.05)
            if child.returncode != 0:
                raise BackupArchiveError("database dump helper failed; no backup was published")
            if os.fstat(destination.fileno()).st_size > max_bytes or os.fstat(diagnostics.fileno()).st_size > 65536:
                raise BackupArchiveError("database backup output exceeded limits")
            # Warnings can indicate an incomplete dump; fail closed and retain
            # no raw database identifiers or connection text in public errors.
            if os.fstat(diagnostics.fileno()).st_size:
                raise BackupArchiveError("database dump reported diagnostics; backup requires investigation")
            destination.flush()
            os.fsync(destination.fileno())
        finally:
            if child.poll() is None:
                child.kill()
                child.wait(timeout=5)


def dump_owned_database(*, postgres_root: Path, pgdata: Path, instance_id: str,
                        admin_dsn: str, destination: Path, max_bytes: int = 8 * 1024**3,
                        timeout: float = 180) -> DatabaseDump:
    """Export one exact schema-v1/v2 database after checking PGDATA and ownership.

    Passwords are passed only in the helper's explicit environment, never argv.
    The output is a private logical dump, intended for immediate encryption.
    No drop, schema initialization, task execution or role changes occur here.
    """
    connection = None
    work = None
    try:
        if type(max_bytes) is not int or max_bytes < 1 or not 1 <= timeout <= 600:
            raise BackupArchiveError("database backup limits are invalid")
        if not destination.is_absolute() or destination.exists() or not destination.parent.is_dir():
            raise BackupArchiveError("database dump destination must be a new absolute file")
        _assert_no_reparse_ancestry(destination.parent, Path(destination.anchor))
        if not postgres_root.is_absolute():
            raise BackupArchiveError("PostgreSQL tools require an absolute root")
        executable = postgres_root / "bin/pg_dump.exe"
        _assert_no_reparse_ancestry(executable, Path(executable.anchor))
        owned_data, identifier = _owned_marker(str(pgdata), instance_id)
        target = validate_database_target(admin_dsn, environ={})
        parameters = {key: target.parameters[key] for key in ("host", "hostaddr", "port", "dbname", "user", "password")}
        if parameters["dbname"] != "aigoofish":
            raise BackupArchiveError("backup must target the owned application database")
        connection = psycopg2.connect(**parameters, sslmode="disable", connect_timeout=5,
            options="-c statement_timeout=120000 -c lock_timeout=5000")
        connection.set_session(isolation_level="REPEATABLE READ", readonly=True, autocommit=False)
        with connection.cursor() as cursor:
            cursor.execute("SHOW data_directory")
            if Path(cursor.fetchone()[0]).resolve(strict=True) != owned_data:
                raise BackupArchiveError("backup server PGDATA does not match the owned cluster")
            cursor.execute("SHOW server_version_num")
            if not 170000 <= int(cursor.fetchone()[0]) < 180000:
                raise BackupArchiveError("backup requires PostgreSQL major version 17")
            cursor.execute("SELECT shobj_description(oid, 'pg_database') FROM pg_database WHERE datname = current_database()")
            if cursor.fetchone()[0] != "aigoofish-instance:" + identifier:
                raise BackupArchiveError("backup database ownership does not match the instance")
            cursor.execute("SELECT version FROM public.app_schema_version LIMIT 2")
            versions = cursor.fetchall()
            if len(versions) != 1 or versions[0][0] not in (1, 2):
                raise BackupArchiveError("backup schema is not supported")
            schema_version = versions[0][0]
            cursor.execute("SELECT pg_database_size(current_database())")
            estimated = int(cursor.fetchone()[0])
            _require_disk_space(destination.parent, min(max_bytes, max(estimated * 2, 16 * 1024**2)))
            cursor.execute("SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY tablename LIMIT 1001")
            tables = [row[0] for row in cursor.fetchall()]
            if len(tables) > 1000:
                raise BackupArchiveError("backup table inventory exceeded limit")
            if set(tables) != expected_tables(schema_version):
                raise BackupArchiveError("backup table inventory differs from schema version")
            counts = []
            for table in tables:
                cursor.execute(sql.SQL("SELECT count(*) FROM public.{}").format(sql.Identifier(table)))
                counts.append((table, int(cursor.fetchone()[0])))
            cursor.execute("SELECT pg_export_snapshot()")
            snapshot = cursor.fetchone()[0]
            work = _private_work_directory(destination.parent, ".pg-backup-")
            pending = work / "database.dump"
            _dump_process(executable, snapshot, pending, _tool_environment(parameters, work), max_bytes, timeout)
            digest = hashlib.sha256()
            with pending.open("rb") as stream:
                if stream.read(5) != b"PGDMP":
                    raise BackupArchiveError("database dump is not a custom archive")
                stream.seek(0)
                while block := stream.read(1024 * 1024):
                    digest.update(block)
            size = pending.stat().st_size
            _commit_new_file(pending, destination)
            return DatabaseDump(destination, digest.hexdigest(), size, schema_version, tuple(counts))
    except BackupArchiveError:
        raise
    except Exception:
        logger.error("Portable database backup failed", extra={"event": "portable_database_backup_failed"})
        raise BackupArchiveError("database backup could not be completed safely") from None
    finally:
        if connection is not None:
            try:
                connection.close()
            except Exception:
                logger.error("Backup snapshot connection cleanup failed")
        if work is not None:
            try:
                pending = work / "database.dump"
                pending.unlink(missing_ok=True)
                work.rmdir()
            except OSError:
                logger.error("Private database backup staging cleanup failed")
