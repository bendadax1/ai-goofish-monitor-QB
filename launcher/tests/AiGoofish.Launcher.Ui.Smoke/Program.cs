using AiGoofish.Launcher.App;
using AiGoofish.Launcher.Ui.Smoke;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
using Avalonia.Threading;

try
{
if (args.Length == 2 && args[0] == "--build-flavor-acceptance")
{
    var expected = args[1] switch { "development" => true, "release" => false, _ => throw new ArgumentException("未知构建类型") };
    if (AiGoofish.Launcher.App.Program.DevelopmentEnabled != expected) return 1;
    Console.WriteLine("BUILD_FLAVOR_PASS");
    return 0;
}
if (args.Length == 1 && args[0] == "--startup-diagnostic-acceptance")
{
    return StartupDiagnosticAcceptance.Run();
}
if (args.Length == 2 && args[0] == "--development-layout-acceptance")
{
    return await StartupDiagnosticAcceptance.CheckDevelopmentLayoutAsync(args[1]);
}
if (args.Length == 2 && args[0] is "--new-bundle-preflight" or "--failed-bundle-preflight")
{
    AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .SetupWithoutStarting();
    var bundleRoot = Path.GetFullPath(args[1]);
    await using var viewModel = new MainWindowViewModel(isSimulation: false, bundleRoot: bundleRoot);
    var initialization = viewModel.InitializeAsync();
    var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(4);
    while (!initialization.IsCompleted)
    {
        Dispatcher.UIThread.RunJobs();
        if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("New bundle preflight did not finish.");
        Thread.Sleep(20);
    }
    Dispatcher.UIThread.RunJobs();
    await initialization;
    var initialPhase = viewModel.PhaseMessage;
    var refresh = viewModel.RefreshStatusAsync();
    deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
    while (!refresh.IsCompleted)
    {
        Dispatcher.UIThread.RunJobs();
        if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("New bundle refresh did not finish.");
        Thread.Sleep(20);
    }
    Dispatcher.UIThread.RunJobs();
    await refresh;
    var ready = viewModel.IsPrimaryEnabled && viewModel.PrimaryButtonText == "一键启动";
    var diagnosticPreserved = viewModel.PhaseMessage == initialPhase;
    Console.WriteLine($"BUNDLE_PREFLIGHT state={viewModel.ServiceState} ready={ready} diagnostic_preserved={diagnosticPreserved}");
    return diagnosticPreserved && (args[0] == "--new-bundle-preflight" ? ready : !ready) ? 0 : 1;
}
if (args.Length == 2 && args[0] == "--automatic-port-real-ui")
{
    return await AutomaticWebPortHostAcceptance.RunAsync(args[1]);
}
if (args.Length == 3 && args[0] == "--automatic-port-reopen-probe")
{
    return await AutomaticWebPortHostAcceptance.RunReopenProbeAsync(args[1], args[2]);
}
if (args.Length == 2 && args[0] == "--host-recovery-acceptance")
{
    return await HostRecoveryAcceptance.RunAsync(args[1]);
}
if (args.Length == 3 && args[0] == "--host-recovery-resume")
{
    return await HostRecoveryAcceptance.RunAsync(args[1], args[2]);
}
if (args.Length == 3 && args[0] == "--host-recovery-host-only")
{
    return await HostRecoveryAcceptance.ProbeHostOnlyAsync(args[1], args[2]);
}
if (args.Length == 3 && args[0] == "--host-recovery-host-stop")
{
    return await HostRecoveryAcceptance.ProbeHostOnlyAsync(args[1], args[2], stop: true);
}
if (args.Length == 3 && args[0] == "--host-recovery-host-start")
{
    return await HostRecoveryAcceptance.StartStoppedHostOnlyAsync(args[1], args[2]);
}
if (args.Length == 3 && args[0] == "--host-recovery-seed")
{
    return await HostRecoveryAcceptance.SeedAsync(args[1], args[2]);
}
if (args.Length == 1 && args[0] == "--diagnostic-exporter-acceptance")
{
    return await RunDiagnosticExporterAcceptanceAsync();
}
if (args.Length == 1 && args[0] == "--restore-ui-acceptance")
{
    return await RestoreUiAcceptance.RunAsync();
}
if (args.Length == 1 && args[0] == "--windows-tray-acceptance")
{
    return await WindowsTrayServiceAcceptance.RunAsync();
}

if (args.Length == 1 && args[0] == "--business-connection-acceptance")
{
    AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = false,
        })
        .SetupWithoutStarting();
    await BusinessConnectionAcceptance.RunAsync();
    return 0;
}
if (args.Length == 1 && args[0] == "--web-port-ui-acceptance")
{
    AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .SetupWithoutStarting();
    await WebPortUiAcceptance.RunAsync();
    return 0;
}
if (args.Length == 1 && args[0] == "--diagnostic-ui-acceptance")
{
    try
    {
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        await WebPortUiAcceptance.RunDiagnosticPreviewAcceptanceAsync();
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"诊断 UI 验收失败（异常类型：{exception.GetType().Name}）。");
        return 1;
    }
}
if (args.Length == 1 && args[0] == "--launcher-preferences-acceptance")
{
    AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .SetupWithoutStarting();
    await LauncherPreferencesAcceptance.RunAsync();
    return 0;
}

