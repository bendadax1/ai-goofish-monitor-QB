namespace AiGoofish.Launcher.Core;

public sealed partial class LauncherCoordinator
{
    private readonly LauncherStartupStepRegistration[] _startupSteps;
    private readonly Dictionary<string, StartupStepSnapshot> _steps;

    private async Task RefreshStartupStepsAsync(CancellationToken cancellationToken)
    {
        foreach (var registration in _startupSteps)
        {
            var step = registration.Step;
            var quiescent = false;
            try { quiescent = await step.IsQuiescentAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception exception)
            {
                StartupDiagnostics.Current.Record(StartupStage.Start, StartupEventKind.Failed,
                    StartupDiagnostics.Component(step.Id), exception, Snapshot.RunId);
                RecordDiagnosticEvent(LauncherDiagnosticCode.ComponentStateReadFailed, step.Id, LauncherDiagnosticSeverity.Error);
                WriteLog(LauncherLogLevel.Error, step.Id, "无法确认启动步骤的辅助进程已退出；保留依赖与实例锁。");
            }
            _steps[step.Id] = _steps[step.Id] with { IsQuiescent = quiescent };
        }
    }

    private async Task VerifyStartupStepsForAdoptionAsync(CancellationToken cancellationToken)
    {
        foreach (var registration in _startupSteps)
        {
            var step = registration.Step;
            try
            {
                await step.VerifyCompletedAsync(cancellationToken).ConfigureAwait(false);
                if (!await step.IsQuiescentAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("启动步骤仍有未确认退出的辅助进程。");
                _steps[step.Id] = _steps[step.Id] with { State = StartupStepState.Completed, IsQuiescent = true };
            }
            catch
            {
                _steps[step.Id] = _steps[step.Id] with { State = StartupStepState.Failed };
                throw;
            }
        }
    }

    // A step before Web may use PostgreSQL. Stop Web first, then refuse to
    // stop its dependencies until that step's helpers are confirmed gone.
    private bool HasUnsafeStepDependingOn(int componentIndex) => _startupSteps.Any(registration =>
        !_steps[registration.Step.Id].IsQuiescent &&
        _registrations.FindIndex(item => item.Component.Id == registration.BeforeComponentId) > componentIndex);
}
