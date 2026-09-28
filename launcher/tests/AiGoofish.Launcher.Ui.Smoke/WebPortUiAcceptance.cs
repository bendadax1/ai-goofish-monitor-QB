using System.Diagnostics;
using AiGoofish.Launcher.App;
using AiGoofish.Launcher.Core;
using AiGoofish.Launcher.Platform.Windows;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Headless;
using Avalonia.Media.Imaging;

namespace AiGoofish.Launcher.Ui.Smoke;

internal static class WebPortUiAcceptance
{
    public static async Task SaveSettingsPreviewAsync(string outputDirectory)
    {
        // Reuse the in-memory host fixture; no real service or port configuration is changed.
        await using var fixture = await Fixture.CreateAsync(running: false, webPortCanBind: _ => true);
        fixture.ViewModel.SelectPage(LauncherPage.Settings);
        foreach (var size in new[] { new Size(1120, 760), new Size(920, 640) })
        {
            fixture.Window.Width = size.Width;
            fixture.Window.Height = size.Height;
            PumpJobs();
            var apply = fixture.Window.FindControl<Button>("ApplyWebPortButton")!;
            apply.BringIntoView();
            PumpJobs();
            var point = apply.TranslatePoint(default, fixture.Window);
            Assert(point is { } p && p.X >= 0 && p.Y >= 0 &&
                p.X + apply.Bounds.Width <= fixture.Window.ClientSize.Width &&
                p.Y + apply.Bounds.Height <= fixture.Window.ClientSize.Height,
                "端口应用按钮在两种窗口尺寸下均可滚动到达。");
            using var frame = fixture.Window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("Settings preview frame unavailable.");
            frame.Save(Path.Combine(outputDirectory, $"slim-port-settings-{size.Width}x{size.Height}.png"), PngBitmapEncoderOptions.Default);
        }
        await fixture.ViewModel.BusinessConnection.SetHostReadyAsync(true);
        await fixture.ViewModel.BusinessConnection.BeginPairingAsync("http://127.0.0.1:58000/api/launcher/authorize");
        PumpUntil(() => fixture.ViewModel.BusinessConnection.IsPaired, TimeSpan.FromSeconds(3), "AI 预览配对未完成。");
        fixture.ViewModel.SelectPage(LauncherPage.Ai);
        fixture.ViewModel.BusinessConnection.TokensParameter = "max_completion_tokens";
        fixture.ViewModel.BusinessConnection.TokensLimit = "8192";
        var advanced = fixture.Window.FindControl<Expander>("AiAdvancedParameters")!;
        advanced.IsExpanded = true;
        foreach (var size in new[] { new Size(1120, 760), new Size(920, 640) })
        {
            fixture.Window.Width = size.Width;
            fixture.Window.Height = size.Height;
            PumpJobs();
            advanced.BringIntoView();
            PumpJobs();
            using var frame = fixture.Window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("AI advanced preview unavailable.");
            frame.Save(Path.Combine(outputDirectory, $"ai-advanced-{size.Width}x{size.Height}.png"), PngBitmapEncoderOptions.Default);
        }
    }

