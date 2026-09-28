using AiGoofish.Launcher.Core;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiGoofish.Launcher.Platform.Windows;


public sealed record PortableWebPortApplyResult(
    bool Applied,
    int EffectivePort,
    int? PendingPort,
    string Message);

public sealed class PortableWebPortApplyException : InvalidOperationException
{
    public PortableWebPortApplyException(string message, Exception applyFailure, Exception rollbackFailure)
        : base(message, new AggregateException(applyFailure, rollbackFailure))
    {
    }
}

public sealed record PortableBundleDescriptor(
    string BundleRoot,
    string ReleaseId,
    string AppVersion,
    int SchemaMinimum,
    int SchemaMaximum,
    string ProgramRoot,
    string PythonExecutable,
    string BrowserRoot,
    string PostgresRoot,
    string LauncherExecutable)
{
    private const int CurrentLimit = 64 * 1024;
    private const int ManifestLimit = 16 * 1024 * 1024;
    private static readonly Regex IdentifierPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,119}$",
        RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<PortableBundleDescriptor> LoadAndVerifyAsync(
        string bundleRoot,
        CancellationToken cancellationToken = default)
    {
        var root = RequirePlainDirectory(bundleRoot, "便携包根目录");
        var currentPath = Path.Combine(root, "current.json");
        var manifestPath = Path.Combine(root, "bundle-manifest.json");
        var current = ReadJson<BundleCurrent>(currentPath, CurrentLimit);
        var manifest = ReadJson<BundleManifest>(manifestPath, ManifestLimit);
        ValidateCurrent(current);
        if (manifest.FormatVersion != 1 ||
            !string.Equals(manifest.ReleaseStatus, "preview-integration", StringComparison.Ordinal) ||
            manifest.Current != current ||
            manifest.Files is null ||
            manifest.Files.Count == 0)
        {
            throw new PortableBundleException("组合包 manifest 与 current.json 不一致或格式不受支持。");
        }

        var components = new[]
        {
            ResolveComponent(root, current.App, "app"),
            ResolveComponent(root, current.Runtime, "runtime"),
            ResolveComponent(root, current.Browser, "browsers"),
            ResolveComponent(root, current.Postgres, "postgres"),
        };
        var launcherRoot = ResolveLauncher(root, current.Launcher);
        var programRoot = components[0];
        var runtimeRoot = components[1];
        var browserRoot = components[2];
        var postgresRoot = components[3];
        var pythonExecutable = RequirePlainFile(Path.Combine(runtimeRoot, "python.exe"), "内置 Python");
        var bootstrap = RequirePlainFile(
            Path.Combine(programRoot, "scripts", "portable", "python-bootstrap.py"),
            "Python bootstrap");
        _ = bootstrap;
        _ = RequirePlainFile(Path.Combine(runtimeRoot, "python-runtime-manifest.json"), "Python 组件身份清单");
        _ = RequirePlainFile(Path.Combine(browserRoot, "browser-runtime-manifest.json"), "Chromium 组件身份清单");
        _ = RequirePlainFile(Path.Combine(postgresRoot, "bin", "postgres.exe"), "PostgreSQL 组件");
        var launcherExecutable = RequirePlainFile(
            Path.Combine(launcherRoot, current.Launcher.Executable),
            "Launcher 入口");

        var executableComponentRoots = new[]
        {
            NormalizeRelativePath(current.Runtime.RelativeDir),
            NormalizeRelativePath(current.Browser.RelativeDir),
            NormalizeRelativePath(current.Postgres.RelativeDir),
        };
        var appComponentRoot = NormalizeRelativePath(current.App.RelativeDir);
        var launcherComponentRoot = NormalizeRelativePath(current.Launcher.RelativeDir);
        var launcherExecutableRelative = Path.Combine(launcherComponentRoot, NormalizeRelativePath(current.Launcher.Executable));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null ||
                entry.Size < 0 ||
                !Regex.IsMatch(entry.Sha256 ?? string.Empty, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
            {
                throw new PortableBundleException("组合包 manifest 文件项格式无效。");
            }

            var relative = NormalizeRelativePath(entry.Path);
            if (relative.Equals("data", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                relative.Equals("current.json", StringComparison.OrdinalIgnoreCase) ||
                relative.Equals("bundle-manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                throw new PortableBundleException("组合包 manifest 不得包含用户数据根或自引用元数据。");
            }

            if (!seen.Add(relative))
            {
                throw new PortableBundleException("组合包 manifest 包含重复路径。");
            }


            if (IsExecutablePayload(relative) &&
                !relative.Equals("AiGoofish.exe", StringComparison.OrdinalIgnoreCase) &&
                !relative.Equals("uninstall.exe", StringComparison.OrdinalIgnoreCase) &&
                !relative.Equals(launcherExecutableRelative, StringComparison.OrdinalIgnoreCase) &&
                !relative.Equals(Path.Combine(launcherComponentRoot, "createdump.exe"), StringComparison.OrdinalIgnoreCase) &&
                !executableComponentRoots.Any(componentRoot => IsStrictDescendant(componentRoot, relative)))
            {
                var location = IsStrictDescendant(appComponentRoot, relative)
                    ? "只读应用组件"
                    : "Launcher 组件边界之外";
                throw new PortableBundleException($"组合包在{location}包含未授权的额外可执行文件。");
            }

            var file = RequirePlainFile(Path.Combine(root, relative), "组合包文件");
            try
            {
                if (new FileInfo(file).Length != entry.Size ||
                    !string.Equals(await HashFileAsync(file, cancellationToken).ConfigureAwait(false), entry.Sha256, StringComparison.Ordinal))
                    throw new PortableBundleException("组合包文件大小或 SHA-256 与 manifest 不匹配。", StartupFileRole.BundleFile);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw PortableBundleException.ForFile("组合包文件", error);
            }
        }

        foreach (var required in new[]
        {
            pythonExecutable,
            Path.Combine(runtimeRoot, "python313.dll"),
            Path.Combine(runtimeRoot, "python313.zip"),
            Path.Combine(runtimeRoot, "python-runtime-manifest.json"),
            Path.Combine(browserRoot, "chrome-win64", "chrome.exe"),
            Path.Combine(browserRoot, "browser-runtime-manifest.json"),
            Path.Combine(postgresRoot, "bin", "postgres.exe"),
            bootstrap,
            launcherExecutable,
            Path.Combine(root, "AiGoofish.exe"),
            Path.Combine(root, "uninstall.exe"),
        })
        {
            var requiredFile = RequirePlainFile(required, "必需组件文件");
            var relative = Path.GetRelativePath(root, requiredFile);
            if (!seen.Contains(relative))
            {
                throw new PortableBundleException("组合包 manifest 未覆盖必需入口文件。");
            }
        }


        var actualFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateBundleFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file);
            if (relative.Equals("current.json", StringComparison.OrdinalIgnoreCase) ||
                relative.Equals("bundle-manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            actualFiles.Add(relative);
        }

        if (!actualFiles.SetEquals(seen))
        {
            throw new PortableBundleException("组合包实际文件集合与 manifest 不一致；拒绝额外注入或不完整解压。");
        }

        return new PortableBundleDescriptor(
            root,
            current.ReleaseId,
            current.App.Version,
            current.App.SchemaMin,
            current.App.SchemaMax,
            programRoot,
            pythonExecutable,
            browserRoot,
            postgresRoot,
            launcherExecutable);
    }

    private static void ValidateCurrent(BundleCurrent current)
    {
        if (current.FormatVersion != 1 ||
            !string.Equals(current.ReleaseStatus, "preview-integration", StringComparison.Ordinal) ||
            !string.Equals(current.Platform, "windows-x64", StringComparison.Ordinal) ||
            !IdentifierPattern.IsMatch(current.ReleaseId ?? string.Empty) ||
            current.App is null ||
            current.Runtime is null ||
            current.Browser is null ||
            current.Postgres is null ||
            current.Launcher is null ||
            string.IsNullOrWhiteSpace(current.App.Version) ||
            current.App.Version.Length > 128 ||
            current.App.SchemaMin <= 0 ||
            current.App.SchemaMax < current.App.SchemaMin)
        {
            throw new PortableBundleException("current.json 格式、平台、版本或 schema 契约不受支持。", StartupFileRole.CurrentMetadata);
        }
    }

    private static string ResolveComponent(string root, BundleComponent component, string requiredPrefix)
    {
        if (!IdentifierPattern.IsMatch(component.ExactId ?? string.Empty))
        {
            throw new PortableBundleException("组合包组件 exact_id 无效。");
        }

        var relative = NormalizeRelativePath(component.RelativeDir);
        if (!string.Equals(relative, requiredPrefix, StringComparison.Ordinal))
        {
            throw new PortableBundleException("组合包组件目录无效。");
        }

        return RequirePlainDirectory(Path.Combine(root, relative), "组合包组件目录");
    }

    private static string ResolveLauncher(string root, LauncherComponent component)
    {
        if (!IdentifierPattern.IsMatch(component.ExactId ?? string.Empty) ||
            component.RelativeDir != "launcher" ||
            !string.Equals(component.Executable, "AiGoofish.Launcher.App.exe", StringComparison.Ordinal))
        {
            throw new PortableBundleException("Launcher current.json 契约无效。");
        }

        return RequirePlainDirectory(Path.Combine(root, "launcher"), "Launcher 组件目录");
    }

    private static string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathFullyQualified(value) ||
            value.StartsWith('/') ||
            value.StartsWith('\\') ||
            value.IndexOf('\0') >= 0)
        {
            throw new PortableBundleException("组合包包含不安全的相对路径。");
        }

        var parts = value.Replace('/', Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.None);
        if (parts.Length == 0 || parts.Any(IsUnsafePathSegment))
        {
            throw new PortableBundleException("组合包包含路径穿越或空路径。");
        }

        return Path.Combine(parts);
    }

    private static bool IsStrictDescendant(string directory, string path) =>
        path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsExecutablePayload(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".com", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".scr", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".msi", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateBundleFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new PortableBundleException("无法枚举组合包实际文件集合。", exception);
            }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new PortableBundleException("无法核验组合包文件类型。", exception);
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new PortableBundleException("组合包路径包含重解析点。");
                }

                if ((attributes & FileAttributes.Directory) == 0)
                {
                    yield return entry;
                    continue;
                }

                var relative = Path.GetRelativePath(root, entry);
                if (!relative.Equals("data", StringComparison.OrdinalIgnoreCase) &&
                    !relative.Equals("backups", StringComparison.OrdinalIgnoreCase))
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static bool IsUnsafePathSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment) ||
            segment is "." or ".." ||
            segment.EndsWith(' ') ||
            segment.EndsWith('.') ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return true;
        }

        var stem = segment.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(stem, "^(COM|LPT)[1-9¹²³]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static T ReadJson<T>(string path, int maximumBytes)
    {
        var file = RequirePlainFile(path, Path.GetFileName(path));
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > maximumBytes)
            {
                throw new PortableBundleException($"{Path.GetFileName(path)} 大小无效。", PortableBundleException.Role(Path.GetFileName(path)));
            }

            return JsonSerializer.Deserialize<T>(stream, JsonOptions)
                ?? throw new PortableBundleException($"{Path.GetFileName(path)} 内容为空。", PortableBundleException.Role(Path.GetFileName(path)));
        }
        catch (PortableBundleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw PortableBundleException.ForFile(Path.GetFileName(path), exception);
        }
    }

    private static string RequirePlainDirectory(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new PortableBundleException($"{name}必须是绝对路径。");
        }

        var fullPath = Path.GetFullPath(path);
        RequirePathType(fullPath, name, directory: true);
        return fullPath;
    }

    private static string RequirePlainFile(string path, string name)
    {
        var fullPath = Path.GetFullPath(path);
        RequirePathType(fullPath, name, directory: false);
        return fullPath;
    }

    private static void RequirePathType(string fullPath, string name, bool directory)
    {
        try
        {
            // Exists suppresses access errors. Read metadata directly so missing
            // and denied access remain distinct, including parent-path failures.
            var attributes = File.GetAttributes(fullPath);
            if (((attributes & FileAttributes.Directory) != 0) != directory)
                throw new PortableBundleException($"{name}类型不正确。", fileRole: PortableBundleException.Role(name));
            EnsureNoReparsePoints(fullPath, name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw PortableBundleException.ForFile(name, exception);
        }
    }

    private static void EnsureNoReparsePoints(string path, string name)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PortableBundleException("组合包路径包含重解析点。", PortableBundleException.Role(name));
            }
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw PortableBundleException.ForFile("组合包文件", exception);
        }
    }

    private sealed record BundleCurrent(
        int FormatVersion,
        string ReleaseStatus,
        string ReleaseId,
        string Platform,
        AppComponent App,
        BundleComponent Runtime,
        BundleComponent Browser,
        BundleComponent Postgres,
        LauncherComponent Launcher);

    private record BundleComponent(string ExactId, string RelativeDir);

    private sealed record AppComponent(
        string ExactId,
        string RelativeDir,
        string Version,
        int SchemaMin,
        int SchemaMax) : BundleComponent(ExactId, RelativeDir);

    private sealed record LauncherComponent(
        string ExactId,
        string RelativeDir,
        string Executable) : BundleComponent(ExactId, RelativeDir);

    private sealed record ManifestFile(string Path, long Size, string Sha256);

    private sealed record BundleManifest(
        int FormatVersion,
        string ReleaseStatus,
        BundleCurrent Current,
        IReadOnlyList<ManifestFile?> Files);
}

