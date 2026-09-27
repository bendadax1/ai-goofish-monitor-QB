using System.Text;
using AiGoofish.Launcher.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("共享 Runtime 端口策略、排他与取消", LauncherRuntimeTests.RunAsync),
    ("一次性准备步骤、取消、接管和依赖保护", StartupStepTests.RunAsync),
    ("维护事务排他、稳定订阅、替换与关闭", MaintenanceSessionTests.RunAsync),
    ("启动诊断落盘降级隐私容量与并发", StartupDiagnosticTests.RunAsync),
    ("状态序列与逆序停止", TestSuccessfulStateSequenceAsync),
    ("并发重复启动被拒绝", TestRepeatedStartRejectedAsync),
    ("启动失败保留真实剩余组件", TestFailedStartPreservesRemainingComponentsAsync),
    ("初始化安全诊断使用固定恢复指引", TestSafeInitializationFailureGuidanceAsync),
    ("恢复拒绝可记录固定业务数据库准备诊断", TestRecoveryRefusalRecordsSafeDiagnosticAsync),
    ("取消等待安全点并回收", TestCancellationAtSafePointAsync),
    ("停止失败不盲停下游", TestStopFailurePreservesDependenciesAsync),
    ("启动返回但未就绪会失败", TestStartMustReachRunningAsync),
    ("停止返回但未停会保护下游", TestStopMustBeConfirmedAsync),
    ("运行中释放不会破坏收尾", TestDisposeDuringStartIsSafeAsync),
    ("预检中释放不会启动组件", TestDisposeDuringPreflightAsync),
    ("并发关闭保持串行", TestConcurrentShutdownIsSerializedAsync),
    ("观察者异常不破坏收尾", TestObserverFailureIsIsolatedAsync),
    ("已运行组件可经真实状态核验接管", TestAdoptAlreadyRunningAsync),
    ("运行状态采样刷新新鲜度并发现外部组件异常", TestObservationRefreshDetectsExternalFailureAsync),
    ("全组件未运行时拒绝假接管", TestAdoptRejectsStoppedComponentsAsync),
    ("全停恢复显示已停止并可显式重新启动", TestStoppedRecoveryThenExplicitStartAsync),
    ("加密备份先停写后核验且保持数据库至导出完成", TestQuiescedBackupAsync),
    ("Web 排空失败拒绝调用备份", TestQuiescedBackupRejectsFailedDrainAsync),
    ("备份回调失败后仍停止已核验组件", TestQuiescedBackupFailureStopsAsync),
    ("日志缓存严格有界", TestBoundedLogBuffer),
    ("诊断事件容量并发与枚举白名单", TestBoundedDiagnosticEventBuffer),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {test.Name}: {exception.Message}");
        Console.Error.WriteLine(failures[^1]);
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"共 {failures.Count} 项失败。");
    return 1;
}

Console.WriteLine($"全部 {tests.Length} 项 Core 验收通过。");
return 0;

static async Task TestSuccessfulStateSequenceAsync()
{
    var calls = new List<string>();
    var database = new ScriptedComponent("database", "数据库", calls);
    var backend = new ScriptedComponent("backend", "后台服务", calls);
    using var coordinator = CreateCoordinator(database, backend);
    var states = new List<LauncherState>();
    coordinator.SnapshotChanged += (_, snapshot) => states.Add(snapshot.State);

    var start = await coordinator.StartAsync();
    Assert(start.Succeeded, "启动应成功");
    Assert(coordinator.Snapshot.State == LauncherState.Running, "最终应为 Running");

    var stop = await coordinator.StopAsync();
    Assert(stop.Succeeded, "停止应成功");
    Assert(coordinator.Snapshot.State == LauncherState.Stopped, "最终应为 Stopped");
    AssertSequence(calls, "start:database", "start:backend", "stop:backend", "stop:database");
    AssertSubsequence(states, LauncherState.Starting, LauncherState.Running, LauncherState.Stopping, LauncherState.Stopped);
}

