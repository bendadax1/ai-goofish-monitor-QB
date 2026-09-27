using AiGoofish.Launcher.Core;
using System.Text;

namespace AiGoofish.Launcher.Platform.Windows;

public enum PostgresTransportState
{
    AcceptingConnections,
    RejectingConnections,
    NoResponse,
    Unknown,
}

public sealed record WindowsPostgresOptions(
    string InstallationRoot,
    string DataDirectory,
    int Port,
    string EngineVersion,
    TimeSpan InitializationTimeout,
    TimeSpan StartupTimeout,
    TimeSpan StopTimeout)
{
    public const string BootstrapAdminRole = "aigoofish_bootstrap";
}

public sealed record PostgresRuntimeRecord(
    int FormatVersion,
    Guid InstanceId,
    Guid ClusterId,
    string DataDirectory,
    string EngineVersion,
    int Port,
    string State,
    OwnedProcessIdentity Identity,
    DateTimeOffset ObservedAtUtc);

public sealed class PostgresLifecycleException : InvalidOperationException, ILauncherSafeFailure
{
    public PostgresLifecycleException(string message, LauncherDiagnosticCode? safeDiagnosticCode = null)
        : base(message)
    {
        SafeDiagnosticCode = safeDiagnosticCode;
    }

    public PostgresLifecycleException(string message, Exception innerException, LauncherDiagnosticCode? safeDiagnosticCode = null)
        : base(message, innerException)
    {
        SafeDiagnosticCode = safeDiagnosticCode;
    }

    public LauncherDiagnosticCode? SafeDiagnosticCode { get; }
}

public sealed class WindowsPostgresComponent : ILauncherComponent, IAsyncDisposable
{
    private const string MarkerFileName = ".aigoofish-cluster.json";
    private const string RuntimeRelativePath = "config/postgres-runtime.json";
    private const int StateFileLimit = 64 * 1024;
    private readonly InstanceDataRootLease _lease;
    private readonly InstanceSecrets _secrets;
    private readonly WindowsPostgresOptions _options;
    private readonly PostgresInstallation _installation;
    private readonly WindowsOwnedProcessManager _processManager;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _lifecycleSync = new();
    private PostgresClusterMarker? _marker;
    private OwnedProcessIdentity? _initializationHelper;
    private bool _initializationHelperMayExist;
    private bool _initializedWithoutPostmaster;
    private int _initializationActive;
    private bool _disposed;

    public WindowsPostgresComponent(
        InstanceDataRootLease lease,
        InstanceSecrets secrets,
        WindowsPostgresOptions options)
    {
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _options = NormalizeAndValidate(lease, options ?? throw new ArgumentNullException(nameof(options)));
        _installation = PostgresInstallation.Validate(_options.InstallationRoot, _options.EngineVersion);
        _processManager = new WindowsOwnedProcessManager(
            lease,
            new PgCtlSmartStopProtocol(_installation.PgCtlPath, _options.DataDirectory),
            logCapacity: 500,
            maxLogLineLength: 2048,
            diagnosticComponent: LauncherDiagnosticComponent.Postgres);
    }

    public string Id => "postgres";

    public string DisplayName => "PostgreSQL";

    public OwnedProcessSnapshot ProcessSnapshot => _processManager.Snapshot;

