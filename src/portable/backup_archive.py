"""Authenticated encrypted container for explicitly staged portable backups.

This module deliberately does not discover business files, copy PGDATA, stop
processes, or switch restored data into service.  Callers provide a stopped
writer's staging root and an exact relative-file allow-list.
"""

from __future__ import annotations

import hashlib
import json
import logging
import os
import secrets
import shutil
import stat
import struct
import tempfile
import zipfile
from dataclasses import dataclass
from pathlib import Path, PureWindowsPath
from typing import Any, Mapping, Sequence

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.scrypt import Scrypt


MAGIC = b"GFBK\x00\x01\r\n"
FORMAT_VERSION = 1
KDF_NAME = "scrypt"
SCRYPT_N = 1 << 17
SCRYPT_R = 8
SCRYPT_P = 1
SALT_BYTES = 16
NONCE_PREFIX_BYTES = 8
NONCE_BYTES = 12
CHUNK_BYTES = 1024 * 1024
MAX_HEADER_BYTES = 16 * 1024
MAX_FINAL_BYTES = 4 * 1024
MAX_ARCHIVE_BYTES = 8 * 1024 * 1024 * 1024
MAX_DECRYPTED_BYTES = 32 * 1024 * 1024 * 1024
MAX_ENTRIES = 10_000
MAX_COMPRESSION_RATIO = 100
MIN_DISK_FREE_BYTES = 10 * 1024**3
MIN_DISK_FREE_RATIO = 0.05
_METADATA_KEYS = frozenset({"instance_id", "created_at", "app_version", "schema_version"})
_RESERVED_NAMES = frozenset({
    "CON", "PRN", "AUX", "NUL",
    *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10)),
    "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
})
logger = logging.getLogger(__name__)


class BackupArchiveError(RuntimeError):
    """Backup input, password, integrity, or restoration safety failure."""


@dataclass(frozen=True)
class BackupArchiveResult:
    archive_path: Path
    sha256: str
    files: tuple[str, ...]
    metadata: Mapping[str, Any]


def _sha256_file(path: Path) -> tuple[str, int]:
    digest = hashlib.sha256()
    total = 0
    with path.open("rb") as stream:
        while block := stream.read(CHUNK_BYTES):
            digest.update(block)
            total += len(block)
    return digest.hexdigest(), total


def _is_reparse(path: Path) -> bool:
    try:
        status = path.stat(follow_symlinks=False)
    except OSError:
        # A failed lstat must not turn into a path-validation bypass.  This is
        # especially important for a junction that disappears during a check.
        raise BackupArchiveError("backup path could not be verified safely") from None
    attributes = getattr(status, "st_file_attributes", 0)
    return stat.S_ISLNK(status.st_mode) or bool(attributes & 0x400)


def _assert_no_reparse_ancestry(path: Path, stop_at: Path) -> None:
    current = path
    while True:
        if _is_reparse(current):
            raise BackupArchiveError("backup path contains a reparse point")
        if current == stop_at:
            return
        if current.parent == current:
            raise BackupArchiveError("backup path escapes its approved root")
        current = current.parent


def _safe_relative(value: str | Path) -> str:
    text = str(value).replace("\\", "/")
    path = PureWindowsPath(text)
    if not text or "\x00" in text or path.anchor or path.drive or ":" in text or any(c in text for c in '<>"|?*') or any(ord(c) < 32 for c in text):
        raise BackupArchiveError("backup file path must be a safe relative path")
    parts = tuple(part for part in path.parts if part not in ("", "."))
    if not parts or any(part == ".." for part in parts):
        raise BackupArchiveError("backup file path must be a safe relative path")
    for part in parts:
        stem = part.split(".", 1)[0].upper()
        if stem in _RESERVED_NAMES or part.endswith((".", " ")):
            raise BackupArchiveError("backup file path contains a reserved name")
    return "/".join(parts)


def _validated_metadata(metadata: Mapping[str, Any]) -> dict[str, Any]:
    if not isinstance(metadata, Mapping) or set(metadata).difference(_METADATA_KEYS):
        raise BackupArchiveError("backup metadata contains unsupported fields")
    result: dict[str, Any] = {}
    for key, value in metadata.items():
        if not isinstance(value, (str, int)) or isinstance(value, bool) or len(str(value)) > 256:
            raise BackupArchiveError("backup metadata values must be short public scalars")
        result[str(key)] = value
    return result


