using System.Net;
using AiGoofish.Launcher.App;
using AiGoofish.Launcher.Platform.Windows;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AiGoofish.Launcher.Ui.Smoke;

internal static class BusinessConnectionAcceptance
{
    public static async Task RunAsync()
    {
        TestFixedLocalRoutes();
        await TestSimulationCannotPairAsync();
        await TestPairingSaveConflictLogoutAndInvalidationAsync();
        await TestExpiredPairingAndReadOnlyIdentityAsync();
        await TestValidationBindingAndKeyboardFocusAsync();
        await TestAiHealthLocalValidationAndManualTestAsync();
        Console.WriteLine("PASS Launcher business pairing, account invalidation, AI configuration CAS and safe routes");
    }

    private static async Task TestAiHealthLocalValidationAndManualTestAsync()
    {
        var fake = new FakeBusinessClient
        {
            ExchangeResults = new Queue<LauncherPairingExchangeResult>(new[]
            {
                new LauncherPairingExchangeResult(false, DateTimeOffset.UtcNow.AddHours(1)),
            }),
        };
        var viewModel = new LauncherBusinessConnectionViewModel(
            _ => Task.FromResult<ILauncherBusinessUserClient>(fake),
            _ => true,
            pairingPollInterval: TimeSpan.FromMilliseconds(1));
        try
        {
            await viewModel.SetHostReadyAsync(true);
            await viewModel.BeginPairingAsync("http://127.0.0.1:58000/api/launcher/authorize");
            WaitUntil(() => viewModel.IsPaired, "AI 健康与测试验收未完成账号配对。");

            var healthReadsBeforeRefresh = fake.HealthReadCalls;
            await viewModel.RefreshAiHealthAsync();
            Assert(fake.HealthReadCalls == healthReadsBeforeRefresh + 1 && viewModel.HasAiHealth && !viewModel.IsHealthHealthy &&
                viewModel.HealthFreshnessText.Contains("尚未检测", StringComparison.Ordinal),
                "never_checked 缓存只能显示尚未检测，不能显示健康。");

            var now = DateTimeOffset.UtcNow;
            var currentConfiguration = fake.CurrentConfiguration;
            fake.HealthSnapshot = new LauncherAiHealthSnapshot(
                "current", "healthy", now, now.AddSeconds(30), 0,
                currentConfiguration.ConfigId, currentConfiguration.ConfigRevision, "postgres_user_config", 12);
            await viewModel.RefreshAiHealthAsync();
            Assert(!viewModel.IsHealthHealthy && viewModel.HealthFreshnessText.Contains("过期", StringComparison.Ordinal) &&
                viewModel.HealthCategoryText.Contains("过期", StringComparison.Ordinal),
                "current+healthy 但 TTL=0 的缓存必须立即按过期展示且不得标绿。");
            Assert(!LauncherBusinessConnectionViewModel.IsAiHealthSnapshotCurrentAt(
                fake.HealthSnapshot with { RemainingTtlSeconds = 30, ExpiresAtUtc = null },
                currentConfiguration,
                DateTimeOffset.UtcNow),
                "缺少 expires_at 的 current 缓存不能标记为当前或健康。");

            fake.HealthSnapshot = new LauncherAiHealthSnapshot(
                "current", "healthy", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(2), 2,
                currentConfiguration.ConfigId, currentConfiguration.ConfigRevision, "postgres_user_config", 12);
            await viewModel.RefreshAiHealthAsync();
            Assert(viewModel.IsHealthHealthy && viewModel.HealthCategoryText == "连接可用",
                "有效期内、配置版本匹配的健康缓存应正常显示。");
            var healthReadsBeforeExpiry = fake.HealthReadCalls;
            WaitUntil(() => !viewModel.IsHealthHealthy && viewModel.HealthObservedText.Contains("TTL 0 秒", StringComparison.Ordinal),
                "健康缓存 ExpiresAt 到期后本地 timer 应刷新状态和 TTL 展示。");
            Assert(fake.HealthReadCalls == healthReadsBeforeExpiry &&
                viewModel.HealthFreshnessText.Contains("过期", StringComparison.Ordinal) &&
                viewModel.HealthCategoryText.Contains("过期", StringComparison.Ordinal) &&
                viewModel.HealthObservedText.Contains("TTL 0 秒", StringComparison.Ordinal),
                "健康缓存到期刷新只能本地降级文案，不能联网或保留连接可用状态。");

            var requestsBeforeLocalCheck = fake.AiRequestCalls;
            viewModel.CheckConfigurationLocally();
            Assert(fake.AiRequestCalls == requestsBeforeLocalCheck && fake.ManualTestCalls == 0 &&
                viewModel.Status.Contains("本地格式", StringComparison.Ordinal),
                "本地配置检查不得访问 AI 测试端点。");

            var noConfirmPreview = viewModel.CreateManualTestPreview(useDraft: false);
            Assert(noConfirmPreview is not null, "已保存有效配置应可生成手测预览。");
            await viewModel.RunManualAiTestAsync(noConfirmPreview!, explicitlyConfirmed: false);
            Assert(fake.ManualTestCalls == 0, "没有显式确认时不得发出手测请求。");

            var savedPreview = viewModel.CreateManualTestPreview(useDraft: false)!;
            await viewModel.RunManualAiTestAsync(savedPreview, explicitlyConfirmed: true);
            Assert(fake.ManualTestCalls == 1 && fake.LastManualTestConfirmed &&
                fake.LastManualTestExpectedConfigId == savedPreview.ConfigId &&
                fake.LastManualTestExpectedConfigRevision == savedPreview.ConfigRevision &&
                fake.LastManualTestBaseUrl is null && fake.LastManualTestModel is null && fake.LastManualTestApiKey is null &&
                !string.IsNullOrWhiteSpace(fake.LastManualTestRequestId),
                "确认已保存配置后必须恰好发送一次且不把密钥或配置覆写混入请求。"
            );

            viewModel.BaseUrl = "https://new-api.example.net/v1";
            viewModel.ModelName = "draft-model";
            viewModel.ApiKeyDraft = "draft-secret-key";
            var draftPreview = viewModel.CreateManualTestPreview(useDraft: true);
            Assert(draftPreview is not null && draftPreview.TargetOrigin == "https://new-api.example.net" &&
                draftPreview.ModelName == "draft-model" && draftPreview.UsesReplacementApiKey,
                "草稿预览应明确展示变更目标和新模型，并要求新目标使用重新输入的 Key。");
            await viewModel.RunManualAiTestAsync(draftPreview!, explicitlyConfirmed: true);
            Assert(fake.ManualTestCalls == 2 && fake.LastManualTestBaseUrl == "https://new-api.example.net/v1" &&
                fake.LastManualTestModel == "draft-model" && fake.LastManualTestApiKey == "draft-secret-key" &&
                fake.LastManualTestExpectedConfigId == draftPreview!.ConfigId &&
                fake.LastManualTestExpectedConfigRevision == draftPreview.ConfigRevision &&
                viewModel.ApiKeyDraft.Length == 0,
                "确认草稿后仅本次请求可收到草稿字段，结束后应清除密钥草稿。"
            );

            fake.BlockManualTestUntilCancelled = true;
            var cancellationPreview = viewModel.CreateManualTestPreview(useDraft: false)!;
            var pending = viewModel.RunManualAiTestAsync(cancellationPreview, explicitlyConfirmed: true);
            WaitUntil(() => viewModel.IsManualTestRunning, "手动测试没有进入等待状态。");
            viewModel.CancelManualAiTest();
            WaitUntil(() => pending.IsCompleted, "取消手动测试后 Headless Dispatcher 未完成 fake 请求清理。");
            await pending;
            Assert(fake.ManualTestCalls == 3 && viewModel.IsManualTestUnknown && !viewModel.IsManualTestRunning,
                "取消等待后应显示 unknown，不能重试或将其当成失败/成功的确定结论。");

            fake.BlockManualTestUntilCancelled = false;
            fake.ThrowConflictOnNextManualTest = true;
            var conflictPreview = viewModel.CreateManualTestPreview(useDraft: false)!;
            await viewModel.RunManualAiTestAsync(conflictPreview, explicitlyConfirmed: true);
            var callsAfterConflict = fake.ManualTestCalls;
            Assert(callsAfterConflict == 4 &&
                fake.LastManualTestExpectedConfigId == conflictPreview.ConfigId &&
                fake.LastManualTestExpectedConfigRevision == conflictPreview.ConfigRevision &&
                !viewModel.IsManualTestUnknown && !viewModel.CanRunManualTest &&
                viewModel.Status.Contains("本次未发送 AI 请求", StringComparison.Ordinal) &&
                viewModel.Status.Contains("重新读取配置", StringComparison.Ordinal),
                "409 必须作为供应商请求前冲突处理、作废旧确认并要求重新读取后才能再次测试。");
            Assert(viewModel.CreateManualTestPreview(useDraft: false) is null && fake.ManualTestCalls == callsAfterConflict,
                "409 后不能在刷新前再次使用陈旧配置预览发出请求。");

            await viewModel.RefreshConfigurationAsync();
            Assert(viewModel.CanRunManualTest && viewModel.ConfigRevisionText == $"配置版本：{fake.CurrentConfiguration.ConfigRevision}",
                    "明确重新读取新配置后应解除手测刷新门控，并清除旧草稿。");

            fake.CurrentConfiguration = fake.CurrentConfiguration with { ConfigId = string.Empty, ConfigRevision = 0 };
            await viewModel.RefreshConfigurationAsync();
            var noConfigPreview = viewModel.CreateManualTestPreview(useDraft: false);
            Assert(noConfigPreview is { ConfigId: "", ConfigRevision: 0 }, "无配置 sentinel 预览必须捕获空 Config ID 和 revision 0。");
            await viewModel.RunManualAiTestAsync(noConfigPreview!, explicitlyConfirmed: true);
            Assert(fake.ManualTestCalls == 5 && fake.LastManualTestExpectedConfigId == string.Empty &&
                fake.LastManualTestExpectedConfigRevision == 0,
                    "无配置 sentinel 必须按 preview 捕获值透传，不能回退读取可变配置。");

        }
        finally
        {
            await viewModel.DisposeAsync();
        }
    }

