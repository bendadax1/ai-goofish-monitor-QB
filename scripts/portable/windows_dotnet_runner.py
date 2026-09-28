"""Run one bounded Windows dotnet DLL invocation without Windows error dialogs."""

from __future__ import annotations

import argparse
import ctypes
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import threading
import time
from ctypes import wintypes


SEM_NOGPFAULTERRORBOX = 0x0002
MAX_TIMEOUT_SECONDS = 60 * 60
MAX_CAPTURE_BYTES_PER_STREAM = 16 * 1024 * 1024
MIN_FREE_DISK_BYTES = 10 * 1024 * 1024 * 1024
JOB_CLEANUP_TIMEOUT_SECONDS = 15
CREATE_SUSPENDED = 0x00000004
TH32CS_SNAPTHREAD = 0x00000004
THREAD_SUSPEND_RESUME = 0x0002
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value
JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000
JobObjectExtendedLimitInformation = 9
TEST_ID_PATTERN = re.compile(r"^[a-z0-9][a-z0-9-]{0,63}$")
MARKER_PATTERN = re.compile(r"^[A-Z0-9_:=.-]{1,128}$")
CAS_FAILURE_DIAGNOSTIC_PATTERN = re.compile(
    rb"(?:^HOST_RECOVERY_SAFE_START code=(?:START_FAILED|START_NOT_READY|START_CANCELLED|OTHER) states=(?:Stopped|Running|Unknown|Failed|Starting|Stopping)-(?:Stopped|Running|Unknown|Failed|Starting|Stopping)-(?:Stopped|Running|Unknown|Failed|Starting|Stopping)$"
    rb"|^HOST_RECOVERY_SAFE_COMPONENT value=(?:PythonWeb-StartupComponentFailed|Postgres-StartupComponentFailed|DatabaseProvision-StartupComponentFailed|DatabaseProvision-DatabaseProvisionInterrupted)$"
    rb"|^HOST_RECOVERY_SAFE_FAILURE stage=(?:seed|preflight|old-launcher-process|new-window-recovery|normal-stop|same-window-restart|all-stopped-reopen|incomplete-provision|partial-pg-only|unknown-record|other) category=(?:Timeout|Io|Security|InvalidOperation|Argument|Other)$"
    rb"|^HOST_RECOVERY_STAGE=(?:seed-process-started|seed-process-exited|new-window-setup|new-window-vm|new-window-init|new-window-initialized)$"
    rb"|^CONFIG_PG_CAS_FAILURE stage=(?:preflight|bundle-verify|fixture-create|host-create|host-start|web-port-seed|web-port-stage|web-port-apply|probe|host-stop) "
    rb"category=(?:Timeout|InvalidData|Security|Platform|Configuration|Io|Other)$"
    rb"|^CONFIG_PG_CAS_PROBE_FAILURE step=(?:arguments|request-validate|import-fastapi|import-sqlalchemy|import-storage-adapter|import-settings-manager|engine-create|storage-create|db-identity|db-identity-address|db-identity-port|seed-users|config-a-create|config-b-create|delete-recreate|concurrent-cas|settings-update|host-change|user-isolation|default-switch|cleanup-dispose|cleanup-delete-users|cleanup-engine-dispose|cleanup-environment) "
    rb"exception=(?:AssertionError|AttributeError|DBAPIError|FileNotFoundError|HTTPException|ImportError|IntegrityError|InterfaceError|JSONDecodeError|ModuleNotFoundError|OperationalError|OSError|PermissionError|ProgrammingError|RuntimeError|SyntaxError|TimeoutError|TypeError|ValueError|Other)$"
    rb"|^CONFIG_PG_CAS_PORT_SELFTEST_FAILURE step=(?:parse-valid|parse-invalid|occupied-port|probe-environment|fixture-create|fixture-lease|transaction-read|transaction-stage|transaction-begin|transaction-commit|transaction-verify|fixture-cleanup) "
    rb"category=(?:Io|Security|Argument|InvalidOperation|Socket|Other)$"
    rb"|^WEB_PORT_HOST_FAILURE stage=(?:preflight|candidate-ready|occupied-rollback|fixture-create|host-start|web-port-apply|rollback-ready) "
    rb"category=(?:Timeout|InvalidData|Security|Platform|Configuration|Io|Other)$"
    rb"|^WEB_PORT_HOST_FIXTURE_RETAINED=YES$"
    rb"|^AUTO_PORT_UI_FAILURE stage=(?:preflight|first-auto-start|same-window-restart|fixed-conflict|existing-auto-conflict|all-stopped-reopen|all-stopped-shutdown|all-stopped-dispose|all-stopped-initialize|all-stopped-state|all-stopped-start|all-stopped-running|all-stopped-stop|all-stopped-final-state) category=(?:[A-Za-z]{1,64})$"
    rb"|^AUTO_PORT_UI_CLEANUP=(?:[A-Za-z]{1,64})$"
    rb"|^AUTO_PORT_UI_RUNNING stage=(?:first-auto-start|same-window-restart|existing-auto-conflict|all-stopped-running) running=[01] setup=[01] stop=[01] (?:copy|setupEntry)=[01]$"
    rb"|^AUTO_PORT_UI_DIAG_STATE=(?:NotStarted|Starting|Recovering|Cancelling|Running|Failed|Stopping|BackingUp|Stopped|StopFailed)$"
    rb"|^AUTO_PORT_UI_DIAG_COMPONENT id=(?:postgres|database-provision|python-web) state=(?:Stopped|Starting|Running|Failed|Stopping|Unknown)$"
    rb"|^AUTO_PORT_UI_DIAG_EVENT component=(?:launcher|postgres|database-provision|python-web|python-maintenance) code=(?:STARTUP_COMPONENT_FAILED|COMPONENT_NOT_READY|COMPONENT_STOP_FAILED|COMPONENT_STOP_UNCONFIRMED|COMPONENT_STATE_READ_FAILED|POSTGRES_INIT_INTERRUPTED|POSTGRES_MARKER_MISSING|POSTGRES_DATA_UNVERIFIED|DATABASE_PROVISION_INTERRUPTED)$"
    rb"|^AUTO_PORT_UI_DIAG_UNAVAILABLE$"
    rb"|^AUTO_PORT_UI_FIXTURE_RETAINED=YES$"
    rb"|^BACKUP_RESTORE_HOST_E2E_FAILED stage=(?:preflight(?:-retention|-location|-fixture-mode|-processes|-ports|-bundle)?|fixture-web-port-seed|source-create|source-snapshot|host-business-backup|source-host-release|archive-secret-scan|restore-session|explicit-activation-confirmation|old-source-preservation-after-confirmation|confirmed-target-web-start|target-normal-stop|final-preservation-audit) type=(?:[A-Za-z]{1,64})$)"
)


