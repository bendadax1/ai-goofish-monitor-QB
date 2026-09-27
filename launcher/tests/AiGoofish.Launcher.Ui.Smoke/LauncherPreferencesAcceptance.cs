using System.Text;
using System.Reflection;
using AiGoofish.Launcher.App;
using Avalonia.Threading;

namespace AiGoofish.Launcher.Ui.Smoke;

internal static class LauncherPreferencesAcceptance
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".tmp",
            "tests",
            "launcher-preferences",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var succeeded = false;
        try
        {
            TestPersistentStore(root);
            await TestViewModelBoundariesAsync(root);
            Console.WriteLine("PASS Launcher preferences defaults, atomic persistence, compatibility and safe lifecycle gates");
            succeeded = true;
        }
        finally
        {
            var expectedParent = Path.GetFullPath(Path.Combine(
                Directory.GetCurrentDirectory(), ".tmp", "tests", "launcher-preferences"));
            var fullRoot = Path.GetFullPath(root);
            if (succeeded && Directory.Exists(fullRoot) &&
                Path.GetDirectoryName(fullRoot)?.Equals(expectedParent, StringComparison.OrdinalIgnoreCase) is true &&
                Guid.TryParseExact(Path.GetFileName(fullRoot), "N", out _) &&
                (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) is 0)
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }
    }

    private static void TestPersistentStore(string root)
    {
        var package = Path.Combine(root, "portable");
        var legacy = Path.Combine(root, "isolated-old-user-preference", "close-choice.txt");
        var store = new LauncherPreferencesStore(package, legacy);
        var defaults = store.Load();
        Assert(defaults.OpenManagementPage && !defaults.AutoStartOnLauncherOpen &&
            defaults.CloseChoice is WindowCloseChoice.Ask,
            "首次使用必须默认自动开管理页、关闭自动启动并询问关闭方式。");

        var configured = new LauncherPreferences(
            OpenManagementPage: false,
            AutoStartOnLauncherOpen: true,
            CloseChoice: WindowCloseChoice.Background);
        Assert(store.TrySave(configured), "有效偏好应保存成功。");
        var preferencePath = Path.Combine(package, "data", "launcher", "preferences.json");
        var bytes = File.ReadAllBytes(preferencePath);
        Assert(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF),
            "偏好 JSON 必须 UTF-8 无 BOM。");
        Assert(new LauncherPreferencesStore(package, legacy).Load() == configured,
            "JSON store 必须跨实例往返读取全部偏好。");

        var concurrentStores = Enumerable.Range(0, 12)
            .Select(_ => new LauncherPreferencesStore(package, legacy))
            .ToArray();
        var writes = concurrentStores.Select((writer, index) => Task.Run(() => writer.TrySave(configured with
        {
            OpenManagementPage = index % 2 == 0,
            AutoStartOnLauncherOpen = index % 2 != 0,
        })));
        Task.WaitAll(writes);
        Assert(writes.All(write => write.Result), "偏好并发写入必须成功串行化。");
        var afterConcurrentWrites = new LauncherPreferencesStore(package, legacy).Load();
        Assert(afterConcurrentWrites.OpenManagementPage != afterConcurrentWrites.AutoStartOnLauncherOpen,
            "并发替换后偏好必须仍是完整有效的一代配置。");

        File.WriteAllText(preferencePath, "{broken", new UTF8Encoding(false));
        var corruptStore = new LauncherPreferencesStore(package, legacy);
        Assert(corruptStore.Load() == new LauncherPreferences() &&
            !corruptStore.LoadWarning!.Contains("异常", StringComparison.Ordinal),
            "坏 JSON 必须回退安全默认且只显示固定安全提示。");
        File.WriteAllText(preferencePath,
            "{\"schemaVersion\":99,\"openManagementPage\":false,\"autoStartOnLauncherOpen\":true,\"closeChoice\":\"Exit\"}",
            new UTF8Encoding(false));
        Assert(new LauncherPreferencesStore(package, legacy).Load() == new LauncherPreferences(),
            "不支持的 schema 版本必须关闭自动启动并回退默认。");

        var legacyPackage = Path.Combine(root, "legacy-import");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, WindowCloseChoice.Background.ToString(), new UTF8Encoding(false));
        var imported = new LauncherPreferencesStore(legacyPackage, legacy);
        Assert(imported.Load() == new LauncherPreferences(CloseChoice: WindowCloseChoice.Background) &&
            File.Exists(legacy) && File.Exists(Path.Combine(legacyPackage, "data", "launcher", "preferences.json")),
            "旧关闭偏好必须只读迁移到新文件并保留旧文件。");

        var blockedRoot = Path.Combine(root, "blocked-root");
        Directory.CreateDirectory(Path.Combine(blockedRoot, "data"));
        File.WriteAllText(Path.Combine(blockedRoot, "data", "launcher"), "sentinel", new UTF8Encoding(false));
        Assert(!new LauncherPreferencesStore(blockedRoot, Path.Combine(root, "none.txt")).TrySave(configured),
            "偏好路径不可写或目录冲突时必须安全失败。");
    }

    private static async Task TestViewModelBoundariesAsync(string root)
    {
        var fake = new FakePreferencesStore(new LauncherPreferences(AutoStartOnLauncherOpen: true));
        var autoStarts = 0;
        await using (var viewModel = new MainWindowViewModel(
            isSimulation: false,
            bundleRoot: Path.Combine(root, "fake-vm"),
            verifiedBundle: null,
            restoreSessionFactory: null,
            userClientFactory: null,
            preferencesStore: fake,
            autoStartAction: () => { autoStarts++; return Task.CompletedTask; }))
        {
            await viewModel.TryAutoStartVerifiedStoppedExistingAsync();
            Assert(autoStarts == 0, "缺少已验证 stopped 实例门控时不得自动启动。");
            viewModel.MarkVerifiedStoppedExistingForAutoStart();
            await viewModel.TryAutoStartVerifiedStoppedExistingAsync();
            await viewModel.TryAutoStartVerifiedStoppedExistingAsync();
            Assert(autoStarts == 1, "自动启动只能由核验后的既有 stopped 状态触发一次。");
            viewModel.OpenManagementPageOnReady = false;
            viewModel.AutoStartOnLauncherOpen = false;
            Assert(!viewModel.OpenManagementPageOnReady && !viewModel.AutoStartOnLauncherOpen && fake.SaveCalls == 2,
                "真实偏好切换必须只写本地 store，不触发业务服务操作。");
        }

        var forbidden = new FakePreferencesStore(new LauncherPreferences(AutoStartOnLauncherOpen: true));
        var simulationStarts = 0;
        await using var simulation = new MainWindowViewModel(
            isSimulation: true,
            bundleRoot: Path.Combine(root, "simulation"),
            verifiedBundle: null,
            restoreSessionFactory: null,
            userClientFactory: null,
            preferencesStore: forbidden,
            autoStartAction: () => { simulationStarts++; return Task.CompletedTask; });
        simulation.AutoStartOnLauncherOpen = true;
        simulation.OpenManagementPageOnReady = false;
        simulation.MarkVerifiedStoppedExistingForAutoStart();
        await simulation.TryAutoStartVerifiedStoppedExistingAsync();
        Assert(forbidden.LoadCalls == 0 && forbidden.SaveCalls == 0 && simulationStarts == 0 &&
            !simulation.CanEditLauncherPreferences,
            "模拟模式不得读取/写入真实偏好或触发真实启动委托。");

        var shutdownGate = (SemaphoreSlim?)typeof(MainWindowViewModel)
            .GetField("_realOperationGate", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(simulation)
            ?? throw new InvalidOperationException("无法取得 ViewModel 的操作门用于取消退出回归。");
        using var activeOperation = new CancellationTokenSource();
        typeof(MainWindowViewModel)
            .GetField("_restoreCancellation", BindingFlags.Instance | BindingFlags.NonPublic)?
            .SetValue(simulation, activeOperation);
        await shutdownGate.WaitAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var shutdown = simulation.ShutdownAsync(cancellation.Token);
            cancellation.Cancel();
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!shutdown.IsCompleted && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            var shutdownSucceeded = await shutdown.WaitAsync(TimeSpan.FromMilliseconds(100));

            Assert(!shutdownSucceeded && simulation.IsPrimaryEnabled && simulation.IsStopEnabled &&
                simulation.PhaseMessage.Contains("已取消退出", StringComparison.Ordinal),
                "取消等待操作门时必须及时返回并恢复窗口状态。");
            Assert(!shutdownGate.Wait(0),
                "取消等待操作门不得释放仍由备份/恢复操作持有的门。");
            Assert(!activeOperation.IsCancellationRequested,
                "取消退出等待时不得取消当前备份/恢复操作。");
        }
        finally
        {
            shutdownGate.Release();
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakePreferencesStore(LauncherPreferences initial) : ILauncherPreferencesStore
    {
        private LauncherPreferences _preferences = initial;
        public int LoadCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public string? LoadWarning => null;

        public LauncherPreferences Load()
        {
            LoadCalls++;
            return _preferences;
        }

        public bool TrySave(LauncherPreferences preferences)
        {
            SaveCalls++;
            _preferences = preferences;
            return true;
        }
    }
}