static async Task TestStoppedRecoveryThenExplicitStartAsync()
{
    var calls = new List<string>();
    var database = new ScriptedComponent("database", "数据库", calls);
    var backend = new ScriptedComponent("backend", "后台服务", calls);
    using (var previous = CreateCoordinator(database, backend))
    {
        Assert((await previous.StartAsync()).Succeeded, "首次启动应成功");
        Assert((await previous.StopAsync()).Succeeded, "首次停止应成功");
    }

    using var reopened = CreateCoordinator(database, backend);
    var passive = await reopened.RecognizeStoppedExistingAsync("全部已停止，等待显式启动。");
    Assert(!passive.Succeeded && passive.ErrorCode == "EXISTING_INSTANCE_STOPPED" &&
        passive.Snapshot.State == LauncherState.Stopped,
        "被动恢复应确认停止且不得把已停止组件误报为运行");
    Assert(calls.Count == 4, "被动恢复不得创建新进程");
    using (var closing = CreateCoordinator(database, backend))
    {
        var closingResult = await closing.RecognizeStoppedExistingAsync("全部已停止，等待显式启动。");
        Assert(!closingResult.Succeeded && closingResult.Snapshot.State == LauncherState.Stopped,
            "关闭场景应保留被动恢复的已停止状态");
        var shutdown = await closing.ShutdownAsync();
        Assert(shutdown.Succeeded && closing.Snapshot.State == LauncherState.Stopped,
            "恢复确认全停的 Host 应允许安全关闭并释放上下文");
    }

    var unexpectedlyRunning = new ScriptedComponent("unexpected", "意外运行组件", calls);
    unexpectedlyRunning.SimulateExternalState(ComponentRuntimeState.Running);
    using (var unsafeRecovery = CreateCoordinator(unexpectedlyRunning))
    {
        var refused = await unsafeRecovery.RecognizeStoppedExistingAsync("全部已停止，等待显式启动。");
        Assert(!refused.Succeeded && refused.ErrorCode == "EXISTING_INSTANCE_NOT_STOPPED" &&
            refused.Snapshot.State == LauncherState.Failed,
            "二次采样发现运行组件时不得显示已停止或启用手动启动");
    }

    var restarted = await reopened.StartAsync();
    Assert(restarted.Succeeded && restarted.Snapshot.State == LauncherState.Running,
        "同一持久实例的显式启动应从已停止状态成功");
    AssertSequence(calls, "start:database", "start:backend", "stop:backend", "stop:database",
        "start:database", "start:backend");
    Assert((await reopened.StopAsync()).Succeeded, "重启后应能正常停止");
}

static async Task TestQuiescedBackupAsync()
{
    var calls = new List<string>();
    var database = new ScriptedComponent("postgres", "数据库", calls);
    var provision = new ScriptedComponent("database-provision", "数据库准备", calls);
    var web = new ScriptedComponent("python-web", "Web", calls);
    using var coordinator = CreateCoordinator(database, provision, web);
    Assert((await coordinator.StartAsync()).Succeeded, "前置启动应成功");
    calls.Clear();
    var backupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var backupRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var operation = coordinator.RunQuiescedBackupAndStopAsync("postgres", "python-web", async token =>
    {
        calls.Add("backup");
        Assert(await database.GetStateAsync(token) == ComponentRuntimeState.Running, "逻辑导出时数据库应继续运行");
        Assert(await web.GetStateAsync(token) == ComponentRuntimeState.Stopped, "逻辑导出前 Web 必须停止");
        backupEntered.SetResult();
        await backupRelease.Task.WaitAsync(token);
        return "encrypted";
    });
    await backupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await AssertThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync());
    backupRelease.SetResult();
    Assert(await operation == "encrypted", "备份结果应在完成后返回");
    Assert(coordinator.Snapshot.State == LauncherState.Stopped, "备份后所有组件应停机");
    AssertSequence(calls, "stop:python-web", "backup", "stop:database-provision", "stop:postgres");
}

static async Task TestQuiescedBackupRejectsFailedDrainAsync()
{
    var calls = new List<string>();
    var database = new ScriptedComponent("postgres", "数据库", calls);
    var provision = new ScriptedComponent("database-provision", "数据库准备", calls);
    var web = new ScriptedComponent("python-web", "Web", calls) { FailStop = true };
    using var coordinator = CreateCoordinator(database, provision, web);
    Assert((await coordinator.StartAsync()).Succeeded, "前置启动应成功");
    var invoked = false;
    await AssertThrowsAsync<InvalidOperationException>(() => coordinator.RunQuiescedBackupAndStopAsync(
        "postgres", "python-web", _ => { invoked = true; return Task.FromResult("invalid"); }));
    Assert(!invoked, "Web 排空失败不可开始数据库或文件备份");
    Assert(coordinator.Snapshot.State == LauncherState.StopFailed, "状态应反映停止失败");
    Assert(await database.GetStateAsync(CancellationToken.None) == ComponentRuntimeState.Running,
        "Web 状态未知或运行时不可继续停数据库");
}

