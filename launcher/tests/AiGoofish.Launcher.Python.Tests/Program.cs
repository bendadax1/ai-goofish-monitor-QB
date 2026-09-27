using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiGoofish.Launcher.Core;
using AiGoofish.Launcher.Platform.Windows;

if (args.Length == 1 && args[0] == "--real-stack")
{
    return await RealPortableStack.RunAsync();
}
if (args.Length == 1 && args[0] == "--http-contracts")
{
    return await HttpFaultCases.RunAsync();
}
if (args.Length == 1 && args[0] == "--loopback-peer-guard")
{
    return await LoopbackPeerGuardAcceptance.RunAsync();
}
if (args.Length == 2 && args[0] == "--bundle-acceptance")
{
    return await BundleAcceptance.RunAsync(args[1]);
}
if (args.Length == 2 && args[0] == "--config-pg-cas-port-selftest")
{
    ConfigPostgresCasAcceptance.RunWebPortHarnessSelfTests(args[1]);
    Console.WriteLine("CONFIG_PG_CAS_WEB_PORT_SELFTEST=PASS");
    return 0;
}
if (args.Length == 2 && args[0] == "--web-port-host-e2e")
{
    return await ConfigPostgresCasAcceptance.RunRealWebPortHostAcceptanceAsync(args[1]);
}
if (args.Length > 0 && args[0] == "--config-pg-cas")
{
    if (args.Length == 2)
        return await ConfigPostgresCasAcceptance.RunAsync(args[1]);
    if (args.Length == 4 && args[2] == "--web-port" &&
        ConfigPostgresCasAcceptance.TryParseWebPort(args[3], out var webPort))
    {
        return await ConfigPostgresCasAcceptance.RunAsync(args[1], webPort);
    }

    Console.Error.WriteLine("用法: --config-pg-cas <bundle-root> [--web-port 1024..65535]");
    return 2;
}
if (args.Length == 1 && args[0] == "--business-backup-restore-fixture-path-selftest")
{
    var repository = Path.GetFullPath(Environment.GetEnvironmentVariable("AIGOOFISH_REPOSITORY_ROOT")
        ?? throw new InvalidOperationException("Explicit repository root is required."));
    BusinessBackupRestoreAcceptance.RunFixturePathSelfTests(repository);
    return 0;
}
if (args.Length == 1 && args[0] == "--business-backup-restore-process-guard-selftest")
{
    BusinessBackupRestoreAcceptance.RunProcessGuardSelfTests();
    return 0;
}
if (args.Length == 1 && args[0] == "--business-backup-restore-web-port-selftest")
{
    BusinessBackupRestoreAcceptance.RunWebPortFixturePolicySelfTests();
    return 0;
}
if (args.Length == 1 && args[0] == "--config-pg-cas-process-guard")
{
    await TestConfigCasProcessGuardAsync();
    Console.WriteLine("CONFIG_PG_CAS_PROCESS_GUARD=PASS");
    return 0;
}
if (args.Length > 0 && args[0] == "--business-backup-restore-e2e")
{
    if (args.Length == 8 && args[2] == "--release-id" && args[4] == "--web-port" &&
        args[6] == "--fixture-run-id" && BusinessBackupRestoreAcceptance.IsAllowedReleaseId(args[3]) &&
        BusinessBackupRestoreAcceptance.TryParseWebPort(args[5], out var isolatedWebPort))
        return await BusinessBackupRestoreAcceptance.RunAsync(args[1], args[3], isolatedWebPort,
            resumeRetainedFixture: false, fixtureRunId: args[7]);
    var optionIndex = Array.IndexOf(args, "--release-id");
    var webPortIndex = Array.IndexOf(args, "--web-port");
    var resumeRetainedFixture = args.Contains("--resume-retained-fixture", StringComparer.Ordinal);
    var expectedLength = resumeRetainedFixture ? 7 : 6;
    if (args.Length != expectedLength || optionIndex != 2 || webPortIndex != 4 ||
        args[3] is not { } releaseId || !BusinessBackupRestoreAcceptance.IsAllowedReleaseId(releaseId) ||
        !BusinessBackupRestoreAcceptance.TryParseWebPort(args[5], out var webPort) ||
        (resumeRetainedFixture && args[6] != "--resume-retained-fixture"))
    {
        Console.Error.WriteLine("用法: --business-backup-restore-e2e <launcher/dist/portable-{release-id}> --release-id <acceptance-frozen-YYYYMMDD-p1-rN> --web-port <1024..65535> [--resume-retained-fixture]，N 必须大于等于 3，Web 端口不得为 55432 或 58000。");
        return 2;
    }

    return await BusinessBackupRestoreAcceptance.RunAsync(args[1], releaseId, webPort, resumeRetainedFixture);
}

if (args.Length > 0 && args[0] == "--child")
{
    return await RunChildAsync(args.Skip(1).ToArray());
}
if (args.Contains("--app-root", StringComparer.Ordinal) && args.Contains("--port", StringComparer.Ordinal))
{
    var portIndex = Array.IndexOf(args, "--port");
    var behaviorPath = Path.Combine(Environment.CurrentDirectory, "python-test-behavior.txt");
    if (portIndex < 0 || portIndex + 1 >= args.Length || !File.Exists(behaviorPath))
    {
        return 2;
    }

    var behavior = (await File.ReadAllTextAsync(behaviorPath, Encoding.UTF8)).Trim();
    return await RunChildAsync(new[] { args[portIndex + 1], behavior });
}

