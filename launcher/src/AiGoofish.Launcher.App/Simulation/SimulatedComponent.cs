using AiGoofish.Launcher.Core;

namespace AiGoofish.Launcher.App.Simulation;

internal sealed class SimulatedComponent : ILauncherComponent
{
    private readonly TimeSpan _startDelay;
    private readonly TimeSpan _stopDelay;
    private readonly object _sync = new();
    private ComponentRuntimeState _state = ComponentRuntimeState.Stopped;

    public SimulatedComponent(string id, string displayName, TimeSpan startDelay, TimeSpan stopDelay)
    {
        Id = id;
        DisplayName = displayName;
        _startDelay = startDelay;
        _stopDelay = stopDelay;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public ValueTask<ComponentRuntimeState> GetStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return ValueTask.FromResult(_state);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        SetState(ComponentRuntimeState.Starting);
        try
        {
            await Task.Delay(_startDelay, cancellationToken).ConfigureAwait(false);
            SetState(ComponentRuntimeState.Running);
        }
        catch
        {
            SetState(ComponentRuntimeState.Stopped);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        SetState(ComponentRuntimeState.Stopping);
        try
        {
            await Task.Delay(_stopDelay, cancellationToken).ConfigureAwait(false);
            SetState(ComponentRuntimeState.Stopped);
        }
        catch
        {
            SetState(ComponentRuntimeState.Running);
            throw;
        }
    }

    private void SetState(ComponentRuntimeState state)
    {
        lock (_sync)
        {
            _state = state;
        }
    }
}