static async Task TestQuiescedBackupFailureStopsAsync()
{
    var calls = new List<string>();
    var database = new ScriptedComponent("postgres", "数据库", calls);
    var provision = new ScriptedComponent("database-provision", "数据库准备", calls);
    var web = new ScriptedComponent("python-web", "Web", calls);
    using var coordinator = CreateCoordinator(database, provision, web);
    Assert((await coordinator.StartAsync()).Succeeded, "前置启动应成功");
    calls.Clear();
    await AssertThrowsAsync<InvalidOperationException>(() => coordinator.RunQuiescedBackupAndStopAsync<string>(
        "postgres", "python-web", _ => throw new InvalidOperationException("模拟停写证明失败")));
    Assert(coordinator.Snapshot.State == LauncherState.Stopped, "失败收尾不得假报运行或备份成功");
    AssertSequence(calls, "stop:python-web", "stop:database-provision", "stop:postgres");
}

static async Task TestRepeatedStartRejectedAsync()
{
    var database = new ScriptedComponent("database", "数据库")
    {
        StartBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
    };
    var backend = new ScriptedComponent("backend", "后台服务");
    using var coordinator = CreateCoordinator(database, backend);

    var firstStart = coordinator.StartAsync();
    await database.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await AssertThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync());
    database.StartBarrier.SetResult();
    Assert((await firstStart).Succeeded, "首次启动应成功");
    await AssertThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync());
    Assert((await coordinator.StopAsync()).Succeeded, "测试收尾停止应成功");
}

static async Task TestFailedStartPreservesRemainingComponentsAsync()
{
    var calls = new List<string>();
    var database = new ScriptedComponent("database", "数据库", calls);
    var backend = new ScriptedComponent("python-web", "后台服务", calls)
    {
        FailStartAfterRunning = true,
        FailStop = true,
    };
    using var coordinator = CreateCoordinator(database, backend);

    var result = await coordinator.StartAsync();
    Assert(!result.Succeeded && result.ErrorCode == "START_FAILED", "启动应报告 START_FAILED");
    Assert(result.Snapshot.State == LauncherState.Failed, "失败后应保持 Failed");
    AssertState(result.Snapshot, "python-web", ComponentRuntimeState.Running);
    AssertState(result.Snapshot, "database", ComponentRuntimeState.Running);
    Assert(coordinator.DiagnosticEvents.Count == 1 &&
        coordinator.DiagnosticEvents[0].Code == LauncherDiagnosticCode.StartupComponentFailed &&
        coordinator.DiagnosticEvents[0].Component == LauncherDiagnosticComponent.PythonWeb,
        "已知组件启动异常应由编排代码发射固定诊断事件");
    AssertSequence(calls, "start:database", "start:python-web", "stop:python-web");

    backend.FailStop = false;
    Assert((await coordinator.StopAsync()).Succeeded, "解除模拟故障后应能清理剩余组件");
}

static async Task TestSafeInitializationFailureGuidanceAsync()
{
    using var coordinator = new LauncherCoordinator(
        new[] { new LauncherComponentRegistration(new SafeFailureComponent()) },
        isSimulation: true);

    var result = await coordinator.StartAsync();
    Assert(!result.Succeeded, "模拟初始化失败必须保持失败");
    Assert(result.Message.Contains("数据目录已保留", StringComparison.Ordinal), "UI 状态应说明数据被保留");
    Assert(result.Message.Contains("独立的新实例", StringComparison.Ordinal), "UI 状态应给出隔离恢复路径");
    Assert(!result.Message.Contains("C:\\", StringComparison.Ordinal) &&
        !result.Message.Contains("never-display-this", StringComparison.Ordinal), "UI 状态不得暴露路径或原始异常");
    Assert(coordinator.DiagnosticEvents.Single().Code == LauncherDiagnosticCode.PostgresInitInterrupted,
        "诊断事件应区分首次 initdb 中断");
    Assert(coordinator.Logs.All(entry => !entry.Message.Contains("never-display-this", StringComparison.Ordinal)),
        "本机受控日志也不得写入原始异常文本");
}