var testBaseRoot = RequireAbsoluteEnvironmentPath("AIGOOFISH_PYTHON_TEST_ROOT");
var workspace = new PythonTestWorkspace(testBaseRoot);
var tests = new (string Name, Func<PythonTestWorkspace, Task> Run)[]
{
    ("维护服务就绪与正常关闭", TestLifecycleAsync),
    ("CAS验收进程预检按可证实归属拦截", _ => TestConfigCasProcessGuardAsync()),
    ("不同实例就绪响应被拒绝", TestWrongInstanceRejectedAsync),
    ("不兼容schema不标记运行", TestIncompatibleSchemaRejectedAsync),
    ("超限HTTP响应不标记运行", TestOversizedResponseRejectedAsync),
    ("HTTP 202后仍等待真实退出", TestAcceptedShutdownStillWaitsAsync),
    ("DPAPI控制凭据不落runtime且可崩溃重连", TestProtectedCredentialReconnectAsync),
    ("setup与control分权并绑定完整进程身份", TestCredentialStoreBinding),
    ("DPAPI控制凭据绑定程序根", TestCredentialStoreRejectsChangedProgramRoot),
    ("凭据篡改超限重复创建和原子失败均拒绝", TestCredentialStoreFailureCases),
    ("无runtime的pending凭据阻断spawn", TestPendingCredentialBlocksSpawnAsync),
    ("旧runtime已停止可识别为Stopped", TestStoppedLegacyRuntimeStateAsync),
    ("旧runtime无凭据拒绝接管live进程", TestLegacyRuntimeRejectsLiveProcessAsync),
    ("重连未Ready不误报Running且可原身份重试", TestReconnectNotReadyCanRetryAsync),
    ("Python启动参数与敏感环境有界", TestLaunchContract),
};
var failures = new List<string>();
try
{
    foreach (var test in tests)
    {
        try
        {
            await test.Run(workspace);
            Console.WriteLine($"PASS {test.Name}");
        }
        catch (Exception exception)
        {
            failures.Add($"FAIL {test.Name}: {exception.Message}");
            Console.Error.WriteLine(failures[^1]);
        }
    }
}
finally
{
    try
    {
        await workspace.StopTrackedChildrenAsync();
        workspace.Dispose();
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL Python测试收尾: {exception.Message}");
        Console.Error.WriteLine(failures[^1]);
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"共 {failures.Count} 项 Python 生命周期验收失败。");
    return 1;
}

Console.WriteLine($"全部 {tests.Length} 项 Python 生命周期验收通过。");
return 0;

static async Task TestLifecycleAsync(PythonTestWorkspace workspace)
{
    using var context = workspace.CreateContext("lifecycle", ChildBehavior.ReadyAndExitOnShutdown);
    await using var component = context.CreateComponent();
    await component.StartAsync(CancellationToken.None);
    var identity = component.ProcessSnapshot.Identity ?? throw new InvalidOperationException("启动后缺少Python身份");
    workspace.Track(identity);
    Assert(await component.GetStateAsync(CancellationToken.None) == ComponentRuntimeState.Running, "Python服务应为Running");
    await component.StopAsync(CancellationToken.None);
    workspace.MarkStopped(identity);
    Assert(await component.GetStateAsync(CancellationToken.None) == ComponentRuntimeState.Stopped, "关闭并确认进程退出后应为Stopped");
}

static Task TestConfigCasProcessGuardAsync()
{
    var repository = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cas-repository"));
    var bundlePython = Path.Combine(repository, ".tmp", "bundle", "python.exe");
    var bundlePostgres = Path.Combine(repository, ".tmp", "bundle", "postgres.exe");
    var fixture = Path.Combine(repository, ".tmp", "tests", "fixture");
    var unrelatedPython = new ConfigCasProcessEvidence(101, 1, 10_001, "python", Path.Combine(Path.GetTempPath(), "DLSS5Tool", "python.exe"));
    Assert(ConfigPostgresCasProcessGuard.FindConflicts(new[] { unrelatedPython }, new Dictionary<int, ConfigCasAncestorIdentity>(), repository, bundlePython, bundlePostgres, fixture).Count == 0,
        "系统 Python 的无关用户进程应允许通过进程身份预检");

    var targetPython = unrelatedPython with { ProcessId = 102, ImagePath = bundlePython };
    Assert(ConfigPostgresCasProcessGuard.FindConflicts(new[] { targetPython }, new Dictionary<int, ConfigCasAncestorIdentity>(), repository, bundlePython, bundlePostgres, fixture).Count == 1,
        "目标 bundle Python 必须被拦截");

    var repositoryPostgres = new ConfigCasProcessEvidence(103, 1, 10_003, "postgres", Path.Combine(repository, ".tmp", "dependencies", "pg", "bin", "postgres.exe"));
    Assert(ConfigPostgresCasProcessGuard.FindConflicts(new[] { repositoryPostgres }, new Dictionary<int, ConfigCasAncestorIdentity>(), repository, bundlePython, bundlePostgres, fixture).Count == 1,
        "仓库依赖目录中的 PostgreSQL 必须被拦截");

    var ancestor = new Dictionary<int, ConfigCasAncestorIdentity>
    {
        [102] = new ConfigCasAncestorIdentity(102, targetPython.StartedAtUtcTicks!.Value),
    };
    Assert(ConfigPostgresCasProcessGuard.FindConflicts(new[] { targetPython }, ancestor, repository, bundlePython, bundlePostgres, fixture).Count == 0,
        "只有 PID 和创建时间匹配的当前 runner 祖先进程才能豁免");
    Assert(ConfigPostgresCasProcessGuard.FindConflicts(new[] { targetPython with { StartedAtUtcTicks = 10_002 } }, ancestor, repository, bundlePython, bundlePostgres, fixture).Count == 1,
        "PID 被复用且创建时间不同的 bundle Python 必须拦截");
    Assert(ConfigPostgresCasProcessGuard.FindConflicts(new[] { targetPython with { StartedAtUtcTicks = null } }, ancestor, repository, bundlePython, bundlePostgres, fixture).Count == 1,
        "缺少创建时间时不得豁免 PID 相同的进程");

    var unreadableUnrelatedPython = unrelatedPython with { ProcessId = 104, StartedAtUtcTicks = null, ImagePath = null };
    Assert(ConfigPostgresCasProcessGuard.FindConflicts(new[] { unreadableUnrelatedPython }, new Dictionary<int, ConfigCasAncestorIdentity>(), repository, bundlePython, bundlePostgres, fixture).Count == 0,
        "映像路径不可读且无端口/PGDATA冲突的无关 Python 进程应允许通过身份预检");

    var resourceConflictRejected = false;
    try
    {
        ConfigPostgresCasResourceGuard.RequireAvailable(
            Path.Combine(fixture, "postgres", "cluster"), 15432, 8000,
            _ => { }, port => { if (port == 15432) throw new IOException("fake occupied port"); });
    }
    catch (IOException) { resourceConflictRejected = true; }
    Assert(resourceConflictRejected, "映像不可读的 Python 进程存在时，目标 PostgreSQL 端口冲突仍必须 fail closed");

    resourceConflictRejected = false;
    try
    {
        ConfigPostgresCasResourceGuard.RequireAvailable(
            Path.Combine(fixture, "postgres", "cluster"), 15432, 8000,
            _ => throw new IOException("fake PGDATA marker"), _ => { });
    }
    catch (IOException) { resourceConflictRejected = true; }
    Assert(resourceConflictRejected, "映像不可读的 Python 进程存在时，PGDATA 冲突仍必须 fail closed");

    var unknownPath = repositoryPostgres with { ProcessId = 105, StartedAtUtcTicks = null, ImagePath = null };
    try
    {
        _ = ConfigPostgresCasProcessGuard.FindConflicts(new[] { unknownPath }, new Dictionary<int, ConfigCasAncestorIdentity>(), repository, bundlePython, bundlePostgres, fixture);
        throw new InvalidOperationException("无法确认相关进程映像时应 fail closed");
    }
    catch (IOException) { }
    return Task.CompletedTask;
}

