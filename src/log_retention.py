"""Shared fail-closed checks for removable log artifacts."""

import os
import logging
import stat
import sys
from pathlib import Path
from typing import Iterable, Optional, Set, Tuple

_OPEN_FILE_IDS_UNSET = object()
_logger = logging.getLogger(__name__)


def _requires_procfs() -> bool:
    return sys.platform.startswith("linux")


def is_reparse_point(path: Path) -> bool:
    """Return true for links/reparse points or when the path cannot be checked."""
    try:
        info = path.lstat()
    except OSError as error:
        _logger.warning(
            "Cannot inspect log retention path; preserving it",
            extra={"event": "log_retention_path_inspect_error"},
            exc_info=(type(error), error, error.__traceback__),
        )
        return True

    if stat.S_ISLNK(info.st_mode):
        return True

    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    attributes = getattr(info, "st_file_attributes", 0)
    return bool(attributes & reparse_flag)


def has_reparse_ancestor(path: Path) -> bool:
    """Reject roots reached through a symlink/junction in any path component."""
    absolute_path = Path(os.path.abspath(os.fspath(path)))
    return any(is_reparse_point(component) for component in (absolute_path, *absolute_path.parents))


def _process_open_file_ids() -> Optional[Set[Tuple[int, int]]]:
    """Enumerate open file identities through procfs when available.

    None means procfs could not be inspected completely; callers must then
    preserve the candidate. Linux requires procfs; non-Linux platforms without
    procfs return an empty set and rely on registered handlers plus the
    operating system's unlink semantics.
    """
    proc = Path("/proc")
    try:
        proc_available = proc.is_dir()
    except OSError as error:
        if _requires_procfs():
            _logger.warning(
                "Cannot inspect Linux procfs; preserving log artifacts",
                extra={"event": "log_retention_procfs_unavailable"},
                exc_info=(type(error), error, error.__traceback__),
            )
            return None
        return set()

    if not proc_available:
        if _requires_procfs():
            _logger.warning(
                "Linux procfs is unavailable; preserving log artifacts",
                extra={"event": "log_retention_procfs_unavailable"},
            )
            return None
        return set()

    identities: Set[Tuple[int, int]] = set()
    try:
        processes = list(os.scandir(proc))
    except OSError as error:
        _logger.warning(
            "Cannot enumerate process file handles; preserving log artifacts",
            extra={"event": "log_retention_handle_scan_error"},
            exc_info=(type(error), error, error.__traceback__),
        )
        return None

    for process in processes:
        if not process.name.isdecimal():
            continue
        descriptor_dir = proc / process.name / "fd"
        try:
            descriptors = list(os.scandir(descriptor_dir))
        except FileNotFoundError:
            continue
        except OSError:
            # A process may exit between enumeration and inspection.
            if not (proc / process.name).exists():
                continue
            _logger.warning(
                "Cannot inspect a process file handle; preserving log artifacts",
                extra={"event": "log_retention_handle_scan_error"},
            )
            return None

        for descriptor in descriptors:
            try:
                info = os.stat(descriptor.path)
            except FileNotFoundError:
                continue
            except OSError as error:
                _logger.warning(
                    "Cannot inspect an open file handle; preserving log artifacts",
                    extra={"event": "log_retention_handle_scan_error"},
                    exc_info=(type(error), error, error.__traceback__),
                )
                return None
            identities.add((info.st_dev, info.st_ino))
    return identities