    private static void TestFixedLocalRoutes()
    {
        const string managementUrl = "http://127.0.0.1:58000/";
        Assert(MainWindowViewModel.BuildLauncherAuthorizationUri(managementUrl) ==
            "http://127.0.0.1:58000/api/launcher/authorize", "配对浏览器地址必须固定且不携带 code/token。");
        Assert(MainWindowViewModel.BuildWebSettingsUri(managementUrl) ==
            "http://127.0.0.1:58000/#settings", "Web 高级设置跳转必须指向已验证设置 section。");
        AssertThrows(() => MainWindowViewModel.BuildLauncherAuthorizationUri("https://127.0.0.1:58000/"), "必须拒绝错误协议。");
        AssertThrows(() => MainWindowViewModel.BuildLauncherAuthorizationUri("http://localhost:58000/"), "必须拒绝非固定 loopback 主机名。");
        AssertThrows(() => MainWindowViewModel.BuildWebSettingsUri("http://127.0.0.1:58000/?token=secret"), "必须拒绝带查询串的管理基址。");
    }

    private static async Task TestSimulationCannotPairAsync()
    {
        var factoryCalls = 0;
        var viewModel = new LauncherBusinessConnectionViewModel(
            _ => { factoryCalls++; return Task.FromResult<ILauncherBusinessUserClient>(new FakeBusinessClient()); },
            _ => throw new InvalidOperationException("模拟模式不得打开浏览器。"),
            isSimulation: true,
            pairingPollInterval: TimeSpan.FromMilliseconds(1));
        await viewModel.SetHostReadyAsync(true);
        await viewModel.BeginPairingAsync("http://127.0.0.1:58000/api/launcher/authorize");
        Assert(!viewModel.HostReady && !viewModel.CanBeginPairing && factoryCalls == 0,
            "模拟模式必须保持业务连接关闭且不得创建真实 client。");
        await viewModel.DisposeAsync();
    }