if (args.Length == 1 && args[0] == "--headless-watchdog-self-test")
{
    return await RunHeadlessWatchdogSelfTestAsync();
}
if (args.Length == 1 && args[0] == "--child-invocation-self-test")
{
    return TestHeadlessChildInvocation();
}

if (args.Length == 1 && args[0] == "--headless-watchdog-probe-child")
{
    Console.WriteLine("HEADLESS_WATCHDOG_PROBE_CHILD_STARTED");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

var isHeadlessChild = args.Length == 2 && args[0] == "--headless-child";
if (!isHeadlessChild && args.Length != 1)
{
    Console.Error.WriteLine("用法：AiGoofish.Launcher.Ui.Smoke <PNG 输出目录> | --headless-watchdog-self-test");
    return 2;
}

var outputDirectory = Path.GetFullPath(isHeadlessChild ? args[1] : args[0]);
var standardOutputPath = Path.Combine(outputDirectory, "ui-smoke-1120x760.png");
var minimumOutputPath = Path.Combine(outputDirectory, "ui-smoke-920x640.png");
var settingsOutputPath = Path.Combine(outputDirectory, "ui-settings-1120x760.png");
var diagnosticsOutputPath = Path.Combine(outputDirectory, "ui-diagnostics-1120x760.png");
var logsOutputPath = Path.Combine(outputDirectory, "ui-logs-1120x760.png");
Directory.CreateDirectory(outputDirectory);

if (!isHeadlessChild)
{
    var restoreAcceptance = await RestoreUiAcceptance.RunAsync();
    if (restoreAcceptance != 0) return restoreAcceptance;
    return await RunHeadlessSmokeWithWatchdogAsync(outputDirectory, TimeSpan.FromSeconds(60));
}

    AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = false,
        })
        .SetupWithoutStarting();

    await BusinessConnectionAcceptance.RunAsync();
    await LauncherPreferencesAcceptance.RunAsync();

    await BrandVisualAcceptance.RunAsync(outputDirectory);
    await LogViewsAcceptance.RunAsync(outputDirectory);

    var closeOwner = new Window { Width = 500, Height = 300 };
    closeOwner.Show();
    Dispatcher.UIThread.RunJobs();
    var closeDialog = new CloseChoiceWindow();
    var closeResultTask = closeDialog.ShowDialog<CloseChoiceResult>(closeOwner);
    Dispatcher.UIThread.RunJobs();
    var closeButtons = FindButtons(closeDialog).ToArray();
    var backgroundButton = closeButtons.Single(button => button.Content?.ToString()?.StartsWith("后台运行", StringComparison.Ordinal) is true);
    Assert(backgroundButton.Content?.ToString() == "后台运行（任务栏）",
        "托盘不可用时，后台操作必须明确标记为任务栏最小化");
    var rememberChoice = FindCheckBoxes(closeDialog).Single();
    rememberChoice.IsChecked = true;
    backgroundButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    PumpUntil(() => closeResultTask.IsCompleted, TimeSpan.FromSeconds(2), "后台运行关闭选择未返回");
    var closeResult = await closeResultTask;
    Assert(closeResult.Choice is WindowCloseChoice.Background && closeResult.Remember,
        "关闭选择必须表达任务栏后台行为并支持记忆选择");
    Assert(!FindButtons(new CloseChoiceWindow()).Any(button => button.Content?.ToString()?.Contains("托盘", StringComparison.Ordinal) is true) &&
        FindButtons(new CloseChoiceWindow(trayAvailable: true)).Any(button => button.Content?.ToString() == "后台运行（托盘）"),
        "关闭选择必须按实际托盘可用状态区分托盘与任务栏入口");
    closeOwner.Close();

    var window = new MainWindow(isSimulation: true)
    {
        Width = 1120,
        Height = 760,
    };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    var primaryButton = window.FindControl<Button>("PrimaryButton")
        ?? throw new InvalidOperationException("未找到模拟演练主按钮。");
    var cancelButton = window.FindControl<Button>("CancelButton")
        ?? throw new InvalidOperationException("未找到安全取消按钮。");
    var backupButton = window.FindControl<Button>("BusinessBackupButton")
        ?? throw new InvalidOperationException("未找到业务备份按钮。");
    var restoreButton = window.FindControl<Button>("BusinessRestoreButton")
        ?? throw new InvalidOperationException("未找到恢复业务备份按钮。");
    var businessPairingButton = window.FindControl<Button>("BusinessPairingButton")
        ?? throw new InvalidOperationException("未找到业务账号配对按钮。");
    var cancelRestoreButton = window.FindControl<Button>("CancelRestoreButton")
        ?? throw new InvalidOperationException("未找到取消恢复按钮。");
    var openManagementPreference = window.FindControl<ToggleSwitch>("OpenManagementPagePreferenceCheckBox")
        ?? throw new InvalidOperationException("未找到自动打开管理页偏好。");
    var autoStartPreference = window.FindControl<ToggleSwitch>("AutoStartPreferenceCheckBox")
        ?? throw new InvalidOperationException("未找到 Launcher 自动启动偏好。");

    Assert(primaryButton.IsEnabled, "初始主按钮应可用。");
    Assert(openManagementPreference.IsChecked is true && autoStartPreference.IsChecked is false &&
        !openManagementPreference.IsEnabled && !autoStartPreference.IsEnabled,
        "模拟模式应显示安全默认偏好但禁用编辑，不访问持久偏好文件。");
    Assert(!cancelButton.IsEnabled, "没有启动操作时取消按钮应禁用。");
    Assert(!backupButton.IsEnabled, "模拟模式不得启用真实业务备份。");
    Assert(!businessPairingButton.IsEnabled && window.DataContext is MainWindowViewModel { BusinessConnection.IsSimulation: true },
        "模拟模式不得创建业务 client、打开授权页或连接真实账号。");
    Assert(!restoreButton.IsEnabled && !cancelRestoreButton.IsVisible, "模拟模式不得打开恢复入口或显示恢复取消操作。");
    Assert(string.Equals(primaryButton.Content?.ToString(), "开始模拟演练", StringComparison.Ordinal), "主按钮必须明确标记模拟演练。");

    var backupDialog = new BackupPassphraseDialog();
    var backupDialogContent = backupDialog.Content as StackPanel
        ?? throw new InvalidOperationException("备份口令对话框缺少内容面板。");
    var passwordFields = backupDialogContent.Children.OfType<TextBox>().ToArray();
    Assert(passwordFields.Length == 2 && passwordFields.All(field => field.PasswordChar == '●'), "备份口令和确认字段必须都掩码显示。");
    var backupNotice = string.Join(" ", backupDialogContent.Children.OfType<TextBlock>().Select(block => block.Text));
    Assert(backupNotice.Contains("Web 与 PostgreSQL", StringComparison.Ordinal) &&
        backupNotice.Contains("较长时间", StringComparison.Ordinal) &&
        backupNotice.Contains("忘记口令将无法恢复", StringComparison.Ordinal), "备份确认窗口必须说明停机、耗时与口令不可找回风险。");

    primaryButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    PumpUntil(
        () => string.Equals(primaryButton.Content?.ToString(), "停止模拟演练", StringComparison.Ordinal),
        TimeSpan.FromSeconds(6),
        "模拟演练未在时限内进入运行状态");
    Assert(!cancelButton.IsEnabled, "模拟运行完成后取消按钮应禁用。");
    Assert(window.DataContext is MainWindowViewModel { ServiceState: "模拟组件运行中", IsSimulation: true }, "ViewModel 必须报告模拟组件运行中。");

    using var standardFrame = window.CaptureRenderedFrame()
        ?? throw new InvalidOperationException("离屏渲染未产生帧。");
    standardFrame.Save(standardOutputPath, PngBitmapEncoderOptions.Default);

    var navigationCases = new[]
    {
        (Name: "SettingsNavigationButton", Page: LauncherPage.Settings, Output: settingsOutputPath),
        (Name: "AiNavigationButton", Page: LauncherPage.Ai, Output: Path.Combine(outputDirectory, "ui-ai-1120x760.png")),
        (Name: "DiagnosticsNavigationButton", Page: LauncherPage.Diagnostics, Output: diagnosticsOutputPath),
        (Name: "LogsNavigationButton", Page: LauncherPage.Logs, Output: logsOutputPath),
    };
    foreach (var navigationCase in navigationCases)
    {
        var navigationButton = window.FindControl<Button>(navigationCase.Name)
            ?? throw new InvalidOperationException($"未找到页面导航按钮：{navigationCase.Name}");
        navigationButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert(window.DataContext is MainWindowViewModel pageViewModel &&
            (navigationCase.Page switch
            {
                LauncherPage.Settings => pageViewModel.IsSettingsPage,
                LauncherPage.Ai => pageViewModel.IsAiPage,
                LauncherPage.Diagnostics => pageViewModel.IsDiagnosticsPage,
                LauncherPage.Logs => pageViewModel.IsLogsPage,
                _ => false,
            }), $"页面导航未切换到 {navigationCase.Page}。");
        using var pageFrame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException($"{navigationCase.Page} 页离屏渲染未产生帧。");
        pageFrame.Save(navigationCase.Output, PngBitmapEncoderOptions.Default);
    }
    var logsPrimary = window.FindControl<Button>("LogsPrimaryButton")!;
    Assert(logsPrimary.IsEffectivelyVisible && logsPrimary.Content?.ToString() == primaryButton.Content?.ToString() &&
        logsPrimary.IsEnabled == primaryButton.IsEnabled, "日志页必须共用首页主操作状态。");
    logsPrimary.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    PumpUntil(
        () => string.Equals(primaryButton.Content?.ToString(), "开始模拟演练", StringComparison.Ordinal),
        TimeSpan.FromSeconds(6), "日志页主操作未能停止模拟演练");
    window.FindControl<Button>("OverviewNavigationButton")
        ?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
    Assert(window.DataContext is MainWindowViewModel { IsOverviewPage: true }, "返回首页失败。");

    Assert(window.DataContext is MainWindowViewModel { ServiceState: "模拟组件已停止", IsSimulation: true }, "ViewModel 必须报告模拟组件已停止。");

    window.Width = 920;
    window.Height = 640;
    Dispatcher.UIThread.RunJobs();
    using var minimumFrame = window.CaptureRenderedFrame()
        ?? throw new InvalidOperationException("最小窗口离屏渲染未产生帧。");
    minimumFrame.Save(minimumOutputPath, PngBitmapEncoderOptions.Default);

    foreach (var outputPath in new[] { standardOutputPath, minimumOutputPath, settingsOutputPath, diagnosticsOutputPath, logsOutputPath })
    {
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new InvalidOperationException($"离屏 PNG 未成功写入：{outputPath}");
        }
    }

    window.Close();
    PumpUntil(() => !window.IsVisible, TimeSpan.FromSeconds(3), "已停止窗口未在时限内关闭");

    var closingWindow = new MainWindow(isSimulation: true);
    closingWindow.Show();
    Dispatcher.UIThread.RunJobs();
    var closingPrimaryButton = closingWindow.FindControl<Button>("PrimaryButton")
        ?? throw new InvalidOperationException("关闭场景未找到模拟演练主按钮。");
    closingPrimaryButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    PumpUntil(
        () => string.Equals(closingPrimaryButton.Content?.ToString(), "模拟演练启动中", StringComparison.Ordinal),
        TimeSpan.FromSeconds(2),
        "关闭场景未进入模拟启动状态");
    closingWindow.Close();
    PumpUntil(() => !closingWindow.IsVisible, TimeSpan.FromSeconds(6), "启动中关闭未在安全收尾后完成");

    Console.WriteLine("UI_SMOKE_PASS");
    Console.WriteLine("INTERACTION=start->Running->stop->Stopped; close-during-start->safe-close");
    Console.WriteLine($"HEADLESS_RENDER_STANDARD={standardOutputPath}");
    Console.WriteLine($"HEADLESS_RENDER_STANDARD_BYTES={new FileInfo(standardOutputPath).Length}");
    Console.WriteLine($"HEADLESS_RENDER_MINIMUM={minimumOutputPath}");
    Console.WriteLine($"HEADLESS_RENDER_MINIMUM_BYTES={new FileInfo(minimumOutputPath).Length}");
    Console.WriteLine("BOUNDARY=离屏 Skia 验证控件树、布局、绑定与像素输出；不代表 Win32 原生窗口、DPI 或辅助技术已验收。");
    return 0;
}
catch (Exception exception)
{
    try
    {
        Console.Error.WriteLine($"Smoke 入口失败（异常类型：{exception.GetType().Name}）。");
    }
    catch
    {
        // stderr 不可用时仍以非零退出，避免再次抛出未处理异常。
    }
    return 1;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"预期抛出 {typeof(TException).Name}");
}