class RunnerError(Exception):
    """A fail-closed runner setup or execution error."""


class MissingExpectedMarker(RunnerError):
    """The process succeeded without emitting its configured exact marker."""


class _BasicLimitInformation(ctypes.Structure):
    _fields_ = [
        ("PerProcessUserTimeLimit", ctypes.c_longlong),
        ("PerJobUserTimeLimit", ctypes.c_longlong),
        ("LimitFlags", wintypes.DWORD),
        ("MinimumWorkingSetSize", ctypes.c_size_t),
        ("MaximumWorkingSetSize", ctypes.c_size_t),
        ("ActiveProcessLimit", wintypes.DWORD),
        ("Affinity", ctypes.c_size_t),
        ("PriorityClass", wintypes.DWORD),
        ("SchedulingClass", wintypes.DWORD),
    ]


class _IoCounters(ctypes.Structure):
    _fields_ = [
        ("ReadOperationCount", ctypes.c_ulonglong),
        ("WriteOperationCount", ctypes.c_ulonglong),
        ("OtherOperationCount", ctypes.c_ulonglong),
        ("ReadTransferCount", ctypes.c_ulonglong),
        ("WriteTransferCount", ctypes.c_ulonglong),
        ("OtherTransferCount", ctypes.c_ulonglong),
    ]


class _ExtendedLimitInformation(ctypes.Structure):
    _fields_ = [
        ("BasicLimitInformation", _BasicLimitInformation),
        ("IoInfo", _IoCounters),
        ("ProcessMemoryLimit", ctypes.c_size_t),
        ("JobMemoryLimit", ctypes.c_size_t),
        ("PeakProcessMemoryUsed", ctypes.c_size_t),
        ("PeakJobMemoryUsed", ctypes.c_size_t),
    ]


class _ThreadEntry32(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("cntUsage", wintypes.DWORD),
        ("th32ThreadID", wintypes.DWORD),
        ("th32OwnerProcessID", wintypes.DWORD),
        ("tpBasePri", wintypes.LONG),
        ("tpDeltaPri", wintypes.LONG),
        ("dwFlags", wintypes.DWORD),
    ]


