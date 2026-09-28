using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AiGoofish.Launcher.Core;
using AiGoofish.Launcher.Platform.Windows;

try
{
if (args.Length == 1 && args[0] == "--progress-metadata-self-test")
{
    await TestPostgresInitializationProgressMetadata();
    return 0;
}

if (args.Length > 0 && args[0] == "--restore-child")
{
    return await RunRestoreChildAsync(args.Skip(1).ToArray());
}

if (args.Length > 0 && args[0] == "--child")
{
    return await RunChildAsync(args.Skip(1).ToArray());
}

string? requestedTestName = null;
if (args.Length == 2 && string.Equals(args[0], "--test", StringComparison.Ordinal))
{
    requestedTestName = args[1];
}
else if (args.Length != 0)
{
    Console.Error.WriteLine("用法：AiGoofish.Launcher.Platform.Windows.Tests [--test <完整测试名称>]");
    return 2;
}

var testBaseRoot = Environment.GetEnvironmentVariable("AIGOOFISH_PROCESS_TEST_ROOT");
if (string.IsNullOrWhiteSpace(testBaseRoot) || !Path.IsPathFullyQualified(testBaseRoot))
{
    Console.Error.WriteLine("AIGOOFISH_PROCESS_TEST_ROOT 必须是绝对路径。");
    return 2;
}

var workspace = new ProcessTestWorkspace(testBaseRoot);
var tests = new (string Name, Func<ProcessTestWorkspace, Task> Run)[]
{
    ("首次设置浏览器短时票据与地址边界", SetupBrowserCases.RunAsync),
    ("实例分配持久状态与初始化证据拒绝", InitializationStateCases.RunAsync),
    ("真实子进程启动与正常停止", TestStartAndStopAsync),
    ("启动确认期内早退", TestEarlyExitAsync),
    ("停止超时不误报并可重试", TestStopTimeoutAsync),
    ("挂起停止协议受总时限约束", TestHangingStopProtocolAsync),
    ("数据根排他锁与持久实例ID", TestInstanceLeaseAsync),
    ("启动取消仍保留归属", TestCancelledStartAsync),
    ("日志条数长度与敏感值有界", TestBoundedRedactedLogsAsync),
    ("启动等待中异步释放不终止子进程", TestDisposeDuringStartAsync),
    ("释放管理器后按身份重连", TestReconnectAsync),
    ("DPAPI 实例基础凭据往返与无明文", TestInstanceSecretsRoundTrip),
    ("便携 Web 端口配置隔离、CAS 与 pending", TestPortableWebPortSettings),
    ("Web 端口维护事务核停、排他与取消释放", TestWebPortReplacementGateOrdering),
    ("Web 端口恢复提交失败时停候选并保留 intent 与 lease", TestWebPortRecoveryCommitFailure),
    ("未知候选恢复保留 intent、释放分类 lease 并持续拒绝入口", TestWebPortUnknownRecoveryIsFailClosed),
    ("全停移动数据根后仍可加载", TestInstanceSecretsAfterMove),
    ("密文篡改与超限文件拒绝", TestTamperedInstanceSecrets),
    ("错误实例与已有文件拒绝覆盖", TestWrongInstanceAndExistingSecrets),
    ("非空或越界 PGDATA 拒绝补造", TestPostgresDataGuard),
    ("首次 PostgreSQL 初始化拒绝超长实例路径", TestNewInstancePathLengthGuard),
    ("原子提交失败清理唯一暂存", TestAtomicCommitCleanup),
    ("恢复交接导入目标用户DPAPI并清理", TestRestoredHandoffImport),
    ("恢复交接拒绝错误身份和既有DPAPI", TestRestoredHandoffGuards),
    ("组合包Windows路径与全量manifest", BundleManifestCases.TestWindowsPathsAndCompleteManifestAsync),
    ("预检区分缺失拒绝访问损坏并保留安全诊断", BundleManifestCases.TestPreflightFailureDiagnosticsAsync),
    ("组合包拒绝额外可执行文件", BundleManifestCases.TestUnauthorizedExecutableRejectedAsync),
    ("组合包manifest拒绝data条目", BundleManifestCases.TestDataEntryRejectedAsync),
    ("Host先持锁再创建缓存日志", BundleManifestCases.TestLeasePrecedesInstanceWritesAsync),
    ("业务备份仅接受数据根外的新目标", TestBackupDestinationGuard),
    ("实例目录尾部分隔符不误判越界", TestCatalogTrailingSeparator),
    ("恢复目标隔离且活动指针显式原子切换", TestRestoreCatalogAndConfirmationAsync),
    ("活动指针并发变化拒绝旧恢复目标", TestRestorePointerCompareAndSwapAsync),
    ("恢复失败保留目标且不改变活动指针", TestRestoreFailureRetainsTargetAsync),
    ("恢复 runner 私有 stdio 四次 proof 与凭据隔离", TestRestoreRunnerProtocolAsync),
    ("恢复 runner 拒绝无效 proof", TestRestoreRunnerRejectsBadProofAsync),
    ("旧实例未确认停止时拒绝恢复切换", TestRestoreActivationRequiresSourceStoppedAsync),
    ("Launcher 用户配对和业务请求上下文", PortableLauncherUserClientCases.TestPairingAndUserRequestsAsync),
    ("Launcher 会话过期与 Host generation 清缓存", PortableLauncherUserClientCases.TestSessionExpiryAndContextInvalidationAsync),
    ("Launcher 配对拒绝跨实例会话", PortableLauncherUserClientCases.TestInstanceMismatchAsync),
    ("Launcher 拒绝过期短期授权", PortableLauncherUserClientCases.TestExpiredPairingTokenAsync),
    ("Launcher 用户上下文校验 ready、实例和运行身份", PortableLauncherUserClientCases.TestHostContextEligibilityGuardsAsync),
    ("Launcher 主动退出撤销短期会话", PortableLauncherUserClientCases.TestLogoutRevokesSessionAsync),
    ("Launcher 超限私有响应拒绝并清缓存", PortableLauncherUserClientCases.TestOversizedPrivateResponseClearsCacheAsync),
    ("旧 Launcher expiry timer 不影响新配对和会话", PortableLauncherUserClientCases.TestObsoleteExpiryCallbackCannotClearNewSessionAsync),
    ("退出后旧用户 GET 200 不得回填新会话缓存", PortableLauncherUserClientCases.TestLateUserSuccessAfterLogoutCannotPopulateCacheAsync),
    ("新登录后旧用户 GET 401 不得清除当前缓存", PortableLauncherUserClientCases.TestLateUserUnauthorizedAfterNewLoginCannotClearCacheAsync),
    ("Launcher AI 手动测试必须确认且结果安全脱敏", PortableLauncherUserClientCases.TestManualAiTestConfirmationAllowlistAndSafeResultAsync),
    ("Launcher AI 手动测试取消返回 unknown 且不重试", PortableLauncherUserClientCases.TestManualAiTestCancellationIsUnknownAndNotRetriedAsync),
    ("Launcher 会话切换后丢弃迟到 AI 测试结果", PortableLauncherUserClientCases.TestLateManualAiTestResultAfterUserSwitchIsDiscardedAsync),
    ("Launcher 健康缓存分离 freshness 与 category", PortableLauncherUserClientCases.TestAiHealthFreshnessAndCategoryStaySeparateAsync),
    ("Launcher 健康缓存未知/未配置不伪造零值", PortableLauncherUserClientCases.TestAiHealthUnknownAndUnconfiguredAreNotZeroAsync),
    ("Launcher 健康缓存 503 不清理用户会话", PortableLauncherUserClientCases.TestAiHealthUnavailableDoesNotExpireSessionAsync),
    ("Launcher 切换用户后丢弃迟到健康快照", PortableLauncherUserClientCases.TestLateAiHealthResponseAfterUserSwitchIsDiscardedAsync),
    ("手动 AI 测试后端 500 不清理会话", PortableLauncherUserClientCases.TestManualAiTestServerErrorDoesNotExpireSessionAsync),
    ("手动 AI 测试仅展示固定脱敏分类", PortableLauncherUserClientCases.TestManualAiTestFixedMessagesAreSafelyClassifiedAsync),
    ("手动 AI 测试 CAS 冲突不泄密、不清会话、不重试", PortableLauncherUserClientCases.TestManualAiTestConfigConflictDoesNotClearSessionOrRetryAsync),
    ("手动 AI 测试支持空配置身份 revision 0", PortableLauncherUserClientCases.TestManualAiTestSupportsNoSavedConfigIdentityAsync),
    ("手动 AI 测试拒绝空配置 ID 搭配正 revision", PortableLauncherUserClientCases.TestManualAiTestRejectsEmptyConfigIdWithPositiveRevisionAsync),
    ("Launcher AI 密钥脱敏与配置 CAS 冲突", PortableLauncherUserClientCases.TestMaskedKeyAndCasConflictAsync),
    ("Launcher AI 首次配置创建并刷新权威值", PortableLauncherUserClientCases.TestAiConfigurationFirstSaveCreatesAndRefreshesAuthoritativeConfigAsync),
    ("Launcher AI 保存拒绝空 ID 搭配正 revision", PortableLauncherUserClientCases.TestAiConfigurationRejectsEmptyIdWithPositiveRevisionAsync),
    ("Launcher 当前账号 tokens 高级参数协议", PortableLauncherUserClientCases.TestAdvancedTokensAsync),
    ("Launcher AI 保存拒绝已有配置身份不一致响应", PortableLauncherUserClientCases.TestAiConfigurationRejectsExistingIdentityMismatchAsync),
};

var failures = new List<string>();
var selectedTests = requestedTestName is null
    ? tests
    : tests.Where(test => string.Equals(test.Name, requestedTestName, StringComparison.Ordinal)).ToArray();
if (selectedTests.Length == 0)
{
    Console.Error.WriteLine($"未找到精确测试名称：{requestedTestName}");
    return 2;
}
if (requestedTestName is not null)
{
    Console.WriteLine($"TEST_FILTER={requestedTestName}");
}
try
{
    foreach (var test in selectedTests)
    {
        try
        {
            await test.Run(workspace);
            Console.WriteLine($"PASS {test.Name}");
        }
        catch (Exception exception)
        {
            failures.Add(test.Name);
            Console.Error.WriteLine($"Windows 验收测试捕获托管异常：{exception.GetType().Name}");
        }
    }
}
finally
{
    await workspace.RequestTrackedChildrenExitAsync();
    workspace.Dispose();
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"共 {failures.Count} 项 Windows 进程验收失败。");
    return 1;
}