static async Task TestWrongInstanceRejectedAsync(PythonTestWorkspace workspace)
{
    using var context = workspace.CreateContext("wrong-instance", ChildBehavior.WrongInstance);
    await using var component = context.CreateComponent();
    await AssertThrowsAsync<PythonLifecycleException>(() => component.StartAsync(CancellationToken.None));
    await context.StopOwnedComponentAsync(component, workspace);
}

static async Task TestIncompatibleSchemaRejectedAsync(PythonTestWorkspace workspace)
{
    using var context = workspace.CreateContext("incompatible-schema", ChildBehavior.IncompatibleSchema);
    await using var component = context.CreateComponent();
    await AssertThrowsAsync<PythonLifecycleException>(() => component.StartAsync(CancellationToken.None));
    await context.StopOwnedComponentAsync(component, workspace);
}

static async Task TestOversizedResponseRejectedAsync(PythonTestWorkspace workspace)
{
    using var context = workspace.CreateContext("oversized-response", ChildBehavior.OversizedReadyResponse);
    await using var component = context.CreateComponent(startupTimeout: TimeSpan.FromMilliseconds(800));
    await AssertThrowsAsync<PythonLifecycleException>(() => component.StartAsync(CancellationToken.None));
    await context.StopOwnedComponentAsync(component, workspace);
}

static async Task TestAcceptedShutdownStillWaitsAsync(PythonTestWorkspace workspace)
{
    using var context = workspace.CreateContext("accepted-still-running", ChildBehavior.AcceptShutdownButKeepRunning);
    await using var component = context.CreateComponent(stopTimeout: TimeSpan.FromMilliseconds(700));
    await component.StartAsync(CancellationToken.None);
    var identity = component.ProcessSnapshot.Identity ?? throw new InvalidOperationException("启动后缺少Python身份");
    workspace.Track(identity);

    var error = await AssertThrowsAsync<PythonLifecycleException>(() => component.StopAsync(CancellationToken.None));
    Assert(error.Message.Contains("202", StringComparison.Ordinal), "失败消息应明确202不代表退出");
    Assert(component.ProcessSnapshot.State is OwnedProcessState.StopTimedOut, "202后进程未退出应保留StopTimedOut");
    await context.StopOwnedComponentAsync(component, workspace);
}

static async Task TestProtectedCredentialReconnectAsync(PythonTestWorkspace workspace)
{
    using var context = workspace.CreateContext("runtime-record", ChildBehavior.ReadyAndExitOnShutdown);
    var component = context.CreateComponent();
    await component.StartAsync(CancellationToken.None);
    var identity = component.ProcessSnapshot.Identity ?? throw new InvalidOperationException("启动后缺少Python身份");
    workspace.Track(identity);
    var recordBytes = File.ReadAllBytes(component.RuntimeRecordPath);
    var recordText = Encoding.UTF8.GetString(recordBytes);
    Assert(recordText.Contains("\"control_credential_persisted\":true", StringComparison.Ordinal), "运行记录必须声明控制凭据另行受保护持久化");
    Assert(!recordText.Contains("GOOFISH_LAUNCHER_TOKEN", StringComparison.Ordinal), "运行记录不得包含控制token字段");
    var credentialText = Encoding.UTF8.GetString(File.ReadAllBytes(component.ControlCredentialPath));
    Assert(!credentialText.Contains("control_token", StringComparison.Ordinal), "DPAPI外层不得出现控制token字段");
    await component.DisposeAsync();

    await using var reopened = context.CreateComponent();
    Assert(await reopened.GetStateAsync(CancellationToken.None) == ComponentRuntimeState.Unknown, "GetState不应隐式执行带凭据重连");
    var reconnected = await reopened.TryReconnectAsync(CancellationToken.None);
    Assert(reconnected.Succeeded, $"受保护凭据与Ready双重核验后应可重连：{reconnected.ErrorCode}");
    Assert(await reopened.GetStateAsync(CancellationToken.None) == ComponentRuntimeState.Running, "显式重连后应为Running");
    await reopened.StopAsync(CancellationToken.None);
    workspace.MarkStopped(identity);
    Assert(!File.Exists(reopened.ControlCredentialPath), "确认退出后应删除本次运行控制凭据");
}

