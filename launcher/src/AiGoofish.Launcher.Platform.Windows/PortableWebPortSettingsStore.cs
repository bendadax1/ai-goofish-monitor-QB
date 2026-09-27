using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiGoofish.Launcher.Core;

namespace AiGoofish.Launcher.Platform.Windows;

/// <summary>
/// Per-instance durable Web port preference. A missing file is a read-only
/// compatibility state and never causes legacy data to be rewritten.
/// </summary>
public sealed class PortableWebPortSettingsStore
{
    public const string RelativePath = "launcher/web-port.json";
    public const string LockRelativePath = "launcher/.web-port.lock";
    private const int MaximumFileBytes = 16 * 1024;
    private const int MinimumPort = 1024;
    private const int MaximumPort = 65535;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public PortableWebPortSettings Read(InstanceDataRootLease lease, int fallbackPort, int postgresPort)
    {
        ValidateLeaseAndPorts(lease, fallbackPort, postgresPort);
        var path = GetPath(lease, RelativePath);
        if (!File.Exists(path))
        {
            return new PortableWebPortSettings(lease.InstanceId, 0, fallbackPort, null, null);
        }

        PortableInstanceCatalog.EnsureSafePath(lease.InstanceRoot, path, allowMissingLeaf: false);
        return ReadFile(path, lease.InstanceId, postgresPort);
    }

    public PortableWebPortSettings Stage(
        InstanceDataRootLease lease,
        long expectedRevision,
        int candidatePort,
        int effectiveFallbackPort,
        int postgresPort,
        bool automatic = false)
    {
        ValidateLeaseAndPorts(lease, effectiveFallbackPort, postgresPort);
        ValidatePort(candidatePort, postgresPort);
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        return Mutate(lease, effectiveFallbackPort, postgresPort, current =>
        {
            if (current.Revision != expectedRevision)
            {
                throw new PortableWebPortSettingsException("Web 端口配置已变化；请刷新后重试。");
            }
            if (current.Applying is not null)
            {
                throw new PortableWebPortSettingsException("Web 端口切换事务尚未完成，拒绝覆盖待处理状态。");
            }

            return current with
            {
                Revision = checked(current.Revision + 1),
                PendingPort = candidatePort == current.EffectivePort ? null : candidatePort,
                Automatic = automatic,
            };
        });
    }

    internal PortableWebPortSettings BeginApply(
        InstanceDataRootLease lease,
        long expectedRevision,
        int effectiveFallbackPort,
        int postgresPort)
    {
        ValidateLeaseAndPorts(lease, effectiveFallbackPort, postgresPort);
        return Mutate(lease, effectiveFallbackPort, postgresPort, current =>
        {
            if (current.Revision != expectedRevision)
            {
                throw new PortableWebPortSettingsException("Web 端口配置已变化；请刷新后重试。");
            }
            if (current.Applying is not null || current.PendingPort is not { } candidate)
            {
                throw new PortableWebPortSettingsException("没有可应用的待处理 Web 端口，或已有切换事务正在进行。");
            }

            ValidatePort(candidate, postgresPort);
            return current with
            {
                Revision = checked(current.Revision + 1),
                Applying = new PortableWebPortApplyIntent(
                    Guid.NewGuid(), current.EffectivePort, candidate, current.Revision),
            };
        });
    }

    internal PortableWebPortSettings CommitApply(
        InstanceDataRootLease lease,
        Guid attemptId,
        int effectiveFallbackPort,
        int postgresPort) => Mutate(lease, effectiveFallbackPort, postgresPort, current =>
    {
        var intent = RequireAttempt(current, attemptId);
        return current with
        {
            Revision = checked(current.Revision + 1),
            EffectivePort = intent.CandidatePort,
            PendingPort = null,
            Applying = null,
        };
    });

    internal PortableWebPortSettings CompleteRollback(
        InstanceDataRootLease lease,
        Guid attemptId,
        int effectiveFallbackPort,
        int postgresPort) => Mutate(lease, effectiveFallbackPort, postgresPort, current =>
    {
        _ = RequireAttempt(current, attemptId);
        return current with
        {
            Revision = checked(current.Revision + 1),
            Applying = null,
        };
    });