static async Task TestRecoveryRefusalRecordsSafeDiagnosticAsync()
{
    using var coordinator = CreateCoordinator(new ScriptedComponent("database-provision", "业务数据库准备"));
    var message = LauncherSafeFailureMessages.For(LauncherDiagnosticCode.DatabaseProvisionInterrupted);
    var result = await coordinator.RefuseRecoveryAsync(
        "RECOVERY_VALIDATION_FAILED",
        message,
        diagnosticCode: LauncherDiagnosticCode.DatabaseProvisionInterrupted,
        diagnosticComponent: LauncherDiagnosticComponent.DatabaseProvision);

    Assert(!result.Succeeded && result.Snapshot.State == LauncherState.Failed,
        "被动恢复拒绝应保持原失败语义");
    Assert(coordinator.DiagnosticEvents.Count == 1 &&
        coordinator.DiagnosticEvents[0].Code == LauncherDiagnosticCode.DatabaseProvisionInterrupted &&
        coordinator.DiagnosticEvents[0].Component == LauncherDiagnosticComponent.DatabaseProvision,
        "恢复拒绝应导出固定 provision 中断诊断类别");
    Assert(coordinator.Logs.Single(entry => entry.Level is LauncherLogLevel.Error).Message == message,
        "恢复拒绝只记录固定安全指引");
}

static async Task TestCancellationAtSafePointAsync()
{
    var calls = new List<string>();
    var database = new ScriptedComponent("database", "数据库", calls)
    {
        StartBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
    };
    var backend = new ScriptedComponent("backend", "后台服务", calls);
    using var coordinator = new LauncherCoordinator(
        new[]
        {
            new LauncherComponentRegistration(database, StartupCancellationIsSafe: false),
            new LauncherComponentRegistration(backend, StartupCancellationIsSafe: true),
        },
        isSimulation: true,
        logCapacity: 32);

    var startTask = coordinator.StartAsync();
    await database.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert(!coordinator.Snapshot.SafeToCancel, "不可中断步骤必须标记 SafeToCancel=false");
    Assert(coordinator.RequestStartupCancellation(), "应接受并排队取消请求");
    await Task.Delay(30);
    Assert(!startTask.IsCompleted, "不可中断步骤完成前不应强行取消");
    database.StartBarrier.SetResult();

    var result = await startTask;
    Assert(result.Cancelled && result.ErrorCode == "START_CANCELLED", "应在下一安全点取消");
    Assert(result.Snapshot.State == LauncherState.Stopped, "回收完成后应为 Stopped");
    AssertSequence(calls, "start:database", "stop:database");
}

static async Task TestStopFailurePreservesDependenciesAsync()
{
    var calls = new List<string>();
    var database = new ScriptedComponent("database", "数据库", calls);
    var backend = new ScriptedComponent("backend", "后台服务", calls) { FailStop = true };
    using var coordinator = CreateCoordinator(database, backend);
    Assert((await coordinator.StartAsync()).Succeeded, "启动应成功");

    var failedStop = await coordinator.StopAsync();
    Assert(!failedStop.Succeeded && failedStop.ErrorCode == "STOP_FAILED", "应报告停止失败");
    Assert(failedStop.Snapshot.State == LauncherState.StopFailed, "不得假报 Stopped");
    AssertState(failedStop.Snapshot, "backend", ComponentRuntimeState.Running);
    AssertState(failedStop.Snapshot, "database", ComponentRuntimeState.Running);
    Assert(calls.Count(call => call == "stop:database") == 0, "上游后台停止失败后不得盲停数据库");

    backend.FailStop = false;
    Assert((await coordinator.StopAsync()).Succeeded, "重试后应能正常停止");
}

static async Task TestStartMustReachRunningAsync()
{
    var calls = new List<string>();
    var database = new ScriptedComponent("database", "数据库", calls) { StayStoppedAfterStart = true };
    var backend = new ScriptedComponent("backend", "后台服务", calls);
    using var coordinator = CreateCoordinator(database, backend);

    var result = await coordinator.StartAsync();
    Assert(!result.Succeeded && result.ErrorCode == "START_FAILED", "未达到 Running 必须按启动失败处理");
    Assert(result.Snapshot.State == LauncherState.Failed, "不得假报 Running");
    Assert(calls.All(call => call != "start:backend"), "当前组件未就绪时不得启动后续组件");
}

