using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiGoofish.Launcher.App;
using AiGoofish.Launcher.Core;
using AiGoofish.Launcher.Platform.Windows;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Skia;
using Avalonia.Threading;

internal static class HostRecoveryAcceptance
{
    private sealed record SeedReceipt(OwnedProcessIdentity Postgres, OwnedProcessIdentity Python, string SetupTokenSha256);

    private sealed class Preferences : ILauncherPreferencesStore
    {
        public LauncherPreferences Load() => new(OpenManagementPage: false);
        public bool TrySave(LauncherPreferences preferences) => true;
        public string? LoadWarning => null;
    }

    private static MainWindowViewModel NewVm(string fixture, PortableBundleDescriptor bundle) =>
        new(false, fixture, bundle, null, null, preferencesStore: new Preferences());

    public static async Task<int> ProbeHostOnlyAsync(string packagedBundleRoot, string fixture, bool stop = false)
    {
        try
        {
            var verified = await PortableBundleDescriptor.LoadAndVerifyAsync(Path.GetFullPath(packagedBundleRoot));
            var bundle = verified with { BundleRoot = Path.GetFullPath(fixture) };
            Console.WriteLine("HOST_ONLY_OPEN_BEGIN");
            var host = RealPortableStackHost.OpenExistingForRecovery(bundle);
            Console.WriteLine("HOST_ONLY_RECOVER_BEGIN");
            var result = await host.TryRecoverExistingAsync().WaitAsync(TimeSpan.FromSeconds(45));
            Console.WriteLine($"HOST_ONLY_RESULT={result.Succeeded} STATE={result.Snapshot.State} CODE={result.ErrorCode}");
            if (result.Succeeded)
            {
                var receipt = JsonSerializer.Deserialize<SeedReceipt>(
                    await File.ReadAllTextAsync(Path.Combine(fixture, "recovery-receipt.json"), Encoding.UTF8))!;
                Assert(host.PostgresProcessIdentity == receipt.Postgres && host.PythonProcessIdentity == receipt.Python,
                    "Host 单独恢复的原身份不一致。");
                Assert(host.SetupToken is { } token &&
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))) == receipt.SetupTokenSha256,
                    "Host 单独恢复的设置口令不一致。");
                if (stop)
                {
                    var stopped = await host.Coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
                    if (!stopped.Succeeded) throw new IOException($"Host 停机失败：{stopped.ErrorCode}");
                    AssertNotLiveIdentity(receipt.Postgres);
                    AssertNotLiveIdentity(receipt.Python);
                    await host.DisposeAsync();
                    Console.WriteLine("HOST_ONLY_STOP=PASS");
                }
            }
            // Exit the test process without disposing a live host; the next window must reacquire the OS lease.
            return result.Succeeded ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"HOST_ONLY_FAILED category={error.GetType().Name} message={error.Message}");
            return 1;
        }
    }

    public static async Task<int> StartStoppedHostOnlyAsync(string packagedBundleRoot, string fixture)
    {
        try
        {
            var verified = await PortableBundleDescriptor.LoadAndVerifyAsync(Path.GetFullPath(packagedBundleRoot));
            var bundle = verified with { BundleRoot = Path.GetFullPath(fixture) };
            var receipt = JsonSerializer.Deserialize<SeedReceipt>(
                await File.ReadAllTextAsync(Path.Combine(fixture, "recovery-receipt.json"), Encoding.UTF8))
                ?? throw new IOException("原进程身份收据缺失。");
            AssertNotLiveIdentity(receipt.Postgres);
            AssertNotLiveIdentity(receipt.Python);
            var markerPath = Path.Combine(fixture, "data", "postgres", "cluster", ".aigoofish-cluster.json");
            var markerHash = SHA256.HashData(await File.ReadAllBytesAsync(markerPath));
            var host = RealPortableStackHost.OpenExistingForRecovery(bundle);
            var result = await host.StartStoppedExistingAsync().WaitAsync(TimeSpan.FromMinutes(3));
            Console.WriteLine($"HOST_RESTART_RESULT={result.Succeeded} STATE={result.Snapshot.State} CODE={result.ErrorCode}");
            if (!result.Succeeded) throw new IOException($"既有全停实例显式启动失败：{result.ErrorCode}");
            var newPostgres = host.PostgresProcessIdentity ?? throw new IOException("新 PostgreSQL 身份缺失。");
            var newPython = host.PythonProcessIdentity ?? throw new IOException("新 Python 身份缺失。");
            Assert(newPostgres != receipt.Postgres && newPython != receipt.Python, "重启不应接管旧进程身份。");
            AssertLiveIdentity(newPostgres);
            AssertLiveIdentity(newPython);
            var markerAfterRestart = SHA256.HashData(await File.ReadAllBytesAsync(markerPath));
            Assert(markerHash.SequenceEqual(markerAfterRestart),
                "重启不应重建或覆盖原集群标记。");
            var stopped = await host.Coordinator.StopAsync().WaitAsync(TimeSpan.FromMinutes(1));
            if (!stopped.Succeeded) throw new IOException($"重启后正常停止失败：{stopped.ErrorCode}");
            AssertNotLiveIdentity(newPostgres);
            AssertNotLiveIdentity(newPython);
            await host.DisposeAsync();
            Console.WriteLine("HOST_RESTART_STOP=PASS");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"HOST_RESTART_FAILED category={error.GetType().Name} message={error.Message}");
            return 1;
        }
    }

    public static async Task<int> SeedAsync(string packagedBundleRoot, string fixture)
    {
        try
        {
            var verified = await PortableBundleDescriptor.LoadAndVerifyAsync(Path.GetFullPath(packagedBundleRoot));
            var bundle = verified with { BundleRoot = Path.GetFullPath(fixture) };
            // The process exits after writing the receipt. Windows releases the lease while PG/Web stay alive.
            var host = RealPortableStackHost.Create(bundle);
            var started = await host.StartAsync();
            if (!started.Succeeded)
            {
                var code = started.ErrorCode is "START_FAILED" or "START_NOT_READY" or "START_CANCELLED"
                    ? started.ErrorCode : "OTHER";
                var states = started.Snapshot.Components.Select(component => component.State.ToString()).ToArray();
                if (states.Length == 3 && states.All(state => state is "Stopped" or "Running" or "Unknown" or "Failed" or "Starting" or "Stopping"))
                    Console.Error.WriteLine($"HOST_RECOVERY_SAFE_START code={code} states={string.Join('-', states)}");
                var diagnostics = host.Coordinator.DiagnosticEvents
                    .Where(item => item.Code is AiGoofish.Launcher.Core.LauncherDiagnosticCode.StartupComponentFailed or
                        AiGoofish.Launcher.Core.LauncherDiagnosticCode.DatabaseProvisionInterrupted)
                    .Select(item => item.Component.ToString() + '-' + item.Code.ToString());
                foreach (var diagnostic in diagnostics)
                    if (diagnostic is "PythonWeb-StartupComponentFailed" or "Postgres-StartupComponentFailed" or
                        "DatabaseProvision-StartupComponentFailed" or "DatabaseProvision-DatabaseProvisionInterrupted")
                        Console.Error.WriteLine($"HOST_RECOVERY_SAFE_COMPONENT value={diagnostic}");
            }
            if (!started.Succeeded || host.PostgresProcessIdentity is null || host.PythonProcessIdentity is null ||
                !host.SetupRequired || string.IsNullOrWhiteSpace(host.SetupToken))
            {
                throw new IOException("隔离实例未到达完整 Running/首次设置状态。");
            }

            var receipt = new SeedReceipt(
                host.PostgresProcessIdentity,
                host.PythonProcessIdentity,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host.SetupToken))));
            await File.WriteAllTextAsync(
                Path.Combine(fixture, "recovery-receipt.json"),
                JsonSerializer.Serialize(receipt),
                new UTF8Encoding(false));
            Console.WriteLine("HOST_RECOVERY_SEED_READY");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"HOST_RECOVERY_SEED_FAILED category={error.GetType().Name} message={error.Message}");
            Console.Error.WriteLine($"HOST_RECOVERY_SAFE_FAILURE stage=seed category={SafeFailureCategory(error)}");
            return 1;
        }
    }

    public static async Task<int> RunAsync(string packagedBundleRoot, string? existingFixture = null)
    {
        var repository = Environment.GetEnvironmentVariable("AIGOOFISH_REPOSITORY_ROOT")
            ?? throw new InvalidOperationException("需要显式 AIGOOFISH_REPOSITORY_ROOT 测试根。");
        var parent = Path.Combine(Path.GetFullPath(repository), ".tmp", "tests", "host-recovery");
        var fixture = existingFixture is null
            ? Path.Combine(parent, Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(existingFixture);
        var stage = "preflight";
        var success = false;
        MainWindowViewModel? currentVm = null;
        byte[]? savedRuntime = null;
        string? pgRuntimePath = null;
        byte[]? savedProvision = null;
        string? provisionPath = null;
        try
        {
            var disk = new DriveInfo(Path.GetPathRoot(fixture)!);
            if (disk.AvailableFreeSpace - 256L * 1024 * 1024 <
                Math.Max(10L * 1024 * 1024 * 1024, disk.TotalSize * .05))
            {
                throw new IOException("隔离 PG fixture 预计增长会越过低磁盘线。");
            }

            EnsurePlainPath(parent);
            if (!string.Equals(Path.GetDirectoryName(fixture), parent, StringComparison.OrdinalIgnoreCase))
                throw new IOException("恢复测试 fixture 不在专属目录内。");
            var verified = await PortableBundleDescriptor.LoadAndVerifyAsync(Path.GetFullPath(packagedBundleRoot));
            if (existingFixture is null) Directory.CreateDirectory(fixture);
            else if (!Directory.Exists(fixture)) throw new IOException("既有恢复 fixture 不存在。");
            var bundle = verified with { BundleRoot = fixture };
            if (existingFixture is null)
            {
            stage = "old-launcher-process";
            using (var child = new Process())
            {
                var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT")
                    ?? throw new IOException("测试必须指定隔离 .NET 10 DOTNET_ROOT。");
                var dotnetExecutable = Path.Combine(dotnetRoot, "dotnet.exe");
                if (!File.Exists(dotnetExecutable)) throw new IOException("隔离 .NET 10 执行文件不存在。");
                child.StartInfo = new ProcessStartInfo(dotnetExecutable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = repository,
                };
                child.StartInfo.ArgumentList.Add(typeof(HostRecoveryAcceptance).Assembly.Location);
                child.StartInfo.ArgumentList.Add("--host-recovery-seed");
                child.StartInfo.ArgumentList.Add(Path.GetFullPath(packagedBundleRoot));
                child.StartInfo.ArgumentList.Add(fixture);
                if (!child.Start()) throw new IOException("旧 Launcher 测试进程未启动。");
                Console.Error.WriteLine("HOST_RECOVERY_STAGE=seed-process-started");
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4));
                Console.Error.WriteLine("HOST_RECOVERY_STAGE=seed-process-exited");
                if (child.ExitCode != 0 || !File.Exists(Path.Combine(fixture, "recovery-receipt.json")))
                {
                    throw new IOException($"旧 Launcher 启动失败，exit={child.ExitCode}。");
                }
            }
            }

            var receipt = JsonSerializer.Deserialize<SeedReceipt>(
                await File.ReadAllTextAsync(Path.Combine(fixture, "recovery-receipt.json"), Encoding.UTF8))
                ?? throw new IOException("旧 Launcher 身份收据缺失。");
            AssertLiveIdentity(receipt.Postgres);
            AssertLiveIdentity(receipt.Python);
            Console.WriteLine($"PASS old launcher exited; original PG PID={receipt.Postgres.ProcessId}, Python PID={receipt.Python.ProcessId} remain alive");

            stage = "new-window-recovery";
            Console.Error.WriteLine("HOST_RECOVERY_STAGE=new-window-setup");
            Console.WriteLine("UI_SETUP_BEGIN");
            AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            Console.WriteLine("UI_SETUP_DONE");
            Console.Error.WriteLine("HOST_RECOVERY_STAGE=new-window-vm");
            currentVm = NewVm(fixture, bundle);
            var vm = currentVm;
            Console.Error.WriteLine("HOST_RECOVERY_STAGE=new-window-init");
            PumpTask(vm.InitializeAsync(), TimeSpan.FromSeconds(50), "新 Launcher 恢复未结束");
            Console.Error.WriteLine("HOST_RECOVERY_STAGE=new-window-initialized");
            PumpUntil(() => vm.ServiceState is "组件运行中" or "已有实例恢复被拒绝" or "发行包不可用",
                TimeSpan.FromSeconds(50), "新窗口恢复未结束");
            Assert(vm.ServiceState == "组件运行中", $"新窗口未显示 Running：{vm.ServiceState} / {vm.PhaseMessage}");
            Assert(vm.SetupRequired && vm.CanOpenSetup, "新窗口未恢复首次设置提示/打开入口。");
            Assert(vm.IsStopEnabled && vm.PrimaryButtonText == "打开管理页", "新窗口未显示运行控制。");
            AssertLiveIdentity(receipt.Postgres);
            AssertLiveIdentity(receipt.Python);
            Assert(ReadRuntimeIdentity(fixture, "postgres-runtime.json") == receipt.Postgres &&
                ReadRuntimeIdentity(fixture, "python-runtime.json") == receipt.Python,
                "恢复期间原进程身份被替换。");
            AssertLocked(bundle);
            Console.WriteLine("PASS new headless window auto-recovered original PG/Web identities, Ready and setup token");

            stage = "normal-stop";
            PumpTask(vm.RunStopActionAsync(), TimeSpan.FromMinutes(2), "恢复后正常停止未结束");
            Assert(vm.ServiceState == "组件已停止" && vm.IsPrimaryEnabled && !vm.IsStopEnabled,
                $"恢复后未进入已停止/手动启动状态：{vm.ServiceState} / {vm.PhaseMessage}");
            AssertNotLiveIdentity(receipt.Postgres);
            AssertNotLiveIdentity(receipt.Python);
            Console.WriteLine("PASS recovered window normal Stop confirmed both original processes exited");

            stage = "same-window-restart";
            PumpTask(vm.RunPrimaryActionAsync(), TimeSpan.FromMinutes(3), "同一窗口停止后重新启动未结束");
            PumpUntil(() => vm.ServiceState is "组件运行中" or "已有实例恢复被拒绝" or "发行包不可用",
                TimeSpan.FromSeconds(50), "同一窗口停止后重新启动未结束");
            Assert(vm.ServiceState == "组件运行中" && vm.IsStopEnabled,
                $"同一窗口重启未进入运行状态：{vm.ServiceState} / {vm.PhaseMessage}");
            var restartedPostgres = ReadRuntimeIdentity(fixture, "postgres-runtime.json");
            AssertLiveIdentity(restartedPostgres);
            PumpTask(vm.RunStopActionAsync(), TimeSpan.FromMinutes(2), "同一窗口重启后停止未结束");
            Assert(vm.ServiceState == "组件已停止", $"同一窗口重启后停止未完成：{vm.ServiceState}");
            Console.WriteLine("PASS same-window normal Stop -> one-click Start -> Stop");

            Close(currentVm);
            currentVm = null;

            stage = "all-stopped-reopen";
            currentVm = NewVm(fixture, bundle);
            vm = currentVm;
            PumpTask(vm.InitializeAsync(), TimeSpan.FromSeconds(45), "全部停止后重开未结束");
            PumpTask(vm.RefreshStatusAsync(), TimeSpan.FromSeconds(15), "全部停止后状态采样未结束");
            PumpUntil(() => vm.ServiceState is "组件已停止" or "已有实例恢复被拒绝" or "发行包不可用",
                TimeSpan.FromSeconds(45), "全部停止后重开未结束");
            PumpUntil(() => vm.FreshnessText.StartsWith("组件采样 ", StringComparison.Ordinal),
                TimeSpan.FromSeconds(15), "全部停止后自动状态采样未结束");
            Assert(vm.ServiceState == "组件已停止" && vm.IsPrimaryEnabled && !vm.IsStopEnabled &&
                vm.PrimaryButtonText == "一键启动", "全停止实例应只显示可手动启动。");
            PumpTask(vm.RunPrimaryActionAsync(), TimeSpan.FromMinutes(3), "全停重开后手动启动未结束");
            Assert(vm.ServiceState == "组件运行中" && vm.IsStopEnabled,
                $"全停重开后未能从 UI 启动：{vm.ServiceState} / {vm.PhaseMessage}");
            PumpTask(vm.RunStopActionAsync(), TimeSpan.FromMinutes(2), "全停重开后正常停止未结束");
            Assert(vm.ServiceState == "组件已停止", "全停重开后的服务未安全停止。");
            Close(currentVm);
            currentVm = null;
            Console.WriteLine("PASS all-stopped reopen allowed explicit UI start and normal stop");

            stage = "incomplete-provision";
            provisionPath = Path.Combine(fixture, "data", "config", "postgres-provision.json");
            savedProvision = File.ReadAllBytes(provisionPath);
            File.WriteAllText(provisionPath, "{invalid", new UTF8Encoding(false));
            currentVm = NewVm(fixture, bundle);
            vm = currentVm;
            PumpTask(vm.InitializeAsync(), TimeSpan.FromSeconds(45), "数据库准备记录损坏时恢复未结束");
            PumpUntil(() => vm.ServiceState is "已有实例恢复被拒绝" or "发行包不可用",
                TimeSpan.FromSeconds(45), "数据库准备记录损坏时恢复未结束");
            Assert(vm.ServiceState == "已有实例恢复被拒绝" && !vm.IsPrimaryEnabled,
                "数据库准备记录未核验时不得开放启动入口。");
            AssertLocked(bundle);
            File.WriteAllBytes(provisionPath, savedProvision);
            savedProvision = null;
            Close(currentVm);
            currentVm = null;
            Console.WriteLine("PASS incomplete provision record refused manual start");

            stage = "partial-pg-only";
            OwnedProcessIdentity partialPg;
            using (var lease = InstanceDataRootLease.Acquire(Path.Combine(fixture, "data")))
            {
                var secrets = new WindowsInstanceSecretsStore().Load(lease);
                var pg = new WindowsPostgresComponent(lease, secrets, new WindowsPostgresOptions(
                    bundle.PostgresRoot, Path.Combine(fixture, "data", "postgres", "cluster"), 55432,
                    "17.11", TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)));
                PumpTask(pg.EnsureInitializedAsync(), TimeSpan.FromMinutes(2), "部分状态 PG 初始化未结束");
                PumpTask(pg.StartAsync(CancellationToken.None), TimeSpan.FromMinutes(2), "部分状态 PG 启动未结束");
                partialPg = pg.ProcessSnapshot.Identity ?? throw new IOException("部分状态 PG 身份缺失。");
                PumpTask(pg.DisposeAsync().AsTask(), TimeSpan.FromSeconds(30), "部分状态 PG 释放未结束");
            }
            currentVm = NewVm(fixture, bundle);
            vm = currentVm;
            PumpTask(vm.InitializeAsync(), TimeSpan.FromSeconds(45), "部分状态恢复未结束");
            PumpUntil(() => vm.ServiceState is "已有实例恢复被拒绝" or "发行包不可用",
                TimeSpan.FromSeconds(45), "部分状态恢复未结束");
            Assert(vm.ServiceState == "已有实例恢复被拒绝" && !vm.IsPrimaryEnabled, "仅 PG 运行时不得启动第二套服务。");
            AssertLiveIdentity(partialPg);
            AssertLocked(bundle);
            Console.WriteLine("PASS partial PG-only state refused recovery/spawn and retained lock");
            PumpTask(vm.RunStopActionAsync(), TimeSpan.FromMinutes(1), "已核验 PG 正常停止未结束");
            AssertNotLiveIdentity(partialPg);
            Close(currentVm);
            currentVm = null;

            stage = "unknown-record";
            pgRuntimePath = Path.Combine(fixture, "data", "config", "postgres-runtime.json");
            savedRuntime = File.ReadAllBytes(pgRuntimePath);
            File.WriteAllText(pgRuntimePath, "{invalid", new UTF8Encoding(false));
            currentVm = NewVm(fixture, bundle);
            vm = currentVm;
            PumpTask(vm.InitializeAsync(), TimeSpan.FromSeconds(45), "未知身份恢复未结束");
            PumpUntil(() => vm.ServiceState is "已有实例恢复被拒绝" or "发行包不可用",
                TimeSpan.FromSeconds(45), "未知身份恢复未结束");
            Assert(vm.ServiceState == "已有实例恢复被拒绝" && !vm.IsPrimaryEnabled,
                "未知运行记录不得开放启动。");
            AssertLocked(bundle);
            AssertNotLiveIdentity(receipt.Postgres);
            AssertNotLiveIdentity(receipt.Python);
            Console.WriteLine("PASS unknown runtime record refused spawn/kill and retained lock");
            File.WriteAllBytes(pgRuntimePath, savedRuntime);
            savedRuntime = null;
            Close(currentVm);
            currentVm = null;

            success = true;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"HOST_RECOVERY_FAILED stage={stage} category={error.GetType().Name} message={error.Message}");
            Console.Error.WriteLine($"HOST_RECOVERY_SAFE_FAILURE stage={SafeFailureStage(stage)} category={SafeFailureCategory(error)}");
        }
        finally
        {
            if (savedProvision is not null && provisionPath is not null)
            {
                try { File.WriteAllBytes(provisionPath, savedProvision); }
                catch (Exception error) { Console.Error.WriteLine("HOST_PROVISION_RECORD_RESTORE_FAILED=" + error.GetType().Name); success = false; }
            }
            if (savedRuntime is not null && pgRuntimePath is not null)
            {
                try { File.WriteAllBytes(pgRuntimePath, savedRuntime); }
                catch (Exception error) { Console.Error.WriteLine("HOST_RECOVERY_RECORD_RESTORE_FAILED=" + error.GetType().Name); success = false; }
            }
            if (currentVm is not null)
            {
                Console.Error.WriteLine("HOST_RECOVERY_VM_REMAINS_ATTACHED_FOR_DIAGNOSIS");
                success = false;
            }
            if (success && existingFixture is null)
            {
                EnsurePlainPath(fixture);
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(fixture)), parent, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("隔离 fixture 清理边界错误。");
                foreach (var path in Directory.EnumerateFileSystemEntries(fixture, "*", SearchOption.AllDirectories)) EnsurePlainPath(path);
                Directory.Delete(fixture, recursive: true);
            }
            else if (Directory.Exists(fixture)) Console.Error.WriteLine("RETAINED_TEST_FIXTURE=" + fixture);
        }
        Console.WriteLine("HOST_UI_RECOVERY=" + (success ? "PASS" : "FAILED"));
        return success ? 0 : 1;
    }

    private static void AssertLocked(PortableBundleDescriptor bundle)
    {
        try
        {
            var duplicate = RealPortableStackHost.OpenExistingForRecovery(bundle);
            _ = duplicate;
            throw new IOException("恢复窗口未持续持有实例锁。");
        }
        catch (InstanceAlreadyLockedException) { }
    }

    private static string SafeFailureStage(string stage) => stage switch
    {
        "preflight" or "old-launcher-process" or "new-window-recovery" or "normal-stop" or
        "same-window-restart" or "all-stopped-reopen" or "incomplete-provision" or
        "partial-pg-only" or "unknown-record" => stage,
        _ => "other",
    };

    private static string SafeFailureCategory(Exception error) => error switch
    {
        TimeoutException => "Timeout",
        IOException => "Io",
        UnauthorizedAccessException => "Security",
        InvalidOperationException => "InvalidOperation",
        ArgumentException => "Argument",
        _ => "Other",
    };


    private static void AssertLiveIdentity(OwnedProcessIdentity identity)
    {
        using var process = Process.GetProcessById(identity.ProcessId);
        Assert(!process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - identity.StartedAtUtc.UtcDateTime).TotalSeconds) < 1,
            $"受管 PID {identity.ProcessId} 未保持原启动时间。");
    }

    private static void AssertNotLiveIdentity(OwnedProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            Assert(process.HasExited || Math.Abs((process.StartTime.ToUniversalTime() - identity.StartedAtUtc.UtcDateTime).TotalSeconds) >= 1,
                $"原受管 PID {identity.ProcessId} 仍在运行。");
        }
        catch (ArgumentException) { }
    }

    private static void Close(MainWindowViewModel vm)
    {
        var shutdown = vm.ShutdownAsync();
        PumpTask(shutdown, TimeSpan.FromMinutes(1), "Launcher 正常关闭未完成");
        Assert(shutdown.GetAwaiter().GetResult(), "Launcher 未确认全部组件安全停止。");
        PumpTask(vm.DisposeAsync().AsTask(), TimeSpan.FromSeconds(30), "Launcher 资源释放未完成");
    }

    private static OwnedProcessIdentity ReadRuntimeIdentity(string fixture, string runtimeName)
    {
        var path = Path.Combine(fixture, "data", "config", runtimeName);
        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var identity = document.RootElement.GetProperty("identity");
        return new OwnedProcessIdentity(
            identity.GetProperty("process_id").GetInt32(),
            identity.GetProperty("started_at_utc").GetDateTimeOffset(),
            identity.GetProperty("executable_path").GetString()!,
            identity.GetProperty("instance_root").GetString()!,
            identity.GetProperty("instance_id").GetGuid(),
            identity.GetProperty("run_id").GetGuid());
    }

    private static void PumpTask(Task task, TimeSpan timeout, string message)
    {
        PumpUntil(() => task.IsCompleted, timeout, message);
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout, string message)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException(message);
            Thread.Sleep(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new IOException(message);
    }

    private static void EnsurePlainPath(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("测试目录存在重解析点。");
            current = Path.GetDirectoryName(current)!;
        }
    }
}
