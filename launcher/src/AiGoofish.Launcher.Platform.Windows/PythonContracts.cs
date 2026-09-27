using AiGoofish.Launcher.Core;
using System.Security.Cryptography;
using System.Text;

namespace AiGoofish.Launcher.Platform.Windows;

public enum PythonServiceMode
{
    Maintenance,
    Normal,
}

public enum PythonReadinessState
{
    Ready,
    NotReady,
    Unreachable,
    Rejected,
    ContractMismatch,
}

public sealed record PythonReadinessResult(
    PythonReadinessState State,
    string? FailureReason,
    int? SchemaVersion,
    bool? SetupRequired = null);

public sealed record WindowsPythonOptions(
    string PythonExecutable,
    string BootstrapScript,
    string ProgramRoot,
    string DataRoot,
    string CacheRoot,
    string BrowserRoot,
    int Port,
    string AppVersion,
    PythonServiceMode Mode,
    int SupportedSchemaMinimum,
    int SupportedSchemaMaximum,
    TimeSpan StartupTimeout,
    TimeSpan RequestTimeout,
    TimeSpan StopTimeout);

public sealed record PythonDatabaseEndpoints(
    string ApplicationDsn,
    string ProbeDsn,
    string? SetupToken = null)
{
    public override string ToString() => "PythonDatabaseEndpoints([REDACTED])";
}

public sealed record PythonRuntimeRecord(
    int FormatVersion,
    Guid InstanceId,
    string Mode,
    string AppVersion,
    int Port,
    string ProgramRoot,
    string DataRoot,
    string State,
    OwnedProcessIdentity Identity,
    Guid CredentialId,
    bool ControlCredentialPersisted,
    DateTimeOffset ObservedAtUtc);

public sealed class PythonLifecycleException : InvalidOperationException, ILauncherSafeFailure
{
    public PythonLifecycleException(string message, LauncherDiagnosticCode? safeDiagnosticCode = null)
        : base(message)
    {
        SafeDiagnosticCode = safeDiagnosticCode;
    }

    public PythonLifecycleException(string message, Exception innerException, LauncherDiagnosticCode? safeDiagnosticCode = null)
        : base(message, innerException)
    {
        SafeDiagnosticCode = safeDiagnosticCode;
    }

    public LauncherDiagnosticCode? SafeDiagnosticCode { get; }
}

public sealed class WindowsPythonComponent : ILauncherComponent, IAsyncDisposable
{
    private const string RuntimeRelativePath = "config/python-runtime.json";
    private const int StateFileLimit = 64 * 1024;
    private readonly InstanceDataRootLease _lease;
    private readonly InstanceSecrets _secrets;
    private readonly WindowsPythonOptions _options;
    private readonly PythonDatabaseEndpoints _databaseEndpoints;
    private readonly PythonLoopbackControlClient _controlClient;
    private readonly PythonHttpStopProtocol _stopProtocol;
    private readonly WindowsOwnedProcessManager _processManager;
    private readonly WindowsPythonRunCredentialStore _credentialStore;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _lifecycleSync = new();
    private readonly object _readinessSync = new();
    private readonly object _launcherContextSync = new();
    private readonly HashSet<PortableLauncherUserClientContext> _launcherContexts = [];
    private long _launcherContextGeneration;
    private string? _controlToken;
    private string? _setupToken;
    private OwnedProcessIdentity? _currentIdentity;
    private PythonReadinessResult? _lastReadiness;
    private OwnedProcessIdentity? _lastReadinessIdentity;
    private bool _disposed;

    public WindowsPythonComponent(
        InstanceDataRootLease lease,
        InstanceSecrets secrets,
        WindowsPythonOptions options,
        PythonDatabaseEndpoints databaseEndpoints)
        : this(lease, secrets, options, databaseEndpoints, new WindowsPythonRunCredentialStore())
    {
    }