Console.WriteLine($"全部 {selectedTests.Length} 项 Windows 进程验收通过。");
return 0;
}
catch (Exception exception)
{
    try
    {
        Console.Error.WriteLine($"Windows 验收入口捕获托管异常：{exception.GetType().Name}");
    }
    catch
    {
        // stderr 本身不可用时仍返回失败码。
    }

    return 1;
}

static Task TestBackupDestinationGuard(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("backup-destination");
    var data = Path.Combine(root, "data");
    Directory.CreateDirectory(data);
    var outside = Path.Combine(root, "new-encrypted.backup");
    PortableBusinessBackupRunner.ValidateDestination(data, outside);
    AssertThrows<InvalidOperationException>(() =>
        PortableBusinessBackupRunner.ValidateDestination(data, Path.Combine(data, "inside.backup")));
    File.WriteAllText(outside, "existing", new UTF8Encoding(false));
    AssertThrows<InvalidOperationException>(() =>
        PortableBusinessBackupRunner.ValidateDestination(data, outside));
    AssertThrows<InvalidOperationException>(() =>
        PortableBusinessBackupRunner.ValidateDestination(data, "relative.backup"));
    return Task.CompletedTask;
}

static Task TestCatalogTrailingSeparator(ProcessTestWorkspace workspace)
{
    var bundleRoot = Path.Combine(workspace.CreateCaseRoot("catalog-trailing-separator"), "bundle");
    Directory.CreateDirectory(bundleRoot);
    var trailingBundleRoot = bundleRoot + Path.DirectorySeparatorChar;
    var catalog = new PortableInstanceCatalog(trailingBundleRoot);
    var active = catalog.ResolveActiveInstance();
    Assert(active.InstanceId is null && active.DataRoot == Path.Combine(bundleRoot, "data"),
        "Windows 可执行文件目录尾部分隔符不得使首次安装被误判为越界");
    PortableInstanceCatalog.EnsureSafePath(trailingBundleRoot, active.DataRoot, allowMissingLeaf: true);
    AssertThrows<PortableInstanceCatalogException>(() => PortableInstanceCatalog.EnsureSafePath(
        trailingBundleRoot, bundleRoot + "-other" + Path.DirectorySeparatorChar + "data", allowMissingLeaf: true));
    return Task.CompletedTask;
}