static async Task<int> RunHeadlessWatchdogSelfTestAsync()
{
    var result = await RunChildWithWatchdogAsync(
        new[] { "--headless-watchdog-probe-child" },
        TimeSpan.FromSeconds(2),
        "headless-watchdog-probe");
    if (result != 124)
    {
        Console.Error.WriteLine($"HEADLESS_WATCHDOG_FAIL: expected timeout exit 124, got {result}");
        return 1;
    }

    Console.WriteLine("HEADLESS_WATCHDOG_PASS");
    Console.WriteLine("WATCHDOG_PROBE=子进程超时后在有界时间内被回收");
    return 0;
}

static int TestHeadlessChildInvocation()
{
    var assembly = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Missing entry assembly");
    var child = new[] { "--headless-child", "C:\\acceptance output" };
    var frameworkArguments = HeadlessChildInvocation.BuildArguments("C:\\dotnet\\dotnet.exe", assembly, child);
    var appHostArguments = HeadlessChildInvocation.BuildArguments("C:\\acceptance\\Ui.Smoke.exe", assembly, child);
    Assert(frameworkArguments.Count == 3 && frameworkArguments[0] == assembly && frameworkArguments[1] == child[0] && frameworkArguments[2] == child[1], "framework-dependent invocation must pass assembly before child args");
    Assert(appHostArguments.SequenceEqual(child), "apphost invocation must not inject an assembly argument");
    AssertThrows<InvalidOperationException>(() => HeadlessChildInvocation.BuildArguments("C:\\dotnet\\dotnet.exe", string.Empty, child));
    Console.WriteLine("HEADLESS_CHILD_INVOCATION_PASS framework/apphost/missing-assembly");
    return 0;
}

