using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AiGoofish.Launcher.Core;
using AiGoofish.Launcher.Platform.Windows;

var testBaseRoot = RequireAbsoluteEnvironmentPath("AIGOOFISH_PG_TEST_ROOT");
var postgresRoot = RequireAbsoluteEnvironmentPath("AIGOOFISH_PG_INSTALLATION_ROOT");
var workspace = new PostgresTestWorkspace(testBaseRoot, postgresRoot);
var tests = new (string Name, Func<PostgresTestWorkspace, Task> Run)[]
{
    ("初始化独立步骤取消边界与被动核验", TestInitializationStepBoundaryAsync),
    ("真实集群初始化启动重连与smart停止", TestLifecycleAndReconnectAsync),
    ("非空无标记PGDATA拒绝自动重建", TestNonEmptyDataDirectoryRejectedAsync),
    ("初始化中断记录阻止自动重试", TestInitializationProgressRejectedAsync),
    ("端口占用不接管其他监听者", TestPortConflictAsync),
    ("临时口令文件创建即限制当前用户", TestPasswordFileAcl),
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
        await workspace.StopTrackedClustersAsync();
        if (failures.Count == 0) workspace.Dispose();
        else Console.Error.WriteLine("POSTGRES_FAILED_FIXTURE_RETAINED");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL PostgreSQL测试收尾: {exception.Message}");
        Console.Error.WriteLine(failures[^1]);
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"共 {failures.Count} 项 PostgreSQL 集成验收失败。");
    return 1;
}

Console.WriteLine($"全部 {tests.Length} 项 PostgreSQL 集成验收通过。");
return 0;

static async Task TestInitializationStepBoundaryAsync(PostgresTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("initialization-step");
    var pgData = Path.Combine(root, "postgres", "cluster");
    Directory.CreateDirectory(pgData);
    using var lease = InstanceDataRootLease.Acquire(root);
    var secrets = new WindowsInstanceSecretsStore().CreateNew(lease, pgData);
    var options = workspace.CreateOptions(pgData, workspace.GetUnusedPort());
    await using var postgres = new WindowsPostgresComponent(lease, secrets, options);
    var preparationCalls = 0;
    var step = new WindowsPostgresInitializationStep(postgres, () => preparationCalls++);
    using var coordinator = new LauncherCoordinator([new(postgres, StartupCancellationIsSafe: false)], false,
        startupSteps: [new(step, postgres.Id, CancellationIsSafe: false)]);
    var cancellationRequested = false;
    coordinator.SnapshotChanged += (_, snapshot) =>
    {
        if (!cancellationRequested && snapshot.StartupSteps.Single().State == StartupStepState.Completed)
        {
            cancellationRequested = true;
            Assert(coordinator.RequestStartupCancellation(), "初始化完成边界可请求取消");
        }
    };
    var result = await coordinator.StartAsync();
    Assert(result.Cancelled && !result.Succeeded && coordinator.Snapshot.IsQuiescent,
        "initdb 后取消应安全停止，不得伪报未知运行服务");
    Assert(postgres.ProcessSnapshot.Identity is null && preparationCalls == 1 &&
        !File.Exists(Path.Combine(root, "config", "postgres-runtime.json")), "取消边界不得启动主进程");
    Assert(await postgres.GetStateAsync(CancellationToken.None) == ComponentRuntimeState.Stopped &&
        await step.IsQuiescentAsync(CancellationToken.None), "仅本进程已知的全新集群可确认没有主进程");
    var markerPath = Path.Combine(pgData, ".aigoofish-cluster.json");
    var marker = File.ReadAllBytes(markerPath);
    await step.VerifyCompletedAsync(CancellationToken.None);
    Assert(preparationCalls == 1 && marker.SequenceEqual(File.ReadAllBytes(markerPath)), "被动核验不得重新分配或改写标记");
    Assert((await coordinator.ShutdownAsync()).Succeeded, "无主进程的完成初始化允许释放");

    await using var reopened = new WindowsPostgresComponent(lease, secrets, options);
    Assert(await reopened.GetStateAsync(CancellationToken.None) == ComponentRuntimeState.Unknown,
        "重开不得继承上个控制器的内存证明或从标记猜测主进程已停止");
    var progressPath = Path.Combine(root, "config", "postgres-initialization.json");
    File.WriteAllText(progressPath, "{invalid", new UTF8Encoding(false));
    Assert(!await reopened.IsInitializationQuiescentAsync(CancellationToken.None), "未知初始化记录不能证明没有辅助进程");
    await AssertThrowsAsync<PostgresLifecycleException>(() => reopened.VerifyInitializationCompletedAsync(CancellationToken.None));
    await AssertThrowsAsync<PostgresLifecycleException>(() => reopened.StartAsync(CancellationToken.None));
    Assert(marker.SequenceEqual(File.ReadAllBytes(markerPath)) && File.ReadAllText(progressPath) == "{invalid",
        "拒绝路径必须保留原文件");
}