    public static async Task RunDiagnosticPreviewAcceptanceAsync()
    {
        var root = Path.Combine(Environment.CurrentDirectory, ".tmp", "tests", "launcher-diagnostic-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var fixture = await Fixture.CreateAsync(running: true);
            var tickets = new List<LauncherDiagnosticPreviewTicket>();
            fixture.Window.DiagnosticPreviewTicketDisposedForAcceptance = tickets.Add;
            var exportButton = fixture.Window.FindControl<Button>("DiagnosticExportButton")
                ?? throw new InvalidOperationException("未找到真实诊断导出按钮。");
            Assert(exportButton.IsEnabled, "有效 Fake Host 下真实诊断导出按钮应可用。");

            static void AssertTicketDisposed(LauncherDiagnosticPreviewTicket ticket)
            {
                try
                {
                    _ = ticket.Contents;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                throw new InvalidOperationException("handler 完成后必须清除预览 ticket 内存字节。");
            }

            async Task ClickDiagnosticAsync(
                bool confirmPreview,
                Func<Task<string?>>? folderSelection,
                Action<string>? verifyPreview = null)
            {
                var previousTicketCount = tickets.Count;
                fixture.Window.DiagnosticFolderSelectionForAcceptance = folderSelection;
                fixture.Window.DiagnosticPreviewShownForAcceptance = (preview, textBox, cancelButton, previewExportButton) =>
                {
                    Assert(textBox.IsReadOnly && !string.IsNullOrWhiteSpace(textBox.Text),
                        "预览必须显示只读的 ticket 文本。");
                    verifyPreview?.Invoke(textBox.Text ?? string.Empty);
                    Dispatcher.UIThread.RunJobs();
                    Assert(preview.IsVisible, "Headless 预览窗口必须先显示，才能测试真实点击路径。");
                    // Assert actual geometry, not merely accessibility presence or
                    // programmatic Click (which succeeds even off-screen).
                    var originalText = textBox.Text;
                    textBox.Text = new string('x', 200) + string.Concat(Enumerable.Repeat("\n诊断长文本", 200));
                    foreach (var size in new[] { new Size(720, 560), new Size(520, 360) })
                    {
                        preview.Width = size.Width;
                        preview.Height = size.Height;
                        preview.UpdateLayout();
                        Dispatcher.UIThread.RunJobs();
                        foreach (var button in new[] { cancelButton, previewExportButton })
                        {
                            var origin = button.TranslatePoint(default, preview);
                            Assert(origin is { } point && point.X >= 0 && point.Y >= 0 &&
                                point.X + button.Bounds.Width <= preview.ClientSize.Width &&
                                point.Y + button.Bounds.Height <= preview.ClientSize.Height,
                                "长诊断文本及缩小窗口时底部按钮必须保持可见。");
                        }
                        Assert(textBox.Bounds.Height > 0 && textBox.Bounds.Height < preview.ClientSize.Height,
                            "诊断文本必须获得有限滚动高度。");
                    }
                    textBox.Text = originalText;
                    (confirmPreview ? previewExportButton : cancelButton)
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return Task.CompletedTask;
                };
                exportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => tickets.Count == previousTicketCount + 1,
                    TimeSpan.FromSeconds(4), "真实诊断 handler 未释放预览 ticket。");
                fixture.Window.DiagnosticPreviewShownForAcceptance = null;
                fixture.Window.DiagnosticFolderSelectionForAcceptance = null;
                var ticket = tickets[^1];
                AssertTicketDisposed(ticket);
            }

            var cancelledDirectory = Path.Combine(root, "cancelled-preview");
            Directory.CreateDirectory(cancelledDirectory);
            var folderSelectionCalls = 0;
            await ClickDiagnosticAsync(
                confirmPreview: false,
                folderSelection: () =>
                {
                    folderSelectionCalls++;
                    return Task.FromResult<string?>(cancelledDirectory);
                },
                verifyPreview: text => Assert(!Directory.EnumerateFiles(cancelledDirectory).Any() &&
                    !string.IsNullOrWhiteSpace(text), "预览确认前不得写盘且必须显示摘要文本。"));
            Assert(folderSelectionCalls == 0 && !Directory.EnumerateFiles(cancelledDirectory).Any(),
                "取消预览不得打开目录选择或写入文件。");

            var cancelledFolderDirectory = Path.Combine(root, "cancelled-folder");
            Directory.CreateDirectory(cancelledFolderDirectory);
            await ClickDiagnosticAsync(
                confirmPreview: true,
                folderSelection: () => Task.FromResult<string?>(null),
                verifyPreview: text => Assert(!Directory.EnumerateFiles(cancelledFolderDirectory).Any() &&
                    !string.IsNullOrWhiteSpace(text), "选择目录前不得写盘且预览文本必须可读。"));
            Assert(!Directory.EnumerateFiles(cancelledFolderDirectory).Any(), "取消目录选择不得写入文件。");

            var acceptedDirectory = Path.Combine(root, "accepted");
            Directory.CreateDirectory(acceptedDirectory);
            string? acceptedPreviewText = null;
            var acceptedFolderSelectionCalled = false;
            await ClickDiagnosticAsync(
                confirmPreview: true,
                folderSelection: () =>
                {
                    acceptedFolderSelectionCalled = true;
                    return Task.FromResult<string?>(acceptedDirectory);
                },
                verifyPreview: text =>
                {
                    acceptedPreviewText = text;
                    Assert(!Directory.EnumerateFiles(acceptedDirectory).Any(), "确认目录选择前不得写盘。");
                });
            Assert(acceptedFolderSelectionCalled,
                $"确认预览后应调用目录选择；当前状态：{fixture.ViewModel.PhaseMessage}");
            var writtenFiles = Directory.EnumerateFiles(acceptedDirectory, "*.json").ToArray();
            Assert(writtenFiles.Length == 1,
                $"选择目录后应写入一个摘要文件；当前状态：{fixture.ViewModel.PhaseMessage}");
            var writtenPath = writtenFiles[0];
            var expectedBytes = System.Text.Encoding.UTF8.GetBytes(acceptedPreviewText!);
            Assert(File.ReadAllBytes(writtenPath).SequenceEqual(expectedBytes),
                "保存字节必须与真实预览框中的文本 UTF-8 字节完全一致。");

            var failureTarget = Path.Combine(root, "missing-target");
            await ClickDiagnosticAsync(
                confirmPreview: true,
                folderSelection: () => Task.FromResult<string?>(failureTarget),
                verifyPreview: text => Assert(!Directory.Exists(failureTarget) && !string.IsNullOrWhiteSpace(text),
                    "目录选择失败场景不得预建目标目录或写盘。"));
            Assert(!Directory.Exists(failureTarget) &&
                fixture.ViewModel.PhaseMessage.Contains("诊断导出失败", StringComparison.Ordinal),
                "落盘失败应显示固定、可理解的安全状态。");

            var exceptionTarget = Path.Combine(root, "exception-target");
            await ClickDiagnosticAsync(
                confirmPreview: true,
                folderSelection: () => throw new IOException("secret-path-and-exception-detail"),
                verifyPreview: text => Assert(!Directory.Exists(exceptionTarget) && !string.IsNullOrWhiteSpace(text),
                    "选择器异常场景不得写盘。"));
            Assert(fixture.ViewModel.PhaseMessage == "诊断摘要操作未能完成。请重试；详细错误信息不会显示在此界面。" &&
                !fixture.ViewModel.PhaseMessage.Contains("secret-path-and-exception-detail", StringComparison.Ordinal),
                "UI 异常必须显示固定中文说明且不泄露异常详情。");

            var staleDirectory = Path.Combine(root, "stale");
            Directory.CreateDirectory(staleDirectory);
            using (var stalePreview = fixture.ViewModel.CreateDiagnosticPreviewTicket())
            {
                fixture.ViewModel.WebPortInput = "58127";
                await fixture.ViewModel.StageWebPortAsync();
                await fixture.Host.Coordinator.StopAsync();
                PumpJobs();
                fixture.Window.WebPortApplyConfirmationForAcceptance = () => Task.FromResult(true);
                var applyButton = fixture.Window.FindControl<Button>("ApplyWebPortButton")
                    ?? throw new InvalidOperationException("未找到 Fake Host 端口确认按钮。");
                applyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(
                    () => fixture.Host.ApplyCalls == 1 && fixture.ViewModel.CanStageWebPort,
                    TimeSpan.FromSeconds(5),
                    "Fake Headless 端口确认或 Coordinator 刷新未完成。");
                var staleExportSucceeded = await AwaitWithDispatcherAsync(
                    fixture.ViewModel.ExportDiagnosticPreviewAsync(staleDirectory, stalePreview),
                    "过期诊断预览拒绝未完成。");
                Assert(!staleExportSucceeded,
                    "Coordinator generation 变化后必须拒绝旧诊断预览。");
                Assert(!Directory.EnumerateFiles(staleDirectory).Any(), "拒绝旧预览时不得创建目标或临时文件。");
            }

            Console.WriteLine("PASS diagnostic button handler preview/cancel/folder/save/failure/ticket acceptance");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    public static async Task RunAsync()
    {
        await TestUnconfirmedPreparationBlocksUiAsync();
        await TestAutomaticModePersistenceAsync();
        await TestWebPortPreflightBeforeStartAsync();
        await TestRunningStageAndCancelledConfirmationAsync();
        await TestHostGateOrderingAndSuccessfulCommitAsync();
        await TestCasAndOccupiedPortRollbackAsync();
        await TestUnknownIntentDisablesUiAsync();
        await TestUnknownPortSettingsFailClosedAsync();
        await TestRefreshFailureReleasesGateAsync();
        Console.WriteLine("WEB_PORT_UI_ACCEPTANCE=PASS");
    }

    private static async Task TestAutomaticModePersistenceAsync()
    {
        await using var fixture = await Fixture.CreateAsync(running: true);
        var oldUrl = fixture.Host.ManagementUrl;
        fixture.ViewModel.AutomaticWebPort = true;
        fixture.ViewModel.WebPortInput = "invalid-ignored-in-auto-mode";
        await fixture.ViewModel.StageWebPortAsync();
        Assert(fixture.Host.WebPortSettings.Automatic && fixture.Host.WebPortSettings.PendingPort is null &&
            fixture.Host.ManagementUrl == oldUrl && fixture.Host.ApplyCalls == 0 && !fixture.ViewModel.CanEnterWebPort,
            "自动模式保存不能重启运行服务或更改有效地址，且禁用固定端口输入。");
        fixture.ViewModel.AutomaticWebPort = false;
        fixture.ViewModel.WebPortInput = "58126";
        await fixture.ViewModel.StageWebPortAsync();
        Assert(!fixture.Host.WebPortSettings.Automatic && fixture.Host.WebPortSettings.PendingPort == 58126 &&
            fixture.Host.ManagementUrl == oldUrl && fixture.ViewModel.CanEnterWebPort,
            "固定模式须保留待应用候选并恢复输入，不能悄悄应用。");
    }

    private static async Task TestUnconfirmedPreparationBlocksUiAsync()
    {
        await using var fixture = await Fixture.CreateAsync(running: false, webPortCanBind: _ => true);
        fixture.Host.PreparationQuiescent = false;
        try
        {
            await fixture.Host.Coordinator.RefreshObservationAsync();
            PumpJobs();
            Assert(!fixture.ViewModel.CanStageWebPort && !fixture.ViewModel.CanApplyWebPort &&
                fixture.ViewModel.RequiresCloseChoice, "辅助进程不明时必须禁用端口操作并要求安全关闭");
            await AwaitWithDispatcherAsync(fixture.ViewModel.RunPrimaryActionAsync(), "准备步骤前检未结束");
            Assert(fixture.Host.Coordinator.Snapshot.State is not LauncherState.Running, "辅助进程不明时不得进入启动");
            Assert(fixture.ViewModel.Components.Single(row => row.Id == "database-provision").Status == "辅助进程未确认退出",
                "UI 必须呈现准备步骤的未确认状态");
        }
        finally
        {
            fixture.Host.PreparationQuiescent = true;
            await fixture.Host.Coordinator.RefreshObservationAsync();
            PumpJobs();
        }
    }

    private static async Task TestWebPortPreflightBeforeStartAsync()
    {
        using var occupied = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        occupied.Start(1);
        var occupiedPort = ((System.Net.IPEndPoint)occupied.LocalEndpoint).Port;
        Assert(!RealPortableStackHost.CanBindWebPort(occupiedPort),
            "实际被占用的回环端口必须被生产探测器拒绝。");

        await using (var fixture = await Fixture.CreateAsync(
            running: false,
            effectiveWebPort: occupiedPort,
            leaveCoordinatorNotStarted: true))
        {
            Assert(fixture.Host.Coordinator.Snapshot.State is LauncherState.NotStarted &&
                fixture.Host.Coordinator.Snapshot.Components.All(component => component.State is ComponentRuntimeState.Unknown),
                "首次启动回归必须从真实 NotStarted/Unknown Coordinator 状态开始。");
            await AwaitWithDispatcherAsync(
                fixture.ViewModel.RunPrimaryActionAsync(),
                "占用端口前置检查未完成。");
            PumpJobs();
            Assert(fixture.Host.Coordinator.Snapshot.State is LauncherState.NotStarted &&
                fixture.ViewModel.PhaseMessage.Contains(occupiedPort.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                fixture.ViewModel.PhaseMessage.Contains("暂存并应用一个可用端口", StringComparison.Ordinal) &&
                fixture.ViewModel.HasWebPortPanel && fixture.ViewModel.CanConfigureWebPort &&
                fixture.ViewModel.CanStageWebPort && fixture.ViewModel.IsPrimaryEnabled && !fixture.ViewModel.IsStopEnabled,
                $"占用端口状态不符：coordinator={fixture.Host.Coordinator.Snapshot.State}, phase={fixture.ViewModel.PhaseMessage}, " +
                $"panel={fixture.ViewModel.HasWebPortPanel}, configure={fixture.ViewModel.CanConfigureWebPort}, stage={fixture.ViewModel.CanStageWebPort}, " +
                $"primary={fixture.ViewModel.IsPrimaryEnabled}, stop={fixture.ViewModel.IsStopEnabled}。");
            Assert(fixture.Host.Coordinator.Snapshot.State is LauncherState.NotStarted &&
                fixture.Host.Coordinator.Snapshot.Components.All(component => component.State is ComponentRuntimeState.Stopped),
                "端口占用后必须仅刷新只读状态为全部 Stopped，不得调用 Coordinator.StartAsync。");
            fixture.ViewModel.WebPortInput = "58129";
            await AwaitWithDispatcherAsync(
                fixture.ViewModel.StageWebPortAsync(),
                "首次启动端口恢复暂存未完成。");
            PumpJobs();
            Assert(fixture.ViewModel.PendingWebPortText == "58129" && fixture.ViewModel.CanApplyWebPort &&
                fixture.Host.Coordinator.Snapshot.State is LauncherState.NotStarted &&
                fixture.Host.Coordinator.Snapshot.Components.All(component => component.State is ComponentRuntimeState.Stopped),
                "首次端口冲突恢复必须能暂存并启用应用操作，同时保持 Host 未启动。");
        }

        occupied.Stop();
        Assert(RealPortableStackHost.CanBindWebPort(occupiedPort),
            "释放后的回环端口应通过生产探测器。");

        await using (var fixture = await Fixture.CreateAsync(
            running: false,
            webPortCanBind: _ => false))
        {
            await AwaitWithDispatcherAsync(
                fixture.ViewModel.RunPrimaryActionAsync(),
                "保留端口等效前置检查未完成。");
            PumpJobs();
            Assert(fixture.Host.Coordinator.Snapshot.State is LauncherState.Stopped &&
                fixture.ViewModel.PhaseMessage.Contains("58000", StringComparison.Ordinal) &&
                fixture.ViewModel.WebPortError.Contains("暂存并应用可用端口", StringComparison.Ordinal) &&
                fixture.ViewModel.CanStageWebPort,
                "系统排除端口等效的 bind 失败必须显示端口号和设置指引，且不启动服务。");
        }

        await using (var fixture = await Fixture.CreateAsync(
            running: false,
            webPortCanBind: _ => true))
        {
            await AwaitWithDispatcherAsync(fixture.ViewModel.RunPrimaryActionAsync(), "可用端口启动未完成。");
            Assert(fixture.Host.Coordinator.Snapshot.State is LauncherState.Running,
                "真实按钮必须经共享 Runtime 完成启动。");
        }

        Console.WriteLine("WEB_PORT_START_PREFLIGHT_PASS occupied/reserved-equivalent/free; stopped host remains editable");
    }

    private static async Task TestRunningStageAndCancelledConfirmationAsync()
    {
        await using var fixture = await Fixture.CreateAsync(running: true);
        var oldUrl = fixture.ViewModel.ManagementUrl;
        fixture.ViewModel.WebPortInput = "58123";
        await fixture.ViewModel.StageWebPortAsync();
        Assert(fixture.ViewModel.EffectiveWebPortText == "58000" &&
            fixture.ViewModel.PendingWebPortText == "58123" &&
            fixture.ViewModel.ManagementUrl == oldUrl && fixture.Host.ApplyCalls == 0,
            "运行中暂存只能更新 pending，不能改变 effective 或管理 URL。");

        await fixture.Host.Coordinator.StopAsync();
        PumpJobs();
        fixture.Window.WebPortApplyConfirmationForAcceptance = () => Task.FromResult(false);
        var applyButton = fixture.Window.FindControl<Button>("ApplyWebPortButton")
            ?? throw new InvalidOperationException("未找到端口应用按钮。");
        Assert(applyButton.IsEnabled, "服务停止且存在 pending 时端口应用按钮应可用。");
        applyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpJobs();
        Assert(fixture.Host.ApplyCalls == 0 && fixture.ViewModel.EffectiveWebPortText == "58000" &&
            fixture.ViewModel.PendingWebPortText == "58123" && fixture.ViewModel.ManagementUrl == oldUrl,
            "用户取消确认后 Host Apply 不得调用，effective、pending 与 URL 必须保持。");
    }

    private static async Task TestHostGateOrderingAndSuccessfulCommitAsync()
    {
        await using var fixture = await Fixture.CreateAsync(running: true);
        var offUiPropertyNotifications = new List<string>();
        fixture.ViewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                lock (offUiPropertyNotifications)
                {
                    offUiPropertyNotifications.Add(eventArgs.PropertyName ?? "<all>");
                }
            }
        };
        await SeedBusinessStateAsync(fixture.ViewModel);
        fixture.ViewModel.WebPortInput = "58124";
        await fixture.ViewModel.StageWebPortAsync();
        await fixture.Host.Coordinator.StopAsync();
        PumpJobs();
        fixture.Host.ProbeConcurrentStart = true;
        var stableCoordinator = fixture.Host.Coordinator;

        await AwaitWithDispatcherAsync(fixture.ViewModel.ApplyPendingWebPortAsync(), "成功应用端口未完成。");
        PumpJobs();
        Assert(offUiPropertyNotifications.Count == 0,
            $"Web 端口 Apply 续接后 PropertyChanged 必须在 Avalonia UI Dispatcher 上；越线程属性：{string.Join(", ", offUiPropertyNotifications)}");
        Assert(fixture.Host.ApplyCalls == 1 && fixture.Host.HostObservedUiBusy &&
            ReferenceEquals(stableCoordinator, fixture.Host.Coordinator),
            "Fake Host 必须在 UI 操作期间保持同一 Coordinator，并通过独占维护事务核停。");
        Assert(fixture.ViewModel.EffectiveWebPortText == "58124" &&
            fixture.ViewModel.ManagementUrl == "http://127.0.0.1:58124/" &&
            fixture.ViewModel.WebPortStatus.Contains("端口已应用", StringComparison.Ordinal),
            "确认提交后 UI 必须使用候选端口地址。");
        Assert(!fixture.ViewModel.BusinessConnection.IsPaired &&
            fixture.ViewModel.BusinessConnection.ApiKeyDraft.Length == 0 &&
            !fixture.ViewModel.BusinessConnection.HasAiHealth &&
            !fixture.ViewModel.BusinessConnection.HasManualTestResult &&
            fixture.FakeBusinessClient.LogoutCalls > 0,
            "替换 Web Host 必须清理业务用户、密钥草稿、健康与手测卡片并撤销短期会话。");

        var expectedServiceState = fixture.ViewModel.ServiceState;
        PumpJobs();
        Assert(fixture.ViewModel.ServiceState == expectedServiceState,
            "同一 Coordinator 的事务事件必须收敛到最终 UI 状态。");
    }

    private static async Task TestCasAndOccupiedPortRollbackAsync()
    {
        await using (var fixture = await Fixture.CreateAsync(running: true))
        {
            await SeedBusinessStateAsync(fixture.ViewModel);
            fixture.ViewModel.BusinessConnection.ApiKeyDraft = "smoke-only-secret";
            Assert(fixture.ViewModel.BusinessConnection.ApiKeyDraft.Length > 0,
                "CAS fixture 未建立 API Key 草稿。");
            fixture.ViewModel.WebPortInput = "58125";
            await fixture.ViewModel.StageWebPortAsync();
            await fixture.Host.Coordinator.StopAsync();
            fixture.Host.Settings = fixture.Host.Settings with { Revision = fixture.Host.Settings.Revision + 1 };
            var oldUrl = fixture.ViewModel.ManagementUrl;
            await AwaitWithDispatcherAsync(fixture.ViewModel.ApplyPendingWebPortAsync(), "Host CAS 端口应用未完成。");
            Assert(fixture.ViewModel.EffectiveWebPortText == "58000" &&
                fixture.ViewModel.PendingWebPortText == "58125" && fixture.ViewModel.ManagementUrl == oldUrl &&
                fixture.ViewModel.WebPortError.Contains("变化", StringComparison.Ordinal) &&
                fixture.ViewModel.BusinessConnection.ApiKeyDraft.Length == 0 &&
                !fixture.ViewModel.BusinessConnection.IsPaired,
                "Host CAS 冲突必须保留旧 effective/URL/pending，并清除业务用户与 Key 草稿。");
        }

        await using (var fixture = await Fixture.CreateAsync(running: true))
        {
            fixture.ViewModel.WebPortInput = "58126";
            await fixture.ViewModel.StageWebPortAsync();
            await fixture.Host.Coordinator.StopAsync();
            var oldUrl = fixture.ViewModel.ManagementUrl;
            fixture.Host.CandidatePortOccupied = true;
            await AwaitWithDispatcherAsync(fixture.ViewModel.ApplyPendingWebPortAsync(), "候选占用回滚未完成。");
            PumpJobs();
            Assert(fixture.ViewModel.EffectiveWebPortText == "58000" &&
                fixture.ViewModel.PendingWebPortText == "58126" && fixture.ViewModel.ManagementUrl == oldUrl &&
                !fixture.ViewModel.BusinessConnection.HasAiHealth && !fixture.ViewModel.BusinessConnection.IsPaired,
                "候选端口占用并回滚后须保留旧 effective/URL/pending，并清理业务私有状态。");
        }
    }

    private static async Task TestUnknownIntentDisablesUiAsync()
    {
        await using var fixture = await Fixture.CreateAsync(running: true);
        await SeedBusinessStateAsync(fixture.ViewModel);
        fixture.Host.Settings = fixture.Host.Settings with
        {
            Applying = new PortableWebPortApplyIntent(Guid.NewGuid(), 58000, 58127, fixture.Host.Settings.Revision),
        };
        fixture.ViewModel.AttachWebPortUiHostForAcceptance(fixture.Host);
        await fixture.ViewModel.BusinessConnection.SetHostReadyAsync(false);
        PumpJobs();
        var pairingButton = fixture.Window.FindControl<Button>("BusinessPairingButton");
        fixture.ViewModel.OpenAdvancedSettings();
        Assert(fixture.ViewModel.ManagementUrl == "端口切换待核验" &&
            fixture.ViewModel.WebPortStatus.Contains("intent 未完成", StringComparison.Ordinal) &&
            !fixture.ViewModel.CanStageWebPort && !fixture.ViewModel.CanApplyWebPort &&
            pairingButton?.IsEnabled is false && !fixture.ViewModel.BusinessConnection.HostReady &&
            !fixture.ViewModel.BusinessConnection.IsPaired && fixture.ViewModel.BusinessConnection.ApiKeyDraft.Length == 0,
            "Applying unknown 必须隐藏管理 URL、禁用业务入口并清除上一用户数据。");
    }

    private static async Task TestUnknownPortSettingsFailClosedAsync()
    {
        await using var fixture = await Fixture.CreateAsync(running: true);
        await SeedBusinessStateAsync(fixture.ViewModel);
        var staleManualPreview = fixture.ViewModel.CreateManualAiTestPreview(useDraft: false)
            ?? throw new InvalidOperationException("Fake AI 配置无法创建端口失效前手测预览。");
        var openTargetCallsBeforeUnknown = fixture.OpenTargetCalls;
        var pairingCallsBeforeUnknown = fixture.FakeBusinessClient.BeginPairingCalls;
        var manualCallsBeforeUnknown = fixture.FakeBusinessClient.ManualTestCalls;

        fixture.Host.ThrowOnNextSettingsRead = true;
        fixture.ViewModel.AttachWebPortUiHostForAcceptance(fixture.Host);
        await AwaitWithDispatcherAsync(fixture.ViewModel.BusinessConnection.SetHostReadyAsync(false),
            "未知端口状态未能撤销业务 Host Ready。");
        fixture.ViewModel.TryOpenManagementWhenReadyForAcceptance();
        fixture.ViewModel.OpenManagementPage();
        fixture.ViewModel.OpenAdvancedSettings();
        await AwaitWithDispatcherAsync(fixture.ViewModel.BeginBusinessPairingAsync(), "未知端口状态配对门禁未完成。");
        await fixture.ViewModel.RunManualAiTestAsync(
            staleManualPreview,
            confirmed: true);
        await AwaitWithDispatcherAsync(fixture.ViewModel.RunPrimaryActionAsync(), "未知端口状态启动保护未完成。");

        Assert(fixture.ViewModel.ManagementUrl == "端口状态待核验" &&
            fixture.ViewModel.WebPortStatus.Contains("端口状态未知", StringComparison.Ordinal) &&
            !fixture.ViewModel.CanConfigureWebPort && !fixture.ViewModel.CanStageWebPort &&
            !fixture.ViewModel.CanApplyWebPort && !fixture.ViewModel.IsPrimaryEnabled &&
            !fixture.ViewModel.BusinessConnection.HostReady &&
            fixture.ViewModel.BusinessConnection.ApiKeyDraft.Length == 0 &&
            !fixture.ViewModel.BusinessConnection.IsPaired &&
            !fixture.ViewModel.BusinessConnection.HasManualTestResult &&
            fixture.Host.ApplyCalls == 0 && fixture.FakeBusinessClient.BeginPairingCalls == pairingCallsBeforeUnknown &&
            fixture.FakeBusinessClient.ManualTestCalls == manualCallsBeforeUnknown &&
            fixture.OpenTargetCalls == openTargetCallsBeforeUnknown &&
            fixture.ViewModel.PhaseMessage.Contains("端口状态未知", StringComparison.Ordinal),
            $"真实 Host 端口状态读取失败后，恢复自动打开、启动、手动打开、配对及 AI 手测入口都必须 fail closed。" +
            $" URL={fixture.ViewModel.ManagementUrl}, status={fixture.ViewModel.WebPortStatus}, phase={fixture.ViewModel.PhaseMessage}, " +
            $"configured={fixture.ViewModel.CanConfigureWebPort}, stage={fixture.ViewModel.CanStageWebPort}, apply={fixture.ViewModel.CanApplyWebPort}, " +
            $"primary={fixture.ViewModel.IsPrimaryEnabled}, hostReady={fixture.ViewModel.BusinessConnection.HostReady}, " +
            $"paired={fixture.ViewModel.BusinessConnection.IsPaired}, manualResult={fixture.ViewModel.BusinessConnection.HasManualTestResult}, " +
            $"pairCalls={fixture.FakeBusinessClient.BeginPairingCalls}/{pairingCallsBeforeUnknown}, " +
            $"manualCalls={fixture.FakeBusinessClient.ManualTestCalls}/{manualCallsBeforeUnknown}, " +
            $"openCalls={fixture.OpenTargetCalls}/{openTargetCallsBeforeUnknown}.");
    }

    private static async Task TestRefreshFailureReleasesGateAsync()
    {
        await using var fixture = await Fixture.CreateAsync(running: true);
        fixture.ViewModel.WebPortInput = "58128";
        await fixture.ViewModel.StageWebPortAsync();
        await fixture.Host.Coordinator.StopAsync();
        fixture.Host.FailRefreshAndLeaveIntentUnknown = true;

        await AwaitWithDispatcherAsync(fixture.ViewModel.ApplyPendingWebPortAsync(), "刷新故障处理未完成。");
        PumpJobs();
        Assert(fixture.ViewModel.ManagementUrl is "界面绑定待核验" or "端口切换待核验" &&
            fixture.ViewModel.WebPortStatus.Contains("待核验", StringComparison.Ordinal) &&
            fixture.ViewModel.WebPortStatus.Contains("不要重复", StringComparison.Ordinal) &&
            !fixture.ViewModel.WebPortStatus.Contains("旧端口", StringComparison.Ordinal) &&
            !fixture.ViewModel.WebPortStatus.Contains("端口已应用", StringComparison.Ordinal),
            "刷新失败且 Applying 未决时 UI 不得宣称新端口已提交或旧端口已恢复。");

        await AwaitWithDispatcherAsync(
            fixture.ViewModel.DisposeAsync().AsTask(),
            "刷新失败后关闭仍被 UI operation gate 卡住。");
    }

    private static async Task SeedBusinessStateAsync(MainWindowViewModel viewModel)
    {
        await viewModel.BusinessConnection.SetHostReadyAsync(true);
        await viewModel.BusinessConnection.BeginPairingAsync("http://127.0.0.1:58000/api/launcher/authorize");
        PumpUntil(() => viewModel.BusinessConnection.IsPaired, TimeSpan.FromSeconds(3), "Fake 当前用户配对未完成。");
        await viewModel.RefreshBusinessAiHealthAsync();
        var preview = viewModel.CreateManualAiTestPreview(useDraft: false)
            ?? throw new InvalidOperationException("Fake 已保存 AI 配置无法生成手测预览。");
        await viewModel.RunManualAiTestAsync(preview, confirmed: true);
        Assert(viewModel.BusinessConnection.IsPaired && viewModel.BusinessConnection.HasAiHealth &&
            viewModel.BusinessConnection.HasManualTestResult,
            $"替换前业务状态 fixture 未完整建立：paired={viewModel.BusinessConnection.IsPaired}, " +
            $"health={viewModel.BusinessConnection.HasAiHealth}, manual={viewModel.BusinessConnection.HasManualTestResult}, " +
            $"status={viewModel.BusinessConnection.Status}");
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            PumpJobs();
            if (timer.Elapsed > timeout) throw new TimeoutException(message);
            Thread.Sleep(5);
        }
        PumpJobs();
    }

