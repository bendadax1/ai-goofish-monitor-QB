using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiGoofish.Launcher.Platform.Windows;

/// <summary>
/// Resolves the stable data container into an active instance root and creates
/// isolated restore candidates. The legacy data root remains a valid active
/// instance until an explicit, compare-and-swap pointer commit succeeds.
/// </summary>
public sealed class PortableInstanceCatalog
{
    public const string ActivePointerRelativePath = "launcher/active-instance.json";
    private const string PointerLockFileName = ".active-instance.lock";
    public const int DefaultPostgresPort = 55432;
    public const int DefaultWebPort = 58000;

    private const int PointerLimit = 16 * 1024;
    private const string RestoreTargetMetadataRelativePath = "launcher/restore-target.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _bundleRoot;
    private readonly string _containerRoot;
    private readonly string _pointerPath;

    public PortableInstanceCatalog(string bundleRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleRoot);
        if (!Path.IsPathFullyQualified(bundleRoot))
        {
            throw new ArgumentException("便携包根目录必须是绝对路径。", nameof(bundleRoot));
        }

        _bundleRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleRoot));
        _containerRoot = Path.Combine(_bundleRoot, "data");
        _pointerPath = Path.Combine(_containerRoot, ActivePointerRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string ContainerRoot => _containerRoot;

    public string ActivePointerPath => _pointerPath;

    public PortableActiveInstance ResolveActiveInstance()
    {
        var selected = ReadActiveSelection();
        if (selected.InstanceId is null)
        {
            return selected;
        }

        using var lease = InstanceDataRootLease.Acquire(selected.DataRoot);
        if (lease.InstanceId != selected.InstanceId)
        {
            throw new PortableInstanceCatalogException("活动指针与目标实例锁身份不匹配。");
        }

        return selected;
    }

    internal PortableActiveInstance ReadActiveSelection()
    {
        EnsureSafePath(_bundleRoot, _containerRoot, allowMissingLeaf: true);
        if (!TryGetAttributes(_pointerPath, out _))
        {
            return new PortableActiveInstance(
                _containerRoot,
                InstanceId: null,
                RelativeRoot: ".",
                PostgresPort: DefaultPostgresPort,
                WebPort: DefaultWebPort,
                PointerRevision: null);
        }

        EnsureSafePath(_containerRoot, _pointerPath, allowMissingLeaf: false);
        var pointer = ReadPointer();
        var targetRoot = ResolveCandidatePath(pointer.RelativeRoot);
        if (!Directory.Exists(targetRoot))
        {
            throw new PortableInstanceCatalogException("活动实例目录缺失；拒绝回退到旧数据根或创建替代实例。");
        }

        EnsureSafePath(_containerRoot, targetRoot, allowMissingLeaf: false);
        var metadata = ReadRestoreTargetMetadata(targetRoot, pointer.InstanceId);
        if (metadata.State != "Validated" || metadata.RelativeRoot != pointer.RelativeRoot ||
            metadata.PostgresPort != pointer.PostgresPort || metadata.WebPort != pointer.WebPort)
        {
            throw new PortableInstanceCatalogException("活动指针与已验证目标元数据不匹配。");
        }

        return new PortableActiveInstance(
            targetRoot,
            pointer.InstanceId,
            pointer.RelativeRoot,
            pointer.PostgresPort,
            pointer.WebPort,
            pointer.Revision);
    }

    public PortableRestoreTarget CreateRestoreTarget()
    {
        var expectedPointerRevision = ReadCurrentPointerRevision();
        EnsureContainerRoot();
        var instancesRoot = Path.Combine(_containerRoot, "instances");
        EnsureSafePath(_containerRoot, instancesRoot, allowMissingLeaf: true);
        Directory.CreateDirectory(instancesRoot);
        EnsureSafePath(_containerRoot, instancesRoot, allowMissingLeaf: false);

        var targetName = "restore-" + Guid.NewGuid().ToString("N");
        var relative = Path.Combine("instances", targetName);
        var targetRoot = ResolveCandidatePath(relative);
        if (TryGetAttributes(targetRoot, out _))
        {
            throw new PortableInstanceCatalogException("恢复目标随机路径已存在；拒绝复用或覆盖既有目录。");
        }

        try
        {
            Directory.CreateDirectory(targetRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PortableInstanceCatalogException("无法建立新的隔离恢复目标目录。", exception);
        }

        try
        {
            EnsureSafePath(_containerRoot, targetRoot, allowMissingLeaf: false);
            var lease = InstanceDataRootLease.Acquire(targetRoot);
            var entries = Directory.EnumerateFileSystemEntries(targetRoot).ToArray();
            if (entries.Length != 1 || !string.Equals(entries[0], lease.LockFilePath, StringComparison.OrdinalIgnoreCase))
            {
                lease.Dispose();
                throw new PortableInstanceCatalogException("新恢复目标在取得 lease 前已出现其他内容；保留目录并拒绝继续。");
            }

            var postgresPort = PortablePortAllocator.GetAvailableLoopbackPort();
            var webPort = PortablePortAllocator.GetAvailableLoopbackPort();
            if (webPort == postgresPort)
            {
                webPort = PortablePortAllocator.GetAvailableLoopbackPort();
            }
            if (webPort == postgresPort)
            {
                lease.Dispose();
                throw new PortableInstanceCatalogException("无法为恢复目标分配两个独立本机端口。");
            }

            var launcherRoot = Path.Combine(targetRoot, "launcher");
            Directory.CreateDirectory(launcherRoot);
            EnsureSafePath(targetRoot, launcherRoot, allowMissingLeaf: false);
            WriteNewJson(
                Path.Combine(targetRoot, RestoreTargetMetadataRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                new RestoreTargetMetadata(1, lease.InstanceId, relative, postgresPort, webPort, "Preparing"));

            return new PortableRestoreTarget(
                targetRoot,
                lease.InstanceId,
                relative,
                postgresPort,
                webPort,
                lease,
                expectedPointerRevision);
        }
        catch
        {
            // Keep the newly-created directory as a diagnostic target. It is
            // never recursively removed by the catalog.
            throw;
        }
    }

    internal void MarkRestoreTargetValidated(PortableRestoreTarget target)
    {
        target.Lease.EnsureHeld();
        var path = Path.Combine(target.DataRoot, RestoreTargetMetadataRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var current = ReadRestoreTargetMetadata(target.DataRoot, target.InstanceId);
        if (current.State != "Preparing" || current.RelativeRoot != target.RelativeRoot ||
            current.PostgresPort != target.PostgresPort || current.WebPort != target.WebPort)
        {
            throw new PortableInstanceCatalogException("恢复目标元数据身份或阶段不匹配。");
        }

        WriteJson(path, current with { State = "Validated" }, overwrite: true);
    }

    public void CommitActiveTarget(PortableRestoreTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Lease.EnsureHeld();
        if (!string.Equals(Path.GetFullPath(target.DataRoot), ResolveCandidatePath(target.RelativeRoot), StringComparison.OrdinalIgnoreCase) ||
            !IsCanonicalRestoreRelativePath(target.RelativeRoot, target.InstanceId))
        {
            throw new PortableInstanceCatalogException("待切换实例不是本次创建的恢复目标。");
        }

        EnsureSafePath(_containerRoot, target.DataRoot, allowMissingLeaf: false);
        var targetMetadata = ReadRestoreTargetMetadata(target.DataRoot, target.InstanceId);
        if (targetMetadata.State != "Validated" || targetMetadata.RelativeRoot != target.RelativeRoot ||
            targetMetadata.PostgresPort != target.PostgresPort || targetMetadata.WebPort != target.WebPort)
        {
            throw new PortableInstanceCatalogException("只有通过最终维护审计的恢复目标可以成为活动实例。");
        }
        var pointerDirectory = Path.GetDirectoryName(_pointerPath)!;
        EnsureSafePath(_containerRoot, pointerDirectory, allowMissingLeaf: true);
        Directory.CreateDirectory(pointerDirectory);
        EnsureSafePath(_containerRoot, pointerDirectory, allowMissingLeaf: false);
        using var pointerLock = AcquirePointerLock(pointerDirectory);
        var currentRevision = ReadCurrentPointerRevision();
        if (!string.Equals(currentRevision, target.ExpectedPointerRevision, StringComparison.Ordinal))
        {
            throw new PortableInstanceCatalogException("活动实例在恢复期间发生变化；拒绝覆盖当前选择。");
        }

        var next = new ActivePointer(
            FormatVersion: 1,
            target.InstanceId,
            target.RelativeRoot,
            target.PostgresPort,
            target.WebPort,
            Guid.NewGuid().ToString("N"));
        var temporary = Path.Combine(pointerDirectory, ".active-instance." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, next, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            EnsureSafePath(_containerRoot, _pointerPath, allowMissingLeaf: true);
            File.Move(temporary, _pointerPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                throw new PortableInstanceCatalogException(
                    "活动指针提交失败，且唯一临时文件清理失败。",
                    new AggregateException(exception, cleanupException));
            }

            throw new PortableInstanceCatalogException("活动实例指针原子提交失败；旧活动实例保持不变。", exception);
        }
    }

    internal string ReadCurrentPointerRevision() => TryGetAttributes(_pointerPath, out _) ? ReadPointer().Revision : "legacy-data-root";

    private ActivePointer ReadPointer()
    {
        try
        {
            using var stream = new FileStream(_pointerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > PointerLimit)
            {
                throw new PortableInstanceCatalogException("活动实例指针大小无效。");
            }

            var pointer = JsonSerializer.Deserialize<ActivePointer>(stream, JsonOptions)
                ?? throw new PortableInstanceCatalogException("活动实例指针内容为空。");
            if (pointer.FormatVersion != 1 || pointer.InstanceId == Guid.Empty || pointer.RelativeRoot is null ||
                pointer.PostgresPort is < 1024 or > 65535 || pointer.WebPort is < 1024 or > 65535 ||
                pointer.PostgresPort == pointer.WebPort ||
                !RegexPatterns.ValidRevision(pointer.Revision) ||
                !IsCanonicalRestoreRelativePath(pointer.RelativeRoot, pointer.InstanceId))
            {
                throw new PortableInstanceCatalogException("活动实例指针身份、版本、路径或端口无效。");
            }

            return pointer;
        }
        catch (PortableInstanceCatalogException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new PortableInstanceCatalogException("无法安全读取活动实例指针。", exception);
        }
    }

    private static RestoreTargetMetadata ReadRestoreTargetMetadata(string targetRoot, Guid instanceId)
    {
        var path = Path.Combine(targetRoot, RestoreTargetMetadataRelativePath.Replace('/', Path.DirectorySeparatorChar));
        EnsureSafePath(targetRoot, path, allowMissingLeaf: false);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > PointerLimit)
            {
                throw new PortableInstanceCatalogException("恢复目标元数据大小无效。");
            }

            var value = JsonSerializer.Deserialize<RestoreTargetMetadata>(stream, JsonOptions)
                ?? throw new PortableInstanceCatalogException("恢复目标元数据为空。");
            if (value.FormatVersion != 1 || value.InstanceId != instanceId ||
                value.PostgresPort is < 1024 or > 65535 || value.WebPort is < 1024 or > 65535 ||
                value.PostgresPort == value.WebPort || value.State is not ("Preparing" or "Validated") ||
                value.RelativeRoot is null || !IsCanonicalRestoreRelativePath(value.RelativeRoot, instanceId))
            {
                throw new PortableInstanceCatalogException("恢复目标元数据身份、端口、路径或阶段无效。");
            }

            return value;
        }
        catch (PortableInstanceCatalogException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new PortableInstanceCatalogException("无法核验恢复目标元数据。", exception);
        }
    }

    private string ResolveCandidatePath(string relativeRoot)
    {
        if (string.IsNullOrWhiteSpace(relativeRoot) || Path.IsPathFullyQualified(relativeRoot) || relativeRoot.IndexOf('\0') >= 0 ||
            relativeRoot.Replace('/', '\\').Split('\\').Any(segment => segment is "" or "." or ".."))
        {
            throw new PortableInstanceCatalogException("恢复目标相对路径无效。");
        }

        var normalized = relativeRoot.Replace('/', Path.DirectorySeparatorChar);
        if (!normalized.StartsWith("instances" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new PortableInstanceCatalogException("恢复目标必须位于独立 instances 子目录。");
        }

        var full = Path.GetFullPath(Path.Combine(_containerRoot, normalized));
        if (!full.StartsWith(_containerRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new PortableInstanceCatalogException("恢复目标路径越出稳定数据容器。");
        }

        return full;
    }

    private void EnsureContainerRoot()
    {
        EnsureSafePath(_bundleRoot, _containerRoot, allowMissingLeaf: true);
        try
        {
            Directory.CreateDirectory(_containerRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PortableInstanceCatalogException("无法准备稳定数据容器。", exception);
        }

        EnsureSafePath(_bundleRoot, _containerRoot, allowMissingLeaf: false);
    }

    private static bool IsCanonicalRestoreRelativePath(string relativeRoot, Guid instanceId) =>
        relativeRoot.Replace('/', '\\').StartsWith("instances\\restore-", StringComparison.Ordinal) &&
        relativeRoot.Replace('/', '\\').Length == "instances\\restore-".Length + 32 &&
        relativeRoot.Replace('/', '\\')["instances\\restore-".Length..]
            .All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static void EnsureSafePath(string root, string target, bool allowMissingLeaf)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        var descendantPrefix = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedTarget.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !normalizedTarget.StartsWith(descendantPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new PortableInstanceCatalogException("数据容器路径越界。");
        }

        var current = normalizedRoot;
        foreach (var part in normalizedTarget[normalizedRoot.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                if (allowMissingLeaf)
                {
                    return;
                }

                throw new PortableInstanceCatalogException("数据容器必需路径缺失。", exception);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new PortableInstanceCatalogException("无法核验数据容器路径。", exception);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PortableInstanceCatalogException("数据容器路径包含重解析点。");
            }
        }
    }

    private sealed record ActivePointer(
        int FormatVersion,
        Guid InstanceId,
        string RelativeRoot,
        int PostgresPort,
        int WebPort,
        string Revision);

    private sealed record RestoreTargetMetadata(
        int FormatVersion,
        Guid InstanceId,
        string RelativeRoot,
        int PostgresPort,
        int WebPort,
        string State);

    private static void WriteNewJson<T>(string path, T value) => WriteJson(path, value, overwrite: false);

    private static void WriteJson<T>(string path, T value, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                throw new PortableInstanceCatalogException(
                    "恢复目标状态提交和唯一临时文件清理均失败。",
                    new AggregateException(exception, cleanupException));
            }

            throw new PortableInstanceCatalogException("恢复目标状态文件无法原子提交。", exception);
        }
    }

    private static class RegexPatterns
    {
        public static bool ValidRevision(string? value) => value is { Length: 32 } &&
            value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private FileStream AcquirePointerLock(string directory)
    {
        var path = Path.Combine(directory, PointerLockFileName);
        EnsureSafePath(_containerRoot, path, allowMissingLeaf: true);
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                4096, FileOptions.WriteThrough);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PortableInstanceCatalogException("无法独占活动指针提交锁；拒绝并发切换。", exception);
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
            throw new PortableInstanceCatalogException("无法核验活动指针或恢复目标路径。", exception);
        }
    }
}

