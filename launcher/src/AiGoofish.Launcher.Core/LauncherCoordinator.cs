namespace AiGoofish.Launcher.Core;

public sealed partial class LauncherCoordinator : IDisposable
{
    private readonly List<LauncherComponentRegistration> _registrations;
    private readonly Dictionary<string, ComponentSnapshot> _components;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly BoundedLogBuffer _logs;
    private readonly BoundedDiagnosticEventBuffer _diagnosticEvents = new();
    private readonly object _snapshotSync = new();
    private readonly object _cancellationSync = new();
    private CancellationTokenSource? _activeStartCancellation;
    private LauncherSnapshot _snapshot;
    private bool _shutdownRequested;
    private bool _disposed;

    public LauncherCoordinator(
        IEnumerable<LauncherComponentRegistration> registrations,
        bool isSimulation,
        int logCapacity = 500,
        IEnumerable<LauncherStartupStepRegistration>? startupSteps = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        _registrations = registrations.ToList();
        if (_registrations.Count == 0)
        {
            throw new ArgumentException("至少需要注册一个可管理组件。", nameof(registrations));
        }

        if (_registrations.Any(item => string.IsNullOrWhiteSpace(item.Component.Id)))
        {
            throw new ArgumentException("组件 ID 不能为空。", nameof(registrations));
        }

        if (_registrations.Select(item => item.Component.Id).Distinct(StringComparer.Ordinal).Count() != _registrations.Count)
        {
            throw new ArgumentException("组件 ID 必须唯一。", nameof(registrations));
        }

        _startupSteps = startupSteps?.ToArray() ?? [];
        if (_startupSteps.Any(item => item.Step is null || string.IsNullOrWhiteSpace(item.Step.Id) ||
                !_registrations.Any(service => service.Component.Id == item.BeforeComponentId)) ||
            _startupSteps.Select(item => item.Step.Id).Distinct(StringComparer.Ordinal).Count() != _startupSteps.Length ||
            _startupSteps.Any(item => _registrations.Any(service => service.Component.Id == item.Step.Id)))
            throw new ArgumentException("启动步骤必须有唯一 ID 和已注册的后续服务。", nameof(startupSteps));
        _steps = _startupSteps.ToDictionary(item => item.Step.Id,
            item => new StartupStepSnapshot(item.Step.Id, item.Step.DisplayName, StartupStepState.NotRun, false),
            StringComparer.Ordinal);

        _components = _registrations.ToDictionary(
            item => item.Component.Id,
            item => new ComponentSnapshot(item.Component.Id, item.Component.DisplayName, ComponentRuntimeState.Unknown),
            StringComparer.Ordinal);
        _logs = new BoundedLogBuffer(logCapacity);
        _snapshot = CreateSnapshot(
            runId: null,
            state: LauncherState.NotStarted,
            phase: "idle",
            message: isSimulation ? "模拟演练尚未开始。" : "尚未启动。",
            safeToCancel: false,
            isSimulation);
    }

    public event EventHandler<LauncherSnapshot>? SnapshotChanged;

    public event EventHandler<LauncherLogEntry>? LogAdded;

    public LauncherSnapshot Snapshot
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

    public IReadOnlyList<LauncherDiagnosticEvent> DiagnosticEvents => _diagnosticEvents.Snapshot();