def is_windows_file_open(path: Path) -> bool:
    """Probe for Windows handles that would make deletion unsafe."""
    if os.name != "nt":
        return False

    import ctypes

    restart_manager = ctypes.WinDLL("Rstrtmgr", use_last_error=True)
    restart_manager.RmStartSession.argtypes = [
        ctypes.POINTER(ctypes.c_uint32), ctypes.c_uint32, ctypes.c_wchar_p
    ]
    restart_manager.RmStartSession.restype = ctypes.c_uint32
    restart_manager.RmRegisterResources.argtypes = [
        ctypes.c_uint32,
        ctypes.c_uint32,
        ctypes.POINTER(ctypes.c_wchar_p),
        ctypes.c_uint32,
        ctypes.c_void_p,
        ctypes.c_uint32,
        ctypes.POINTER(ctypes.c_wchar_p),
    ]
    restart_manager.RmRegisterResources.restype = ctypes.c_uint32
    restart_manager.RmGetList.argtypes = [
        ctypes.c_uint32,
        ctypes.POINTER(ctypes.c_uint32),
        ctypes.POINTER(ctypes.c_uint32),
        ctypes.c_void_p,
        ctypes.POINTER(ctypes.c_uint32),
    ]
    restart_manager.RmGetList.restype = ctypes.c_uint32
    restart_manager.RmEndSession.argtypes = [ctypes.c_uint32]
    restart_manager.RmEndSession.restype = ctypes.c_uint32
    session = ctypes.c_uint32()
    session_key = ctypes.create_unicode_buffer(33)
    result = restart_manager.RmStartSession(
        ctypes.byref(session), 0, session_key
    )
    if result != 0:
        _logger.warning(
            "Restart Manager session failed; preserving log artifact",
            extra={"event": "log_retention_windows_probe_error", "win32_error": int(result)},
        )
        return True

    try:
        resources = (ctypes.c_wchar_p * 1)(os.fspath(path))
        result = restart_manager.RmRegisterResources(
            session, 1, resources, 0, None, 0, None
        )
        if result != 0:
            _logger.warning(
                "Restart Manager resource registration failed; preserving log artifact",
                extra={"event": "log_retention_windows_probe_error", "win32_error": int(result)},
            )
            return True

        needed = ctypes.c_uint32()
        supplied = ctypes.c_uint32()
        reboot_reasons = ctypes.c_uint32()
        result = restart_manager.RmGetList(
            session,
            ctypes.byref(needed),
            ctypes.byref(supplied),
            None,
            ctypes.byref(reboot_reasons),
        )
        # ERROR_MORE_DATA means Restart Manager found one or more applications
        # using the file. Any other unexpected status fails closed as well.
        if result not in (0, 234):
            _logger.warning(
                "Restart Manager handle check failed; preserving log artifact",
                extra={"event": "log_retention_windows_probe_error", "win32_error": int(result)},
            )
            return True
        return result == 234 or needed.value > 0
    finally:
        result = restart_manager.RmEndSession(session)
        if result != 0:
            _logger.warning(
                "Restart Manager session cleanup failed",
                extra={"event": "log_retention_windows_probe_error", "win32_error": int(result)},
            )


def is_file_open(
    path: Path,
    active_paths: Iterable[str] = (),
    opened_file_ids=_OPEN_FILE_IDS_UNSET,
) -> bool:
    """Check known active handlers and process handles; uncertainty preserves."""
    normalized = os.path.normcase(os.path.abspath(os.fspath(path)))
    if normalized in {
        os.path.normcase(os.path.abspath(os.fspath(active)))
        for active in active_paths
    }:
        return True

    try:
        info = path.stat()
    except OSError as error:
        _logger.warning(
            "Cannot inspect log artifact before cleanup; preserving it",
            extra={"event": "log_retention_path_inspect_error"},
            exc_info=(type(error), error, error.__traceback__),
        )
        return True

    try:
        if is_windows_file_open(path):
            return True
    except Exception as error:
        _logger.warning(
            "Cannot verify Windows file use; preserving log artifact",
            extra={"event": "log_retention_windows_probe_error"},
            exc_info=(type(error), error, error.__traceback__),
        )
        return True
    if opened_file_ids is _OPEN_FILE_IDS_UNSET:
        opened_file_ids = _process_open_file_ids()
    if opened_file_ids is None:
        return True
    return (info.st_dev, info.st_ino) in opened_file_ids