def configure_windows_error_mode() -> int:
    """Set and verify SEM_NOGPFAULTERRORBOX; return the previous process mode."""
    if os.name != "nt":
        raise RunnerError("该运行器仅支持 Windows。")

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    set_error_mode = kernel32.SetErrorMode
    set_error_mode.argtypes = [wintypes.UINT]
    set_error_mode.restype = wintypes.UINT

    previous_mode = int(set_error_mode(SEM_NOGPFAULTERRORBOX))
    intended_mode = previous_mode | SEM_NOGPFAULTERRORBOX
    set_error_mode(intended_mode)
    verified_mode = int(set_error_mode(intended_mode))
    if verified_mode != intended_mode or not (verified_mode & SEM_NOGPFAULTERRORBOX):
        set_error_mode(previous_mode)
        raise RunnerError("无法设置并核验 Windows 无故障弹窗模式；拒绝启动子进程。")
    return previous_mode


def restore_windows_error_mode(previous_mode: int) -> None:
    if os.name != "nt":
        return
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    set_error_mode = kernel32.SetErrorMode
    set_error_mode.argtypes = [wintypes.UINT]
    set_error_mode.restype = wintypes.UINT
    set_error_mode(previous_mode)


def _contains_reparse_point(path: Path) -> bool:
    current = Path(path.anchor)
    for part in path.parts[1:]:
        current = current / part
        try:
            info = current.lstat()
        except FileNotFoundError:
            continue
        if getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
            return True
    return False


def resolve_log_directory(repository_root: Path, requested: Path) -> Path:
    repository_root = repository_root.resolve(strict=True)
    allowed_root_input = repository_root / ".tmp"
    if _contains_reparse_point(allowed_root_input):
        raise RunnerError("仓库 .tmp 路径包含符号链接或其他重解析点，拒绝写入。")
    allowed_root = allowed_root_input.resolve(strict=True)
    candidate = requested if requested.is_absolute() else repository_root / requested
    candidate = Path(os.path.abspath(candidate))
    if _contains_reparse_point(candidate):
        raise RunnerError("日志目录路径包含符号链接或其他重解析点，拒绝写入。")
    try:
        resolved_candidate = candidate.resolve(strict=False)
        resolved_candidate.relative_to(allowed_root)
    except ValueError as exc:
        raise RunnerError("日志目录必须位于仓库 .tmp 目录内。") from exc
    if resolved_candidate == allowed_root:
        raise RunnerError("必须为本次运行指定独立的 .tmp 子目录。")
    return resolved_candidate


def _create_job_object():
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
    kernel32.CreateJobObjectW.restype = wintypes.HANDLE
    handle = kernel32.CreateJobObjectW(None, None)
    if not handle:
        raise RunnerError(f"创建 Windows Job Object 失败：{ctypes.get_last_error()}。")

    limits = _ExtendedLimitInformation()
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL
    kernel32.SetInformationJobObject.argtypes = [
        wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD
    ]
    kernel32.SetInformationJobObject.restype = wintypes.BOOL
    if not kernel32.SetInformationJobObject(
        handle, JobObjectExtendedLimitInformation, ctypes.byref(limits), ctypes.sizeof(limits)
    ):
        error = ctypes.get_last_error()
        kernel32.CloseHandle(handle)
        raise RunnerError(f"设置 Windows Job Object 清理策略失败：{error}。")
    return kernel32, handle


def _primary_thread_id(process_id: int) -> int:
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel32.Thread32First.argtypes = [wintypes.HANDLE, ctypes.POINTER(_ThreadEntry32)]
    kernel32.Thread32First.restype = wintypes.BOOL
    kernel32.Thread32Next.argtypes = [wintypes.HANDLE, ctypes.POINTER(_ThreadEntry32)]
    kernel32.Thread32Next.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL
    snapshot = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0)
    if snapshot == INVALID_HANDLE_VALUE:
        raise RunnerError(f"无法检查暂停中子进程的主线程：{ctypes.get_last_error()}。")
    try:
        entry = _ThreadEntry32()
        entry.dwSize = ctypes.sizeof(entry)
        if not kernel32.Thread32First(snapshot, ctypes.byref(entry)):
            raise RunnerError(f"无法枚举暂停中子进程的主线程：{ctypes.get_last_error()}。")
        while True:
            if entry.th32OwnerProcessID == process_id:
                return int(entry.th32ThreadID)
            if not kernel32.Thread32Next(snapshot, ctypes.byref(entry)):
                break
        raise RunnerError("找不到暂停中子进程的主线程；拒绝启动。")
    finally:
        kernel32.CloseHandle(snapshot)