    private PortableWebPortSettings Mutate(
        InstanceDataRootLease lease,
        int effectiveFallbackPort,
        int postgresPort,
        Func<PortableWebPortSettings, PortableWebPortSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ValidateLeaseAndPorts(lease, effectiveFallbackPort, postgresPort);
        var directory = GetPath(lease, "launcher");
        PortableInstanceCatalog.EnsureSafePath(lease.InstanceRoot, directory, allowMissingLeaf: true);
        Directory.CreateDirectory(directory);
        PortableInstanceCatalog.EnsureSafePath(lease.InstanceRoot, directory, allowMissingLeaf: false);
        using var fileLock = AcquireLock(GetPath(lease, LockRelativePath));
        var filePath = GetPath(lease, RelativePath);
        var current = File.Exists(filePath)
            ? ReadFile(filePath, lease.InstanceId, postgresPort)
            : new PortableWebPortSettings(lease.InstanceId, 0, effectiveFallbackPort, null, null);
        var updated = update(current);
        ValidateSettings(updated, lease.InstanceId, postgresPort);
        WriteAtomic(filePath, updated);
        return updated;
    }

    private static PortableWebPortSettings ReadFile(string path, Guid instanceId, int postgresPort)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumFileBytes)
            {
                throw new PortableWebPortSettingsException("Web 端口配置文件大小无效，拒绝自动修复。");
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaximumFileBytes)
            {
                throw new PortableWebPortSettingsException("Web 端口配置文件大小无效，拒绝自动修复。");
            }
            var settings = JsonSerializer.Deserialize<PortableWebPortSettings>(stream, JsonOptions)
                ?? throw new PortableWebPortSettingsException("Web 端口配置文件为空，拒绝自动修复。");
            ValidateSettings(settings, instanceId, postgresPort);
            return settings;
        }
        catch (PortableWebPortSettingsException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new PortableWebPortSettingsException("无法安全读取 Web 端口配置文件。", exception);
        }
    }

    private static void ValidateSettings(PortableWebPortSettings settings, Guid instanceId, int postgresPort)
    {
        if (settings.InstanceId != instanceId || settings.Revision < 0)
        {
            throw new PortableWebPortSettingsException("Web 端口配置实例身份或 revision 无效。");
        }
        ValidatePort(settings.EffectivePort, postgresPort);
        if (settings.PendingPort is { } pending)
        {
            ValidatePort(pending, postgresPort);
            if (pending == settings.EffectivePort)
            {
                throw new PortableWebPortSettingsException("待处理 Web 端口不得与当前生效端口相同。");
            }
        }
        if (settings.Applying is { } applying &&
            (applying.AttemptId == Guid.Empty || applying.FromPort != settings.EffectivePort ||
             applying.CandidatePort != settings.PendingPort || applying.CandidatePort == applying.FromPort ||
             applying.ConfirmedRevision < 0))
        {
            throw new PortableWebPortSettingsException("Web 端口切换 intent 与生效/待处理配置不一致。");
        }
    }

    private static PortableWebPortApplyIntent RequireAttempt(PortableWebPortSettings current, Guid attemptId)
    {
        if (attemptId == Guid.Empty || current.Applying is not { } intent || intent.AttemptId != attemptId)
        {
            throw new PortableWebPortSettingsException("Web 端口切换事务身份已变化；拒绝提交。");
        }

        return intent;
    }

    private static void ValidateLeaseAndPorts(InstanceDataRootLease lease, int fallbackPort, int postgresPort)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureHeld();
        ValidatePort(fallbackPort, postgresPort);
        ValidatePort(postgresPort, null);
    }

    private static void ValidatePort(int port, int? postgresPort)
    {
        if (port is < MinimumPort or > MaximumPort || postgresPort == port)
        {
            throw new PortableWebPortSettingsException("Web 端口必须为 1024 到 65535，且不能与 PostgreSQL 端口相同。");
        }
    }

    private static string GetPath(InstanceDataRootLease lease, string relativePath) =>
        Path.Combine(lease.InstanceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static FileStream AcquireLock(string path)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(path)!)!;
        PortableInstanceCatalog.EnsureSafePath(root, path, allowMissingLeaf: true);
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 4096, FileOptions.WriteThrough);
            try
            {
                PortableInstanceCatalog.EnsureSafePath(root, path, allowMissingLeaf: false);
                if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                {
                    throw new PortableWebPortSettingsException("Web 端口锁路径不是普通文件。");
                }
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (PortableWebPortSettingsException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PortableWebPortSettingsException("无法取得 Web 端口配置独占锁；拒绝并发修改。", exception);
        }
    }

    private static void WriteAtomic(string path, PortableWebPortSettings settings)
    {
        var directory = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(directory, ".web-port." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var root = Path.GetDirectoryName(directory)!;
            PortableInstanceCatalog.EnsureSafePath(root, path, allowMissingLeaf: true);
            PortableInstanceCatalog.EnsureSafePath(root, temporary, allowMissingLeaf: true);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            PortableInstanceCatalog.EnsureSafePath(root, path, allowMissingLeaf: true);
            File.Move(temporary, path, overwrite: true);
        }
        catch (PortableWebPortSettingsException)
        {
            TryDeleteTemporary(temporary);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                TryDeleteTemporary(temporary);
            }
            catch (Exception cleanupException)
            {
                throw new PortableWebPortSettingsException("Web 端口原子提交和唯一暂存文件清理都失败。",
                    new AggregateException(exception, cleanupException));
            }

            throw new PortableWebPortSettingsException("Web 端口配置无法原子提交。", exception);
        }
    }

    private static void TryDeleteTemporary(string temporary)
    {
        if (File.Exists(temporary))
        {
            File.Delete(temporary);
        }
    }
}