    private static void PumpJobs() => Dispatcher.UIThread.RunJobs();

    private static async Task AwaitWithDispatcherAsync(Task task, string timeoutMessage)
    {
        PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5), timeoutMessage);
        await task;
    }

    private static async Task<T> AwaitWithDispatcherAsync<T>(Task<T> task, string timeoutMessage)
    {
        PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5), timeoutMessage);
        return await task;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(MainWindowViewModel viewModel, MainWindow window, FakeWebPortHost host, FakeBusinessClient client)
        {
            ViewModel = viewModel;
            Window = window;
            Host = host;
            FakeBusinessClient = client;
        }

        private int _openTargetCallCount;

        public MainWindowViewModel ViewModel { get; }
        public MainWindow Window { get; }
        public FakeWebPortHost Host { get; }
        public FakeBusinessClient FakeBusinessClient { get; }
        public int OpenTargetCalls => _openTargetCallCount;

        public static async Task<Fixture> CreateAsync(
            bool running,
            Func<int, bool>? webPortCanBind = null,
            int effectiveWebPort = 58000,
            bool leaveCoordinatorNotStarted = false)
        {
            var host = new FakeWebPortHost { CanBind = webPortCanBind ?? RealPortableStackHost.CanBindWebPort };
            host.Settings = host.Settings with { EffectivePort = effectiveWebPort };
            if (leaveCoordinatorNotStarted)
            {
                if (running)
                    throw new InvalidOperationException("NotStarted fixture cannot be requested as running.");
            }
            else if (running)
            {
                await host.Coordinator.StartAsync();
            }
            else
            {
                await host.Coordinator.StartAsync();
                await host.Coordinator.StopAsync();
            }

            var client = new FakeBusinessClient();
            Fixture? fixture = null;
            var viewModel = new MainWindowViewModel(
                isSimulation: false,
                bundleRoot: null,
                verifiedBundle: null,
                restoreSessionFactory: null,
                userClientFactory: _ => Task.FromResult<ILauncherBusinessUserClient>(client),
                businessOpenTargetForAcceptance: _ =>
                {
                    if (fixture is not null) Interlocked.Increment(ref fixture._openTargetCallCount);
                    return true;
                },
                businessPairingPollIntervalForAcceptance: TimeSpan.FromMilliseconds(1));
            host.ViewModelGateProbe = () => !viewModel.CanStageWebPort;
            viewModel.AttachWebPortUiHostForAcceptance(host);
            await viewModel.BusinessConnection.SetHostReadyAsync(running);
            var window = new MainWindow(viewModel, skipInitialization: true)
            {
                Width = 1120,
                Height = 760,
            };
            window.Show();
            PumpJobs();
            fixture = new Fixture(viewModel, window, host, client);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await AwaitWithDispatcherAsync(ViewModel.DisposeAsync().AsTask(), "Fake VM dispose 未完成。");
            Window.Hide();
        }
    }

    private sealed class FakeWebPortHost : IPortableWebPortUiHost
    {
        private PortableWebPortSettings _settings = new(Guid.NewGuid(), 0, 58000, null, null);
        private readonly int _postgresPort = 55432;

        public FakeWebPortHost()
        {
            Coordinator = CreateCoordinator();
            Runtime = new LauncherRuntime(Coordinator, (action, token) =>
                Coordinator.RunMaintenanceAsync(maintenance =>
                {
                    if (_settings.Applying is not null) throw new InvalidOperationException("未决端口事务");
                    return action(new StartSession(this, maintenance));
                }, token));
        }
        public LauncherRuntime Runtime { get; }
        public Func<int, bool> CanBind { get; init; } = _ => true;
        private sealed class StartSession(FakeWebPortHost host, LauncherCoordinator.MaintenanceSession maintenance)
            : ILauncherStartSession
        {
            public LauncherStartSettings Settings => new(host._settings.EffectivePort,
                host._settings.Automatic, host._settings.PendingPort is not null);
            public bool CanBindEffectivePort() => host.CanBind(host._settings.EffectivePort);
            public async Task<LauncherStartResult> StartCurrentAsync(CancellationToken token) =>
                new(await maintenance.StartAsync(token), host._settings.EffectivePort, false, false);
            public async Task<LauncherStartResult> StartOnAutomaticPortAsync(CancellationToken token)
            {
                host._settings = host._settings with { EffectivePort = 58131 };
                var result = await maintenance.StartAsync(token);
                return new(result, host._settings.EffectivePort, true, false);
            }
        }
        public bool PreparationQuiescent { get; set; } = true;

        public LauncherCoordinator Coordinator { get; private set; }
        public int PostgresPort => _postgresPort;
        public string ManagementUrl => $"http://127.0.0.1:{_settings.EffectivePort}/";
        public bool SetupRequired => false;
        public int ApplyCalls { get; private set; }
        public bool CandidatePortOccupied { get; set; }
        public bool ProbeConcurrentStart { get; set; }
        public bool HostObservedUiBusy { get; private set; }
        public bool FailRefreshAndLeaveIntentUnknown { get; set; }
        public bool ThrowOnNextSettingsRead { get; set; }
        public PortableWebPortSettings Settings
        {
            get => _settings;
            set => _settings = value;
        }
        public PortableWebPortSettings WebPortSettings
        {
            get
            {
                if (ThrowOnNextSettingsRead)
                {
                    ThrowOnNextSettingsRead = false;
                    throw new IOException("Fake 刷新设置读取失败。");
                }

                return _settings;
            }
        }

        public PortableWebPortSettings StageWebPort(long expectedRevision, int candidatePort, bool automatic = false)
        {
            if (expectedRevision != _settings.Revision)
                throw new PortableWebPortSettingsException("Web 端口配置已变化；请刷新后重试。");
            if (candidatePort == _postgresPort)
                throw new PortableWebPortSettingsException("Web 端口不能与 PostgreSQL 端口相同。");
            _settings = _settings with
            {
                Revision = checked(_settings.Revision + 1),
                PendingPort = candidatePort == _settings.EffectivePort ? null : candidatePort,
                Automatic = automatic,
            };
            return _settings;
        }

        public async Task<PortableWebPortApplyResult> ApplyPendingWebPortAsync(long expectedRevision)
        {
            ApplyCalls++;
            await Task.Delay(10).ConfigureAwait(false);
            return await Coordinator.RunMaintenanceAsync(async maintenance =>
            {
                HostObservedUiBusy = ViewModelGateProbe();
                if (_settings.Revision != expectedRevision || _settings.PendingPort is not { } candidatePort ||
                    _settings.Applying is not null)
                    throw new PortableWebPortSettingsException("Web 端口待处理状态已变化；刷新后重试。");
                var intent = new PortableWebPortApplyIntent(Guid.NewGuid(), _settings.EffectivePort, candidatePort, _settings.Revision);
                _settings = _settings with { Applying = intent };
                if (FailRefreshAndLeaveIntentUnknown)
                {
                    ThrowOnNextSettingsRead = true;
                    throw new InvalidOperationException("Fake Host 保留 Applying intent 以模拟未知事务。");
                }
                if (ProbeConcurrentStart)
                {
                    var rejected = false;
                    try { await Coordinator.StartAsync(); }
                    catch (InvalidOperationException) { rejected = true; }
                    Assert(rejected, "维护事务必须拒绝插入另一轮启动");
                }
                await maintenance.StartAsync();
                if (CandidatePortOccupied)
                {
                    _settings = _settings with { Applying = null };
                    return new PortableWebPortApplyResult(false, intent.FromPort, candidatePort, "候选端口占用；旧端口已恢复，候选仍待处理。");
                }
                _settings = _settings with { EffectivePort = candidatePort, PendingPort = null, Applying = null };
                return new PortableWebPortApplyResult(true, candidatePort, null, "候选端口已 Ready。");
            });
        }

        internal Func<bool> ViewModelGateProbe { private get; set; } = () => false;

        private LauncherCoordinator CreateCoordinator() => new(
            new[]
            {
                new LauncherComponentRegistration(new FakeComponent("postgres", "PostgreSQL")),
                new LauncherComponentRegistration(new FakeComponent("python-web", "Python Web")),
            },
            isSimulation: false,
            logCapacity: 32,
            startupSteps: [new(new FakePreparation(this), "python-web")]);

        private sealed class FakePreparation(FakeWebPortHost owner) : ILauncherStartupStep
        {
            public string Id => "database-provision";
            public string DisplayName => "业务数据库准备";
            public Task ExecuteAsync(CancellationToken token) => Task.CompletedTask;
            public Task VerifyCompletedAsync(CancellationToken token) => Task.CompletedTask;
            public ValueTask<bool> IsQuiescentAsync(CancellationToken token) => ValueTask.FromResult(owner.PreparationQuiescent);
        }

        private sealed class FakeComponent(string id, string displayName) : ILauncherComponent
        {
            private ComponentRuntimeState _state = ComponentRuntimeState.Stopped;
            public string Id { get; } = id;
            public string DisplayName { get; } = displayName;
            public ValueTask<ComponentRuntimeState> GetStateAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(_state);
            }
            public Task StartAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _state = ComponentRuntimeState.Running;
                return Task.CompletedTask;
            }
            public Task StopAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _state = ComponentRuntimeState.Stopped;
                return Task.CompletedTask;
            }
        }
    }

    private sealed class FakeBusinessClient : ILauncherBusinessUserClient
    {
        private static readonly Guid InstanceId = Guid.Parse("343164d5-e348-4adf-bd50-25c8e36b08d5");
        public event Action? UserDataInvalidated { add { } remove { } }
        public int LogoutCalls { get; private set; }
        public int BeginPairingCalls { get; private set; }
        public int ManualTestCalls { get; private set; }
        public Task<LauncherPairingPrompt> BeginPairingAsync(CancellationToken cancellationToken = default)
        {
            BeginPairingCalls++;
            return Task.FromResult(new LauncherPairingPrompt("123456", DateTimeOffset.UtcNow.AddMinutes(1), 60));
        }
        public Task<LauncherPairingExchangeResult> ExchangePairingOnceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LauncherPairingExchangeResult(false, null));
        public Task<LauncherUserIdentity> GetCurrentUserAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LauncherUserIdentity("smoke-user", "Smoke User", true, true, true, InstanceId));
        public Task<LauncherAiConfiguration> GetAiConfigurationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LauncherAiConfiguration(true, "https://api.example.test/v1", false, "smoke-model", 1,
                "smoke-config", "postgres_user_config", "ready", true, false));
        public Task<LauncherAiHealthSnapshot> GetAiHealthAsync(CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new LauncherAiHealthSnapshot("current", "healthy", now, now.AddMinutes(1), 60,
                "smoke-config", 1, "postgres_user_config", 1));
        }
        public Task<LauncherAiConfiguration> SaveAiConfigurationAsync(int configRevision, string configId,
            string? baseUrl, string? modelName, string? replacementApiKey, bool removeApiKey = false,
            CancellationToken cancellationToken = default,
        string? tokensParameter = null,
        int? tokensLimit = null) => GetAiConfigurationAsync(cancellationToken);
        public Task<LauncherAiManualTestResult> RunManualAiTestAsync(bool explicitlyConfirmed, string requestId,
            string expectedConfigId, int expectedConfigRevision, string? baseUrl, string? modelName, string? apiKey,
            CancellationToken cancellationToken = default)
        {
            ManualTestCalls++;
            return Task.FromResult(new LauncherAiManualTestResult(true, "success", "Fake 手测已完成。", 1, DateTimeOffset.UtcNow));
        }
        public Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            LogoutCalls++;
            return Task.CompletedTask;
        }
        public void ClearUserDataCache() { }
        public void Dispose() { }
    }
}