public sealed class PortableBundleException : InvalidOperationException, IStartupFailureContext
{
    public StartupFailureKind FailureKind { get; }
    public StartupFileRole FileRole { get; }

    public PortableBundleException(string message, StartupFileRole fileRole = StartupFileRole.BundleManifest)
        : base(message)
    {
        FailureKind = StartupFailureKind.InvalidData;
        FileRole = fileRole;
    }

    public PortableBundleException(string message, Exception innerException)
        : base(message, innerException)
    {
        FailureKind = Classify(innerException);
        FileRole = StartupFileRole.BundleFile;
        HResult = innerException.HResult;
    }

    private PortableBundleException(string message, Exception error, StartupFileRole role)
        : this(message, error) => FileRole = role;

    internal static StartupFileRole Role(string name) => name switch
    {
        "current.json" => StartupFileRole.CurrentMetadata,
        "bundle-manifest.json" => StartupFileRole.BundleManifest,
        "便携包根目录" => StartupFileRole.BundleRoot,
        "组合包组件目录" => StartupFileRole.ComponentDirectory,
        "组合包文件" => StartupFileRole.BundleFile,
        _ => StartupFileRole.ComponentFile,
    };

    private static StartupFailureKind Classify(Exception error) => error switch
    {
        UnauthorizedAccessException => StartupFailureKind.AccessDenied,
        FileNotFoundException or DirectoryNotFoundException => StartupFailureKind.MissingFile,
        JsonException or InvalidDataException => StartupFailureKind.InvalidData,
        IOException => StartupFailureKind.Io,
        _ => StartupFailureKind.InvalidData,
    };