static Task TestCredentialStoreBinding(PythonTestWorkspace workspace)
{
    using var context = workspace.CreateContext("credential-binding", ChildBehavior.ReadyAndExitOnShutdown);
    var setupToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    var normalOptions = context.Options with { Mode = PythonServiceMode.Normal };
    var store = new WindowsPythonRunCredentialStore();
    var pending = store.CreatePending(context.Lease, normalOptions, setupToken);
    Assert(pending.ControlToken != setupToken, "setup token不得复用control token");
    Assert(pending.ToString() == "PythonRunCredentials([REDACTED])", "凭据对象字符串必须脱敏");
    var identity = new OwnedProcessIdentity(
        Environment.ProcessId,
        new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero),
        normalOptions.PythonExecutable,
        context.Lease.InstanceRoot,
        context.Lease.InstanceId,
        Guid.NewGuid());
    store.Bind(context.Lease, normalOptions, pending, identity);
    var runtime = new PythonRuntimeRecord(
        2, context.Lease.InstanceId, "normal", normalOptions.AppVersion, normalOptions.Port,
        normalOptions.ProgramRoot, normalOptions.DataRoot, "Running", identity,
        pending.CredentialId, true, DateTimeOffset.UtcNow);
    var loaded = store.LoadBound(context.Lease, normalOptions, runtime);
    Assert(loaded.ControlToken == pending.ControlToken && loaded.SetupToken == setupToken,
        "绑定后必须恢复相互独立的control/setup凭据");

    var security = new FileInfo(store.GetCredentialPath(context.Lease)).GetAccessControl();
    Assert(security.AreAccessRulesProtected, "控制凭据DACL必须禁止继承");
    store.DeleteBound(context.Lease, runtime);
    return Task.CompletedTask;
}

static Task TestCredentialStoreRejectsChangedProgramRoot(PythonTestWorkspace workspace)
{
    using var context = workspace.CreateContext("credential-program-root", ChildBehavior.ReadyAndExitOnShutdown);
    var store = new WindowsPythonRunCredentialStore();
    var pending = store.CreatePending(context.Lease, context.Options, null);
    var identity = TestIdentity(context);
    store.Bind(context.Lease, context.Options, pending, identity);
    var runtime = new PythonRuntimeRecord(
        2, context.Lease.InstanceId, "maintenance", context.Options.AppVersion, context.Options.Port,
        context.Options.ProgramRoot, context.Options.DataRoot, "Running", identity,
        pending.CredentialId, true, DateTimeOffset.UtcNow);
    var replacementProgramRoot = Path.Combine(Path.GetDirectoryName(context.Options.ProgramRoot)!, "program-replaced");
    Directory.CreateDirectory(replacementProgramRoot);
    var sameVersionReplacement = context.Options with { ProgramRoot = replacementProgramRoot };
    AssertThrows<PythonLifecycleException>(() => store.LoadBound(context.Lease, sameVersionReplacement, runtime));
    store.DeleteBound(context.Lease, runtime);
    return Task.CompletedTask;
}

static Task TestCredentialStoreFailureCases(PythonTestWorkspace workspace)
{
    using (var context = workspace.CreateContext("credential-tamper", ChildBehavior.ReadyAndExitOnShutdown))
    {
        var store = new WindowsPythonRunCredentialStore();
        var pending = store.CreatePending(context.Lease, context.Options, null);
        var snapshot = File.ReadAllBytes(store.GetCredentialPath(context.Lease));
        AssertThrows<PythonLifecycleException>(() => store.CreatePending(context.Lease, context.Options, null));
        Assert(File.ReadAllBytes(store.GetCredentialPath(context.Lease)).SequenceEqual(snapshot), "重复创建不得覆盖既有密文");
        snapshot[^1] ^= 0x01;
        OverwriteFile(store.GetCredentialPath(context.Lease), snapshot);
        AssertThrows<PythonLifecycleException>(() => store.Bind(context.Lease, context.Options, pending, TestIdentity(context)));
    }

    using (var context = workspace.CreateContext("credential-oversize", ChildBehavior.ReadyAndExitOnShutdown))
    {
        var store = new WindowsPythonRunCredentialStore();
        var pending = store.CreatePending(context.Lease, context.Options, null);
        OverwriteFile(store.GetCredentialPath(context.Lease), new byte[(64 * 1024) + 1]);
        AssertThrows<PythonLifecycleException>(() => store.Bind(context.Lease, context.Options, pending, TestIdentity(context)));
    }

    using (var context = workspace.CreateContext("credential-commit-failure", ChildBehavior.ReadyAndExitOnShutdown))
    {
        var store = new WindowsPythonRunCredentialStore(new FailingPythonCredentialCommitter());
        AssertThrows<PythonLifecycleException>(() => store.CreatePending(context.Lease, context.Options, null));
        Assert(!File.Exists(store.GetCredentialPath(context.Lease)), "原子提交失败不得留下目标凭据");
        Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(store.GetCredentialPath(context.Lease))!, ".python-control.*.tmp").Any(),
            "原子提交失败必须清理本次唯一暂存文件");
    }

    return Task.CompletedTask;
}