    public async Task<LauncherSnapshot> RefreshObservationAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Snapshot;
        }

        try
        {
            await RefreshComponentStatesAsync(cancellationToken).ConfigureAwait(false);
            var current = Snapshot;
            if (current.State is LauncherState.Running &&
                (_components.Values.Any(component => component.State is not ComponentRuntimeState.Running) ||
                 _steps.Values.Any(step => !step.IsQuiescent)))
            {
                var message = "运行期间组件状态不再满足就绪条件；未尝试自动重启或结束任何进程。";
                WriteLog(LauncherLogLevel.Error, "launcher", message);
                Transition(current.RunId, LauncherState.Failed, "observation:component-failed", message, false);
                return Snapshot;
            }

            var refreshed = current with
            {
                ObservedAt = DateTimeOffset.UtcNow,
                Components = _registrations.Select(item => _components[item.Component.Id]).ToArray(),
                StartupSteps = _startupSteps.Select(item => _steps[item.Step.Id]).ToArray(),
            };
            lock (_snapshotSync)
            {
                _snapshot = refreshed;
            }

            PublishSnapshot(refreshed);
            return refreshed;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public bool RequestStartupCancellation()
    {
        lock (_cancellationSync)
        {
            ThrowIfDisposed();
            var cancellation = _activeStartCancellation;
            if (cancellation is null || cancellation.IsCancellationRequested)
            {
                return false;
            }

            WriteLog(LauncherLogLevel.Warning, "launcher", "已请求取消；若当前阶段不可安全取消，将等待下一个安全点。");
            cancellation.Cancel();
            return true;
        }
    }

    public async Task<LauncherOperationResult> StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("已有启动或停止操作正在进行，不能重复启动。");
        }

        try { return await StartCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _operationGate.Release(); }
    }

    private async Task<LauncherOperationResult> StartCoreAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? linkedCancellation = null;
        try
        {
            await RefreshComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);
            EnsureStartAllowed();

            var runId = Guid.NewGuid();
            lock (_cancellationSync)
            {
                ThrowIfDisposed();
                if (_shutdownRequested)
                {
                    throw new InvalidOperationException("Launcher 正在关闭，不能启动新操作。");
                }

                linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _activeStartCancellation = linkedCancellation;
            }
            Transition(runId, LauncherState.Starting, "preflight", "正在准备启动组件。", true);
            WriteLog(LauncherLogLevel.Information, "launcher", $"启动序列开始，运行标识 {runId}。");

            for (var index = 0; index < _registrations.Count; index++)
            {
                var registration = _registrations[index];
                if (linkedCancellation.IsCancellationRequested)
                {
                    return await CancelAndRollbackAsync(runId).ConfigureAwait(false);
                }

                var component = registration.Component;
                var activeId = component.Id;
                var activeName = component.DisplayName;
                try
                {
                    foreach (var preparation in _startupSteps.Where(item => item.BeforeComponentId == component.Id))
                    {
                        var step = preparation.Step;
                        activeId = step.Id;
                        activeName = step.DisplayName;
                        _steps[step.Id] = _steps[step.Id] with { State = StartupStepState.Executing, IsQuiescent = false };
                        Transition(runId, LauncherState.Starting, $"prepare:{step.Id}",
                            $"正在执行{step.DisplayName}。", preparation.CancellationIsSafe);
                        WriteLog(LauncherLogLevel.Information, step.Id, $"{step.DisplayName}开始。");
                        await step.ExecuteAsync(preparation.CancellationIsSafe ? linkedCancellation.Token : CancellationToken.None)
                            .ConfigureAwait(false);
                        if (!await step.IsQuiescentAsync(CancellationToken.None).ConfigureAwait(false))
                            throw new InvalidOperationException("启动步骤返回后辅助进程尚未确认退出。");
                        _steps[step.Id] = _steps[step.Id] with { State = StartupStepState.Completed, IsQuiescent = true };
                        Transition(runId, LauncherState.Starting, $"prepare:{step.Id}", $"{step.DisplayName}已完成。", true);
                        WriteLog(LauncherLogLevel.Information, step.Id, $"{step.DisplayName}已完成。");
                        if (linkedCancellation.IsCancellationRequested)
                            return await CancelAndRollbackAsync(runId).ConfigureAwait(false);
                    }
                    activeId = component.Id;
                    activeName = component.DisplayName;
                    Transition(runId, LauncherState.Starting, $"start:{component.Id}",
                        $"正在启动{component.DisplayName}（{index + 1}/{_registrations.Count}）。",
                        registration.StartupCancellationIsSafe);
                    WriteLog(LauncherLogLevel.Information, component.Id, $"开始启动{component.DisplayName}。");
                    var componentToken = registration.StartupCancellationIsSafe
                        ? linkedCancellation.Token
                        : CancellationToken.None;
                    await component.StartAsync(componentToken).ConfigureAwait(false);
                    await RefreshComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);
                    if (_components[component.Id].State is not ComponentRuntimeState.Running)
                    {
                        throw new InvalidOperationException(
                            $"启动步骤返回后，{component.DisplayName}实际状态为 {_components[component.Id].State}，未达到运行状态。");
                    }

                    WriteLog(LauncherLogLevel.Information, component.Id, $"{component.DisplayName}启动步骤完成。");
                }
                catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
                {
                    if (_steps.TryGetValue(activeId, out var failedStep))
                        _steps[activeId] = failedStep with { State = StartupStepState.Failed };
                    await RefreshComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);
                    return await CancelAndRollbackAsync(runId).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    if (_steps.TryGetValue(activeId, out var failedStep))
                        _steps[activeId] = failedStep with { State = StartupStepState.Failed };
                    StartupDiagnostics.Current.Record(StartupStage.Start, StartupEventKind.Failed,
                        StartupDiagnostics.Component(activeId), exception, runId);
                    var safeFailure = exception as ILauncherSafeFailure;
                    var diagnosticCode = safeFailure?.SafeDiagnosticCode ?? LauncherDiagnosticCode.StartupComponentFailed;
                    var safeMessage = safeFailure?.SafeDiagnosticCode is { } safeCode
                        ? LauncherSafeFailureMessages.For(safeCode)
                        : $"{activeName}启动失败；请查看诊断摘要。";
                    RecordDiagnosticEvent(diagnosticCode, activeId, LauncherDiagnosticSeverity.Error);
                    WriteLog(LauncherLogLevel.Error, activeId, safeMessage);
                    await RefreshComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);
                    var rollback = await RollbackRunningComponentsAsync(runId, "startup-failed").ConfigureAwait(false);
                    var message = rollback is null
                        ? $"{safeMessage} 已停止可安全回收的组件。"
                        : $"{safeMessage} 可安全回收的组件停止未确认；请保留数据并查看诊断摘要。";
                    Transition(runId, LauncherState.Failed, "failed", message, false);
                    return Result(false, false, "START_FAILED", message);
                }

                if (linkedCancellation.IsCancellationRequested)
                {
                    return await CancelAndRollbackAsync(runId).ConfigureAwait(false);
                }
            }

            await RefreshComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);
            if (_steps.Values.Any(step => !step.IsQuiescent || step.State is not StartupStepState.Completed))
            {
                await RollbackRunningComponentsAsync(runId, "preparation-unconfirmed").ConfigureAwait(false);
                Transition(runId, LauncherState.Failed, "preparation-unconfirmed", "启动步骤未全部完成或辅助进程未确认退出。", false);
                return Result(false, false, "START_PREPARATION_UNCONFIRMED", "启动准备未通过最终核验。");
            }
            var notReady = _components.Values.FirstOrDefault(component => component.State is not ComponentRuntimeState.Running);
            if (notReady is not null)
            {
                var finalCheckMessage = $"最终就绪检查失败：{notReady.DisplayName}实际状态为 {notReady.State}。";
                RecordDiagnosticEvent(LauncherDiagnosticCode.ComponentNotReady, notReady.Id, LauncherDiagnosticSeverity.Error);
                WriteLog(LauncherLogLevel.Error, notReady.Id, finalCheckMessage);
                var rollback = await RollbackRunningComponentsAsync(runId, "final-check-failed").ConfigureAwait(false);
                if (rollback is not null)
                {
                    finalCheckMessage = $"{finalCheckMessage} 回收未完成：{rollback}";
                }

                Transition(runId, LauncherState.Failed, "failed", finalCheckMessage, false);
                return Result(false, false, "START_NOT_READY", finalCheckMessage);
            }

            Transition(runId, LauncherState.Running, "ready", "所有组件已启动。", false);
            WriteLog(LauncherLogLevel.Information, "launcher", "启动序列完成。");
            return Result(true, false, null, "启动成功。");
        }
        finally
        {
            lock (_cancellationSync)
            {
                if (ReferenceEquals(_activeStartCancellation, linkedCancellation))
                {
                    _activeStartCancellation = null;
                }

                linkedCancellation?.Dispose();
            }
        }
    }

    public async Task<LauncherOperationResult> AdoptAlreadyRunningAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("已有启动、恢复或停止操作正在进行，不能重复接管。");
        }

        try
        {
            lock (_cancellationSync)
            {
                ThrowIfDisposed();
                if (_shutdownRequested)
                {
                    throw new InvalidOperationException("Launcher 正在关闭，不能接管运行中的组件。");
                }
            }

            var current = Snapshot;
            if (current.State is not (LauncherState.NotStarted or LauncherState.Failed))
            {
                throw new InvalidOperationException($"当前状态 {current.State} 不允许接管已有运行组件。");
            }

            var runId = Guid.NewGuid();
            Transition(runId, LauncherState.Recovering, "recover:verify", "正在核验已有组件的真实运行状态。", false);
            WriteLog(LauncherLogLevel.Information, "launcher", $"已有实例恢复核验开始，运行标识 {runId}。");
            await RefreshComponentStatesAsync(cancellationToken).ConfigureAwait(false);
            try { await VerifyStartupStepsForAdoptionAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception exception)
            {
                StartupDiagnostics.Current.Record(StartupStage.InstanceRecovery, StartupEventKind.Failed,
                    exception: exception, operationId: runId);
                await RefreshStartupStepsAsync(CancellationToken.None).ConfigureAwait(false);
                Transition(runId, LauncherState.Failed, "recover:preparation-refused", "启动准备完成记录或辅助进程无法核验；拒绝接管。", false);
                return Result(false, false, "ADOPTION_PREPARATION_UNVERIFIED", "启动准备尚未核验。");
            }
            var notRunning = _components.Values
                .Where(component => component.State is not ComponentRuntimeState.Running)
                .ToArray();
            if (notRunning.Length > 0)
            {
                var detail = string.Join("；", notRunning.Select(component => $"{component.DisplayName}={component.State}"));
                var message = $"拒绝接管：并非所有组件都已通过运行核验（{detail}）。未启动、停止或改写这些组件。";
                WriteLog(LauncherLogLevel.Error, "launcher", message);
                Transition(runId, LauncherState.Failed, "recover:refused", message, false);
                return Result(false, false, "ADOPTION_COMPONENT_NOT_RUNNING", message);
            }

            Transition(runId, LauncherState.Running, "recovered", "已核验并接管原有本地服务。", false);
            WriteLog(LauncherLogLevel.Information, "launcher", "已有实例恢复核验完成；所有组件保持原进程运行。");
            return Result(true, false, null, "已安全接管原有运行组件。");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<LauncherOperationResult> RefuseRecoveryAsync(
        string errorCode,
        string message,
        CancellationToken cancellationToken = default,
        LauncherDiagnosticCode? diagnosticCode = null,
        LauncherDiagnosticComponent? diagnosticComponent = null)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(errorCode) || string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("恢复拒绝必须包含稳定错误码与可解释原因。");
        }

        if ((diagnosticCode is null) != (diagnosticComponent is null) ||
            diagnosticCode is { } code && !Enum.IsDefined(code) ||
            diagnosticComponent is { } component && !Enum.IsDefined(component))
        {
            throw new ArgumentException("恢复拒绝诊断类别必须提供有效的错误码和组件。");
        }

        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("已有启动、恢复或停止操作正在进行，不能并发记录恢复拒绝。");
        }

        try
        {
            var current = Snapshot;
            if (current.State is not (LauncherState.NotStarted or LauncherState.Failed))
            {
                throw new InvalidOperationException($"当前状态 {current.State} 不允许记录恢复拒绝。");
            }

            var runId = current.RunId ?? Guid.NewGuid();
            Transition(runId, LauncherState.Recovering, "recover:observe", "正在记录恢复拒绝前的真实组件状态。", false);
            await RefreshComponentStatesAsync(cancellationToken).ConfigureAwait(false);
            if (diagnosticCode is { } safeCode && diagnosticComponent is { } safeComponent)
            {
                _diagnosticEvents.Add(new LauncherDiagnosticEvent(
                    DateTimeOffset.UtcNow,
                    safeCode,
                    safeComponent,
                    LauncherDiagnosticSeverity.Error));
            }
            WriteLog(LauncherLogLevel.Error, "launcher", message);
            Transition(runId, LauncherState.Failed, "recover:refused", message, false);
            return Result(false, false, errorCode, message);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<LauncherOperationResult> RecognizeStoppedExistingAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("已有启动、恢复或停止操作正在进行，不能并发核验已停止实例。");
        }

        try
        {
            var current = Snapshot;
            if (current.State is not (LauncherState.NotStarted or LauncherState.Failed))
            {
                throw new InvalidOperationException($"当前状态 {current.State} 不允许核验已停止实例。");
            }

            var runId = current.RunId ?? Guid.NewGuid();
            Transition(runId, LauncherState.Recovering, "recover:observe", "正在确认既有组件全部停止。", false);
            await RefreshComponentStatesAsync(cancellationToken).ConfigureAwait(false);
            if (HasPossiblyRunningComponents())
            {
                const string refusal = "既有组件并非全部确认停止；拒绝提供手动启动入口。";
                WriteLog(LauncherLogLevel.Error, "launcher", refusal);
                Transition(runId, LauncherState.Failed, "recover:refused", refusal, false);
                return Result(false, false, "EXISTING_INSTANCE_NOT_STOPPED", refusal);
            }

            Transition(runId, LauncherState.Stopped, "recover:stopped", message, false);
            WriteLog(LauncherLogLevel.Information, "launcher", "既有实例全部停止，等待用户显式启动。");
            return Result(false, false, "EXISTING_INSTANCE_STOPPED", message);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<LauncherOperationResult> StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("已有启动或停止操作正在进行；启动中请先请求安全取消。");
        }

        try
        {
            return await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Keep the first (database) component running while the last (Web) component
    /// drains, run one explicit backup, then stop the remaining components.
    /// The operation gate excludes Start, Stop, recovery adoption and Shutdown.
    /// </summary>
    public async Task<T> RunQuiescedBackupAndStopAsync<T>(
        string databaseId,
        string writerId,
        Func<CancellationToken, Task<T>> backup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ThrowIfDisposed();
        if (_registrations.Count < 2 ||
            _registrations[0].Component.Id != databaseId ||
            _registrations[^1].Component.Id != writerId)
        {
            throw new InvalidOperationException("备份组件顺序与固定数据库/Web 契约不匹配。");
        }

        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("已有生命周期操作正在进行，不能开始备份。");
        }

        Exception? backupError = null;
        T? result = default;
        try
        {
            await RefreshComponentStatesAsync(cancellationToken).ConfigureAwait(false);
            if (Snapshot.State is not LauncherState.Running ||
                (_components.Values.Any(component => component.State is not ComponentRuntimeState.Running) ||
                 _steps.Values.Any(step => !step.IsQuiescent || step.State is not StartupStepState.Completed)))
            {
                throw new InvalidOperationException("只有全部组件已核验运行时才能开始停写备份。");
            }

            var runId = Snapshot.RunId;
            Transition(runId, LauncherState.BackingUp, "backup:drain", "正在排空 Web 与受管工作进程。", false);
            try
            {
                await _registrations[^1].Component.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await RefreshComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);
                if (_components[writerId].State is not ComponentRuntimeState.Stopped ||
                    _components[databaseId].State is not ComponentRuntimeState.Running ||
                    _steps.Values.Any(step => !step.IsQuiescent))
                {
                    throw new InvalidOperationException("Web 停机或数据库归属核验失败，拒绝备份。");
                }
            }
            catch (Exception exception)
            {
                Transition(runId, LauncherState.StopFailed, "backup:drain-failed", "Web 排空或数据库归属核验失败，拒绝备份并保留实例锁。", false);
                throw new InvalidOperationException("Web 排空失败，备份未开始。", exception);
            }

            try
            {
                Transition(runId, LauncherState.BackingUp, "backup:export", "正在创建加密业务备份。", false);
                result = await backup(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                backupError = exception;
                WriteLog(LauncherLogLevel.Error, "launcher", "加密备份未确认完成；正在安全停止剩余组件。");
            }

            Transition(runId, LauncherState.Stopping, "backup:stop", "正在停止数据库并结束备份维护窗口。", false);
            for (var index = _registrations.Count - 1; index >= 0; index--)
            {
                var component = _registrations[index].Component;
                await RefreshComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);
                if (HasUnsafeStepDependingOn(index))
                {
                    Transition(runId, LauncherState.StopFailed, "backup:step-unconfirmed", "准备辅助进程尚未确认退出，保留数据库。", false);
                    throw new InvalidOperationException("准备辅助进程尚未确认退出，拒绝停止其依赖。");
                }
                if (_components[component.Id].State is ComponentRuntimeState.Stopped)
                {
                    continue;
                }
                if (_components[component.Id].State is not ComponentRuntimeState.Running)
                {
                    Transition(runId, LauncherState.StopFailed, "backup:stop-failed", "备份后组件状态未知，未释放实例锁。", false);
                    throw new InvalidOperationException("备份后组件状态未知，拒绝继续停止下游组件。");
                }
                try
                {
                    await component.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    Transition(runId, LauncherState.StopFailed, "backup:stop-failed", "备份后组件停止失败，未释放实例锁。", false);
                    throw;
                }
            }

            await RefreshComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);
            if (HasPossiblyRunningComponents())
            {
                Transition(runId, LauncherState.StopFailed, "backup:stop-failed", "备份后组件未全部确认停止。", false);
                throw new InvalidOperationException("备份后组件未全部确认停止。");
            }
            Transition(runId, LauncherState.Stopped, "backup:stopped", "备份维护窗口已结束，组件均已停止。", false);
            if (backupError is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(backupError).Throw();
            }
            return result!;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<LauncherOperationResult> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (_cancellationSync)
        {
            ThrowIfDisposed();
            _shutdownRequested = true;
            _activeStartCancellation?.Cancel();
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);
            if (!HasPossiblyRunningComponents())
            {
                var current = Snapshot;
                if (current.State is not LauncherState.Stopped)
                {
                    Transition(
                        current.RunId,
                        LauncherState.Stopped,
                        "shutdown",
                        "关闭前检查完成，没有仍在运行的组件。",
                        false);
                }

                return Result(true, false, null, "可以安全关闭 Launcher。");
            }

            return await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        lock (_cancellationSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activeStartCancellation?.Cancel();
        }

        // 进行中的异步操作仍会在 finally 中释放门；SemaphoreSlim 由 GC 回收，避免并发 Dispose/Release。
    }

    private async Task<LauncherOperationResult> CancelAndRollbackAsync(Guid runId)
    {
        Transition(runId, LauncherState.Cancelling, "cancelling", "已到达安全点，正在回收本次演练启动的组件。", false);
        WriteLog(LauncherLogLevel.Warning, "launcher", "启动已在安全点取消，开始逆序回收。");
        var rollback = await RollbackRunningComponentsAsync(runId, "cancelled").ConfigureAwait(false);
        await RefreshComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);

        if (rollback is not null || HasPossiblyRunningComponents())
        {
            var failedMessage = rollback is null
                ? "取消后的回收未完成，仍有组件处于非停止状态。"
                : $"取消后的回收未完成：{rollback}";
            Transition(runId, LauncherState.StopFailed, "cancel-incomplete", failedMessage, false);
            return Result(false, true, "CANCEL_CLEANUP_INCOMPLETE", failedMessage);
        }

        Transition(runId, LauncherState.Stopped, "cancelled", "启动已取消，已启动组件均已安全停止。", false);
        return Result(false, true, "START_CANCELLED", "启动已安全取消。");
    }

    private async Task<LauncherOperationResult> StopCoreAsync(CancellationToken cancellationToken)
    {
        await RefreshComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);
        var current = Snapshot;
        if (current.State is (LauncherState.NotStarted or LauncherState.Stopped) && !HasPossiblyRunningComponents())
        {
            throw new InvalidOperationException("当前没有需要停止的组件。");
        }

        var runId = current.RunId ?? Guid.NewGuid();
        Transition(runId, LauncherState.Stopping, "stop", "正在按依赖逆序安全停止组件。", false);
        WriteLog(LauncherLogLevel.Information, "launcher", "停止序列开始。");

        for (var index = _registrations.Count - 1; index >= 0; index--)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await RefreshComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);
                var cancelledMessage = "停止操作已在组件边界取消；仍在运行的组件保持原状。";
                Transition(runId, LauncherState.StopFailed, "stop-cancelled", cancelledMessage, false);
                return Result(false, true, "STOP_CANCELLED", cancelledMessage);
            }

            var registration = _registrations[index];
            var component = registration.Component;
            var observed = _components[component.Id].State;
            if (HasUnsafeStepDependingOn(index))
            {
                Transition(runId, LauncherState.StopFailed, "stop:step-unconfirmed", "准备辅助进程尚未确认退出，保留数据库。", false);
                return Result(false, false, "STOP_PREPARATION_UNCONFIRMED", "准备辅助进程仍在运行或归属未知。");
            }
            if (observed is ComponentRuntimeState.Stopped)
            {
                continue;
            }

            if (observed is ComponentRuntimeState.Unknown)
            {
                RecordDiagnosticEvent(LauncherDiagnosticCode.ComponentStateReadFailed, component.Id, LauncherDiagnosticSeverity.Error);
                var unknownMessage = $"无法确认{component.DisplayName}归属状态；为避免误停下游组件，停止序列中止。";
                WriteLog(LauncherLogLevel.Error, component.Id, unknownMessage);
                Transition(runId, LauncherState.StopFailed, "stop-failed", unknownMessage, false);
                return Result(false, false, "STOP_STATE_UNKNOWN", unknownMessage);
            }

            Transition(runId, LauncherState.Stopping, $"stop:{component.Id}", $"正在停止{component.DisplayName}。", false);
            try
            {
                await component.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await RefreshComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);
                if (_components[component.Id].State is not ComponentRuntimeState.Stopped)
                {
                    RecordDiagnosticEvent(LauncherDiagnosticCode.ComponentStopUnconfirmed, component.Id, LauncherDiagnosticSeverity.Error);
                    var unconfirmedMessage = $"{component.DisplayName}停止步骤返回后实际状态为 {_components[component.Id].State}；未继续停止其下游依赖。";
                    WriteLog(LauncherLogLevel.Error, component.Id, unconfirmedMessage);
                    Transition(runId, LauncherState.StopFailed, "stop-unconfirmed", unconfirmedMessage, false);
                    return Result(false, false, "STOP_NOT_CONFIRMED", unconfirmedMessage);
                }

                WriteLog(LauncherLogLevel.Information, component.Id, $"{component.DisplayName}停止步骤完成。");
            }
            catch (Exception exception)
            {
                RecordDiagnosticEvent(LauncherDiagnosticCode.ComponentStopFailed, component.Id, LauncherDiagnosticSeverity.Error);
                StartupDiagnostics.Current.Record(StartupStage.Stop, StartupEventKind.Failed,
                    StartupDiagnostics.Component(component.Id), exception, runId);
                WriteLog(LauncherLogLevel.Error, component.Id, $"{component.DisplayName}停止失败：{exception.Message}");
                await RefreshComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);
                var failedMessage = $"{component.DisplayName}停止失败；未继续停止其下游依赖。";
                Transition(runId, LauncherState.StopFailed, "stop-failed", failedMessage, false);
                return Result(false, false, "STOP_FAILED", failedMessage);
            }
        }

        await RefreshComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);
        if (HasPossiblyRunningComponents())
        {
            var incompleteMessage = "停止步骤结束，但仍有组件状态不是“已停止”。";
            Transition(runId, LauncherState.StopFailed, "stop-incomplete", incompleteMessage, false);
            return Result(false, false, "STOP_INCOMPLETE", incompleteMessage);
        }

        Transition(runId, LauncherState.Stopped, "stopped", "全部组件已安全停止。", false);
        WriteLog(LauncherLogLevel.Information, "launcher", "停止序列完成。");
        return Result(true, false, null, "停止成功。");
    }

    private async Task<string?> RollbackRunningComponentsAsync(Guid runId, string phasePrefix)
    {
        await RefreshComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);

        for (var index = _registrations.Count - 1; index >= 0; index--)
        {
            var component = _registrations[index].Component;
            var observed = _components[component.Id].State;
            if (HasUnsafeStepDependingOn(index))
                return "准备辅助进程尚未确认退出；未继续停止其依赖";
            if (observed is ComponentRuntimeState.Stopped)
            {
                continue;
            }

            if (observed is ComponentRuntimeState.Unknown)
            {
                return $"无法确认{component.DisplayName}状态，未继续回收下游依赖";
            }

            Transition(runId, LauncherState.Cancelling, $"{phasePrefix}:stop:{component.Id}", $"正在回收{component.DisplayName}。", false);
            try
            {
                await component.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await RefreshComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);
                if (_components[component.Id].State is not ComponentRuntimeState.Stopped)
                {
                    var observedAfterStop = _components[component.Id].State;
                    WriteLog(LauncherLogLevel.Error, component.Id, $"{component.DisplayName}回收步骤返回后实际状态为 {observedAfterStop}。");
                    return $"{component.DisplayName}未确认停止；未继续停止其下游依赖";
                }

                WriteLog(LauncherLogLevel.Information, component.Id, $"{component.DisplayName}已回收。");
            }
            catch (Exception exception)
            {
                WriteLog(LauncherLogLevel.Error, component.Id, $"{component.DisplayName}回收失败：{exception.Message}");
                await RefreshComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);
                return $"{component.DisplayName}回收失败；未继续停止其下游依赖";
            }
        }

        return null;
    }

    private async Task RefreshComponentStatesAsync(CancellationToken cancellationToken)
    {
        await RefreshStartupStepsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var registration in _registrations)
        {
            var component = registration.Component;
            try
            {
                var state = await component.GetStateAsync(cancellationToken).ConfigureAwait(false);
                _components[component.Id] = new ComponentSnapshot(component.Id, component.DisplayName, state);
            }
            catch (Exception exception)
            {
                RecordDiagnosticEvent(LauncherDiagnosticCode.ComponentStateReadFailed, component.Id, LauncherDiagnosticSeverity.Error);
                _components[component.Id] = new ComponentSnapshot(
                    component.Id,
                    component.DisplayName,
                    ComponentRuntimeState.Unknown,
                    exception.Message);
                WriteLog(LauncherLogLevel.Error, component.Id, $"读取{component.DisplayName}状态失败：{exception.Message}");
            }
        }
    }

    private void EnsureStartAllowed()
    {
        var current = Snapshot.State;
        if (current is LauncherState.Starting or LauncherState.Recovering or LauncherState.Cancelling or LauncherState.Running or LauncherState.BackingUp or LauncherState.Stopping)
        {
            throw new InvalidOperationException($"当前状态 {current} 不允许重复启动。");
        }

        if (HasPossiblyRunningComponents())
        {
            throw new InvalidOperationException("仍有组件处于运行、过渡或未知状态；请先完成停止或诊断，不能重复启动。");
        }
    }

    private bool HasPossiblyRunningComponents()
    {
        return _components.Values.Any(component => component.State is not ComponentRuntimeState.Stopped) ||
            _steps.Values.Any(step => !step.IsQuiescent);
    }

    private void Transition(Guid? runId, LauncherState state, string phase, string message, bool safeToCancel)
    {
        if (!Snapshot.IsSimulation)
        {
            var component = phase.StartsWith("start:", StringComparison.Ordinal) ? phase[6..]
                : phase.StartsWith("prepare:", StringComparison.Ordinal) ? phase[8..]
                : phase.StartsWith("stop:", StringComparison.Ordinal) ? phase[5..] : "launcher";
            var stage = state is LauncherState.Stopping or LauncherState.Stopped or LauncherState.StopFailed
                ? StartupStage.Stop : state is LauncherState.Recovering ? StartupStage.InstanceRecovery : StartupStage.Start;
            var kind = state is LauncherState.Failed or LauncherState.StopFailed ? StartupEventKind.Failed
                : state is LauncherState.Running or LauncherState.Stopped ? StartupEventKind.Completed : StartupEventKind.Begin;
            StartupDiagnostics.Current.Record(stage, kind, StartupDiagnostics.Component(component), operationId: runId);
        }
        var current = Snapshot;
        var next = CreateSnapshot(runId, state, phase, message, safeToCancel, current.IsSimulation);
        lock (_snapshotSync)
        {
            _snapshot = next;
        }

        PublishSnapshot(next);
    }

    private LauncherSnapshot CreateSnapshot(
        Guid? runId,
        LauncherState state,
        string phase,
        string message,
        bool safeToCancel,
        bool isSimulation)
    {
        return new LauncherSnapshot(
            runId,
            state,
            phase,
            message,
            safeToCancel,
            isSimulation,
            DateTimeOffset.UtcNow,
            _registrations.Select(item => _components[item.Component.Id]).ToArray())
        {
            StartupSteps = _startupSteps.Select(item => _steps[item.Step.Id]).ToArray(),
        };
    }

    private LauncherOperationResult Result(bool succeeded, bool cancelled, string? errorCode, string message)
    {
        return new LauncherOperationResult(succeeded, cancelled, errorCode, message, Snapshot);
    }

    private void WriteLog(LauncherLogLevel level, string component, string message)
    {
        var entry = BoundedLogBuffer.BoundEntry(new LauncherLogEntry(DateTimeOffset.Now, level, component, message));
        _logs.Add(entry);

        var handlers = LogAdded?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers)
        {
            try
            {
                ((EventHandler<LauncherLogEntry>)handler)(this, entry);
            }
            catch
            {
                // 观察者异常不能破坏编排收尾；避免在这里再次触发日志事件造成递归。
            }
        }
    }

    private void RecordDiagnosticEvent(
        LauncherDiagnosticCode code,
        string componentId,
        LauncherDiagnosticSeverity severity)
    {
        LauncherDiagnosticComponent? component = componentId switch
        {
            "postgres" or "postgres-initialize" => LauncherDiagnosticComponent.Postgres,
            "database-provision" => LauncherDiagnosticComponent.DatabaseProvision,
            "python-web" => LauncherDiagnosticComponent.PythonWeb,
            "python-maintenance" => LauncherDiagnosticComponent.PythonMaintenance,
            _ => null,
        };

        if (component is not null)
        {
            _diagnosticEvents.Add(new LauncherDiagnosticEvent(DateTimeOffset.UtcNow, code, component.Value, severity));
        }
    }

    private void PublishSnapshot(LauncherSnapshot snapshot)
    {
        var handlers = SnapshotChanged?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers)
        {
            try
            {
                ((EventHandler<LauncherSnapshot>)handler)(this, snapshot);
            }
            catch (Exception exception)
            {
                WriteLog(LauncherLogLevel.Warning, "launcher", $"状态观察者异常已隔离：{exception.Message}");
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
