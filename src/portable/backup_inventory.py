"""Read-only file inventory for a future quiesced portable backup.

An inventory is not a backup or proof that writers are stopped. It deliberately
never walks PGDATA, caches, logs or launcher control credentials. Callers must
hold the instance lease and stop business writers before capturing its files.
"""

from __future__ import annotations

import logging
import json
import os
import re
import stat
from uuid import UUID
from dataclasses import dataclass
from pathlib import Path

from src.portable.backup_archive import BackupArchiveError, _safe_relative

logger = logging.getLogger(__name__)
BUSINESS_DIRECTORIES = frozenset({"state", "assets", "results"})
EXCLUDED_DIRECTORIES = {
    "postgres": "数据库必须使用匹配版本的逻辑备份，禁止普通复制 PGDATA",
    "cache": "可重建缓存，不属于业务备份",
    "logs": "诊断日志单独导出，不作为业务备份",
    "launcher": "本机运行与偏好状态，不恢复进程身份",
}
EXCLUDED_CONFIG = frozenset({
    "instance-secrets.dpapi", "python-control.dpapi", "python-runtime.json", "postgres-runtime.json",
    "postgres-provision.json", "postgres-provision-progress.json",
})


@dataclass(frozen=True)
class BackupFile:
    path: str
    size: int
    modified_ns: int


@dataclass(frozen=True)
class BackupInventory:
    files: tuple[BackupFile, ...]
    excluded: tuple[tuple[str, str], ...]
    unknown: tuple[str, ...]
    required_separate_payloads: tuple[str, ...] = (
        "postgres-logical-dump", "database-role-recreation", "protected-business-key-export",
    )

    @property
    def total_bytes(self) -> int:
        return sum(item.size for item in self.files)

    @property
    def file_scope_ready(self) -> bool:
        """Known file scope only; never implies a complete/restorable backup."""
        return not self.unknown


def _inspect(path: Path):
    status = path.lstat()
    if stat.S_ISLNK(status.st_mode) or getattr(status, "st_file_attributes", 0) & 0x400:
        raise BackupArchiveError("backup inventory contains a reparse point")
    if not stat.S_ISDIR(status.st_mode) and not stat.S_ISREG(status.st_mode):
        raise BackupArchiveError("backup inventory contains a special file")
    return status


def _is_restore_catalog(path: Path, max_entries: int) -> bool:
    """Recognize catalog-owned siblings, never descend into their business data.

    A directory named instances alone is not sufficient to omit unknown data.
    Each candidate must have the bounded identity/path metadata written by the
    Launcher. Unfinished or foreign content still blocks complete backup.
    """
    try:
        with os.scandir(path) as entries:
            for index, entry in enumerate(entries):
                if index >= max_entries or not re.fullmatch(r"restore-[0-9a-f]{32}", entry.name):
                    return False
                candidate = Path(entry.path)
                if not stat.S_ISDIR(_inspect(candidate).st_mode):
                    return False
                launcher = candidate / "launcher"
                if not stat.S_ISDIR(_inspect(launcher).st_mode):
                    return False
                marker = launcher / "restore-target.json"
                info = _inspect(marker)
                if not stat.S_ISREG(info.st_mode) or info.st_size > 16 * 1024:
                    return False
                with marker.open("rb") as reader:
                    raw = reader.read(16 * 1024 + 1)
                if len(raw) > 16 * 1024:
                    return False
                metadata = json.loads(raw)
                if not isinstance(metadata, dict):
                    return False
                identity = UUID(metadata.get("instance_id", ""))
                if (identity.int == 0 or metadata.get("format_version") != 1
                    or metadata.get("relative_root", "").replace("\\", "/") != f"instances/{entry.name}"
                    or metadata.get("state") not in {"Preparing", "Validated"}):
                    return False
                ports = [metadata.get("postgres_port"), metadata.get("web_port")]
                if any(type(port) is not int or not 1 <= port <= 65535 for port in ports) or ports[0] == ports[1]:
                    return False
        return True
    except (OSError, ValueError, TypeError, AttributeError, BackupArchiveError):
        logger.warning("恢复候选目录身份无法核验，保留为未知备份路径",
                       extra={"event": "backup_restore_catalog_unverified"})
        return False


def inventory_business_files(data_root: Path, *, max_entries: int = 100_000) -> BackupInventory:
    """Enumerate assets without reading business contents or changing data.

    Unknown paths block file-scope readiness instead of being silently omitted.
    Only bounded Launcher restore metadata is read to identify other instances.
    Login state is sensitive: consumers must encrypt the resulting archive.
    """
    if not data_root.is_absolute() or type(max_entries) is not int or max_entries < 1:
        raise BackupArchiveError("inventory requires an absolute root and positive limit")
    files: list[BackupFile] = []
    excluded: list[tuple[str, str]] = []
    unknown: list[str] = []
    seen: set[str] = set()
    observed = 0
    try:
        for ancestor in (data_root, *data_root.parents):
            if not stat.S_ISDIR(_inspect(ancestor).st_mode):
                raise BackupArchiveError("inventory root is not a plain directory")
        pending = [(data_root, "root")]
        while pending:
            directory, scope = pending.pop()
            with os.scandir(directory) as entries:
                for entry in entries:
                    observed += 1
                    if observed > max_entries:
                        raise BackupArchiveError("backup inventory entry limit exceeded")
                    candidate = Path(entry.path)
                    relative = candidate.relative_to(data_root).as_posix()
                    if _safe_relative(relative) != relative or relative.casefold() in seen:
                        raise BackupArchiveError("backup inventory path alias or collision")
                    seen.add(relative.casefold())
                    status = _inspect(candidate)
                    is_directory = stat.S_ISDIR(status.st_mode)
                    if scope == "root":
                        if is_directory and entry.name in BUSINESS_DIRECTORIES:
                            pending.append((candidate, "business"))
                        elif is_directory and entry.name == "config":
                            pending.append((candidate, "config"))
                        elif is_directory and entry.name in EXCLUDED_DIRECTORIES:
                            excluded.append((relative, EXCLUDED_DIRECTORIES[entry.name]))
                        elif is_directory and entry.name == "instances" and _is_restore_catalog(candidate, max_entries):
                            excluded.append((relative, "已核验的隔离恢复候选，不属于当前活动实例业务数据"))
                        elif not is_directory and entry.name == ".launcher.instance.lock":
                            excluded.append((relative, "本机排他锁，不复制运行身份"))
                        else:
                            unknown.append(relative)
                    elif scope == "config":
                        if not is_directory and entry.name == "app.env":
                            files.append(BackupFile(relative, status.st_size, status.st_mtime_ns))
                        elif not is_directory and entry.name in EXCLUDED_CONFIG:
                            excluded.append((relative, "本机控制状态；业务解密密钥需另行受保护导出"))
                        else:
                            unknown.append(relative)
                    elif is_directory:
                        pending.append((candidate, "business"))
                    else:
                        files.append(BackupFile(relative, status.st_size, status.st_mtime_ns))
        return BackupInventory(tuple(sorted(files, key=lambda item: item.path)),
            tuple(sorted(excluded)), tuple(sorted(unknown)))
    except BackupArchiveError:
        raise
    except OSError:
        logger.error("Portable backup inventory failed", extra={"event": "portable_backup_inventory_failed"})
        raise BackupArchiveError("backup file inventory could not be read completely") from None