    private static async Task TestPairingSaveConflictLogoutAndInvalidationAsync()
    {
        var fake = new FakeBusinessClient
        {
            ExchangeResults = new Queue<LauncherPairingExchangeResult>(new[]
            {
                new LauncherPairingExchangeResult(true, null),
                new LauncherPairingExchangeResult(false, DateTimeOffset.UtcNow.AddHours(1)),
            }),
        };
        var opened = new List<string>();
        var viewModel = new LauncherBusinessConnectionViewModel(
            _ => Task.FromResult<ILauncherBusinessUserClient>(fake),
            target => { opened.Add(target); return true; },
            pairingPollInterval: TimeSpan.FromMilliseconds(2));
        await viewModel.SetHostReadyAsync(true);
        Assert(viewModel.CanBeginPairing, "仅真实 ready Host 应开放配对入口。");
        await viewModel.BeginPairingAsync("http://127.0.0.1:58000/api/launcher/authorize");
        Assert(viewModel.IsPairing && viewModel.PairingCode == "ABCD1234EFGH", "应显示 12 位配对码和有效期限。");
        Assert(opened.Single() == "http://127.0.0.1:58000/api/launcher/authorize" &&
            !opened.Single().Contains("ABCD1234EFGH", StringComparison.Ordinal), "授权 URL 不能携带配对码或令牌。");
        WaitUntil(() => viewModel.IsPaired, "浏览器明确批准后 Launcher 未完成一次性兑换。");
        Assert(fake.CurrentUserCalls == 1 && fake.AiReadCalls == 1, "兑换成功后必须读取当前用户和脱敏 AI 配置。");
        Assert(viewModel.AccountSummary.Contains("demo-user", StringComparison.Ordinal) &&
            viewModel.KeyState == "已设置（不回显）" && viewModel.ConfigRevisionText == "配置版本：7" &&
            viewModel.ConfigSource == "当前用户默认 AI 配置" && viewModel.EffectiveState.Contains("新请求", StringComparison.Ordinal),
            "已配对卡片应展示当前用户、密钥设置状态、revision、source 与实际生效状态。");
        var oldClientInvalidation = fake.CaptureInvalidationHandler();

        var savesBeforeLocalValidation = fake.SaveCalls;
        viewModel.BaseUrl = "not an absolute URL";
        viewModel.ModelName = string.Empty;
        await viewModel.SaveAsync();
        Assert(fake.SaveCalls == savesBeforeLocalValidation && viewModel.HasValidationErrors &&
            viewModel.HasBaseUrlError && viewModel.HasModelNameError &&
            viewModel.ValidationFocusTarget == "baseUrl", "本地 URL/model 校验应阻止请求并指出首个字段。");
        viewModel.BaseUrl = "https://api.example.com/v1";
        viewModel.ModelName = "demo-model";
        viewModel.ApiKeyDraft = new string('k', 4097);
        await viewModel.SaveAsync();
        Assert(fake.SaveCalls == savesBeforeLocalValidation && viewModel.ApiKeyDraft.Length == 0 &&
            viewModel.HasApiKeyError && viewModel.ValidationFocusTarget == "apiKey" &&
            viewModel.ValidationSummary.Contains("API Key", StringComparison.Ordinal) &&
            !viewModel.ValidationSummary.Contains(new string('k', 24), StringComparison.Ordinal),
            "超长 API Key 必须在本地拒绝、清除密钥草稿并显示不含密钥的可定位字段错误。");
        viewModel.ApiKeyDraft = "short-key";
        viewModel.ApiKeyDraft = string.Empty;
        Assert(!viewModel.HasApiKeyError, "重新编辑 API Key 后应清除旧字段错误。");
        Assert(!viewModel.HasValidationErrors, "修正字段后本地错误应清除。");
        await viewModel.RefreshConfigurationAsync();

        fake.ThrowBadRequestOnNextSave = true;
        viewModel.ModelName = "server-rejected-model";
        viewModel.ApiKeyDraft = "key-must-not-remain-after-failure";
        var revisionBeforeBadRequest = viewModel.ConfigRevisionText;
        await viewModel.SaveAsync();
        Assert(viewModel.ModelName == "server-rejected-model" && viewModel.ModelNameError.Length > 0 &&
            viewModel.ApiKeyDraft.Length == 0 && viewModel.ConfigRevisionText == revisionBeforeBadRequest &&
            !viewModel.ValidationSummary.Contains("raw server detail", StringComparison.Ordinal),
            "普通服务端校验失败应保留非密钥草稿与原 revision、清除密钥并使用安全错误文案。");
        viewModel.ModelName = "demo-model";
        Assert(!viewModel.HasValidationErrors, "编辑对应字段后服务端字段错误应清除。");

        viewModel.ModelName = "new-model";
        viewModel.ApiKeyDraft = "replacement-key-for-test";
        await viewModel.SaveAsync();
        Assert(fake.SavedModel == "new-model" && fake.SavedBaseUrl is null && fake.SavedReplacementKey == "replacement-key-for-test" &&
            fake.SavedRemoveKey is false, "保存必须字段级提交，仅提交修改字段并允许替换 Key。");
        Assert(fake.AiRequestCalls == 0 && viewModel.ApiKeyDraft.Length == 0, "保存不能触发 AI 请求，且提交后清空 Key 草稿。");

        viewModel.ModelName = "keep-key-model";
        await viewModel.SaveAsync();
        Assert(fake.SavedModel == "keep-key-model" && fake.SavedReplacementKey is null && fake.SavedRemoveKey is false,
            "API Key 留空必须表示保持原值，不得回写掩码或清除密钥。");

        var beforeOriginChange = fake.SaveCalls;
        viewModel.BaseUrl = "https://different.example/v1";
        await viewModel.SaveAsync();
        Assert(fake.SaveCalls == beforeOriginChange && viewModel.Status.Contains("重新绑定", StringComparison.Ordinal),
            "更改 Base URL 主机时必须要求重新绑定 Key 或明确移除，不能把旧 Key 发往新目标。");
        viewModel.ClearDrafts();

        fake.ThrowConflictOnNextSave = true;
        viewModel.ModelName = "stale-model";
        viewModel.ApiKeyDraft = "stale-key";
        await viewModel.SaveAsync();
        Assert(fake.AiReadCalls == 3 && viewModel.ModelName == "server-model" &&
            viewModel.ApiKeyDraft.Length == 0 && !viewModel.CanSave &&
            viewModel.Status.Contains("其他窗口修改", StringComparison.Ordinal),
            "409 后必须丢弃旧草稿并读新 revision，不得静默重发旧值。");

        fake.CurrentConfiguration = fake.CurrentConfiguration with { BaseUrl = null, BaseUrlRedacted = true };
        await viewModel.RefreshConfigurationAsync();
        Assert(viewModel.IsBaseUrlRedacted && !viewModel.IsBaseUrlEditable && viewModel.BaseUrl.Length == 0,
            "Base URL 已脱敏时不得呈现或编辑空值作为真实地址。");
        viewModel.ModelName = "redacted-url-model";
        await viewModel.SaveAsync();
        Assert(fake.SavedBaseUrl is null, "保存其他字段时脱敏 Base URL 必须省略，不得写回空字符串。");

        fake.CurrentConfiguration = fake.CurrentConfiguration with { ApiKeySet = true, ConfigRevision = 9 };
        await viewModel.RefreshConfigurationAsync();
        var savesBeforeUnconfirmedRemoval = fake.SaveCalls;
        await viewModel.RemoveApiKeyAsync(confirmed: false);
        Assert(fake.SaveCalls == savesBeforeUnconfirmedRemoval, "未明确确认时不得移除 API Key。");
        await viewModel.RemoveApiKeyAsync(confirmed: true);
        Assert(fake.SavedRemoveKey is true && fake.AiRequestCalls == 0, "明确确认移除 Key 只保存配置，不发送 AI 请求。");

        fake.CurrentIdentity = fake.CurrentIdentity with { UserId = "u-switched", Username = "other-user" };
        viewModel.ApiKeyDraft = "discard-on-account-switch";
        await viewModel.RefreshSessionAsync();
        Assert(!viewModel.IsPaired && !viewModel.IsConfigurationVisible && viewModel.ApiKeyDraft.Length == 0 &&
            fake.DisposeCalls > 0, "Web 账号切换检查必须撤销旧 client 并清除所有卡片和草稿。");

        fake = new FakeBusinessClient
        {
            ExchangeResults = new Queue<LauncherPairingExchangeResult>(new[]
            {
                new LauncherPairingExchangeResult(false, DateTimeOffset.UtcNow.AddHours(1)),
            }),
        };
        await viewModel.BeginPairingAsync("http://127.0.0.1:58000/api/launcher/authorize");
        WaitUntil(() => viewModel.IsPaired, "账号切换后重新配对未成功。");
        oldClientInvalidation();
        PumpDispatcherFor(TimeSpan.FromMilliseconds(25));
        Assert(viewModel.IsPaired && viewModel.AccountSummary.Contains("demo-user", StringComparison.Ordinal) &&
            viewModel.ModelName == fake.CurrentConfiguration.ModelName,
            "旧 client 的延迟 invalidation 回调不得清除新用户卡片或新草稿。");
        fake.RaiseInvalidated();
        WaitUntil(() => !viewModel.IsPaired && !viewModel.IsConfigurationVisible,
            "客户端会话失效后 UI 私有数据必须立即清除。");
        WaitUntil(() => fake.DisposeCalls > 0, "失效 client 应在当前操作释放后被销毁。");
        Assert(viewModel.PairingCode.Length == 0 && viewModel.BaseUrl.Length == 0 && viewModel.ModelName.Length == 0 &&
            viewModel.ApiKeyDraft.Length == 0, "会话失效必须清空配对码、配置卡片和所有草稿。");

        await viewModel.SetHostReadyAsync(true);
        fake = new FakeBusinessClient
        {
            ExchangeResults = new Queue<LauncherPairingExchangeResult>(new[]
            {
                new LauncherPairingExchangeResult(false, DateTimeOffset.UtcNow.AddHours(1)),
            }),
        };
        await viewModel.BeginPairingAsync("http://127.0.0.1:58000/api/launcher/authorize");
        WaitUntil(() => viewModel.IsPaired, "切换账号用第二次显式授权后应可重新连接。");
        await viewModel.SetHostReadyAsync(false);
        Assert(!viewModel.IsPaired && !viewModel.IsConfigurationVisible && viewModel.ApiKeyDraft.Length == 0,
            "Host 停止或进入维护时必须清除账号和配置。");
        Assert(fake.LogoutCalls > 0, "Host 退出前应尝试撤销短期账号会话。");
        await viewModel.DisposeAsync();
    }

