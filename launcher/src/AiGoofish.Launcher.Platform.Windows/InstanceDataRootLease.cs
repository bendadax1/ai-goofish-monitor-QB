using System.Text;
using System.Text.Json;

namespace AiGoofish.Launcher.Platform.Windows;

public sealed class InstanceDataRootLease : IDisposable
{
    public const string LockFileName = ".launcher.instance.lock";

    private readonly FileStream _lockStream;
    private bool _disposed;

    private InstanceDataRootLease(string instanceRoot, string lockFilePath, Guid instanceId, FileStream lockStream)
    {
        InstanceRoot = instanceRoot;
        LockFilePath = lockFilePath;
        InstanceId = instanceId;
        _lockStream = lockStream;
    }

    public string InstanceRoot { get; }

    public string LockFilePath { get; }

    public Guid InstanceId { get; }

    public bool IsHeld => !_disposed;

    public static InstanceDataRootLease Acquire(string instanceRoot)
    {
        if (string.IsNullOrWhiteSpace(instanceRoot))
        {
            throw new ArgumentException("实例数据根不能为空。", nameof(instanceRoot));
        }

        if (!Path.IsPathFullyQualified(instanceRoot))
        {
            throw new ArgumentException("实例数据根必须是绝对路径。", nameof(instanceRoot));
        }

        var normalizedRoot = Path.GetFullPath(instanceRoot);
        EnsureNoReparsePointsInExistingPath(normalizedRoot);
        try
        {
            Directory.CreateDirectory(normalizedRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("无法准备实例数据根。", exception);
        }

        var rootInfo = new DirectoryInfo(normalizedRoot);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("实例数据根不能是重解析点。");
        }

        var lockFilePath = Path.Combine(normalizedRoot, LockFileName);
        FileStream lockStream;
        var lockFileExisted = false;
        try
        {
            lockStream = new FileStream(
                lockFilePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);
        }
        catch (IOException)
        {
            var attributes = GetExistingLockAttributes(lockFilePath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("实例锁文件不能是重解析点。");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new InvalidOperationException("实例锁路径不能是目录。");
            }

            lockFileExisted = true;
            try
            {
                lockStream = new FileStream(
                    lockFilePath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough);
            }
            catch (IOException exception)
            {
                throw new InstanceAlreadyLockedException(normalizedRoot, exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new InvalidOperationException("无权持有实例数据根锁。", exception);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException("无权持有实例数据根锁。", exception);
        }

        try
        {
            EnsureNoReparsePointsInExistingPath(normalizedRoot);
            if ((File.GetAttributes(lockFilePath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("实例锁文件在打开后被识别为重解析点，拒绝读取或写入。");
            }

            var instanceId = ReadOrCreateMetadata(lockStream, lockFileExisted);
            return new InstanceDataRootLease(normalizedRoot, lockFilePath, instanceId, lockStream);
        }
        catch
        {
            lockStream.Dispose();
            throw;
        }
    }

    private static FileAttributes GetExistingLockAttributes(string lockFilePath)
    {
        try
        {
            return File.GetAttributes(lockFilePath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException("创建实例锁失败，且无法确认已有锁文件；拒绝自动重试。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException("无权核验已有实例锁。", exception);
        }
    }

    public void EnsureHeld()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lockStream.Dispose();
    }

    private static Guid ReadOrCreateMetadata(FileStream stream, bool lockFileExisted)
    {
        if (stream.Length == 0)
        {
            if (lockFileExisted)
            {
                throw new InstanceMetadataException("已有实例锁元数据为空，拒绝自动重置。");
            }

            var instanceId = Guid.NewGuid();
            var metadata = JsonSerializer.Serialize(new InstanceLockMetadata(1, instanceId, DateTimeOffset.UtcNow));
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(metadata);
            stream.Position = 0;
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return instanceId;
        }

        try
        {
            if (stream.Length > 4096)
            {
                throw new InstanceMetadataException("实例锁元数据超过允许大小，拒绝自动重置。");
            }

            stream.Position = 0;
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);
            var json = reader.ReadToEnd();
            var metadata = JsonSerializer.Deserialize<InstanceLockMetadata>(json);
            if (metadata is null || metadata.FormatVersion != 1 || metadata.InstanceId == Guid.Empty)
            {
                throw new InstanceMetadataException("实例锁元数据无效，拒绝自动重置。");
            }

            return metadata.InstanceId;
        }
        catch (InstanceMetadataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or IOException)
        {
            throw new InstanceMetadataException("实例锁元数据损坏或不可读，拒绝自动重置。", exception);
        }
    }

    private static void EnsureNoReparsePointsInExistingPath(string path)
    {
        var current = Path.GetPathRoot(path)
            ?? throw new InvalidOperationException("无法解析实例数据根盘符。");
        var remaining = path[current.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in remaining)
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
            {
                break;
            }

            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"实例数据根路径包含重解析点：{current}");
            }
        }
    }

    private sealed record InstanceLockMetadata(int FormatVersion, Guid InstanceId, DateTimeOffset CreatedAtUtc);
}

public sealed class InstanceAlreadyLockedException : InvalidOperationException
{
    public InstanceAlreadyLockedException(string instanceRoot, Exception innerException)
        : base($"实例数据根已由另一个 Launcher 会话持有：{instanceRoot}", innerException)
    {
        InstanceRoot = instanceRoot;
    }

    public string InstanceRoot { get; }
}

public sealed class InstanceMetadataException : IOException
{
    public InstanceMetadataException(string message)
        : base(message)
    {
    }

    public InstanceMetadataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
