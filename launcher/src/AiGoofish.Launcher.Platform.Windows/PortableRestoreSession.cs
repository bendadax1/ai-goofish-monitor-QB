namespace AiGoofish.Launcher.Platform.Windows;

public enum PortableRestoreState
{
    Idle,
    PreparingTarget,
    Restoring,
    Auditing,
    AwaitingUserConfirmation,
    Activated,
    FailedTargetRetained,
    CancelledTargetRetained,
}

public sealed record PortableRestorePreview(
    Guid TargetInstanceId,
    string TargetRelativeRoot,
    string SourceInstanceId,
    string BackupSha256,
    DateTimeOffset? BackupCreatedAtUtc,
    IReadOnlyDictionary<string, long> TableCounts,
    int RestoredFileCount,
    long? RestoredFileBytes,
    int RevokedSessionCount,
    bool MaintenanceAuditPassed,
    string DataLossNotice);

public sealed record PortableRestoreSnapshot(
    PortableRestoreState State,
    PortableRestorePreview? Preview,
    string? FailureCode,
    string? DiagnosticTargetRoot,
    string Message);

/// <summary>
/// Production restore lifecycle contract. RestoreAndAuditAsync must leave the
/// candidate stopped before returning (success or failure); it must never
/// activate it or start normal Web/worker services.
/// </summary>
public interface IPortableRestoreExecutor
{
    Task<PortableRestorePreview> RestoreAndAuditAsync(
        PortableRestoreTarget target,
        string archiveFile,
        string passphrase,
        CancellationToken cancellationToken);

    Task EnsureCandidateStoppedAsync(PortableRestoreTarget target, CancellationToken cancellationToken);

    Task<IAsyncDisposable?> AcquirePreviousInstanceSwitchGuardAsync(
        PortableRestoreTarget target,
        CancellationToken cancellationToken);
}

/// <summary>
/// Coordinates a restore candidate and explicit active-pointer confirmation.
/// No method in this class starts normal business services.
/// </summary>
public sealed class PortableRestoreSession : IAsyncDisposable
{
    public const string RequiredConfirmationText = "确认将此恢复副本设为活动数据";
    public const string RequiredBackupTrustText = "我信任此备份来源并允许在隔离目标执行数据库内容";

    private readonly PortableInstanceCatalog _catalog;
    private readonly IPortableRestoreExecutor _executor;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private PortableRestoreTarget? _target;
    private PortableRestorePreview? _preview;
    private PortableRestoreState _state = PortableRestoreState.Idle;
    private string? _failureCode;
    private string _message = "尚未开始恢复。";
    private bool _disposed;

    public PortableRestoreSession(PortableInstanceCatalog catalog, IPortableRestoreExecutor executor)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public PortableRestoreSnapshot Snapshot => new(
        _state,
        _preview,
        _failureCode,
        _target?.DataRoot,
        _message);

    public async Task<PortableRestoreSnapshot> RestoreAndPreviewAsync(
        string archiveFile,
        string passphrase,
        string backupTrustConfirmationText,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_state is not PortableRestoreState.Idle)
            {
                throw new InvalidOperationException("此恢复会话已经开始；请创建新的恢复会话继续。");
            }

            if (string.IsNullOrWhiteSpace(archiveFile) || !Path.IsPathFullyQualified(archiveFile) ||
                string.IsNullOrWhiteSpace(passphrase) ||
                !string.Equals(backupTrustConfirmationText, RequiredBackupTrustText, StringComparison.Ordinal))
            {
                throw new PortableRestoreException(
                    "BACKUP_TRUST_CONFIRMATION_REQUIRED",
                    "PG 逻辑备份包含可执行 SQL；必须先确认信任其来源。");
            }