    private static async Task TestExpiredPairingAndReadOnlyIdentityAsync()
    {
        var expiredFake = new FakeBusinessClient
        {
            Prompt = new LauncherPairingPrompt("ZXCV1234ASDF", DateTimeOffset.UtcNow.AddMilliseconds(30), 1),
            ExchangeResults = new Queue<LauncherPairingExchangeResult>(),
        };
        var expired = new LauncherBusinessConnectionViewModel(
            _ => Task.FromResult<ILauncherBusinessUserClient>(expiredFake), _ => true,
            pairingPollInterval: TimeSpan.FromMilliseconds(5));
        await expired.SetHostReadyAsync(true);
        await expired.BeginPairingAsync("http://127.0.0.1:58000/api/launcher/authorize");
        WaitUntil(() => !expired.IsPairing && expired.PairingCode.Length == 0, "配对过期后必须清除 code。");
        await expired.DisposeAsync();

        var readOnlyFake = new FakeBusinessClient
        {
            ExchangeResults = new Queue<LauncherPairingExchangeResult>(new[]
            {
                new LauncherPairingExchangeResult(false, DateTimeOffset.UtcNow.AddHours(1)),
            }),
            CurrentIdentity = new LauncherUserIdentity("u-readonly", "viewer", true, true, false, Guid.NewGuid()),
        };
        var readOnly = new LauncherBusinessConnectionViewModel(
            _ => Task.FromResult<ILauncherBusinessUserClient>(readOnlyFake), _ => true,
            pairingPollInterval: TimeSpan.FromMilliseconds(2));
        await readOnly.SetHostReadyAsync(true);
        await readOnly.BeginPairingAsync("http://127.0.0.1:58000/api/launcher/authorize");
        WaitUntil(() => readOnly.IsPaired, "只读用户配对没有完成。");
        Assert(!readOnly.CanWriteAi && !readOnly.CanSave && !readOnly.CanRemoveApiKey,
            "无 AI 写权限的账号必须只能读取脱敏配置。");
        await readOnly.DisposeAsync();
    }