    internal WindowsPythonComponent(
        InstanceDataRootLease lease,
        InstanceSecrets secrets,
        WindowsPythonOptions options,
        PythonDatabaseEndpoints databaseEndpoints,
        WindowsPythonRunCredentialStore credentialStore)
    {
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _options = PythonConfiguration.NormalizeAndValidate(
            lease,
            options ?? throw new ArgumentNullException(nameof(options)));
        _databaseEndpoints = PythonConfiguration.ValidateDatabaseEndpoints(
            _options.Mode,
            databaseEndpoints ?? throw new ArgumentNullException(nameof(databaseEndpoints)));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _controlClient = new PythonLoopbackControlClient(_options.Port, _options.RequestTimeout);
        _stopProtocol = new PythonHttpStopProtocol(_controlClient);
        _processManager = new WindowsOwnedProcessManager(
            lease,
            _stopProtocol,
            logCapacity: 500,
            maxLogLineLength: 2048,
            diagnosticComponent: _options.Mode is PythonServiceMode.Normal
                ? LauncherDiagnosticComponent.PythonWeb : LauncherDiagnosticComponent.PythonMaintenance);
    }

    public string Id => _options.Mode is PythonServiceMode.Maintenance ? "python-maintenance" : "python-web";

    public string DisplayName => _options.Mode is PythonServiceMode.Maintenance ? "Python 维护服务" : "Python Web 服务";

    public OwnedProcessSnapshot ProcessSnapshot => _processManager.Snapshot;

    public IReadOnlyList<LauncherLogEntry> Logs => _processManager.Logs;

    public string RuntimeRecordPath => Path.Combine(_lease.InstanceRoot, RuntimeRelativePath);

    public string ControlCredentialPath => _credentialStore.GetCredentialPath(_lease);

    public PythonReadinessResult? LastReadiness => GetReadinessForCurrentIdentity();

    public string? SetupToken => GetReadinessForCurrentIdentity()?.SetupRequired is true ? _setupToken : null;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        if (!await _operationGate.WaitAsync(0, linkedCancellation.Token).ConfigureAwait(false))
        {
            throw new InvalidOperationException("已有 Python 生命周期操作正在进行，不能重复启动。");
        }