def _derive_key(passphrase: str, salt: bytes) -> bytes:
    if not isinstance(passphrase, str):
        raise BackupArchiveError("backup passphrase is required")
    try:
        encoded = passphrase.encode("utf-8")
    except UnicodeEncodeError:
        raise BackupArchiveError("backup passphrase is invalid") from None
    if not 12 <= len(encoded) <= 1024:
        raise BackupArchiveError("backup passphrase must be 12 to 1024 UTF-8 bytes")
    return Scrypt(salt=salt, length=32, n=SCRYPT_N, r=SCRYPT_R, p=SCRYPT_P).derive(encoded)


def _nonce(prefix: bytes, index: int) -> bytes:
    if index < 0 or index >= 2 ** 32:
        raise BackupArchiveError("backup archive has too many chunks")
    return prefix + index.to_bytes(4, "big")


def _header(metadata: Mapping[str, Any], salt: bytes, prefix: bytes) -> bytes:
    payload = {
        "format": FORMAT_VERSION,
        "kdf": {"name": KDF_NAME, "n": SCRYPT_N, "r": SCRYPT_R, "p": SCRYPT_P},
        "metadata": metadata,
        "nonce_prefix": prefix.hex(),
        "salt": salt.hex(),
    }
    return json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _parse_header(value: bytes) -> tuple[dict[str, Any], bytes, bytes]:
    if not 2 <= len(value) <= MAX_HEADER_BYTES:
        raise BackupArchiveError("backup archive header is invalid")
    try:
        payload = json.loads(value.decode("utf-8"))
        kdf = payload["kdf"]
        salt = bytes.fromhex(payload["salt"])
        prefix = bytes.fromhex(payload["nonce_prefix"])
    except (KeyError, TypeError, ValueError, UnicodeDecodeError, json.JSONDecodeError, RecursionError):
        raise BackupArchiveError("backup archive header is invalid") from None
    if (
        not isinstance(payload, dict)
        or set(payload) != {"format", "kdf", "metadata", "nonce_prefix", "salt"}
        or payload.get("format") != FORMAT_VERSION
        or not isinstance(kdf, dict)
        or kdf != {"name": KDF_NAME, "n": SCRYPT_N, "r": SCRYPT_R, "p": SCRYPT_P}
        or len(salt) != SALT_BYTES
        or len(prefix) != NONCE_PREFIX_BYTES
    ):
        raise BackupArchiveError("backup archive header is unsupported")
    metadata = _validated_metadata(payload.get("metadata", {}))
    return metadata, salt, prefix


def _write_fsync(stream) -> None:
    stream.flush()
    os.fsync(stream.fileno())


def _require_disk_space(directory: Path, growth: int) -> None:
    """Reserve requested growth without deleting data or weakening the floor."""
    try:
        usage = shutil.disk_usage(directory)
    except OSError:
        raise BackupArchiveError("backup storage capacity could not be checked") from None
    if growth < 0 or usage.free - growth < max(MIN_DISK_FREE_BYTES, usage.total * MIN_DISK_FREE_RATIO):
        raise BackupArchiveError("backup operation would cross the disk free-space safety floor")