static async Task TestPendingCredentialBlocksSpawnAsync(PythonTestWorkspace workspace)
{
    using var context = workspace.CreateContext("pending-blocks-spawn", ChildBehavior.ReadyAndExitOnShutdown);
    _ = new WindowsPythonRunCredentialStore().CreatePending(context.Lease, context.Options, null);
    await using var component = context.CreateComponent();
    await AssertThrowsAsync<PythonLifecycleException>(() => component.StartAsync(CancellationToken.None));
    Assert(component.ProcessSnapshot.Identity is null, "遗留pending凭据必须在spawn前阻断新进程");
}

static async Task TestStoppedLegacyRuntimeStateAsync(PythonTestWorkspace workspace)
{
    using var context = workspace.CreateContext("legacy-stopped", ChildBehavior.ReadyAndExitOnShutdown);
    var missingIdentity = new OwnedProcessIdentity(
        int.MaxValue,
        DateTimeOffset.UnixEpoch,
        context.Options.PythonExecutable,
        context.Lease.InstanceRoot,
        context.Lease.InstanceId,
        Guid.NewGuid());
    var runtime = new PythonRuntimeRecord(
        1, context.Lease.InstanceId, "maintenance", context.Options.AppVersion, context.Options.Port,
        context.Options.ProgramRoot, context.Options.DataRoot, "Running", missingIdentity,
        Guid.Empty, false, DateTimeOffset.UtcNow);
    await using var component = context.CreateComponent();
    PythonStateFile.WriteReplace(component.RuntimeRecordPath, runtime);
    Assert(await component.GetStateAsync(CancellationToken.None) == ComponentRuntimeState.Stopped,
        "已停止的旧v1 runtime应可安全收敛为Stopped");
    await component.StartAsync(CancellationToken.None);
    var replacementIdentity = component.ProcessSnapshot.Identity
        ?? throw new InvalidOperationException("旧v1停止记录后的新启动缺少Python身份");
    workspace.Track(replacementIdentity);
    Assert(await component.GetStateAsync(CancellationToken.None) == ComponentRuntimeState.Running,
        "已停止的旧v1 runtime应允许创建新凭据并启动新进程");
    await component.StopAsync(CancellationToken.None);
    workspace.MarkStopped(replacementIdentity);
}

static async Task TestLegacyRuntimeRejectsLiveProcessAsync(PythonTestWorkspace workspace)
{
    using var context = workspace.CreateContext("legacy-live", ChildBehavior.ReadyAndExitOnShutdown);
    var component = context.CreateComponent();
    await component.StartAsync(CancellationToken.None);
    var identity = component.ProcessSnapshot.Identity ?? throw new InvalidOperationException("启动后缺少Python身份");
    workspace.Track(identity);
    var runtime = PythonStateFile.Read<PythonRuntimeRecord>(component.RuntimeRecordPath, 64 * 1024);
    await component.DisposeAsync();
    File.Delete(Path.Combine(context.Lease.InstanceRoot, WindowsPythonRunCredentialStore.RelativeCredentialPath));
    PythonStateFile.WriteReplace(component.RuntimeRecordPath, runtime with
    {
        FormatVersion = 1,
        CredentialId = Guid.Empty,
        ControlCredentialPersisted = false,
    });
    await using var reopened = context.CreateComponent();
    await AssertThrowsAsync<PythonLifecycleException>(() => reopened.TryReconnectAsync(CancellationToken.None));
    await AssertThrowsAsync<PythonLifecycleException>(() => reopened.StartAsync(CancellationToken.None));
    Assert(reopened.ProcessSnapshot.Identity is null, "旧runtime无凭据不得接管或新spawn覆盖live进程");
    await context.StopChildDirectAsync(identity);
    workspace.MarkStopped(identity);
}

static async Task TestReconnectNotReadyCanRetryAsync(PythonTestWorkspace workspace)
{
    using var context = workspace.CreateContext("reconnect-not-ready", ChildBehavior.ReadyFileControlled);
    var original = context.CreateComponent();
    await original.StartAsync(CancellationToken.None);
    var identity = original.ProcessSnapshot.Identity ?? throw new InvalidOperationException("启动后缺少Python身份");
    workspace.Track(identity);
    await original.DisposeAsync();

    context.SetReady(false);
    await using var reopened = context.CreateComponent();
    var notReady = await reopened.TryReconnectAsync(CancellationToken.None);
    Assert(!notReady.Succeeded && notReady.ErrorCode == "PYTHON_NOT_READY", "HTTP未Ready必须返回明确的重连失败");
    Assert(reopened.ProcessSnapshot.Identity == identity, "未Ready重连必须保留原进程身份");
    Assert(await reopened.GetStateAsync(CancellationToken.None) != ComponentRuntimeState.Running,
        "HTTP未Ready时GetState不得仅凭进程存活误报Running");
    await AssertThrowsAsync<PythonLifecycleException>(() => reopened.StopAsync(CancellationToken.None));
    Assert(reopened.ProcessSnapshot.Identity == identity && reopened.ProcessSnapshot.State is OwnedProcessState.Running,
        "未Ready且尚未恢复控制上下文时不得发送停止或丢失原进程身份");

    context.SetReady(true);
    var retried = await reopened.TryReconnectAsync(CancellationToken.None);
    Assert(retried.Succeeded, $"同一身份Ready后应可安全重试重连：{retried.ErrorCode}");
    Assert(reopened.ProcessSnapshot.Identity == identity, "重试不得替换或spawn第二个进程");
    await reopened.StopAsync(CancellationToken.None);
    workspace.MarkStopped(identity);
}

