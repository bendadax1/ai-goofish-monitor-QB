using AiGoofish.Launcher.Core;

internal static class LauncherRuntimeTests
{
    public static async Task RunAsync()
    {
        foreach (var automatic in new[] { false, true })
        foreach (var pending in new[] { false, true })
        foreach (var available in new[] { false, true })
        {
            using var host = new Host(automatic, pending, available);
            var result = await host.Runtime.StartAsync();
            var shouldSwitch = automatic && !pending && !available;
            Require(result.Operation.Succeeded == (available || shouldSwitch), "Startup policy matrix");
            Require(host.AutomaticStarts == (shouldSwitch ? 1 : 0), "Only binding conflict permits automatic replacement");
            Require(host.Service.Starts == (available || shouldSwitch ? 1 : 0), "No silent pending application or duplicate start");
            Require(result.PortChanged == shouldSwitch && result.EffectivePort == (shouldSwitch ? 58131 : 58000), "Actual port result");
            if (!result.Operation.Succeeded)
                Require(result.Operation.ErrorCode == "WEB_PORT_UNAVAILABLE" &&
                    result.Operation.Snapshot.IsQuiescent && result.Operation.Snapshot.State == LauncherState.NotStarted,
                    "Refused start must publish fresh stopped observation without starting");
            if (result.Operation.Succeeded) await host.Runtime.StopAsync();
        }

        using (var host = new Host(true, false, true))
        {
            host.Service.Fail = true;
            var failed = await host.Runtime.StartAsync();
            Require(!failed.Operation.Succeeded && host.AutomaticStarts == 0, "Generic startup failure must not pick a port");
            host.Service.Fail = false;
            Require((await host.Runtime.StartAsync()).Operation.Succeeded, "Failure releases gate for retry");
            await host.Runtime.StopAsync();
        }

        using (var host = new Host(true, false, true))
        {
            host.ProbeFailure = true;
            await Reject(() => host.Runtime.StartAsync());
            Require(host.Service.Starts == 0 && host.AutomaticStarts == 0, "Probe error is not a binding conflict");
            host.ProbeFailure = false;
            host.Service.State = ComponentRuntimeState.Unknown;
            await Reject(() => host.Runtime.StartAsync());
            Require(host.Probes == 1, "Unknown ownership must be refused before port probe");
            host.Service.State = ComponentRuntimeState.Stopped;
        }

        using (var host = new Host(false, false, true))
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Service.WaitForCancellation = entered;
            var starting = host.Runtime.StartAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Reject(() => host.Runtime.StartAsync());
            await Reject(() => host.Runtime.StopAsync());
            Require(host.Coordinator.Snapshot.SafeToCancel && host.Coordinator.RequestStartupCancellation(),
                "Runtime must preserve safe startup cancellation");
            var result = await starting.WaitAsync(TimeSpan.FromSeconds(3));
            Require(result.Operation.Cancelled && result.Operation.Snapshot.IsQuiescent, "Cancellation leaves no service");
            host.Service.WaitForCancellation = null;
            Require((await host.Runtime.StartAsync()).Operation.Succeeded, "Cancelled runtime can restart");
            await host.Runtime.StopAsync();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await host.Runtime.StartAsync(cancelled.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Require((await host.Coordinator.ShutdownAsync()).Succeeded, "Shutdown after cancellation");
            await Reject(() => host.Runtime.StartAsync());
        }
        Console.WriteLine("LAUNCHER_RUNTIME_PASS");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected refusal");
    }

    private sealed class Host : IDisposable
    {
        public Component Service { get; } = new();
        public LauncherCoordinator Coordinator { get; }
        public LauncherRuntime Runtime { get; }
        public int AutomaticStarts { get; private set; }
        public int Probes { get; private set; }
        public bool ProbeFailure { get; set; }
        public Host(bool automatic, bool pending, bool available)
        {
            Coordinator = new LauncherCoordinator([new(Service)], false);
            Runtime = new LauncherRuntime(Coordinator, (action, token) =>
                Coordinator.RunMaintenanceAsync(session =>
                    action(new Session(this, session, automatic, pending, available)), token));
        }
        public void Dispose() => Coordinator.Dispose();
        private sealed class Session(Host host, LauncherCoordinator.MaintenanceSession session,
            bool automatic, bool pending, bool available) : ILauncherStartSession
        {
            public LauncherStartSettings Settings => new(58000, automatic, pending);
            public bool CanBindEffectivePort()
            {
                host.Probes++;
                if (host.ProbeFailure) throw new InvalidOperationException("Synthetic probe failure");
                return available;
            }
            public async Task<LauncherStartResult> StartCurrentAsync(CancellationToken token) =>
                new(await session.StartAsync(token), 58000, false, false);
            public async Task<LauncherStartResult> StartOnAutomaticPortAsync(CancellationToken token)
            {
                host.AutomaticStarts++;
                return new(await session.StartAsync(token), 58131, true, false);
            }
        }
    }
    private sealed class Component : ILauncherComponent
    {
        public string Id => "web";
        public string DisplayName => "Web";
        public ComponentRuntimeState State { get; set; } = ComponentRuntimeState.Stopped;
        public int Starts { get; private set; }
        public bool Fail { get; set; }
        public TaskCompletionSource? WaitForCancellation { get; set; }
        public ValueTask<ComponentRuntimeState> GetStateAsync(CancellationToken token) => ValueTask.FromResult(State);
        public async Task StartAsync(CancellationToken token)
        {
            Starts++;
            if (Fail) throw new InvalidOperationException("Synthetic start failure");
            if (WaitForCancellation is { } entered)
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            State = ComponentRuntimeState.Running;
        }
        public Task StopAsync(CancellationToken token)
        { State = ComponentRuntimeState.Stopped; return Task.CompletedTask; }
    }
}