        try
        {
            InvalidateLauncherUserContexts();
            ClearRunReadiness();
            _lease.EnsureHeld();
            EnsureDurableRuntimeAllowsStart();
            var credentials = _credentialStore.CreatePending(_lease, _options, _databaseEndpoints.SetupToken);
            var controlToken = credentials.ControlToken;
            var launchSpec = PythonConfiguration.CreateLaunchSpec(
                _lease,
                _secrets,
                _options,
                _databaseEndpoints,
                controlToken);
            OwnedProcessOperationResult started;
            try
            {
                started = await _processManager.StartAsync(launchSpec, linkedCancellation.Token).ConfigureAwait(false);
            }
            catch
            {
                // Process.Start may have succeeded before identity capture or
                // stream setup threw. Keep the durable pending guard: an
                // exception alone is not evidence that no child exists.
                throw;
            }
            if (started.Snapshot.Identity is null)
            {
                _credentialStore.DeletePending(_lease, _options, credentials);
                throw new PythonLifecycleException($"Python 服务进程启动失败（{started.ErrorCode ?? "UNKNOWN"}）。");
            }

            _controlToken = controlToken;
            _setupToken = credentials.SetupToken;
            _currentIdentity = started.Snapshot.Identity;
            _stopProtocol.Configure(_currentIdentity, _lease.InstanceId, controlToken);
            _credentialStore.Bind(_lease, _options, credentials, _currentIdentity);
            WriteRuntime(CreateRuntime("Starting", _currentIdentity, credentials.CredentialId));
            if (started.Cancelled)
            {
                throw new OperationCanceledException("Python 启动等待已取消；进程归属与本次控制凭据仍保留。", cancellationToken);
            }

            if (!started.Succeeded)
            {
                WriteRuntime(CreateRuntime("Failed", _currentIdentity, credentials.CredentialId));
                throw new PythonLifecycleException($"Python 服务进程启动失败（{started.ErrorCode ?? "UNKNOWN"}）。");
            }

            var deadline = DateTimeOffset.UtcNow + _options.StartupTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                linkedCancellation.Token.ThrowIfCancellationRequested();
                var processState = await _processManager.RefreshAsync(linkedCancellation.Token).ConfigureAwait(false);
                if (processState.Snapshot.State is OwnedProcessState.Exited)
                {
                    WriteRuntime(CreateRuntime("Failed", _currentIdentity, credentials.CredentialId));
                    throw new PythonLifecycleException("Python 服务在就绪契约通过前退出。");
                }

                if (processState.Snapshot.State is not OwnedProcessState.Running)
                {
                    throw new PythonLifecycleException("Python 服务进程归属无法确认；保留运行身份且不接管其他监听者。");
                }

                var readiness = await _controlClient.CheckReadinessAsync(
                    _currentIdentity,
                    _lease.InstanceId,
                    controlToken,
                    _options.AppVersion,
                    PythonConfiguration.GetWireMode(_options.Mode),
                    _options.SupportedSchemaMinimum,
                    _options.SupportedSchemaMaximum,
                    linkedCancellation.Token).ConfigureAwait(false);
                SetRunReadinessIfCurrent(_currentIdentity, controlToken, readiness);
                if (readiness.State is PythonReadinessState.Ready)
                {
                    var verified = await _processManager.RefreshAsync(linkedCancellation.Token).ConfigureAwait(false);
                    if (!verified.Succeeded ||
                        verified.Snapshot.State is not OwnedProcessState.Running ||
                        verified.Snapshot.Identity != _currentIdentity)
                    {
                        throw new PythonLifecycleException("HTTP 就绪契约通过，但 Python 进程归属复核失败。");
                    }

                    WriteRuntime(CreateRuntime("Running", _currentIdentity, credentials.CredentialId));
                    if (readiness.SetupRequired is not true)
                    {
                        _setupToken = null;
                    }
                    return;
                }

                if (readiness.State is PythonReadinessState.Rejected or PythonReadinessState.ContractMismatch)
                {
                    throw new PythonLifecycleException("HTTP 就绪响应与本次 Launcher 控制身份或固定契约不匹配，拒绝标记运行。");
                }

                await Task.Delay(100, linkedCancellation.Token).ConfigureAwait(false);
            }

            throw new PythonLifecycleException("Python 进程仍受管，但未在时限内达到数据库与 schema 就绪状态。");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<ComponentRuntimeState> GetStateAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _processManager.Snapshot;
            if (current.Identity is null)
            {
                if (!File.Exists(RuntimeRecordPath))
                {
                    return ComponentRuntimeState.Stopped;
                }

                try
                {
                    // State inspection must remain able to retire a stopped v1
                    // record. A live v1 record is still reported as Unknown and
                    // can neither be reconnected nor replaced without credentials.
                    var runtime = LoadRuntimeForStart();
                    var runState = PythonProcessIdentityProbe.GetRunState(runtime.Identity);
                    if (runState is PythonIdentityRunState.NotRunning)
                    {
                        if (runtime.State != "Stopped")
                        {
                            WriteRuntime(runtime with { State = "Stopped", ObservedAtUtc = DateTimeOffset.UtcNow });
                        }

                        return ComponentRuntimeState.Stopped;
                    }
                }
                catch (PythonLifecycleException)
                {
                }

                return ComponentRuntimeState.Unknown;
            }

