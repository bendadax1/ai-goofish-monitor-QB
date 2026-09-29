"""Quiesced database + file + recovery-key archive assembly.

The caller owns the instance lease and must provide a live quiescence verifier.
No CLI is exposed until Launcher owns that lifecycle; no backup is installed or
restored over an existing instance by this module.
"""
from __future__ import annotations

import base64
import hashlib
import json
import logging
from pathlib import Path
import shutil
import uuid
from typing import Callable, Mapping

from src.portable.backup_archive import (
    BackupArchiveError, BackupArchiveResult, _assert_no_reparse_ancestry,
    _commit_new_file, _private_work_directory, _require_disk_space, create_backup_archive,
)
from src.portable.backup_inventory import inventory_business_files
from src.portable.backup_postgres import dump_owned_database

logger = logging.getLogger(__name__)
KEY_FIELDS = frozenset({"encryption_master_key", "secret_key"})


def _recovery_material(keys: Mapping[str, str]) -> bytes:
    if not isinstance(keys, Mapping) or set(keys) != KEY_FIELDS:
        raise BackupArchiveError("business recovery keys are incomplete")
    for value in keys.values():
        if not isinstance(value, str) or len(value) > 256:
            raise BackupArchiveError("business recovery key is invalid")
        try:
            decoded = base64.b64decode(value, validate=True)
        except (ValueError, TypeError):
            raise BackupArchiveError("business recovery key is invalid") from None
        if len(decoded) != 32:
            raise BackupArchiveError("business recovery key must contain 32 random bytes")
    return (json.dumps({"format_version": 1, "keys": dict(keys)}, sort_keys=True) + "\n").encode("utf-8")


def create_business_backup(*, data_root: Path, postgres_root: Path, pgdata: Path,
                           instance_id: str, app_version: str, admin_dsn: str,
                           recovery_keys: Mapping[str, str], destination: Path,
                           passphrase: str, verify_quiesced: Callable[[], None]) -> BackupArchiveResult:
    """Build a non-overwriting encrypted archive while the caller keeps writers stopped.

    verify_quiesced must raise when the instance lease or stopped-writer proof is
    no longer valid. A no-op callback is not valid production integration.
    Database login passwords/control tokens/DPAPI blobs are intentionally not
    exported: restore must create fresh role passwords and re-protect business
    keys for the destination Windows user.
    """
    if not callable(verify_quiesced):
        raise BackupArchiveError("backup requires a quiescence verifier")
    if not destination.is_absolute() or destination.exists() or not destination.parent.is_dir():
        raise BackupArchiveError("backup requires a new destination file")
    work = None
    encrypted_pending = None
    try:
        if not data_root.is_absolute():
            raise BackupArchiveError("backup data root must be absolute")
        try:
            destination.parent.resolve(strict=True).relative_to(data_root.resolve(strict=True))
        except ValueError:
            pass
        else:
            raise BackupArchiveError("backup destination must be outside the data root")
        _assert_no_reparse_ancestry(destination.parent, Path(destination.anchor))
        key_payload = _recovery_material(recovery_keys)
        verify_quiesced()
        inventory = inventory_business_files(data_root)
        if not inventory.file_scope_ready:
            raise BackupArchiveError("unknown data paths prevent complete backup")
        _require_disk_space(destination.parent, inventory.total_bytes * 3 + 32 * 1024**2)
        work = _private_work_directory(destination.parent, ".business-backup-")
        database = dump_owned_database(postgres_root=postgres_root, pgdata=pgdata,
            instance_id=instance_id, admin_dsn=admin_dsn, destination=work / "database.dump")
        verify_quiesced()
        selected = ["database.dump", "recovery-keys.json", "backup.json"]
        assets = []
        for item in inventory.files:
            source = data_root / item.path
            _assert_no_reparse_ancestry(source, Path(source.anchor))
            before = source.stat()
            if (before.st_size, before.st_mtime_ns) != (item.size, item.modified_ns):
                raise BackupArchiveError("business file changed after inventory")
            target = work / "files" / item.path
            target.parent.mkdir(parents=True, exist_ok=True)
            digest = hashlib.sha256()
            size = 0
            with source.open("rb") as reader, target.open("xb") as writer:
                while block := reader.read(1024 * 1024):
                    size += len(block)
                    if size > item.size:
                        raise BackupArchiveError("business file changed during backup")
                    _require_disk_space(work, len(block))
                    digest.update(block)
                    writer.write(block)
            after = source.stat()
            if size != item.size or (before.st_ino, before.st_mtime_ns, before.st_size) != (after.st_ino, after.st_mtime_ns, after.st_size):
                raise BackupArchiveError("business file changed during backup")
            selected.append("files/" + item.path)
            assets.append({"path": item.path, "sha256": digest.hexdigest(), "size": size})
        if inventory_business_files(data_root) != inventory:
            raise BackupArchiveError("business file set changed during backup")
        verify_quiesced()
        (work / "recovery-keys.json").write_bytes(key_payload)
        manifest = {"format_version": 1, "kind": "business-backup", "schema_version": database.schema_version,
            "instance_id": instance_id, "app_version": app_version,
            "database": {"sha256": database.sha256, "size": database.size, "table_counts": dict(database.table_counts)},
            "files": assets, "excluded": list(inventory.excluded),
            "restore": {"postgres_major": 17, "database": "aigoofish", "new_instance_required": True,
                "fresh_database_passwords_required": True, "dpapi_reprotection_required": True,
                "app_role": "aigoofish_app", "probe_role": "aigoofish_probe",
                "grant_policy": f"portable-schema-v{database.schema_version}"}}
        (work / "backup.json").write_text(json.dumps(manifest, ensure_ascii=False, sort_keys=True, indent=2) + "\n", encoding="utf-8")
        encrypted_pending = destination.parent / (".business-encrypted-" + uuid.uuid4().hex + ".tmp")
        result = create_backup_archive(work, selected, encrypted_pending, passphrase,
            {"instance_id": instance_id, "app_version": app_version,
             "schema_version": database.schema_version})
        _clear_private_stage(work, destination.parent)
        work = None
        _commit_new_file(encrypted_pending, destination)
        encrypted_pending = None
        return BackupArchiveResult(destination, result.sha256, result.files, result.metadata)
    except BackupArchiveError:
        raise
    except Exception:
        logger.error("Portable business backup failed", extra={"event": "portable_business_backup_failed"})
        raise BackupArchiveError("business backup could not be completed safely") from None
    finally:
        if work is not None:
            try:
                _clear_private_stage(work, destination.parent)
            except (OSError, BackupArchiveError):
                logger.error("Private business backup staging cleanup failed at %s", work)
        if encrypted_pending is not None:
            try:
                encrypted_pending.unlink(missing_ok=True)
            except OSError:
                logger.error("Encrypted business backup staging cleanup failed at %s", encrypted_pending)


def _clear_private_stage(work: Path, parent: Path) -> None:
    """Remove only our new private tree after inspecting every descendant."""
    _assert_no_reparse_ancestry(work, parent)
    pending = [work]
    while pending:
        directory = pending.pop()
        for child in directory.iterdir():
            _assert_no_reparse_ancestry(child, work)
            if child.is_dir():
                pending.append(child)
    shutil.rmtree(work)