static Task TestLaunchContract(PythonTestWorkspace workspace)
{
    using var context = workspace.CreateContext("launch-contract", ChildBehavior.ReadyAndExitOnShutdown);
    var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    var spec = PythonConfiguration.CreateLaunchSpec(
        context.Lease,
        context.Secrets,
        context.Options,
        context.Endpoints,
        token);
    Assert(spec.ExecutablePath == context.Options.PythonExecutable, "必须显式使用配置的内置python.exe");
    Assert(spec.WorkingDirectory == context.Options.DataRoot, "cwd必须是实例数据根");
    Assert(spec.PublicArguments.Contains("python-bootstrap.py") || spec.PublicArguments.Contains(context.Options.BootstrapScript), "必须经固定python-bootstrap.py入口");
    Assert(spec.PublicArguments.Contains("maintenance"), "维护组件必须选择maintenance target");
    Assert(!spec.PublicArguments.Any(argument => argument.Contains(token, StringComparison.Ordinal)), "控制token不得进入argv");
    Assert(!spec.PublicArguments.Any(argument => argument.Contains("postgresql://", StringComparison.OrdinalIgnoreCase)), "DSN不得进入argv");
    var names = spec.EnvironmentVariables.Select(variable => variable.Name).ToHashSet(StringComparer.Ordinal);
    Assert(names.SetEquals(new[]
    {
        "GOOFISH_LAUNCHER_TOKEN",
        "GOOFISH_PORTABLE_MODE",
        "GOOFISH_PORTABLE_PROGRAM_ROOT",
        "GOOFISH_PORTABLE_DATA_ROOT",
        "GOOFISH_PORTABLE_CACHE_ROOT",
        "GOOFISH_PORTABLE_BROWSER_ROOT",
        "GOOFISH_PORTABLE_DATABASE_URL",
        "TEMP",
        "TMP",
    }), "维护模式环境必须严格匹配允许列表");
    Assert(spec.EnvironmentVariables.Single(variable => variable.Name == "GOOFISH_LAUNCHER_TOKEN").Sensitive, "控制token必须标记敏感");
    Assert(spec.EnvironmentVariables.Single(variable => variable.Name == "GOOFISH_PORTABLE_DATABASE_URL").Sensitive, "probe DSN必须标记敏感");
    return Task.CompletedTask;
}

static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException exception)
    {
        return exception;
    }

    throw new InvalidOperationException($"预期抛出 {typeof(TException).Name}。");
}

static TException AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException exception)
    {
        return exception;
    }

    throw new InvalidOperationException($"预期抛出 {typeof(TException).Name}。");
}

static OwnedProcessIdentity TestIdentity(PythonTestContext context)
{
    using var process = Process.GetCurrentProcess();
    return new OwnedProcessIdentity(
        process.Id,
        new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
        context.Options.PythonExecutable,
        context.Lease.InstanceRoot,
        context.Lease.InstanceId,
        Guid.NewGuid());
}

static void OverwriteFile(string path, byte[] bytes)
{
    using var stream = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.None);
    stream.Write(bytes);
    stream.Flush(flushToDisk: true);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static string RequireAbsoluteEnvironmentPath(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
    {
        throw new InvalidOperationException($"{name} 必须是绝对路径。");
    }

    return Path.GetFullPath(value);
}

static async Task<int> RunChildAsync(string[] childArguments)
{
    if (childArguments.Length != 2 ||
        !int.TryParse(childArguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
        !Enum.TryParse<ChildBehavior>(childArguments[1], ignoreCase: false, out var behavior))
    {
        Console.Error.WriteLine("Python测试child参数无效。");
        return 2;
    }

    var token = Environment.GetEnvironmentVariable("GOOFISH_LAUNCHER_TOKEN") ?? string.Empty;
    var instanceId = Environment.GetEnvironmentVariable("AIGOOFISH_LAUNCHER_INSTANCE_ID") ?? string.Empty;
    if (token.Length < 32 || string.IsNullOrWhiteSpace(instanceId))
    {
        Console.Error.WriteLine("Python测试child控制环境缺失。");
        return 3;
    }

    using var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    while (true)
    {
        using var peer = await listener.AcceptTcpClientAsync();
        await using var stream = peer.GetStream();
        try
        {
            var request = await ReadRequestAsync(stream);
            var authorized = request.Headers.TryGetValue("Authorization", out var authorization) &&
                authorization == $"Bearer {token}";
            var instanceMatches = request.Headers.TryGetValue("X-Goofish-Instance-Id", out var observedHeaderInstance) &&
                observedHeaderInstance == instanceId;
            if (!authorized)
            {
                await WriteJsonAsync(stream, 401, "Unauthorized", "{\"detail\":\"unauthorized\"}");
                continue;
            }

            if (request.Path == "/internal/ready" && request.Method == "GET")
            {
                if (behavior is ChildBehavior.OversizedReadyResponse)
                {
                    var oversized = Encoding.UTF8.GetBytes(new string('x', 65 * 1024));
                    await WriteResponseAsync(stream, 200, "OK", oversized);
                    continue;
                }

                var observedInstance = behavior is ChildBehavior.WrongInstance ? Guid.NewGuid().ToString("D") : instanceId;
                var schemaVersion = behavior is ChildBehavior.IncompatibleSchema ? 2 : 1;
                var ready = behavior is not ChildBehavior.ReadyFileControlled ||
                    string.Equals(
                        (await File.ReadAllTextAsync(Path.Combine(Environment.CurrentDirectory, "python-test-ready.txt"), Encoding.UTF8)).Trim(),
                        "ready",
                        StringComparison.Ordinal);
                var json = JsonSerializer.Serialize(new
                {
                    protocol_version = 1,
                    instance_id = observedInstance,
                    app_version = "V1.0.4.5",
                    mode = "maintenance",
                    ready,
                    database = new { status = ready ? "available" : "unavailable" },
                    schema = new { status = "compatible", version = schemaVersion, supported_min = 1, supported_max = 1 },
                    failure_reason = ready ? null : "database_unavailable",
                });
                await WriteJsonAsync(stream, ready ? 200 : 503, ready ? "OK" : "Service Unavailable", json);
                continue;
            }

            if (request.Path == "/internal/shutdown" && request.Method == "POST")
            {
                if (!instanceMatches)
                {
                    await WriteJsonAsync(stream, 403, "Forbidden", "{\"detail\":\"control context mismatch\"}");
                    continue;
                }

                await WriteJsonAsync(stream, 202, "Accepted", "{\"status\":\"shutdown_requested\"}");
                if (behavior is not ChildBehavior.AcceptShutdownButKeepRunning)
                {
                    return 0;
                }

                continue;
            }

            await WriteJsonAsync(stream, 404, "Not Found", "{\"detail\":\"not found\"}");
        }
        catch (IOException)
        {
            return 0;
        }
    }
}

static async Task<ChildHttpRequest> ReadRequestAsync(NetworkStream stream)
{
    const int maximumHeaderBytes = 16 * 1024;
    var bytes = new List<byte>(1024);
    var buffer = new byte[1024];
    while (bytes.Count < maximumHeaderBytes)
    {
        var read = await stream.ReadAsync(buffer);
        if (read == 0)
        {
            throw new IOException("HTTP request ended before headers");
        }

        bytes.AddRange(buffer.AsSpan(0, read).ToArray());
        if (bytes.Count >= 4 && Encoding.ASCII.GetString(bytes.ToArray()).Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            break;
        }
    }

    var headerText = Encoding.ASCII.GetString(bytes.ToArray());
    var headerEnd = headerText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
    if (headerEnd < 0)
    {
        throw new IOException("HTTP request headers exceeded limit");
    }

    var lines = headerText[..headerEnd].Split("\r\n", StringSplitOptions.None);
    var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (requestLine.Length != 3)
    {
        throw new IOException("HTTP request line invalid");
    }

    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in lines.Skip(1))
    {
        var separator = line.IndexOf(':');
        if (separator <= 0 || !headers.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim()))
        {
            throw new IOException("HTTP request header invalid");
        }
    }

    return new ChildHttpRequest(requestLine[0], requestLine[1], headers);
}