static Task TestPortableWebPortSettings(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("web-port-settings");
    Directory.CreateDirectory(root);
    using var lease = InstanceDataRootLease.Acquire(root);
    var store = new PortableWebPortSettingsStore();

    var legacy = store.Read(lease, PortableInstanceCatalog.DefaultWebPort, PortableInstanceCatalog.DefaultPostgresPort);
    Assert(legacy.Revision == 0 && legacy.EffectivePort == PortableInstanceCatalog.DefaultWebPort &&
        legacy.PendingPort is null && legacy.Applying is null, "缺配置的旧实例必须无损回退到 active/default 且不生成文件");
    Assert(!File.Exists(Path.Combine(root, PortableWebPortSettingsStore.RelativePath.Replace('/', Path.DirectorySeparatorChar))),
        "只读兼容读取不得回写旧实例");

    var staged = store.Stage(lease, legacy.Revision, 58123, legacy.EffectivePort,
        PortableInstanceCatalog.DefaultPostgresPort);
    Assert(staged.Revision == 1 && staged.EffectivePort == PortableInstanceCatalog.DefaultWebPort &&
        staged.PendingPort == 58123, "stage 只能写 pending，effective 必须不变");
    AssertThrows<PortableWebPortSettingsException>(() => store.Stage(
        lease, legacy.Revision, 58124, legacy.EffectivePort, PortableInstanceCatalog.DefaultPostgresPort));
    AssertThrows<PortableWebPortSettingsException>(() => store.Stage(
        lease, staged.Revision, PortableInstanceCatalog.DefaultPostgresPort,
        staged.EffectivePort, PortableInstanceCatalog.DefaultPostgresPort));

    var jsonPath = Path.Combine(root, PortableWebPortSettingsStore.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    var bytes = File.ReadAllBytes(jsonPath);
    Assert(bytes.Length > 0 && !(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF),
        "Web 端口配置必须为 UTF-8 无 BOM");
    var json = new UTF8Encoding(false, true).GetString(bytes);
    Assert(json.Contains("\"instance_id\"", StringComparison.Ordinal) &&
        json.Contains("\"effective_port\"", StringComparison.Ordinal) &&
        json.Contains("\"pending_port\"", StringComparison.Ordinal), "持久字段必须采用稳定 snake_case 契约");
    Assert(store.Read(lease, 58000, 55432) == staged, "写入后状态应可稳定读取");

    var intentState = store.BeginApply(lease, staged.Revision, staged.EffectivePort,
        PortableInstanceCatalog.DefaultPostgresPort);
    Assert(intentState.EffectivePort == staged.EffectivePort && intentState.PendingPort == 58123 &&
        intentState.Applying is { CandidatePort: 58123, FromPort: 58000, ConfirmedRevision: 1 },
        "begin apply 必须只写 intent，不得提前提交 effective");
    var committed = store.CommitApply(lease, intentState.Applying!.AttemptId, 58000,
        PortableInstanceCatalog.DefaultPostgresPort);
    Assert(committed.EffectivePort == 58123 && committed.PendingPort is null && committed.Applying is null,
        "候选 Ready 后的提交必须原子更新 effective 并清除 pending/intent");

    var retry = store.Stage(lease, committed.Revision, 58124, committed.EffectivePort,
        PortableInstanceCatalog.DefaultPostgresPort);
    var retryIntent = store.BeginApply(lease, retry.Revision, retry.EffectivePort,
        PortableInstanceCatalog.DefaultPostgresPort);
    var rolledBack = store.CompleteRollback(lease, retryIntent.Applying!.AttemptId, 58123,
        PortableInstanceCatalog.DefaultPostgresPort);
    Assert(rolledBack.EffectivePort == 58123 && rolledBack.PendingPort == 58124 && rolledBack.Applying is null,
        "旧 Host Ready 后回滚只能清 intent，保留旧 effective 与待处理候选");

    var lockPath = Path.Combine(root, PortableWebPortSettingsStore.LockRelativePath.Replace('/', Path.DirectorySeparatorChar));
    using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    {
        AssertThrows<PortableWebPortSettingsException>(() => store.Stage(
            lease, rolledBack.Revision, 58125, rolledBack.EffectivePort, PortableInstanceCatalog.DefaultPostgresPort));
    }

    var otherRoot = workspace.CreateCaseRoot("web-port-settings-other-instance");
    Directory.CreateDirectory(otherRoot);
    using var otherLease = InstanceDataRootLease.Acquire(otherRoot);
    var otherInstance = store.Read(otherLease, 58001, PortableInstanceCatalog.DefaultPostgresPort);
    Assert(otherInstance.InstanceId != lease.InstanceId && otherInstance.EffectivePort == 58001 &&
        otherInstance.PendingPort is null, "不同实例不得继承本实例 effective 或 pending 端口");
    Assert(!otherInstance.Automatic, "无设置的既有实例必须保留固定端口兼容行为");
    var automatic = store.Stage(otherLease, 0, 58001, 58001, 55432, automatic: true);
    Assert(automatic.Automatic && automatic.PendingPort is null &&
        store.Read(otherLease, 58001, 55432) == automatic, "自动模式必须跨读取持久化且不提前改变有效端口");
    AssertThrows<PortableWebPortSettingsException>(() => store.Stage(otherLease, 0, 58001, 58001, 55432));
    var fixedAgain = store.Stage(otherLease, automatic.Revision, 58001, 58001, 55432);
    Assert(!fixedAgain.Automatic && fixedAgain.EffectivePort == 58001, "显式固定模式应停止自动换端口");
    var otherPath = Path.Combine(otherRoot, PortableWebPortSettingsStore.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    File.WriteAllText(otherPath, File.ReadAllText(otherPath).Replace(",\"automatic\":false", "", StringComparison.Ordinal), new UTF8Encoding(false));
    Assert(!store.Read(otherLease, 58001, 55432).Automatic, "旧 JSON 缺 mode 字段不得默默启用自动换端口");
    var available = RealPortableStackHost.SelectAvailableWebPort(58001, 55432);
    Assert(available is >= 1024 and <= 65535 && available != 58001 && available != 55432,
        "自动候选必须避开旧端口与 PostgreSQL 端口");
    return Task.CompletedTask;
}

static async Task TestWebPortReplacementGateOrdering(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("web-port-maintenance");
    using var lease = InstanceDataRootLease.Acquire(root);
    var store = new PortableWebPortSettingsStore();
    var staged = store.Stage(lease, 0, 58123, 58000, 55432);
    var settingsPath = Path.Combine(root, PortableWebPortSettingsStore.RelativePath);
    var before = File.ReadAllBytes(settingsPath);
    var component = new FakeStateComponent("python-web", ComponentRuntimeState.Running);
    using var coordinator = new LauncherCoordinator([new LauncherComponentRegistration(component)], false);
    await AssertThrowsAsync<InvalidOperationException>(() => coordinator.RunMaintenanceAsync(_ =>
        Task.FromResult(store.BeginApply(lease, staged.Revision, 58000, 55432))));
    Assert(File.ReadAllBytes(settingsPath).SequenceEqual(before), "未停止时不得提交 intent");
    component.State = ComponentRuntimeState.Stopped;
    await AssertThrowsAsync<OperationCanceledException>(() => coordinator.RunMaintenanceAsync<bool>(_ =>
        throw new OperationCanceledException()));
    Assert(File.ReadAllBytes(settingsPath).SequenceEqual(before), "维护取消不得写入 intent");
    await coordinator.RunMaintenanceAsync(async _ =>
    {
        await AssertThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync());
        var applying = store.BeginApply(lease, staged.Revision, 58000, 55432);
        Assert(applying.Applying is not null, "核停并独占后允许提交 intent");
        return true;
    });
}

static async Task TestWebPortRecoveryCommitFailure(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("web-port-recovery-commit-failure");
    Directory.CreateDirectory(root);
    using var lease = InstanceDataRootLease.Acquire(root);
    var store = new PortableWebPortSettingsStore();
    var legacy = store.Read(lease, PortableInstanceCatalog.DefaultWebPort, PortableInstanceCatalog.DefaultPostgresPort);
    var staged = store.Stage(lease, legacy.Revision, 58123, legacy.EffectivePort,
        PortableInstanceCatalog.DefaultPostgresPort);
    var applying = store.BeginApply(lease, staged.Revision, legacy.EffectivePort,
        PortableInstanceCatalog.DefaultPostgresPort);
    var intent = applying.Applying ?? throw new InvalidOperationException("测试未能建立 applying intent。");
    var unresolved = false;
    var stopAttempted = false;
    var snapshot = new LauncherSnapshot(
        Guid.NewGuid(), LauncherState.StopFailed, "test:stop-failed", "fixture", false, false,
        DateTimeOffset.UtcNow,
        [new ComponentSnapshot("python-web", "Python Web", ComponentRuntimeState.Unknown)]);

    var result = await PortableWebPortRecoveryCommitGuard.CommitOrStopCandidateAsync(
        () => throw new PortableInstanceCatalogException("injected atomic commit failure"),
        () =>
        {
            stopAttempted = true;
            Assert(unresolved, "必须在尝试停候选之前标记事务 unresolved");
            throw new IOException("injected inability to verify candidate stop");
        },
        () => unresolved = true,
        () => snapshot);

    Assert(result is { Succeeded: false, ErrorCode: "WEB_PORT_COMMIT_AND_STOP_FAILED" } &&
        result.Snapshot == snapshot, "catalog commit失败/候选停机不确定必须返回 fail-closed 状态");
    Assert(stopAttempted && unresolved, "提交失败后必须请求正常停机并保留 unresolved 标记");
    var persisted = store.Read(lease, legacy.EffectivePort, PortableInstanceCatalog.DefaultPostgresPort);
    Assert(persisted.EffectivePort == legacy.EffectivePort && persisted.PendingPort == 58123 &&
        persisted.Applying?.AttemptId == intent.AttemptId, "提交失败和候选停机失败不得清理事务 intent 或切 effective");
    AssertThrows<InstanceAlreadyLockedException>(() => InstanceDataRootLease.Acquire(root));
}

static Task TestWebPortUnknownRecoveryIsFailClosed(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("web-port-unknown-recovery");
    var bundleRoot = Path.Combine(root, "bundle");
    var dataRoot = Path.Combine(bundleRoot, "data");
    var configRoot = Path.Combine(dataRoot, "config");
    Directory.CreateDirectory(bundleRoot);
    Directory.CreateDirectory(configRoot);
    var runtimePath = Path.Combine(configRoot, "python-runtime.json");
    File.WriteAllText(runtimePath, "{}", new UTF8Encoding(false));
    var bundle = new PortableBundleDescriptor(
        bundleRoot, "fixture-release", "fixture-app", 1, 1,
        root, @"C:\fixture\python.exe", root, root, @"C:\fixture\Launcher.exe");
    var store = new PortableWebPortSettingsStore();
    PortableWebPortSettings applying;
    using (var lease = InstanceDataRootLease.Acquire(dataRoot))
    {
        var legacy = store.Read(lease, PortableInstanceCatalog.DefaultWebPort, PortableInstanceCatalog.DefaultPostgresPort);
        var staged = store.Stage(lease, legacy.Revision, 58123, legacy.EffectivePort,
            PortableInstanceCatalog.DefaultPostgresPort);
        applying = store.BeginApply(lease, staged.Revision, legacy.EffectivePort,
            PortableInstanceCatalog.DefaultPostgresPort);
    }

    var intent = applying.Applying ?? throw new InvalidOperationException("测试未能建立未决端口 intent。");
    var identity = new OwnedProcessIdentity(
        1, DateTimeOffset.UtcNow, @"C:\fixture\python.exe", dataRoot, applying.InstanceId, Guid.NewGuid());
    var runtime = new PythonRuntimeRecord(
        2, applying.InstanceId, "normal", "fixture-app", intent.CandidatePort,
        root, dataRoot, "Running", identity, Guid.NewGuid(), true, DateTimeOffset.UtcNow);
    var runtimeReads = 0;
    var processProbes = 0;
    var settingsPath = Path.Combine(dataRoot, PortableWebPortSettingsStore.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    var settingsBefore = File.ReadAllBytes(settingsPath);

    // The recovery classifier accepts only durable runtime reading and a local
    // process identity probe; it has no HTTP or process-stop dependency.
    using (var lease = InstanceDataRootLease.Acquire(dataRoot))
    {
        try
        {
            RealPortableStackHost.ResolveWebPortApplyRecoveryAndReleaseLeaseOnFailure(
                lease, bundle, store, applying, PortableInstanceCatalog.DefaultPostgresPort,
                (_, _) => { runtimeReads++; return runtime; },
                _ => { processProbes++; return PythonIdentityRunState.Unknown; });
            throw new InvalidOperationException("身份状态未知时应拒绝恢复。");
        }
        catch (PythonLifecycleException exception)
        {
            Assert(exception.Message.Contains("本次分类 lease 已释放", StringComparison.Ordinal) &&
                exception.Message.Contains("intent 已保留", StringComparison.Ordinal),
                "错误说明必须准确描述 intent 保留和分类 lease 释放");
        }

        Assert(!lease.IsHeld, "Unknown分类失败应在统一边界释放本次临时lease");
    }

    Assert(runtimeReads == 1 && processProbes == 1,
        "Unknown分类只能读取持久runtime并执行本机身份探测");

    PortableWebPortSettings persisted;
    using (var retryLease = InstanceDataRootLease.Acquire(dataRoot))
    {
        persisted = store.Read(retryLease, PortableInstanceCatalog.DefaultWebPort,
            PortableInstanceCatalog.DefaultPostgresPort);
        Assert(persisted.Applying?.AttemptId == intent.AttemptId &&
            persisted.EffectivePort == PortableInstanceCatalog.DefaultWebPort && persisted.PendingPort == 58123,
            "分类失败必须保留原 effective/pending/applying intent");
        AssertThrows<PortableWebPortSettingsException>(() => store.Stage(
            retryLease, persisted.Revision, 58124, persisted.EffectivePort, PortableInstanceCatalog.DefaultPostgresPort));
    }
    AssertThrows<PythonLifecycleException>(() => RealPortableStackHost.Create(bundle));

    PortableWebPortSettings persistedAgain;
    using (var secondLauncherLease = InstanceDataRootLease.Acquire(dataRoot))
    {
        persistedAgain = store.Read(secondLauncherLease, PortableInstanceCatalog.DefaultWebPort,
            PortableInstanceCatalog.DefaultPostgresPort);
        Assert(persistedAgain.Applying?.AttemptId == intent.AttemptId,
            "后续Host创建入口必须重复拒绝并保持同一intent");
    }
    AssertThrows<PythonLifecycleException>(() => RealPortableStackHost.Create(bundle));

    Assert(settingsBefore.SequenceEqual(File.ReadAllBytes(settingsPath)),
        "Unknown恢复分类不得改写任何端口配置字节");
    return Task.CompletedTask;
}

static async Task TestRestoreCatalogAndConfirmationAsync(ProcessTestWorkspace workspace)
{
    var bundle = Path.Combine(workspace.CreateCaseRoot("restore-catalog"), "bundle");
    Directory.CreateDirectory(bundle);
    var dataRoot = Path.Combine(bundle, "data");
    var oldMarker = Path.Combine(dataRoot, "state", "keep.json");
    Directory.CreateDirectory(Path.GetDirectoryName(oldMarker)!);
    File.WriteAllText(oldMarker, "old-business-data", new UTF8Encoding(false));
    var catalog = new PortableInstanceCatalog(bundle);
    var initial = catalog.ResolveActiveInstance();
    Assert(initial.DataRoot == dataRoot && initial.InstanceId is null, "无指针时必须保留旧 data 根为活动实例");

    await using var session = new PortableRestoreSession(catalog, new FakeRestoreExecutor());
    var preview = await session.RestoreAndPreviewAsync(
        Path.Combine(bundle, "trusted.gfbk"), "isolated-fixture-passphrase", PortableRestoreSession.RequiredBackupTrustText);
    Assert(preview.State == PortableRestoreState.AwaitingUserConfirmation, "成功维护核对后必须等待用户确认");
    Assert(File.Exists(oldMarker) && File.ReadAllText(oldMarker) == "old-business-data", "恢复准备不得覆盖旧实例业务文件");
    Assert(!File.Exists(catalog.ActivePointerPath), "预览阶段不得创建活动指针");
    Assert(Directory.Exists(preview.DiagnosticTargetRoot), "恢复候选目录必须实际存在并保留");
    AssertThrows<PortableRestoreException>(() => session.ConfirmActivationAsync("确认").GetAwaiter().GetResult());
    Assert(!File.Exists(catalog.ActivePointerPath), "错误确认文字不得切换活动实例");

    var activated = await session.ConfirmActivationAsync(PortableRestoreSession.RequiredConfirmationText);
    Assert(activated.State == PortableRestoreState.Activated, "只有明确确认后才应进入 Activated");
    var selected = catalog.ResolveActiveInstance();
    Assert(selected.InstanceId == preview.Preview!.TargetInstanceId, "活动指针应指向已核对的新目标");
    Assert(Path.GetFullPath(selected.DataRoot) == Path.GetFullPath(preview.DiagnosticTargetRoot!), "活动指针目标目录必须稳定且明确");
    Assert(File.Exists(oldMarker) && File.ReadAllText(oldMarker) == "old-business-data", "切换后仍须保留旧实例数据");
}

static async Task TestRestorePointerCompareAndSwapAsync(ProcessTestWorkspace workspace)
{
    var bundle = Path.Combine(workspace.CreateCaseRoot("restore-cas"), "bundle");
    Directory.CreateDirectory(bundle);
    var catalog = new PortableInstanceCatalog(bundle);
    using var first = catalog.CreateRestoreTarget();
    using var second = catalog.CreateRestoreTarget();
    catalog.MarkRestoreTargetValidated(first);
    catalog.MarkRestoreTargetValidated(second);
    catalog.CommitActiveTarget(first);
    var firstId = first.InstanceId;
    first.Dispose();
    AssertThrows<PortableInstanceCatalogException>(() => catalog.CommitActiveTarget(second));
    second.Dispose();
    Assert(catalog.ResolveActiveInstance().InstanceId == firstId, "旧候选不得覆盖并发提交的新活动指针");
    await Task.CompletedTask;
}

static async Task TestRestoreFailureRetainsTargetAsync(ProcessTestWorkspace workspace)
{
    var bundle = Path.Combine(workspace.CreateCaseRoot("restore-failure"), "bundle");
    Directory.CreateDirectory(bundle);
    var catalog = new PortableInstanceCatalog(bundle);
    var executor = new FakeRestoreExecutor(throwDuringRestore: true);
    await using var session = new PortableRestoreSession(catalog, executor);
    try
    {
        await session.RestoreAndPreviewAsync(
            Path.Combine(bundle, "trusted.gfbk"), "isolated-fixture-passphrase", PortableRestoreSession.RequiredBackupTrustText);
        throw new InvalidOperationException("预期隔离恢复失败");
    }
    catch (PortableRestoreException exception) when (exception.Code == "RESTORE_FAILED")
    {
    }

    var snapshot = session.Snapshot;
    Assert(snapshot.State == PortableRestoreState.FailedTargetRetained, "失败必须保留可诊断状态");
    Assert(snapshot.DiagnosticTargetRoot is not null && Directory.Exists(snapshot.DiagnosticTargetRoot), "失败候选不得自动删除");
    Assert(!File.Exists(catalog.ActivePointerPath), "失败恢复不得修改活动指针");
    Assert(catalog.ResolveActiveInstance().InstanceId is null, "旧 data 根仍应为活动实例");
    var cancelled = await session.CancelAndRetainTargetAsync();
    Assert(cancelled.State == PortableRestoreState.CancelledTargetRetained, "失败且已释放锁后仍应能取消会话");
    await session.DisposeAsync();
}

static async Task TestRestoreRunnerProtocolAsync(ProcessTestWorkspace workspace)
{
    foreach (var fileCount in new[] { 0, 1, 2, 3 })
    {
        await TestRestoreRunnerFileCountAsync(workspace, fileCount);
    }
}

static async Task TestRestoreRunnerFileCountAsync(ProcessTestWorkspace workspace, int fileCount)
{
    var root = workspace.CreateCaseRoot($"restore-runner-{fileCount}");
    var bundleRoot = Path.Combine(root, "bundle");
    Directory.CreateDirectory(bundleRoot);
    var archive = Path.Combine(root, "source.gfbk");
    File.WriteAllBytes(archive, [1, 2, 3, 4]);
    var target = new PortableInstanceCatalog(bundleRoot).CreateRestoreTarget();
    try
    {
        var bundle = new PortableBundleDescriptor(
            bundleRoot, "fixture-release", "fixture-app", 1, 1,
            bundleRoot, workspace.ChildExecutable, bundleRoot, bundleRoot, workspace.ChildExecutable);
        var checks = 0;
        int? childExitCode = null;
        var passphrase = "fixture-only private passphrase";
        var adminPassword = "fixture-only admin password";
        PortableBusinessRestoreResult result;
        try
        {
            result = await PortableBusinessRestoreRunner.RunAsync(
                bundle, target, archive, passphrase, adminPassword,
                _ => { checks++; return Task.CompletedTask; },
                CancellationToken.None,
                startInfo =>
                {
                    Assert(!startInfo.ArgumentList.Any(value => value.Contains(passphrase, StringComparison.Ordinal) ||
                        value.Contains(adminPassword, StringComparison.Ordinal)), "口令和管理员凭据不得进入 argv");
                    Assert(!startInfo.Environment.Values.Any(value => value is not null &&
                        (value.Contains(passphrase, StringComparison.Ordinal) || value.Contains(adminPassword, StringComparison.Ordinal))),
                        "口令和管理员凭据不得进入环境变量");
                    var dotnet = GetCurrentDotnetHost();
                    startInfo.FileName = dotnet.HostPath;
                    startInfo.ArgumentList.Clear();
                    startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
                    startInfo.ArgumentList.Add("--restore-child");
                    startInfo.ArgumentList.Add($"files-{fileCount}");
                    startInfo.Environment["DOTNET_ROOT"] = dotnet.Root;
                    var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                    process.Exited += (_, _) =>
                    {
                        try { childExitCode = process.ExitCode; }
                        catch (InvalidOperationException) { }
                    };
                    return process;
                });
        }
        catch (PortableRestoreException exception) when (exception.Code == "RESTORE_HELPER_FAILED")
        {
            throw new InvalidOperationException(
                $"恢复 child 安全诊断：exit={(childExitCode?.ToString() ?? "unknown")}, proofs={checks}。", exception);
        }
        Assert(checks == 4, "四次维护 proof 都必须由 Launcher 回核");
        Assert(result.RestoredFileCount == fileCount, "业务文件计数不得包含数据库、清单和密钥载荷");
        Assert(result.TargetInstanceId == target.InstanceId && result.TableCounts["users"] == 1,
            "runner 必须只接收经过身份绑定的恢复摘要");
        Assert(result.HandoffDirectory.StartsWith(Path.GetDirectoryName(target.DataRoot)!, StringComparison.OrdinalIgnoreCase),
            "handoff 必须位于候选实例根之外的专用父目录");
    }
    finally
    {
        target.Dispose();
    }
}

static async Task TestRestoreRunnerRejectsBadProofAsync(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("restore-runner-bad-proof");
    var bundleRoot = Path.Combine(root, "bundle");
    Directory.CreateDirectory(bundleRoot);
    var archive = Path.Combine(root, "source.gfbk");
    File.WriteAllBytes(archive, [1, 2, 3, 4]);
    var target = new PortableInstanceCatalog(bundleRoot).CreateRestoreTarget();
    try
    {
        var bundle = new PortableBundleDescriptor(
            bundleRoot, "fixture-release", "fixture-app", 1, 1,
            bundleRoot, workspace.ChildExecutable, bundleRoot, bundleRoot, workspace.ChildExecutable);
        await AssertThrowsAsync<PortableRestoreException>(() => PortableBusinessRestoreRunner.RunAsync(
            bundle, target, archive, "fixture-only passphrase", "fixture-only admin",
            _ => Task.CompletedTask,
            CancellationToken.None,
            startInfo =>
            {
                var dotnet = GetCurrentDotnetHost();
                startInfo.FileName = dotnet.HostPath;
                startInfo.ArgumentList.Clear();
                startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
                startInfo.ArgumentList.Add("--restore-child");
                startInfo.ArgumentList.Add("bad-ack");
                startInfo.Environment["DOTNET_ROOT"] = dotnet.Root;
                return new Process { StartInfo = startInfo };
            }));
    }
    finally
    {
        target.Dispose();
    }
}

static async Task TestRestoreActivationRequiresSourceStoppedAsync(ProcessTestWorkspace workspace)
{
    var bundle = Path.Combine(workspace.CreateCaseRoot("restore-source-running"), "bundle");
    Directory.CreateDirectory(bundle);
    var catalog = new PortableInstanceCatalog(bundle);
    var executor = new FakeRestoreExecutor(sourceRunning: true);
    await using var session = new PortableRestoreSession(catalog, executor);
    var preview = await session.RestoreAndPreviewAsync(
        Path.Combine(bundle, "trusted.gfbk"),
        "isolated-fixture-passphrase",
        PortableRestoreSession.RequiredBackupTrustText);
    Assert(preview.State == PortableRestoreState.AwaitingUserConfirmation, "旧实例运行不应阻止隔离准备与预览");
    await AssertThrowsAsync<PortableRestoreException>(() => session.ConfirmActivationAsync(
        PortableRestoreSession.RequiredConfirmationText));
    Assert(!File.Exists(catalog.ActivePointerPath), "旧实例运行时不得提交活动指针");
    Assert(session.Snapshot.State == PortableRestoreState.AwaitingUserConfirmation, "停止旧实例后应能再次显式确认");
    executor.SourceRunning = false;
    var activated = await session.ConfirmActivationAsync(PortableRestoreSession.RequiredConfirmationText);
    Assert(activated.State == PortableRestoreState.Activated, "旧实例确认停止后可切换到已核对目标");
}

static async Task<int> RunRestoreChildAsync(string[] childArguments)
{
    if (childArguments.Length != 1)
    {
        return 64;
    }

    try
    {
        var requestLine = await Console.In.ReadLineAsync();
        if (requestLine is null)
        {
            return 65;
        }

        using var request = JsonDocument.Parse(requestLine);
        var root = request.RootElement;
        if (root.GetProperty("type").GetString() != "restore" ||
            !root.TryGetProperty("passphrase", out _) || !root.TryGetProperty("admin_dsn", out _) ||
            !root.TryGetProperty("session", out var sessionElement))
        {
            return 66;
        }

        var session = sessionElement.GetString();
        if (session is null)
        {
            return 67;
        }

        var mode = childArguments[0];
        for (var counter = 1; counter <= 4; counter++)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { type = "verify", session, counter }));
            Console.Out.Flush();
            var acknowledgementLine = await Console.In.ReadLineAsync();
            if (acknowledgementLine is null)
            {
                return 68;
            }

            using var acknowledgement = JsonDocument.Parse(acknowledgementLine);
            var answer = acknowledgement.RootElement;
            if (answer.GetProperty("type").GetString() != "verified" ||
                answer.GetProperty("session").GetString() != session ||
                answer.GetProperty("counter").GetInt32() != (mode == "bad-ack" && counter == 1 ? 2 : counter))
            {
                return 69;
            }
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            type = "complete",
            session,
            instance_id = root.GetProperty("instance_id").GetString(),
            source_instance_id = Guid.NewGuid().ToString("D"),
            archive_sha256 = new string('a', 64),
            tables = new Dictionary<string, long> { ["users"] = 1, ["tasks"] = 0 },
            files = mode.StartsWith("files-", StringComparison.Ordinal) ? int.Parse(mode[6..]) : 3,
            revoked_sessions = 2,
            handoff_directory = Path.Combine(Path.GetDirectoryName(root.GetProperty("target_data_root").GetString())!, ".restore-handoff-fixture123"),
            status = "awaiting_target_user_dpapi_and_switch_confirmation",
        }));
        Console.Out.Flush();
        return 0;
    }
    catch
    {
        return 70;
    }
}