    private static async Task TestValidationBindingAndKeyboardFocusAsync()
    {
        var fake = new FakeBusinessClient
        {
            ExchangeResults = new Queue<LauncherPairingExchangeResult>(new[]
            {
                new LauncherPairingExchangeResult(false, DateTimeOffset.UtcNow.AddHours(1)),
            }),
        };
        var viewModel = new MainWindowViewModel(
            isSimulation: false,
            bundleRoot: AppContext.BaseDirectory,
            verifiedBundle: null,
            restoreSessionFactory: null,
            userClientFactory: _ => Task.FromResult<ILauncherBusinessUserClient>(fake));
        await viewModel.BusinessConnection.SetHostReadyAsync(true);
        await viewModel.BusinessConnection.BeginPairingAsync("http://127.0.0.1:58000/api/launcher/authorize");
        WaitUntil(() => viewModel.BusinessConnection.IsPaired, "XAML acceptance fake 用户配对未完成。");

        var window = new MainWindow(viewModel, skipInitialization: true) { Width = 1120, Height = 760 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var settingsNavigation = window.FindControl<Button>("AiNavigationButton")
                ?? throw new InvalidOperationException("缺少 AI 连接导航按钮。");
            settingsNavigation.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert(viewModel.IsAiPage, "配置校验验收必须先进入 AI 连接页。");
            var url = window.FindControl<TextBox>("BusinessBaseUrlTextBox")
                ?? throw new InvalidOperationException("缺少 Base URL XAML 绑定控件。");
            var model = window.FindControl<TextBox>("BusinessModelNameTextBox")
                ?? throw new InvalidOperationException("缺少模型名称 XAML 绑定控件。");
            var summary = window.FindControl<Button>("BusinessValidationSummaryButton")
                ?? throw new InvalidOperationException("缺少配置校验摘要按钮。");
            var urlError = window.FindControl<TextBlock>("BusinessBaseUrlErrorTextBlock")
                ?? throw new InvalidOperationException("缺少 Base URL 字段错误提示。");
            var modelError = window.FindControl<TextBlock>("BusinessModelNameErrorTextBlock")
                ?? throw new InvalidOperationException("缺少模型字段错误提示。");
            var apiKey = window.FindControl<TextBox>("BusinessApiKeyTextBox")
                ?? throw new InvalidOperationException("缺少 API Key 输入控件。");
            var apiKeyError = window.FindControl<TextBlock>("BusinessApiKeyErrorTextBlock")
                ?? throw new InvalidOperationException("缺少 API Key 字段错误提示。");
            viewModel.BusinessConnection.BaseUrl = "not-a-url";
            viewModel.BusinessConnection.ModelName = "";
            await viewModel.BusinessConnection.SaveAsync();
            Dispatcher.UIThread.RunJobs();
            Assert(url.Text == "not-a-url" && model.Text == "" && urlError.IsVisible && modelError.IsVisible && summary.IsVisible,
                "校验字段与摘要必须通过真实 XAML binding 显示对应草稿和错误状态。");
            Assert(summary.IsTabStop && summary.TabIndex == 5,
                "错误摘要必须能由键盘 Tab 到达。");
            summary.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert(url.IsFocused, "激活错误摘要后应将焦点定位到第一个错误字段。");
            viewModel.BusinessConnection.BaseUrl = "https://api.example.com/v1";
            viewModel.BusinessConnection.ModelName = "demo-model";
            viewModel.BusinessConnection.ApiKeyDraft = new string('s', 4097);
            await viewModel.BusinessConnection.SaveAsync();
            Dispatcher.UIThread.RunJobs();
            Assert(apiKeyError.IsVisible && (apiKey.Text ?? string.Empty).Length == 0 && summary.IsVisible &&
                viewModel.BusinessConnection.ValidationFocusTarget == "apiKey",
                "超长 API Key 必须通过真实 XAML 显示邻接错误、清空密钥输入并保持摘要可用。");
            summary.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert(apiKey.IsFocused, "API Key 摘要错误必须把键盘焦点定位到密钥输入框。");
        }
        finally
        {
            if (window.IsVisible)
            {
                typeof(MainWindow).GetField("_allowClose", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.SetValue(window, true);
                window.Close();
            }
        }
    }

    private static void WaitUntil(Func<bool> condition, string message)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        Assert(condition(), message);
    }

