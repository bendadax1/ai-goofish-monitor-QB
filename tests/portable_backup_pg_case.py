"""Real database dump/restore check inside the schema suite's owned cluster."""
import hashlib
import base64
import json
import os
from pathlib import Path
import secrets
import subprocess
import uuid

from psycopg2 import sql

from src.portable.backup_archive import restore_backup_archive
from src.portable.backup_business import create_business_backup
from src.portable.backup_business import _clear_private_stage
from src.portable.backup_restore import restore_business_backup
from tests import portable_pg_smoke as pg


def check_backup_roundtrip(state, port, admin, password, instance_id, runtime_root):
    business_data = state.root / "backup-business-fixture"
    (business_data / "assets").mkdir(parents=True)
    master = base64.b64encode(secrets.token_bytes(32)).decode("ascii")
    signing = base64.b64encode(secrets.token_bytes(32)).decode("ascii")
    state.raw_secrets.extend((master, signing))
    (business_data / "assets/中文.txt").write_text("本地恢复样本", encoding="utf-8")
    from cryptography.fernet import Fernet
    from cryptography.hazmat.primitives.kdf.pbkdf2 import PBKDF2HMAC
    from cryptography.hazmat.primitives import hashes
    def cipher(key):
        derived = PBKDF2HMAC(algorithm=hashes.SHA256(), length=32, salt=b"fixture-owner", iterations=100000).derive(hashlib.sha256(key.encode()).digest())
        return Fernet(base64.urlsafe_b64encode(derived))
    (business_data / "assets/encrypted.fixture").write_bytes(cipher(master).encrypt(b"fixture-only-sensitive-value"))
    archive = state.root / "fixture.gfbk"
    passphrase = secrets.token_urlsafe(24)
    state.raw_secrets.append(passphrase)
    def verify_fixture_quiesced():
        # This fixture never starts application writers. Also reject unexpected
        # connections before/between file and database snapshot operations.
        check = pg._connect(port=port, database="aigoofish", user=admin, password=password)
        try:
            with check.cursor() as cursor:
                cursor.execute("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid()")
                assert cursor.fetchone()[0] == 0
        finally:
            check.close()
    create_business_backup(data_root=business_data, postgres_root=runtime_root, pgdata=state.data_root,
        instance_id=instance_id, app_version="fixture-v1", admin_dsn=pg._dsn(port=port, database="aigoofish", user=admin, password=password),
        recovery_keys={"encryption_master_key": master, "secret_key": signing}, destination=archive,
        passphrase=passphrase, verify_quiesced=verify_fixture_quiesced)
    restored = state.root / "restored-fixture"
    restore_backup_archive(archive, restored, passphrase)
    report = json.loads((restored / "backup.json").read_text(encoding="utf-8"))
    assert hashlib.sha256((restored / "database.dump").read_bytes()).hexdigest() == report["database"]["sha256"]
    assert (restored / "files/assets/中文.txt").read_text(encoding="utf-8") == "本地恢复样本"
    recovered_keys = json.loads((restored / "recovery-keys.json").read_text(encoding="utf-8"))["keys"]
    assert recovered_keys["secret_key"] == signing
    assert cipher(recovered_keys["encryption_master_key"]).decrypt((restored / "files/assets/encrypted.fixture").read_bytes()) == b"fixture-only-sensitive-value"
    assert master.encode() not in archive.read_bytes()
    database = "restore_" + secrets.token_hex(6)
    connection = pg._connect(port=port, database="postgres", user=admin, password=password)
    try:
        connection.autocommit = True
        with connection.cursor() as cursor:
            cursor.execute(sql.SQL("CREATE DATABASE {} TEMPLATE template0").format(sql.Identifier(database)))
    finally:
        connection.close()
    environment = {key: os.environ[key] for key in ("SYSTEMROOT", "WINDIR") if key in os.environ}
    environment.update(PGHOST="127.0.0.1", PGHOSTADDR="127.0.0.1", PGPORT=str(port),
        PGUSER=admin, PGPASSWORD=password, PGDATABASE=database, PGCONNECT_TIMEOUT="5", PGSSLMODE="disable",
        TEMP=str(state.root), TMP=str(state.root))
    command = [str(runtime_root / "bin/pg_restore.exe"), "--no-password", "--no-owner", "--no-acl",
        "--single-transaction", "--exit-on-error", "--dbname=" + database, str(restored / "database.dump")]
    completed = subprocess.run(command, stdin=subprocess.DEVNULL, capture_output=True,
        cwd=state.root, env=environment, timeout=60, creationflags=getattr(subprocess,"CREATE_NO_WINDOW",0))
    if completed.returncode:
        raise pg.SmokeFailure("isolated logical restore failed")
    connection = pg._connect(port=port, database=database, user=admin, password=password)
    try:
        with connection.cursor() as cursor:
            for table, count in report["database"]["table_counts"].items():
                cursor.execute(sql.SQL("SELECT count(*) FROM public.{}").format(sql.Identifier(table)))
                if cursor.fetchone()[0] != count:
                    raise pg.SmokeFailure("logical restore row count mismatch")
            cursor.execute("SELECT version FROM public.app_schema_version")
            assert cursor.fetchall() == [(1,)]
    finally:
        connection.close()
    _check_full_isolated_restore(state, archive, passphrase, instance_id, runtime_root)
    print("PORTABLE_BUSINESS_BACKUP_DB_FILES_KEYS_RESTORE=PASS")