static (string HostPath, string Root) GetCurrentDotnetHost()
{
    if (!RuntimeInformation.FrameworkDescription.StartsWith(".NET 10.", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("恢复协议子进程必须使用当前 .NET 10 runtime。");
    }

    var runtimeDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
    var root = runtimeDirectory.Parent?.Parent?.Parent
        ?? throw new InvalidOperationException("无法从当前 runtime directory 推导 .NET host 根目录。");
    var hostPath = Path.Combine(root.FullName, "dotnet.exe");
    if (!File.Exists(hostPath))
    {
        throw new FileNotFoundException("当前 .NET 10 runtime 根目录中缺少 dotnet.exe。", hostPath);
    }

    return (hostPath, root.FullName);
}

static async Task<int> RunChildAsync(string[] childArguments)
{
    if (childArguments.Length != 1)
    {
        return 64;
    }

    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    var mode = childArguments[0];
    if (mode == "early-exit")
    {
        Console.WriteLine("child-early-exit");
        return 17;
    }

    var runIdValue = Environment.GetEnvironmentVariable("AIGOOFISH_LAUNCHER_RUN_ID");
    if (!Guid.TryParse(runIdValue, out var runId))
    {
        return 65;
    }

    if (mode == "bounded-log")
    {
        var secret = Environment.GetEnvironmentVariable("LAUNCHER_TEST_SECRET") ?? string.Empty;
        Console.Write(new string('L', 120));
        Console.Out.Flush();
        await Task.Delay(20);
        Console.Write(secret);
        Console.WriteLine(new string('R', 4096));
        for (var index = 0; index < 5; index++)
        {
            Console.WriteLine($"bounded-line-{index}");
        }

        Console.WriteLine($"secret-check={secret}");
        Console.Write(secret);
        Console.Out.Flush();
    }
    else
    {
        Console.WriteLine("child-ready");
    }

    var ignoredStops = 0;
    while (true)
    {
        await using var pipe = new NamedPipeServerStream(
            TestStopProtocol.GetPipeName(runId),
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var command = await reader.ReadLineAsync();
        if (!string.Equals(command, "stop", StringComparison.Ordinal))
        {
            continue;
        }

        if (mode == "ignore-first-stop" && ignoredStops++ == 0)
        {
            continue;
        }

        Console.WriteLine("child-stopped");
        return 0;
    }
}

static async Task TestStartAndStopAsync(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("start-stop");
    using var lease = InstanceDataRootLease.Acquire(root);
    using var manager = workspace.CreateManager(lease);
    var started = await manager.StartAsync(workspace.CreateSpec(root, "normal", TimeSpan.FromMilliseconds(200)));
    workspace.Track(started.Snapshot.Identity);
    Assert(started.Succeeded, "真实子进程应启动成功");
    Assert(started.Snapshot.State == OwnedProcessState.Running, "启动后应为 Running");
    Assert(started.Snapshot.Identity?.RunId != Guid.Empty, "必须记录随机 run_id");
    Assert(started.Snapshot.Identity!.InstanceId == lease.InstanceId, "身份必须记录持久 instance_id");

    var stopped = await manager.StopAsync(TimeSpan.FromSeconds(2));
    Assert(stopped.Succeeded && stopped.Snapshot.State == OwnedProcessState.Exited, "正常协议停止后应确认 Exited");
}

static async Task TestEarlyExitAsync(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("early-exit");
    using var lease = InstanceDataRootLease.Acquire(root);
    using var manager = workspace.CreateManager(lease);
    var result = await manager.StartAsync(workspace.CreateSpec(root, "early-exit", TimeSpan.FromMilliseconds(400)));
    Assert(!result.Succeeded && result.ErrorCode == "PROCESS_EARLY_EXIT", "早退必须返回明确错误");
    Assert(result.Snapshot.State == OwnedProcessState.Exited && result.Snapshot.ExitCode == 17, "早退必须保留退出码");
}

static async Task TestStopTimeoutAsync(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("stop-timeout");
    using var lease = InstanceDataRootLease.Acquire(root);
    using var manager = workspace.CreateManager(lease);
    var started = await manager.StartAsync(workspace.CreateSpec(root, "ignore-first-stop", TimeSpan.FromMilliseconds(200)));
    workspace.Track(started.Snapshot.Identity);
    Assert(started.Succeeded, "超时场景子进程应先启动");

    var timedOut = await manager.StopAsync(TimeSpan.FromMilliseconds(120));
    Assert(!timedOut.Succeeded && timedOut.ErrorCode == "PROCESS_STOP_TIMEOUT", "首次停止必须超时");
    Assert(timedOut.Snapshot.State is OwnedProcessState.StopTimedOut or OwnedProcessState.Unknown, "超时不得假报 Exited");
    Assert(timedOut.Snapshot.Identity == started.Snapshot.Identity, "超时后必须保留归属身份");

    var stopped = await manager.StopAsync(TimeSpan.FromSeconds(2));
    Assert(stopped.Succeeded && stopped.Snapshot.State == OwnedProcessState.Exited, "第二次正常协议应完成停止");
}

static async Task TestHangingStopProtocolAsync(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("hanging-stop-protocol");
    using var lease = InstanceDataRootLease.Acquire(root);
    var protocol = new HangingStopProtocol();
    await using var manager = new WindowsOwnedProcessManager(lease, protocol);
    var started = await manager.StartAsync(workspace.CreateSpec(root, "normal", TimeSpan.FromMilliseconds(200)));
    workspace.Track(started.Snapshot.Identity);
    Assert(started.Succeeded, "挂起停止协议场景应先启动");

    var startedAt = DateTimeOffset.UtcNow;
    var timedOut = await manager.StopAsync(TimeSpan.FromMilliseconds(150));
    Assert(DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(1), "停止协议必须受同一总时限约束");
    Assert(!timedOut.Succeeded && timedOut.ErrorCode == "PROCESS_STOP_TIMEOUT", "挂起协议应返回停止超时");
    Assert(timedOut.Snapshot.Identity == started.Snapshot.Identity, "挂起协议超时后必须保留归属");

    var duplicate = await manager.StopAsync(TimeSpan.FromMilliseconds(150));
    Assert(duplicate.ErrorCode == "PROCESS_STOP_PROTOCOL_PENDING", "未完成协议不得重复发送停止请求");
    Assert(protocol.RequestCount == 1, "停止协议只能发送一次");
}

static Task TestInstanceLeaseAsync(ProcessTestWorkspace workspace)
{
    AssertThrows<ArgumentException>(() => InstanceDataRootLease.Acquire("relative-root"));
    var root = workspace.CreateCaseRoot("instance-lock");
    Guid instanceId;
    using (var first = InstanceDataRootLease.Acquire(root))
    {
        instanceId = first.InstanceId;
        AssertThrows<InstanceAlreadyLockedException>(() => InstanceDataRootLease.Acquire(root));
    }

    Assert(File.Exists(Path.Combine(root, InstanceDataRootLease.LockFileName)), "释放锁后不得删除锁文件");
    using (var reopened = InstanceDataRootLease.Acquire(root))
    {
        Assert(reopened.InstanceId == instanceId, "同一数据根重开必须复用持久 instance_id");
    }

    var lockPath = Path.Combine(root, InstanceDataRootLease.LockFileName);
    File.WriteAllText(lockPath, "损坏的元数据", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    AssertThrows<InstanceMetadataException>(() => InstanceDataRootLease.Acquire(root));
    Assert(File.ReadAllText(lockPath, Encoding.UTF8) == "损坏的元数据", "损坏元数据不得被自动重置");

    var emptyRoot = workspace.CreateCaseRoot("empty-instance-lock");
    var emptyLockPath = Path.Combine(emptyRoot, InstanceDataRootLease.LockFileName);
    File.WriteAllBytes(emptyLockPath, Array.Empty<byte>());
    AssertThrows<InstanceMetadataException>(() => InstanceDataRootLease.Acquire(emptyRoot));
    Assert(new FileInfo(emptyLockPath).Length == 0, "已有 0 B 锁不得自动生成 instance_id");
    return Task.CompletedTask;
}

static async Task TestCancelledStartAsync(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("cancel-start");
    using var lease = InstanceDataRootLease.Acquire(root);
    using var manager = workspace.CreateManager(lease);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
    var result = await manager.StartAsync(
        workspace.CreateSpec(root, "normal", TimeSpan.FromSeconds(3)),
        cancellation.Token);
    workspace.Track(result.Snapshot.Identity);
    Assert(result.Cancelled && result.ErrorCode == "PROCESS_START_CANCELLED", "启动等待应报告取消");
    Assert(result.Snapshot.Identity is not null, "取消后必须保留受管身份");
    Assert(result.Snapshot.State is OwnedProcessState.Running or OwnedProcessState.Unknown, "取消后不得假报未启动或已停止");

    var stopped = await manager.StopAsync(TimeSpan.FromSeconds(2));
    Assert(stopped.Succeeded, "取消后的受管子进程仍应能通过正常协议停止");
}

static async Task TestBoundedRedactedLogsAsync(ProcessTestWorkspace workspace)
{
    const string secret = "test-secret-1234567890";
    var root = workspace.CreateCaseRoot("bounded-log");
    using var lease = InstanceDataRootLease.Acquire(root);
    using var manager = workspace.CreateManager(lease, logCapacity: 12, maxLogLineLength: 128);
    var spec = workspace.CreateSpec(
        root,
        "bounded-log",
        TimeSpan.FromMilliseconds(250),
        new ProcessEnvironmentVariable("LAUNCHER_TEST_SECRET", secret, Sensitive: true));
    var started = await manager.StartAsync(spec);
    workspace.Track(started.Snapshot.Identity);
    Assert(started.Succeeded, "日志场景子进程应启动成功");
    var stopped = await manager.StopAsync(TimeSpan.FromSeconds(2));
    Assert(stopped.Succeeded, "日志场景子进程应正常停止");

    var logs = manager.Logs;
    Assert(logs.Count <= 12, "日志条数不得超过容量");
    Assert(logs.All(entry => entry.Message.Length <= 128), "每条日志不得超过字符上限");
    Assert(logs.All(entry => !entry.Message.Contains(secret, StringComparison.Ordinal)), "敏感值不得出现在日志");
    Assert(logs.Any(entry => entry.Message.Contains("[REDACTED]", StringComparison.Ordinal)), "跨读取块敏感值必须脱敏");
    Assert(logs.Any(entry => entry.Message.Contains("[已截断]", StringComparison.Ordinal)), "超长无界输出必须截断");
}

static async Task TestDisposeDuringStartAsync(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("dispose-during-start");
    using var lease = InstanceDataRootLease.Acquire(root);
    var firstManager = workspace.CreateManager(lease);
    var startTask = firstManager.StartAsync(workspace.CreateSpec(root, "normal", TimeSpan.FromSeconds(3)));
    await WaitUntilAsync(
        () => firstManager.Snapshot.Identity is not null,
        TimeSpan.FromSeconds(2),
        "启动等待中未及时生成受管身份");
    var identity = firstManager.Snapshot.Identity!;
    workspace.Track(identity);

    await firstManager.DisposeAsync();
    var cancelledStart = await startTask;
    Assert(cancelledStart.Cancelled && cancelledStart.Snapshot.Identity == identity, "异步释放应取消等待但保留身份");

    await using var secondManager = workspace.CreateManager(lease);
    var reconnected = await secondManager.TryReconnectAsync(identity);
    Assert(reconnected.Succeeded, "异步释放不得终止子进程，应能由新管理器重连");
    var stopped = await secondManager.StopAsync(TimeSpan.FromSeconds(2));
    Assert(stopped.Succeeded, "重连后应通过独立正常协议停止");
}

static async Task TestReconnectAsync(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("reconnect");
    using var lease = InstanceDataRootLease.Acquire(root);
    OwnedProcessIdentity identity;
    using (var firstManager = workspace.CreateManager(lease))
    {
        var started = await firstManager.StartAsync(workspace.CreateSpec(root, "normal", TimeSpan.FromMilliseconds(200)));
        workspace.Track(started.Snapshot.Identity);
        Assert(started.Succeeded, "重连场景子进程应先启动");
        identity = started.Snapshot.Identity!;
    }

    Assert(lease.IsHeld, "释放进程管理器不得释放共享实例锁");
    using var secondManager = workspace.CreateManager(lease);
    var reconnected = await secondManager.TryReconnectAsync(identity);
    Assert(reconnected.Succeeded && reconnected.Snapshot.State == OwnedProcessState.Running, "应按完整身份复核重连");
    var stopped = await secondManager.StopAsync(TimeSpan.FromSeconds(2));
    Assert(stopped.Succeeded, "重连后应通过独立控制通道正常停止");
}

static Task TestInstanceSecretsRoundTrip(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("secrets-roundtrip");
    var pgData = Path.Combine(root, "postgres", "cluster");
    Directory.CreateDirectory(pgData);
    InstanceSecrets created;
    InstanceSecrets loaded;
    using (var lease = InstanceDataRootLease.Acquire(root))
    {
        var store = new WindowsInstanceSecretsStore();
        created = store.CreateNew(lease, pgData);
        loaded = store.Load(lease);
        Assert(!Directory.EnumerateFiles(Path.Combine(root, "config"), "*.tmp").Any(), "成功提交后不得残留唯一暂存文件");
    }

    AssertSecretsEqual(created, loaded);
    Assert(created.Values().All(value => Convert.FromBase64String(value).Length >= 32), "每个凭据必须至少包含 32 字节 CSPRNG 数据");
    Assert(created.ToString() == "InstanceSecrets([REDACTED])", "对象字符串不得回显凭据");
    AssertNoPlaintextSecrets(root, created);
    return Task.CompletedTask;
}

static Task TestInstanceSecretsAfterMove(ProcessTestWorkspace workspace)
{
    var parent = workspace.CreateCaseRoot("secrets-move");
    var sourceRoot = Path.Combine(parent, "source");
    var targetRoot = Path.Combine(parent, "moved");
    Directory.CreateDirectory(sourceRoot);
    InstanceSecrets created;
    Guid instanceId;
    using (var lease = InstanceDataRootLease.Acquire(sourceRoot))
    {
        instanceId = lease.InstanceId;
        var pgData = Path.Combine(sourceRoot, "postgres", "cluster");
        Directory.CreateDirectory(pgData);
        created = new WindowsInstanceSecretsStore().CreateNew(lease, pgData);
    }

    Directory.Move(sourceRoot, targetRoot);
    using var movedLease = InstanceDataRootLease.Acquire(targetRoot);
    Assert(movedLease.InstanceId == instanceId, "移动数据根后必须保留 instance_id");
    var loaded = new WindowsInstanceSecretsStore().Load(movedLease);
    AssertSecretsEqual(created, loaded);
    return Task.CompletedTask;
}

static Task TestTamperedInstanceSecrets(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("secrets-tamper");
    var pgData = Path.Combine(root, "postgres", "cluster");
    Directory.CreateDirectory(pgData);
    using var lease = InstanceDataRootLease.Acquire(root);
    var store = new WindowsInstanceSecretsStore();
    _ = store.CreateNew(lease, pgData);
    var path = store.GetSecretsPath(lease);

    using (var document = JsonDocument.Parse(File.ReadAllBytes(path)))
    {
        var protectedPayload = Convert.FromBase64String(document.RootElement.GetProperty("protected_payload").GetString()!);
        protectedPayload[protectedPayload.Length / 2] ^= 0x5A;
        var tamperedEnvelope = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format_version = 1,
            instance_id = lease.InstanceId,
            protected_payload = Convert.ToBase64String(protectedPayload),
        });
        File.WriteAllBytes(path, tamperedEnvelope);
    }

    var tamperedSnapshot = File.ReadAllBytes(path);
    AssertThrows<InstanceSecretsException>(() => store.Load(lease));
    Assert(File.ReadAllBytes(path).SequenceEqual(tamperedSnapshot), "加载篡改密文失败时不得修改原文件");

    var oversized = new byte[(64 * 1024) + 1];
    Array.Fill(oversized, (byte)'X');
    File.WriteAllBytes(path, oversized);
    var oversizedSnapshot = File.ReadAllBytes(path);
    AssertThrows<InstanceSecretsFormatException>(() => store.Load(lease));
    Assert(File.ReadAllBytes(path).SequenceEqual(oversizedSnapshot), "超限文件加载失败时不得修改原文件");

    var nullPayloadEnvelope = JsonSerializer.SerializeToUtf8Bytes(new
    {
        format_version = 1,
        instance_id = lease.InstanceId,
        protected_payload = (string?)null,
    });
    File.WriteAllBytes(path, nullPayloadEnvelope);
    var nullPayloadSnapshot = File.ReadAllBytes(path);
    AssertThrows<InstanceSecretsFormatException>(() => store.Load(lease));
    Assert(File.ReadAllBytes(path).SequenceEqual(nullPayloadSnapshot), "null 密文字段失败时不得修改原文件");

    var entropy = Encoding.UTF8.GetBytes($"ai-goofish-monitor/instance-secrets/v1/{lease.InstanceId:D}");
    var missingSecretPayload = JsonSerializer.SerializeToUtf8Bytes(new
    {
        format_version = 1,
        instance_id = lease.InstanceId,
        postgres_bootstrap_admin_password = Convert.ToBase64String(new byte[32]),
        application_database_password = Convert.ToBase64String(new byte[32]),
        probe_database_password = Convert.ToBase64String(new byte[32]),
        encryption_master_key = Convert.ToBase64String(new byte[32]),
    });
    var protectedMissingSecret = new WindowsCurrentUserDataProtector().Protect(missingSecretPayload, entropy);
    var missingSecretEnvelope = JsonSerializer.SerializeToUtf8Bytes(new
    {
        format_version = 1,
        instance_id = lease.InstanceId,
        protected_payload = Convert.ToBase64String(protectedMissingSecret),
    });
    File.WriteAllBytes(path, missingSecretEnvelope);
    var missingSecretSnapshot = File.ReadAllBytes(path);
    AssertThrows<InstanceSecretsFormatException>(() => store.Load(lease));
    Assert(File.ReadAllBytes(path).SequenceEqual(missingSecretSnapshot), "缺失密钥字段失败时不得修改原文件");
    return Task.CompletedTask;
}

static Task TestWrongInstanceAndExistingSecrets(ProcessTestWorkspace workspace)
{
    var firstRoot = workspace.CreateCaseRoot("secrets-first-instance");
    var secondRoot = workspace.CreateCaseRoot("secrets-second-instance");
    using var firstLease = InstanceDataRootLease.Acquire(firstRoot);
    var firstPgData = Path.Combine(firstRoot, "postgres", "cluster");
    Directory.CreateDirectory(firstPgData);
    var store = new WindowsInstanceSecretsStore();
    _ = store.CreateNew(firstLease, firstPgData);
    var firstPath = store.GetSecretsPath(firstLease);
    var existingSnapshot = File.ReadAllBytes(firstPath);
    AssertThrows<InstanceSecretsAlreadyExistException>(() => store.CreateNew(firstLease, firstPgData));
    Assert(File.ReadAllBytes(firstPath).SequenceEqual(existingSnapshot), "重复 CreateNew 不得覆盖已有凭据");

    using var secondLease = InstanceDataRootLease.Acquire(secondRoot);
    var secondPath = store.GetSecretsPath(secondLease);
    Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
    File.Copy(firstPath, secondPath);
    var wrongInstanceSnapshot = File.ReadAllBytes(secondPath);
    AssertThrows<InstanceSecretsFormatException>(() => store.Load(secondLease));
    Assert(File.ReadAllBytes(secondPath).SequenceEqual(wrongInstanceSnapshot), "错误实例加载失败时不得修改文件");
    return Task.CompletedTask;
}

static Task TestPostgresDataGuard(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("secrets-pgdata-guard");
    using var lease = InstanceDataRootLease.Acquire(root);
    var store = new WindowsInstanceSecretsStore();
    var pgData = Path.Combine(root, "postgres", "cluster");
    Directory.CreateDirectory(pgData);
    File.WriteAllText(Path.Combine(pgData, "PG_VERSION"), "17", Encoding.UTF8);
    var pgSnapshot = File.ReadAllBytes(Path.Combine(pgData, "PG_VERSION"));
    AssertThrows<InstanceSecretsException>(() => store.CreateNew(lease, pgData));
    Assert(File.ReadAllBytes(Path.Combine(pgData, "PG_VERSION")).SequenceEqual(pgSnapshot), "非空 PGDATA 不得被修改");
    Assert(!File.Exists(store.GetSecretsPath(lease)), "非空 PGDATA 不得生成凭据文件");

    var outsidePgData = Path.Combine(workspace.Root, "outside-p", "cluster");
    AssertThrows<ArgumentException>(() => store.CreateNew(lease, outsidePgData));
    Assert(!Directory.Exists(outsidePgData), "越界 PGDATA 校验不得创建目录");
    return Task.CompletedTask;
}

static Task TestNewInstancePathLengthGuard(ProcessTestWorkspace workspace)
{
    var drive = Path.GetPathRoot(workspace.Root)!;
    var maximum = PostgresPathGuard.MaximumNewInstanceRootLength;
    var accepted = drive + new string('x', maximum - drive.Length);
    var rejected = accepted + "x";
    PostgresPathGuard.EnsureNewInstancePathLength(accepted);
    AssertThrows<PostgresLifecycleException>(() => PostgresPathGuard.EnsureNewInstancePathLength(rejected));
    Assert(!Directory.Exists(accepted) && !Directory.Exists(rejected),
        "路径预检不得创建或修改数据库目录");
    return Task.CompletedTask;
}

static Task TestPostgresInitializationProgressMetadata()
{
    var instanceId = Guid.NewGuid();
    const string dataDirectory = "F:\\portable-data\\postgres\\cluster";
    const string engineVersion = "17.6-portable";
    const string initDbExecutable = "F:\\portable-bin\\initdb.exe";
    var validHelper = new PostgresHelperIdentity(1234, DateTimeOffset.UtcNow, initDbExecutable);
    bool IsValid(int formatVersion = 1, Guid? id = null, string? directory = dataDirectory,
        string? engine = engineVersion, string? state = "Running", PostgresHelperIdentity? helper = null,
        bool useValidHelper = false) =>
        WindowsPostgresComponent.IsValidInitializationProgress(
            formatVersion, id ?? instanceId, directory, engine, state, useValidHelper ? validHelper : helper,
            instanceId, dataDirectory, engineVersion, initDbExecutable);

    Assert(IsValid(useValidHelper: true), "带正确 initdb 辅助身份的 Running 记录应可分类为初始化中断");
    Assert(IsValid(state: "Starting"), "匹配实例的 Starting 进度记录应可分类为初始化中断");
    Assert(!IsValid(), "Running 状态缺少辅助进程身份必须视为坏状态");
    Assert(!IsValid(state: "Starting", helper: validHelper), "Starting 状态出现辅助身份必须视为坏状态");
    Assert(!IsValid(helper: validHelper with { ProcessId = 0 }), "无效辅助进程 ID 必须视为坏状态");
    Assert(!IsValid(helper: validHelper with { StartedAtUtc = default }), "无效辅助进程启动时刻必须视为坏状态");
    Assert(!IsValid(helper: validHelper with { ExecutablePath = "F:\\other\\postgres.exe" }), "非 initdb 可执行文件必须视为坏状态");
    Assert(!IsValid(formatVersion: 0), "未知格式必须视为坏状态");
    Assert(!IsValid(id: Guid.NewGuid()), "其他实例的记录必须视为坏状态");
    Assert(!IsValid(directory: "F:\\other\\cluster"), "其他数据目录必须视为坏状态");
    Assert(!IsValid(engine: "18.0"), "其他 PostgreSQL 引擎版本必须视为坏状态");
    Assert(!IsValid(state: "Complete"), "未知状态必须视为坏状态");
    Assert(!IsValid(directory: null), "缺失数据目录必须视为坏状态");
    Console.WriteLine("POSTGRES_INIT_METADATA=PASS");
    return Task.CompletedTask;
}

static Task TestAtomicCommitCleanup(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("secrets-commit-failure");
    var pgData = Path.Combine(root, "postgres", "cluster");
    Directory.CreateDirectory(pgData);
    using var lease = InstanceDataRootLease.Acquire(root);
    var store = new WindowsInstanceSecretsStore(new FailingAtomicFileCommitter());
    AssertThrows<InstanceSecretsException>(() => store.CreateNew(lease, pgData));
    Assert(!File.Exists(store.GetSecretsPath(lease)), "提交失败不得留下目标文件");
    var configDirectory = Path.Combine(root, "config");
    Assert(!Directory.EnumerateFiles(configDirectory, ".instance-secrets.*.tmp").Any(), "提交失败必须清理唯一暂存文件");
    return Task.CompletedTask;
}

static Task TestRestoredHandoffImport(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("restore-import");
    var target = Path.Combine(root, "target");
    var parent = Path.Combine(root, "private");
    Directory.CreateDirectory(target);
    Directory.CreateDirectory(parent);
    using var lease = InstanceDataRootLease.Acquire(target);
    var pgData = WriteRestoredClusterMarker(lease);
    var handoff = WriteRestoreHandoff(parent, lease.InstanceId);
    using var handoffJson = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(handoff, "secrets.json")));
    var expectedAppPassword = handoffJson.RootElement.GetProperty("app_database_password").GetString();
    var expectedProbePassword = handoffJson.RootElement.GetProperty("probe_database_password").GetString();
    var admin = Convert.ToBase64String(Enumerable.Repeat((byte)11, 32).ToArray());
    var store = new WindowsInstanceSecretsStore();

    var imported = store.ImportRestoredHandoff(lease, pgData, parent, handoff, admin);
    var loaded = store.Load(lease);
    AssertSecretsEqual(imported, loaded);
    Assert(loaded.ApplicationDatabasePassword == expectedAppPassword &&
        loaded.ProbeDatabasePassword == expectedProbePassword &&
        loaded.ApplicationDatabasePassword.Contains('-') &&
        loaded.ProbeDatabasePassword.Contains('_'), "URL 安全角色口令必须原样保存在 DPAPI 内");
    Assert(loaded.PostgresBootstrapAdminPassword == admin, "必须保留目标集群初始化管理员口令");
    Assert(!Directory.Exists(handoff), "成功导入后必须清理私有明文交接目录");
    lease.Dispose();
    AssertNoPlaintextSecrets(target, loaded);
    return Task.CompletedTask;
}