    private static void PumpDispatcherFor(TimeSpan duration)
    {
        var deadline = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }

    internal sealed class FakeBusinessClient : ILauncherBusinessUserClient
    {
        public event Action? UserDataInvalidated;
        public LauncherPairingPrompt Prompt { get; init; } = new("ABCD1234EFGH", DateTimeOffset.UtcNow.AddMinutes(5), 300);
        public Queue<LauncherPairingExchangeResult> ExchangeResults { get; init; } = new();
        public LauncherUserIdentity CurrentIdentity { get; set; } = new("u-demo", "demo-user", true, true, true, Guid.NewGuid());
        public LauncherAiConfiguration CurrentConfiguration { get; set; } = new(true, "https://api.example.com/v1", false,
            "demo-model", 7, "config-7", "user_default_api_config", "applies_to_new_requests", false, false);
        public int CurrentUserCalls { get; private set; }
        public int AiReadCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public int AiRequestCalls { get; private set; }
        public int HealthReadCalls { get; private set; }
        public int ManualTestCalls { get; private set; }
        public bool LastManualTestConfirmed { get; private set; }
        public string? LastManualTestRequestId { get; private set; }
        public string? LastManualTestExpectedConfigId { get; private set; }
        public int LastManualTestExpectedConfigRevision { get; private set; }
        public string? LastManualTestBaseUrl { get; private set; }
        public string? LastManualTestModel { get; private set; }
        public string? LastManualTestApiKey { get; private set; }
        public bool BlockManualTestUntilCancelled { get; set; }
        public bool ThrowConflictOnNextManualTest { get; set; }
        public LauncherAiHealthSnapshot HealthSnapshot { get; set; } = new(
            "never_checked", "unknown", null, null, null, "config-7", 7, "postgres_user_config", null);
        public int LogoutCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool ThrowConflictOnNextSave { get; set; }
        public bool ThrowBadRequestOnNextSave { get; set; }
        public string? SavedBaseUrl { get; private set; }
        public string? SavedModel { get; private set; }
        public string? SavedReplacementKey { get; private set; }
        public bool? SavedRemoveKey { get; private set; }