            _state = PortableRestoreState.PreparingTarget;
            _message = "正在为恢复准备全新隔离目标。";
            _target = _catalog.CreateRestoreTarget();
            try
            {
                _state = PortableRestoreState.Restoring;
                _message = "正在将备份恢复到新目标；现有活动数据保持不变。";
                var preview = await _executor.RestoreAndAuditAsync(
                    _target,
                    Path.GetFullPath(archiveFile),
                    passphrase,
                    cancellationToken).ConfigureAwait(false);
                _state = PortableRestoreState.Auditing;
            if (preview.TargetInstanceId != _target.InstanceId ||
                !string.Equals(preview.TargetRelativeRoot, _target.RelativeRoot, StringComparison.Ordinal) ||
                !preview.MaintenanceAuditPassed || preview.RestoredFileCount < 0 ||
                    preview.RestoredFileBytes is < 0 || preview.RevokedSessionCount < 0 ||
                    preview.TableCounts is null || preview.TableCounts.Count == 0 ||
                    preview.TableCounts.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0) ||
                    preview.BackupSha256.Length != 64 ||
                    preview.BackupSha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                {
                    throw new PortableRestoreException("RESTORE_AUDIT_CONTRACT_INVALID", "恢复维护核对或差异摘要未通过固定契约。");
                }

                await _executor.EnsureCandidateStoppedAsync(_target, cancellationToken).ConfigureAwait(false);
                _catalog.MarkRestoreTargetValidated(_target);
                _preview = preview;
                _state = PortableRestoreState.AwaitingUserConfirmation;
                _message = "恢复和维护核对已通过。活动实例尚未更改；查看差异后输入确认文字以切换。";
                return Snapshot;
            }
            catch (OperationCanceledException)
            {
                await RetainFailureAsync("RESTORE_CANCELLED", "恢复已取消；新目标保留且活动实例未改变。", CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                await RetainFailureAsync(
                    exception is PortableRestoreException restoreException ? restoreException.Code : "RESTORE_FAILED",
                    "恢复或维护核对失败；隔离目标保留供诊断，活动实例未改变。",
                    CancellationToken.None).ConfigureAwait(false);
                throw new PortableRestoreException(_failureCode!, _message, exception);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<PortableRestoreSnapshot> ConfirmActivationAsync(
        string confirmationText,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_state is not PortableRestoreState.AwaitingUserConfirmation || _target is null || _preview is null)
            {
                throw new InvalidOperationException("当前没有已核对并等待确认的恢复目标。");
            }

            if (!string.Equals(confirmationText, RequiredConfirmationText, StringComparison.Ordinal))
            {
                throw new PortableRestoreException("USER_CONFIRMATION_REQUIRED", "必须输入完整确认文字；活动实例保持不变。");
            }

            await _executor.EnsureCandidateStoppedAsync(_target, cancellationToken).ConfigureAwait(false);
            await using (var sourceGuard = await _executor.AcquirePreviousInstanceSwitchGuardAsync(_target, cancellationToken)
                .ConfigureAwait(false))
            {
                _catalog.CommitActiveTarget(_target);
            }
            _target.Dispose();
            _state = PortableRestoreState.Activated;
            _message = "用户已确认；活动实例指针已原子切换。原实例仍完整保留，服务尚未自动启动。";
            return Snapshot;
        }
        catch (Exception exception) when (exception is not PortableRestoreException)
        {
            _failureCode = "ACTIVATION_FAILED";
            _state = PortableRestoreState.FailedTargetRetained;
            _message = "活动指针切换失败；恢复目标保留，原活动实例选择未被覆盖。";
            throw new PortableRestoreException(_failureCode, _message, exception);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<PortableRestoreSnapshot> CancelAndRetainTargetAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_state is not (PortableRestoreState.AwaitingUserConfirmation or PortableRestoreState.FailedTargetRetained))
            {
                throw new InvalidOperationException("只有已停止的待确认或失败目标可以保留并结束恢复会话。");
            }

            if (_target is { IsLeaseHeld: true })
            {
                await _executor.EnsureCandidateStoppedAsync(_target, cancellationToken).ConfigureAwait(false);
                _target.Dispose();
            }

            _state = PortableRestoreState.CancelledTargetRetained;
            _message = "已放弃切换；恢复目标保留供后续诊断，活动实例未改变。";
            return Snapshot;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            if (_target is { IsLeaseHeld: true } target)
            {
                await _executor.EnsureCandidateStoppedAsync(target, CancellationToken.None).ConfigureAwait(false);
                target.Dispose();
            }

            if (_executor is IAsyncDisposable disposableExecutor)
            {
                await disposableExecutor.DisposeAsync().ConfigureAwait(false);
            }

            _disposed = true;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task RetainFailureAsync(string code, string message, CancellationToken cancellationToken)
    {
        _failureCode = code;
        _state = PortableRestoreState.FailedTargetRetained;
        _message = message;
        if (_target is not null)
        {
            // Release the target lock only after the executor proves that no
            // restore-owned PostgreSQL process remains active or unknown.
            try
            {
                await _executor.EnsureCandidateStoppedAsync(_target, cancellationToken).ConfigureAwait(false);
                _target.Dispose();
            }
            catch
            {
                _message = "恢复失败且目标进程状态不确定；隔离目标及 lease 保留，禁止其他进程接管。";
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

public sealed class PortableRestoreException : InvalidOperationException
{
    public PortableRestoreException(string code, string message) : base(message) => Code = code;
    public PortableRestoreException(string code, string message, Exception innerException) : base(message, innerException) => Code = code;
    public string Code { get; }
}