static Task<int> RunHeadlessSmokeWithWatchdogAsync(string outputDirectory, TimeSpan timeout) =>
    RunChildWithWatchdogAsync(new[] { "--headless-child", outputDirectory }, timeout, "headless-ui-smoke");

static async Task<int> RunChildWithWatchdogAsync(IReadOnlyList<string> arguments, TimeSpan timeout, string label)
{
    var executablePath = Environment.ProcessPath
        ?? throw new InvalidOperationException("无法确定 UI smoke 可执行文件路径，拒绝无界启动 Headless 测试。");
    var startInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName = executablePath,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? string.Empty;
    var childInvocationArguments = HeadlessChildInvocation.BuildArguments(executablePath, entryAssembly, arguments);
    foreach (var argument in childInvocationArguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = new System.Diagnostics.Process { StartInfo = startInfo };
    try
    {
        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动 {label} 子进程。");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (!string.IsNullOrWhiteSpace(stdout)) Console.Write(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.Write(stderr);
            Console.Error.WriteLine($"HEADLESS_WATCHDOG_TIMEOUT: {label} 超过 {timeout.TotalSeconds:0} 秒；已终止隔离子进程以避免 UI 线程卡死拖住验收。");
            return 124;
        }

        var childOutput = await stdoutTask;
        var childError = await stderrTask;
        if (!string.IsNullOrWhiteSpace(childOutput)) Console.Write(childOutput);
        if (!string.IsNullOrWhiteSpace(childError)) Console.Error.Write(childError);
        return process.ExitCode;
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        Console.Error.WriteLine($"HEADLESS_WATCHDOG_FAIL: {label} 启动或监督失败：{exception.GetType().Name}: {exception.Message}");
        return 1;
    }
}

