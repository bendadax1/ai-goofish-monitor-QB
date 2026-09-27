"""Focused retention tests using only task-owned fixtures under .tmp."""

import os
import stat
import tempfile
import time
import zipfile
from pathlib import Path
from types import SimpleNamespace

import pytest

from src import log_exporter, logging_config, log_retention


@pytest.fixture
def isolated_log_root():
    repo_root = Path(__file__).resolve().parents[1]
    temp_root = repo_root / ".tmp" / "tests"
    temp_root.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="log-retention-", dir=temp_root) as directory:
        yield Path(directory)


def _set_age(path: Path, age_days: float) -> None:
    timestamp = time.time() - age_days * 24 * 60 * 60
    os.utime(path, (timestamp, timestamp))


def _write_zip(path: Path) -> None:
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("logs/system.log", "synthetic\n")


def test_log_cleanup_keeps_recent_and_active_logs(isolated_log_root, monkeypatch):
    old_log = isolated_log_root / "old.log.1"
    active_log = isolated_log_root / "system.log"
    old_log.write_text("synthetic old log\n", encoding="utf-8")
    active_log.write_text("synthetic active log\n", encoding="utf-8")
    _set_age(old_log, 8)
    _set_age(active_log, 8)

    monkeypatch.setattr(
        logging_config,
        "is_file_open",
        lambda path, active_paths=(), opened_file_ids=None: path == active_log,
    )

    logging_config.cleanup_old_logs(str(isolated_log_root))

    assert not old_log.exists()
    assert active_log.exists()


def test_log_cleanup_preserves_manual_retention_days_semantics(isolated_log_root):
    eight_day_log = isolated_log_root / "eight-days.log"
    eight_day_log.write_text("synthetic log\n", encoding="utf-8")
    _set_age(eight_day_log, 8)

    logging_config.cleanup_old_logs(str(isolated_log_root), retention_days=10)

    assert eight_day_log.exists()


def test_log_cleanup_does_not_delete_unknown_log_suffixes(isolated_log_root):
    unknown_file = isolated_log_root / "old.log.backup.json"
    rotated_log = isolated_log_root / "old.log.2"
    unknown_file.write_text("synthetic non-log data\n", encoding="utf-8")
    rotated_log.write_text("synthetic rotated log\n", encoding="utf-8")
    _set_age(unknown_file, 8)
    _set_age(rotated_log, 8)

    logging_config.cleanup_old_logs(str(isolated_log_root))

    assert unknown_file.exists()
    assert not rotated_log.exists()


def test_log_cleanup_does_not_follow_reparse_directories(isolated_log_root):
    log_root = isolated_log_root / "logs"
    log_root.mkdir()
    outside = isolated_log_root / "outside"
    outside.mkdir()
    external_log = outside / "old.log"
    external_log.write_text("synthetic outside log\n", encoding="utf-8")
    _set_age(external_log, 8)
    link = log_root / "linked"
    try:
        link.symlink_to(outside, target_is_directory=True)
    except (OSError, NotImplementedError):
        external_log.unlink(missing_ok=True)
        outside.rmdir()
        pytest.skip("directory symlinks are not available for this test account")

    try:
        logging_config.cleanup_old_logs(str(log_root))
        assert external_log.exists()
    finally:
        link.unlink(missing_ok=True)
        external_log.unlink(missing_ok=True)
        outside.rmdir()


