using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AiGoofish.Launcher.Platform.Windows;

/// <summary>
/// Restores an authenticated business archive into a catalog-created PG17
/// candidate, re-protects business keys for this Windows user, runs the
/// maintenance-only readiness contract, then smart-stops the candidate.
/// It never provisions over an existing instance or starts Normal Web.
/// </summary>
public sealed class WindowsPortableRestoreExecutor : IPortableRestoreExecutor, IAsyncDisposable
{
    private static readonly TimeSpan PostgresInitializationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PostgresStartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PostgresStopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaintenanceStartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaintenanceRequestTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MaintenanceStopTimeout = TimeSpan.FromSeconds(30);

    private readonly PortableBundleDescriptor _bundle;
    private readonly ConcurrentDictionary<Guid, CandidateComponents> _candidates = new();
    private bool _disposed;

    public WindowsPortableRestoreExecutor(PortableBundleDescriptor bundle)
    {
        _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
    }

    public async Task<PortableRestorePreview> RestoreAndAuditAsync(
        PortableRestoreTarget target,
        string archiveFile,
        string passphrase,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);
        target.Lease.EnsureHeld();
        if (_candidates.ContainsKey(target.InstanceId))
        {
            throw new PortableRestoreException("RESTORE_TARGET_ALREADY_ATTEMPTED", "此目标已有恢复尝试；禁止原位重试或覆盖。");
        }

        var dataRoot = target.DataRoot;
        PostgresPathGuard.EnsureNewInstancePathLength(dataRoot);
        var cacheRoot = Path.Combine(dataRoot, "cache");
        var temporaryRoot = Path.Combine(cacheRoot, "temp");
        var logRoot = Path.Combine(dataRoot, "logs");
        var pgData = Path.Combine(dataRoot, "postgres", "cluster");
        foreach (var directory in new[] { cacheRoot, temporaryRoot, logRoot })
        {
            PortableInstanceCatalog.EnsureSafePath(dataRoot, directory, allowMissingLeaf: true);
            Directory.CreateDirectory(directory);
            PortableInstanceCatalog.EnsureSafePath(dataRoot, directory, allowMissingLeaf: false);
        }

        var bootstrapSecrets = InstanceSecrets.Generate();
        var postgres = new WindowsPostgresComponent(
            target.Lease,
            bootstrapSecrets,
            new WindowsPostgresOptions(
                _bundle.PostgresRoot,
                pgData,
                target.PostgresPort,
                "17.11",
                PostgresInitializationTimeout,
                PostgresStartupTimeout,
                PostgresStopTimeout));
        var candidate = new CandidateComponents(postgres);
        candidate.Target = target;
        if (!_candidates.TryAdd(target.InstanceId, candidate))
        {
            await postgres.DisposeAsync().ConfigureAwait(false);
            throw new PortableRestoreException("RESTORE_TARGET_ALREADY_ATTEMPTED", "此目标已有恢复尝试；禁止原位重试或覆盖。");
        }