static async Task TestStopMustBeConfirmedAsync()
{
    var calls = new List<string>();
    var database = new ScriptedComponent("database", "数据库", calls);
    var backend = new ScriptedComponent("backend", "后台服务", calls) { StayRunningAfterStop = true };
    using var coordinator = CreateCoordinator(database, backend);
    Assert((await coordinator.StartAsync()).Succeeded, "启动应成功");

    var result = await coordinator.StopAsync();
    Assert(!result.Succeeded && result.ErrorCode == "STOP_NOT_CONFIRMED", "未确认 Stopped 必须报告失败");
    Assert(result.Snapshot.State == LauncherState.StopFailed, "不得假报 Stopped");
    Assert(calls.All(call => call != "stop:database"), "上游未确认停止时不得停止数据库");

    backend.StayRunningAfterStop = false;
    Assert((await coordinator.StopAsync()).Succeeded, "修复模拟状态后应能重试停止");
}

static async Task TestDisposeDuringStartIsSafeAsync()
{
    var database = new ScriptedComponent("database", "数据库")
    {
        StartBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
    };
    var backend = new ScriptedComponent("backend", "后台服务");
    var coordinator = CreateCoordinator(database, backend);

    var startTask = coordinator.StartAsync();
    await database.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    coordinator.Dispose();
    database.StartBarrier.SetResult();

    var result = await startTask;
    Assert(result.Cancelled, "释放时应请求取消进行中的启动");
    Assert(result.Snapshot.State == LauncherState.Stopped, "安全回收后应为 Stopped");
}

static async Task TestDisposeDuringPreflightAsync()
{
    var calls = new List<string>();
    var database = new ScriptedComponent("database", "数据库", calls)
    {
        StateBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
    };
    var backend = new ScriptedComponent("backend", "后台服务", calls);
    var coordinator = CreateCoordinator(database, backend);

    var startTask = coordinator.StartAsync();
    await database.StateEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    coordinator.Dispose();
    database.StateBarrier.SetResult();

    await AssertThrowsAsync<ObjectDisposedException>(() => startTask);
    Assert(calls.All(call => !call.StartsWith("start:", StringComparison.Ordinal)), "预检释放后不得启动组件");
}

static async Task TestConcurrentShutdownIsSerializedAsync()
{
    var calls = new List<string>();
    var database = new ScriptedComponent("database", "数据库", calls);
    var backend = new ScriptedComponent("backend", "后台服务", calls);
    using var coordinator = CreateCoordinator(database, backend);
    Assert((await coordinator.StartAsync()).Succeeded, "启动应成功");

    backend.StopBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var firstShutdown = coordinator.ShutdownAsync();
    await backend.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var secondShutdown = coordinator.ShutdownAsync();
    await Task.Delay(30);
    Assert(!secondShutdown.IsCompleted, "第二个关闭必须等待第一个关闭释放操作门");
    backend.StopBarrier.SetResult();

    Assert((await firstShutdown).Succeeded, "第一个关闭应成功");
    Assert((await secondShutdown).Succeeded, "第二个关闭应在串行检查后成功");
    Assert(calls.Count(call => call == "stop:backend") == 1, "串行关闭不得重复停止后台");
    Assert(calls.Count(call => call == "stop:database") == 1, "串行关闭不得重复停止数据库");
}

static async Task TestObserverFailureIsIsolatedAsync()
{
    var database = new ScriptedComponent("database", "数据库");
    var backend = new ScriptedComponent("backend", "后台服务");
    using var coordinator = CreateCoordinator(database, backend);
    coordinator.SnapshotChanged += (_, _) => throw new InvalidOperationException("模拟 UI 观察者异常");
    coordinator.LogAdded += (_, _) => throw new InvalidOperationException("模拟日志观察者异常");

    Assert((await coordinator.StartAsync()).Succeeded, "观察者异常不应破坏启动");
    Assert((await coordinator.StopAsync()).Succeeded, "观察者异常不应破坏停止收尾");
    Assert(coordinator.Logs.Any(log => log.Message.Contains("观察者异常", StringComparison.Ordinal)), "应在有界日志中记录观察者异常");
}