static async Task TestLifecycleAndReconnectAsync(PostgresTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("lifecycle");
    var pgData = Path.Combine(root, "postgres", "cluster");
    Directory.CreateDirectory(pgData);
    using var lease = InstanceDataRootLease.Acquire(root);
    var secrets = new WindowsInstanceSecretsStore().CreateNew(lease, pgData);
    var options = workspace.CreateOptions(pgData, workspace.GetUnusedPort());

    var first = new WindowsPostgresComponent(lease, secrets, options);
    await first.EnsureInitializedAsync();
    Assert(File.ReadAllText(Path.Combine(pgData, "PG_VERSION"), Encoding.UTF8).Trim() == "17", "PG_VERSION 必须为17");
    var hba = File.ReadAllText(Path.Combine(pgData, "pg_hba.conf"), Encoding.UTF8);
    Assert(hba.Contains("scram-sha-256", StringComparison.Ordinal), "pg_hba.conf 必须使用SCRAM");
    Assert(!Directory.EnumerateFiles(Path.Combine(root, "config"), ".initdb-password.*.tmp").Any(), "initdb口令临时文件必须删除");

    await first.StartAsync(CancellationToken.None);
    var identity = first.ProcessSnapshot.Identity ?? throw new InvalidOperationException("启动后缺少postgres身份");
    workspace.Track(options, identity);
    Assert(await first.GetStateAsync(CancellationToken.None) == ComponentRuntimeState.Running, "postgres应为Running");
    Assert(await first.CheckTransportAsync() == PostgresTransportState.AcceptingConnections, "pg_isready应报告传输可连接");
    await first.DisposeAsync();

    await using var reconnected = new WindowsPostgresComponent(lease, secrets, options);
    var reconnect = await reconnected.TryReconnectAsync();
    Assert(reconnect.Succeeded && reconnect.Snapshot.State == OwnedProcessState.Running, "应复核postmaster.pid和B3身份后重连postgres.exe");
    await reconnected.StopAsync(CancellationToken.None);
    Assert(await reconnected.GetStateAsync(CancellationToken.None) == ComponentRuntimeState.Stopped, "smart stop后应确认Stopped");
    workspace.MarkStopped(identity);
    Assert(!File.Exists(Path.Combine(pgData, "postmaster.pid")), "正常停止后postmaster.pid应由PostgreSQL自行移除");

    await using var reopened = new WindowsPostgresComponent(lease, secrets, options);
    Assert(
        await reopened.GetStateAsync(CancellationToken.None) == ComponentRuntimeState.Stopped,
        "新component应从持久Stopped记录确认已停止");
}