def _set_windows_current_user_dacl(path: Path) -> None:
    """Replace the DACL with full access for only the current Windows SID."""

    # Kept local so importing this module remains harmless on non-Windows
    # development hosts.  A private work directory prevents plaintext ZIPs
    # and restored files from inheriting a broad backup-directory ACL.
    import ctypes
    from ctypes import wintypes

    TOKEN_QUERY = 0x0008
    TOKEN_USER = 1
    ACL_REVISION = 2
    GENERIC_ALL = 0x10000000
    OBJECT_INHERIT_ACE = 0x1
    CONTAINER_INHERIT_ACE = 0x2
    SE_FILE_OBJECT = 1
    DACL_SECURITY_INFORMATION = 0x00000004
    PROTECTED_DACL_SECURITY_INFORMATION = 0x80000000

    advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    advapi32.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
    advapi32.OpenProcessToken.restype = wintypes.BOOL
    advapi32.GetTokenInformation.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
    advapi32.GetTokenInformation.restype = wintypes.BOOL
    advapi32.GetLengthSid.argtypes = [ctypes.c_void_p]
    advapi32.GetLengthSid.restype = wintypes.DWORD
    advapi32.InitializeAcl.argtypes = [ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD]
    advapi32.InitializeAcl.restype = wintypes.BOOL
    advapi32.AddAccessAllowedAceEx.argtypes = [ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p]
    advapi32.AddAccessAllowedAceEx.restype = wintypes.BOOL
    advapi32.SetNamedSecurityInfoW.argtypes = [wintypes.LPWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
    advapi32.SetNamedSecurityInfoW.restype = wintypes.DWORD
    kernel32.GetCurrentProcess.argtypes = []
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL
    token = wintypes.HANDLE()
    if not advapi32.OpenProcessToken(kernel32.GetCurrentProcess(), TOKEN_QUERY, ctypes.byref(token)):
        raise OSError(ctypes.get_last_error(), "OpenProcessToken failed")
    try:
        required = wintypes.DWORD()
        advapi32.GetTokenInformation(token, TOKEN_USER, None, 0, ctypes.byref(required))
        if not required.value:
            raise OSError(ctypes.get_last_error(), "GetTokenInformation size query failed")
        token_data = ctypes.create_string_buffer(required.value)
        if not advapi32.GetTokenInformation(token, TOKEN_USER, token_data, required, ctypes.byref(required)):
            raise OSError(ctypes.get_last_error(), "GetTokenInformation failed")
        sid = ctypes.c_void_p.from_buffer(token_data).value
        if not sid:
            raise OSError("current token does not contain a user SID")
        sid_length = advapi32.GetLengthSid(ctypes.c_void_p(sid))
        if not sid_length:
            raise OSError(ctypes.get_last_error(), "GetLengthSid failed")
        # ACL header + ACE header/access mask + variable-sized SID, aligned.
        acl_length = 8 + ((8 + sid_length + 3) & ~3)
        acl = ctypes.create_string_buffer(acl_length)
        acl_pointer = ctypes.cast(acl, ctypes.c_void_p)
        if not advapi32.InitializeAcl(acl_pointer, acl_length, ACL_REVISION):
            raise OSError(ctypes.get_last_error(), "InitializeAcl failed")
        if not advapi32.AddAccessAllowedAceEx(
            acl_pointer, ACL_REVISION, OBJECT_INHERIT_ACE | CONTAINER_INHERIT_ACE, GENERIC_ALL, ctypes.c_void_p(sid)
        ):
            raise OSError(ctypes.get_last_error(), "AddAccessAllowedAceEx failed")
        result = advapi32.SetNamedSecurityInfoW(
            str(path),
            SE_FILE_OBJECT,
            DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
            None,
            None,
            acl_pointer,
            None,
        )
        if result:
            raise OSError(result, "SetNamedSecurityInfoW failed")
    finally:
        kernel32.CloseHandle(token)


def _restrict_private_path(path: Path, *, directory: bool) -> None:
    """Make a plaintext temporary artifact private to this OS user."""

    try:
        if os.name == "nt":
            _set_windows_current_user_dacl(path)
        else:
            path.chmod(0o700 if directory else 0o600)
    except OSError:
        raise BackupArchiveError("backup temporary storage could not be secured") from None


def _private_work_directory(parent: Path, prefix: str) -> Path:
    work = Path(tempfile.mkdtemp(prefix=prefix, dir=parent))
    try:
        _restrict_private_path(work, directory=True)
    except BackupArchiveError:
        try:
            work.rmdir()
        except OSError:
            logger.warning("Portable backup private work directory cleanup failed", extra={"event": "portable_backup_private_dir_cleanup_failed"})
        raise
    return work


def _commit_new_file(temporary: Path, destination: Path) -> None:
    """Atomically publish a same-directory file only if no target exists."""

    try:
        os.link(temporary, destination)
    except FileExistsError:
        raise BackupArchiveError("backup destination already exists") from None
    except OSError:
        # Portable release targets Windows/NTFS.  os.rename there has no
        # overwrite behavior; retain the hard-link path wherever available.
        if os.name != "nt":
            raise BackupArchiveError("backup destination could not be committed safely") from None
        try:
            os.rename(temporary, destination)
            return
        except FileExistsError:
            raise BackupArchiveError("backup destination already exists") from None
        except OSError:
            raise BackupArchiveError("backup destination could not be committed safely") from None
    try:
        temporary.unlink()
    except OSError:
        # The destination is already durably published. Failure to remove the
        # second hard link must not turn a successful commit into a reported
        # failure with an apparently orphaned backup.
        logger.warning("Private backup staging link cleanup failed", extra={"event": "portable_backup_link_cleanup_failed"})


def _validated_sources(source_root: Path, relative_files: Sequence[str | Path]) -> list[tuple[str, Path]]:
    if not source_root.is_absolute() or not source_root.is_dir():
        raise BackupArchiveError("backup source root must be an existing absolute staging directory")
    _assert_no_reparse_ancestry(source_root, Path(source_root.anchor))
    source_root = source_root.resolve(strict=True)
    selected: list[tuple[str, Path]] = []
    seen: set[str] = set()
    for raw_name in relative_files:
        name = _safe_relative(raw_name)
        if name.casefold() in seen or name.casefold() == "manifest.json":
            raise BackupArchiveError("backup file list contains duplicates")
        candidate = source_root.joinpath(*name.split("/"))
        try:
            resolved = candidate.resolve(strict=True)
            resolved.relative_to(source_root)
        except (OSError, RuntimeError, ValueError):
            raise BackupArchiveError("backup file is outside the staging directory") from None
        _assert_no_reparse_ancestry(candidate, source_root)
        if not resolved.is_file():
            raise BackupArchiveError("backup file list contains a missing or non-file entry")
        selected.append((name, resolved))
        seen.add(name.casefold())
    if not selected:
        raise BackupArchiveError("backup file list must not be empty")
    return selected


def _write_zip_entry(archive: zipfile.ZipFile, name: str, path: Path, remaining_bytes: int, storage_directory: Path | None) -> tuple[str, int]:
    """Hash exactly the bytes written to the ZIP, rejecting changed inputs."""

    flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(path, flags)
    try:
        before = os.fstat(descriptor)
        if not stat.S_ISREG(before.st_mode):
            raise BackupArchiveError("backup file list contains a missing or non-file entry")
        if before.st_size > remaining_bytes:
            raise BackupArchiveError("backup source data exceeds the configured limit")
        info = zipfile.ZipInfo(filename=name)
        # The encrypted outer container is intentionally incompressible.  A
        # stored inner ZIP keeps restore's anti-zip-bomb limit compatible with
        # ordinary highly repetitive database exports and assets.
        info.compress_type = zipfile.ZIP_STORED
        info.external_attr = (stat.S_IFREG | 0o600) << 16
        digest = hashlib.sha256()
        total = 0
        with os.fdopen(descriptor, "rb", closefd=False) as source, archive.open(info, "w", force_zip64=True) as destination:
            while block := source.read(CHUNK_BYTES):
                total += len(block)
                if total > remaining_bytes:
                    raise BackupArchiveError("backup source grew beyond the configured limit")
                if storage_directory is not None:
                    _require_disk_space(storage_directory, len(block))
                digest.update(block)
                destination.write(block)
        after = os.fstat(descriptor)
        identity = (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns)
        if identity != (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns) or total != before.st_size:
            raise BackupArchiveError("backup source file changed while being archived")
        return digest.hexdigest(), total
    finally:
        os.close(descriptor)


def _build_zip(source_root: Path, relative_files: Sequence[str | Path], output, storage_directory: Path | None = None) -> tuple[list[str], dict[str, Any]]:
    selected = _validated_sources(source_root, relative_files)
    if len(selected) + 1 > MAX_ENTRIES:
        raise BackupArchiveError("backup file list exceeds the configured entry limit")
    if sum(path.stat().st_size for _, path in selected) > MAX_DECRYPTED_BYTES:
        raise BackupArchiveError("backup source data exceeds the configured limit")
    manifest_files: list[dict[str, Any]] = []
    written_bytes = 0
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6, strict_timestamps=False) as archive:
        for name, path in selected:
            digest, size = _write_zip_entry(archive, name, path, MAX_DECRYPTED_BYTES - written_bytes, storage_directory)
            written_bytes += size
            manifest_files.append({"path": name, "sha256": digest, "size": size})
        manifest = {"format": 1, "files": manifest_files}
        manifest_bytes = json.dumps(manifest, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
        if len(manifest_bytes) > min(4 * 1024 * 1024, MAX_DECRYPTED_BYTES - written_bytes):
            raise BackupArchiveError("backup manifest exceeds the configured limit")
        archive.writestr("manifest.json", manifest_bytes)
    return [name for name, _ in selected], manifest


def create_backup_archive(
    source_root: str | Path,
    relative_file_list: Sequence[str | Path],
    destination_new_file: str | Path,
    passphrase: str,
    metadata: Mapping[str, Any],
) -> BackupArchiveResult:
    """Create a non-overwriting encrypted backup from explicit staged files."""

    source = Path(source_root)
    destination = Path(destination_new_file)
    if not destination.is_absolute() or destination.exists() or not destination.parent.is_dir():
        raise BackupArchiveError("backup destination must be a new file in an existing directory")
    _assert_no_reparse_ancestry(destination.parent, Path(destination.anchor))
    public_metadata = _validated_metadata(metadata)
    # Validate and estimate before any plaintext staging. ZIP_STORED bounds
    # payload growth; reserve filenames/manifest/framing plus both copies.
    planned = _validated_sources(source, relative_file_list)
    try:
        planned_bytes = sum(path.stat().st_size + len(name.encode("utf-8")) * 4 + 512 for name, path in planned)
    except OSError:
        raise BackupArchiveError("backup source size could not be checked") from None
    _require_disk_space(destination.parent, planned_bytes * 2 + 8 * CHUNK_BYTES)
    temporary_archive: Path | None = None
    temporary_output: Path | None = None
    work_directory: Path | None = None
    try:
        work_directory = _private_work_directory(destination.parent, ".backup-work-")
        descriptor, archive_name = tempfile.mkstemp(prefix="plain-", suffix=".zip", dir=work_directory)
        temporary_archive = Path(archive_name)
        try:
            _restrict_private_path(temporary_archive, directory=False)
        except BackupArchiveError:
            os.close(descriptor)
            raise
        with os.fdopen(descriptor, "w+b") as archive_stream:
            files, _manifest = _build_zip(source, relative_file_list, archive_stream, destination.parent)
            _write_fsync(archive_stream)
        if temporary_archive.stat().st_size > MAX_DECRYPTED_BYTES:
            raise BackupArchiveError("backup ZIP payload exceeds the configured limit")

        salt = secrets.token_bytes(SALT_BYTES)
        prefix = secrets.token_bytes(NONCE_PREFIX_BYTES)
        header = _header(public_metadata, salt, prefix)
        key = _derive_key(passphrase, salt)
        aead = AESGCM(key)
        header_hash = hashlib.sha256(header).digest()
        descriptor, output_name = tempfile.mkstemp(prefix="encrypted-", suffix=".tmp", dir=work_directory)
        temporary_output = Path(output_name)
        try:
            _restrict_private_path(temporary_output, directory=False)
        except BackupArchiveError:
            os.close(descriptor)
            raise
        plaintext_digest = hashlib.sha256()
        index = 0
        total = 0
        with os.fdopen(descriptor, "wb") as output, temporary_archive.open("rb") as archive_stream:
            encrypted_size = len(MAGIC) + 4 + len(header)
            if encrypted_size > MAX_ARCHIVE_BYTES:
                raise BackupArchiveError("backup archive exceeds the configured limit")
            output.write(MAGIC)
            output.write(struct.pack(">I", len(header)))
            output.write(header)
            while block := archive_stream.read(CHUNK_BYTES):
                plaintext_digest.update(block)
                total += len(block)
                cipher_length = len(block) + 16
                encrypted_size += 5 + cipher_length
                if encrypted_size > MAX_ARCHIVE_BYTES:
                    raise BackupArchiveError("backup archive exceeds the configured limit")
                _require_disk_space(destination.parent, cipher_length + 5)
                aad = header_hash + b"D" + struct.pack(">II", index, cipher_length)
                ciphertext = aead.encrypt(_nonce(prefix, index), block, aad)
                output.write(b"D" + struct.pack(">I", len(ciphertext)) + ciphertext)
                index += 1
            final = json.dumps(
                {"count": index, "sha256": plaintext_digest.hexdigest(), "total": total},
                sort_keys=True, separators=(",", ":"),
            ).encode("utf-8")
            cipher_length = len(final) + 16
            encrypted_size += 5 + cipher_length
            if encrypted_size > MAX_ARCHIVE_BYTES:
                raise BackupArchiveError("backup archive exceeds the configured limit")
            aad = header_hash + b"F" + struct.pack(">II", index, cipher_length)
            ciphertext = aead.encrypt(_nonce(prefix, index), final, aad)
            output.write(b"F" + struct.pack(">I", len(ciphertext)) + ciphertext)
            _write_fsync(output)
        temporary_archive.unlink()
        temporary_archive = None
        digest, _size = _sha256_file(temporary_output)
        _commit_new_file(temporary_output, destination)
        temporary_output = None
        return BackupArchiveResult(destination, digest, tuple(files), public_metadata)
    except BackupArchiveError:
        raise
    except Exception:
        logger.error("Portable backup archive creation failed", extra={"event": "portable_backup_create_failed"})
        raise BackupArchiveError("backup archive creation failed") from None
    finally:
        for temporary in (temporary_archive, temporary_output):
            if temporary is not None:
                try:
                    temporary.unlink(missing_ok=True)
                except OSError:
                    logger.warning("Portable backup temporary cleanup failed", extra={"event": "portable_backup_temp_cleanup_failed"})
        if work_directory is not None:
            try:
                work_directory.rmdir()
            except OSError:
                logger.warning("Portable backup work directory cleanup failed", extra={"event": "portable_backup_work_cleanup_failed"})


def _read_exact(stream, length: int) -> bytes:
    value = stream.read(length)
    if len(value) != length:
        raise BackupArchiveError("backup archive is truncated")
    return value


def _decrypt_to_zip(archive_path: Path, passphrase: str, output, max_decrypted_bytes: int, storage_directory: Path | None = None) -> dict[str, Any]:
    if not archive_path.is_absolute():
        raise BackupArchiveError("backup archive path must be absolute")
    _assert_no_reparse_ancestry(archive_path, Path(archive_path.anchor))
    try:
        archive_size = archive_path.stat().st_size
    except OSError:
        raise BackupArchiveError("backup archive is missing or exceeds the configured limit") from None
    if not archive_path.is_file() or archive_size > MAX_ARCHIVE_BYTES:
        raise BackupArchiveError("backup archive is missing or exceeds the configured limit")
    with archive_path.open("rb") as source:
        if _read_exact(source, len(MAGIC)) != MAGIC:
            raise BackupArchiveError("backup archive magic is invalid")
        header_length = struct.unpack(">I", _read_exact(source, 4))[0]
        if not 2 <= header_length <= MAX_HEADER_BYTES:
            raise BackupArchiveError("backup archive header length is invalid")
        header = _read_exact(source, header_length)
        metadata, salt, prefix = _parse_header(header)
        aead = AESGCM(_derive_key(passphrase, salt))
        header_hash = hashlib.sha256(header).digest()
        index = 0
        total = 0
        digest = hashlib.sha256()
        final_seen = False
        while True:
            record_type = source.read(1)
            if not record_type:
                break
            length = struct.unpack(">I", _read_exact(source, 4))[0]
            if record_type not in (b"D", b"F") or length < 16:
                raise BackupArchiveError("backup archive record framing is invalid")
            if (record_type == b"D" and length > CHUNK_BYTES + 32) or (record_type == b"F" and length > MAX_FINAL_BYTES + 32):
                raise BackupArchiveError("backup archive record exceeds the configured limit")
            ciphertext = _read_exact(source, length)
            try:
                aad = header_hash + record_type + struct.pack(">II", index, length)
                plaintext = aead.decrypt(_nonce(prefix, index), ciphertext, aad)
            except InvalidTag:
                raise BackupArchiveError("backup archive password or integrity verification failed") from None
            if record_type == b"D":
                if final_seen:
                    raise BackupArchiveError("backup archive record order is invalid")
                total += len(plaintext)
                if total > max_decrypted_bytes:
                    raise BackupArchiveError("backup archive decrypted data exceeds the configured limit")
                if storage_directory is not None:
                    _require_disk_space(storage_directory, len(plaintext))
                digest.update(plaintext)
                output.write(plaintext)
            elif record_type == b"F":
                if final_seen or source.read(1):
                    raise BackupArchiveError("backup archive has trailing or duplicate final data")
                try:
                    final = json.loads(plaintext.decode("utf-8"))
                except (UnicodeDecodeError, json.JSONDecodeError):
                    raise BackupArchiveError("backup archive final record is invalid") from None
                if (
                    not isinstance(final, dict)
                    or set(final) != {"count", "sha256", "total"}
                    or type(final["count"]) is not int
                    or type(final["total"]) is not int
                    or not isinstance(final["sha256"], str)
                    or final != {"count": index, "sha256": digest.hexdigest(), "total": total}
                ):
                    raise BackupArchiveError("backup archive final record does not match its data")
                final_seen = True
                break
            else:
                raise BackupArchiveError("backup archive record type is invalid")
            index += 1
        if not final_seen:
            raise BackupArchiveError("backup archive is missing its final authentication record")
        _write_fsync(output)
        return metadata


def _safe_zip_name(name: str) -> str:
    if name == "manifest.json":
        return name
    return _safe_relative(name)


def _validated_manifest_entries(manifest: Any, actual_names: set[str]) -> dict[str, dict[str, Any]]:
    if not isinstance(manifest, dict) or set(manifest) != {"format", "files"} or manifest.get("format") != 1:
        raise BackupArchiveError("backup archive manifest is invalid")
    expected = manifest["files"]
    if not isinstance(expected, list):
        raise BackupArchiveError("backup archive manifest is invalid")
    validated: dict[str, dict[str, Any]] = {}
    for item in expected:
        if not isinstance(item, dict) or set(item) != {"path", "sha256", "size"}:
            raise BackupArchiveError("backup archive manifest is invalid")
        name, digest, size = item["path"], item["sha256"], item["size"]
        if (
            not isinstance(name, str)
            or _safe_relative(name) != name
            or not isinstance(digest, str)
            or len(digest) != 64
            or any(char not in "0123456789abcdef" for char in digest)
            or type(size) is not int
            or size < 0
            or name in validated
        ):
            raise BackupArchiveError("backup archive manifest is invalid")
        validated[name] = item
    if set(validated) != actual_names:
        raise BackupArchiveError("backup archive manifest file set does not match")
    return validated


def _verify_and_extract(zip_path: Path, staging: Path, *, max_entries: int, max_ratio: int, max_expanded_bytes: int = MAX_DECRYPTED_BYTES) -> tuple[str, ...]:
    try:
        with zipfile.ZipFile(zip_path) as archive:
            entries = archive.infolist()
            if len(entries) > max_entries or not entries or any(info.is_dir() or info.flag_bits & 1 for info in entries):
                raise BackupArchiveError("backup archive entry count exceeds the configured limit")
            names = [_safe_zip_name(info.filename) for info in entries]
            if len({name.casefold() for name in names}) != len(names) or names.count("manifest.json") != 1:
                raise BackupArchiveError("backup archive file set is invalid")
            manifest_info = next(info for info in entries if info.filename == "manifest.json")
            if manifest_info.file_size > min(4 * 1024 * 1024, max_expanded_bytes):
                raise BackupArchiveError("backup manifest exceeds limit")
            if sum(info.file_size for info in entries) > max_expanded_bytes:
                raise BackupArchiveError("backup expanded file size exceeds limit")
            _require_disk_space(staging, sum(info.file_size for info in entries))
            if any(((info.external_attr >> 16) & 0o170000) == 0o120000 for info in entries):
                raise BackupArchiveError("backup archive contains a symbolic link")
            try:
                manifest = json.loads(archive.read(manifest_info).decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError):
                raise BackupArchiveError("backup archive manifest is invalid") from None
            actual_names = set(names) - {"manifest.json"}
            expected_by_path = _validated_manifest_entries(manifest, actual_names)
            extracted_total = 0
            for info in entries:
                if info.filename == "manifest.json":
                    continue
                if info.is_dir() or info.file_size < 0 or info.compress_size < 0:
                    raise BackupArchiveError("backup archive contains an invalid entry")
                if info.compress_size == 0 and info.file_size > 0 or info.compress_size and info.file_size > info.compress_size * max_ratio:
                    raise BackupArchiveError("backup archive compression ratio exceeds the configured limit")
                name = _safe_relative(info.filename)
                target = staging.joinpath(*name.split("/"))
                target.parent.mkdir(parents=True, exist_ok=True)
                digest = hashlib.sha256()
                total = 0
                with archive.open(info) as source, target.open("xb") as destination:
                    while block := source.read(CHUNK_BYTES):
                        total += len(block)
                        extracted_total += len(block)
                        if total > info.file_size or extracted_total > max_expanded_bytes:
                            raise BackupArchiveError("backup archive expanded file size exceeds the configured limit")
                        _require_disk_space(staging, len(block))
                        digest.update(block)
                        destination.write(block)
                expected_item = expected_by_path[name]
                if total != expected_item.get("size") or digest.hexdigest() != expected_item.get("sha256"):
                    raise BackupArchiveError("backup archive file hash verification failed")
            return tuple(sorted(actual_names))
    except BackupArchiveError:
        raise
    except (OSError, zipfile.BadZipFile, zipfile.LargeZipFile):
        raise BackupArchiveError("backup archive ZIP payload is invalid") from None


def restore_backup_archive(
    archive_file: str | Path,
    destination_new_directory: str | Path,
    passphrase: str,
    *,
    max_decrypted_bytes: int = MAX_DECRYPTED_BYTES,
    max_entries: int = MAX_ENTRIES,
    max_compression_ratio: int = MAX_COMPRESSION_RATIO,
) -> BackupArchiveResult:
    """Verify then safely extract into a new isolated destination directory."""

    archive = Path(archive_file)
    destination = Path(destination_new_directory)
    if not destination.is_absolute() or destination.exists() or not destination.parent.is_dir():
        raise BackupArchiveError("restore destination must be a new directory below an existing parent")
    if max_decrypted_bytes <= 0 or max_entries <= 0 or max_compression_ratio < 1:
        raise BackupArchiveError("restore limits are invalid")
    _assert_no_reparse_ancestry(destination.parent, Path(destination.anchor))
    if not archive.is_absolute():
        raise BackupArchiveError("backup archive path must be absolute")
    _assert_no_reparse_ancestry(archive, Path(archive.anchor))
    try:
        archive_size = archive.stat().st_size
    except OSError:
        raise BackupArchiveError("backup archive size could not be checked") from None
    _require_disk_space(destination.parent, min(archive_size, max_decrypted_bytes) + 2 * CHUNK_BYTES)
    zip_descriptor = None
    zip_name = None
    staging: Path | None = None
    work_directory: Path | None = None
    phase = "prepare"
    try:
        phase = "private-work-directory"
        work_directory = _private_work_directory(destination.parent, ".backup-restore-work-")
        phase = "decrypted-temp-file"
        zip_descriptor, zip_name = tempfile.mkstemp(prefix="decrypt-", suffix=".zip", dir=work_directory)
        _restrict_private_path(Path(zip_name), directory=False)
        phase = "decrypt"
        with os.fdopen(zip_descriptor, "w+b") as zip_output:
            zip_descriptor = None
            metadata = _decrypt_to_zip(archive, passphrase, zip_output, max_decrypted_bytes, destination.parent)
        phase = "extraction-stage"
        staging = Path(tempfile.mkdtemp(prefix="restore-", dir=work_directory))
        _restrict_private_path(staging, directory=True)
        phase = "extract"
        files = _verify_and_extract(Path(zip_name), staging, max_entries=max_entries, max_ratio=max_compression_ratio, max_expanded_bytes=max_decrypted_bytes)
        if os.name != "nt":
            raise BackupArchiveError("atomic directory restore commit is supported only on Windows")
        phase = "digest"
        digest, _size = _sha256_file(archive)
        try:
            Path(zip_name).unlink()
        except OSError:
            logger.error("Portable backup decrypted ZIP cleanup failed", extra={"event": "portable_backup_decrypt_cleanup_failed"})
            raise BackupArchiveError("decrypted backup cleanup failed before restore publication") from None
        zip_name = None
        phase = "publish"
        os.rename(staging, destination)
        staging = None
        return BackupArchiveResult(archive, digest, files, metadata)
    except BackupArchiveError:
        raise
    except Exception as error:
        logger.error("Portable backup restoration failed (phase=%s, class=%s, winerror=%s)",
                     phase, type(error).__name__, getattr(error, "winerror", None),
                     extra={"event": "portable_backup_restore_failed"})
        raise BackupArchiveError("backup restoration failed") from None
    finally:
        if zip_descriptor is not None:
            try:
                os.close(zip_descriptor)
            except OSError:
                logger.error("Portable backup decrypted ZIP close failed", extra={"event": "portable_backup_decrypt_close_failed"})
        if zip_name:
            try:
                Path(zip_name).unlink(missing_ok=True)
            except OSError:
                logger.error("Portable backup decrypted ZIP cleanup failed", extra={"event": "portable_backup_decrypt_cleanup_failed"})
        if staging is not None:
            try:
                shutil.rmtree(staging)
            except OSError:
                logger.error("Portable backup staging cleanup failed", extra={"event": "portable_backup_restore_cleanup_failed"})
        if work_directory is not None:
            try:
                work_directory.rmdir()
            except OSError:
                logger.error("Portable backup work directory cleanup failed", extra={"event": "portable_backup_restore_work_cleanup_failed"})


__all__ = [
    "BackupArchiveError", "BackupArchiveResult", "create_backup_archive", "restore_backup_archive",
]