public sealed record PortableWebPortSettings(
    Guid InstanceId,
    long Revision,
    int EffectivePort,
    int? PendingPort,
    PortableWebPortApplyIntent? Applying,
    bool Automatic = false);

public sealed record PortableWebPortApplyIntent(
    Guid AttemptId,
    int FromPort,
    int CandidatePort,
    long ConfirmedRevision);

public sealed class PortableWebPortSettingsException : InvalidOperationException
{
    public PortableWebPortSettingsException(string message) : base(message) { }
    public PortableWebPortSettingsException(string message, Exception innerException) : base(message, innerException) { }
}

internal static class PortableWebPortRecoveryCommitGuard
{
    public static async Task<LauncherOperationResult?> CommitOrStopCandidateAsync(
        Action commit,
        Func<Task<LauncherOperationResult>> stopCandidate,
        Action markUnresolved,
        Func<LauncherSnapshot> snapshot)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(stopCandidate);
        ArgumentNullException.ThrowIfNull(markUnresolved);
        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {
            commit();
            return null;
        }
        catch (Exception commitFailure) when (
            commitFailure is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            markUnresolved();
            try
            {
                var stop = await stopCandidate().ConfigureAwait(false);
                return new LauncherOperationResult(
                    false,
                    false,
                    stop.Succeeded ? "WEB_PORT_COMMIT_FAILED" : "WEB_PORT_COMMIT_AND_STOP_FAILED",
                    stop.Succeeded
                        ? "候选 Web 已通过进程归属和 Ready 核验，但 effective 提交失败；已请求正常停止候选服务，保留 intent 与 lease。"
                        : "候选 Web 已通过进程归属和 Ready 核验，但 effective 提交失败，且未能安全停止候选服务；保留 intent 与 lease。",
                    stop.Snapshot);
            }
            catch (Exception stopFailure)
            {
                return new LauncherOperationResult(
                    false,
                    false,
                    "WEB_PORT_COMMIT_AND_STOP_FAILED",
                    $"候选 Web 已通过进程归属和 Ready 核验，但 effective 提交失败，且未能安全停止候选服务；保留 intent 与 lease。commit={commitFailure.GetType().Name}; stop={stopFailure.GetType().Name}",
                    snapshot());
            }
        }
    }
}