            var refreshed = await _processManager.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return refreshed.Snapshot.State switch
            {
                OwnedProcessState.Running when IsReadyCurrentRun(refreshed.Snapshot.Identity) => ComponentRuntimeState.Running,
                OwnedProcessState.Running when GetReadinessForIdentity(current.Identity)?.State is PythonReadinessState.NotReady or PythonReadinessState.Unreachable => ComponentRuntimeState.Starting,
                OwnedProcessState.Running => ComponentRuntimeState.Unknown,
                OwnedProcessState.Starting => ComponentRuntimeState.Starting,
                OwnedProcessState.StopRequested => ComponentRuntimeState.Stopping,
                OwnedProcessState.Exited => ComponentRuntimeState.Stopped,
                OwnedProcessState.StartFailed => ComponentRuntimeState.Failed,
                OwnedProcessState.StopTimedOut or OwnedProcessState.Unknown => ComponentRuntimeState.Unknown,
                _ => ComponentRuntimeState.Unknown,
            };
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<OwnedProcessOperationResult> TryReconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("已有 Python 生命周期操作正在进行，不能并发重连。");
        }

        try
        {
            InvalidateLauncherUserContexts();
            ClearRunReadiness();
            _lease.EnsureHeld();
            if (_currentIdentity is not null || _controlToken is not null)
            {
                throw new InvalidOperationException("当前 Python 组件已持有进程归属，不能覆盖重连。");
            }

            var runtime = LoadAndValidateRuntime();
            if (runtime.State is not ("Running" or "Starting"))
            {
                return ReconnectFailure(runtime.Identity, "PYTHON_RUNTIME_NOT_ACTIVE");
            }

            var credentials = _credentialStore.LoadBound(_lease, _options, runtime);
            OwnedProcessOperationResult reconnected;
            var attached = _processManager.Snapshot;
            if (attached.Identity is null)
            {
                reconnected = await _processManager.TryReconnectAsync(runtime.Identity, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (!IdentityEquals(attached.Identity, runtime.Identity))
                {
                    throw new InvalidOperationException("当前 Python 组件已附加到不同进程身份，拒绝覆盖重连。");
                }

                // A previous reconnect can safely retain ownership while HTTP is
                // still warming up. Recheck the exact identity instead of trying
                // to attach again or spawning a second process.
                reconnected = await _processManager.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!reconnected.Succeeded || reconnected.Snapshot.State is not OwnedProcessState.Running)
            {
                return reconnected;
            }

            var readiness = await _controlClient.CheckReadinessAsync(
                runtime.Identity,
                _lease.InstanceId,
                credentials.ControlToken,
                _options.AppVersion,
                PythonConfiguration.GetWireMode(_options.Mode),
                _options.SupportedSchemaMinimum,
                _options.SupportedSchemaMaximum,
                cancellationToken).ConfigureAwait(false);
            if (readiness.State is not PythonReadinessState.Ready)
            {
                WriteRuntime(runtime with { State = "Starting", ObservedAtUtc = DateTimeOffset.UtcNow });
                return reconnected with
                {
                    Succeeded = false,
                    ErrorCode = readiness.State switch
                    {
                        PythonReadinessState.Rejected => "PYTHON_CONTROL_REJECTED",
                        PythonReadinessState.ContractMismatch => "PYTHON_CONTROL_CONTRACT_MISMATCH",
                        PythonReadinessState.Unreachable => "PYTHON_CONTROL_UNREACHABLE",
                        _ => "PYTHON_NOT_READY",
                    },
                };
            }

            var verified = await _processManager.RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (!verified.Succeeded || verified.Snapshot.State is not OwnedProcessState.Running ||
                !IdentityEquals(verified.Snapshot.Identity, runtime.Identity))
            {
                return verified with { Succeeded = false, ErrorCode = "PYTHON_OWNERSHIP_RECHECK_FAILED" };
            }

            _controlToken = credentials.ControlToken;
            _setupToken = readiness.SetupRequired is true ? credentials.SetupToken : null;
            _currentIdentity = runtime.Identity;
            SetRunReadinessIfCurrent(_currentIdentity, credentials.ControlToken, readiness);
            _stopProtocol.Configure(runtime.Identity, _lease.InstanceId, credentials.ControlToken);
            WriteRuntime(runtime with { State = "Running", ObservedAtUtc = DateTimeOffset.UtcNow });
            return verified;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<PythonReadinessResult> RefreshReadinessAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var identity = _currentIdentity;
        var controlToken = _controlToken;
        if (identity is null || string.IsNullOrEmpty(controlToken))
        {
            throw new PythonLifecycleException("当前 Python 服务没有可用的本次运行控制上下文。");
        }

        var refreshed = await _processManager.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (!refreshed.Succeeded ||
            refreshed.Snapshot.State is not OwnedProcessState.Running ||
            refreshed.Snapshot.Identity != identity)
        {
            throw new PythonLifecycleException("刷新就绪状态前无法确认 Python 进程归属。");
        }

        var readiness = await _controlClient.CheckReadinessAsync(
            identity,
            _lease.InstanceId,
            controlToken,
            _options.AppVersion,
            PythonConfiguration.GetWireMode(_options.Mode),
            _options.SupportedSchemaMinimum,
            _options.SupportedSchemaMaximum,
            cancellationToken).ConfigureAwait(false);
        var rechecked = await _processManager.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (!rechecked.Succeeded || rechecked.Snapshot.State is not OwnedProcessState.Running ||
            !IdentityEquals(rechecked.Snapshot.Identity, identity) ||
            !SetRunReadinessIfCurrent(identity, controlToken, readiness))
        {
            throw new PythonLifecycleException("刷新就绪状态时 Python 运行身份已变化，结果未缓存。");
        }

        return readiness;
    }

    public async Task<PortableLauncherUserClientContext> CreateLauncherUserClientContextAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _lease.EnsureHeld();
            var identity = _currentIdentity;
            var controlToken = _controlToken;
            var currentReadiness = GetReadinessSnapshotForIdentity(identity);
            LauncherUserContextGuard.EnsureReady(currentReadiness.Readiness, currentReadiness.Identity, identity, controlToken);

            var refreshed = await _processManager.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var runtime = LoadAndValidateRuntime();
            LauncherUserContextGuard.EnsureOwned(
                _lease.InstanceId,
                identity!,
                refreshed,
                _processManager.Snapshot.Identity,
                runtime);

            var credentials = _credentialStore.LoadBound(_lease, _options, runtime);
            LauncherUserContextGuard.EnsureControlCredentialMatches(credentials.ControlToken, controlToken!);

            lock (_launcherContextSync)
            {
                if (!IsStillCurrentContext(identity!, controlToken!))
                {
                    throw new PythonLifecycleException("创建 Launcher 用户上下文时本实例运行身份已变化。");
                }

                var generation = _launcherContextGeneration;
                if (generation <= 0)
                {
                    throw new PythonLifecycleException("本实例尚未完成一次有效的服务启动，不能建立 Launcher 用户上下文。");
                }

                var context = new PortableLauncherUserClientContext(
                    _lease.InstanceId,
                    _options.Port,
                    generation,
                    controlToken!,
                    IsLauncherContextGenerationCurrent,
                    RemoveLauncherUserContext);
                _launcherContexts.Add(context);
                return context;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("已有 Python 生命周期操作正在进行，不能并发停止。");
        }

        try
        {
            InvalidateLauncherUserContexts();
            ClearRunReadiness();
            var identity = _currentIdentity;
            var controlToken = _controlToken;
            if (identity is null || string.IsNullOrEmpty(controlToken))
            {
                throw new PythonLifecycleException("本次组件没有可用的运行期控制凭据；B6 不支持崩溃后重连停止。");
            }

            var runtime = LoadAndValidateRuntime();
            if (runtime.Identity != identity || _processManager.Snapshot.Identity != identity)
            {
                throw new PythonLifecycleException("停止前持久身份与当前受管 Python 进程不匹配，拒绝发送关闭请求。");
            }

            _stopProtocol.Configure(identity, _lease.InstanceId, controlToken);
            WriteRuntime(runtime with { State = "Stopping", ObservedAtUtc = DateTimeOffset.UtcNow });
            var result = await _processManager.StopAsync(_options.StopTimeout, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded || result.Snapshot.State is not OwnedProcessState.Exited)
            {
                var state = result.Snapshot.State is OwnedProcessState.Running ? "Running" : "Unknown";
                WriteRuntime(runtime with { State = state, ObservedAtUtc = DateTimeOffset.UtcNow });
                if (result.Cancelled)
                {
                    throw new OperationCanceledException("Python 正常关闭等待已取消；未强制终止。", cancellationToken);
                }

                throw new PythonLifecycleException($"Python 正常关闭未完成（{result.ErrorCode ?? "UNKNOWN"}）；HTTP 202 未被当作进程退出。");
            }

            WriteRuntime(runtime with { State = "Stopped", ObservedAtUtc = DateTimeOffset.UtcNow });
            try
            {
                _credentialStore.DeleteBound(_lease, runtime);
            }
            finally
            {
                _stopProtocol.Clear();
                _controlToken = null;
                _setupToken = null;
                _currentIdentity = null;
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
            InvalidateLauncherUserContexts();
            ClearRunReadiness();
        }

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _processManager.DisposeAsync().ConfigureAwait(false);
            _stopProtocol.Clear();
            _controlToken = null;
            _setupToken = null;
            _currentIdentity = null;
            _controlClient.Dispose();
            _lifetimeCancellation.Dispose();
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
        }
    }

    private void EnsureDurableRuntimeAllowsStart()
    {
        if (!File.Exists(RuntimeRecordPath))
        {
            if (File.Exists(ControlCredentialPath))
            {
                throw new PythonLifecycleException(
                    "存在未配对 runtime 的 Python 控制凭据；可能处于 spawn 后身份尚未持久化的故障窗口，拒绝覆盖。");
            }

            return;
        }

        var runtime = LoadRuntimeForStart();
        var runState = PythonProcessIdentityProbe.GetRunState(runtime.Identity);
        if (runState is PythonIdentityRunState.NotRunning)
        {
            if (runtime.FormatVersion == 2 && runtime.ControlCredentialPersisted && File.Exists(ControlCredentialPath))
            {
                _credentialStore.DeleteBound(_lease, runtime);
            }
            else if (runtime.FormatVersion == 1 && File.Exists(ControlCredentialPath))
            {
                throw new PythonLifecycleException("旧版 Python runtime 旁存在无法归属的控制凭据，拒绝覆盖。");
            }

            if (runtime.State != "Stopped")
            {
                WriteRuntime(runtime with { State = "Stopped", ObservedAtUtc = DateTimeOffset.UtcNow });
            }

            return;
        }

        throw new PythonLifecycleException(
            runtime.FormatVersion == 1 || !runtime.ControlCredentialPersisted
                ? "旧版持久 Python 身份仍在运行或无法安全确认且没有可恢复凭据；拒绝重启、补造凭据或接管。"
                : "持久 Python 身份仍在运行或无法安全确认；请显式重连，拒绝启动新进程或覆盖控制凭据。");
    }

    private PythonRuntimeRecord LoadAndValidateRuntime(bool allowPreviousConfiguration = false)
    {
        PythonPathGuard.EnsureSafeInstancePath(_lease, RuntimeRecordPath, allowMissingLeaf: false);
        var runtime = PythonStateFile.Read<PythonRuntimeRecord>(RuntimeRecordPath, StateFileLimit);
        if (runtime.FormatVersion != 2 ||
            runtime.InstanceId != _lease.InstanceId ||
            runtime.Identity is null ||
            runtime.Port is < 1024 or > 65535 ||
            runtime.Mode is not ("maintenance" or "normal") ||
            (!allowPreviousConfiguration && runtime.Port != _options.Port) ||
            runtime.CredentialId == Guid.Empty ||
            !runtime.ControlCredentialPersisted ||
            (!allowPreviousConfiguration && !string.Equals(runtime.Mode, PythonConfiguration.GetWireMode(_options.Mode), StringComparison.Ordinal)) ||
            (!allowPreviousConfiguration && !string.Equals(runtime.AppVersion, _options.AppVersion, StringComparison.Ordinal)) ||
            (!allowPreviousConfiguration && !string.Equals(runtime.ProgramRoot, _options.ProgramRoot, StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(runtime.DataRoot, _options.DataRoot, StringComparison.OrdinalIgnoreCase) ||
            runtime.Identity.InstanceId != _lease.InstanceId ||
            !string.Equals(runtime.Identity.InstanceRoot, _lease.InstanceRoot, StringComparison.OrdinalIgnoreCase) ||
            (!allowPreviousConfiguration && !string.Equals(runtime.Identity.ExecutablePath, _options.PythonExecutable, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PythonLifecycleException("Python 运行身份记录与当前实例配置不匹配。");
        }

        return runtime;
    }

    private PythonRuntimeRecord LoadRuntimeForStart()
    {
        PythonPathGuard.EnsureSafeInstancePath(_lease, RuntimeRecordPath, allowMissingLeaf: false);
        var runtime = PythonStateFile.Read<PythonRuntimeRecord>(RuntimeRecordPath, StateFileLimit);
        var versionValid = runtime.FormatVersion switch
        {
            1 => !runtime.ControlCredentialPersisted && runtime.CredentialId == Guid.Empty,
            2 => runtime.ControlCredentialPersisted && runtime.CredentialId != Guid.Empty,
            _ => false,
        };
        if (!versionValid || runtime.InstanceId != _lease.InstanceId || runtime.Identity is null ||
            runtime.Port is < 1024 or > 65535 || runtime.Mode is not ("maintenance" or "normal") ||
            runtime.Identity.InstanceId != _lease.InstanceId ||
            !string.Equals(runtime.DataRoot, _options.DataRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(runtime.Identity.InstanceRoot, _lease.InstanceRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new PythonLifecycleException("Python 持久运行身份记录无效，拒绝据此启动或清理凭据。");
        }

        return runtime;
    }

    private PythonRuntimeRecord CreateRuntime(string state, OwnedProcessIdentity identity, Guid credentialId)
    {
        return new PythonRuntimeRecord(
            2,
            _lease.InstanceId,
            PythonConfiguration.GetWireMode(_options.Mode),
            _options.AppVersion,
            _options.Port,
            _options.ProgramRoot,
            _options.DataRoot,
            state,
            identity,
            credentialId,
            ControlCredentialPersisted: true,
            DateTimeOffset.UtcNow);
    }

    private void WriteRuntime(PythonRuntimeRecord runtime)
    {
        PythonPathGuard.EnsureSafeInstancePath(_lease, Path.GetDirectoryName(RuntimeRecordPath)!, allowMissingLeaf: true);
        PythonStateFile.WriteReplace(RuntimeRecordPath, runtime);
    }

    private static OwnedProcessOperationResult ReconnectFailure(OwnedProcessIdentity identity, string errorCode)
    {
        return new OwnedProcessOperationResult(
            false,
            false,
            errorCode,
            new OwnedProcessSnapshot(OwnedProcessState.Unknown, identity, null, DateTimeOffset.UtcNow, "Python runtime 不允许重连。"));
    }

    private static bool IdentityEquals(OwnedProcessIdentity? left, OwnedProcessIdentity right)
    {
        return left is not null && left.ProcessId == right.ProcessId &&
            left.StartedAtUtc.UtcTicks == right.StartedAtUtc.UtcTicks &&
            left.InstanceId == right.InstanceId && left.RunId == right.RunId &&
            string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.InstanceRoot, right.InstanceRoot, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsReadyCurrentRun(OwnedProcessIdentity? identity)
    {
        return _currentIdentity is not null &&
            !string.IsNullOrEmpty(_controlToken) &&
            GetReadinessForIdentity(_currentIdentity)?.State is PythonReadinessState.Ready &&
            IdentityEquals(identity, _currentIdentity);
    }

    private PythonReadinessResult? GetReadinessForCurrentIdentity() => GetReadinessForIdentity(_currentIdentity);

    private PythonReadinessResult? GetReadinessForIdentity(OwnedProcessIdentity? identity)
        => GetReadinessSnapshotForIdentity(identity).Readiness;

    private (PythonReadinessResult? Readiness, OwnedProcessIdentity? Identity) GetReadinessSnapshotForIdentity(
        OwnedProcessIdentity? identity)
    {
        lock (_readinessSync)
        {
            var current = identity is not null && IdentityEquals(_lastReadinessIdentity, identity);
            return current ? (_lastReadiness, _lastReadinessIdentity) : (null, null);
        }
    }

    private bool SetRunReadinessIfCurrent(
        OwnedProcessIdentity? identity,
        string? controlToken,
        PythonReadinessResult readiness)
    {
        if (identity is null || string.IsNullOrEmpty(controlToken))
        {
            return false;
        }

        lock (_readinessSync)
        {
            if (_disposed || !IdentityEquals(_currentIdentity, identity) ||
                !FixedTimeTokenEquals(_controlToken ?? string.Empty, controlToken) ||
                _processManager.Snapshot.State is not OwnedProcessState.Running ||
                !IdentityEquals(_processManager.Snapshot.Identity, identity))
            {
                return false;
            }

            _lastReadiness = readiness;
            _lastReadinessIdentity = identity;
            return true;
        }
    }

    private void ClearRunReadiness()
    {
        lock (_readinessSync)
        {
            _lastReadiness = null;
            _lastReadinessIdentity = null;
        }
    }

    private bool IsStillCurrentContext(OwnedProcessIdentity identity, string controlToken)
    {
        var snapshot = _processManager.Snapshot;
        return !_disposed && _controlToken is not null && FixedTimeTokenEquals(_controlToken, controlToken) &&
            IdentityEquals(_currentIdentity, identity) && snapshot.State is OwnedProcessState.Running &&
            IdentityEquals(snapshot.Identity, identity);
    }

    private bool IsLauncherContextGenerationCurrent(long generation)
    {
        lock (_launcherContextSync)
        {
            if (generation != _launcherContextGeneration || string.IsNullOrEmpty(_controlToken) ||
                _disposed || _currentIdentity is null)
            {
                return false;
            }

            var snapshot = _processManager.Snapshot;
            return snapshot.State is OwnedProcessState.Running && IdentityEquals(snapshot.Identity, _currentIdentity);
        }
    }

    private void InvalidateLauncherUserContexts()
    {
        PortableLauncherUserClientContext[] contexts;
        lock (_launcherContextSync)
        {
            InvalidateLauncherUserContextsCore();
            contexts = _launcherContexts.ToArray();
            _launcherContexts.Clear();
        }

        foreach (var context in contexts)
        {
            context.Invalidate();
        }
    }

    private void InvalidateLauncherUserContextsCore()
    {
        _launcherContextGeneration = _launcherContextGeneration == long.MaxValue ? 1 : _launcherContextGeneration + 1;
    }

    private void RemoveLauncherUserContext(PortableLauncherUserClientContext context)
    {
        lock (_launcherContextSync)
        {
            _launcherContexts.Remove(context);
        }
    }

    private static bool FixedTimeTokenEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        try
        {
            return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