def _check_full_isolated_restore(state, archive, passphrase, source_instance_id, runtime_root):
    target_root = state.root / "new-recovery-target"
    pgdata = target_root / "postgres" / "cluster"
    pgdata.parent.mkdir(parents=True)
    (target_root / "config").mkdir()
    target_state = pg.SmokeState(root=target_root, data_root=pgdata, pg_log=target_root / "postgres.log")
    target_id = str(uuid.uuid4())
    target_admin = "recovery_admin_" + secrets.token_hex(4)
    target_password = secrets.token_urlsafe(36)
    state.raw_secrets.append(target_password)
    target_port = pg._reserve_loopback_port()
    password_file = target_root / "initdb-password.txt"
    target_started = False
    result = None
    try:
        password_file.touch(exist_ok=False)
        pg._restrict_secret_file(password_file)
        password_file.write_text(target_password + "\n", encoding="utf-8")
        pg._run_tool([runtime_root / "bin/initdb.exe", "-D", pgdata, "--username", target_admin,
            "--pwfile", password_file, "--encoding=UTF8", "--locale=C",
            "--auth-local=scram-sha-256", "--auth-host=scram-sha-256", "--no-instructions"],
            stage="restore-initdb", diagnostic_root=state.root / "diagnostics", timeout=60)
        pg._assert_scram_hba(pgdata)
        pg._append_server_configuration(pgdata, target_port)
        (pgdata / ".aigoofish-cluster.json").write_text(json.dumps({
            "format_version": 1, "instance_id": target_id, "cluster_id": str(uuid.uuid4()),
            "engine_version": "17.11"}), encoding="utf-8")
        target_state.postgres_start_attempted = True
        target_state.postgres_port = target_port
        target_state.postgres_start_epoch = __import__("time").time()
        pg._run_tool([runtime_root / "bin/pg_ctl.exe", "start", "-D", pgdata,
            "-l", target_state.pg_log, "-o", f"-p {target_port}", "-w", "-t", "30"],
            stage="restore-pg-start", diagnostic_root=state.root / "diagnostics",
            timeout=45, direct_output=True)
        target_state.postgres_pid = pg._read_owned_postmaster_identity(target_state).pid
        target_started = True
        staging = state.root / "restore-staging"
        handoff = state.root / "restore-handoff"
        staging.mkdir()
        handoff.mkdir()
        def verify_fixture_maintenance():
            # No application writers exist in this newly initialized cluster.
            check = pg._connect(port=target_port, database="postgres", user=target_admin,
                password=target_password)
            try:
                with check.cursor() as cursor:
                    cursor.execute("SHOW data_directory")
                    assert Path(cursor.fetchone()[0]).resolve(strict=True) == pgdata.resolve(strict=True)
            finally:
                check.close()
        result = restore_business_backup(archive_file=archive, passphrase=passphrase,
            postgres_root=runtime_root, pgdata=pgdata, instance_id=target_id,
            admin_dsn=pg._dsn(port=target_port, database="postgres", user=target_admin,
                password=target_password), target_data_root=target_root,
            staging_parent=staging, handoff_parent=handoff,
            verify_maintenance=verify_fixture_maintenance)
        assert result.source_instance_id == source_instance_id
        assert result.instance_id == target_id
        assert result.status == "awaiting_target_user_dpapi_and_switch_confirmation"
        assert (target_root / "assets/中文.txt").read_text(encoding="utf-8") == "本地恢复样本"
        payload = json.loads((result.private_handoff / "secrets.json").read_text(encoding="utf-8"))
        assert payload["instance_id"] == target_id
        assert payload["encryption_master_key"] == json.loads(
            (state.root / "restored-fixture" / "recovery-keys.json").read_text(encoding="utf-8"))["keys"]["encryption_master_key"]
        _clear_private_stage(result.private_handoff, handoff)
        assert not any(handoff.iterdir()) and not any(staging.iterdir())
        assert not (target_root / "recovery-keys.json").exists()
        assert not (target_root / "secrets.json").exists()
    finally:
        if result is not None and result.private_handoff.exists():
            _clear_private_stage(result.private_handoff, handoff)
        if password_file.exists():
            password_file.unlink()
        if target_started or target_state.postgres_start_attempted:
            if not pg._stop_postgres(runtime_root / "bin/pg_ctl.exe", target_state):
                raise pg.SmokeFailure("new restore cluster did not stop safely")
