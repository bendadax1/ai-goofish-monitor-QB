"""Fixed portable business-backup entry, controlled over private stdio pipes.

Only the Launcher starts this entry. Secrets arrive in one bounded stdin frame;
each backend quiescence callback makes a fresh round trip to the Launcher.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

from src.portable.backup_archive import BackupArchiveError
from src.portable.backup_business import create_business_backup


def _read_frame(limit: int) -> dict:
    raw = sys.stdin.buffer.readline(limit + 1)
    if not raw or len(raw) > limit or not raw.endswith(b"\n"):
        raise BackupArchiveError("backup control frame is missing or oversized")
    try:
        frame = json.loads(raw.decode("utf-8"))
    except (UnicodeError, ValueError):
        raise BackupArchiveError("backup control frame is invalid") from None
    if not isinstance(frame, dict):
        raise BackupArchiveError("backup control frame must be an object")
    return frame


def _send_frame(frame: dict) -> None:
    sys.stdout.buffer.write((json.dumps(frame, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8"))
    sys.stdout.buffer.flush()


def run() -> int:
    try:
        request = _read_frame(64 * 1024)
        if set(request) != {"type", "data_root", "postgres_root", "pgdata", "instance_id",
                            "app_version", "admin_dsn", "recovery_keys", "destination",
                            "passphrase", "session"} or request["type"] != "backup":
            raise BackupArchiveError("backup request contract is invalid")
        if not isinstance(request["session"], str) or len(request["session"]) != 64:
            raise BackupArchiveError("backup session contract is invalid")
        counter = 0

        def verify_quiesced() -> None:
            nonlocal counter
            counter += 1
            _send_frame({"type": "verify", "session": request["session"], "counter": counter})
            answer = _read_frame(512)
            if answer != {"type": "verified", "session": request["session"], "counter": counter}:
                raise BackupArchiveError("Launcher quiescence proof was refused")

        result = create_business_backup(
            data_root=Path(request["data_root"]), postgres_root=Path(request["postgres_root"]),
            pgdata=Path(request["pgdata"]), instance_id=request["instance_id"],
            app_version=request["app_version"], admin_dsn=request["admin_dsn"],
            recovery_keys=request["recovery_keys"], destination=Path(request["destination"]),
            passphrase=request["passphrase"], verify_quiesced=verify_quiesced)
        _send_frame({"type": "complete", "session": request["session"], "sha256": result.sha256,
                     "files": len(result.files)})
        return 0
    except Exception:
        # Do not print exception text: Python or a dependency may include a DSN,
        # passphrase, key, or private file path in its message.
        print("Portable business backup failed safely", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(run())