def _resume_suspended_process(process_id: int) -> None:
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenThread.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.OpenThread.restype = wintypes.HANDLE
    kernel32.ResumeThread.argtypes = [wintypes.HANDLE]
    kernel32.ResumeThread.restype = wintypes.DWORD
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL
    thread_id = _primary_thread_id(process_id)
    thread = kernel32.OpenThread(THREAD_SUSPEND_RESUME, False, thread_id)
    if not thread:
        raise RunnerError(f"无法打开暂停中子进程的主线程：{ctypes.get_last_error()}。")
    try:
        if kernel32.ResumeThread(thread) == 0xFFFFFFFF:
            raise RunnerError(f"无法恢复暂停中的 dotnet 子进程：{ctypes.get_last_error()}。")
    finally:
        kernel32.CloseHandle(thread)


def _capture_output(pipe, destination: Path | None, overflow: threading.Event,
                    errors: list[OSError], safe_diagnostics: list[str] | None = None) -> None:
    captured = 0
    pending_line = bytearray()
    oversized_line = False
    try:
        output_context = destination.open("xb") if destination is not None else None
        try:
            while True:
                chunk = pipe.read(64 * 1024)
                if not chunk:
                    break
                room = MAX_CAPTURE_BYTES_PER_STREAM - captured
                if output_context is not None and room > 0:
                    output_context.write(chunk[:room])
                if safe_diagnostics is not None:
                    for byte in chunk:
                        if byte == 10:
                            line = bytes(pending_line)
                            if line.endswith(b"\r"):
                                line = line[:-1]
                            if not oversized_line and CAS_FAILURE_DIAGNOSTIC_PATTERN.fullmatch(line):
                                safe_diagnostics.append(line.decode("ascii"))
                            pending_line.clear()
                            oversized_line = False
                        elif not oversized_line:
                            if len(pending_line) < 256:
                                pending_line.append(byte)
                            else:
                                pending_line.clear()
                                oversized_line = True
                captured += min(len(chunk), room)
                if len(chunk) > room:
                    overflow.set()
        finally:
            if output_context is not None:
                output_context.close()
    except OSError as exc:
        errors.append(exc)
        overflow.set()
    finally:
        try:
            pipe.close()
        except OSError as exc:
            errors.append(exc)
            overflow.set()


def _contains_exact_marker(output: bytes, expected_marker: str) -> bool:
    marker_bytes = expected_marker.encode("ascii")
    for line in output.split(b"\n"):
        if line.endswith(b"\r"):
            line = line[:-1]
        if line == marker_bytes:
            return True
    return False


def _capture_contains_exact_marker(stdout_path: Path, expected_marker: str) -> bool:
    # stdout is capped at MAX_CAPTURE_BYTES_PER_STREAM by its collector.
    return _contains_exact_marker(stdout_path.read_bytes(), expected_marker)


def _end_job(kernel32, job_handle, exit_code: int, *, terminate: bool,
             cleanup_deadline: float) -> None:
    kernel32.TerminateJobObject.argtypes = [wintypes.HANDLE, wintypes.UINT]
    kernel32.TerminateJobObject.restype = wintypes.BOOL
    kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel32.WaitForSingleObject.restype = wintypes.DWORD
    if terminate and not kernel32.TerminateJobObject(job_handle, exit_code):
        raise RunnerError(f"无法终止自有 Job Object：{ctypes.get_last_error()}。")
    wait_ms = 0 if not terminate else max(0, int((cleanup_deadline - time.monotonic()) * 1000))
    wait_result = kernel32.WaitForSingleObject(job_handle, wait_ms)
    if wait_result == 0x102 and not terminate:
        _end_job(kernel32, job_handle, exit_code, terminate=True,
                 cleanup_deadline=cleanup_deadline)
    elif wait_result != 0:
        raise RunnerError("无法确认自有 Job Object 中的进程树已退出。")


def _wait_process_until(process, cleanup_deadline: float) -> bool:
    remaining = max(0, cleanup_deadline - time.monotonic())
    try:
        process.wait(timeout=remaining)
        return True
    except subprocess.TimeoutExpired:
        return False