        public Task<LauncherPairingPrompt> BeginPairingAsync(CancellationToken cancellationToken = default) => Task.FromResult(Prompt);
        public Task<LauncherPairingExchangeResult> ExchangePairingOnceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ExchangeResults.Count > 0 ? ExchangeResults.Dequeue() : new LauncherPairingExchangeResult(true, null));
        public Task<LauncherUserIdentity> GetCurrentUserAsync(CancellationToken cancellationToken = default)
        {
            CurrentUserCalls++;
            return Task.FromResult(CurrentIdentity);
        }
        public Task<LauncherAiConfiguration> GetAiConfigurationAsync(CancellationToken cancellationToken = default)
        {
            AiReadCalls++;
            return Task.FromResult(CurrentConfiguration);
        }
        public Task<LauncherAiHealthSnapshot> GetAiHealthAsync(CancellationToken cancellationToken = default)
        {
            HealthReadCalls++;
            return Task.FromResult(HealthSnapshot with
            {
                ConfigId = CurrentConfiguration.ConfigId,
                ConfigRevision = CurrentConfiguration.ConfigRevision,
            });
        }
        public Task<LauncherAiConfiguration> SaveAiConfigurationAsync(
            int configRevision, string configId, string? baseUrl, string? modelName, string? replacementApiKey,
            bool removeApiKey = false, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            SavedBaseUrl = baseUrl;
            SavedModel = modelName;
            SavedReplacementKey = replacementApiKey;
            SavedRemoveKey = removeApiKey;
            if (ThrowConflictOnNextSave)
            {
                ThrowConflictOnNextSave = false;
                CurrentConfiguration = CurrentConfiguration with { ModelName = "server-model", ConfigRevision = configRevision + 1 };
                throw new LauncherUserClientException(HttpStatusCode.Conflict, "conflict");
            }
            if (ThrowBadRequestOnNextSave)
            {
                ThrowBadRequestOnNextSave = false;
                throw new LauncherUserClientException(HttpStatusCode.BadRequest, "raw server detail must not be displayed");
            }

            CurrentConfiguration = CurrentConfiguration with
            {
                BaseUrl = baseUrl ?? CurrentConfiguration.BaseUrl,
                ModelName = modelName ?? CurrentConfiguration.ModelName,
                ApiKeySet = removeApiKey ? false : replacementApiKey is not null || CurrentConfiguration.ApiKeySet,
                ConfigRevision = configRevision + 1,
            };
            return Task.FromResult(CurrentConfiguration);
        }
        public async Task<LauncherAiManualTestResult> RunManualAiTestAsync(
            bool explicitlyConfirmed, string requestId, string expectedConfigId, int expectedConfigRevision,
            string? baseUrl, string? modelName, string? apiKey,
            CancellationToken cancellationToken = default)
        {
            ManualTestCalls++;
            LastManualTestConfirmed = explicitlyConfirmed;
            LastManualTestRequestId = requestId;
            LastManualTestExpectedConfigId = expectedConfigId;
            LastManualTestExpectedConfigRevision = expectedConfigRevision;
            LastManualTestBaseUrl = baseUrl;
            LastManualTestModel = modelName;
            LastManualTestApiKey = apiKey;
            if (ThrowConflictOnNextManualTest)
            {
                ThrowConflictOnNextManualTest = false;
                CurrentConfiguration = CurrentConfiguration with
                {
                    ConfigId = "config-after-conflict",
                    ConfigRevision = CurrentConfiguration.ConfigRevision + 1,
                };
                throw new LauncherUserClientException(HttpStatusCode.Conflict, "conflict");
            }
            if (BlockManualTestUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new LauncherAiManualTestResult(true, "healthy", "测试成功", 12, DateTimeOffset.UtcNow);
        }
        public Task LogoutAsync(CancellationToken cancellationToken = default) { LogoutCalls++; return Task.CompletedTask; }
        public void ClearUserDataCache() { }
        public void RaiseInvalidated() => UserDataInvalidated?.Invoke();
        public Action CaptureInvalidationHandler() => UserDataInvalidated ?? throw new InvalidOperationException("No invalidation handler attached.");
        public void Dispose() => DisposeCalls++;
    }
}
