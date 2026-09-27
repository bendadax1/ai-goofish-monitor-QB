namespace AiGoofish.Launcher.Core;

public sealed record LauncherStartSettings(int EffectivePort, bool Automatic, bool HasPendingPort);

public sealed record LauncherStartResult(
    LauncherOperationResult Operation, int EffectivePort, bool PortChanged, bool SetupRequired);

/// <summary>
/// Platform capability scoped to the host lock AND the coordinator maintenance
/// gate. Adapters validate lease, durable intent and current revision before
/// invoking the runtime; callers must not retain this session.
/// </summary>
public interface ILauncherStartSession
{
    LauncherStartSettings Settings { get; }
    bool CanBindEffectivePort();
    Task<LauncherStartResult> StartCurrentAsync(CancellationToken cancellationToken);
    Task<LauncherStartResult> StartOnAutomaticPortAsync(CancellationToken cancellationToken);
}

/// <summary>UI-independent product startup policy shared by desktop and headless callers.</summary>
public sealed class LauncherRuntime(
    LauncherCoordinator coordinator,
    Func<Func<ILauncherStartSession, Task<LauncherStartResult>>, CancellationToken, Task<LauncherStartResult>> runStartup)
{
    public LauncherCoordinator Coordinator { get; } = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly Func<Func<ILauncherStartSession, Task<LauncherStartResult>>, CancellationToken, Task<LauncherStartResult>>
        _runStartup = runStartup ?? throw new ArgumentNullException(nameof(runStartup));

    public Task<LauncherStartResult> StartAsync(CancellationToken cancellationToken = default) =>
        _runStartup(async session =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = session.Settings;
            if (settings.EffectivePort is < 1 or > 65535)
                throw new InvalidOperationException("有效 Web 端口未通过核验；拒绝启动。");
            if (session.CanBindEffectivePort())
                return await session.StartCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (settings.Automatic && !settings.HasPendingPort)
                return await session.StartOnAutomaticPortAsync(cancellationToken).ConfigureAwait(false);

            // A pending manual choice is never silently applied by Start.
            var message = $"Web 端口 {settings.EffectivePort} 当前不可用。请在端口设置中暂存并应用一个可用端口后重试。";
            return new LauncherStartResult(new LauncherOperationResult(false, false,
                "WEB_PORT_UNAVAILABLE", message, Coordinator.Snapshot), settings.EffectivePort, false, false);
        }, cancellationToken);

    public Task<LauncherOperationResult> StopAsync(CancellationToken cancellationToken = default) =>
        Coordinator.StopAsync(cancellationToken);
}
