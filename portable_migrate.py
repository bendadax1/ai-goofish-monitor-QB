"""Launcher 控制的显式 schema 002 升级入口；不在普通启动时执行。"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import struct
import sys

from src.portable.backup_archive import (
    BackupArchiveError, MAGIC, MAX_ARCHIVE_BYTES, MAX_HEADER_BYTES,
    _assert_no_reparse_ancestry, _parse_header, _read_exact,
)
from src.portable.schema import ADMIN_DATABASE_ENVIRONMENT_VARIABLE, DatabaseRoles, _engine_from_environment
from src.portable.upstream_migration import migrate_upstream_schema


def _read_frame(limit: int) -> dict:
    raw = sys.stdin.buffer.readline(limit + 1)
    if not raw or len(raw) > limit or not raw.endswith(b"\n"):
        raise ValueError("migration control frame is invalid")
    try:
        frame = json.loads(raw.decode("utf-8"))
    except (UnicodeError, ValueError):
        raise ValueError("migration control frame is invalid") from None
    if not isinstance(frame, dict):
        raise ValueError("migration control frame is invalid")
    return frame


def _send_frame(frame: dict) -> None:
    sys.stdout.buffer.write((json.dumps(frame, separators=(",", ":")) + "\n").encode("utf-8"))
    sys.stdout.buffer.flush()


def _verify_backup(path: Path, digest: str, instance_id: str, data_root: Path) -> None:
    if (not path.is_absolute() or not data_root.is_absolute() or
        len(digest) != 64 or set(digest) - set("0123456789abcdef")):
        raise BackupArchiveError("migration backup proof is invalid")
    _assert_no_reparse_ancestry(path, Path(path.anchor))
    try:
        path.resolve(strict=True).relative_to(data_root.resolve(strict=True))
    except ValueError:
        pass
    else:
        raise BackupArchiveError("migration backup is inside the data root")
    if not path.is_file() or path.stat().st_size > MAX_ARCHIVE_BYTES:
        raise BackupArchiveError("migration backup is missing or oversized")
    observed = hashlib.sha256()
    with path.open("rb") as stream:
        if _read_exact(stream, len(MAGIC)) != MAGIC:
            raise BackupArchiveError("migration backup header is invalid")
        header_size = struct.unpack(">I", _read_exact(stream, 4))[0]
        if not 2 <= header_size <= MAX_HEADER_BYTES:
            raise BackupArchiveError("migration backup header is invalid")
        metadata, _, _ = _parse_header(_read_exact(stream, header_size))
        if metadata["instance_id"] != instance_id or metadata["schema_version"] != 1:
            raise BackupArchiveError("migration backup does not match the v1 instance")
        stream.seek(0)
        while block := stream.read(1024 * 1024):
            observed.update(block)
    if observed.hexdigest() != digest:
        raise BackupArchiveError("migration backup digest differs")


def run() -> int:
    engine = None
    try:
        request = _read_frame(64 * 1024)
        if set(request) != {
            "type", "session", "data_root", "pgdata", "instance_id", "admin_dsn",
            "admin_role", "app_role", "probe_role", "backup_file", "backup_sha256",
        } or request["type"] != "migrate":
            raise ValueError("migration request contract is invalid")
        session = request["session"]
        if not isinstance(session, str) or len(session) != 64 or set(session) - set("0123456789abcdef"):
            raise ValueError("migration session is invalid")
        counter = 0

        def verify_maintenance() -> None:
            nonlocal counter
            if counter >= 4:
                raise ValueError("migration maintenance proof count exceeded")
            counter += 1
            _send_frame({"type": "verify", "session": session, "counter": counter})
            answer = _read_frame(512)
            if answer != {"type": "verified", "session": session, "counter": counter}:
                raise ValueError("migration maintenance proof was refused")

        def verify_backup() -> None:
            _verify_backup(Path(request["backup_file"]), request["backup_sha256"],
                           request["instance_id"], Path(request["data_root"]))

        engine = _engine_from_environment({ADMIN_DATABASE_ENVIRONMENT_VARIABLE: request["admin_dsn"]})
        result = migrate_upstream_schema(
            engine, DatabaseRoles(request["admin_role"], request["app_role"], request["probe_role"]),
            pgdata=Path(request["pgdata"]), instance_id=request["instance_id"],
            verify_maintenance=verify_maintenance, verify_backup=verify_backup)
        _send_frame({"type": "complete", "session": session, "schema_version": result.version,
                     "status": result.status, "checks": counter})
        return 0
    except Exception:
        print("Portable schema migration failed safely", file=sys.stderr)
        return 1
    finally:
        if engine is not None:
            try:
                engine.dispose()
            except Exception:
                print("Portable migration connection cleanup failed", file=sys.stderr)


if __name__ == "__main__":
    raise SystemExit(run())