static Task TestRestoredHandoffGuards(ProcessTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("restore-guards");
    var target = Path.Combine(root, "target");
    var parent = Path.Combine(root, "private");
    Directory.CreateDirectory(target);
    Directory.CreateDirectory(parent);
    using var lease = InstanceDataRootLease.Acquire(target);
    var pgData = WriteRestoredClusterMarker(lease);
    var admin = Convert.ToBase64String(Enumerable.Repeat((byte)11, 32).ToArray());
    var store = new WindowsInstanceSecretsStore();

    var markerPath = Path.Combine(pgData, ".aigoofish-cluster.json");
    var validMarker = File.ReadAllBytes(markerPath);
    File.WriteAllText(markerPath, JsonSerializer.Serialize(new
    {
        format_version = 1,
        instance_id = Guid.NewGuid(),
        cluster_id = Guid.NewGuid(),
        engine_version = "17.11",
        created_at_utc = DateTimeOffset.UtcNow,
    }), new UTF8Encoding(false));
    var markerGuardHandoff = WriteRestoreHandoff(parent, lease.InstanceId);
    AssertThrows<InstanceSecretsFormatException>(() =>
        store.ImportRestoredHandoff(lease, pgData, parent, markerGuardHandoff, admin));
    Assert(Directory.Exists(markerGuardHandoff), "错误目标集群标记时不得消费交接文件");
    File.WriteAllBytes(markerPath, validMarker);

    var wrongHandoff = WriteRestoreHandoff(parent, Guid.NewGuid());
    AssertThrows<InstanceSecretsFormatException>(() =>
        store.ImportRestoredHandoff(lease, pgData, parent, wrongHandoff, admin));
    Assert(!Directory.Exists(wrongHandoff), "已确认归属的错误身份交接也必须清理");
    Assert(!File.Exists(store.GetSecretsPath(lease)), "错误身份不得生成 DPAPI 凭据");

    var correctHandoff = WriteRestoreHandoff(parent, lease.InstanceId);
    var secretsPath = store.GetSecretsPath(lease);
    Directory.CreateDirectory(Path.GetDirectoryName(secretsPath)!);
    File.WriteAllText(secretsPath, "existing-real-target", new UTF8Encoding(false));
    var snapshot = File.ReadAllBytes(secretsPath);
    AssertThrows<InstanceSecretsAlreadyExistException>(() =>
        store.ImportRestoredHandoff(lease, pgData, parent, correctHandoff, admin));
    Assert(File.ReadAllBytes(secretsPath).SequenceEqual(snapshot), "已有目标 DPAPI 不得被覆盖");
    Assert(Directory.Exists(correctHandoff), "已有目标被拒绝时不得消费交接文件");
    return Task.CompletedTask;
}