def test_log_cleanup_skips_reparse_candidates_and_permission_failures(
    isolated_log_root, monkeypatch
):
    reparse_directory = isolated_log_root / "junction"
    reparse_directory.mkdir()
    protected_log = reparse_directory / "old.log"
    denied_log = isolated_log_root / "denied.log"
    protected_log.write_text("synthetic protected log\n", encoding="utf-8")
    denied_log.write_text("synthetic denied log\n", encoding="utf-8")
    _set_age(protected_log, 8)
    _set_age(denied_log, 8)

    original_reparse_check = logging_config.is_reparse_point
    monkeypatch.setattr(
        logging_config,
        "is_reparse_point",
        lambda path: path == reparse_directory or original_reparse_check(path),
    )
    original_unlink = Path.unlink

    def deny_one_delete(path, *args, **kwargs):
        if path == denied_log:
            raise PermissionError("synthetic sharing violation")
        return original_unlink(path, *args, **kwargs)

    monkeypatch.setattr(Path, "unlink", deny_one_delete)

    logging_config.cleanup_old_logs(str(isolated_log_root))

    assert protected_log.exists()
    assert denied_log.exists()


def test_reparse_detector_recognizes_windows_reparse_attribute(monkeypatch, isolated_log_root):
    candidate = isolated_log_root / "synthetic-junction"
    monkeypatch.setattr(
        Path,
        "lstat",
        lambda _path: SimpleNamespace(st_mode=stat.S_IFREG, st_file_attributes=0x400),
    )

    assert logging_config.is_reparse_point(candidate)


def test_cleanup_preserves_candidates_when_a_root_has_reparse_ancestor(
    isolated_log_root, monkeypatch
):
    log_root = isolated_log_root / "logs"
    log_root.mkdir()
    old_log = log_root / "old.log"
    old_log.write_text("synthetic log\n", encoding="utf-8")
    _set_age(old_log, 8)
    export_root = isolated_log_root / "exports"
    export_root.mkdir()
    old_export = export_root / "logs_export_old.zip"
    _write_zip(old_export)
    _set_age(old_export, 8)
    monkeypatch.setattr(logging_config, "has_reparse_ancestor", lambda _path: True)
    monkeypatch.setattr(log_exporter, "has_reparse_ancestor", lambda _path: True)

    logging_config.cleanup_old_logs(str(log_root))
    deleted_exports = log_exporter.cleanup_old_exports(str(export_root))

    assert old_log.exists()
    assert old_export.exists()
    assert deleted_exports == 0


def test_unknown_windows_open_check_failure_preserves_candidate(
    isolated_log_root, monkeypatch
):
    candidate = isolated_log_root / "possibly-open.zip"
    candidate.write_bytes(b"synthetic")

    def fail_probe(_path):
        raise OSError("synthetic probe failure")

    monkeypatch.setattr(log_retention, "is_windows_file_open", fail_probe)

    assert log_retention.is_file_open(candidate, opened_file_ids=set())


def test_linux_missing_procfs_fails_closed_for_open_file_check(
    isolated_log_root, monkeypatch
):
    candidate = isolated_log_root / "possibly-open.log"
    candidate.write_text("synthetic log\n", encoding="utf-8")
    monkeypatch.setattr(log_retention, "_requires_procfs", lambda: True)
    original_is_dir = Path.is_dir
    monkeypatch.setattr(
        Path,
        "is_dir",
        lambda path: False if path == Path("/proc") else original_is_dir(path),
    )

    open_file_ids = log_retention._process_open_file_ids()

    assert open_file_ids is None
    assert log_retention.is_file_open(candidate, opened_file_ids=open_file_ids)


def test_linux_procfs_permission_error_fails_closed(isolated_log_root, monkeypatch):
    candidate = isolated_log_root / "possibly-open.log"
    candidate.write_text("synthetic log\n", encoding="utf-8")
    monkeypatch.setattr(log_retention, "_requires_procfs", lambda: True)
    original_is_dir = Path.is_dir

    def deny_procfs(path):
        if path == Path("/proc"):
            raise PermissionError("synthetic procfs denial")
        return original_is_dir(path)

    monkeypatch.setattr(Path, "is_dir", deny_procfs)

    open_file_ids = log_retention._process_open_file_ids()

    assert open_file_ids is None
    assert log_retention.is_file_open(candidate, opened_file_ids=open_file_ids)


