using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AiGoofish.Launcher.Core;

namespace AiGoofish.Launcher.Platform.Windows;

public sealed class WindowsOwnedProcessManager : IDisposable, IAsyncDisposable
{
    private static readonly string[] InheritedEnvironmentAllowList =
    {
        "COMSPEC",
        "DOTNET_ROOT",
        "NUMBER_OF_PROCESSORS",
        "PATH",
        "PATHEXT",
        "PROCESSOR_ARCHITECTURE",
        "SystemRoot",
        "TEMP",
        "TMP",
        "WINDIR",
    };

    private static readonly HashSet<string> ReservedEnvironmentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AIGOOFISH_LAUNCHER_INSTANCE_ID",
        "AIGOOFISH_LAUNCHER_INSTANCE_ROOT",
        "AIGOOFISH_LAUNCHER_RUN_ID",
    };

    private readonly InstanceDataRootLease _lease;
    private readonly IProcessStopProtocol _stopProtocol;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly BoundedLogBuffer _logs;
    private readonly object _snapshotSync = new();
    private readonly object _lifecycleSync = new();
    private readonly int _maxLogLineLength;
    private readonly LauncherDiagnosticComponent _diagnosticComponent;
    private readonly HashSet<ProcessOutputHint> _reportedOutputHints = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private OwnedProcessRecord? _owned;
    private Task? _pendingStopProtocolTask;
    private OwnedProcessSnapshot _snapshot;
    private bool _disposed;

    public WindowsOwnedProcessManager(
        InstanceDataRootLease lease,
        IProcessStopProtocol stopProtocol,
        int logCapacity = 500,
        int maxLogLineLength = 2048,
        LauncherDiagnosticComponent diagnosticComponent = LauncherDiagnosticComponent.Launcher)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(stopProtocol);
        lease.EnsureHeld();
        if (maxLogLineLength < 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLogLineLength), "单条日志上限不能小于 64 个字符。");
        }

        _lease = lease;
        _stopProtocol = stopProtocol;
        _logs = new BoundedLogBuffer(logCapacity);
        _maxLogLineLength = maxLogLineLength;
        if (!Enum.IsDefined(diagnosticComponent)) throw new ArgumentOutOfRangeException(nameof(diagnosticComponent));
        _diagnosticComponent = diagnosticComponent;
        _snapshot = new OwnedProcessSnapshot(
            OwnedProcessState.NotStarted,
            Identity: null,
            ExitCode: null,
            DateTimeOffset.UtcNow,
            "尚未启动受管进程。");
    }

    public OwnedProcessSnapshot Snapshot
    {
        get
        {
            lock (_snapshotSync)
            {
                return _snapshot;
            }
        }
    }

    public IReadOnlyList<LauncherLogEntry> Logs => _logs.Snapshot();

    public async Task<OwnedProcessOperationResult> StartAsync(
        OwnedProcessLaunchSpec spec,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(spec);
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("已有进程操作正在进行，不能重复启动。");
        }

        try
        {
            _lease.EnsureHeld();
            EnsureNoManagedProcessIsActive();
            var normalizedSpec = ValidateAndNormalize(spec);
            var runId = Guid.NewGuid();
            lock (_reportedOutputHints) _reportedOutputHints.Clear();
            StartupDiagnostics.Current.Record(StartupStage.Process, StartupEventKind.Begin, _diagnosticComponent, operationId: runId);
            var sanitizer = new SensitiveLogSanitizer(
                normalizedSpec.EnvironmentVariables.Where(item => item.Sensitive).Select(item => item.Value));
            var startInfo = CreateStartInfo(normalizedSpec, runId);
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            Transition(OwnedProcessState.Starting, identity: null, exitCode: null, "正在启动受管进程。");
            WriteLog(LauncherLogLevel.Information, "process", $"受管进程启动请求已提交；公开参数数量 {normalizedSpec.PublicArguments.Count}。");
            try
            {
                lock (_lifecycleSync)
                {
                    ThrowIfDisposedLocked();
                    if (!process.Start())
                    {
                        process.Dispose();
                        Transition(OwnedProcessState.StartFailed, identity: null, exitCode: null, "操作系统未创建受管进程。");
                        return Result(false, false, "PROCESS_START_REJECTED");
                    }
                }
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
            {
                StartupDiagnostics.Current.Record(StartupStage.Process, StartupEventKind.Failed, _diagnosticComponent, exception, runId);
                process.Dispose();
                WriteLog(LauncherLogLevel.Error, "process", "操作系统拒绝或无法创建受管进程。");
                Transition(OwnedProcessState.StartFailed, identity: null, exitCode: null, "受管进程启动失败。");
                return Result(false, false, "PROCESS_START_FAILED");
            }

            var identity = new OwnedProcessIdentity(
                process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
                normalizedSpec.ExecutablePath,
                _lease.InstanceRoot,
                _lease.InstanceId,
                runId);
            var standardOutputTask = ConsumeStreamAsync(process.StandardOutput, "process.stdout", sanitizer, _lifetimeCancellation.Token);
            var standardErrorTask = ConsumeStreamAsync(process.StandardError, "process.stderr", sanitizer, _lifetimeCancellation.Token);
            _owned = new OwnedProcessRecord(process, identity, standardOutputTask, standardErrorTask);
            Transition(OwnedProcessState.Starting, identity, exitCode: null, "受管进程已创建，正在确认启动状态。");

            var exitTask = process.WaitForExitAsync(CancellationToken.None);
            try
            {
                using var startupWaitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _lifetimeCancellation.Token);
                var graceTask = Task.Delay(normalizedSpec.StartupGracePeriod, startupWaitCancellation.Token);
                var completed = await Task.WhenAny(exitTask, graceTask).ConfigureAwait(false);
                if (completed == exitTask)
                {
                    await exitTask.ConfigureAwait(false);
                    return await CompleteExitedAsync("受管进程在启动确认期内退出。", "PROCESS_EARLY_EXIT").ConfigureAwait(false);
                }

                await graceTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested || _lifetimeCancellation.IsCancellationRequested)
            {
                var cancelledSnapshot = RefreshOwnership(identity, process, OwnedProcessState.Running);
                WriteLog(LauncherLogLevel.Warning, "process", "启动等待已取消；受管进程归属记录仍保留。");
                return new OwnedProcessOperationResult(false, true, "PROCESS_START_CANCELLED", cancelledSnapshot);
            }

            var verified = RefreshOwnership(identity, process, OwnedProcessState.Running);
            if (verified.State is OwnedProcessState.Running)
            {
                WriteLog(LauncherLogLevel.Information, "process", "受管进程启动并通过归属复核。");
                return Result(true, false, null);
            }

            if (verified.State is OwnedProcessState.Exited)
            {
                return await CompleteExitedAsync("受管进程在启动确认时已退出。", "PROCESS_EARLY_EXIT").ConfigureAwait(false);
            }

            WriteLog(LauncherLogLevel.Error, "process", "受管进程已创建，但无法确认其归属。");
            return Result(false, false, "PROCESS_OWNERSHIP_UNKNOWN");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<OwnedProcessOperationResult> StopAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "停止等待必须大于零。");
        }

        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("已有进程操作正在进行，不能并发停止。");
        }

        try
        {
            _lease.EnsureHeld();
            var owned = _owned ?? throw new InvalidOperationException("当前没有可停止的受管进程。");
            if (_pendingStopProtocolTask is { IsCompleted: false })
            {
                var pending = RefreshOwnership(owned.Identity, owned.Process, OwnedProcessState.StopRequested);
                WriteLog(LauncherLogLevel.Warning, "process", "上一次正常停止协议仍未完成，未重复发送停止请求。");
                return new OwnedProcessOperationResult(false, false, "PROCESS_STOP_PROTOCOL_PENDING", pending);
            }

            _pendingStopProtocolTask = null;
            var verified = RefreshOwnership(owned.Identity, owned.Process, OwnedProcessState.Running);
            if (verified.State is OwnedProcessState.Exited)
            {
                return await CompleteExitedAsync("受管进程已经退出。", errorCode: null).ConfigureAwait(false);
            }

            if (verified.State is not OwnedProcessState.Running)
            {
                WriteLog(LauncherLogLevel.Error, "process", "停止前无法确认受管进程归属，未发送停止请求。");
                return Result(false, false, "PROCESS_OWNERSHIP_UNKNOWN");
            }

            Transition(OwnedProcessState.StopRequested, owned.Identity, exitCode: null, "已发送正常停止请求，正在等待退出。");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            deadline.CancelAfter(timeout);
            try
            {
                _pendingStopProtocolTask = _stopProtocol.RequestStopAsync(owned.Identity, deadline.Token);
                await _pendingStopProtocolTask.WaitAsync(deadline.Token).ConfigureAwait(false);
                _pendingStopProtocolTask = null;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested || _lifetimeCancellation.IsCancellationRequested)
            {
                var cancelled = RefreshOwnership(owned.Identity, owned.Process, OwnedProcessState.Running);
                WriteLog(LauncherLogLevel.Warning, "process", "正常停止请求已取消；未执行强制终止。");
                return new OwnedProcessOperationResult(false, true, "PROCESS_STOP_CANCELLED", cancelled);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested &&
                !_lifetimeCancellation.IsCancellationRequested &&
                deadline.IsCancellationRequested)
            {
                var timedOut = RefreshOwnership(owned.Identity, owned.Process, OwnedProcessState.StopTimedOut);
                WriteLog(LauncherLogLevel.Warning, "process", "正常停止协议在总时限内未完成；未强制终止，且不会重复发送停止请求。");
                return new OwnedProcessOperationResult(false, false, "PROCESS_STOP_TIMEOUT", timedOut);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
            {
                var failed = RefreshOwnership(owned.Identity, owned.Process, OwnedProcessState.Running);
                WriteLog(LauncherLogLevel.Error, "process", "正常停止协议失败；未执行强制终止。");
                return new OwnedProcessOperationResult(false, false, "PROCESS_STOP_PROTOCOL_FAILED", failed);
            }

            try
            {
                await owned.Process.WaitForExitAsync(CancellationToken.None).WaitAsync(deadline.Token).ConfigureAwait(false);
                return await CompleteExitedAsync("受管进程已正常退出。", errorCode: null).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested &&
                !_lifetimeCancellation.IsCancellationRequested &&
                deadline.IsCancellationRequested)
            {
                var timedOut = RefreshOwnership(owned.Identity, owned.Process, OwnedProcessState.StopTimedOut);
                if (timedOut.State is OwnedProcessState.Exited)
                {
                    return await CompleteExitedAsync("受管进程已正常退出。", errorCode: null).ConfigureAwait(false);
                }

                WriteLog(LauncherLogLevel.Warning, "process", "停止等待超时；未执行强制终止，归属记录继续保留。");
                return new OwnedProcessOperationResult(false, false, "PROCESS_STOP_TIMEOUT", timedOut);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested || _lifetimeCancellation.IsCancellationRequested)
            {
                var cancelled = RefreshOwnership(owned.Identity, owned.Process, OwnedProcessState.Running);
                WriteLog(LauncherLogLevel.Warning, "process", "停止等待已取消；未执行强制终止，归属记录继续保留。");
                return new OwnedProcessOperationResult(false, true, "PROCESS_STOP_CANCELLED", cancelled);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<OwnedProcessOperationResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lease.EnsureHeld();
            var owned = _owned;
            if (owned is null)
            {
                return Result(false, false, "PROCESS_NOT_MANAGED");
            }

            var refreshed = RefreshOwnership(owned.Identity, owned.Process, OwnedProcessState.Running);
            return new OwnedProcessOperationResult(
                refreshed.State is OwnedProcessState.Running or OwnedProcessState.Exited,
                false,
                refreshed.State is OwnedProcessState.Unknown ? "PROCESS_OWNERSHIP_UNKNOWN" : null,
                refreshed);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<OwnedProcessOperationResult> TryReconnectAsync(
        OwnedProcessIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(identity);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lease.EnsureHeld();
            if (_owned is not null)
            {
                throw new InvalidOperationException("当前管理器已持有进程归属，不能覆盖重连。");
            }

            if (!IdentityMatchesLease(identity))
            {
                Transition(OwnedProcessState.Unknown, identity, exitCode: null, "持久身份与当前实例数据根不匹配。");
                return Result(false, false, "PROCESS_INSTANCE_MISMATCH");
            }

            Process process;
            try
            {
                process = Process.GetProcessById(identity.ProcessId);
            }
            catch (ArgumentException)
            {
                Transition(OwnedProcessState.Exited, identity, exitCode: null, "持久身份对应的进程已不存在。");
                return Result(false, false, "PROCESS_NOT_FOUND");
            }

            var verified = RefreshOwnership(identity, process, OwnedProcessState.Running);
            if (verified.State is not OwnedProcessState.Running)
            {
                process.Dispose();
                return Result(false, false, verified.State is OwnedProcessState.Exited ? "PROCESS_NOT_FOUND" : "PROCESS_OWNERSHIP_UNKNOWN");
            }

            _owned = new OwnedProcessRecord(process, identity, Task.CompletedTask, Task.CompletedTask);
            WriteLog(LauncherLogLevel.Information, "process", "已通过 PID、启动时间、可执行文件和实例身份复核重连受管进程。");
            return Result(true, false, null);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (!_operationGate.Wait(0))
        {
            throw new InvalidOperationException("进程操作仍在进行；请使用 DisposeAsync 等待安全释放。");
        }

        try
        {
            lock (_lifecycleSync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _lifetimeCancellation.Cancel();
                _owned?.Process.Dispose();
                WriteLog(LauncherLogLevel.Information, "process", "进程管理器已释放；未强制终止受管进程，也未释放共享实例锁。");
                _lifetimeCancellation.Dispose();
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycleSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetimeCancellation.Cancel();
        }

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var owned = _owned;
            if (owned is not null)
            {
                try
                {
                    await Task.WhenAll(owned.StandardOutputTask, owned.StandardErrorTask)
                        .WaitAsync(TimeSpan.FromSeconds(1))
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException)
                {
                    WriteLog(LauncherLogLevel.Warning, "process", "释放时输出读取未完全结束；未终止受管进程。");
                }

                owned.Process.Dispose();
            }

            WriteLog(LauncherLogLevel.Information, "process", "进程管理器已异步释放；未强制终止受管进程，也未释放共享实例锁。");
            _lifetimeCancellation.Dispose();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private OwnedProcessLaunchSpec ValidateAndNormalize(OwnedProcessLaunchSpec spec)
    {
        if (spec.StartupGracePeriod <= TimeSpan.Zero || spec.StartupGracePeriod > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(spec), "启动确认期必须在 0 到 5 分钟之间。");
        }

        var executablePath = Path.GetFullPath(spec.ExecutablePath);
        if (!Path.IsPathFullyQualified(spec.ExecutablePath) || !File.Exists(executablePath))
        {
            throw new FileNotFoundException("受管进程可执行文件必须是存在的绝对路径。", executablePath);
        }

        var workingDirectory = Path.GetFullPath(spec.WorkingDirectory);
        if (!Path.IsPathFullyQualified(spec.WorkingDirectory) || !Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException("受管进程工作目录必须是存在的绝对路径。");
        }

        if (spec.PublicArguments.Any(argument => argument is null))
        {
            throw new ArgumentException("公开参数不能包含 null。", nameof(spec));
        }

        var seenEnvironmentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in spec.EnvironmentVariables)
        {
            if (string.IsNullOrWhiteSpace(variable.Name) || variable.Name.Contains('=', StringComparison.Ordinal))
            {
                throw new ArgumentException("环境变量名称无效。", nameof(spec));
            }

            if (ReservedEnvironmentNames.Contains(variable.Name))
            {
                throw new ArgumentException($"环境变量 {variable.Name} 由进程管理器保留。", nameof(spec));
            }

            if (!seenEnvironmentNames.Add(variable.Name))
            {
                throw new ArgumentException($"环境变量 {variable.Name} 重复。", nameof(spec));
            }
        }

        return spec with
        {
            ExecutablePath = executablePath,
            WorkingDirectory = workingDirectory,
            PublicArguments = spec.PublicArguments.ToArray(),
            EnvironmentVariables = spec.EnvironmentVariables.ToArray(),
        };
    }

    private ProcessStartInfo CreateStartInfo(OwnedProcessLaunchSpec spec, Guid runId)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.ExecutablePath,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        foreach (var argument in spec.PublicArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var name in InheritedEnvironmentAllowList)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                startInfo.Environment[name] = value;
            }
        }

        foreach (var variable in spec.EnvironmentVariables)
        {
            startInfo.Environment[variable.Name] = variable.Value;
        }

        startInfo.Environment["AIGOOFISH_LAUNCHER_INSTANCE_ID"] = _lease.InstanceId.ToString("D");
        startInfo.Environment["AIGOOFISH_LAUNCHER_INSTANCE_ROOT"] = _lease.InstanceRoot;
        startInfo.Environment["AIGOOFISH_LAUNCHER_RUN_ID"] = runId.ToString("D");
        return startInfo;
    }

    private void EnsureNoManagedProcessIsActive()
    {
        if (_owned is null)
        {
            return;
        }

        var refreshed = RefreshOwnership(_owned.Identity, _owned.Process, OwnedProcessState.Running);
        if (refreshed.State is not OwnedProcessState.Exited)
        {
            throw new InvalidOperationException("仍有运行中或归属未知的受管进程，不能重复启动。");
        }

        _owned.Process.Dispose();
        _owned = null;
    }

    private OwnedProcessSnapshot RefreshOwnership(
        OwnedProcessIdentity identity,
        Process process,
        OwnedProcessState runningState)
    {
        if (!IdentityMatchesLease(identity) || process.Id != identity.ProcessId)
        {
            return Transition(OwnedProcessState.Unknown, identity, exitCode: null, "进程身份与当前实例会话不匹配。");
        }

        try
        {
            if (process.HasExited)
            {
                return Transition(OwnedProcessState.Exited, identity, process.ExitCode, "受管进程已退出。");
            }

            var observedStartTime = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var observedExecutable = process.MainModule?.FileName;
            if (observedStartTime.UtcTicks != identity.StartedAtUtc.UtcTicks ||
                string.IsNullOrWhiteSpace(observedExecutable) ||
                !string.Equals(
                    Path.GetFullPath(observedExecutable),
                    identity.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Transition(OwnedProcessState.Unknown, identity, exitCode: null, "PID 已存在，但启动时间或可执行文件不匹配。");
            }

            return Transition(runningState, identity, exitCode: null, "受管进程仍在运行且归属已复核。");
        }
        catch (InvalidOperationException) when (TryGetExitCode(process, out var exitCode))
        {
            return Transition(OwnedProcessState.Exited, identity, exitCode, "受管进程已退出。");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return Transition(OwnedProcessState.Unknown, identity, exitCode: null, "无法读取足够信息复核受管进程归属。");
        }
    }

    private bool IdentityMatchesLease(OwnedProcessIdentity identity)
    {
        return _lease.IsHeld &&
            identity.InstanceId == _lease.InstanceId &&
            string.Equals(identity.InstanceRoot, _lease.InstanceRoot, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<OwnedProcessOperationResult> CompleteExitedAsync(string message, string? errorCode)
    {
        var owned = _owned ?? throw new InvalidOperationException("受管进程记录不存在。");
        await AwaitStreamReadersAsync(owned).ConfigureAwait(false);
        int? exitCode = TryGetExitCode(owned.Process, out var observedExitCode) ? observedExitCode : null;
        Transition(OwnedProcessState.Exited, owned.Identity, exitCode, message);
        StartupDiagnostics.Current.Record(StartupStage.Process, StartupEventKind.ProcessExited,
            _diagnosticComponent, operationId: owned.Identity.RunId, exitCode: exitCode);
        WriteLog(
            exitCode == 0 ? LauncherLogLevel.Information : LauncherLogLevel.Warning,
            "process",
            $"受管进程已退出；退出码 {(exitCode?.ToString() ?? "未知")}。");
        return Result(errorCode is null, false, errorCode);
    }

    private async Task AwaitStreamReadersAsync(OwnedProcessRecord owned)
    {
        try
        {
            await Task.WhenAll(owned.StandardOutputTask, owned.StandardErrorTask)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException)
        {
            WriteLog(LauncherLogLevel.Warning, "process", "进程输出流未在时限内完全收尾。");
        }
    }

    private async Task ConsumeStreamAsync(
        StreamReader reader,
        string component,
        SensitiveLogSanitizer sanitizer,
        CancellationToken cancellationToken)
    {
        var buffer = new char[256];
        var pending = new StringBuilder(Math.Min(_maxLogLineLength + sanitizer.MaxSensitiveLength, 4096));
        var droppingRemainder = false;
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                for (var index = 0; index < read; index++)
                {
                    var character = buffer[index];
                    if (character == '\n')
                    {
                        if (!droppingRemainder)
                        {
                            EmitProcessLine(component, pending.ToString().TrimEnd('\r'), sanitizer, truncated: false);
                        }

                        pending.Clear();
                        droppingRemainder = false;
                        continue;
                    }

                    if (droppingRemainder)
                    {
                        continue;
                    }

                    pending.Append(character);
                    if (pending.Length > _maxLogLineLength + sanitizer.MaxSensitiveLength)
                    {
                        EmitProcessLine(component, pending.ToString(), sanitizer, truncated: true);
                        pending.Clear();
                        droppingRemainder = true;
                    }
                }
            }

            if (pending.Length > 0 && !droppingRemainder)
            {
                WriteLog(LauncherLogLevel.Warning, component, "[未完成输出已省略]");
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            WriteLog(LauncherLogLevel.Warning, component, "[输出流不可用]");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WriteLog(LauncherLogLevel.Debug, component, "[输出读取已停止]");
        }
    }

    private void EmitProcessLine(
        string component,
        string rawLine,
        SensitiveLogSanitizer sanitizer,
        bool truncated)
    {
        var sanitized = sanitizer.Redact(rawLine);
        var hint = StartupDiagnostics.ClassifyOutput(sanitized);
        bool report;
        lock (_reportedOutputHints) report = hint is not ProcessOutputHint.None && _reportedOutputHints.Add(hint);
        if (report) StartupDiagnostics.Current.Record(StartupStage.Process, StartupEventKind.OutputHint,
            _diagnosticComponent, operationId: Snapshot.Identity?.RunId, outputHint: hint);
        var suffix = truncated ? "…[已截断]" : string.Empty;
        var availableLength = Math.Max(0, _maxLogLineLength - suffix.Length);
        var content = sanitized.Length > availableLength ? sanitized[..availableLength] : sanitized;
        WriteLog(LauncherLogLevel.Information, component, content + suffix);
    }

    private void WriteLog(LauncherLogLevel level, string component, string message)
    {
        var boundedMessage = message.Length <= _maxLogLineLength
            ? message
            : message[.._maxLogLineLength];
        _logs.Add(new LauncherLogEntry(DateTimeOffset.Now, level, component, boundedMessage));
    }

    private OwnedProcessSnapshot Transition(
        OwnedProcessState state,
        OwnedProcessIdentity? identity,
        int? exitCode,
        string message)
    {
        var snapshot = new OwnedProcessSnapshot(state, identity, exitCode, DateTimeOffset.UtcNow, message);
        lock (_snapshotSync)
        {
            _snapshot = snapshot;
        }

        return snapshot;
    }

    private OwnedProcessOperationResult Result(bool succeeded, bool cancelled, string? errorCode)
    {
        return new OwnedProcessOperationResult(succeeded, cancelled, errorCode, Snapshot);
    }

    private static bool TryGetExitCode(Process process, out int exitCode)
    {
        try
        {
            if (process.HasExited)
            {
                exitCode = process.ExitCode;
                return true;
            }
        }
        catch (InvalidOperationException)
        {
        }

        exitCode = default;
        return false;
    }

    private void ThrowIfDisposed()
    {
        lock (_lifecycleSync)
        {
            ThrowIfDisposedLocked();
        }
    }

    private void ThrowIfDisposedLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class SensitiveLogSanitizer
    {
        private readonly string[] _sensitiveValues;

        public SensitiveLogSanitizer(IEnumerable<string> sensitiveValues)
        {
            _sensitiveValues = sensitiveValues
                .Where(value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(value => value.Length)
                .ToArray();
            MaxSensitiveLength = _sensitiveValues.Length == 0 ? 0 : _sensitiveValues.Max(value => value.Length);
        }

        public int MaxSensitiveLength { get; }

        public string Redact(string value)
        {
            foreach (var sensitiveValue in _sensitiveValues)
            {
                value = value.Replace(sensitiveValue, "[REDACTED]", StringComparison.Ordinal);
            }

            return value;
        }
    }
}