static async Task TestAdoptAlreadyRunningAsync()
{
    var calls = new List<string>();
    var database = new ScriptedComponent("database", "数据库", calls);
    var backend = new ScriptedComponent("backend", "后台服务", calls);
    await database.StartAsync(CancellationToken.None);
    await backend.StartAsync(CancellationToken.None);
    calls.Clear();
    using var coordinator = CreateCoordinator(database, backend);
    var states = new List<LauncherState>();
    coordinator.SnapshotChanged += (_, snapshot) => states.Add(snapshot.State);

    var result = await coordinator.AdoptAlreadyRunningAsync();

    Assert(result.Succeeded, "所有组件真实为 Running 时接管应成功");
    Assert(result.Snapshot.State == LauncherState.Running, "接管完成后应为 Running");
    Assert(calls.Count == 0, "接管核验不得调用组件 StartAsync 或 StopAsync");
    AssertSubsequence(states, LauncherState.Recovering, LauncherState.Running);
    Assert((await coordinator.StopAsync()).Succeeded, "接管后应可走正常逆序停止");
}

static async Task TestObservationRefreshDetectsExternalFailureAsync()
{
    var database = new ScriptedComponent("database", "数据库");
    var backend = new ScriptedComponent("backend", "后台服务");
    using var coordinator = CreateCoordinator(database, backend);
    Assert((await coordinator.StartAsync()).Succeeded, "前置启动应成功");
    var previousObservation = coordinator.Snapshot.ObservedAt;
    backend.SimulateExternalState(ComponentRuntimeState.Failed);
    await Task.Delay(5);

    var observed = await coordinator.RefreshObservationAsync();
    Assert(observed.ObservedAt > previousObservation, "采样应刷新观测时间");
    Assert(observed.State is LauncherState.Failed && observed.Phase is "observation:component-failed",
        "运行中发现组件故障必须退出 Running 摘要");
    AssertState(observed, "backend", ComponentRuntimeState.Failed);
    Assert(await backend.GetStateAsync(CancellationToken.None) is ComponentRuntimeState.Failed,
        "被动采样不得重启或停止异常组件");
}

static async Task TestAdoptRejectsStoppedComponentsAsync()
{
    var calls = new List<string>();
    var database = new ScriptedComponent("database", "数据库", calls);
    var backend = new ScriptedComponent("backend", "后台服务", calls);
    using var coordinator = CreateCoordinator(database, backend);

    var result = await coordinator.AdoptAlreadyRunningAsync();

    Assert(!result.Succeeded, "全组件未运行时不得接管");
    Assert(result.ErrorCode == "ADOPTION_COMPONENT_NOT_RUNNING", "拒绝应返回稳定错误码");
    Assert(result.Snapshot.State == LauncherState.Failed, "拒绝接管后不得假报 Running");
    Assert(result.Snapshot.Components.All(component => component.State == ComponentRuntimeState.Stopped),
        "拒绝结果必须保留真实组件状态");
    Assert(calls.Count == 0, "拒绝接管不得启动或停止组件");
}