static async Task TestNonEmptyDataDirectoryRejectedAsync(PostgresTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("nonempty");
    var pgData = Path.Combine(root, "postgres", "cluster");
    Directory.CreateDirectory(pgData);
    var sentinelPath = Path.Combine(pgData, "user-sentinel.txt");
    File.WriteAllText(sentinelPath, "保留", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    using var lease = InstanceDataRootLease.Acquire(root);
    var secrets = InstanceSecrets.Generate();
    await using var component = new WindowsPostgresComponent(lease, secrets, workspace.CreateOptions(pgData, workspace.GetUnusedPort()));

    await AssertThrowsAsync<PostgresLifecycleException>(() => component.EnsureInitializedAsync());
    Assert(File.ReadAllText(sentinelPath, Encoding.UTF8) == "保留", "拒绝初始化不得修改非空PGDATA");
    Assert(!File.Exists(Path.Combine(pgData, ".aigoofish-cluster.json")), "拒绝初始化不得补造marker");
}

static async Task TestInitializationProgressRejectedAsync(PostgresTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("initialization-progress");
    var pgData = Path.Combine(root, "postgres", "cluster");
    var configDirectory = Path.Combine(root, "config");
    Directory.CreateDirectory(pgData);
    Directory.CreateDirectory(configDirectory);
    using var lease = InstanceDataRootLease.Acquire(root);
    var options = workspace.CreateOptions(pgData, workspace.GetUnusedPort());
    var progressPath = Path.Combine(configDirectory, "postgres-initialization.json");
    var progressJson = JsonSerializer.Serialize(
        new
        {
            format_version = 1,
            instance_id = lease.InstanceId,
            data_directory = options.DataDirectory,
            engine_version = options.EngineVersion,
            state = "Starting",
            helper = (object?)null,
            observed_at_utc = DateTimeOffset.UtcNow,
        });
    File.WriteAllText(progressPath, progressJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    var originalProgress = File.ReadAllBytes(progressPath);
    await using var component = new WindowsPostgresComponent(lease, InstanceSecrets.Generate(), options);

    await AssertThrowsAsync<PostgresLifecycleException>(() => component.EnsureInitializedAsync());
    Assert(File.ReadAllBytes(progressPath).SequenceEqual(originalProgress), "拒绝重试不得改写初始化中断记录");
    Assert(!File.Exists(Path.Combine(pgData, ".aigoofish-cluster.json")), "初始化中断记录存在时不得补造marker");
    Assert(!Directory.EnumerateFileSystemEntries(pgData).Any(), "初始化中断记录存在时不得修改PGDATA");
}

static async Task TestPortConflictAsync(PostgresTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("port-conflict");
    var pgData = Path.Combine(root, "postgres", "cluster");
    Directory.CreateDirectory(pgData);
    using var lease = InstanceDataRootLease.Acquire(root);
    var secrets = new WindowsInstanceSecretsStore().CreateNew(lease, pgData);
    var port = workspace.GetUnusedPort();
    var options = workspace.CreateOptions(pgData, port);
    await using var component = new WindowsPostgresComponent(lease, secrets, options);
    await component.EnsureInitializedAsync();

    using var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    await AssertThrowsAsync<PostgresLifecycleException>(() => component.StartAsync(CancellationToken.None));
    using var probeClient = new TcpClient();
    await probeClient.ConnectAsync(IPAddress.Loopback, port);
    Assert(probeClient.Connected, "端口占用失败后不得停止或接管原监听者");
    Assert(component.ProcessSnapshot.State == OwnedProcessState.Exited, "自身postgres应早退而非误报运行");
}

static Task TestPasswordFileAcl(PostgresTestWorkspace workspace)
{
    var root = workspace.CreateCaseRoot("password-acl");
    var path = Path.Combine(root, ".password-test.tmp");
    const string password = "integration-test-secret";
    try
    {
        SecurePasswordFile.WriteNew(path, password);
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        Assert(security.AreAccessRulesProtected, "口令文件DACL必须关闭继承");
        var currentSid = WindowsIdentity.GetCurrent(TokenAccessLevels.Query).User
            ?? throw new InvalidOperationException("无法读取测试用户SID");
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        Assert(rules.Length == 1, "口令文件必须只有一条当前用户显式规则");
        Assert(!rules[0].IsInherited && rules[0].AccessControlType == AccessControlType.Allow, "口令文件规则必须是显式允许");
        Assert(rules[0].IdentityReference == currentSid, "口令文件规则必须只授予当前用户SID");
        Assert(File.ReadAllText(path, Encoding.UTF8).TrimEnd('\r', '\n') == password, "口令文件内容写入异常");
    }
    finally
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    return Task.CompletedTask;
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

    throw new InvalidOperationException($"预期抛出 {typeof(TException).Name}。");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class PostgresTestWorkspace : IDisposable
{
    private readonly string _baseRoot;
    private readonly List<(WindowsPostgresOptions Options, OwnedProcessIdentity Identity)> _tracked = new();

    public PostgresTestWorkspace(string baseRoot, string postgresRoot)
    {
        _baseRoot = Path.GetFullPath(baseRoot);
        PostgresRoot = Path.GetFullPath(postgresRoot);
        Directory.CreateDirectory(_baseRoot);
        Root = Path.Combine(_baseRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PostgresRoot { get; }

    public string CreateCaseRoot(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public int GetUnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public WindowsPostgresOptions CreateOptions(string pgData, int port)
    {
        return new WindowsPostgresOptions(
            PostgresRoot,
            Path.GetFullPath(pgData),
            port,
            "17.11",
            TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(20));
    }

    public void Track(WindowsPostgresOptions options, OwnedProcessIdentity identity)
    {
        _tracked.Add((options, identity));
    }

    public void MarkStopped(OwnedProcessIdentity identity)
    {
        _tracked.RemoveAll(item => item.Identity.RunId == identity.RunId);
    }

    public async Task StopTrackedClustersAsync()
    {
        foreach (var tracked in _tracked.ToArray())
        {
            Process? process;
            try
            {
                process = Process.GetProcessById(tracked.Identity.ProcessId);
            }
            catch (ArgumentException)
            {
                _tracked.Remove(tracked);
                continue;
            }

            using (process)
            {
                var observedStart = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                var observedExe = process.MainModule?.FileName;
                if (observedStart.UtcTicks != tracked.Identity.StartedAtUtc.UtcTicks ||
                    string.IsNullOrWhiteSpace(observedExe) ||
                    !string.Equals(Path.GetFullPath(observedExe), tracked.Identity.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("跟踪的PostgreSQL PID身份变化，拒绝停止并保留测试目录。");
                }

                var pgCtl = Path.Combine(PostgresRoot, "bin", "pg_ctl.exe");
                var stop = await PostgresCommand.RunAsync(
                    pgCtl,
                    Path.GetDirectoryName(pgCtl)!,
                    new[] { "stop", "-D", tracked.Options.DataDirectory, "-m", "smart", "-W" },
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None);
                if (stop.ExitCode != 0)
                {
                    throw new InvalidOperationException("跟踪的PostgreSQL未接受smart stop，未强杀并保留测试目录。");
                }

                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                _tracked.Remove(tracked);
            }
        }
    }

    public void Dispose()
    {
        if (_tracked.Count > 0)
        {
            throw new InvalidOperationException("仍有跟踪的PostgreSQL进程，拒绝清理测试目录。");
        }

        var root = Path.GetFullPath(Root);
        if (!root.StartsWith(_baseRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("拒绝清理PostgreSQL测试根之外的路径。");
        }

        var info = new DirectoryInfo(root);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("拒绝清理重解析点PostgreSQL测试目录。");
        }

        info.Delete(recursive: true);
    }
}
