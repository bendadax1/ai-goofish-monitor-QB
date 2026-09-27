using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AiGoofish.Launcher.Platform.Windows;

public sealed partial class WindowsInstanceSecretsStore
{
    // This is only the target-user credential import step. It does not assert
    // that the database restore succeeded or make this instance active.
    internal InstanceSecrets ImportRestoredHandoff(
        InstanceDataRootLease lease,
        string postgresDataPath,
        string handoffParent,
        string handoffDirectory,
        string bootstrapAdminPassword)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureHeld();
        ValidateRestoredClusterMarker(lease, postgresDataPath);
        var secretsPath = GetSecretsPath(lease);
        EnsureNoReparsePoints(lease.InstanceRoot, secretsPath);
        if (TryGetAttributes(secretsPath, out _))
        {
            throw new InstanceSecretsAlreadyExistException();
        }

        var payloadPath = ValidateHandoffPath(lease, handoffParent, handoffDirectory);
        InstanceSecrets? imported = null;
        Exception? operationError = null;
        Exception? cleanupError = null;
        using (var stream = OpenHandoff(payloadPath))
        {
            try
            {
                if (stream.Length <= 0 || stream.Length > MaximumFileBytes)
                {
                    throw new InstanceSecretsFormatException("恢复凭据交接文件大小无效。");
                }

                var bytes = new byte[checked((int)stream.Length)];
                try
                {
                    stream.ReadExactly(bytes);
                    if (stream.ReadByte() != -1)
                    {
                        throw new InstanceSecretsFormatException("恢复凭据交接文件在读取期间发生变化。");
                    }

                    RestoredHandoff handoff;
                    try
                    {
                        handoff = JsonSerializer.Deserialize<RestoredHandoff>(bytes, JsonOptions)
                            ?? throw new InstanceSecretsFormatException("恢复凭据交接内容为空。");
                    }
                    catch (JsonException)
                    {
                        throw new InstanceSecretsFormatException("恢复凭据交接格式无效。");
                    }

                    if (handoff.FormatVersion != 1 || handoff.InstanceId != lease.InstanceId)
                    {
                        throw new InstanceSecretsFormatException("恢复凭据交接的实例身份或版本不匹配。");
                    }

                    imported = new InstanceSecrets(
                        bootstrapAdminPassword,
                        handoff.AppDatabasePassword,
                        handoff.ProbeDatabasePassword,
                        handoff.EncryptionMasterKey,
                        handoff.SecretKey);
                    WriteNewProtected(lease, secretsPath, imported);
                    var checkedSecrets = Load(lease);
                    if (!imported.Values().SequenceEqual(checkedSecrets.Values()))
                    {
                        throw new InstanceSecretsException("目标当前用户 DPAPI 恢复校验失败。");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            catch (Exception exception)
            {
                operationError = exception;
            }

            try
            {
                stream.Position = 0;
                var zeros = new byte[4096];
                var remaining = stream.Length;
                while (remaining > 0)
                {
                    var count = checked((int)Math.Min(remaining, zeros.Length));
                    stream.Write(zeros, 0, count);
                    remaining -= count;
                }

                stream.Flush(flushToDisk: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                cleanupError = exception;
            }
        }

        if (cleanupError is null)
        {
            try
            {
                // The directory is a restore-owned, one-file handoff. Recheck
                // it after closing the handle before removing exact paths.
                _ = ValidateHandoffPath(lease, handoffParent, handoffDirectory);
                File.Delete(payloadPath);
                Directory.Delete(handoffDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InstanceSecretsException)
            {
                cleanupError = exception;
            }
        }

        if (cleanupError is not null)
        {
            throw new InstanceSecretsException("恢复凭据交接清理失败；目标实例不得报告恢复成功。",
                operationError is null ? cleanupError : new AggregateException(operationError, cleanupError));
        }

        if (operationError is not null)
        {
            ExceptionDispatchInfo.Capture(operationError).Throw();
        }

        return imported ?? throw new InstanceSecretsException("恢复凭据未导入。");
    }

    private static void ValidateRestoredClusterMarker(InstanceDataRootLease lease, string postgresDataPath)
    {
        var expected = Path.Combine(lease.InstanceRoot, "postgres", "cluster");
        if (!Path.IsPathFullyQualified(postgresDataPath) ||
            !string.Equals(Path.GetFullPath(postgresDataPath), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstanceSecretsException("恢复 PGDATA 必须是新实例的标准数据目录。");
        }

        var markerPath = Path.Combine(expected, ".aigoofish-cluster.json");
        var versionPath = Path.Combine(expected, "PG_VERSION");
        PostgresPathGuard.EnsureSafePath(lease, markerPath, allowMissingLeaf: false);
        PostgresPathGuard.EnsureSafePath(lease, versionPath, allowMissingLeaf: false);
        var marker = PostgresStateFile.Read<RestoredClusterMarker>(markerPath, 16 * 1024);
        string version;
        try
        {
            version = File.ReadAllText(versionPath).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstanceSecretsException("无法核验恢复目标 PG_VERSION。", exception);
        }

        if (marker.FormatVersion != 1 || marker.InstanceId != lease.InstanceId ||
            marker.ClusterId == Guid.Empty || marker.EngineVersion is null ||
            !marker.EngineVersion.StartsWith("17.", StringComparison.Ordinal) ||
            version != "17")
        {
            throw new InstanceSecretsFormatException("恢复目标 PG17 集群标记与当前实例不匹配。");
        }
    }

    private static string ValidateHandoffPath(
        InstanceDataRootLease lease, string handoffParent, string handoffDirectory)
    {
        if (!Path.IsPathFullyQualified(handoffParent) || !Path.IsPathFullyQualified(handoffDirectory))
        {
            throw new InstanceSecretsException("恢复交接目录必须是绝对路径。");
        }

        var parent = Path.GetFullPath(handoffParent);
        var directory = Path.GetFullPath(handoffDirectory);
        var directoryName = Path.GetFileName(directory);
        const string prefix = ".restore-handoff-";
        if (string.Equals(parent, lease.InstanceRoot, StringComparison.OrdinalIgnoreCase) ||
            parent.StartsWith(lease.InstanceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(directory), parent, StringComparison.OrdinalIgnoreCase) ||
            !directoryName.StartsWith(prefix, StringComparison.Ordinal) ||
            directoryName.Length < prefix.Length + 6 || directoryName.Length > 80 ||
            directoryName[prefix.Length..].Any(character =>
                character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
        {
            throw new InstanceSecretsException("恢复交接目录不在允许的目标外私有位置。");
        }

        try
        {
            for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Directory) == 0)
                {
                    throw new InstanceSecretsException("恢复交接路径包含重解析点或非目录。");
                }
            }

            var entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            var payload = Path.Combine(directory, "secrets.json");
            if (entries.Length != 1 || !string.Equals(entries[0], payload, StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(payload) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                throw new InstanceSecretsException("恢复交接目录必须只包含普通 secrets.json 文件。");
            }

            return payload;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstanceSecretsException("无法核验恢复交接路径或文件。", exception);
        }
    }

    private static FileStream OpenHandoff(string payloadPath)
    {
        try
        {
            return new FileStream(payloadPath, FileMode.Open, FileAccess.ReadWrite,
                FileShare.None, bufferSize: 4096, FileOptions.WriteThrough);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstanceSecretsException("无法独占打开恢复凭据交接文件。", exception);
        }
    }

    private sealed record RestoredClusterMarker(
        int FormatVersion, Guid InstanceId, Guid ClusterId, string EngineVersion, DateTimeOffset CreatedAtUtc);

    private sealed record RestoredHandoff(
        int FormatVersion, Guid InstanceId, string EncryptionMasterKey, string SecretKey,
        string AppDatabasePassword, string ProbeDatabasePassword);
}
