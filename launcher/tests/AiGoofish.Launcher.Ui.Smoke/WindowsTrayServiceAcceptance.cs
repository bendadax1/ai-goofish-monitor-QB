using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Skia;
using AiGoofish.Launcher.App;

namespace AiGoofish.Launcher.Ui.Smoke;

internal static class WindowsTrayServiceAcceptance
{
    internal static async Task<int> RunAsync()
    {
        AppBuilder.Configure<AiGoofish.Launcher.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var app = Application.Current as AiGoofish.Launcher.App.App
            ?? throw new InvalidOperationException("Headless application failed to initialize.");
        var openCalls = 0;
        var safeExitCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new WindowsTrayService(
            app,
            () => openCalls++,
            () =>
            {
                safeExitCompletion.SetResult();
                return Task.CompletedTask;
            });

        var items = service.CreateMenu().Items.OfType<NativeMenuItem>().ToArray();
        if (items.Length != 2 || items[0].Header != "打开窗口" || items[1].Header != "安全退出")
        {
            throw new InvalidOperationException("Tray menu must expose exactly the discoverable open and safe-exit actions.");
        }

        items[0].Command?.Execute(null);
        if (openCalls != 1)
        {
            throw new InvalidOperationException("Tray open command did not invoke its window callback exactly once.");
        }

        items[1].Command?.Execute(null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await safeExitCompletion.Task.WaitAsync(timeout.Token);

        if (service.TryInstall() || string.IsNullOrWhiteSpace(service.FailureReason))
        {
            throw new InvalidOperationException("Headless execution must report tray integration unavailable instead of claiming native tray success.");
        }

        if (app.TryHideMainWindowToTray())
        {
            throw new InvalidOperationException("A headless application without a classic desktop lifetime must use the taskbar fallback.");
        }

        if (MainWindow.ShouldAskCloseChoice(requiresChoice: true, safeExitRequested: true) ||
            !MainWindow.ShouldAskCloseChoice(requiresChoice: true, safeExitRequested: false))
        {
            throw new InvalidOperationException("Safe exit must bypass the remembered close choice while an ordinary close still asks.");
        }

        var fallback = new TrayUnavailableWindow(service.FailureReason);
        var fallbackPanel = (StackPanel)fallback.Content!;
        if (!((TextBlock)fallbackPanel.Children[0]).Text!.Contains("任务栏", StringComparison.Ordinal) ||
            !((TextBlock)fallbackPanel.Children[1]).Text!.Contains(service.FailureReason, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unavailable tray must explain the taskbar fallback and its reason.");
        }

        service.Dispose();
        Console.WriteLine("WINDOWS_TRAY_HEADLESS_ACCEPTANCE_PASS");
        Console.WriteLine("检查：打开/安全退出菜单回调、Headless 明确降级、关闭选择绕过策略、任务栏降级说明。未验证原生托盘可见性。");
        return 0;
    }
}
