"""Fixed target-only business restore entry controlled over private stdio.

The Launcher owns the target lease and starts this helper with no secrets in
argv or environment. The helper never starts Web/workers and never publishes
or switches the restored target.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

from src.portable.backup_restore import restore_business_backup
from src.portable.backup_archive import BackupArchiveError


def _read_frame(limit: int) -> dict:
    raw = sys.stdin.buffer.readline(limit + 1)
    if not raw or len(raw) > limit or not raw.endswith(b"\n"):
        raise BackupArchiveError("restore control frame is missing or oversized")
    try:
        frame = json.loads(raw.decode("utf-8"))
    except (UnicodeError, ValueError):
        raise BackupArchiveError("restore control frame is invalid") from None
    if not isinstance(frame, dict):
        raise BackupArchiveError("restore control frame must be an object")
    return frame


def _send_frame(frame: dict) -> None:
    sys.stdout.buffer.write(
        (json.dumps(frame, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
    )
    sys.stdout.buffer.flush()


def run() -> int:
    try:
        request = _read_frame(64 * 1024)
        expected = {
            "type", "archive_file", "passphrase", "postgres_root", "pgdata",
            "instance_id", "admin_dsn", "target_data_root", "staging_parent",
            "handoff_parent", "session",
        }
        if set(request) != expected or request["type"] != "restore":
            raise BackupArchiveError("restore request contract is invalid")
        session = request["session"]
        if not isinstance(session, str) or len(session) != 64:
            raise BackupArchiveError("restore session contract is invalid")
        counter = 0

        def verify_maintenance() -> None:
            nonlocal counter
            if counter >= 4:
                raise BackupArchiveError("restore maintenance proof count exceeded")
            counter += 1
            _send_frame({"type": "verify", "session": session, "counter": counter})
            answer = _read_frame(512)
            if answer != {"type": "verified", "session": session, "counter": counter}:
                raise BackupArchiveError("Launcher maintenance proof was refused")

        result = restore_business_backup(
            archive_file=Path(request["archive_file"]),
            passphrase=request["passphrase"],
            postgres_root=Path(request["postgres_root"]),
            pgdata=Path(request["pgdata"]),
            instance_id=request["instance_id"],
            admin_dsn=request["admin_dsn"],
            target_data_root=Path(request["target_data_root"]),
            staging_parent=Path(request["staging_parent"]),
            handoff_parent=Path(request["handoff_parent"]),
            verify_maintenance=verify_maintenance,
        )
        _send_frame({
            "type": "complete",
            "session": session,
            "instance_id": result.instance_id,
            "source_instance_id": result.source_instance_id,
            "archive_sha256": result.archive_sha256,
            "schema_version": result.schema_version,
            "tables": result.table_counts,
            "files": result.file_count,
            "revoked_sessions": result.revoked_sessions,
            "handoff_directory": str(result.private_handoff),
            "status": result.status,
        })
        return 0
    except Exception:
        # Dependency messages can contain DSNs, passphrases, keys, or private
        # paths. Keep the fixed entry's stderr generic.
        print("Portable business restore failed safely", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(run())
