using System.Diagnostics;

namespace AiGoofish.Launcher.Platform.Windows;

public enum OwnedProcessState
{
    NotStarted,
    Starting,
    Running,
    Exited,
    StartFailed,
    StopRequested,
    StopTimedOut,
    Unknown,
}

public sealed record ProcessEnvironmentVariable(string Name, string Value, bool Sensitive = false);

public sealed record OwnedProcessLaunchSpec(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> PublicArguments,
    IReadOnlyList<ProcessEnvironmentVariable> EnvironmentVariables,
    TimeSpan StartupGracePeriod);

public sealed record OwnedProcessIdentity(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    string ExecutablePath,
    string InstanceRoot,
    Guid InstanceId,
    Guid RunId);

public sealed record OwnedProcessSnapshot(
    OwnedProcessState State,
    OwnedProcessIdentity? Identity,
    int? ExitCode,
    DateTimeOffset ObservedAt,
    string Message);

public sealed record OwnedProcessOperationResult(
    bool Succeeded,
    bool Cancelled,
    string? ErrorCode,
    OwnedProcessSnapshot Snapshot);

public interface IProcessStopProtocol
{
    Task RequestStopAsync(OwnedProcessIdentity identity, CancellationToken cancellationToken);
}

internal sealed record OwnedProcessRecord(
    Process Process,
    OwnedProcessIdentity Identity,
    Task StandardOutputTask,
    Task StandardErrorTask);
