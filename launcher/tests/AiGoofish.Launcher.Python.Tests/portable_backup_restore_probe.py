"""Private-stdio helpers for the isolated real Host-to-PostgreSQL E2E fixture."""

from __future__ import annotations

import base64
import hashlib
import json
from pathlib import Path
import sys


KEY_CHECK_RELATIVE_PATH = Path("assets") / "portable-backup-restore-key-check.bin"
KEY_CHECK_PLAINTEXT = b"portable-backup-restore-e2e-key-check-v1"
KEY_CHECK_USER_ID = "portable-backup-restore-e2e-fixture"


def _cipher(master_key: str):
    from cryptography.fernet import Fernet
    from cryptography.hazmat.primitives import hashes
    from cryptography.hazmat.primitives.kdf.pbkdf2 import PBKDF2HMAC

    derived = PBKDF2HMAC(
        algorithm=hashes.SHA256(),
        length=32,
        salt=KEY_CHECK_USER_ID.encode("utf-8"),
        iterations=100_000,
    ).derive(hashlib.sha256(master_key.encode("utf-8")).digest())
    return Fernet(base64.urlsafe_b64encode(derived))


def _fixture_path(root_value: str) -> Path:
    root = Path(root_value).resolve(strict=True)
    target = root / KEY_CHECK_RELATIVE_PATH
    if target.parent.resolve(strict=True) != (root / "assets").resolve(strict=True):
        raise ValueError("fixture path mismatch")
    if target.is_symlink() or target.exists() and not target.is_file():
        raise ValueError("fixture file is not a plain file")
    return target


def _table_counts(dsn: str) -> dict[str, int]:
    import psycopg2
    from psycopg2 import sql

    connection = psycopg2.connect(dsn, connect_timeout=5, sslmode="disable")
    try:
        with connection.cursor() as cursor:
            cursor.execute(
                "SELECT tablename FROM pg_catalog.pg_tables "
                "WHERE schemaname = 'public' ORDER BY tablename"
            )
            names = [row[0] for row in cursor.fetchall()]
            result: dict[str, int] = {}
            for name in names:
                cursor.execute(
                    sql.SQL("SELECT count(*) FROM public.{}").format(sql.Identifier(name))
                )
                result[name] = int(cursor.fetchone()[0])
            return result
    finally:
        connection.close()


def main() -> int:
    try:
        if len(sys.argv) != 3 or sys.argv[1] != "--app-root":
            raise ValueError("test app root is required")
        app_root = Path(sys.argv[2]).resolve(strict=True)
        if not app_root.is_dir():
            raise ValueError("test app root is invalid")
        sys.path.insert(0, str(app_root))
        request = json.loads(sys.stdin.buffer.read(16 * 1024 + 1).decode("utf-8"))
        if not isinstance(request, dict):
            raise ValueError("request must be an object")
        action = request.get("action")
        result: dict[str, object]
        if action == "table_counts" and set(request) == {"action", "admin_dsn"}:
            result = {"table_counts": _table_counts(request["admin_dsn"])}
        elif action == "seal_key_fixture" and set(request) == {
            "action", "data_root", "master_key"
        }:
            target = _fixture_path(request["data_root"])
            encrypted = _cipher(request["master_key"]).encrypt(KEY_CHECK_PLAINTEXT)
            with target.open("xb") as output:
                output.write(encrypted)
                output.flush()
            result = {"sealed": True}
        elif action == "verify_key_fixture" and set(request) == {
            "action", "data_root", "master_key"
        }:
            target = _fixture_path(request["data_root"])
            plaintext = _cipher(request["master_key"]).decrypt(target.read_bytes())
            result = {"key_check": plaintext == KEY_CHECK_PLAINTEXT}
        else:
            raise ValueError("unsupported probe request")
        sys.stdout.write(json.dumps(result, ensure_ascii=False, separators=(",", ":")) + "\n")
        return 0
    except Exception:
        # Never echo a DSN, key, archive password, request body, or filesystem
        # value through the child process diagnostics.
        sys.stderr.write("fixture probe failed\n")
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
