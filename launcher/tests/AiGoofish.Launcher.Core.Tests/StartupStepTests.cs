using AiGoofish.Launcher.Core;

internal static class StartupStepTests
{
    public static async Task RunAsync()
    {
        var calls = new List<string>();
        var pg = new Service("postgres", calls);
        var web = new Service("python-web", calls);
        var step = new Step(calls);
        using var coordinator = Create(pg, web, step);
        Require((await coordinator.StartAsync()).Succeeded, "Start");
        Require(string.Join(',', calls) == "start:postgres,prepare,start:python-web", "Preparation order");
        Require(coordinator.Snapshot.Components.Count == 2 && coordinator.Snapshot.StartupSteps.Single().State == StartupStepState.Completed,
            "Completed step is not a running service");
        await coordinator.StopAsync();
        Require(coordinator.Snapshot.IsQuiescent && coordinator.Snapshot.StartupSteps.Single().State == StartupStepState.Completed,
            "Stopping services must not undo preparation");
        Require(string.Join(',', calls) == "start:postgres,prepare,start:python-web,stop:python-web,stop:postgres", "No fake stop for preparation");
        await coordinator.StartAsync();
        calls.Clear();
        await coordinator.RunQuiescedBackupAndStopAsync("postgres", "python-web", _ =>
        {
            Require(pg.State == ComponentRuntimeState.Running && web.State == ComponentRuntimeState.Stopped, "Backup dependency");
            calls.Add("backup");
            return Task.FromResult(true);
        });
        Require(string.Join(',', calls) == "stop:python-web,backup,stop:postgres", "Backup after split");

        foreach (var unsafeHelper in new[] { false, true })
        {
            calls.Clear();
            var failedStep = new Step(calls) { Execute = _ =>
            {
                throw new IOException("synthetic-secret-must-not-leak");
            } };
            failedStep.OnExecute = () => failedStep.Quiescent = !unsafeHelper;
            var failedPg = new Service("postgres", calls);
            var failedWeb = new Service("python-web", calls);
            using var failed = Create(failedPg, failedWeb, failedStep);
            Require(!(await failed.StartAsync()).Succeeded && !calls.Contains("start:python-web"), "Failed step must prevent Web");
            Require(failedPg.State == (unsafeHelper ? ComponentRuntimeState.Running : ComponentRuntimeState.Stopped), "Rollback must respect helper ownership");
            Require(!failed.Logs.Any(entry => entry.Message.Contains("synthetic-secret")), "Failure privacy");
            if (unsafeHelper)
            {
                Require(!(await failed.StopAsync()).Succeeded && !(await failed.ShutdownAsync()).Succeeded, "Unsafe helper blocks stop/shutdown");
                Require(failed.Snapshot.HasUnconfirmedStartupWork, "Unsafe helper visible to lease/UI guards");
                failedStep.Quiescent = true;
                Require((await failed.ShutdownAsync()).Succeeded && failed.Snapshot.IsQuiescent, "Safe shutdown after helper exits");
            }
        }

        calls.Clear();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var protectedStep = new Step(calls) { Execute = async token =>
        {
            Require(!token.CanBeCanceled, "Noncancellable preparation safety boundary");
            entered.SetResult();
            await finish.Task;
        } };
        using (var cancelled = Create(new("postgres", calls), new("python-web", calls), protectedStep))
        {
            var start = cancelled.StartAsync();
            await entered.Task;
            Require(cancelled.RequestStartupCancellation() && !start.IsCompleted, "Cancellation waits for step");
            finish.SetResult();
            Require((await start).Cancelled && cancelled.Snapshot.IsQuiescent && !calls.Contains("start:python-web"), "Cancel at preparation boundary");
        }

        calls.Clear();
        var existingPg = new Service("postgres", calls) { State = ComponentRuntimeState.Running };
        var existingWeb = new Service("python-web", calls) { State = ComponentRuntimeState.Running };
        var existingStep = new Step(calls) { RefuseVerification = true };
        using (var recovered = Create(existingPg, existingWeb, existingStep))
        {
            Require(!(await recovered.AdoptAlreadyRunningAsync()).Succeeded && calls.Count == 0, "Adoption verifies without executing preparation");
            existingStep.RefuseVerification = false;
            Require((await recovered.AdoptAlreadyRunningAsync()).Succeeded && calls.Count == 0, "Adoption accepts verified completion");
            existingStep.ThrowOnObservation = true;
            Require((await recovered.RefreshObservationAsync()).State == LauncherState.Failed, "Lost helper observation invalidates Ready");
            Require(!(await recovered.StopAsync()).Succeeded && existingPg.State == ComponentRuntimeState.Running && existingWeb.State == ComponentRuntimeState.Stopped,
                "Observation error protects dependency, permits writer drain");
            existingStep.ThrowOnObservation = false;
            Require((await recovered.ShutdownAsync()).Succeeded, "Recovered observation permits cleanup");
        }

        using (var invalid = new LauncherCoordinator([new(new Service("only", calls))], false))
            Require(invalid.Snapshot.StartupSteps.Count == 0, "No-step compatibility");
        try
        {
            using var invalid = new LauncherCoordinator([new(new Service("only", calls))], false,
                startupSteps: [new(step, "missing")]);
            throw new Exception("Missing dependency accepted");
        }
        catch (ArgumentException) { }
        Console.WriteLine("STARTUP_STEPS_PASS");
    }

    private static LauncherCoordinator Create(Service pg, Service web, Step step) =>
        new([new(pg), new(web)], false, startupSteps: [new(step, web.Id, CancellationIsSafe: false)]);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Service(string id, List<string> calls) : ILauncherComponent
    {
        public string Id => id;
        public string DisplayName => id;
        public ComponentRuntimeState State { get; set; } = ComponentRuntimeState.Stopped;
        public ValueTask<ComponentRuntimeState> GetStateAsync(CancellationToken token) => ValueTask.FromResult(State);
        public Task StartAsync(CancellationToken token) { calls.Add("start:" + Id); State = ComponentRuntimeState.Running; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken token) { calls.Add("stop:" + Id); State = ComponentRuntimeState.Stopped; return Task.CompletedTask; }
    }

    private sealed class Step(List<string> calls) : ILauncherStartupStep
    {
        public string Id => "database-provision";
        public string DisplayName => "数据库准备";
        public bool Quiescent { get; set; } = true;
        public bool ThrowOnObservation { get; set; }
        public bool RefuseVerification { get; set; }
        public Func<CancellationToken, Task>? Execute { get; init; }
        public Action? OnExecute { get; set; }
        public async Task ExecuteAsync(CancellationToken token)
        {
            calls.Add("prepare");
            OnExecute?.Invoke();
            if (Execute is not null) await Execute(token);
        }
        public Task VerifyCompletedAsync(CancellationToken token) => RefuseVerification
            ? Task.FromException(new IOException("untrusted completion")) : Task.CompletedTask;
        public ValueTask<bool> IsQuiescentAsync(CancellationToken token) => ThrowOnObservation
            ? throw new IOException("unknown helper") : ValueTask.FromResult(Quiescent);
    }
}