    internal static PortableBundleException ForFile(string name, Exception error) => new(
        Classify(error) switch
        {
            StartupFailureKind.AccessDenied => $"无法读取{name}：访问被拒绝，请检查当前用户的读取权限。",
            StartupFailureKind.MissingFile => $"{name}不存在或组合包尚未完整解压。",
            StartupFailureKind.InvalidData => $"{name}内容或格式无效，请核对发行包完整性。",
            _ => $"读取{name}失败，请查看启动诊断中的系统错误码。",
        }, error, Role(name));
}

public sealed class RealPortableStackHost : IAsyncDisposable
{
    private readonly InstanceDataRootLease _lease;
    private readonly WindowsPostgresComponent _postgres;
    private readonly PortableInitializationState _initialization;
    private readonly WindowsPortableProvisionStep _provision;
    private readonly InstanceSecrets _secrets;
    private readonly string _cacheRoot;
    private readonly PortableWebPortSettingsStore _webPortSettingsStore;
    private WindowsPythonComponent _python;
    private readonly LauncherCoordinator _coordinator;
    private readonly int _postgresPort;
    private int _webPort;
    private PortableWebPortSettings _webPortSettings;
    private string? _setupToken;
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private bool _disposed;
    private bool _webPortApplyUnresolved;

    // Per-host deterministic fault injection; never set by production callers.
    internal Action? AutomaticPortStagedForAcceptance { get; set; }

    private RealPortableStackHost(
        PortableBundleDescriptor bundle,
        InstanceDataRootLease lease,
        WindowsPostgresComponent postgres,
        WindowsPortableProvisionStep provision,
        InstanceSecrets secrets,
        string cacheRoot,
        WindowsPythonComponent python,
        int postgresPort,
        int webPort,
        PortableWebPortSettings webPortSettings,
        PortableWebPortSettingsStore webPortSettingsStore)
    {
        Bundle = bundle;
        _lease = lease;
        _initialization = new PortableInitializationState(lease);
        _postgres = postgres;
        _provision = provision;
        _secrets = secrets;
        _cacheRoot = cacheRoot;
        _webPortSettingsStore = webPortSettingsStore;
        _python = python;
        _postgresPort = postgresPort;
        _webPort = webPort;
        _webPortSettings = webPortSettings;
        _coordinator = CreateCoordinator(postgres, provision, python);
        Runtime = new LauncherRuntime(_coordinator, RunStartupAsync);
    }

    public PortableBundleDescriptor Bundle { get; }

    public LauncherCoordinator Coordinator => _coordinator;

    public LauncherRuntime Runtime { get; }

    public string ManagementUrl => $"http://127.0.0.1:{_webPort}/";

    public async Task<string> CreateManagementBrowserUrlAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _recoveryGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("实例维护中，请稍后打开管理页。");
        try
        {
            _lease.EnsureHeld();
            var readiness = await _python.RefreshReadinessAsync(cancellationToken).ConfigureAwait(false);
            if (readiness.State is not PythonReadinessState.Ready)
                throw new InvalidOperationException("本机服务尚未就绪。");
            if (readiness.SetupRequired is not true) return ManagementUrl;
            var token = _python.SetupToken ?? throw new InvalidOperationException("首次设置授权暂不可用。");
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(5),
                MaxResponseContentBufferSize = 4096,
            };
            return await PortableSetupBrowserClient.CreateUrlAsync(
                client, ManagementUrl, _lease.InstanceId.ToString("D"), token, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    public string DataRoot => _lease.InstanceRoot;

    public int PostgresPort => _postgresPort;

    public PortableWebPortSettings WebPortSettings =>
        _webPortSettingsStore.Read(_lease, _webPortSettings.EffectivePort, _postgresPort);

    public string LogRoot => Path.Combine(_lease.InstanceRoot, "logs");

    public OwnedProcessIdentity? PostgresProcessIdentity => _postgres.ProcessSnapshot.Identity;

    public OwnedProcessIdentity? PythonProcessIdentity => _python.ProcessSnapshot.Identity;

    public string? SetupToken
    {
        get
        {
            if (!SetupRequired)
            {
                _setupToken = null;
            }

            return _setupToken;
        }
        private init => _setupToken = value;
    }

    public bool SetupRequired => _python.LastReadiness?.SetupRequired is true;

    /// <summary>
    /// Creates an in-memory user-client context only after the Python component
    /// revalidates this run's identity, readiness, and protected control credential.
    /// </summary>
    public Task<PortableLauncherUserClientContext> CreateLauncherUserClientContextAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lease.EnsureHeld();
        return CreateLauncherUserClientContextCoreAsync(cancellationToken);
    }