static IEnumerable<Button> FindButtons(Control root)
{
    if (root is Button button)
    {
        yield return button;
    }

    if (root is ContentControl { Content: Control content })
    {
        foreach (var child in FindButtons(content)) yield return child;
    }

    if (root is Panel panel)
    {
        foreach (var child in panel.Children)
        {
            foreach (var nested in FindButtons(child)) yield return nested;
        }
    }
}

static IEnumerable<CheckBox> FindCheckBoxes(Control root)
{
    if (root is CheckBox checkBox)
    {
        yield return checkBox;
    }

    if (root is ContentControl { Content: Control content })
    {
        foreach (var child in FindCheckBoxes(content)) yield return child;
    }

    if (root is Panel panel)
    {
        foreach (var child in panel.Children)
        {
            foreach (var nested in FindCheckBoxes(child)) yield return nested;
        }
    }
}

static void PumpUntil(Func<bool> condition, TimeSpan timeout, string failureMessage)
{
    var startedAt = DateTimeOffset.UtcNow;
    while (!condition())
    {
        Dispatcher.UIThread.RunJobs();
        if (DateTimeOffset.UtcNow - startedAt >= timeout)
        {
            throw new TimeoutException(failureMessage);
        }

        Thread.Sleep(10);
    }

    Dispatcher.UIThread.RunJobs();
}