def run_dotnet(dotnet_exe: Path, dll: Path, arguments: list[str], timeout_seconds: int,
               log_directory: Path, repository_root: Path,
               expected_marker: str | None = None) -> int:
    if os.name != "nt":
        raise RunnerError("该运行器仅支持 Windows；未启动任何进程。")
    if not 1 <= timeout_seconds <= MAX_TIMEOUT_SECONDS:
        raise RunnerError(f"超时必须在 1 到 {MAX_TIMEOUT_SECONDS} 秒之间。")
    dotnet_exe = dotnet_exe.resolve(strict=True)
    dll = dll.resolve(strict=True)
    if not dotnet_exe.is_file() or dotnet_exe.name.casefold() != "dotnet.exe":
        raise RunnerError("--dotnet-exe 必须指向存在的 dotnet.exe。")
    if not dll.is_file() or dll.suffix.casefold() != ".dll":
        raise RunnerError("--dll 必须指向存在的 DLL 文件。")

    log_directory = resolve_log_directory(repository_root, log_directory)
    try:
        disk = shutil.disk_usage(repository_root / ".tmp")
    except OSError as exc:
        raise RunnerError("无法读取 .tmp 所在磁盘空间；拒绝启动验收子进程。") from exc
    if disk.free < MIN_FREE_DISK_BYTES or disk.free / disk.total < 0.05:
        raise RunnerError(".tmp 所在磁盘低于 10 GiB 或 5% 可用空间阈值；拒绝启动验收子进程。")
    stdout_path = log_directory / "dotnet.stdout.log"
    previous_mode = configure_windows_error_mode()
    job_handle = None
    process = None
    assigned_to_job = False
    timed_out = False
    output_limited = False
    output_errors: list[OSError] = []
    output_overflow = threading.Event()
    collectors: list[threading.Thread] = []
    return_code = 1
    directory_created = False
    cleanup_failed = False
    cleanup_deadline = 0.0
    expected_marker_found = expected_marker is None
    safe_diagnostics: list[str] = []
    try:
        log_directory.mkdir(parents=True, exist_ok=False)
        directory_created = True
        kernel32, job_handle = _create_job_object()
        process = subprocess.Popen(
            [str(dotnet_exe), str(dll), *arguments],
            cwd=str(repository_root),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            bufsize=0,
            close_fds=True,
            creationflags=CREATE_SUSPENDED,
        )
        collectors = [
            threading.Thread(target=_capture_output, args=(process.stdout, stdout_path, output_overflow, output_errors, safe_diagnostics), daemon=True),
            threading.Thread(target=_capture_output, args=(process.stderr, None, output_overflow, output_errors, safe_diagnostics), daemon=True),
        ]
        for collector in collectors:
            collector.start()
        kernel32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
        kernel32.AssignProcessToJobObject.restype = wintypes.BOOL
        if not kernel32.AssignProcessToJobObject(job_handle, int(process._handle)):
            raise RunnerError(f"无法将 dotnet 加入受控 Windows Job Object：{ctypes.get_last_error()}。")
        assigned_to_job = True
        _resume_suspended_process(process.pid)

        deadline = time.monotonic() + timeout_seconds
        while True:
            if output_overflow.is_set():
                output_limited = not output_errors
                cleanup_deadline = time.monotonic() + JOB_CLEANUP_TIMEOUT_SECONDS
                _end_job(kernel32, job_handle, 125, terminate=True,
                         cleanup_deadline=cleanup_deadline)
                if not _wait_process_until(process, cleanup_deadline):
                    cleanup_failed = True
                return_code = 125
                break
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                timed_out = True
                cleanup_deadline = time.monotonic() + JOB_CLEANUP_TIMEOUT_SECONDS
                _end_job(kernel32, job_handle, 124, terminate=True,
                         cleanup_deadline=cleanup_deadline)
                if not _wait_process_until(process, cleanup_deadline):
                    cleanup_failed = True
                return_code = 124
                break
            try:
                return_code = process.wait(timeout=min(0.1, remaining))
                break
            except subprocess.TimeoutExpired:
                continue

        if not timed_out and not output_limited:
            cleanup_deadline = time.monotonic() + JOB_CLEANUP_TIMEOUT_SECONDS
            _end_job(kernel32, job_handle, return_code, terminate=False,
                     cleanup_deadline=cleanup_deadline)
        for collector in collectors:
            collector.join(timeout=max(0, cleanup_deadline - time.monotonic()))
            if collector.is_alive():
                cleanup_failed = True
        if output_errors:
            raise RunnerError("有界输出采集失败。")
        if output_overflow.is_set() and not output_limited:
            output_limited = True
            cleanup_deadline = time.monotonic() + JOB_CLEANUP_TIMEOUT_SECONDS
            _end_job(kernel32, job_handle, 125, terminate=True,
                     cleanup_deadline=cleanup_deadline)
        if return_code == 0 and expected_marker is not None and not output_limited:
            expected_marker_found = _capture_contains_exact_marker(stdout_path, expected_marker)
    finally:
        if process is not None and process.poll() is None:
            cleanup_deadline = cleanup_deadline or (time.monotonic() + JOB_CLEANUP_TIMEOUT_SECONDS)
            if assigned_to_job and job_handle:
                # Closing our kill-on-close job terminates only its assigned members.
                kernel32.CloseHandle(job_handle)
                job_handle = None
            else:
                # This is our own suspended Popen child and was not assigned to a job.
                process.kill()
            if not _wait_process_until(process, cleanup_deadline):
                cleanup_failed = True
        if job_handle:
            kernel32.CloseHandle(job_handle)
        for collector in collectors:
            if collector.is_alive():
                cleanup_deadline = cleanup_deadline or (time.monotonic() + JOB_CLEANUP_TIMEOUT_SECONDS)
                collector.join(timeout=max(0, cleanup_deadline - time.monotonic()))
                if collector.is_alive():
                    cleanup_failed = True
        collectors_done = all(not collector.is_alive() for collector in collectors)
        if directory_created and collectors_done:
            for output_path in (stdout_path,):
                try:
                    output_path.unlink(missing_ok=True)
                except OSError:
                    cleanup_failed = True
            try:
                log_directory.rmdir()
            except OSError:
                cleanup_failed = True
        elif directory_created:
            cleanup_failed = True
        restore_windows_error_mode(previous_mode)
        if cleanup_failed:
            raise RunnerError("有界清理未能确认子进程树或移除未脱敏临时输出。")

    if return_code != 0:
        for diagnostic in safe_diagnostics:
            print(f"SAFE_TEST_DIAGNOSTIC:{diagnostic}", file=sys.stderr)
    if timed_out:
        raise RunnerError(f"dotnet 超过 {timeout_seconds} 秒上限；已终止其专属 Job Object。")
    if output_limited:
        raise RunnerError("stdout 或 stderr 超过单流 16 MiB 上限；已终止其专属 Job Object。")
    if return_code == 0 and not expected_marker_found:
        raise MissingExpectedMarker()
    return int(return_code)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="在 Windows 上对单个 dotnet DLL 调用设置无故障弹窗模式并施加超时。"
    )
    parser.add_argument("--dotnet-exe", type=Path, required=True, help="指定的 dotnet.exe 完整路径")
    parser.add_argument("--dll", type=Path, required=True, help="要运行的一个 DLL 完整路径")
    parser.add_argument("--timeout-seconds", type=int, required=True, help="运行超时，范围 1 到 3600 秒")
    parser.add_argument("--log-dir", type=Path, required=True, help="本次独占临时捕获目录，必须位于仓库 .tmp 内")
    parser.add_argument("--test-id", required=True, help="固定、安全的本次测试标识")
    parser.add_argument("--expected-marker", help="stdout 中必须出现的固定 ASCII 整行成功标记")
    parser.add_argument("--repository-root", type=Path,
                        default=Path(__file__).resolve().parents[2], help=argparse.SUPPRESS)
    parser.add_argument("dotnet_args", nargs=argparse.REMAINDER, help="传给指定 DLL 的参数")
    options = parser.parse_args(argv)
    if not TEST_ID_PATTERN.fullmatch(options.test_id):
        parser.error("--test-id 格式无效。")
    if options.expected_marker is not None and not MARKER_PATTERN.fullmatch(options.expected_marker):
        parser.error("--expected-marker 仅支持固定 ASCII 标记字符。")
    arguments = options.dotnet_args
    if arguments and arguments[0] == "--":
        arguments = arguments[1:]
    try:
        exit_code = run_dotnet(
            options.dotnet_exe, options.dll, arguments, options.timeout_seconds,
            options.log_dir, options.repository_root, options.expected_marker,
        )
    except (OSError, RunnerError, subprocess.SubprocessError) as exc:
        print(f"SAFE_TEST_FAIL:{options.test_id}:EXIT:1:TYPE:{type(exc).__name__}", file=sys.stderr)
        return 1
    if exit_code != 0:
        print(f"SAFE_TEST_FAIL:{options.test_id}:EXIT:{exit_code}:TYPE:DotnetExit", file=sys.stderr)
        return exit_code
    print(options.expected_marker or f"SAFE_TEST_PASS:{options.test_id}")
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
