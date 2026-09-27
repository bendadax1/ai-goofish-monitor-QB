using AiGoofish.Launcher.Platform.Windows;
using AiGoofish.Launcher.App;
using Avalonia.Controls;
using System.Reflection;

internal static class RestoreUiAcceptance
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Environment.CurrentDirectory, ".tmp", "tests", "portable-restore-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var passphraseInput = RestoreDialogControls.CreatePassphraseInput();
            Assert(passphraseInput.PasswordChar == '●' && passphraseInput.Text is null,
                "恢复口令输入必须掩码且初始不含明文");
            Assert(!RestoreDialogControls.IsExactConfirmation("确认", PortableRestoreSession.RequiredConfirmationText),
                "不完整确认文字不得启用 UI 确认");
            Assert(RestoreDialogControls.IsExactConfirmation(
                PortableRestoreSession.RequiredConfirmationText,
                PortableRestoreSession.RequiredConfirmationText),
                "只有完整确认文字才满足 UI 确认条件");

            Assert(MainWindowViewModel.IsNewInstallRestoreAllowed(false, true, false, false),
                "新安装无 active instance 且无 Host 时必须开放受保护的备份恢复入口");
            Assert(!MainWindowViewModel.IsNewInstallRestoreAllowed(true, true, false, false) &&
                !MainWindowViewModel.IsNewInstallRestoreAllowed(false, false, false, false) &&
                !MainWindowViewModel.IsNewInstallRestoreAllowed(false, true, true, false) &&
                !MainWindowViewModel.IsNewInstallRestoreAllowed(false, true, false, true),
                "模拟模式、预检失败、Host 存在或已有数据时必须关闭新装恢复旁路");

            var brokenBundleRoot = Path.Combine(root, "broken-bundle");
            var activePointerDirectory = Path.Combine(brokenBundleRoot, "data", "launcher");
            Directory.CreateDirectory(activePointerDirectory);
            await File.WriteAllTextAsync(Path.Combine(activePointerDirectory, "active-instance.json"), "{");
            try
            {
                new PortableInstanceCatalog(brokenBundleRoot).ResolveActiveInstance();
                throw new InvalidOperationException("Expected malformed active pointer rejection.");
            }
            catch (PortableInstanceCatalogException)
            {
            }
            Assert(!MainWindowViewModel.IsNewInstallRestoreAllowed(false, true, false, true),
                "active pointer 预检失败时必须关闭新装恢复入口");

            var readinessRoot = Path.Combine(root, "view-model-preflight");
            Directory.CreateDirectory(readinessRoot);
            var readinessBundle = new PortableBundleDescriptor(
                readinessRoot,
                "synthetic-release",
                "0.0.0-test",
                1,
                1,
                readinessRoot,
                Path.Combine(readinessRoot, "runtime", "python.exe"),
                Path.Combine(readinessRoot, "browser"),
                Path.Combine(readinessRoot, "postgres"),
                Path.Combine(readinessRoot, "launcher", "AiGoofish.Launcher.App.exe"));
            await using (var readinessViewModel = new MainWindowViewModel(false, readinessRoot, readinessBundle))
            {
                var notified = false;
                readinessViewModel.PropertyChanged += (_, eventArgs) =>
                    notified |= eventArgs.PropertyName == nameof(MainWindowViewModel.CanRestoreBusinessBackup);
                Assert(!readinessViewModel.CanRestoreBusinessBackup,
                    "新装恢复入口在发行包/数据目录预检结果写入前必须保持禁用");

                typeof(MainWindowViewModel)
                    .GetField("_bundle", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(readinessViewModel, readinessBundle);
                typeof(MainWindowViewModel)
                    .GetMethod("MarkNewInstallReady", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(readinessViewModel, null);

                Assert(notified && readinessViewModel.CanRestoreBusinessBackup,
                    "新装预检确认无活动实例后必须通知绑定并启用恢复入口");
            }

            var bundleRoot = Path.Combine(root, "bundle");
            Directory.CreateDirectory(bundleRoot);
            var archive = Path.Combine(root, "synthetic-backup.gfbk");
            await File.WriteAllTextAsync(archive, "synthetic fixture only");
            var catalog = new PortableInstanceCatalog(bundleRoot);
            Assert(!Directory.Exists(Path.Combine(bundleRoot, "data", "instances")),
                "新安装 fixture 应从 data/instances 缺失状态开始");

            var trustExecutor = new FakeRestoreExecutor();
            await using (var trustSession = new PortableRestoreSession(catalog, trustExecutor))
            {
                await AssertThrowsAsync<PortableRestoreException>(() => trustSession.RestoreAndPreviewAsync(
                    archive,
                    "synthetic passphrase",
                    "untrusted source"));
                Assert(trustExecutor.RestoreCallCount == 0, "未明确确认来源信任时不得启动恢复执行器");
                Assert(!File.Exists(catalog.ActivePointerPath), "来源信任失败不得切换活动指针");
            }

            var cancelExecutor = new FakeRestoreExecutor();
            var cancelSession = new PortableRestoreSession(catalog, cancelExecutor);
            var preview = await cancelSession.RestoreAndPreviewAsync(
                archive,
                "synthetic passphrase",
                PortableRestoreSession.RequiredBackupTrustText);
            Assert(preview.State is PortableRestoreState.AwaitingUserConfirmation && preview.Preview is not null,
                "审计通过后必须停在待用户确认状态");
            Assert(Directory.Exists(Path.Combine(bundleRoot, "data", "instances")),
                "恢复目录目录树不存在时 catalog 应安全创建隔离 instances 容器");
            Assert(!File.Exists(catalog.ActivePointerPath), "仅生成差异预览不得切换活动指针");
            var targetRoot = preview.DiagnosticTargetRoot;
            await AssertThrowsAsync<PortableRestoreException>(() => cancelSession.ConfirmActivationAsync("确认"));
            Assert(cancelSession.Snapshot.State is PortableRestoreState.AwaitingUserConfirmation,
                "不完整确认文字不得推进状态机");
            Assert(!File.Exists(catalog.ActivePointerPath), "不完整确认文字不得切换活动指针");
            var cancelled = await cancelSession.CancelAndRetainTargetAsync();
            Assert(cancelled.State is PortableRestoreState.CancelledTargetRetained, "取消须保留隔离目标供诊断");
            Assert(targetRoot is not null && Directory.Exists(targetRoot), "取消不得删除隔离目标");
            Assert(!File.Exists(catalog.ActivePointerPath), "明确取消不得切换活动指针");
            await cancelSession.DisposeAsync();
            Assert(cancelExecutor.EnsureStoppedCallCount >= 2, "取消/释放前须再次核实恢复目标已停止");

            var closeExecutor = new FakeRestoreExecutor();
            var closeSession = new PortableRestoreSession(catalog, closeExecutor);
            await closeSession.RestoreAndPreviewAsync(
                archive,
                "synthetic passphrase",
                PortableRestoreSession.RequiredBackupTrustText);
            var closeTargetRoot = closeSession.Snapshot.DiagnosticTargetRoot;
            await closeSession.DisposeAsync();
            Assert(closeTargetRoot is not null && Directory.Exists(closeTargetRoot), "预览阶段关闭会话须保留诊断目标");
            Assert(!File.Exists(catalog.ActivePointerPath), "关闭未确认的恢复会话不得切换活动指针");
            Assert(closeExecutor.EnsureStoppedCallCount >= 1, "关闭会话须确认候选已停止后释放 lease");

            var failedExecutor = new FakeRestoreExecutor { CancelDuringRestore = true };
            var failedSession = new PortableRestoreSession(catalog, failedExecutor);
            await AssertThrowsAsync<OperationCanceledException>(() => failedSession.RestoreAndPreviewAsync(
                archive,
                "synthetic passphrase",
                PortableRestoreSession.RequiredBackupTrustText));
            Assert(failedSession.Snapshot.State is PortableRestoreState.FailedTargetRetained,
                "执行取消须保留失败隔离目标而不触碰旧实例");
            Assert(!File.Exists(catalog.ActivePointerPath), "恢复执行取消不得切换活动指针");
            await failedSession.CancelAndRetainTargetAsync();
            await failedSession.DisposeAsync();

            var errorExecutor = new FakeRestoreExecutor { FailDuringRestore = true };
            var errorSession = new PortableRestoreSession(catalog, errorExecutor);
            await AssertThrowsAsync<PortableRestoreException>(() => errorSession.RestoreAndPreviewAsync(
                archive,
                "synthetic passphrase",
                PortableRestoreSession.RequiredBackupTrustText));
            Assert(errorSession.Snapshot.State is PortableRestoreState.FailedTargetRetained,
                "恢复异常须保留失败目标供诊断");
            Assert(!errorSession.Snapshot.Message.Contains("sensitive fixture path", StringComparison.Ordinal),
                "恢复状态消息不得回显异常中的敏感路径");
            Assert(!File.Exists(catalog.ActivePointerPath), "恢复异常不得切换活动指针");
            await errorSession.CancelAndRetainTargetAsync();
            await errorSession.DisposeAsync();

            var activationExecutor = new FakeRestoreExecutor();
            await using (var activationSession = new PortableRestoreSession(catalog, activationExecutor))
            {
                var ready = await activationSession.RestoreAndPreviewAsync(
                    archive,
                    "synthetic passphrase",
                    PortableRestoreSession.RequiredBackupTrustText);
                Assert(!File.Exists(catalog.ActivePointerPath), "确认前活动指针必须仍指向旧实例");
                var activated = await activationSession.ConfirmActivationAsync(PortableRestoreSession.RequiredConfirmationText);
                Assert(activated.State is PortableRestoreState.Activated, "完整确认后应完成指针切换");
                var active = catalog.ResolveActiveInstance();
                Assert(active.InstanceId == ready.Preview!.TargetInstanceId, "原子切换后 active pointer 必须指向刚审计的恢复目标");
                Assert(Directory.Exists(active.DataRoot), "切换不得删除旧数据容器或恢复目标");
            }

            Console.WriteLine("RESTORE_UI_ACCEPTANCE_PASS");
            Console.WriteLine("覆盖：来源信任门、预览无切换、不完整确认、明确取消保留目标、关闭会话安全释放、执行取消、完整确认后切换");
            Console.WriteLine("BOUNDARY=合成 fixture + fake restore executor；不访问真实备份、PostgreSQL、DPAPI 或用户数据。");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"RESTORE_UI_ACCEPTANCE_FAIL: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
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

    private sealed class FakeRestoreExecutor : IPortableRestoreExecutor
    {
        public bool CancelDuringRestore { get; init; }
        public bool FailDuringRestore { get; init; }
        public int RestoreCallCount { get; private set; }
        public int EnsureStoppedCallCount { get; private set; }

        public Task<PortableRestorePreview> RestoreAndAuditAsync(
            PortableRestoreTarget target,
            string archiveFile,
            string passphrase,
            CancellationToken cancellationToken)
        {
            RestoreCallCount++;
            Assert(Path.IsPathFullyQualified(archiveFile), "恢复执行器接收的备份路径须为绝对路径");
            Assert(!string.IsNullOrEmpty(passphrase), "合成 fixture 须收到口令");
            if (CancelDuringRestore) throw new OperationCanceledException(cancellationToken);
            if (FailDuringRestore) throw new IOException("sensitive fixture path and passphrase");
            return Task.FromResult(new PortableRestorePreview(
                target.InstanceId,
                target.RelativeRoot,
                Guid.NewGuid().ToString("D"),
                new string('a', 64),
                null,
                new Dictionary<string, long> { ["users"] = 1, ["tasks"] = 2 },
                2,
                null,
                1,
                true,
                "仅含合成 fixture 数据。"));
        }

        public Task EnsureCandidateStoppedAsync(PortableRestoreTarget target, CancellationToken cancellationToken)
        {
            Assert(target.IsLeaseHeld, "恢复停机核验必须持有实例锁，不能重复使用已释放目标");
            EnsureStoppedCallCount++;
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable?> AcquirePreviousInstanceSwitchGuardAsync(
            PortableRestoreTarget target,
            CancellationToken cancellationToken) => Task.FromResult<IAsyncDisposable?>(null);
    }
}