static async Task<int> RunDiagnosticExporterAcceptanceAsync()
{
    var root = Path.Combine(Environment.CurrentDirectory, ".tmp", "tests", "launcher-diagnostic", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var snapshot = new AiGoofish.Launcher.Core.LauncherSnapshot(
            Guid.NewGuid(),
            AiGoofish.Launcher.Core.LauncherState.Running,
            "ready",
            "fixture contains secret phase detail",
            false,
            false,
            DateTimeOffset.UtcNow,
            new[]
            {
                new AiGoofish.Launcher.Core.ComponentSnapshot("postgres", "Database display name", AiGoofish.Launcher.Core.ComponentRuntimeState.Running, "password=fixture-secret"),
                new AiGoofish.Launcher.Core.ComponentSnapshot("unknown-user-data", "private label", AiGoofish.Launcher.Core.ComponentRuntimeState.Failed, "fixture-path"),
            })
        {
            StartupSteps = [
                new("postgres-initialize", "private label", AiGoofish.Launcher.Core.StartupStepState.Completed, true),
                new("database-provision", "private label", AiGoofish.Launcher.Core.StartupStepState.Completed, true),
                new("unknown-user-data", "private label", AiGoofish.Launcher.Core.StartupStepState.Failed, false),
            ],
        };
        var events = new[]
        {
            new AiGoofish.Launcher.Core.LauncherDiagnosticEvent(
                DateTimeOffset.Now,
                AiGoofish.Launcher.Core.LauncherDiagnosticCode.ComponentStateReadFailed,
                AiGoofish.Launcher.Core.LauncherDiagnosticComponent.Postgres,
                AiGoofish.Launcher.Core.LauncherDiagnosticSeverity.Error),
            new AiGoofish.Launcher.Core.LauncherDiagnosticEvent(
                DateTimeOffset.Now,
                AiGoofish.Launcher.Core.LauncherDiagnosticCode.PostgresInitInterrupted,
                AiGoofish.Launcher.Core.LauncherDiagnosticComponent.Postgres,
                AiGoofish.Launcher.Core.LauncherDiagnosticSeverity.Error),
            new AiGoofish.Launcher.Core.LauncherDiagnosticEvent(
                DateTimeOffset.Now,
                AiGoofish.Launcher.Core.LauncherDiagnosticCode.DatabaseProvisionInterrupted,
                AiGoofish.Launcher.Core.LauncherDiagnosticComponent.DatabaseProvision,
                AiGoofish.Launcher.Core.LauncherDiagnosticSeverity.Error),
        };
        var bytes = LauncherDiagnosticExporter.CreatePreview(snapshot, events);
        Assert(bytes.Length > 0 && bytes.Length <= LauncherDiagnosticExporter.MaximumBytes, "摘要长度须在受控范围内");
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        using var versionReport = System.Text.Json.JsonDocument.Parse(bytes);
        var expectedVersion = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(LauncherDiagnosticExporter).Assembly)
            ?.InformationalVersion;
        Assert(!string.IsNullOrWhiteSpace(expectedVersion) &&
            versionReport.RootElement.GetProperty("launcherVersion").GetString() == expectedVersion,
            "诊断必须保留程序集完整信息版本（包括 beta 后缀）");
        Assert(json.Contains("postgres", StringComparison.Ordinal), "白名单组件状态应包含");
        Assert(json.Contains("startupSteps", StringComparison.Ordinal) && json.Contains("Completed", StringComparison.Ordinal) &&
            json.Contains("postgres-initialize", StringComparison.Ordinal) &&
            !json.Contains("unknown-user-data", StringComparison.Ordinal), "准备步骤单独导出且只含白名单状态");
        Assert(json.Contains("COMPONENT_STATE_READ_FAILED", StringComparison.Ordinal) &&
            json.Contains("POSTGRES_INIT_INTERRUPTED", StringComparison.Ordinal) &&
            json.Contains("DATABASE_PROVISION_INTERRUPTED", StringComparison.Ordinal) &&
            json.Contains("occurredAtUtc", StringComparison.Ordinal), "显式诊断事件须以固定错误码和 UTC 时刻导出");
        Assert(!json.Contains("fixture-secret", StringComparison.Ordinal) &&
            !json.Contains("fixture-path", StringComparison.Ordinal) &&
            !json.Contains("private label", StringComparison.Ordinal) &&
            !json.Contains("fixture contains secret", StringComparison.Ordinal) &&
            !json.Contains("password=", StringComparison.Ordinal) &&
            !json.Contains("cookie=", StringComparison.Ordinal) &&
            !json.Contains("account=", StringComparison.Ordinal) &&
            !json.Contains(Environment.CurrentDirectory, StringComparison.Ordinal), "错误、异常文本、路径、账号、密钥、Cookie 和未知组件不得泄漏");
        await AssertThrowsAsync<ArgumentOutOfRangeException>(() => Task.Run(() => LauncherDiagnosticExporter.CreatePreview(
            snapshot,
            Enumerable.Repeat(events[0], AiGoofish.Launcher.Core.BoundedDiagnosticEventBuffer.MaximumCapacity + 1).ToArray())));
        await AssertThrowsAsync<ArgumentOutOfRangeException>(() => Task.Run(() => _ = new AiGoofish.Launcher.Core.LauncherDiagnosticEvent(
            DateTimeOffset.UtcNow,
            (AiGoofish.Launcher.Core.LauncherDiagnosticCode)int.MaxValue,
            AiGoofish.Launcher.Core.LauncherDiagnosticComponent.Launcher,
            AiGoofish.Launcher.Core.LauncherDiagnosticSeverity.Error)));

        var destination = Path.Combine(root, "diagnostic.json");
        await LauncherDiagnosticExporter.SaveNewFileAtomicallyAsync(destination, bytes);
        Assert(File.ReadAllBytes(destination).SequenceEqual(bytes), "新文件写入应完整");
        Assert(!File.ReadAllBytes(destination).AsSpan().StartsWith(System.Text.Encoding.UTF8.Preamble), "导出文件必须是 UTF-8 无 BOM");
        await AssertThrowsAsync<IOException>(() => LauncherDiagnosticExporter.SaveNewFileAtomicallyAsync(destination, bytes));
        Assert(File.ReadAllBytes(destination).SequenceEqual(bytes), "目标已存在时必须保持原内容");
        await AssertThrowsAsync<ArgumentOutOfRangeException>(() => LauncherDiagnosticExporter.SaveNewFileAtomicallyAsync(
            Path.Combine(root, "oversized.json"), new byte[LauncherDiagnosticExporter.MaximumBytes + 1]));
        Assert(!File.Exists(Path.Combine(root, "oversized.json")), "超限内容不得创建目标文件");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => LauncherDiagnosticExporter.SaveNewFileAtomicallyAsync(
            Path.Combine(root, "cancelled.json"), bytes, cancelled.Token));
        Assert(!File.Exists(Path.Combine(root, "cancelled.json")), "取消写入不得创建最终文件");
        Assert(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.TopDirectoryOnly).Any(), "原子失败不应遗留临时文件");
        Console.WriteLine("LAUNCHER_DIAGNOSTIC_EXPORTER_PASS");
        Console.WriteLine("检查：安全事件白名单/恶意自由文本排除、16 KiB 上限、UTF-8 无 BOM、原子新文件、不覆盖目标、取消失败清理临时文件");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"LAUNCHER_DIAGNOSTIC_EXPORTER_FAIL: {exception.GetType().Name}");
        return 1;
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task AssertThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
