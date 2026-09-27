using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AiGoofish.Launcher.App;
using AiGoofish.Launcher.Platform.Windows;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Skia;
using Avalonia.Threading;

namespace AiGoofish.Launcher.Ui.Smoke;

internal static class AutomaticWebPortHostAcceptance
{
    private sealed class Preferences : ILauncherPreferencesStore
    {
        public LauncherPreferences Load() => new(OpenManagementPage: false, AutoStartOnLauncherOpen: true);
        public bool TrySave(LauncherPreferences preferences) => true;
        public string? LoadWarning => null;
    }

    public static async Task<int> RunAsync(string packagedRoot)
    {
        var repository = Path.GetFullPath(Environment.GetEnvironmentVariable("AIGOOFISH_REPOSITORY_ROOT")
            ?? throw new InvalidOperationException("Explicit repository root required"));
        var parent = Path.Combine(repository, ".tmp", "tests", "automatic-port-ui");
        var fixture = Path.Combine(parent, "中文 空格-" + Guid.NewGuid().ToString("N"));
        MainWindowViewModel? vm = null;
        TcpListener? occupant = null;
        var defaultPortAccessDenied = false;
        var success = false;
        var stopped = false;
        var stage = "preflight";
        try
        {
            stage = "preflight-path";
            EnsurePlainPath(parent);
            stage = "preflight-disk";
            var disk = new DriveInfo(Path.GetPathRoot(parent)!);
            if (disk.AvailableFreeSpace - 256L * 1024 * 1024 < Math.Max(10L * 1024 * 1024 * 1024, disk.TotalSize * .05))
                throw new IOException("Disk floor");
            stage = "preflight-bundle";
            var bundle = await PortableBundleDescriptor.LoadAndVerifyAsync(Path.GetFullPath(packagedRoot));
            stage = "preflight-postgres-port";
            using (var pg = Occupy(PortableInstanceCatalog.DefaultPostgresPort)) { pg.Stop(); }
            stage = "preflight-web-port";
            try { occupant = Occupy(PortableInstanceCatalog.DefaultWebPort); }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.AccessDenied)
            {
                defaultPortAccessDenied = true;
            }
            stage = "preflight-fixture";
            Directory.CreateDirectory(fixture);
            bundle = bundle with { BundleRoot = fixture };
            stage = "allocation-refusal-guards";
            await CheckAllocationRefusalsAsync(bundle, fixture);
            stage = "allocated-close-before-initialization";
            await using (var allocated = RealPortableStackHost.Create(bundle))
            {
                Assert(allocated.PostgresProcessIdentity is null && allocated.PythonProcessIdentity is null,
                    "Allocation must not start services");
            }
            var pgData = Path.Combine(fixture, "data", "postgres", "cluster");
            var secretsPath = Path.Combine(fixture, "data", "config", "instance-secrets.dpapi");
            var lockPath = Path.Combine(fixture, "data", InstanceDataRootLease.LockFileName);
            var initialSecrets = File.ReadAllBytes(secretsPath);
            var initialIdentity = File.ReadAllBytes(lockPath);
            Assert(!Directory.Exists(pgData), "Closing allocated Host must not initialize PostgreSQL");
            AppBuilder.Configure<AiGoofish.Launcher.App.App>().UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            vm = new MainWindowViewModel(false, fixture, bundle, null, null, preferencesStore: new Preferences());
            stage = "allocated-reopen";
            Pump(vm.InitializeAsync());
            Assert(vm.IsPrimaryEnabled && !vm.IsStopEnabled && vm.PrimaryButtonText == "一键启动",
                "Allocated instance must be manually startable after reopen");
            Pump(vm.TryAutoStartVerifiedStoppedExistingAsync());
            Assert(!Directory.Exists(pgData), "Allocated instance must not use initialized-instance autostart");
            var allocatedShutdown = vm.ShutdownAsync();
            Pump(allocatedShutdown);
            Assert(allocatedShutdown.Result, "Allocated reopen must safely release lease");
            Pump(vm.DisposeAsync().AsTask());
            Assert(File.ReadAllBytes(lockPath).SequenceEqual(initialIdentity), "Allocated reopen must preserve instance identity");
            vm = new MainWindowViewModel(false, fixture, bundle, null, null, preferencesStore: new Preferences());
            Pump(vm.InitializeAsync());
            Assert(File.ReadAllBytes(secretsPath).SequenceEqual(initialSecrets), "Reopen must preserve secrets");
            using var originalPreview = vm.CreateDiagnosticPreviewTicket();
            var stableCoordinator = originalPreview.Coordinator ?? throw new IOException("Missing runtime coordinator");
            var retainedLog = stableCoordinator.Logs.First();
            var runningEvents = 0;
            stableCoordinator.SnapshotChanged += (_, snapshot) =>
            {
                if (snapshot.State == AiGoofish.Launcher.Core.LauncherState.Running) runningEvents++;
            };
            stage = "first-auto-start";
            Pump(vm.RunPrimaryActionAsync());
            AssertRunning(vm, stage);
            Assert(File.ReadAllBytes(secretsPath).SequenceEqual(initialSecrets), "First start must not replace allocated secrets");
            Assert(stableCoordinator.Snapshot.Components.Count == 2 &&
                stableCoordinator.Snapshot.StartupSteps.Count == 2 &&
                stableCoordinator.Snapshot.StartupSteps.All(step => step.State == AiGoofish.Launcher.Core.StartupStepState.Completed) &&
                vm.Components.Single(row => row.Id == "postgres-initialize").Status == "已完成" &&
                vm.Components.Single(row => row.Id == "database-provision").Status == "已完成",
                "Provision must be a completed step, not a running service");
            var provisionPath = Path.Combine(fixture, "data", "config", "postgres-provision.json");
            var provisionRecord = File.ReadAllBytes(provisionPath);
            var initializationPath = Path.Combine(fixture, "data", "launcher", "initialization.json");
            using (var initialization = JsonDocument.Parse(File.ReadAllBytes(initializationPath)))
                Assert(initialization.RootElement.GetProperty("phase").GetString() == "InitializationStarted",
                    "First start must consume the durable allocation permission");
            var firstPort = new Uri(vm.ManagementUrl).Port;
            Assert(firstPort != PortableInstanceCatalog.DefaultWebPort &&
                (defaultPortAccessDenied || occupant?.Server.IsBound == true),
                "New instance did not avoid the owned synthetic occupant");
            Assert(vm.AutomaticWebPort && ReadPort(fixture) == firstPort, "Automatic selection was not committed");
            occupant?.Stop(); occupant = null;
            stage = "same-window-restart";
            Pump(vm.RunStopActionAsync());
            Assert(stableCoordinator.Snapshot.IsQuiescent &&
                stableCoordinator.Snapshot.StartupSteps.All(step => step.State == AiGoofish.Launcher.Core.StartupStepState.Completed),
                "Stop must preserve completed preparation state");
            Pump(vm.RunPrimaryActionAsync());
            AssertRunning(vm, stage);
            Assert(File.ReadAllBytes(provisionPath).SequenceEqual(provisionRecord), "Restart must verify, not rerun provision");
            Assert(!vm.BusinessConnection.Status.Contains("端口即将切换", StringComparison.Ordinal),
                "Ordinary startup must not retain port-maintenance status");
            Assert(new Uri(vm.ManagementUrl).Port == firstPort, "Free previous port was not reused");
            stage = "completed-instance-missing-cluster-marker";
            Pump(vm.RunStopActionAsync());
            var clusterMarker = Path.Combine(pgData, ".aigoofish-cluster.json");
            var retainedMarker = Path.Combine(fixture, "retained-cluster-marker.json");
            var pgRuntime = Path.Combine(fixture, "data", "config", "postgres-runtime.json");
            var stoppedRuntime = File.ReadAllBytes(pgRuntime);
            File.Move(clusterMarker, retainedMarker);
            Pump(vm.RunPrimaryActionAsync());
            Assert(vm.ServiceState != "组件运行中" && !File.Exists(clusterMarker) &&
                File.ReadAllBytes(pgRuntime).SequenceEqual(stoppedRuntime), "Missing marker must not recreate or start PostgreSQL");
            File.Move(retainedMarker, clusterMarker);
            Pump(vm.RunStopActionAsync());
            stage = "fixed-conflict";
            vm.AutomaticWebPort = false;
            vm.WebPortInput = firstPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Pump(vm.StageWebPortAsync());
            occupant = Occupy(firstPort);
            Pump(vm.RunPrimaryActionAsync());
            Assert(vm.ServiceState == "组件已停止" && vm.HasWebPortError && occupant.Server.IsBound &&
                ReadPort(fixture) == firstPort, "Fixed conflict changed address or started services");
            stage = "existing-auto-conflict";
            vm.AutomaticWebPort = true;
            Pump(vm.StageWebPortAsync());
            stage = "automatic-start-cancellation";
            void CancelCandidate(object? sender, AiGoofish.Launcher.Core.LauncherSnapshot snapshot)
            {
                if (snapshot.Phase == "start:python-web")
                    stableCoordinator.RequestStartupCancellation();
            }
            stableCoordinator.SnapshotChanged += CancelCandidate;
            try { Pump(vm.RunPrimaryActionAsync()); }
            finally { stableCoordinator.SnapshotChanged -= CancelCandidate; }
            Assert(stableCoordinator.Snapshot.IsQuiescent && ReadPort(fixture) == firstPort &&
                occupant.Server.IsBound && vm.PhaseMessage.Contains("已取消", StringComparison.Ordinal) &&
                vm.IsPrimaryEnabled, "Automatic cancellation must rollback and remain retryable");
            Assert(File.ReadAllBytes(provisionPath).SequenceEqual(provisionRecord),
                "Cancelled retry must not rerun provision");
            stage = "existing-auto-conflict";
            Pump(vm.RunPrimaryActionAsync());
            AssertRunning(vm, stage);
            Assert(new Uri(vm.ManagementUrl).Port != firstPort && occupant.Server.IsBound &&
                ReadPort(fixture) == new Uri(vm.ManagementUrl).Port, "Existing automatic mode failed");
            stage = "manual-candidate-rollback";
            var committedPort = new Uri(vm.ManagementUrl).Port;
            Pump(vm.RunStopActionAsync());
            vm.AutomaticWebPort = false;
            vm.WebPortInput = firstPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Pump(vm.StageWebPortAsync());
            Pump(vm.ApplyPendingWebPortAsync());
            AssertRunning(vm, stage);
            Assert(new Uri(vm.ManagementUrl).Port == committedPort && occupant.Server.IsBound &&
                vm.HasPendingWebPort && ReadPort(fixture) == committedPort,
                "Failed candidate must restore old Ready endpoint and retain pending selection");
            stage = "manual-candidate-commit";
            Pump(vm.RunStopActionAsync());
            int candidate;
            using (var probe = Occupy(0)) { candidate = ((IPEndPoint)probe.LocalEndpoint).Port; }
            vm.WebPortInput = candidate.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Pump(vm.StageWebPortAsync());
            Pump(vm.ApplyPendingWebPortAsync());
            AssertRunning(vm, stage);
            Assert(new Uri(vm.ManagementUrl).Port == candidate && ReadPort(fixture) == candidate && !vm.HasPendingWebPort,
                "Manual candidate must commit after Ready");
            using (var currentPreview = vm.CreateDiagnosticPreviewTicket())
                Assert(ReferenceEquals(currentPreview.Coordinator, stableCoordinator) &&
                    currentPreview.CoordinatorGeneration != originalPreview.CoordinatorGeneration,
                    "Coordinator identity must remain stable while preview epoch changes");
            Assert(stableCoordinator.Logs.Contains(retainedLog) && runningEvents >= 5,
                "Original subscription and earlier logs must survive automatic changes, rollback and manual commit");
            stage = "all-stopped-reopen";
            Pump(vm.RunStopActionAsync());
            stage = "all-stopped-shutdown";
            var shutdown = vm.ShutdownAsync();
            Pump(shutdown);
            Assert(shutdown.GetAwaiter().GetResult(), "Stopped Host did not safely release its lease");
            stage = "all-stopped-dispose";
            Pump(vm.DisposeAsync().AsTask());
            Assert(File.ReadAllBytes(lockPath).SequenceEqual(initialIdentity), "Startup must preserve allocated instance identity");
            // This synthetic completed instance now models a legacy installation
            // with no allocation journal. Keep the evidence rather than erase it.
            File.Move(initializationPath, initializationPath + ".legacy-fixture");
            vm = new MainWindowViewModel(false, fixture, bundle, null, null, preferencesStore: new Preferences());
            stage = "all-stopped-initialize";
            Pump(vm.InitializeAsync());
            stage = "all-stopped-state";
            Assert(vm.ServiceState == "组件已停止" && vm.IsPrimaryEnabled && !vm.IsStopEnabled &&
                vm.PrimaryButtonText == "一键启动", "Reopened stopped instance was not manually startable");
            stage = "all-stopped-start";
            Pump(vm.RunPrimaryActionAsync());
            stage = "all-stopped-running";
            AssertRunning(vm, stage);
            stage = "all-stopped-stop";
            Pump(vm.RunStopActionAsync());
            stage = "all-stopped-final-state";
            Assert(vm.ServiceState == "组件已停止", "Reopened instance did not stop normally");
            success = true;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"AUTO_PORT_UI_FAILURE stage={stage} category={error.GetType().Name}");
            if (Directory.Exists(fixture))
            {
                try { File.WriteAllText(Path.Combine(fixture, "FAILURE.txt"),
                    $"Stage: {stage}\nException: {error.GetType().Name}\n", new UTF8Encoding(false)); }
                catch (IOException) { Console.Error.WriteLine("AUTO_PORT_UI_RECORD=FAILED"); }
                catch (UnauthorizedAccessException) { Console.Error.WriteLine("AUTO_PORT_UI_RECORD=DENIED"); }
            }
        }
        finally
        {
            occupant?.Stop();
            if (vm is not null)
            {
                try
                {
                    var shutdown = vm.ShutdownAsync();
                    Pump(shutdown, TimeSpan.FromSeconds(60));
                    stopped = shutdown.GetAwaiter().GetResult();
                    if (stopped) Pump(vm.DisposeAsync().AsTask(), TimeSpan.FromSeconds(60));
                }
                catch (Exception error) { Console.Error.WriteLine("AUTO_PORT_UI_CLEANUP=" + error.GetType().Name); }
            }
            if (success && stopped)
            {
                EnsurePlainPath(fixture);
                if (Path.GetDirectoryName(Path.GetFullPath(fixture)) != parent) throw new IOException("Cleanup boundary");
                foreach (var path in Directory.EnumerateFileSystemEntries(fixture, "*", SearchOption.AllDirectories))
                    EnsurePlainPath(path);
                Directory.Delete(fixture, true);
            }
            else if (Directory.Exists(fixture)) Console.Error.WriteLine("AUTO_PORT_UI_FIXTURE_RETAINED=YES");
        }
        Console.WriteLine("AUTO_PORT_REAL_UI=" + (success && stopped ? "PASS" : "FAILED"));
        return success && stopped ? 0 : 1;
    }

    public static async Task<int> RunReopenProbeAsync(string packagedRoot, string existingFixture)
    {
        var repository = Path.GetFullPath(Environment.GetEnvironmentVariable("AIGOOFISH_REPOSITORY_ROOT")
            ?? throw new InvalidOperationException("Explicit repository root required"));
        var parent = Path.Combine(repository, ".tmp", "tests", "automatic-port-ui");
        var fixture = Path.GetFullPath(existingFixture);
        MainWindowViewModel? vm = null;
        var success = false;
        var stage = "preflight";
        try
        {
            EnsurePlainPath(fixture);
            if (!string.Equals(Path.GetDirectoryName(fixture), parent, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(fixture)) throw new IOException("Probe fixture boundary or existence failed");
            var bundle = await PortableBundleDescriptor.LoadAndVerifyAsync(Path.GetFullPath(packagedRoot));
            bundle = bundle with { BundleRoot = fixture };
            AppBuilder.Configure<AiGoofish.Launcher.App.App>().UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            vm = new MainWindowViewModel(false, fixture, bundle, null, null, preferencesStore: new Preferences());
            stage = "all-stopped-initialize";
            Pump(vm.InitializeAsync());
            stage = "all-stopped-state";
            Assert(vm.ServiceState == "组件已停止" && vm.IsPrimaryEnabled, "Probe fixture not safely stopped");
            stage = "all-stopped-start";
            Pump(vm.RunPrimaryActionAsync());
            stage = "all-stopped-running";
            AssertRunning(vm, stage);
            stage = "all-stopped-stop";
            Pump(vm.RunStopActionAsync());
            success = vm.ServiceState == "组件已停止";
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"AUTO_PORT_UI_FAILURE stage={stage} category={error.GetType().Name}");
        }
        finally
        {
            if (vm is not null)
            {
                try
                {
                    var shutdown = vm.ShutdownAsync();
                    Pump(shutdown, TimeSpan.FromSeconds(60));
                    if (shutdown.GetAwaiter().GetResult()) Pump(vm.DisposeAsync().AsTask(), TimeSpan.FromSeconds(60));
                    else success = false;
                }
                catch (Exception error)
                {
                    success = false;
                    Console.Error.WriteLine("AUTO_PORT_UI_CLEANUP=" + error.GetType().Name);
                }
            }
        }
        Console.WriteLine("AUTO_PORT_UI_PROBE=" + (success ? "PASS" : "FAILED"));
        return success ? 0 : 1;
    }

    private static async Task CheckAllocationRefusalsAsync(PortableBundleDescriptor bundle, string fixture)
    {
        foreach (var kind in new[] { "init-progress", "provision-progress", "runtime", "empty-pgdata", "started", "legacy-incomplete" })
        {
            var isolated = bundle with { BundleRoot = Path.Combine(fixture, "refusal-" + kind) };
            Directory.CreateDirectory(isolated.BundleRoot);
            await using (var host = RealPortableStackHost.Create(isolated))
                Assert(host.PostgresProcessIdentity is null && host.PythonProcessIdentity is null, "Guard fixture must not start processes");
            var data = Path.Combine(isolated.BundleRoot, "data");
            var initialization = Path.Combine(data, "launcher", "initialization.json");
            var secrets = Path.Combine(data, "config", "instance-secrets.dpapi");
            var protectedBytes = File.ReadAllBytes(secrets);
            switch (kind)
            {
                case "init-progress":
                    File.WriteAllText(Path.Combine(data, "config", "postgres-initialization.json"), "{}", new UTF8Encoding(false));
                    break;
                case "provision-progress":
                    File.WriteAllText(Path.Combine(data, "config", "postgres-provision-progress.json"), "{}", new UTF8Encoding(false));
                    break;
                case "runtime":
                    File.WriteAllText(Path.Combine(data, "config", "python-runtime.json"), "{}", new UTF8Encoding(false));
                    break;
                case "empty-pgdata":
                    Directory.CreateDirectory(Path.Combine(data, "postgres", "cluster"));
                    break;
                case "started":
                    File.WriteAllText(initialization, File.ReadAllText(initialization).Replace("Allocated", "InitializationStarted"), new UTF8Encoding(false));
                    break;
                case "legacy-incomplete":
                    File.Move(initialization, Path.Combine(isolated.BundleRoot, "original-initialization.json"));
                    break;
            }
            var evidence = Directory.GetFiles(data, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllBytes);
            var refused = false;
            try { await using var unexpected = RealPortableStackHost.OpenExistingForRecovery(isolated); }
            catch (Exception exception) when (exception is PythonLifecycleException or IOException or InvalidOperationException)
            { refused = true; }
            Assert(refused, "Unverified initialization must not open as allocated");
            Assert(Directory.GetFiles(data, "*", SearchOption.AllDirectories).Length == evidence.Count &&
                evidence.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "Refusal must preserve all fixture evidence");
            Assert(File.ReadAllBytes(secrets).SequenceEqual(protectedBytes), "Refusal must preserve credentials");
            using var lease = InstanceDataRootLease.Acquire(data);
            Assert(lease.IsHeld, "Failed open must release its classification lease");
        }
    }

    private static int ReadPort(string fixture)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture, "data", "launcher", "web-port.json")));
        Assert(json.RootElement.GetProperty("applying").ValueKind == JsonValueKind.Null, "Unresolved intent");
        return json.RootElement.GetProperty("effective_port").GetInt32();
    }

    private static TcpListener Occupy(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Server.ExclusiveAddressUse = true; listener.Start(); return listener; }
        catch { listener.Stop(); throw; }
    }

    private static void AssertRunning(MainWindowViewModel vm, string stage)
    {
        var running = vm.ServiceState == "组件运行中";
        if (!running || !vm.SetupRequired || !vm.IsStopEnabled || !vm.CanCopySetupToken)
        {
            Console.Error.WriteLine($"AUTO_PORT_UI_RUNNING stage={stage} running={(running ? 1 : 0)} " +
                $"setup={(vm.SetupRequired ? 1 : 0)} stop={(vm.IsStopEnabled ? 1 : 0)} " +
                $"copy={(vm.CanCopySetupToken ? 1 : 0)}");
            EmitSafeDiagnostic(vm);
        }
        Assert(running && vm.SetupRequired && vm.IsStopEnabled && vm.CanCopySetupToken,
            "Production view model did not reach initialized Ready state");
    }

    private static void EmitSafeDiagnostic(MainWindowViewModel vm)
    {
        try
        {
            using var report = JsonDocument.Parse(vm.CreateDiagnosticPreview());
            var root = report.RootElement;
            Console.Error.WriteLine("AUTO_PORT_UI_DIAG_STATE=" + root.GetProperty("state").GetString());
            foreach (var component in root.GetProperty("components").EnumerateArray())
                Console.Error.WriteLine("AUTO_PORT_UI_DIAG_COMPONENT id=" + component.GetProperty("id").GetString() +
                    " state=" + component.GetProperty("state").GetString());
            foreach (var item in root.GetProperty("events").EnumerateArray())
                Console.Error.WriteLine("AUTO_PORT_UI_DIAG_EVENT component=" + item.GetProperty("component").GetString() +
                    " code=" + item.GetProperty("code").GetString());
        }
        catch (Exception error) when (error is InvalidOperationException or JsonException or KeyNotFoundException)
        {
            Console.Error.WriteLine("AUTO_PORT_UI_DIAG_UNAVAILABLE");
        }
    }

    private static void Pump(Task task, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromMinutes(3));
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("UI operation deadline");
            Thread.Sleep(20);
        }
        Dispatcher.UIThread.RunJobs();
        task.GetAwaiter().GetResult();
    }

    private static void Assert(bool condition, string reason) { if (!condition) throw new IOException(reason); }

    private static void EnsurePlainPath(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse point");
    }
}