static string WriteRestoredClusterMarker(InstanceDataRootLease lease)
{
    var pgData = Path.Combine(lease.InstanceRoot, "postgres", "cluster");
    Directory.CreateDirectory(pgData);
    File.WriteAllText(Path.Combine(pgData, "PG_VERSION"), "17\n", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(pgData, ".aigoofish-cluster.json"), JsonSerializer.Serialize(new
    {
        format_version = 1,
        instance_id = lease.InstanceId,
        cluster_id = Guid.NewGuid(),
        engine_version = "17.11",
        created_at_utc = DateTimeOffset.UtcNow,
    }), new UTF8Encoding(false));
    return pgData;
}

static string WriteRestoreHandoff(string parent, Guid instanceId)
{
    var directory = Path.Combine(parent, ".restore-handoff-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    static string Standard(byte value) => Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray());
    static string UrlSafe(byte value) => Convert.ToBase64String(Enumerable.Repeat(value, 36).ToArray())
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    File.WriteAllText(Path.Combine(directory, "secrets.json"), JsonSerializer.Serialize(new
    {
        format_version = 1,
        instance_id = instanceId,
        encryption_master_key = Standard(1),
        secret_key = Standard(2),
        app_database_password = UrlSafe(251),
        probe_database_password = UrlSafe(255),
    }), new UTF8Encoding(false));
    return directory;
}

static void AssertSecretsEqual(InstanceSecrets expected, InstanceSecrets actual)
{
    Assert(expected.PostgresBootstrapAdminPassword == actual.PostgresBootstrapAdminPassword, "PG 初始化管理员密码不一致");
    Assert(expected.ApplicationDatabasePassword == actual.ApplicationDatabasePassword, "应用数据库密码不一致");
    Assert(expected.ProbeDatabasePassword == actual.ProbeDatabasePassword, "探测数据库密码不一致");
    Assert(expected.EncryptionMasterKey == actual.EncryptionMasterKey, "ENCRYPTION_MASTER_KEY 不一致");
    Assert(expected.SecretKey == actual.SecretKey, "SECRET_KEY 不一致");
}

static void AssertNoPlaintextSecrets(string root, InstanceSecrets secrets)
{
    foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        var content = File.ReadAllBytes(path);
        foreach (var secret in secrets.Values())
        {
            Assert(content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0, "生成文件不得包含明文凭据");
        }
    }
}

