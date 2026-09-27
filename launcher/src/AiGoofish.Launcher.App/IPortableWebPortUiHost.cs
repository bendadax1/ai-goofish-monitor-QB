using AiGoofish.Launcher.Core;
using AiGoofish.Launcher.Platform.Windows;

namespace AiGoofish.Launcher.App;

/// <summary>
/// App-only surface required by the Web-port settings UI. Production instances
/// are created only by wrapping an already verified RealPortableStackHost.
/// </summary>
internal interface IPortableWebPortUiHost
{
    LauncherCoordinator Coordinator { get; }
    LauncherRuntime Runtime { get; }
    int PostgresPort { get; }
    string ManagementUrl { get; }
    bool SetupRequired { get; }
    PortableWebPortSettings WebPortSettings { get; }
    PortableWebPortSettings StageWebPort(long expectedRevision, int candidatePort, bool automatic = false);
    Task<PortableWebPortApplyResult> ApplyPendingWebPortAsync(
        long expectedRevision);
}

internal sealed class RealPortableWebPortUiHost(RealPortableStackHost host) : IPortableWebPortUiHost
{
    private readonly RealPortableStackHost _host = host ?? throw new ArgumentNullException(nameof(host));

    public LauncherCoordinator Coordinator => _host.Coordinator;
    public LauncherRuntime Runtime => _host.Runtime;
    public int PostgresPort => _host.PostgresPort;
    public string ManagementUrl => _host.ManagementUrl;
    public bool SetupRequired => _host.SetupRequired;
    public PortableWebPortSettings WebPortSettings => _host.WebPortSettings;
    public PortableWebPortSettings StageWebPort(long expectedRevision, int candidatePort, bool automatic = false) =>
        _host.StageWebPort(expectedRevision, candidatePort, automatic);

    public Task<PortableWebPortApplyResult> ApplyPendingWebPortAsync(
        long expectedRevision) =>
        _host.ApplyPendingWebPortAsync(expectedRevision);
}