    public IReadOnlyList<LauncherLogEntry> Logs => _processManager.Logs;

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        await _initializationGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        Interlocked.Exchange(ref _initializationActive, 1);
        try
        {
            _lease.EnsureHeld();
            PostgresPathGuard.EnsureSafePath(_lease, _options.DataDirectory, allowMissingLeaf: true);
            if (!Directory.Exists(_options.DataDirectory))
            {
                Directory.CreateDirectory(_options.DataDirectory);
            }

            var markerPath = Path.Combine(_options.DataDirectory, MarkerFileName);
            var initProgressPath = GetInitializationProgressPath();
            if (File.Exists(markerPath))
            {
                if (File.Exists(initProgressPath))
                {
                    throw new PostgresLifecycleException(
                        "集群标记与初始化进行中记录冲突，拒绝假定初始化完成。",
                        LauncherDiagnosticCode.PostgresDataUnverified);
                }

                try
                {
                    _marker = LoadAndValidateMarker(markerPath);
                }
                catch (PostgresLifecycleException exception) when (exception.SafeDiagnosticCode is null)
                {
                    throw new PostgresLifecycleException(
                        "PostgreSQL 初始化标记或数据目录无法核验；数据已保留且拒绝自动修复。",
                        exception,
                        LauncherDiagnosticCode.PostgresDataUnverified);
                }
                return;
            }

            if (File.Exists(initProgressPath))
            {
                try
                {
                    var progress = PostgresStateFile.Read<PostgresInitializationProgress>(initProgressPath, StateFileLimit);
                    if (!IsValidInitializationProgress(
                        progress.FormatVersion,
                        progress.InstanceId,
                        progress.DataDirectory,
                        progress.EngineVersion,
                        progress.State,
                        progress.Helper,
                        _lease.InstanceId,
                        _options.DataDirectory,
                        _options.EngineVersion,
                        _installation.InitDbPath))
                    {
                        throw new PostgresLifecycleException("PostgreSQL 初始化进度记录与当前实例不匹配。");
                    }
                }
                catch (PostgresLifecycleException exception)
                {
                    throw new PostgresLifecycleException(
                        "PostgreSQL 初始化进度记录无法核验；数据已保留且拒绝自动修复。",
                        exception,
                        LauncherDiagnosticCode.PostgresDataUnverified);
                }
                throw new PostgresLifecycleException(
                    "检测到 PostgreSQL 首次初始化中断记录；拒绝自动重试或重建。",
                    LauncherDiagnosticCode.PostgresInitInterrupted);
            }

            if (Directory.EnumerateFileSystemEntries(_options.DataDirectory).Any())
            {
                throw new PostgresLifecycleException(
                    "PostgreSQL 数据目录非空但缺少有效初始化标记，拒绝自动重建或补标记。",
                    LauncherDiagnosticCode.PostgresMarkerMissing);
            }

            PostgresPathGuard.EnsureNewInstancePathLength(_lease.InstanceRoot);

            var progressRecord = new PostgresInitializationProgress(
                FormatVersion: 1,
                InstanceId: _lease.InstanceId,
                DataDirectory: _options.DataDirectory,
                EngineVersion: _options.EngineVersion,
                State: "Starting",
                Helper: null,
                ObservedAtUtc: DateTimeOffset.UtcNow);
            PostgresStateFile.WriteNew(initProgressPath, progressRecord);
            await RunInitDbAsync(progressRecord, linkedCancellation.Token).ConfigureAwait(false);
            var pgVersionPath = Path.Combine(_options.DataDirectory, "PG_VERSION");
            if (!File.Exists(pgVersionPath) ||
                !string.Equals(File.ReadAllText(pgVersionPath, Encoding.UTF8).Trim(), "17", StringComparison.Ordinal))
            {
                throw new PostgresLifecycleException(
                    "initdb 返回成功，但 PostgreSQL 数据目录未通过版本校验；未写初始化标记。",
                    LauncherDiagnosticCode.PostgresDataUnverified);
            }

            var marker = new PostgresClusterMarker(
                FormatVersion: 1,
                InstanceId: _lease.InstanceId,
                ClusterId: Guid.NewGuid(),
                EngineVersion: _options.EngineVersion,
                CreatedAtUtc: DateTimeOffset.UtcNow);
            PostgresStateFile.WriteNew(markerPath, marker);
            try
            {
                File.Delete(initProgressPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new PostgresLifecycleException(
                    "集群初始化已验证，但状态记录未能一致提交；拒绝假定完成。",
                    exception,
                    LauncherDiagnosticCode.PostgresDataUnverified);
            }

            _marker = marker;
            _initializedWithoutPostmaster = true;
        }
        catch (PostgresLifecycleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PostgresLifecycleException("无法检查或初始化 PostgreSQL 数据目录。", exception);
        }
        finally
        {
            Interlocked.Exchange(ref _initializationActive, 0);
            _initializationGate.Release();
        }
    }