static Task WriteJsonAsync(NetworkStream stream, int statusCode, string reason, string json) =>
    WriteResponseAsync(stream, statusCode, reason, Encoding.UTF8.GetBytes(json));

static async Task WriteResponseAsync(NetworkStream stream, int statusCode, string reason, byte[] body)
{
    var headers = Encoding.ASCII.GetBytes(
        $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(headers);
    await stream.WriteAsync(body);
    await stream.FlushAsync();
}

internal sealed record ChildHttpRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers);

internal enum ChildBehavior
{
    ReadyAndExitOnShutdown,
    WrongInstance,
    IncompatibleSchema,
    OversizedReadyResponse,
    AcceptShutdownButKeepRunning,
    ReadyFileControlled,
}

internal sealed class PythonTestWorkspace : IDisposable
{
    private readonly string _baseRoot;
    private readonly string _testExecutable;
    private readonly List<OwnedProcessIdentity> _tracked = new();

    public PythonTestWorkspace(string baseRoot)
    {
        _baseRoot = Path.GetFullPath(baseRoot);
        _testExecutable = Path.ChangeExtension(typeof(PythonTestWorkspace).Assembly.Location, ".exe");
        if (!File.Exists(_testExecutable))
        {
            throw new InvalidOperationException("无法确定Python测试入口EXE");
        }
        Directory.CreateDirectory(_baseRoot);
        Root = Path.Combine(_baseRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public PythonTestContext CreateContext(string name, ChildBehavior behavior)
    {
        return new PythonTestContext(Path.Combine(Root, name), _testExecutable, behavior);
    }

    public void Track(OwnedProcessIdentity identity) => _tracked.Add(identity);

    public void MarkStopped(OwnedProcessIdentity identity)
    {
        _tracked.RemoveAll(item => item.RunId == identity.RunId);
    }

    public async Task StopTrackedChildrenAsync()
    {
        foreach (var identity in _tracked.ToArray())
        {
            Process? process;
            try
            {
                process = Process.GetProcessById(identity.ProcessId);
            }
            catch (ArgumentException)
            {
                _tracked.Remove(identity);
                continue;
            }

            using (process)
            {
                VerifyIdentity(process, identity);
                process.CloseMainWindow();
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: false);
                }

                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                _tracked.Remove(identity);
            }
        }
    }

    public void Dispose()
    {
        if (_tracked.Count > 0)
        {
            throw new InvalidOperationException("仍有跟踪的Python测试子进程，拒绝清理测试目录。");
        }

        var root = Path.GetFullPath(Root);
        if (!root.StartsWith(_baseRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("拒绝清理Python测试根之外的路径。");
        }

        var info = new DirectoryInfo(root);
        EnsureNoReparseAncestors(info);
        EnsureNoReparseDescendants(info);
        info.Delete(recursive: true);
    }

    private static void EnsureNoReparseAncestors(DirectoryInfo directory)
    {
        FileSystemInfo? current = directory;
        while (current is not null)
        {
            current.Refresh();
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("拒绝清理祖先包含重解析点的Python测试目录。");
            }

            current = current is DirectoryInfo currentDirectory ? currentDirectory.Parent : null;
        }
    }