        try
        {
            await postgres.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await postgres.StartAsync(cancellationToken).ConfigureAwait(false);
            var identity = postgres.ProcessSnapshot.Identity
                ?? throw new PortableRestoreException("RESTORE_POSTGRES_IDENTITY_MISSING", "新 PostgreSQL 目标缺少可核验进程身份。");

            async Task VerifyMaintenance(CancellationToken token)
            {
                target.Lease.EnsureHeld();
                if (postgres.ProcessSnapshot.Identity != identity ||
                    await postgres.GetStateAsync(token).ConfigureAwait(false) is not AiGoofish.Launcher.Core.ComponentRuntimeState.Running ||
                    await postgres.CheckTransportAsync(token).ConfigureAwait(false) is not PostgresTransportState.AcceptingConnections)
                {
                    throw new PortableRestoreException("RESTORE_TARGET_OWNERSHIP_LOST", "恢复期间新 PostgreSQL 目标归属或连接状态未通过复核。");
                }
            }

            var instancesRoot = Path.GetDirectoryName(dataRoot)
                ?? throw new PortableRestoreException("RESTORE_PATH_INVALID", "恢复目标父目录无效。");
            var restored = await PortableBusinessRestoreRunner.RunAsync(
                _bundle,
                target,
                archiveFile,
                passphrase,
                bootstrapSecrets.PostgresBootstrapAdminPassword,
                VerifyMaintenance,
                cancellationToken).ConfigureAwait(false);

            var importedSecrets = new WindowsInstanceSecretsStore().ImportRestoredHandoff(
                target.Lease,
                pgData,
                instancesRoot,
                restored.HandoffDirectory,
                bootstrapSecrets.PostgresBootstrapAdminPassword);

            var bootstrap = Path.Combine(_bundle.ProgramRoot, "scripts", "portable", "python-bootstrap.py");
            var python = new WindowsPythonComponent(
                target.Lease,
                importedSecrets,
                new WindowsPythonOptions(
                    _bundle.PythonExecutable,
                    bootstrap,
                    _bundle.ProgramRoot,
                    dataRoot,
                    cacheRoot,
                    _bundle.BrowserRoot,
                    target.WebPort,
                    _bundle.AppVersion,
                    PythonServiceMode.Maintenance,
                    _bundle.SchemaMinimum,
                    _bundle.SchemaMaximum,
                    MaintenanceStartupTimeout,
                    MaintenanceRequestTimeout,
                    MaintenanceStopTimeout),
                new PythonDatabaseEndpoints(
                    MakeDsn("aigoofish_app", importedSecrets.ApplicationDatabasePassword, target.PostgresPort),
                    MakeDsn("aigoofish_probe", importedSecrets.ProbeDatabasePassword, target.PostgresPort)));
            candidate.Python = python;
            await python.StartAsync(cancellationToken).ConfigureAwait(false);
            var readiness = python.LastReadiness;
            if (readiness is null || readiness.State is not PythonReadinessState.Ready || readiness.SchemaVersion != 1)
            {
                throw new PortableRestoreException("RESTORE_MAINTENANCE_AUDIT_FAILED", "恢复后维护 Web 未确认同一 PG17 实例和 schema 1。");
            }

            await StopCandidateAsync(target, candidate, cancellationToken).ConfigureAwait(false);
            WindowsPortableProvisionStep.RecordRestoredCompletion(target.Lease, pgData, 1);
            return new PortableRestorePreview(
                restored.TargetInstanceId,
                target.RelativeRoot,
                restored.SourceInstanceId,
                restored.ArchiveSha256,
                BackupCreatedAtUtc: null,
                restored.TableCounts,
                restored.RestoredFileCount,
                RestoredFileBytes: null,
                restored.RevokedSessionCount,
                MaintenanceAuditPassed: true,
                DataLossNotice: "恢复结果与备份时点一致。备份之后新增、修改或删除的数据不在本次恢复内，也不会与旧实例自动合并。");
        }
        catch (Exception exception)
        {
            try
            {
                await StopCandidateAsync(target, candidate, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Keep the component objects and target lease in the executor.
                // An unknown process state must block every later adoption.
            }

            if (exception is PortableRestoreException)
            {
                throw;
            }

            throw new PortableRestoreException(
                "RESTORE_EXECUTION_FAILED",
                "隔离恢复或维护核对未完成；目标实例保留供诊断，活动实例未改变。",
                exception);
        }
    }

    public async Task EnsureCandidateStoppedAsync(PortableRestoreTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_candidates.TryGetValue(target.InstanceId, out var candidate))
        {
            target.Lease.EnsureHeld();
            var pgData = Path.Combine(target.DataRoot, "postgres", "cluster");
            var state = new WindowsPostgresComponent(
                target.Lease,
                InstanceSecrets.Generate(),
                new WindowsPostgresOptions(
                    _bundle.PostgresRoot,
                    pgData,
                    target.PostgresPort,
                    "17.11",
                    PostgresInitializationTimeout,
                    PostgresStartupTimeout,
                    PostgresStopTimeout));
            var actual = await state.GetStateAsync(cancellationToken).ConfigureAwait(false);
            await state.DisposeAsync().ConfigureAwait(false);
            if (actual is not AiGoofish.Launcher.Core.ComponentRuntimeState.Stopped)
            {
                throw new PortableRestoreException("RESTORE_TARGET_STOP_UNPROVEN", "未能证明恢复目标已停止；保留 lease 并拒绝释放控制权。");
            }

            return;
        }