def test_non_linux_missing_procfs_keeps_empty_snapshot_semantics(monkeypatch):
    monkeypatch.setattr(log_retention, "_requires_procfs", lambda: False)
    original_is_dir = Path.is_dir
    monkeypatch.setattr(
        Path,
        "is_dir",
        lambda path: False if path == Path("/proc") else original_is_dir(path),
    )

    assert log_retention._process_open_file_ids() == set()


@pytest.mark.skipif(os.name != "nt", reason="Windows handle-sharing probe")
def test_windows_open_handle_is_detected(isolated_log_root):
    candidate = isolated_log_root / "open.zip"
    candidate.write_bytes(b"synthetic")

    with candidate.open("rb"):
        assert log_retention.is_windows_file_open(candidate)

    assert not log_retention.is_windows_file_open(candidate)


def test_export_cleanup_applies_age_and_five_file_cap(isolated_log_root, monkeypatch):
    export_dir = isolated_log_root / "exports"
    export_dir.mkdir()
    exports = []
    for index in range(7):
        path = export_dir / f"logs_export_{index}.zip"
        _write_zip(path)
        _set_age(path, 8 if index == 0 else index / 10)
        exports.append(path)

    deleted = log_exporter.cleanup_old_exports(str(export_dir), keep_count=20)

    assert deleted == 2
    assert exports[0].exists() is False
    assert exports[6].exists() is False
    assert all(path.exists() for path in exports[1:6])


def test_export_cleanup_preserves_open_candidate(isolated_log_root, monkeypatch):
    export_dir = isolated_log_root / "exports"
    export_dir.mkdir()
    candidates = []
    for index in range(7):
        path = export_dir / f"logs_export_{index}.zip"
        _write_zip(path)
        _set_age(path, index / 10)
        candidates.append(path)

    protected = candidates[5]
    monkeypatch.setattr(
        log_exporter,
        "is_file_open",
        lambda path, active_paths=(), opened_file_ids=None: path == protected,
    )

    deleted = log_exporter.cleanup_old_exports(str(export_dir))

    assert deleted == 1
    assert protected.exists()
    assert candidates[6].exists() is False
    assert all(path.exists() for path in candidates[:6])


def test_export_cleanup_does_not_remove_symlinked_archives(isolated_log_root):
    export_dir = isolated_log_root / "exports"
    export_dir.mkdir()
    outside = isolated_log_root / "outside.zip"
    _write_zip(outside)
    _set_age(outside, 9)
    link = export_dir / "logs_export_link.zip"
    try:
        link.symlink_to(outside)
    except (OSError, NotImplementedError):
        outside.unlink(missing_ok=True)
        pytest.skip("file symlinks are not available for this test account")

    try:
        log_exporter.cleanup_old_exports(str(export_dir))
        assert outside.exists()
        assert link.is_symlink()
    finally:
        link.unlink(missing_ok=True)


def test_export_cleanup_skips_reparse_and_permission_denied_files(
    isolated_log_root, monkeypatch
):
    export_dir = isolated_log_root / "exports"
    export_dir.mkdir()
    reparse_archive = export_dir / "logs_export_link.zip"
    denied_archive = export_dir / "logs_export_denied.zip"
    _write_zip(reparse_archive)
    _write_zip(denied_archive)
    _set_age(reparse_archive, 9)
    _set_age(denied_archive, 9)

    original_reparse_check = log_exporter.is_reparse_point
    monkeypatch.setattr(
        log_exporter,
        "is_reparse_point",
        lambda path: path == reparse_archive or original_reparse_check(path),
    )
    original_unlink = Path.unlink

    def deny_one_delete(path, *args, **kwargs):
        if path == denied_archive:
            raise PermissionError("synthetic sharing violation")
        return original_unlink(path, *args, **kwargs)

    monkeypatch.setattr(Path, "unlink", deny_one_delete)

    deleted = log_exporter.cleanup_old_exports(str(export_dir))

    assert deleted == 0
    assert reparse_archive.exists()
    assert denied_archive.exists()
