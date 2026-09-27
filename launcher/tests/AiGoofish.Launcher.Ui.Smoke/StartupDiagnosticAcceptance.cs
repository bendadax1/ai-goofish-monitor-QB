using System.Text;
using AiGoofish.Launcher.App;
using AiGoofish.Launcher.Core;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace AiGoofish.Launcher.Ui.Smoke;

internal static class StartupDiagnosticAcceptance
{
    public static int Run()
    {
        AppBuilder.Configure<AiGoofish.Launcher.App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var repository = Environment.GetEnvironmentVariable("AIGOOFISH_REPOSITORY_ROOT")
            ?? throw new InvalidOperationException("需要显式仓库根目录。");
        var root = Path.Combine(repository, ".tmp", "tests", "launcher-refactor", "preflight-" + Guid.NewGuid().ToString("N"));
        var viewModel = new MainWindowViewModel(false, root);
        try
        {
            Pump(viewModel.InitializeAsync());
            Require(!viewModel.IsPrimaryEnabled && viewModel.CanExportDiagnostic, "缺包预检失败也应允许诊断");
            Require(!Directory.Exists(root), "只读预检不得分配实例");
            var error = new IOException("fixture-secret-path", new UnauthorizedAccessException("fixture-secret-key"));
            StartupDiagnostics.Current.Record(StartupStage.Process, StartupEventKind.Failed, LauncherDiagnosticComponent.PythonWeb, error);
            Dispatcher.UIThread.RunJobs();
            Require(viewModel.Logs.Count > 0, "安全启动事件应进入 UI 日志");
            var before = viewModel.Logs.Count;
            var sample = StartupDiagnostics.Current.Snapshot().Last();
            viewModel.OnStartupDiagnostic(null, sample with { Sequence = 1000002 });
            viewModel.OnStartupDiagnostic(null, sample with { Sequence = 1000001 });
            viewModel.OnStartupDiagnostic(null, sample with { Sequence = 1000002 });
            Dispatcher.UIThread.RunJobs();
            Require(viewModel.Logs.Count == before + 2, "倒序投递不得丢失有效事件，重复事件只显示一次");
            using var preview = viewModel.CreateDiagnosticPreviewTicket();
            Require(preview.PreviewText.Contains("startup") && preview.PreviewText.Contains("AccessDenied") && !preview.PreviewText.Contains("fixture-secret"), "预检诊断包含可读安全事件但不含原始异常");
            // A preview is immutable even when new events arrive before the save.
            var original = preview.Contents.ToArray();
            StartupDiagnostics.Current.Record(StartupStage.Entry, StartupEventKind.Completed);
            Require(preview.Contents.Span.SequenceEqual(original), "预览字节不可随日志变化");
            Directory.CreateDirectory(root);
            var export = viewModel.ExportDiagnosticPreviewAsync(root, preview);
            Pump(export);
            Require(export.Result, "无 Host 的预检诊断可保存");
            Require(File.ReadAllBytes(Directory.GetFiles(root, "*.json").Single()).SequenceEqual(original), "保存必须等于预览");
            Console.WriteLine("STARTUP_DIAGNOSTIC_UI_PASS");
            return 0;
        }
        finally
        {
            Pump(viewModel.DisposeAsync().AsTask());
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    public static async Task<int> CheckDevelopmentLayoutAsync(string bundleRoot)
    {
        var repository = Environment.GetEnvironmentVariable("AIGOOFISH_REPOSITORY_ROOT")
            ?? throw new InvalidOperationException("需要显式仓库根目录。");
        var id = "layout-" + Guid.NewGuid().ToString("N");
        var request = new DevelopmentLaunchRequest(repository, Path.GetFullPath(bundleRoot), id);
        var loaded = await request.LoadAsync();
        Require(loaded.BundleRoot == request.SessionRoot && !Directory.Exists(Path.Combine(request.SessionRoot, "data")), "开发解析不应创建数据库或业务数据");
        Require(loaded.PythonExecutable.StartsWith(Path.GetFullPath(bundleRoot), StringComparison.OrdinalIgnoreCase), "复用包组件，不复制运行时");
        Require((await request.LoadAsync()).ReleaseId == loaded.ReleaseId, "同一开发会话可重开");
        var traversalRejected = false;
        try { _ = new DevelopmentLaunchRequest(repository, Path.GetFullPath(bundleRoot), "../data"); }
        catch (ArgumentException) { traversalRejected = true; }
        Require(traversalRejected, "会话路径穿越必须拒绝");
        Directory.Delete(request.SessionRoot, recursive: true); // Only marker, no services ever started.
        Console.WriteLine("DEVELOPMENT_LAYOUT_PASS");
        return 0;
    }

    private static void Pump(Task task)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("诊断测试超时。");
            Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
