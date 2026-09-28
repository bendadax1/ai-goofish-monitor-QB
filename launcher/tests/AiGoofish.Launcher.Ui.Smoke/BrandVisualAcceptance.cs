using AiGoofish.Launcher.App;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiGoofish.Launcher.Ui.Smoke;

internal static class BrandVisualAcceptance
{
    // Render-only state fixtures. Never initialize a host, open a browser or click a live action.
    public static async Task RunAsync(string outputDirectory)
    {
        await using var vm = new MainWindowViewModel(isSimulation: false);
        var window = new MainWindow(vm, skipInitialization: true) { Width = 1120, Height = 760 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var primary = window.FindControl<Button>("PrimaryButton")!;
            var footer = window.FindControl<Button>("FooterPrimaryButton")!;
            var actionFooter = window.FindControl<Border>("ActionFooter")!;
            var overview = window.FindControl<ScrollViewer>("OverviewPage")!;
            var logo = window.FindControl<Image>("BrandLogo")!;
            var background = window.FindControl<Image>("BrandBackground")!;
            Require(logo.Source?.Size.Width >= 128 && background.Source?.Size.Width > 1000, "brand resources loaded");
            Require(primary.Background is LinearGradientBrush, "primary gradient theme");
            Require(!footer.IsVisible, "no duplicate primary action on overview");

            var changes = new List<string?>();
            vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            Set(vm, nameof(vm.PrimaryButtonText), "一键启动");
            Set(vm, nameof(vm.ServiceState), "服务已停止");
            Set(vm, nameof(vm.PhaseMessage), "准备好后，点击一键启动即可开始。");
            Set(vm, nameof(vm.IsPrimaryEnabled), true);
            Set(vm, nameof(vm.IsStopEnabled), false);
            Set(vm, nameof(vm.IsCancelEnabled), false);
            Require(actionFooter.IsVisible && primary.IsEffectivelyVisible, "idle overview retains bottom-right primary action");
            Require(!overview.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "PrimaryButton"), "no primary action inside scrollable overview");
            Require(window.FindControl<ItemsControl>("OverviewHealthComponents") is not null, "instance health summary present");
            foreach (var component in vm.Components) component.Status = "已停止";
            Save(window, primary, outputDirectory, "brand-stopped-1120x760.png", "一键启动");
            window.Width = 920;
            window.Height = 640;
            Save(window, primary, outputDirectory, "brand-stopped-920x640.png", "一键启动");
            var anchoredPoint = primary.TranslatePoint(default, window);
            Set(vm, nameof(vm.PhaseMessage), string.Concat(Enumerable.Repeat("启动失败：请检查诊断报告后重试。\n", 40)));
            Dispatcher.UIThread.RunJobs();
            overview.Offset = new Vector(0, 200);
            Dispatcher.UIThread.RunJobs();
            Require(primary.TranslatePoint(default, window) == anchoredPoint, "long status and scrolling cannot move primary action");
            Save(window, primary, outputDirectory, "brand-long-status-920x640.png", "一键启动");
            overview.Offset = default;
            window.Width = 1120;
            window.Height = 760;

            Set(vm, nameof(vm.PrimaryButtonText), "打开管理页");
            Set(vm, nameof(vm.ServiceState), "服务运行中");
            Set(vm, nameof(vm.PhaseMessage), "本机服务已就绪，完成首次设置即可开始。");
            Set(vm, nameof(vm.SetupRequired), true);
            Set(vm, nameof(vm.IsStopEnabled), true);
            Require(actionFooter.IsVisible && window.FindControl<Button>("StopButton")!.IsEffectivelyVisible, "running overview retains stop control");
            foreach (var component in vm.Components) component.Status = "已就绪";
            Save(window, primary, outputDirectory, "brand-setup-1120x760.png", "设置登录密码");
            Require(changes.Contains(nameof(vm.DisplayPrimaryButtonText)), "display label notifies bindings");

            window.Width = 920;
            window.Height = 640;
            Save(window, primary, outputDirectory, "brand-setup-920x640.png", "设置登录密码");
            Set(vm, nameof(vm.SetupRequired), false);
            Set(vm, nameof(vm.ManagementUrl), "http://127.0.0.1:58000");
            Set(vm, nameof(vm.FreshnessText), "刚刚更新 · 测试展示");
            Set(vm, nameof(vm.PhaseMessage), "本机服务已就绪，可以打开管理页。");
            Save(window, primary, outputDirectory, "brand-ready-920x640.png", "打开管理页");

            Set(vm, nameof(vm.PrimaryButtonText), "正在停止");
            Set(vm, nameof(vm.ServiceState), "正在停止");
            Set(vm, nameof(vm.PhaseMessage), "正在安全停止本机服务，请稍候。");
            Set(vm, nameof(vm.IsPrimaryEnabled), false);
            Set(vm, nameof(vm.SetupRequired), true);
            Save(window, primary, outputDirectory, "brand-disabled-920x640.png", "正在停止");
            Require(!primary.IsEnabled, "busy state remains disabled, setup cannot override it");
            Set(vm, nameof(vm.IsStopEnabled), false);
            Set(vm, nameof(vm.IsCancelEnabled), true);
            Require(actionFooter.IsVisible, "startup cancellation retains footer");
            Set(vm, nameof(vm.IsCancelEnabled), false);
            Require(actionFooter.IsVisible && primary.IsEffectivelyVisible, "primary remains anchored when safe actions end");

            vm.SelectPage(LauncherPage.Settings);
            Dispatcher.UIThread.RunJobs();
            Require(footer.IsVisible && !footer.IsEnabled && footer.Content?.ToString() == "正在停止", "secondary pages share action state");
            Require(!primary.IsVisible, "secondary pages have no duplicate primary action");
            Require(actionFooter.IsVisible, "secondary pages retain footer");

            Set(vm, nameof(vm.SetupRequired), false);
            Set(vm, nameof(vm.PrimaryButtonText), "一键启动");
            Set(vm, nameof(vm.ServiceState), "服务已停止");
            Set(vm, nameof(vm.PhaseMessage), "准备好后，点击一键启动即可开始。");
            Set(vm, nameof(vm.IsPrimaryEnabled), true);
            foreach (var component in vm.Components) component.Status = "已停止";
            foreach (var size in new[] { (1120, 760), (920, 640) })
            {
                window.Width = size.Item1;
                window.Height = size.Item2;
                foreach (var page in new[] { LauncherPage.Settings, LauncherPage.Diagnostics, LauncherPage.Logs })
                {
                    vm.SelectPage(page);
                    var pagePrimary = page is LauncherPage.Logs ? window.FindControl<Button>("LogsPrimaryButton")! : footer;
                    Save(window, pagePrimary, outputDirectory, $"slim-{page.ToString().ToLowerInvariant()}-{size.Item1}x{size.Item2}.png", "一键启动");
                    var expected = page switch
                    {
                        LauncherPage.Settings => "DataDirectoryShortcutButton",
                        LauncherPage.Diagnostics => "DiagnosticComponents",
                        _ => "LogsDirectoryShortcutButton",
                    };
                    Require(window.FindControl<Control>(expected)!.IsEffectivelyVisible, "relocated control accessible on " + page);
                }
            }
            Require(window.FindControl<TextBox>("BusinessApiKeyTextBox") is not null, "AI configuration retained");
            await WebPortUiAcceptance.SaveSettingsPreviewAsync(outputDirectory);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Console.WriteLine("BRAND_VISUAL_ACCEPTANCE=PASS");
    }

    private static void Set(MainWindowViewModel vm, string property, object value) =>
        typeof(MainWindowViewModel).GetProperty(property)!.SetValue(vm, value);

    private static void Save(Window window, Button primary, string directory, string name, string label)
    {
        Dispatcher.UIThread.RunJobs();
        Require(primary.Content?.ToString() == label, "primary label " + label);
        var point = primary.TranslatePoint(new Point(), window);
        Require(point is { } p && p.X >= 0 && p.Y >= 0 &&
            p.X + primary.Bounds.Width <= window.ClientSize.Width &&
            p.Y + primary.Bounds.Height <= window.ClientSize.Height, "primary action visible without scrolling");
        Require(primary.Name == "LogsPrimaryButton" || point is { } anchor &&
            window.ClientSize.Width - anchor.X - primary.Bounds.Width <= 28 &&
            window.ClientSize.Height - anchor.Y - primary.Bounds.Height <= 26,
            "primary action anchored to bottom-right window edge");
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Brand frame unavailable");
        frame.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Brand acceptance: " + message);
    }
}