    internal ValueTask<bool> IsInitializationQuiescentAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _lease.EnsureHeld();
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _initializationActive) != 0) return ValueTask.FromResult(false);
        if (_initializationHelperMayExist)
            return ValueTask.FromResult(_initializationHelper is not null &&
                GetIdentityRunState(_initializationHelper) is IdentityRunState.NotRunning);
        // A previous process's interrupted record is not proof of quiescence.
        var progress = GetInitializationProgressPath();
        PostgresPathGuard.EnsureSafePath(_lease, progress, allowMissingLeaf: true);
        return ValueTask.FromResult(!File.Exists(progress));
    }

    internal async Task VerifyInitializationCompletedAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lease.EnsureHeld();
            if (!await IsInitializationQuiescentAsync(cancellationToken).ConfigureAwait(false) ||
                File.Exists(GetInitializationProgressPath()))
                throw new PostgresLifecycleException("初始化辅助进程或中断记录尚未核验，拒绝接管。",
                    LauncherDiagnosticCode.PostgresInitInterrupted);
            _marker = LoadAndValidateMarker(Path.Combine(_options.DataDirectory, MarkerFileName));
        }
        finally { _initializationGate.Release(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _lease.EnsureHeld();
        if (!await IsInitializationQuiescentAsync(cancellationToken).ConfigureAwait(false) ||
            File.Exists(GetInitializationProgressPath()))
            throw new PostgresLifecycleException("初始化未确认结束，拒绝启动 PostgreSQL 主进程。",
                LauncherDiagnosticCode.PostgresInitInterrupted);
        var marker = _marker ?? LoadAndValidateMarker(Path.Combine(_options.DataDirectory, MarkerFileName));
        _marker = marker;
        var spec = new OwnedProcessLaunchSpec(
            _installation.PostgresPath,
            _installation.BinDirectory,
            new[]
            {
                "-D", _options.DataDirectory,
                "-h", "127.0.0.1",
                "-p", _options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-c", "password_encryption=scram-sha-256",
                "-c", "log_statement=none",
                "-c", "log_min_error_statement=panic",
            },
            Array.Empty<ProcessEnvironmentVariable>(),
            StartupGracePeriod: TimeSpan.FromMilliseconds(350));
        _initializedWithoutPostmaster = false;
        var started = await _processManager.StartAsync(spec, cancellationToken).ConfigureAwait(false);
        if (started.Snapshot.Identity is null)
        {
            throw new PostgresLifecycleException($"PostgreSQL 主进程启动失败（{started.ErrorCode ?? "UNKNOWN"}）。");
        }

        var startingRuntime = new PostgresRuntimeRecord(
            FormatVersion: 1,
            InstanceId: _lease.InstanceId,
            ClusterId: marker.ClusterId,
            DataDirectory: _options.DataDirectory,
            EngineVersion: _options.EngineVersion,
            Port: _options.Port,
            State: "Starting",
            Identity: started.Snapshot.Identity,
            ObservedAtUtc: DateTimeOffset.UtcNow);
        WriteRuntime(startingRuntime);
        if (started.Cancelled)
        {
            throw new OperationCanceledException("PostgreSQL 启动等待已取消；受管归属与 Starting 记录仍保留。", cancellationToken);
        }

        if (!started.Succeeded)
        {
            throw new PostgresLifecycleException($"PostgreSQL 主进程启动失败（{started.ErrorCode ?? "UNKNOWN"}）。");
        }

        var deadline = DateTimeOffset.UtcNow + _options.StartupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processState = await _processManager.RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (processState.Snapshot.State is OwnedProcessState.Exited or OwnedProcessState.Unknown)
            {
                throw new PostgresLifecycleException("PostgreSQL 在传输就绪前退出或归属变为未知。");
            }

            if (await CheckTransportAsync(cancellationToken).ConfigureAwait(false) is PostgresTransportState.AcceptingConnections)
            {
                var verified = await _processManager.RefreshAsync(cancellationToken).ConfigureAwait(false);
                if (!verified.Succeeded ||
                    verified.Snapshot.State is not OwnedProcessState.Running ||
                    verified.Snapshot.Identity != startingRuntime.Identity)
                {
                    throw new PostgresLifecycleException("pg_isready 可连接，但受管 postgres.exe 归属复核失败，拒绝标记 Running。");
                }

                ValidatePostmasterPid(startingRuntime);
                var runtime = new PostgresRuntimeRecord(
                    FormatVersion: 1,
                    InstanceId: _lease.InstanceId,
                    ClusterId: marker.ClusterId,
                    DataDirectory: _options.DataDirectory,
                    EngineVersion: _options.EngineVersion,
                    Port: _options.Port,
                    State: "Running",
                    Identity: started.Snapshot.Identity,
                    ObservedAtUtc: DateTimeOffset.UtcNow);
                WriteRuntime(runtime);
                return;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new PostgresLifecycleException("PostgreSQL 主进程仍受管，但 pg_isready 未在时限内达到传输可连接状态。");
    }

    public async ValueTask<ComponentRuntimeState> GetStateAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var current = _processManager.Snapshot;
        if (current.Identity is null)
        {
            if (!File.Exists(GetRuntimePath()))
            {
                if (_initializedWithoutPostmaster && _marker is not null &&
                    !File.Exists(GetInitializationProgressPath()) &&
                    !File.Exists(Path.Combine(_options.DataDirectory, "postmaster.pid")))
                {
                    // Only this live owner witnessed successful initdb without
                    // attempting postmaster. Never infer this from files on reopen.
                    try
                    {
                        if (LoadAndValidateMarker(Path.Combine(_options.DataDirectory, MarkerFileName)) == _marker)
                            return ComponentRuntimeState.Stopped;
                    }
                    catch (PostgresLifecycleException) { }
                    return ComponentRuntimeState.Unknown;
                }
                // An initialized cluster without a runtime record has no
                // durable proof that its previous postmaster stopped.
                return !File.Exists(Path.Combine(_options.DataDirectory, MarkerFileName)) &&
                    !File.Exists(Path.Combine(_options.DataDirectory, "postmaster.pid"))
                    ? ComponentRuntimeState.Stopped
                    : ComponentRuntimeState.Unknown;
            }

            try
            {
                var runtime = LoadRuntime();
                var marker = LoadAndValidateMarker(Path.Combine(_options.DataDirectory, MarkerFileName));
                if (IsStoppedRuntimeForCurrentCluster(runtime, marker) &&
                    !File.Exists(Path.Combine(_options.DataDirectory, "postmaster.pid")) &&
                    GetIdentityRunState(runtime.Identity) is IdentityRunState.NotRunning)
                {
                    return ComponentRuntimeState.Stopped;
                }
            }
            catch (PostgresLifecycleException)
            {
            }

            return ComponentRuntimeState.Unknown;
        }

        var refreshed = await _processManager.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return refreshed.Snapshot.State switch
        {
            OwnedProcessState.Running => ComponentRuntimeState.Running,
            OwnedProcessState.Starting => ComponentRuntimeState.Starting,
            OwnedProcessState.StopRequested => ComponentRuntimeState.Stopping,
            OwnedProcessState.Exited => ComponentRuntimeState.Stopped,
            OwnedProcessState.StartFailed => ComponentRuntimeState.Failed,
            OwnedProcessState.StopTimedOut or OwnedProcessState.Unknown => ComponentRuntimeState.Unknown,
            _ => ComponentRuntimeState.Unknown,
        };
    }

    private bool IsStoppedRuntimeForCurrentCluster(PostgresRuntimeRecord runtime, PostgresClusterMarker marker) =>
        runtime.FormatVersion == 1 &&
        runtime.State == "Stopped" &&
        runtime.InstanceId == _lease.InstanceId &&
        runtime.ClusterId == marker.ClusterId &&
        runtime.EngineVersion == _options.EngineVersion &&
        runtime.Port == _options.Port &&
        string.Equals(runtime.DataDirectory, _options.DataDirectory, StringComparison.OrdinalIgnoreCase) &&
        runtime.Identity is not null &&
        runtime.Identity.InstanceId == _lease.InstanceId &&
        string.Equals(runtime.Identity.InstanceRoot, _lease.InstanceRoot, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(runtime.Identity.ExecutablePath, _installation.PostgresPath, StringComparison.OrdinalIgnoreCase);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var runtime = LoadRuntime();
        if (runtime.State is not ("Running" or "Starting") ||
            _processManager.Snapshot.Identity != runtime.Identity)
        {
            throw new PostgresLifecycleException("停止前运行身份记录与当前受管 PostgreSQL 不匹配，拒绝发送 smart stop。");
        }

        ValidatePostmasterPid(runtime);
        var result = await _processManager.StopAsync(_options.StopTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Snapshot.Identity is null)
        {
            if (result.Cancelled)
            {
                throw new OperationCanceledException("PostgreSQL smart stop 等待已取消；未强制终止。", cancellationToken);
            }

            throw new PostgresLifecycleException($"PostgreSQL smart stop 未完成（{result.ErrorCode ?? "UNKNOWN"}）；未强制终止。 ");
        }

        var marker = _marker ?? LoadAndValidateMarker(Path.Combine(_options.DataDirectory, MarkerFileName));
        var stopped = new PostgresRuntimeRecord(
            1,
            _lease.InstanceId,
            marker.ClusterId,
            _options.DataDirectory,
            _options.EngineVersion,
            _options.Port,
            "Stopped",
            result.Snapshot.Identity,
            DateTimeOffset.UtcNow);
        WriteRuntime(stopped);
    }

    public async Task<OwnedProcessOperationResult> TryReconnectAsync(CancellationToken cancellationToken = default)
    {
        var marker = LoadAndValidateMarker(Path.Combine(_options.DataDirectory, MarkerFileName));
        var runtime = LoadRuntime();
        if (runtime.FormatVersion != 1 ||
            runtime.InstanceId != _lease.InstanceId ||
            runtime.ClusterId != marker.ClusterId ||
            runtime.EngineVersion != _options.EngineVersion ||
            runtime.Port != _options.Port ||
            !string.Equals(runtime.DataDirectory, _options.DataDirectory, StringComparison.OrdinalIgnoreCase) ||
            runtime.State is not ("Running" or "Starting"))
        {
            throw new PostgresLifecycleException("PostgreSQL 运行身份记录与当前实例配置不匹配，拒绝重连。");
        }

        ValidatePostmasterPid(runtime);
        OwnedProcessOperationResult result;
        var attached = _processManager.Snapshot;
        if (attached.Identity is null)
        {
            result = await _processManager.TryReconnectAsync(runtime.Identity, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (attached.Identity != runtime.Identity)
            {
                throw new PostgresLifecycleException("当前 PostgreSQL 组件已附加到不同进程身份，拒绝覆盖重连。");
            }

            result = await _processManager.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (result.Succeeded)
        {
            _marker = marker;
            if (await CheckTransportAsync(cancellationToken).ConfigureAwait(false) is PostgresTransportState.AcceptingConnections)
            {
                WriteRuntime(runtime with { State = "Running", ObservedAtUtc = DateTimeOffset.UtcNow });
            }
        }

        return result;
    }

    public async Task<PostgresTransportState> CheckTransportAsync(CancellationToken cancellationToken = default)
    {
        var result = await PostgresCommand.RunAsync(
            _installation.PgIsReadyPath,
            _installation.BinDirectory,
            new[]
            {
                "-h", "127.0.0.1",
                "-p", _options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-d", "postgres",
                "-U", WindowsPostgresOptions.BootstrapAdminRole,
                "-q",
                "-t", "1",
            },
            timeout: TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode switch
        {
            0 => PostgresTransportState.AcceptingConnections,
            1 => PostgresTransportState.RejectingConnections,
            2 => PostgresTransportState.NoResponse,
            _ => PostgresTransportState.Unknown,
        };
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

        await _initializationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _processManager.DisposeAsync().ConfigureAwait(false);
            _lifetimeCancellation.Dispose();
        }
        finally
        {
            _initializationGate.Release();
            _initializationGate.Dispose();
        }
    }

    private async Task RunInitDbAsync(
        PostgresInitializationProgress progress,
        CancellationToken cancellationToken)
    {
        var configDirectory = Path.Combine(_lease.InstanceRoot, "config");
        Directory.CreateDirectory(configDirectory);
        PostgresPathGuard.EnsureSafePath(_lease, configDirectory, allowMissingLeaf: false);
        var passwordPath = Path.Combine(configDirectory, $".initdb-password.{Guid.NewGuid():N}.tmp");
        Exception? operationError = null;
        try
        {
            SecurePasswordFile.WriteNew(passwordPath, _secrets.PostgresBootstrapAdminPassword);
            StartupDiagnostics.Current.Record(StartupStage.PostgresInitialize, StartupEventKind.Begin, LauncherDiagnosticComponent.Postgres);
            _initializationHelperMayExist = true;
            var result = await PostgresCommand.RunAsync(
                _installation.InitDbPath,
                _installation.BinDirectory,
                new[]
                {
                    "--pgdata", _options.DataDirectory,
                    "--encoding", "UTF8",
                    "--locale", "C",
                    "--auth-host", "scram-sha-256",
                    "--auth-local", "scram-sha-256",
                    "--username", WindowsPostgresOptions.BootstrapAdminRole,
                    "--pwfile", passwordPath,
                    "--no-instructions",
                },
                _options.InitializationTimeout,
                cancellationToken,
                helper =>
                {
                    _initializationHelper = new OwnedProcessIdentity(helper.ProcessId, helper.StartedAtUtc,
                        helper.ExecutablePath, _lease.InstanceRoot, _lease.InstanceId, Guid.NewGuid());
                    PostgresStateFile.WriteReplace(
                        GetInitializationProgressPath(),
                        progress with
                        {
                            State = "Running",
                            Helper = helper,
                            ObservedAtUtc = DateTimeOffset.UtcNow,
                        });
                }).ConfigureAwait(false);
            StartupDiagnostics.Current.Record(StartupStage.PostgresInitialize, StartupEventKind.ProcessExited,
                LauncherDiagnosticComponent.Postgres, exitCode: result.ExitCode,
                outputHint: StartupDiagnostics.ClassifyOutput(result.StandardError));
            if (result.ExitCode != 0)
            {
                throw new PostgresLifecycleException(
                    "initdb 未成功完成；初始化进度记录已保留，未写初始化标记。",
                    LauncherDiagnosticCode.PostgresInitInterrupted);
            }
        }
        catch (Exception exception)
        {
            operationError = exception;
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(passwordPath))
                {
                    File.Delete(passwordPath);
                }
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                if (operationError is not null)
                {
                    throw new PostgresLifecycleException(
                        "initdb 未完成，且临时口令文件清理失败；未写初始化标记。",
                        new AggregateException(operationError, cleanupException));
                }

                throw new PostgresLifecycleException("initdb 完成，但临时口令文件清理失败；未写初始化标记。", cleanupException);
            }
        }
    }

    private PostgresClusterMarker LoadAndValidateMarker(string markerPath)
    {
        PostgresPathGuard.EnsureSafePath(_lease, markerPath, allowMissingLeaf: false);
        var marker = PostgresStateFile.Read<PostgresClusterMarker>(markerPath, StateFileLimit);
        if (marker.FormatVersion != 1 ||
            marker.InstanceId != _lease.InstanceId ||
            marker.ClusterId == Guid.Empty ||
            !string.Equals(marker.EngineVersion, _options.EngineVersion, StringComparison.Ordinal))
        {
            throw new PostgresLifecycleException("PostgreSQL 初始化标记与当前实例不匹配。");
        }

        var pgVersionPath = Path.Combine(_options.DataDirectory, "PG_VERSION");
        if (!File.Exists(pgVersionPath) ||
            !string.Equals(File.ReadAllText(pgVersionPath, Encoding.UTF8).Trim(), "17", StringComparison.Ordinal))
        {
            throw new PostgresLifecycleException("PostgreSQL 数据目录缺少有效 PG_VERSION 17。");
        }

        return marker;
    }

    private void ValidatePostmasterPid(PostgresRuntimeRecord runtime)
    {
        var pidPath = Path.Combine(_options.DataDirectory, "postmaster.pid");
        PostgresPathGuard.EnsureSafePath(_lease, pidPath, allowMissingLeaf: false);
        string[] lines;
        try
        {
            lines = File.ReadAllLines(pidPath, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PostgresLifecycleException("无法读取 PostgreSQL postmaster.pid，拒绝重连。", exception);
        }

        if (lines.Length < 4 ||
            !int.TryParse(lines[0], out var pid) ||
            !long.TryParse(lines[2], out var startEpoch) ||
            !int.TryParse(lines[3], out var port) ||
            pid != runtime.Identity.ProcessId ||
            port != runtime.Port ||
            !string.Equals(Path.GetFullPath(lines[1]), runtime.DataDirectory, StringComparison.OrdinalIgnoreCase) ||
            Math.Abs(startEpoch - runtime.Identity.StartedAtUtc.ToUnixTimeSeconds()) > 5)
        {
            throw new PostgresLifecycleException("postmaster.pid 与持久运行身份不匹配，拒绝重连或接管。");
        }
    }

    private string GetRuntimePath() => Path.Combine(_lease.InstanceRoot, "config", "postgres-runtime.json");

    private string GetInitializationProgressPath() => Path.Combine(_lease.InstanceRoot, "config", "postgres-initialization.json");

    internal static bool IsValidInitializationProgress(
        int formatVersion,
        Guid instanceId,
        string? dataDirectory,
        string? engineVersion,
        string? state,
        PostgresHelperIdentity? helper,
        Guid expectedInstanceId,
        string expectedDataDirectory,
        string expectedEngineVersion,
        string expectedInitDbExecutable)
    {
        if (formatVersion != 1 || instanceId == Guid.Empty || instanceId != expectedInstanceId ||
            dataDirectory is null ||
            !string.Equals(dataDirectory, expectedDataDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(engineVersion, expectedEngineVersion, StringComparison.Ordinal) ||
            state is not ("Starting" or "Running"))
        {
            return false;
        }

        if (state is "Starting")
        {
            return helper is null;
        }

        if (helper is null || helper.ProcessId <= 0 || helper.StartedAtUtc == default ||
            string.IsNullOrWhiteSpace(helper.ExecutablePath) ||
            !Path.IsPathFullyQualified(helper.ExecutablePath))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(helper.ExecutablePath),
                expectedInitDbExecutable,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private PostgresRuntimeRecord LoadRuntime()
    {
        var runtimePath = GetRuntimePath();
        PostgresPathGuard.EnsureSafePath(_lease, runtimePath, allowMissingLeaf: false);
        return PostgresStateFile.Read<PostgresRuntimeRecord>(runtimePath, StateFileLimit);
    }

    private void WriteRuntime(PostgresRuntimeRecord runtime)
    {
        var runtimePath = GetRuntimePath();
        PostgresPathGuard.EnsureSafePath(_lease, Path.GetDirectoryName(runtimePath)!, allowMissingLeaf: true);
        PostgresStateFile.WriteReplace(runtimePath, runtime);
    }

    private static IdentityRunState GetIdentityRunState(OwnedProcessIdentity identity)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(identity.ProcessId);
            if (process.HasExited)
            {
                return IdentityRunState.NotRunning;
            }

            var observedStart = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var observedExe = process.MainModule?.FileName;
            if (observedStart.UtcTicks != identity.StartedAtUtc.UtcTicks)
            {
                return IdentityRunState.NotRunning;
            }

            if (string.IsNullOrWhiteSpace(observedExe))
            {
                return IdentityRunState.Unknown;
            }

            return string.Equals(Path.GetFullPath(observedExe), identity.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                ? IdentityRunState.Running
                : IdentityRunState.NotRunning;
        }
        catch (ArgumentException)
        {
            return IdentityRunState.NotRunning;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return IdentityRunState.Unknown;
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private static WindowsPostgresOptions NormalizeAndValidate(InstanceDataRootLease lease, WindowsPostgresOptions options)
    {
        lease.EnsureHeld();
        if (!Path.IsPathFullyQualified(options.InstallationRoot) || !Path.IsPathFullyQualified(options.DataDirectory))
        {
            throw new ArgumentException("PostgreSQL 安装根和 PGDATA 必须是绝对路径。", nameof(options));
        }

        if (options.Port is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PostgreSQL 端口必须在 1024 到 65535 之间。");
        }

        if (options.InitializationTimeout <= TimeSpan.Zero ||
            options.StartupTimeout <= TimeSpan.Zero ||
            options.StopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PostgreSQL 生命周期超时必须大于零。");
        }

        var normalizedDataDirectory = Path.GetFullPath(options.DataDirectory);
        if (!normalizedDataDirectory.StartsWith(lease.InstanceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("PGDATA 必须严格位于实例数据根内。", nameof(options));
        }

        return options with
        {
            InstallationRoot = Path.GetFullPath(options.InstallationRoot),
            DataDirectory = normalizedDataDirectory,
        };
    }

    private sealed record PostgresClusterMarker(
        int FormatVersion,
        Guid InstanceId,
        Guid ClusterId,
        string EngineVersion,
        DateTimeOffset CreatedAtUtc);

    private sealed record PostgresInitializationProgress(
        int FormatVersion,
        Guid InstanceId,
        string DataDirectory,
        string EngineVersion,
        string State,
        PostgresHelperIdentity? Helper,
        DateTimeOffset ObservedAtUtc);

    private enum IdentityRunState
    {
        Running,
        NotRunning,
        Unknown,
    }
}