    private static void EnsureNoReparseDescendants(DirectoryInfo root)
    {
        var pending = new Queue<DirectoryInfo>();
        pending.Enqueue(root);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
        };
        while (pending.TryDequeue(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
            {
                entry.Refresh();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("拒绝清理包含重解析点子项的Python测试目录。");
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Enqueue(child);
                }
            }
        }
    }

    internal static void VerifyIdentity(Process process, OwnedProcessIdentity identity)
    {
        var observedStart = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        var observedExe = process.MainModule?.FileName;
        if (observedStart.UtcTicks != identity.StartedAtUtc.UtcTicks ||
            string.IsNullOrWhiteSpace(observedExe) ||
            !string.Equals(Path.GetFullPath(observedExe), identity.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("跟踪的Python测试PID身份变化，拒绝停止并保留测试目录。");
        }
    }
}

internal sealed class PythonTestContext : IDisposable
{
    private readonly ChildBehavior _behavior;
    private bool _disposed;

    public PythonTestContext(string root, string testExecutable, ChildBehavior behavior)
    {
        _behavior = behavior;
        Directory.CreateDirectory(root);
        var programRoot = Path.Combine(root, "program");
        var runtimeRoot = Path.Combine(programRoot, "runtime", "python");
        var bootstrapRoot = Path.Combine(programRoot, "scripts", "portable");
        var browserRoot = Path.Combine(programRoot, "runtime", "browser");
        var dataRoot = Path.Combine(root, "data");
        var cacheRoot = Path.Combine(dataRoot, "cache");
        Directory.CreateDirectory(runtimeRoot);
        Directory.CreateDirectory(bootstrapRoot);
        Directory.CreateDirectory(browserRoot);
        Directory.CreateDirectory(cacheRoot);
        File.WriteAllText(Path.Combine(dataRoot, "python-test-behavior.txt"), behavior.ToString(), new UTF8Encoding(false));
        if (behavior is ChildBehavior.ReadyFileControlled)
        {
            File.WriteAllText(Path.Combine(dataRoot, "python-test-ready.txt"), "ready", new UTF8Encoding(false));
        }
        var executablePath = Path.Combine(runtimeRoot, "python.exe");
        File.Copy(testExecutable, executablePath);
        var testBinaryRoot = Path.GetDirectoryName(testExecutable)
            ?? throw new InvalidOperationException("无法确定Python测试二进制目录");
        foreach (var dependencyName in new[]
        {
            "AiGoofish.Launcher.Python.Tests.dll",
            "AiGoofish.Launcher.Python.Tests.deps.json",
            "AiGoofish.Launcher.Python.Tests.runtimeconfig.json",
            "AiGoofish.Launcher.Platform.Windows.dll",
            "AiGoofish.Launcher.Core.dll",
        })
        {
            File.Copy(Path.Combine(testBinaryRoot, dependencyName), Path.Combine(runtimeRoot, dependencyName));
        }
        var runtimeConfigSource = Path.ChangeExtension(testExecutable, ".runtimeconfig.json");
        if (File.Exists(runtimeConfigSource))
        {
            File.Copy(runtimeConfigSource, Path.ChangeExtension(executablePath, ".runtimeconfig.json"));
        }

        File.WriteAllText(Path.Combine(bootstrapRoot, "python-bootstrap.py"), "# test", new UTF8Encoding(false));
        Lease = InstanceDataRootLease.Acquire(dataRoot);
        Secrets = InstanceSecrets.Generate();
        var port = GetUnusedPort();
        Options = new WindowsPythonOptions(
            executablePath,
            Path.Combine(bootstrapRoot, "python-bootstrap.py"),
            programRoot,
            dataRoot,
            cacheRoot,
            browserRoot,
            port,
            "V1.0.4.5",
            PythonServiceMode.Maintenance,
            1,
            1,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(3));
        Endpoints = new PythonDatabaseEndpoints(
            ApplicationDsn: "postgresql://aigoofish_app:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@127.0.0.1:5432/aigoofish",
            ProbeDsn: "postgresql://aigoofish_probe:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb@127.0.0.1:5432/aigoofish");
    }

    public InstanceDataRootLease Lease { get; }

    public InstanceSecrets Secrets { get; }

    public WindowsPythonOptions Options { get; }

    public PythonDatabaseEndpoints Endpoints { get; }

    public string? ControlToken { get; private set; }

    public void SetReady(bool ready)
    {
        if (_behavior is not ChildBehavior.ReadyFileControlled)
        {
            throw new InvalidOperationException("当前测试行为不支持动态Ready状态。");
        }

        File.WriteAllText(
            Path.Combine(Options.DataRoot, "python-test-ready.txt"),
            ready ? "ready" : "not-ready",
            new UTF8Encoding(false));
    }

    public WindowsPythonComponent CreateComponent(
        TimeSpan? startupTimeout = null,
        TimeSpan? stopTimeout = null)
    {
        var options = Options with
        {
            StartupTimeout = startupTimeout ?? Options.StartupTimeout,
            StopTimeout = stopTimeout ?? Options.StopTimeout,
        };
        var component = new WindowsPythonComponent(Lease, Secrets, options, Endpoints);
        return component;
    }

    public async Task StopOwnedComponentAsync(WindowsPythonComponent component, PythonTestWorkspace workspace)
    {
        var identity = component.ProcessSnapshot.Identity;
        if (identity is null)
        {
            return;
        }

        workspace.Track(identity);
        try
        {
            await component.StopAsync(CancellationToken.None);
            workspace.MarkStopped(identity);
        }
        catch (PythonLifecycleException)
        {
            await StopChildDirectAsync(identity);
            workspace.MarkStopped(identity);
        }
    }

    public async Task StopChildDirectAsync(OwnedProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            PythonTestWorkspace.VerifyIdentity(process, identity);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Lease.Dispose();
    }

    private static int GetUnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

internal sealed class FailingPythonCredentialCommitter : IPythonCredentialFileCommitter
{
    public void Commit(string temporaryPath, string targetPath, bool overwrite)
    {
        throw new IOException("injected credential commit failure");
    }
}