    private async Task<PortableLauncherUserClientContext> CreateLauncherUserClientContextCoreAsync(
        CancellationToken cancellationToken)
    {
        if (!await _recoveryGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("实例维护事务正在进行，暂不能建立业务用户会话。");
        }
        try
        {
            _lease.EnsureHeld();
            return await _python.CreateLauncherUserClientContextAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    public async Task<PythonReadinessResult> RefreshReadinessAsync(CancellationToken cancellationToken = default)
    {
        if (!await _recoveryGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("实例维护事务正在进行，暂不能刷新 Web Ready 状态。");
        }
        try
        {
            var readiness = await _python.RefreshReadinessAsync(cancellationToken).ConfigureAwait(false);
            if (readiness.State is PythonReadinessState.Ready && readiness.SetupRequired is not true)
            {
                _setupToken = null;
            }

            return readiness;
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    public static bool HasPersistentInstance(PortableBundleDescriptor bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var active = new PortableInstanceCatalog(bundle.BundleRoot).ResolveActiveInstance();
        if (active.InstanceId is not null) return true;
        var dataRoot = active.DataRoot;
        if (!Directory.Exists(dataRoot))
        {
            return false;
        }

        var attributes = File.GetAttributes(dataRoot);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new PythonLifecycleException("实例数据根不能是重解析点。");
        }

        try
        {
            // Inactive restore candidates are not an active legacy instance.
            return Directory.EnumerateFileSystemEntries(dataRoot).Any(entry =>
                !string.Equals(Path.GetFileName(entry), "instances", StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PythonLifecycleException("无法只读检查既有实例数据根。", exception);
        }
    }

    public static RealPortableStackHost Create(PortableBundleDescriptor bundle)
    {
        var active = new PortableInstanceCatalog(bundle.BundleRoot).ResolveActiveInstance();
        return CreateCore(bundle, existingRecoveryOnly: false, active);
    }

    public static RealPortableStackHost OpenExistingForRecovery(PortableBundleDescriptor bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var active = new PortableInstanceCatalog(bundle.BundleRoot).ResolveActiveInstance();
        var dataRoot = active.DataRoot;
        var lockPath = Path.Combine(dataRoot, InstanceDataRootLease.LockFileName);
        if (!Directory.Exists(dataRoot) || !File.Exists(lockPath))
        {
            throw new PythonLifecycleException("未找到可恢复的持久实例锁元数据；恢复流程不会创建新实例。");
        }

        return CreateCore(bundle, existingRecoveryOnly: true, active);
    }

    public static PortableRestoreSession CreateBusinessRestoreSession(PortableBundleDescriptor bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return new PortableRestoreSession(
            new PortableInstanceCatalog(bundle.BundleRoot),
            new WindowsPortableRestoreExecutor(bundle));
    }

    public async Task<LauncherOperationResult> TryRecoverExistingAsync(CancellationToken cancellationToken = default)
    {
        if (!await _recoveryGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("已有恢复核验正在进行，不能并发覆盖控制上下文。");
        }

        try
        {
            var initialStates = await ObserveComponentStatesAsync(cancellationToken).ConfigureAwait(false);
            if (initialStates.All(state => state is ComponentRuntimeState.Stopped))
            {
                if (_initialization.IsAllocated)
                {
                    var allocated = await _coordinator.RecognizeStoppedExistingAsync(
                        "实例仅完成分配，数据库初始化尚未开始；等待用户手动启动。", cancellationToken).ConfigureAwait(false);
                    return allocated.ErrorCode == "EXISTING_INSTANCE_STOPPED"
                        ? allocated with { ErrorCode = "INSTANCE_NOT_INITIALIZED" } : allocated;
                }
                // An all-stopped process snapshot does not prove that an interrupted
                // first provision left a startable instance behind.
                _provision.VerifyCompletedForRecovery();
                return await _coordinator.RecognizeStoppedExistingAsync(
                    "既有实例已确认全部停止；未执行自动启动，可释放本次恢复上下文后等待用户手动启动。",
                    cancellationToken).ConfigureAwait(false);
            }

            string? recoveryErrorCode = null;
            string? recoveryFailure = null;
            var postgres = await _postgres.TryReconnectAsync(cancellationToken).ConfigureAwait(false);
            if (!postgres.Succeeded || postgres.Snapshot.State is not OwnedProcessState.Running)
            {
                recoveryErrorCode = postgres.ErrorCode ?? "POSTGRES_RECOVERY_FAILED";
                recoveryFailure = "无法按持久 PID、启动时间、可执行文件、实例与集群身份恢复 PostgreSQL。";
            }
            else if (await _postgres.CheckTransportAsync(cancellationToken).ConfigureAwait(false) is not PostgresTransportState.AcceptingConnections)
            {
                recoveryErrorCode = "POSTGRES_TRANSPORT_NOT_READY";
                recoveryFailure = "PostgreSQL 进程归属已核验，但固定本机端口未确认可接受连接。";
            }
            else
            {
                VerifyProvisionForRecovery();
                var python = await _python.TryReconnectAsync(cancellationToken).ConfigureAwait(false);
                if (!python.Succeeded || python.Snapshot.State is not OwnedProcessState.Running)
                {
                    recoveryErrorCode = python.ErrorCode ?? "PYTHON_RECOVERY_FAILED";
                    recoveryFailure = "Python Web 服务未同时通过持久进程身份、DPAPI 控制凭据与 Ready 契约核验。";
                }
                else if (_python.LastReadiness?.State is not PythonReadinessState.Ready ||
                    await _python.GetStateAsync(cancellationToken).ConfigureAwait(false) is not ComponentRuntimeState.Running)
                {
                    recoveryErrorCode = "PYTHON_RECOVERY_NOT_READY";
                    recoveryFailure = "Python Web 服务身份已附加，但未确认同一进程达到 Ready，拒绝接管。";
                }
                else
                {
                    _setupToken = _python.SetupToken;
                }
            }
            if (recoveryFailure is not null)
            {
                return await _coordinator.RefuseRecoveryAsync(
                    recoveryErrorCode ?? "RECOVERY_VALIDATION_FAILED",
                    $"恢复被拒绝：{recoveryFailure} 未启动、停止或自动准备任何服务。",
                    cancellationToken).ConfigureAwait(false);
            }

            var adoption = await _coordinator.AdoptAlreadyRunningAsync(cancellationToken).ConfigureAwait(false);
            if (!adoption.Succeeded)
            {
                return adoption;
            }

            _setupToken = _python.SetupToken;
            if (_webPortSettings.Applying is { } intent && _webPort == intent.CandidatePort)
            {
                // The user had already confirmed this exact attempt before the
                // previous Launcher process exited. Commit only after passive
                // recovery proved the same process and Ready contract.
                var commitFailure = await PortableWebPortRecoveryCommitGuard.CommitOrStopCandidateAsync(
                    () => _webPortSettings = _webPortSettingsStore.CommitApply(
                        _lease, intent.AttemptId, intent.FromPort, _postgresPort),
                    () => _coordinator.StopAsync(CancellationToken.None),
                    () => _webPortApplyUnresolved = true,
                    () => _coordinator.Snapshot).ConfigureAwait(false);
                if (commitFailure is not null)
                {
                    return commitFailure;
                }
            }
            return adoption with { Message = "已恢复原有 PostgreSQL 与 Python 进程；未创建替代服务。" };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is PostgresLifecycleException or PythonLifecycleException or
            IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var safeDiagnosticCode = (exception as ILauncherSafeFailure)?.SafeDiagnosticCode;
            var recoveryMessage = safeDiagnosticCode is { } safeCode
                ? LauncherSafeFailureMessages.For(safeCode)
                : "恢复核验无法确认数据或进程状态。现有数据已保留，未启动、停止或自动准备服务；请导出诊断摘要并寻求支持。";
            var diagnosticComponent = safeDiagnosticCode switch
            {
                LauncherDiagnosticCode.PostgresInitInterrupted or
                LauncherDiagnosticCode.PostgresMarkerMissing or
                LauncherDiagnosticCode.PostgresDataUnverified => LauncherDiagnosticComponent.Postgres,
                LauncherDiagnosticCode.DatabaseProvisionInterrupted => LauncherDiagnosticComponent.DatabaseProvision,
                _ => (LauncherDiagnosticComponent?)null,
            };
            return await _coordinator.RefuseRecoveryAsync(
                "RECOVERY_VALIDATION_FAILED",
                recoveryMessage,
                CancellationToken.None,
                diagnosticComponent is null ? null : safeDiagnosticCode,
                diagnosticComponent).ConfigureAwait(false);
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private void VerifyProvisionForRecovery()
    {
        try
        {
            _provision.VerifyCompletedForRecovery();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new PythonLifecycleException(
                "业务数据库准备记录或关联状态无法核验；现有数据已保留，拒绝接管。",
                exception,
                LauncherDiagnosticCode.DatabaseProvisionInterrupted);
        }
    }

    // Compatibility entry; all caller types now use the same start policy.
    public Task<LauncherOperationResult> StartStoppedExistingAsync(CancellationToken cancellationToken = default) =>
        StartAsync(cancellationToken);

    public async Task<LauncherOperationResult> StartAsync(CancellationToken cancellationToken = default) =>
        (await Runtime.StartAsync(cancellationToken).ConfigureAwait(false)).Operation;

    private async Task<LauncherStartResult> RunStartupAsync(
        Func<ILauncherStartSession, Task<LauncherStartResult>> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _recoveryGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("已有实例维护事务正在进行，不能启动服务。");
        try
        {
            _lease.EnsureHeld();
            return await _coordinator.RunMaintenanceAsync(async maintenance =>
            {
                var current = _webPortSettingsStore.Read(_lease, _webPortSettings.EffectivePort, _postgresPort);
                if (_webPortApplyUnresolved || current.Applying is not null || current.EffectivePort != _webPort)
                    throw new InvalidOperationException("Web 端口事务或有效配置未通过核验；拒绝启动。");
                _webPortSettings = current;
                // Policy, bind check, initialization, replacement and Ready all
                // share the existing locks. No UI callback or nested gate.
                return await action(new StartSession(this, maintenance, current)).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _recoveryGate.Release(); }
    }

    private sealed class StartSession(RealPortableStackHost host,
        LauncherCoordinator.MaintenanceSession maintenance, PortableWebPortSettings settings) : ILauncherStartSession
    {
        public LauncherStartSettings Settings { get; } =
            new(settings.EffectivePort, settings.Automatic, settings.PendingPort is not null);

        public bool CanBindEffectivePort() => CanBindWebPort(settings.EffectivePort);

        public async Task<LauncherStartResult> StartCurrentAsync(CancellationToken cancellationToken)
        {
            var result = await maintenance.StartAsync(cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                try
                {
                    await host.EnsureHostReadyOnPortAsync(settings.EffectivePort, true, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Ready/identity changed after orchestration. Never report success;
                    // normal ownership-aware stop retains unsafe dependencies.
                    await maintenance.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
            return new(result, host._webPort, false, result.Succeeded && host.SetupRequired);
        }

        public async Task<LauncherStartResult> StartOnAutomaticPortAsync(CancellationToken cancellationToken)
        {
            try
            {
                var applied = await host.ApplyWebPortInMaintenanceAsync(
                    maintenance, settings.Revision, true, cancellationToken, bindingConflictConfirmed: true).ConfigureAwait(false);
                var operation = new LauncherOperationResult(applied.Applied, false,
                    applied.Applied ? null : "AUTO_PORT_START_FAILED", applied.Message, host._coordinator.Snapshot);
                return new(operation, applied.EffectivePort, applied.Applied, applied.Applied && host.SetupRequired);
            }
            catch (OperationCanceledException)
            {
                // The transaction rethrows cancellation only AFTER confirmed
                // rollback; unconfirmed rollback throws an apply exception.
                return new(new LauncherOperationResult(false, true, "START_CANCELLED",
                    "启动已取消，端口事务已安全回滚。", host._coordinator.Snapshot),
                    host._webPort, false, false);
            }
        }
    }

    /// <summary>Passive exclusive bind probe; never connects to or controls an existing listener.</summary>
    public static bool CanBindWebPort(int port)
    {
        try { EnsureLoopbackPortCanBind(port); return true; }
        catch (PythonLifecycleException error) when (error.InnerException is SocketException) { return false; }
    }

    public PortableWebPortSettings StageWebPort(long expectedRevision, int candidatePort, bool automatic = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_recoveryGate.Wait(0))
        {
            throw new InvalidOperationException("已有实例维护事务正在进行，不能暂存 Web 端口。");
        }

        try
        {
            _lease.EnsureHeld();
            var current = _webPortSettingsStore.Read(_lease, _webPortSettings.EffectivePort, _postgresPort);
            var staged = _webPortSettingsStore.Stage(
                _lease, expectedRevision, candidatePort, current.EffectivePort, _postgresPort, automatic);
            _webPortSettings = staged;
            return staged;
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    /// <summary>Applies a port in a Host/Coordinator-owned maintenance transaction; no UI callbacks.</summary>
    public Task<PortableWebPortApplyResult> ApplyPendingWebPortAsync(
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        ApplyWebPortCoreAsync(expectedRevision, false, cancellationToken);

    public Task<PortableWebPortApplyResult> ApplyAutomaticWebPortAsync(
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        ApplyWebPortCoreAsync(expectedRevision, true, cancellationToken);

    private async Task<PortableWebPortApplyResult> ApplyWebPortCoreAsync(
        long expectedRevision, bool automaticSelection, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _recoveryGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("已有实例维护事务正在进行，不能应用 Web 端口。");
        try
        {
            _lease.EnsureHeld();
            return await _coordinator.RunMaintenanceAsync(
                maintenance => ApplyWebPortInMaintenanceAsync(maintenance, expectedRevision, automaticSelection, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally { _recoveryGate.Release(); }
    }

    private async Task<PortableWebPortApplyResult> ApplyWebPortInMaintenanceAsync(
        LauncherCoordinator.MaintenanceSession maintenance, long expectedRevision,
        bool automaticSelection, CancellationToken cancellationToken, bool bindingConflictConfirmed = false)
    {
        if (_webPortApplyUnresolved)
            throw new InvalidOperationException("端口事务未完成核验；拒绝再次切换。");

        var current = _webPortSettingsStore.Read(_lease, _webPortSettings.EffectivePort, _postgresPort);
        if (automaticSelection)
        {
            if (!current.Automatic || current.Revision != expectedRevision ||
                current.Applying is not null || current.PendingPort is not null)
                throw new PortableWebPortSettingsException("自动端口模式或事务状态已变化；拒绝自动切换。");
            if (!bindingConflictConfirmed) try
            {
                EnsureLoopbackPortCanBind(current.EffectivePort);
                return new PortableWebPortApplyResult(false, current.EffectivePort, null,
                    "原端口仍可用，无需自动切换；请启动服务。");
            }
            catch (PythonLifecycleException error) when (error.InnerException is SocketException)
            {
                // Only a binding conflict permits automatic selection, never a generic startup failure.
            }
            var selected = SelectAvailableWebPort(current.EffectivePort, _postgresPort);
            current = _webPortSettingsStore.Stage(_lease, expectedRevision, selected,
                current.EffectivePort, _postgresPort, automatic: true);
            expectedRevision = current.Revision;
            _webPortSettings = current;
            AutomaticPortStagedForAcceptance?.Invoke();
        }
        if (current.Revision != expectedRevision || current.PendingPort is not { } candidatePort || current.Applying is not null)
        {
            throw new PortableWebPortSettingsException("Web 端口待处理状态已变化；刷新后重试。");
        }
        if (_webPort != current.EffectivePort)
        {
            throw new PortableWebPortSettingsException("Host 当前端口与 effective 配置不一致；拒绝应用。");
        }

        var intentSettings = _webPortSettingsStore.BeginApply(
            _lease, expectedRevision, current.EffectivePort, _postgresPort);
        _webPortSettings = intentSettings;
        var intent = intentSettings.Applying
            ?? throw new PortableWebPortSettingsException("端口切换 intent 未能持久化。");

        try
        {
            // Once Stage has persisted a candidate, cancellation must run through
            // the durable intent rollback below, never escape with PendingPort.
            cancellationToken.ThrowIfCancellationRequested();
            EnsureLoopbackPortCanBind(candidatePort);
            await ReplacePythonComponentAsync(maintenance, candidatePort).ConfigureAwait(false);
            var candidateStart = await maintenance.StartAsync(cancellationToken).ConfigureAwait(false);
            if (candidateStart.Cancelled) throw new OperationCanceledException(cancellationToken);
            await EnsureHostReadyOnPortAsync(candidatePort, candidateStart.Succeeded, cancellationToken).ConfigureAwait(false);
            _webPortSettings = _webPortSettingsStore.CommitApply(
                _lease, intent.AttemptId, intent.FromPort, _postgresPort);
            _webPortApplyUnresolved = false;
            return new PortableWebPortApplyResult(true, candidatePort, null,
                "候选 Web 端口已由正常 Host 启动并通过身份与 Ready 核验。");
        }
        catch (Exception candidateFailure) when (candidateFailure is not OperationCanceledException)
        {
            try
            {
                await RestorePreviousWebPortAsync(maintenance, intent, cancellationToken, start: !automaticSelection).ConfigureAwait(false);
                _webPortSettings = _webPortSettingsStore.CompleteRollback(
                    _lease, intent.AttemptId, intent.FromPort, _postgresPort);
                if (automaticSelection)
                    _webPortSettings = _webPortSettingsStore.Stage(_lease, _webPortSettings.Revision,
                        intent.FromPort, intent.FromPort, _postgresPort, automatic: true);
                _webPortApplyUnresolved = false;
                return new PortableWebPortApplyResult(false, intent.FromPort, automaticSelection ? null : intent.CandidatePort,
                    automaticSelection
                        ? "自动端口启动未完成；本次服务已安全停止，原有效端口保留。请查看诊断后重试。"
                        : $"候选端口未应用；旧端口已恢复并通过 Ready 核验。{candidateFailure.Message}");
            }
            catch (Exception rollbackFailure)
            {
                _webPortApplyUnresolved = true;
                throw new PortableWebPortApplyException(
                    "Web 端口候选失败且旧 Host 未能验证恢复；事务 intent 与实例 lease 已保留，禁止继续启动或释放实例。",
                    candidateFailure, rollbackFailure);
            }
        }
        catch (OperationCanceledException candidateCancellation)
        {
            try
            {
                await RestorePreviousWebPortAsync(maintenance, intent, CancellationToken.None, start: !automaticSelection).ConfigureAwait(false);
                _webPortSettings = _webPortSettingsStore.CompleteRollback(
                    _lease, intent.AttemptId, intent.FromPort, _postgresPort);
                if (automaticSelection)
                    _webPortSettings = _webPortSettingsStore.Stage(_lease, _webPortSettings.Revision,
                        intent.FromPort, intent.FromPort, _postgresPort, automatic: true);
                _webPortApplyUnresolved = false;
            }
            catch (Exception rollbackFailure)
            {
                _webPortApplyUnresolved = true;
                throw new PortableWebPortApplyException(
                    "端口切换取消后无法确认旧 Host 恢复；事务 intent 与实例 lease 已保留。",
                    candidateCancellation, rollbackFailure);
            }

            throw;
        }
    }



    /// <summary>
    /// Explicit business backup. Gracefully drains Web/workers, keeps owned PG
    /// alive for the logical dump, then stops PG. The caller may manually start
    /// the stopped instance after the backup result is inspected.
    /// </summary>
    public async Task<PortableBusinessBackupResult> CreateBusinessBackupAndStopAsync(
        string destination,
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        if (!await _recoveryGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("已有实例恢复或备份操作正在进行。");
        }
        try
        {
            _lease.EnsureHeld();
            PortableBusinessBackupRunner.ValidateDestination(DataRoot, destination);
            if (string.IsNullOrEmpty(passphrase) || Encoding.UTF8.GetByteCount(passphrase) is < 12 or > 1024)
            {
                throw new InvalidOperationException("备份口令必须包含 12 到 1024 个 UTF-8 字节。");
            }
            var identity = _postgres.ProcessSnapshot.Identity
                ?? throw new InvalidOperationException("PostgreSQL 未持有可核验的进程身份。");
            var secrets = new WindowsInstanceSecretsStore().Load(_lease);

            async Task VerifyQuiesced(CancellationToken token)
            {
                _lease.EnsureHeld();
                _provision.VerifyCompletedForRecovery();
                if (await _python.GetStateAsync(token).ConfigureAwait(false) is not ComponentRuntimeState.Stopped ||
                    !await _provision.IsQuiescentAsync(token).ConfigureAwait(false) ||
                    !await _postgres.IsInitializationQuiescentAsync(token).ConfigureAwait(false) ||
                    await _postgres.GetStateAsync(token).ConfigureAwait(false) is not ComponentRuntimeState.Running ||
                    _postgres.ProcessSnapshot.Identity != identity ||
                    await _postgres.CheckTransportAsync(token).ConfigureAwait(false) is not PostgresTransportState.AcceptingConnections)
                {
                    throw new InvalidOperationException("实例锁、停写状态或数据库归属复核失败，拒绝发布备份。");
                }
            }

            return await _coordinator.RunQuiescedBackupAndStopAsync(
                "postgres", "python-web",
                token => PortableBusinessBackupRunner.RunAsync(
                Bundle, _lease, secrets, _postgresPort, destination, passphrase, VerifyQuiesced, token),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private static RealPortableStackHost CreateCore(
        PortableBundleDescriptor bundle,
        bool existingRecoveryOnly,
        PortableActiveInstance active)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var dataRoot = active.DataRoot;
        var cacheRoot = Path.Combine(dataRoot, "cache");
        var temporaryRoot = Path.Combine(cacheRoot, "temp");
        var logRoot = Path.Combine(dataRoot, "logs");
        var pgData = Path.Combine(dataRoot, "postgres", "cluster");
        var lease = InstanceDataRootLease.Acquire(dataRoot);
        try
        {
            var catalog = new PortableInstanceCatalog(bundle.BundleRoot);
            var expectedRevision = active.PointerRevision ?? "legacy-data-root";
            if (!string.Equals(catalog.ReadCurrentPointerRevision(), expectedRevision, StringComparison.Ordinal))
            {
                throw new PythonLifecycleException("活动实例在取得 lease 前发生变化；拒绝启动过期目标。");
            }

            if (active.InstanceId is { } expectedId && lease.InstanceId != expectedId)
            {
                throw new PythonLifecycleException("活动实例指针与已取得 lease 身份不匹配；拒绝启动服务。");
            }

            var webPortSettingsStore = new PortableWebPortSettingsStore();
            var webPortSettings = webPortSettingsStore.Read(lease, active.WebPort, active.PostgresPort);
            var webPort = webPortSettings.EffectivePort;
            if (!existingRecoveryOnly)
            {
                EnsureNewHostCreationAllowed(webPortSettings);
            }
            if (webPortSettings.Applying is not null)
            {
                if (!existingRecoveryOnly)
                {
                    throw new PythonLifecycleException("检测到未决 Web 端口切换 intent；拒绝创建 Host。");
                }

                (webPortSettings, webPort) = ResolveWebPortApplyRecoveryAndReleaseLeaseOnFailure(
                    lease, bundle, webPortSettingsStore, webPortSettings, active.PostgresPort);
            }

            var initialization = new PortableInitializationState(lease);
            var allocated = initialization.IsAllocated;
            var secretStore = new WindowsInstanceSecretsStore();
            var secretPath = secretStore.GetSecretsPath(lease);
            var allocateNew = !existingRecoveryOnly && !File.Exists(secretPath);
            // Never repair missing credentials or bless partial database state.
            // Validate before creating cache/config directories or new secrets.
            if (allocateNew) initialization.EnsureCanAllocate();
            if (existingRecoveryOnly)
            {
                foreach (var (path, name) in new[]
                {
                    (cacheRoot, "缓存目录"),
                    (temporaryRoot, "临时目录"),
                    (logRoot, "日志目录"),
                    (pgData, "PostgreSQL 数据目录"),
                })
                {
                    if (allocated && path == pgData) continue;
                    PythonPathGuard.EnsureSafeInstancePath(lease, path, allowMissingLeaf: false);
                    if (!Directory.Exists(path))
                    {
                        throw new PythonLifecycleException($"既有实例{name}缺失；恢复流程不会创建或修复目录。");
                    }
                }
            }
            else
            {
                PrepareInstanceDirectory(lease, cacheRoot, "缓存目录");
                PrepareInstanceDirectory(lease, temporaryRoot, "临时目录");
                PrepareInstanceDirectory(lease, logRoot, "日志目录");
            }

            var markerPath = Path.Combine(pgData, ".aigoofish-cluster.json");
            var newCluster = !File.Exists(markerPath);
            // Only genuinely new instances opt in; missing settings on existing instances remain fixed.
            if (!existingRecoveryOnly && !File.Exists(secretPath) && !Directory.Exists(pgData) && webPortSettings.Revision == 0)
                webPortSettings = webPortSettingsStore.Stage(lease, 0, webPort, webPort,
                    active.PostgresPort, automatic: true);
            var secrets = existingRecoveryOnly
                ? secretStore.Load(lease)
                : File.Exists(secretPath)
                    ? secretStore.Load(lease)
                    : secretStore.CreateNew(lease, pgData);
            if (allocateNew) initialization.RecordAllocation();
            var postgres = new WindowsPostgresComponent(
                lease,
                secrets,
                new WindowsPostgresOptions(
                    bundle.PostgresRoot,
                    pgData,
                    active.PostgresPort,
                    "17.11",
                    TimeSpan.FromMinutes(2),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(30)));
            var bootstrap = Path.Combine(bundle.ProgramRoot, "scripts", "portable", "python-bootstrap.py");
            var provision = new WindowsPortableProvisionStep(
                lease,
                secrets,
                bundle.PythonExecutable,
                bootstrap,
                bundle.ProgramRoot,
                dataRoot,
                temporaryRoot,
                pgData,
                active.PostgresPort,
                newCluster,
                TimeSpan.FromMinutes(2));
            // A stopped existing instance may still need its first administrator.
            // The fresh token is used only for a new Web process; reconnecting a
            // live process replaces it with the token from verified credentials.
            var setupToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var endpoints = new PythonDatabaseEndpoints(
                Dsn("aigoofish_app", secrets.ApplicationDatabasePassword, active.PostgresPort),
                Dsn("aigoofish_probe", secrets.ProbeDatabasePassword, active.PostgresPort),
                setupToken);
            var python = new WindowsPythonComponent(
                lease,
                secrets,
                new WindowsPythonOptions(
                    bundle.PythonExecutable,
                    bootstrap,
                    bundle.ProgramRoot,
                    dataRoot,
                    cacheRoot,
                    bundle.BrowserRoot,
                    webPort,
                    bundle.AppVersion,
                    PythonServiceMode.Normal,
                    bundle.SchemaMinimum,
                    bundle.SchemaMaximum,
                    TimeSpan.FromSeconds(45),
                    TimeSpan.FromSeconds(4),
                    TimeSpan.FromSeconds(30)),
                endpoints);
            return new RealPortableStackHost(
                bundle, lease, postgres, provision, secrets, cacheRoot, python,
                active.PostgresPort, webPort, webPortSettings, webPortSettingsStore)
            {
                SetupToken = setupToken,
            };
        }
        catch
        {
            if (lease.IsHeld)
            {
                lease.Dispose();
            }
            throw;
        }
    }

    internal static void EnsureNewHostCreationAllowed(PortableWebPortSettings settings)
    {
        if (settings.Applying is not null)
        {
            throw new PythonLifecycleException("检测到未决 Web 端口切换 intent；拒绝创建 Host。");
        }
    }

    internal static (PortableWebPortSettings Settings, int WebPort) ResolveWebPortApplyRecoveryAndReleaseLeaseOnFailure(
        InstanceDataRootLease lease,
        PortableBundleDescriptor bundle,
        PortableWebPortSettingsStore store,
        PortableWebPortSettings settings,
        int postgresPort,
        Func<string, int, PythonRuntimeRecord>? runtimeReader = null,
        Func<OwnedProcessIdentity, PythonIdentityRunState>? processProbe = null)
    {
        try
        {
            return ResolveWebPortApplyRecovery(
                lease, bundle, store, settings, postgresPort, runtimeReader, processProbe);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (lease.IsHeld)
            {
                lease.Dispose();
            }

            throw new PythonLifecycleException(
                "未决 Web 端口切换 intent 已保留；候选身份分类未完成，本次分类 lease 已释放。后续 Host 启动、端口设置和旧实例切换入口仍会拒绝操作，且不会启动或停止进程。",
                exception);
        }
    }

    private static (PortableWebPortSettings Settings, int WebPort) ResolveWebPortApplyRecovery(
        InstanceDataRootLease lease,
        PortableBundleDescriptor bundle,
        PortableWebPortSettingsStore store,
        PortableWebPortSettings settings,
        int postgresPort,
        Func<string, int, PythonRuntimeRecord>? runtimeReader,
        Func<OwnedProcessIdentity, PythonIdentityRunState>? processProbe)
    {
        var intent = settings.Applying
            ?? throw new PythonLifecycleException("Web 端口恢复 intent 缺失。");
        var runtimePath = Path.Combine(lease.InstanceRoot, "config", "python-runtime.json");
        PortableInstanceCatalog.EnsureSafePath(lease.InstanceRoot, runtimePath, allowMissingLeaf: false);
        var runtime = (runtimeReader ?? PythonStateFile.Read<PythonRuntimeRecord>)(runtimePath, 16 * 1024);
        var identity = runtime.Identity;
        if (runtime.FormatVersion != 2 || runtime.InstanceId != lease.InstanceId ||
            runtime.Port != intent.CandidatePort || runtime.Mode != "normal" ||
            !string.Equals(runtime.DataRoot, lease.InstanceRoot, StringComparison.OrdinalIgnoreCase) ||
            identity is null || identity.InstanceId != lease.InstanceId ||
            !string.Equals(identity.InstanceRoot, lease.InstanceRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identity.ExecutablePath, bundle.PythonExecutable, StringComparison.OrdinalIgnoreCase))
        {
            throw new PythonLifecycleException("候选 Web 运行记录无法绑定到未决端口 intent 的实例/进程身份。");
        }

        var runState = (processProbe ?? PythonProcessIdentityProbe.GetRunState)(identity);
        if (runState is PythonIdentityRunState.Running)
        {
            // CreateCore will use the candidate port. TryRecoverExistingAsync
            // must still validate DPAPI credentials and the Ready contract.
            return (settings, intent.CandidatePort);
        }
        if (runState is PythonIdentityRunState.NotRunning)
        {
            // The exact candidate process is proven absent. Keep the staged
            // value for a fresh explicit attempt and return to effective.
            var rolledBack = store.CompleteRollback(lease, intent.AttemptId, intent.FromPort, postgresPort);
            return (rolledBack, rolledBack.EffectivePort);
        }

        throw new PythonLifecycleException("未决 Web 端口 intent 的候选进程身份状态未知。");
    }

    private WindowsPythonComponent CreatePythonComponent(int port)
    {
        var bootstrap = Path.Combine(Bundle.ProgramRoot, "scripts", "portable", "python-bootstrap.py");
        var endpoints = new PythonDatabaseEndpoints(
            Dsn("aigoofish_app", _secrets.ApplicationDatabasePassword, _postgresPort),
            Dsn("aigoofish_probe", _secrets.ProbeDatabasePassword, _postgresPort),
            _setupToken);
        return new WindowsPythonComponent(
            _lease,
            _secrets,
            new WindowsPythonOptions(
                Bundle.PythonExecutable,
                bootstrap,
                Bundle.ProgramRoot,
                DataRoot,
                _cacheRoot,
                Bundle.BrowserRoot,
                port,
                Bundle.AppVersion,
                PythonServiceMode.Normal,
                Bundle.SchemaMinimum,
                Bundle.SchemaMaximum,
                TimeSpan.FromSeconds(45),
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(30)),
            endpoints);
    }

    private LauncherCoordinator CreateCoordinator(
        WindowsPostgresComponent postgres,
        WindowsPortableProvisionStep provision,
        WindowsPythonComponent python) => new(
        new[]
        {
            new LauncherComponentRegistration(postgres, StartupCancellationIsSafe: false),
            new LauncherComponentRegistration(python, StartupCancellationIsSafe: true),
        },
        isSimulation: false,
        logCapacity: 500,
        startupSteps: [
            new(new WindowsPostgresInitializationStep(postgres, PrepareDatabaseStart), postgres.Id, CancellationIsSafe: false),
            new(provision, python.Id, CancellationIsSafe: false),
        ]);

    private void PrepareDatabaseStart()
    {
        if (_initialization.IsAllocated)
        {
            PostgresPathGuard.EnsureNewInstancePathLength(_lease.InstanceRoot);
            _initialization.BeginInitialization();
        }
        else
        {
            // Existing completion records remain authoritative, including for
            // legacy installations which predate the allocation journal.
            _provision.VerifyCompletedForRecovery();
            var marker = Path.Combine(_lease.InstanceRoot, "postgres", "cluster", ".aigoofish-cluster.json");
            PostgresPathGuard.EnsureSafePath(_lease, marker, allowMissingLeaf: true);
            if (!File.Exists(marker))
                throw new PostgresLifecycleException("既有实例缺少集群标记；拒绝重新初始化数据库。",
                    LauncherDiagnosticCode.PostgresMarkerMissing);
        }
    }

    private async Task ReplacePythonComponentAsync(LauncherCoordinator.MaintenanceSession maintenance, int port)
    {
        _lease.EnsureHeld();
        var replacement = CreatePythonComponent(port);
        var previous = _python;
        try
        {
            await maintenance.ReplaceStoppedComponentAsync(previous, replacement).ConfigureAwait(false);
        }
        catch
        {
            await replacement.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _python = replacement;
        _webPort = port;
        await previous.DisposeAsync().ConfigureAwait(false);
    }

    private async Task EnsureHostReadyOnPortAsync(
        int expectedPort,
        bool coordinatorSucceeded,
        CancellationToken cancellationToken)
    {
        _lease.EnsureHeld();
        if (!coordinatorSucceeded || _webPort != expectedPort ||
            _python.ProcessSnapshot.Identity is not { } identity ||
            identity.InstanceId != _lease.InstanceId ||
            !string.Equals(identity.InstanceRoot, _lease.InstanceRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new PythonLifecycleException("Host 启动结果或进程身份与候选 Web 端口不匹配。");
        }

        var readiness = await _python.RefreshReadinessAsync(cancellationToken).ConfigureAwait(false);
        var state = await _python.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (readiness.State is not PythonReadinessState.Ready || state is not ComponentRuntimeState.Running ||
            _python.ProcessSnapshot.Identity != identity ||
            _postgres.ProcessSnapshot.Identity is not { } postgresIdentity ||
            postgresIdentity.InstanceId != _lease.InstanceId ||
            await _postgres.CheckTransportAsync(cancellationToken).ConfigureAwait(false) is not PostgresTransportState.AcceptingConnections)
        {
            throw new PythonLifecycleException("候选 Host 未通过同一进程身份、Web Ready 与原 PostgreSQL 连接核验。");
        }

        _setupToken = _python.SetupToken ?? _setupToken;
    }

    private async Task RestorePreviousWebPortAsync(
        LauncherCoordinator.MaintenanceSession maintenance,
        PortableWebPortApplyIntent intent,
        CancellationToken cancellationToken,
        bool start = true)
    {
        var states = await ObserveComponentStatesAsync(cancellationToken).ConfigureAwait(false);
        if (states.Any(state => state is not ComponentRuntimeState.Stopped))
        {
            var stop = await maintenance.StopAsync(cancellationToken).ConfigureAwait(false);
            if (!stop.Succeeded)
            {
                throw new PythonLifecycleException("候选 Host 未能按正常生命周期停止；保留事务 intent。");
            }
        }

        states = await ObserveComponentStatesAsync(CancellationToken.None).ConfigureAwait(false);
        if (states.Any(state => state is not ComponentRuntimeState.Stopped))
        {
            throw new PythonLifecycleException("候选 Host 组件未全部确认停止；不启动旧 Host，也不释放 lease。");
        }

        if (_webPort != intent.FromPort)
        {
            await ReplacePythonComponentAsync(maintenance, intent.FromPort).ConfigureAwait(false);
        }

        if (!start) return;
        var restored = await maintenance.StartAsync(cancellationToken).ConfigureAwait(false);
        await EnsureHostReadyOnPortAsync(intent.FromPort, restored.Succeeded, cancellationToken).ConfigureAwait(false);
    }

    internal static int SelectAvailableWebPort(int previousPort, int postgresPort)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Server.ExclusiveAddressUse = true;
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                if (port is >= 1024 and <= 65535 && port != previousPort && port != postgresPort)
                    return port;
            }
            catch (SocketException) { /* Bounded retries; no connection to another listener. */ }
        }
        throw new PythonLifecycleException("无法选择可用的本机 Web 端口；未启动服务。请稍后重试或设置固定端口。");
    }

    private static void EnsureLoopbackPortCanBind(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
        }
        catch (SocketException exception)
        {
            throw new PythonLifecycleException("候选 Web 端口已被占用或不可绑定；没有连接、接管或停止现有监听者。", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _recoveryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            if (_webPortApplyUnresolved)
            {
                throw new InvalidOperationException("Web 端口切换未通过恢复核验；保留实例 lease 和事务 intent。");
            }

            var observedStopped = await AreAllComponentsStoppedAsync().ConfigureAwait(false);
            if (!observedStopped)
            {
                throw new InvalidOperationException("真实组件尚未确认停止，拒绝释放实例锁和控制上下文。");
            }

            _disposed = true;
            _coordinator.Dispose();
            await _python.DisposeAsync().ConfigureAwait(false);
            await _postgres.DisposeAsync().ConfigureAwait(false);
            _lease.Dispose();
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private async Task<ComponentRuntimeState[]> ObserveComponentStatesAsync(CancellationToken cancellationToken)
    {
        var result = new ComponentRuntimeState[4];
        result[0] = await _postgres.GetStateAsync(cancellationToken).ConfigureAwait(false);
        result[1] = await _provision.IsQuiescentAsync(cancellationToken).ConfigureAwait(false)
            ? ComponentRuntimeState.Stopped : ComponentRuntimeState.Unknown;
        result[2] = await _python.GetStateAsync(cancellationToken).ConfigureAwait(false);
        result[3] = await _postgres.IsInitializationQuiescentAsync(cancellationToken).ConfigureAwait(false)
            ? ComponentRuntimeState.Stopped : ComponentRuntimeState.Unknown;
        return result;
    }

    private async Task<bool> AreAllComponentsStoppedAsync()
    {
        if (!await _provision.IsQuiescentAsync(CancellationToken.None).ConfigureAwait(false) ||
            !await _postgres.IsInitializationQuiescentAsync(CancellationToken.None).ConfigureAwait(false)) return false;
        foreach (var component in new ILauncherComponent[] { _postgres, _python })
        {
            if (await component.GetStateAsync(CancellationToken.None).ConfigureAwait(false) is not ComponentRuntimeState.Stopped)
            {
                return false;
            }
        }

        return true;
    }

    private static void PrepareInstanceDirectory(InstanceDataRootLease lease, string path, string name)
    {
        PythonPathGuard.EnsureSafeInstancePath(lease, path, allowMissingLeaf: true);
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PythonLifecycleException($"无法准备{name}。", exception);
        }

        PythonPathGuard.EnsureSafeInstancePath(lease, path, allowMissingLeaf: false);
    }

    private static string Dsn(string role, string password, int port)
    {
        return $"postgresql://{role}:{Uri.EscapeDataString(password)}@127.0.0.1:{port}/aigoofish";
    }
}
