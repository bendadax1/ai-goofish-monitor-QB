using AiGoofish.Launcher.Core;

namespace AiGoofish.Launcher.Platform.Windows;

/// <summary>Runs before the PostgreSQL service; adoption only verifies existing completion.</summary>
internal sealed class WindowsPostgresInitializationStep(
    WindowsPostgresComponent postgres, Action prepare) : ILauncherStartupStep
{
    public string Id => "postgres-initialize";
    public string DisplayName => "PostgreSQL 初始化";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        prepare();
        await postgres.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task VerifyCompletedAsync(CancellationToken cancellationToken) =>
        postgres.VerifyInitializationCompletedAsync(cancellationToken);

    public ValueTask<bool> IsQuiescentAsync(CancellationToken cancellationToken) =>
        postgres.IsInitializationQuiescentAsync(cancellationToken);
}
