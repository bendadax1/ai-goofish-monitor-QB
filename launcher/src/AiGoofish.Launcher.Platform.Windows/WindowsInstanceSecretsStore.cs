using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiGoofish.Launcher.Platform.Windows;

public sealed partial class WindowsInstanceSecretsStore
{
    public const string RelativeSecretsPath = "config/instance-secrets.dpapi";
    private const int FormatVersion = 1;
    private const int MaximumFileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly WindowsCurrentUserDataProtector _protector = new();
    private readonly IAtomicFileCommitter _committer;

    public WindowsInstanceSecretsStore()
        : this(new AtomicFileCommitter())
    {
    }

    internal WindowsInstanceSecretsStore(IAtomicFileCommitter committer)
    {
        _committer = committer ?? throw new ArgumentNullException(nameof(committer));
    }

    public InstanceSecrets CreateNew(InstanceDataRootLease lease, string postgresDataPath)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureHeld();
        ValidatePostgresDataPath(lease, postgresDataPath);
        var secretsPath = GetSecretsPath(lease);
        EnsureSecretsDirectory(lease, secretsPath);
        if (TryGetAttributes(secretsPath, out _))
        {
            throw new InstanceSecretsAlreadyExistException();
        }

        var secrets = InstanceSecrets.Generate();
        WriteNewProtected(lease, secretsPath, secrets);
        return secrets;
    }

    private void WriteNewProtected(InstanceDataRootLease lease, string secretsPath, InstanceSecrets secrets)
    {
        lease.EnsureHeld();
        EnsureSecretsDirectory(lease, secretsPath);
        if (TryGetAttributes(secretsPath, out _))
        {
            throw new InstanceSecretsAlreadyExistException();
        }

        var payload = new SecretsPayload(
            FormatVersion,
            lease.InstanceId,
            secrets.PostgresBootstrapAdminPassword,
            secrets.ApplicationDatabasePassword,
            secrets.ProbeDatabasePassword,
            secrets.EncryptionMasterKey,
            secrets.SecretKey);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var entropy = CreateEntropy(lease.InstanceId);
        byte[]? protectedPayload = null;
        byte[]? envelopeBytes = null;
        try
        {
            protectedPayload = _protector.Protect(plaintext, entropy);
            var envelope = new SecretsEnvelope(
                FormatVersion,
                lease.InstanceId,
                Convert.ToBase64String(protectedPayload));
            envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            WriteAtomically(secretsPath, envelopeBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(entropy);
            if (protectedPayload is not null)
            {
                CryptographicOperations.ZeroMemory(protectedPayload);
            }

            if (envelopeBytes is not null)
            {
                CryptographicOperations.ZeroMemory(envelopeBytes);
            }
        }
    }

    public InstanceSecrets Load(InstanceDataRootLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureHeld();
        var secretsPath = GetSecretsPath(lease);
        EnsureSafeExistingFile(lease, secretsPath);
        var envelopeBytes = ReadBoundedFile(secretsPath);
        byte[]? protectedPayload = null;
        byte[]? plaintext = null;
        var entropy = CreateEntropy(lease.InstanceId);
        try
        {
            SecretsEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<SecretsEnvelope>(envelopeBytes, JsonOptions)
                    ?? throw new InstanceSecretsFormatException("实例基础凭据外层格式为空。");
            }
            catch (JsonException exception)
            {
                throw new InstanceSecretsFormatException("实例基础凭据外层格式无效。", exception);
            }

            if (envelope.FormatVersion != FormatVersion || envelope.InstanceId != lease.InstanceId)
            {
                throw new InstanceSecretsFormatException("实例基础凭据版本或 instance_id 与当前实例不匹配。");
            }

            if (string.IsNullOrWhiteSpace(envelope.ProtectedPayload))
            {
                throw new InstanceSecretsFormatException("实例基础凭据密文字段缺失或为空。");
            }

            try
            {
                protectedPayload = Convert.FromBase64String(envelope.ProtectedPayload);
            }
            catch (FormatException exception)
            {
                throw new InstanceSecretsFormatException("实例基础凭据密文不是有效的 Base64。", exception);
            }

            if (protectedPayload.Length == 0 || protectedPayload.Length > MaximumFileBytes)
            {
                throw new InstanceSecretsFormatException("实例基础凭据密文大小无效。");
            }

            plaintext = _protector.Unprotect(protectedPayload, entropy);
            SecretsPayload payload;
            try
            {
                payload = JsonSerializer.Deserialize<SecretsPayload>(plaintext, JsonOptions)
                    ?? throw new InstanceSecretsFormatException("实例基础凭据保护载荷为空。");
            }
            catch (JsonException exception)
            {
                throw new InstanceSecretsFormatException("实例基础凭据保护载荷结构无效。", exception);
            }

            if (payload.FormatVersion != FormatVersion || payload.InstanceId != lease.InstanceId)
            {
                throw new InstanceSecretsFormatException("实例基础凭据保护载荷版本或 instance_id 不匹配。");
            }

            return new InstanceSecrets(
                payload.PostgresBootstrapAdminPassword,
                payload.ApplicationDatabasePassword,
                payload.ProbeDatabasePassword,
                payload.EncryptionMasterKey,
                payload.SecretKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelopeBytes);
            CryptographicOperations.ZeroMemory(entropy);
            if (protectedPayload is not null)
            {
                CryptographicOperations.ZeroMemory(protectedPayload);
            }

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public string GetSecretsPath(InstanceDataRootLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureHeld();
        return Path.Combine(lease.InstanceRoot, "config", "instance-secrets.dpapi");
    }

    private void WriteAtomically(string targetPath, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new InstanceSecretsException("无法解析实例基础凭据目录。");
        var temporaryPath = Path.Combine(directory, $".instance-secrets.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (TryGetAttributes(targetPath, out _))
            {
                throw new InstanceSecretsAlreadyExistException();
            }

            _committer.Commit(temporaryPath, targetPath);
        }
        catch (InstanceSecretsException)
        {
            CleanupTemporaryFile(temporaryPath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                CleanupTemporaryFile(temporaryPath);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                throw new InstanceSecretsException(
                    "实例基础凭据原子提交失败，且唯一暂存文件清理失败。",
                    new AggregateException(exception, cleanupException));
            }

            throw new InstanceSecretsException("实例基础凭据原子提交失败；目标文件未被覆盖。", exception);
        }
    }

    private static void CleanupTemporaryFile(string temporaryPath)
    {
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }
    }

    private static byte[] ReadBoundedFile(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > MaximumFileBytes)
            {
                throw new InstanceSecretsFormatException("实例基础凭据文件大小无效或超过 64 KiB 上限。");
            }

            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw new InstanceSecretsFormatException("实例基础凭据文件在读取期间发生变化。");
            }

            return bytes;
        }
        catch (InstanceSecretsException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstanceSecretsException("无法读取实例基础凭据文件。", exception);
        }
    }

    private static void ValidatePostgresDataPath(InstanceDataRootLease lease, string postgresDataPath)
    {
        if (string.IsNullOrWhiteSpace(postgresDataPath) || !Path.IsPathFullyQualified(postgresDataPath))
        {
            throw new ArgumentException("PGDATA 必须是当前实例数据根内的绝对路径。", nameof(postgresDataPath));
        }

        var normalized = Path.GetFullPath(postgresDataPath);
        if (!normalized.StartsWith(lease.InstanceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("PGDATA 必须严格位于当前实例数据根内。", nameof(postgresDataPath));
        }

        EnsureNoReparsePoints(lease.InstanceRoot, normalized);
        if (!TryGetAttributes(normalized, out var attributes))
        {
            return;
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new InstanceSecretsException("PGDATA 路径是文件，拒绝创建实例基础凭据。");
        }

        try
        {
            if (Directory.EnumerateFileSystemEntries(normalized).Any())
            {
                throw new InstanceSecretsException("PGDATA 已非空，拒绝补造实例基础凭据。");
            }
        }
        catch (InstanceSecretsException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstanceSecretsException("无法确认 PGDATA 是否为空，拒绝创建实例基础凭据。", exception);
        }
    }

    private static void EnsureSecretsDirectory(InstanceDataRootLease lease, string secretsPath)
    {
        var directory = Path.GetDirectoryName(secretsPath)
            ?? throw new InstanceSecretsException("无法解析实例基础凭据目录。");
        EnsureNoReparsePoints(lease.InstanceRoot, directory);
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstanceSecretsException("无法准备实例基础凭据目录。", exception);
        }

        EnsureNoReparsePoints(lease.InstanceRoot, directory);
    }

    private static void EnsureSafeExistingFile(InstanceDataRootLease lease, string path)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InstanceSecretsException("无法解析实例基础凭据目录。");
        EnsureNoReparsePoints(lease.InstanceRoot, directory);
        if (!TryGetAttributes(path, out var attributes))
        {
            throw new InstanceSecretsException("实例基础凭据文件不存在；Load 不会自动创建。");
        }

        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new InstanceSecretsException("实例基础凭据路径不是安全的普通文件。");
        }
    }

    private static void EnsureNoReparsePoints(string instanceRoot, string targetPath)
    {
        var root = Path.GetFullPath(instanceRoot);
        var target = Path.GetFullPath(targetPath);
        if (target != root && !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstanceSecretsException("路径超出当前实例数据根。");
        }

        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InstanceSecretsException("实例数据根是重解析点。");
        }

        var remaining = target[root.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in remaining)
        {
            current = Path.Combine(current, segment);
            if (!TryGetAttributes(current, out var attributes))
            {
                break;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InstanceSecretsException("路径包含重解析点。");
            }

            if ((attributes & FileAttributes.Directory) == 0 && !string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
            {
                throw new InstanceSecretsException("路径祖先不是目录，拒绝继续解析。");
            }
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
            throw new InstanceSecretsException("无法核验实例基础凭据路径属性。", exception);
        }
    }

    private static byte[] CreateEntropy(Guid instanceId)
    {
        return Encoding.UTF8.GetBytes($"ai-goofish-monitor/instance-secrets/v1/{instanceId:D}");
    }

    private sealed record SecretsEnvelope(int FormatVersion, Guid InstanceId, string ProtectedPayload);

    private sealed record SecretsPayload(
        int FormatVersion,
        Guid InstanceId,
        string PostgresBootstrapAdminPassword,
        string ApplicationDatabasePassword,
        string ProbeDatabasePassword,
        string EncryptionMasterKey,
        string SecretKey);
}

internal interface IAtomicFileCommitter
{
    void Commit(string temporaryPath, string targetPath);
}

internal sealed class AtomicFileCommitter : IAtomicFileCommitter
{
    public void Commit(string temporaryPath, string targetPath)
    {
        File.Move(temporaryPath, targetPath, overwrite: false);
    }
}
