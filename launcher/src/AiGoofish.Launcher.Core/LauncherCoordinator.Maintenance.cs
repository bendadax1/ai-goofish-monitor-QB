namespace AiGoofish.Launcher.Core;

public sealed partial class LauncherCoordinator
{
    /// <summary>
    /// Holds the same operation gate across a multi-step maintenance transaction.
    /// Observers stay attached; competing starts/stops fail and shutdown waits.
    /// The scoped session cannot be retained or used concurrently.
    /// </summary>
    public async Task<T> RunMaintenanceAsync<T>(
        Func<MaintenanceSession, Task<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ThrowIfDisposed();
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("已有生命周期操作正在进行，不能开始维护事务。");
        var session = new MaintenanceSession(this);
        try
        {
            ThrowIfDisposed();
            lock (_cancellationSync)
                if (_shutdownRequested) throw new InvalidOperationException("Launcher 正在关闭，不能开始维护事务。");
            await RefreshComponentStatesAsync(cancellationToken).ConfigureAwait(false);
            // A rejected startup (e.g. occupied fixed port) still needs a fresh
            // passive snapshot; it must not leave the UI at NotStarted/Unknown.
            var observed = Snapshot with
            {
                ObservedAt = DateTimeOffset.UtcNow,
                Components = _registrations.Select(item => _components[item.Component.Id]).ToArray(),
                StartupSteps = _startupSteps.Select(item => _steps[item.Step.Id]).ToArray(),
            };
            lock (_snapshotSync) _snapshot = observed;
            PublishSnapshot(observed);
            if (HasPossiblyRunningComponents())
                throw new InvalidOperationException("维护事务要求所有组件已确认停止。");
            return await action(session).ConfigureAwait(false);
        }
        finally
        {
            // Drain an accidentally unawaited operation before releasing the
            // coordinator gate; all later uses of this capability are refused.
            await session.CloseAsync().ConfigureAwait(false);
            _operationGate.Release();
        }
    }

    public sealed class MaintenanceSession
    {
        private readonly LauncherCoordinator _owner;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _closed;

        internal MaintenanceSession(LauncherCoordinator owner) => _owner = owner;

        public Task<LauncherOperationResult> StartAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => _owner.StartCoreAsync(cancellationToken));

        public Task<LauncherOperationResult> StopAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => _owner.StopCoreAsync(cancellationToken));

        public Task ReplaceStoppedComponentAsync(ILauncherComponent expected, ILauncherComponent replacement,
            CancellationToken cancellationToken = default) => RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(expected);
            ArgumentNullException.ThrowIfNull(replacement);
            cancellationToken.ThrowIfCancellationRequested();
            if (expected.Id != replacement.Id) throw new InvalidOperationException("替换组件必须保持相同 ID。");
            var index = _owner._registrations.FindIndex(item => ReferenceEquals(item.Component, expected));
            if (index < 0) throw new InvalidOperationException("待替换组件已变化。");
            await _owner.RefreshComponentStatesAsync(cancellationToken).ConfigureAwait(false);
            if (_owner.HasPossiblyRunningComponents() ||
                await replacement.GetStateAsync(cancellationToken).ConfigureAwait(false) is not ComponentRuntimeState.Stopped)
                throw new InvalidOperationException("旧组件或替换组件未确认停止，拒绝替换。");
            _owner._registrations[index] = _owner._registrations[index] with { Component = replacement };
            _owner._components[replacement.Id] = new ComponentSnapshot(replacement.Id, replacement.DisplayName, ComponentRuntimeState.Stopped);
            var current = _owner.Snapshot;
            _owner.Transition(current.RunId, LauncherState.Stopped, "maintenance:replaced", "组件配置已更新，全部组件保持停止。", false);
            return true;
        });

        private async Task<T> RunAsync<T>(Func<Task<T>> operation)
        {
            if (Volatile.Read(ref _closed) != 0 || !await _gate.WaitAsync(0).ConfigureAwait(false))
                throw new InvalidOperationException("维护会话已结束或已有操作正在进行。");
            try
            {
                if (Volatile.Read(ref _closed) != 0) throw new InvalidOperationException("维护会话已结束。");
                _owner.ThrowIfDisposed();
                return await operation().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        internal async Task CloseAsync()
        {
            Interlocked.Exchange(ref _closed, 1);
            await _gate.WaitAsync().ConfigureAwait(false);
            _gate.Release();
        }
    }
}
