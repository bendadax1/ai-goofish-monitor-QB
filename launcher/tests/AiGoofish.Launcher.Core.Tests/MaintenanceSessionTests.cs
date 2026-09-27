using AiGoofish.Launcher.Core;

internal static class MaintenanceSessionTests
{
    public static async Task RunAsync()
    {
        var old = new Component("web");
        var replacement = new Component("web");
        using var coordinator = new LauncherCoordinator([new(old)], false);
        var snapshots = new List<LauncherSnapshot>();
        coordinator.SnapshotChanged += (_, value) => snapshots.Add(value);
        Require((await coordinator.StartAsync()).Succeeded, "Baseline start");
        Require((await coordinator.StopAsync()).Succeeded, "Baseline stop");
        var logCount = coordinator.Logs.Count;
        LauncherCoordinator.MaintenanceSession? expired = null;
        await coordinator.RunMaintenanceAsync(async session =>
        {
            expired = session;
            await Reject(() => coordinator.StartAsync());
            await Reject(() => coordinator.StopAsync());
            await Reject(() => coordinator.RunMaintenanceAsync(_ => Task.FromResult(true)));
            await Reject(() => session.ReplaceStoppedComponentAsync(old, new Component("different")));
            await Reject(() => session.ReplaceStoppedComponentAsync(old, new Component("web") { State = ComponentRuntimeState.Unknown }));
            await session.ReplaceStoppedComponentAsync(old, replacement);
            await Reject(() => session.ReplaceStoppedComponentAsync(old, new Component("web")));
            Require((await session.StartAsync()).Succeeded, "Replacement start");
            await Reject(() => session.ReplaceStoppedComponentAsync(replacement, new Component("web")));
            Require((await session.StopAsync()).Succeeded, "Replacement stop");
            return true;
        });
        Require(old.Starts == 1 && replacement.Starts == 1, "Only replacement used after commit");
        Require(coordinator.Logs.Count > logCount, "Logs must survive component replacement");
        Require(snapshots.Count(s => s.State == LauncherState.Running) == 2, "Existing subscription must see both runs");
        await Reject(() => expired!.StartAsync());
        await Reject(() => expired!.ReplaceStoppedComponentAsync(replacement, old));
        await Reject(() => coordinator.RunMaintenanceAsync<bool>(_ => throw new InvalidOperationException("synthetic")));
        Require((await coordinator.StartAsync()).Succeeded, "Exception must release transaction gate");
        await Reject(() => coordinator.RunMaintenanceAsync(_ => Task.FromResult(true)));
        await coordinator.StopAsync();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await coordinator.RunMaintenanceAsync(_ => Task.FromResult(true), cancellation.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        await coordinator.RunMaintenanceAsync(_ => Task.FromResult(true));

        var entered = Signal();
        var finish = Signal();
        var slow = new Component("web") { BeforeStart = async () => { entered.SetResult(); await finish.Task; } };
        using var closing = new LauncherCoordinator([new(slow)], false);
        var transaction = closing.RunMaintenanceAsync(async session =>
        {
            var starting = session.StartAsync();
            await entered.Task;
            await Reject(() => session.StopAsync());
            await starting;
            return true;
        });
        await entered.Task;
        var shutdown = closing.ShutdownAsync();
        Require(!shutdown.IsCompleted, "Shutdown must wait for transaction cleanup");
        finish.SetResult();
        await transaction.WaitAsync(TimeSpan.FromSeconds(5));
        Require((await shutdown.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded && slow.State == ComponentRuntimeState.Stopped,
            "Shutdown must leave no live component");
        await Reject(() => closing.RunMaintenanceAsync(_ => Task.FromResult(true)));
        Console.WriteLine("MAINTENANCE_SESSION_PASS");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected maintenance refusal");
    }

    private sealed class Component(string id) : ILauncherComponent
    {
        public string Id => id;
        public string DisplayName => id;
        public ComponentRuntimeState State { get; set; } = ComponentRuntimeState.Stopped;
        public Func<Task>? BeforeStart { get; init; }
        public int Starts { get; private set; }
        public ValueTask<ComponentRuntimeState> GetStateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(State);
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            if (BeforeStart is not null) await BeforeStart();
            State = ComponentRuntimeState.Running;
        }
        public Task StopAsync(CancellationToken cancellationToken) { State = ComponentRuntimeState.Stopped; return Task.CompletedTask; }
    }
}