        await StopCandidateAsync(target, candidate, cancellationToken).ConfigureAwait(false);
        if (_candidates.TryRemove(target.InstanceId, out var removed))
        {
            await removed.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<IAsyncDisposable?> AcquirePreviousInstanceSwitchGuardAsync(
        PortableRestoreTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var catalog = new PortableInstanceCatalog(_bundle.BundleRoot);
        var active = catalog.ReadActiveSelection();
        if (string.Equals(Path.GetFullPath(active.DataRoot), Path.GetFullPath(target.DataRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new PortableRestoreException("RESTORE_SOURCE_TARGET_COLLISION", "活动源与恢复目标相同；拒绝切换。");
        }

        if (!TryGetAttributes(active.DataRoot, out var activeRootAttributes))
        {
            return null;
        }
        if ((activeRootAttributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
        {
            throw new PortableRestoreException("RESTORE_SOURCE_PATH_INVALID", "旧活动数据根不是普通目录；拒绝切换。");
        }

        var lockPath = Path.Combine(active.DataRoot, InstanceDataRootLease.LockFileName);
        if (!TryGetAttributes(lockPath, out var lockAttributes))
        {
            if (HasPortableRuntimeEvidence(active.DataRoot))
            {
                throw new PortableRestoreException(
                    "RESTORE_SOURCE_OWNERSHIP_UNKNOWN",
                    "旧实例存在运行身份或 PostgreSQL 数据，但缺少实例 lease；拒绝切换。");
            }

            return null;
        }
        if ((lockAttributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new PortableRestoreException("RESTORE_SOURCE_LOCK_INVALID", "旧实例 lease 路径不是普通文件；拒绝切换。");
        }

        InstanceDataRootLease sourceLease;
        try
        {
            sourceLease = InstanceDataRootLease.Acquire(active.DataRoot);
        }
        catch (InstanceAlreadyLockedException exception)
        {
            throw new PortableRestoreException(
                "RESTORE_SOURCE_LEASE_BUSY",
                "旧实例 Launcher 仍持有 lease；请先正常停止旧实例后再确认切换。",
                exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new PortableRestoreException("RESTORE_SOURCE_LEASE_UNKNOWN", "无法核验旧实例 lease；拒绝切换。", exception);
        }
        try
        {
            if (active.InstanceId is { } expectedId && sourceLease.InstanceId != expectedId)
            {
                throw new PortableRestoreException("RESTORE_SOURCE_IDENTITY_MISMATCH", "旧活动实例 lease 与活动指针不匹配。");
            }

            if (HasPortableRuntimeEvidence(active.DataRoot))
            {
            var secretsPath = Path.Combine(active.DataRoot, WindowsInstanceSecretsStore.RelativeSecretsPath.Replace('/', Path.DirectorySeparatorChar));
            InstanceSecrets secrets;
            if (File.Exists(secretsPath))
            {
                secrets = new WindowsInstanceSecretsStore().Load(sourceLease);
            }
            else
            {
                throw new PortableRestoreException(
                    "RESTORE_SOURCE_SECRETS_MISSING",
                    "旧实例存在进程或 PG 归属记录但缺少可核验 DPAPI 凭据；拒绝切换。");
            }

            var postgres = new WindowsPostgresComponent(
                sourceLease,
                secrets,
                new WindowsPostgresOptions(
                    _bundle.PostgresRoot,
                    Path.Combine(active.DataRoot, "postgres", "cluster"),
                    active.PostgresPort,
                    "17.11",
                    PostgresInitializationTimeout,
                    PostgresStartupTimeout,
                    PostgresStopTimeout));
            try
            {
                if (await postgres.GetStateAsync(cancellationToken).ConfigureAwait(false) is not AiGoofish.Launcher.Core.ComponentRuntimeState.Stopped)
                {
                    throw new PortableRestoreException(
                        "RESTORE_SOURCE_POSTGRES_NOT_STOPPED",
                        "旧 PostgreSQL 正在运行或无法确认已停止；请先正常停止旧实例再确认切换。");
                }
            }
            finally
            {
                await postgres.DisposeAsync().ConfigureAwait(false);
            }

            var pythonRuntimePath = Path.Combine(active.DataRoot, "config", "python-runtime.json");
            var controlPath = Path.Combine(active.DataRoot, "config", "python-control.dpapi");
            if (File.Exists(pythonRuntimePath))
            {
                PythonPathGuard.EnsureSafeInstancePath(sourceLease, pythonRuntimePath, allowMissingLeaf: false);
                var runtime = PythonStateFile.Read<PythonRuntimeRecord>(pythonRuntimePath, 64 * 1024);
                var mode = runtime.Mode switch
                {
                    "maintenance" => PythonServiceMode.Maintenance,
                    "normal" => PythonServiceMode.Normal,
                    _ => throw new PortableRestoreException("RESTORE_SOURCE_PYTHON_RECORD_INVALID", "旧 Python runtime mode 无效。"),
                };
                var bootstrap = Path.Combine(_bundle.ProgramRoot, "scripts", "portable", "python-bootstrap.py");
                var python = new WindowsPythonComponent(
                    sourceLease,
                    secrets,
                    new WindowsPythonOptions(
                        _bundle.PythonExecutable,
                        bootstrap,
                        _bundle.ProgramRoot,
                        active.DataRoot,
                        Path.Combine(active.DataRoot, "cache"),
                        _bundle.BrowserRoot,
                        runtime.Port,
                        runtime.AppVersion,
                        mode,
                        _bundle.SchemaMinimum,
                        _bundle.SchemaMaximum,
                        MaintenanceStartupTimeout,
                        MaintenanceRequestTimeout,
                        MaintenanceStopTimeout),
                    new PythonDatabaseEndpoints(
                        MakeDsn("aigoofish_app", secrets.ApplicationDatabasePassword, active.PostgresPort),
                        MakeDsn("aigoofish_probe", secrets.ProbeDatabasePassword, active.PostgresPort)));
                try
                {
                    if (await python.GetStateAsync(cancellationToken).ConfigureAwait(false) is not AiGoofish.Launcher.Core.ComponentRuntimeState.Stopped)
                    {
                        throw new PortableRestoreException(
                            "RESTORE_SOURCE_PYTHON_NOT_STOPPED",
                            "旧 Python/Web 进程正在运行或归属状态未知；拒绝切换。");
                    }
                }
                finally
                {
                    await python.DisposeAsync().ConfigureAwait(false);
                }
            }
            else if (File.Exists(controlPath))
            {
                throw new PortableRestoreException(
                    "RESTORE_SOURCE_PYTHON_RECORD_MISSING",
                    "旧实例存在无法配对的 Python 控制凭据；拒绝切换。");
            }
            }

            var guard = new SourceInstanceSwitchGuard(sourceLease);
            sourceLease = null!;
            return guard;
        }
        finally
        {
            sourceLease?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var item in _candidates.ToArray())
        {
            var target = item.Value.Target;
            if (target is not null)
            {
                await StopCandidateAsync(target, item.Value, CancellationToken.None).ConfigureAwait(false);
                if (_candidates.TryRemove(item.Key, out var candidate))
                {
                    await candidate.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        _disposed = true;
    }

    private async Task StopCandidateAsync(
        PortableRestoreTarget target,
        CandidateComponents candidate,
        CancellationToken cancellationToken)
    {
        target.Lease.EnsureHeld();
        if (candidate.Python is { } python)
        {
            var pythonState = await python.GetStateAsync(cancellationToken).ConfigureAwait(false);
            if (pythonState is AiGoofish.Launcher.Core.ComponentRuntimeState.Running or
                AiGoofish.Launcher.Core.ComponentRuntimeState.Starting)
            {
                await python.StopAsync(cancellationToken).ConfigureAwait(false);
            }

            if (await python.GetStateAsync(cancellationToken).ConfigureAwait(false) is not AiGoofish.Launcher.Core.ComponentRuntimeState.Stopped)
            {
                throw new PortableRestoreException("RESTORE_PYTHON_STOP_UNPROVEN", "恢复维护进程未确认停止；保留候选 lease。");
            }
        }

        var postgresState = await candidate.Postgres.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (postgresState is AiGoofish.Launcher.Core.ComponentRuntimeState.Running or
            AiGoofish.Launcher.Core.ComponentRuntimeState.Starting)
        {
            await candidate.Postgres.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await candidate.Postgres.GetStateAsync(cancellationToken).ConfigureAwait(false) is not AiGoofish.Launcher.Core.ComponentRuntimeState.Stopped)
        {
            throw new PortableRestoreException("RESTORE_POSTGRES_STOP_UNPROVEN", "恢复 PostgreSQL 未确认 smart stop；保留候选 lease。");
        }
    }

    private static string MakeDsn(string role, string password, int port) =>
        $"postgresql://{role}:{Uri.EscapeDataString(password)}@127.0.0.1:{port}/aigoofish";

    private static bool HasPortableRuntimeEvidence(string dataRoot)
    {
        var config = Path.Combine(dataRoot, "config");
        var pgData = Path.Combine(dataRoot, "postgres", "cluster");
        var markers = new[]
        {
            Path.Combine(config, "postgres-runtime.json"),
            Path.Combine(config, "postgres-initialization.json"),
            Path.Combine(config, "python-runtime.json"),
            Path.Combine(config, "python-control.dpapi"),
            Path.Combine(config, "instance-secrets.dpapi"),
        };
        if (HasUnknownOrReparse(config) || HasUnknownOrReparse(Path.Combine(dataRoot, "postgres")) ||
            HasUnknownOrReparse(pgData) || markers.Any(HasUnknownOrReparse))
        {
            return true;
        }

        if (markers.Any(path => TryGetAttributes(path, out _)))
        {
            return true;
        }

        if (!TryGetAttributes(pgData, out var pgAttributes))
        {
            return false;
        }

        if ((pgAttributes & FileAttributes.Directory) == 0)
        {
            return true;
        }

        try
        {
            return Directory.EnumerateFileSystemEntries(pgData).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PortableRestoreException("RESTORE_SOURCE_PATH_UNKNOWN", "无法只读检查旧 PostgreSQL 目录；拒绝切换。", exception);
        }
    }

    private static bool HasUnknownOrReparse(string path)
    {
        try
        {
            return TryGetAttributes(path, out var attributes) &&
                (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (PortableRestoreException)
        {
            return true;
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PortableRestoreException("RESTORE_SOURCE_PATH_UNKNOWN", "无法核验旧实例身份路径；拒绝切换。", exception);
        }
    }

    private sealed class SourceInstanceSwitchGuard(InstanceDataRootLease lease) : IAsyncDisposable
    {
        private InstanceDataRootLease? _lease = lease;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _lease, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CandidateComponents(WindowsPostgresComponent postgres) : IAsyncDisposable
    {
        public WindowsPostgresComponent Postgres { get; } = postgres;
        public WindowsPythonComponent? Python { get; set; }
        public PortableRestoreTarget? Target { get; set; }

        public async ValueTask DisposeAsync()
        {
            if (Python is not null)
            {
                await Python.DisposeAsync().ConfigureAwait(false);
            }

            await Postgres.DisposeAsync().ConfigureAwait(false);
        }
    }
}