static async Task TestBoundedLogBuffer()
{
    var buffer = new BoundedLogBuffer(3);
    for (var index = 0; index < 5; index++)
    {
        buffer.Add(new LauncherLogEntry(DateTimeOffset.UtcNow, LauncherLogLevel.Debug, "test", index.ToString()));
    }

    var snapshot = buffer.Snapshot();
    Assert(snapshot.Count == 3, "缓存不得超过容量");
    AssertSequence(snapshot.Select(entry => entry.Message).ToList(), "2", "3", "4");

    var multiByte = new string('界', BoundedLogBuffer.MaxMessageUtf8Bytes);
    buffer.Add(new LauncherLogEntry(DateTimeOffset.UtcNow, LauncherLogLevel.Error, "test", multiByte));
    var bounded = buffer.Snapshot()[^1].Message;
    Assert(Encoding.UTF8.GetByteCount(bounded) <= BoundedLogBuffer.MaxMessageUtf8Bytes,
        "多字节消息必须在 UTF-8 字节上限内");
    Assert(bounded.EndsWith("…", StringComparison.Ordinal), "截断消息应带标记");
    Assert(!HasUnpairedSurrogate(bounded), "截断不得拆开代理项");

    var emoji = "prefix" + char.ConvertFromUtf32(0x1F680) + new string('x', BoundedLogBuffer.MaxMessageUtf8Bytes);
    buffer.Add(new LauncherLogEntry(DateTimeOffset.UtcNow, LauncherLogLevel.Error, "test", emoji));
    bounded = buffer.Snapshot()[^1].Message;
    Assert(!HasUnpairedSurrogate(bounded), "补充平面字符截断后不得留下半个代理项");
    Assert(Encoding.UTF8.GetByteCount(bounded) <= BoundedLogBuffer.MaxMessageUtf8Bytes,
        "截断后的补充平面文本必须满足字节上限");

    var malformed = new string('a', BoundedLogBuffer.MaxMessageUtf8Bytes - 1) + '\uD800' + new string('b', 8);
    buffer.Add(new LauncherLogEntry(DateTimeOffset.UtcNow, LauncherLogLevel.Error, "test", malformed));
    bounded = buffer.Snapshot()[^1].Message;
    Assert(!HasUnpairedSurrogate(bounded), "无效孤立代理项不得泄漏到缓存");

    var hugeException = new string('E', 4 * 1024 * 1024);
    buffer.Add(new LauncherLogEntry(DateTimeOffset.UtcNow, LauncherLogLevel.Error, "test", hugeException));
    bounded = buffer.Snapshot()[^1].Message;
    Assert(Encoding.UTF8.GetByteCount(bounded) == BoundedLogBuffer.MaxMessageUtf8Bytes,
        "超大异常文本应被截为有界 UTF-8 长度");
    Assert(buffer.Snapshot().All(entry => Encoding.UTF8.GetByteCount(entry.Message) <= BoundedLogBuffer.MaxMessageUtf8Bytes),
        "缓冲内所有消息都必须受字节上限约束");

    var concurrent = new BoundedLogBuffer(16);
    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
    {
        for (var index = 0; index < 500; index++)
        {
            concurrent.Add(new LauncherLogEntry(DateTimeOffset.UtcNow, LauncherLogLevel.Warning,
                "concurrent", new string('中', 5000)));
        }
    })));
    var concurrentSnapshot = concurrent.Snapshot();
    Assert(concurrentSnapshot.Count == concurrent.Capacity, "并发写入后条数仍不得超过容量");
    Assert(concurrentSnapshot.All(entry => Encoding.UTF8.GetByteCount(entry.Message) <= BoundedLogBuffer.MaxMessageUtf8Bytes),
        "并发写入后每条消息仍需受字节上限约束");
}

static bool HasUnpairedSurrogate(string value)
{
    for (var index = 0; index < value.Length; index++)
    {
        if (char.IsHighSurrogate(value[index]))
        {
            if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
            {
                return true;
            }

            index++;
        }
        else if (char.IsLowSurrogate(value[index]))
        {
            return true;
        }
    }

    return false;
}

static async Task TestBoundedDiagnosticEventBuffer()
{
    var buffer = new BoundedDiagnosticEventBuffer(4);
    var writers = Enumerable.Range(0, 8)
        .Select(writer => Task.Run(() =>
        {
            for (var index = 0; index < 500; index++)
            {
                buffer.Add(new LauncherDiagnosticEvent(
                    DateTimeOffset.Now,
                    LauncherDiagnosticCode.ComponentStateReadFailed,
                    LauncherDiagnosticComponent.Postgres,
                    LauncherDiagnosticSeverity.Error));
            }
        }))
        .ToArray();
    await Task.WhenAll(writers);

    var snapshot = buffer.Snapshot();
    Assert(snapshot.Count == 4 && snapshot.All(item => item.OccurredAtUtc.Offset == TimeSpan.Zero),
        "并发事件写入必须安全且时间统一为 UTC，容量须严格受限");
    AssertThrows<ArgumentOutOfRangeException>(() => new BoundedDiagnosticEventBuffer(65));
    AssertThrows<ArgumentOutOfRangeException>(() => new LauncherDiagnosticEvent(
        DateTimeOffset.UtcNow,
        (LauncherDiagnosticCode)int.MaxValue,
        LauncherDiagnosticComponent.Launcher,
        LauncherDiagnosticSeverity.Error));
}

