namespace AiGoofish.Launcher.Core;

public enum LauncherState
{
    NotStarted,
    Starting,
    Recovering,
    Cancelling,
    Running,
    BackingUp,
    Failed,
    Stopping,
    Stopped,
    StopFailed,
}

public enum ComponentRuntimeState
{
    Unknown,
    Stopped,
    Starting,
    Running,
    Stopping,
    Failed,
}

public enum LauncherLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

public interface ILauncherComponent
{
    string Id { get; }

    string DisplayName { get; }

    ValueTask<ComponentRuntimeState> GetStateAsync(CancellationToken cancellationToken);

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public sealed record LauncherComponentRegistration(
    ILauncherComponent Component,
    bool StartupCancellationIsSafe = true);

/// <summary>A bounded preparation operation, never a long-lived service.</summary>
public interface ILauncherStartupStep
{
    string Id { get; }
    string DisplayName { get; }
    Task ExecuteAsync(CancellationToken cancellationToken);
    Task VerifyCompletedAsync(CancellationToken cancellationToken);
    // False (including unknown ownership) protects dependent services and the lease.
    ValueTask<bool> IsQuiescentAsync(CancellationToken cancellationToken);
}

public sealed record LauncherStartupStepRegistration(
    ILauncherStartupStep Step,
    string BeforeComponentId,
    bool CancellationIsSafe = true);

public enum StartupStepState { NotRun, Executing, Completed, Failed }

public sealed record StartupStepSnapshot(
    string Id, string DisplayName, StartupStepState State, bool IsQuiescent);

public sealed record ComponentSnapshot(
    string Id,
    string DisplayName,
    ComponentRuntimeState State,
    string? ObservationError = null);

public sealed record LauncherSnapshot(
    Guid? RunId,
    LauncherState State,
    string Phase,
    string Message,
    bool SafeToCancel,
    bool IsSimulation,
    DateTimeOffset ObservedAt,
    IReadOnlyList<ComponentSnapshot> Components)
{
    public IReadOnlyList<StartupStepSnapshot> StartupSteps { get; init; } = [];
    public bool HasUnconfirmedStartupWork => StartupSteps.Any(step => !step.IsQuiescent);
    public bool IsQuiescent => !HasUnconfirmedStartupWork &&
        Components.All(component => component.State is ComponentRuntimeState.Stopped);
}

public sealed record LauncherLogEntry(
    DateTimeOffset Timestamp,
    LauncherLogLevel Level,
    string Component,
    string Message);

public sealed record LauncherOperationResult(
    bool Succeeded,
    bool Cancelled,
    string? ErrorCode,
    string Message,
    LauncherSnapshot Snapshot);