static void AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"预期抛出 {typeof(TException).Name}。");
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

    throw new InvalidOperationException($"预期异步抛出 {typeof(TException).Name}。");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failureMessage)
{
    var startedAt = DateTimeOffset.UtcNow;
    while (!condition())
    {
        if (DateTimeOffset.UtcNow - startedAt >= timeout)
        {
            throw new TimeoutException(failureMessage);
        }

        await Task.Delay(10);
    }
}

internal sealed class TestStopProtocol : IProcessStopProtocol
{
    public async Task RequestStopAsync(OwnedProcessIdentity identity, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: GetPipeName(identity.RunId),
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000, cancellationToken);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
        {
            AutoFlush = true,
        };
        await writer.WriteLineAsync("stop".AsMemory(), cancellationToken);
    }

    public static string GetPipeName(Guid runId) => $"AiGoofishLauncherTest-{runId:N}";
}

internal sealed class HangingStopProtocol : IProcessStopProtocol
{
    private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int RequestCount { get; private set; }

    public Task RequestStopAsync(OwnedProcessIdentity identity, CancellationToken cancellationToken)
    {
        RequestCount++;
        return _never.Task;
    }
}

internal sealed class FailingAtomicFileCommitter : IAtomicFileCommitter
{
    public void Commit(string temporaryPath, string targetPath)
    {
        throw new IOException("模拟原子提交失败");
    }
}