static LauncherCoordinator CreateCoordinator(params ScriptedComponent[] components)
{
    return new LauncherCoordinator(
        components.Select(component => new LauncherComponentRegistration(component)),
        isSimulation: true,
        logCapacity: 64);
}

static void AssertState(LauncherSnapshot snapshot, string componentId, ComponentRuntimeState expected)
{
    var actual = snapshot.Components.Single(component => component.Id == componentId).State;
    Assert(actual == expected, $"组件 {componentId} 预期 {expected}，实际 {actual}");
}

static void AssertSubsequence(IReadOnlyList<LauncherState> actual, params LauncherState[] expected)
{
    var expectedIndex = 0;
    foreach (var state in actual)
    {
        if (state == expected[expectedIndex])
        {
            expectedIndex++;
            if (expectedIndex == expected.Length)
            {
                return;
            }
        }
    }

    throw new InvalidOperationException($"未找到状态子序列：{string.Join(" -> ", expected)}；实际：{string.Join(" -> ", actual)}");
}

static void AssertSequence<T>(IReadOnlyList<T> actual, params T[] expected)
{
    if (!actual.SequenceEqual(expected))
    {
        throw new InvalidOperationException($"序列不一致。预期：{string.Join(", ", expected)}；实际：{string.Join(", ", actual)}");
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

    throw new InvalidOperationException($"预期抛出 {typeof(TException).Name}。");
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

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class SafeFailureComponent : ILauncherComponent
{
    public string Id => "postgres";
    public string DisplayName => "PostgreSQL";
    public ValueTask<ComponentRuntimeState> GetStateAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(ComponentRuntimeState.Stopped);
    public Task StartAsync(CancellationToken cancellationToken) =>
        throw new SafeInitializationFailureException();
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class SafeInitializationFailureException : InvalidOperationException, ILauncherSafeFailure
{
    public SafeInitializationFailureException() : base("never-display-this C:\\private\\secret") { }
    public LauncherDiagnosticCode? SafeDiagnosticCode => LauncherDiagnosticCode.PostgresInitInterrupted;
}

internal sealed class ScriptedComponent : ILauncherComponent
{
    private readonly List<string>? _calls;
    private readonly object _sync = new();
    private ComponentRuntimeState _state = ComponentRuntimeState.Stopped;

    public ScriptedComponent(string id, string displayName, List<string>? calls = null)
    {
        Id = id;
        DisplayName = displayName;
        _calls = calls;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public bool FailStartAfterRunning { get; set; }

    public bool FailStop { get; set; }

    public bool StayStoppedAfterStart { get; set; }

    public bool StayRunningAfterStop { get; set; }

    public TaskCompletionSource? StartBarrier { get; set; }

    public TaskCompletionSource? StateBarrier { get; set; }

    public TaskCompletionSource? StopBarrier { get; set; }

    public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource StateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask<ComponentRuntimeState> GetStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StateEntered.TrySetResult();
        if (StateBarrier is not null)
        {
            await StateBarrier.Task.WaitAsync(cancellationToken);
        }

        lock (_sync)
        {
            return _state;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _calls?.Add($"start:{Id}");
        SetState(ComponentRuntimeState.Starting);
        StartEntered.TrySetResult();
        if (StartBarrier is not null)
        {
            await StartBarrier.Task.WaitAsync(cancellationToken);
        }

        SetState(StayStoppedAfterStart ? ComponentRuntimeState.Stopped : ComponentRuntimeState.Running);
        if (FailStartAfterRunning)
        {
            throw new InvalidOperationException("模拟启动返回错误，但组件实际已运行");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls?.Add($"stop:{Id}");
        StopEntered.TrySetResult();
        if (StopBarrier is not null)
        {
            await StopBarrier.Task.WaitAsync(cancellationToken);
        }

        if (FailStop)
        {
            SetState(ComponentRuntimeState.Running);
            throw new InvalidOperationException("模拟停止失败");
        }

        SetState(StayRunningAfterStop ? ComponentRuntimeState.Running : ComponentRuntimeState.Stopped);
    }

    private void SetState(ComponentRuntimeState state)
    {
        lock (_sync)
        {
            _state = state;
        }
    }

    public void SimulateExternalState(ComponentRuntimeState state) => SetState(state);
}