public sealed record PortableActiveInstance(
    string DataRoot,
    Guid? InstanceId,
    string RelativeRoot,
    int PostgresPort,
    int WebPort,
    string? PointerRevision);

public sealed class PortableRestoreTarget : IDisposable
{
    internal PortableRestoreTarget(
        string dataRoot,
        Guid instanceId,
        string relativeRoot,
        int postgresPort,
        int webPort,
        InstanceDataRootLease lease,
        string expectedPointerRevision)
    {
        DataRoot = dataRoot;
        InstanceId = instanceId;
        RelativeRoot = relativeRoot;
        PostgresPort = postgresPort;
        WebPort = webPort;
        Lease = lease;
        ExpectedPointerRevision = expectedPointerRevision;
    }

    public string DataRoot { get; }
    public Guid InstanceId { get; }
    public string RelativeRoot { get; }
    public int PostgresPort { get; }
    public int WebPort { get; }
    public bool IsLeaseHeld => Lease.IsHeld;

    internal InstanceDataRootLease Lease { get; }
    internal string ExpectedPointerRevision { get; }

    public void Dispose() => Lease.Dispose();
}

internal static class PortablePortAllocator
{
    public static int GetAvailableLoopbackPort()
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            throw new PortableInstanceCatalogException("无法为隔离恢复目标分配本机端口。", exception);
        }
    }
}

public sealed class PortableInstanceCatalogException : InvalidOperationException
{
    public PortableInstanceCatalogException(string message) : base(message) { }
    public PortableInstanceCatalogException(string message, Exception innerException) : base(message, innerException) { }
}