internal sealed class ProcessTestWorkspace : IDisposable
{
    private readonly string _baseRoot;
    private readonly List<OwnedProcessIdentity> _tracked = new();
    private readonly TestStopProtocol _stopProtocol = new();

    public ProcessTestWorkspace(string baseRoot)
    {
        _baseRoot = Path.GetFullPath(baseRoot);
        Directory.CreateDirectory(_baseRoot);
        Root = Path.Combine(_baseRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        ChildExecutable = Path.Combine(AppContext.BaseDirectory, "AiGoofish.Launcher.Platform.Windows.Tests.exe");
        if (!File.Exists(ChildExecutable))
        {
            throw new FileNotFoundException("未找到 Windows 测试子进程入口。", ChildExecutable);
        }
    }

    public string Root { get; }

    public string ChildExecutable { get; }

    public string CreateCaseRoot(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public WindowsOwnedProcessManager CreateManager(
        InstanceDataRootLease lease,
        int logCapacity = 64,
        int maxLogLineLength = 512)
    {
        return new WindowsOwnedProcessManager(lease, _stopProtocol, logCapacity, maxLogLineLength);
    }

    public OwnedProcessLaunchSpec CreateSpec(
        string workingDirectory,
        string mode,
        TimeSpan startupGracePeriod,
        params ProcessEnvironmentVariable[] additionalEnvironment)
    {
        var environment = new List<ProcessEnvironmentVariable>(additionalEnvironment);
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            environment.Add(new ProcessEnvironmentVariable("DOTNET_ROOT", dotnetRoot));
            environment.Add(new ProcessEnvironmentVariable("DOTNET_MULTILEVEL_LOOKUP", "0"));
        }

        return new OwnedProcessLaunchSpec(
            ChildExecutable,
            Path.GetFullPath(workingDirectory),
            new[] { "--child", mode },
            environment,
            startupGracePeriod);
    }

    public void Track(OwnedProcessIdentity? identity)
    {
        if (identity is not null && _tracked.All(item => item.RunId != identity.RunId))
        {
            _tracked.Add(identity);
        }
    }

    public async Task RequestTrackedChildrenExitAsync()
    {
        foreach (var identity in _tracked)
        {
            Process? process;
            try
            {
                process = Process.GetProcessById(identity.ProcessId);
            }
            catch (ArgumentException)
            {
                continue;
            }

            using (process)
            {
                if (process.HasExited)
                {
                    continue;
                }

                var observedStartTime = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                var observedExecutable = process.MainModule?.FileName;
                if (observedStartTime.UtcTicks != identity.StartedAtUtc.UtcTicks ||
                    string.IsNullOrWhiteSpace(observedExecutable) ||
                    !string.Equals(Path.GetFullPath(observedExecutable), identity.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"跟踪 PID {identity.ProcessId} 的启动时间或 EXE 已变化，拒绝发送清理请求并保留测试目录。");
                }

                for (var attempt = 0; attempt < 2 && !process.HasExited; attempt++)
                {
                    try
                    {
                        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                        await _stopProtocol.RequestStopAsync(identity, cancellation.Token);
                    }
                    catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
                    {
                        break;
                    }
                }

                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (TimeoutException exception)
                {
                    throw new InvalidOperationException(
                        $"跟踪的测试子进程 PID {identity.ProcessId} 未在正常协议后退出；未强杀并保留测试目录。",
                        exception);
                }
            }
        }
    }

    public void Dispose()
    {
        var normalizedRoot = Path.GetFullPath(Root);
        if (!normalizedRoot.StartsWith(_baseRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("拒绝清理测试根之外的路径。");
        }

        var info = new DirectoryInfo(normalizedRoot);
        if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("拒绝清理重解析点测试目录。");
        }

        if (info.Exists)
        {
            info.Delete(recursive: true);
        }
    }
}

internal sealed class FakeRestoreExecutor(bool throwDuringRestore = false, bool sourceRunning = false) : IPortableRestoreExecutor
{
    public bool SourceRunning { get; set; } = sourceRunning;
    public Task<PortableRestorePreview> RestoreAndAuditAsync(
        PortableRestoreTarget target,
        string archiveFile,
        string passphrase,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (throwDuringRestore)
        {
            throw new IOException("fixture failure; no secret values");
        }

        var preview = new PortableRestorePreview(
            target.InstanceId,
            target.RelativeRoot,
            Guid.NewGuid().ToString("D"),
            new string('a', 64),
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, long> { ["users"] = 1, ["tasks"] = 0 },
            RestoredFileCount: 3,
            RestoredFileBytes: 4096,
            RevokedSessionCount: 2,
            MaintenanceAuditPassed: true,
            DataLossNotice: "备份创建后产生的数据不会包含在此次恢复中。");
        return Task.FromResult(preview);
    }

    public Task EnsureCandidateStoppedAsync(PortableRestoreTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Match the real executor's ownership contract, including repeat calls.
        target.Lease.EnsureHeld();
        return Task.CompletedTask;
    }

    public Task<IAsyncDisposable?> AcquirePreviousInstanceSwitchGuardAsync(
        PortableRestoreTarget target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SourceRunning)
        {
            throw new PortableRestoreException("RESTORE_SOURCE_NOT_STOPPED", "fixture source still running");
        }

        return Task.FromResult<IAsyncDisposable?>(null);
    }
}

internal sealed class FakeStateComponent(string id, ComponentRuntimeState initialState) : ILauncherComponent
{
    public string Id { get; } = id;
    public string DisplayName => Id;
    public ComponentRuntimeState State { get; set; } = initialState;

    public ValueTask<ComponentRuntimeState> GetStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(State);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = ComponentRuntimeState.Running;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = ComponentRuntimeState.Stopped;
        return Task.CompletedTask;
    }
}
