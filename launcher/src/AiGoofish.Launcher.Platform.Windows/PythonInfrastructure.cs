using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiGoofish.Launcher.Platform.Windows;

internal static class PythonConfiguration
{
    private static readonly HashSet<string> AllowedEnvironmentNames = new(StringComparer.Ordinal)
    {
        "ENCRYPTION_MASTER_KEY",
        "SECRET_KEY",
        "GOOFISH_LAUNCHER_TOKEN",
        "GOOFISH_PORTABLE_BROWSER_ROOT",
        "GOOFISH_PORTABLE_CACHE_ROOT",
        "GOOFISH_PORTABLE_DATABASE_URL",
        "GOOFISH_PORTABLE_DATA_ROOT",
        "GOOFISH_PORTABLE_MODE",
        "GOOFISH_PORTABLE_PROBE_DATABASE_URL",
        "GOOFISH_PORTABLE_PROGRAM_ROOT",
        "GOOFISH_PORTABLE_SETUP_TOKEN",
        "TEMP",
        "TMP",
    };

    public static WindowsPythonOptions NormalizeAndValidate(InstanceDataRootLease lease, WindowsPythonOptions options)
    {
        lease.EnsureHeld();
        var pythonExecutable = RequireExistingFile(options.PythonExecutable, "内置 Python EXE");
        if (!string.Equals(Path.GetFileName(pythonExecutable), "python.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("内置 Python 入口必须是 python.exe。", nameof(options));
        }

        var bootstrapScript = RequireExistingFile(options.BootstrapScript, "Python bootstrap");
        if (!string.Equals(Path.GetFileName(bootstrapScript), "python-bootstrap.py", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Python bootstrap 文件名必须是 python-bootstrap.py。", nameof(options));
        }

        var programRoot = RequireExistingDirectory(options.ProgramRoot, "程序根");
        var dataRoot = RequireExistingDirectory(options.DataRoot, "数据根");
        var cacheRoot = RequireExistingDirectory(options.CacheRoot, "缓存根");
        var browserRoot = RequireExistingDirectory(options.BrowserRoot, "浏览器根");
        EnsureExistingPathHasNoReparsePoints(pythonExecutable, "内置 Python EXE");
        EnsureExistingPathHasNoReparsePoints(bootstrapScript, "Python bootstrap");
        EnsureStrictDescendant(programRoot, bootstrapScript, "Python bootstrap 必须位于程序根内。");
        if (!string.Equals(dataRoot, lease.InstanceRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Python 数据根必须等于当前实例数据根。", nameof(options));
        }

        EnsureStrictDescendant(dataRoot, cacheRoot, "Python 缓存根必须严格位于实例数据根内。");
        PythonPathGuard.EnsureSafeInstancePath(lease, cacheRoot, allowMissingLeaf: false);
        foreach (var immutableRoot in new[] { programRoot, browserRoot, Path.GetDirectoryName(pythonExecutable)! })
        {
            if (PathsOverlap(immutableRoot, dataRoot) || PathsOverlap(immutableRoot, cacheRoot))
            {
                throw new ArgumentException("程序组件不能与数据或缓存根重叠。", nameof(options));
            }
        }
        if (options.Port is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Python HTTP 端口必须在 1024 到 65535 之间。");
        }

        if (string.IsNullOrWhiteSpace(options.AppVersion) || options.AppVersion.Length > 128)
        {
            throw new ArgumentException("Python 应用版本不能为空或超过 128 个字符。", nameof(options));
        }

        if (options.SupportedSchemaMinimum <= 0 ||
            options.SupportedSchemaMaximum < options.SupportedSchemaMinimum)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "支持的 schema 版本范围无效。");
        }

        if (options.StartupTimeout <= TimeSpan.Zero ||
            options.RequestTimeout <= TimeSpan.Zero ||
            options.StopTimeout <= TimeSpan.Zero ||
            options.RequestTimeout > options.StartupTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Python 生命周期和 HTTP 超时必须为有界正值。");
        }

        EnsureExistingPathHasNoReparsePoints(programRoot, "程序根");
        EnsureExistingPathHasNoReparsePoints(browserRoot, "浏览器根");
        return options with
        {
            PythonExecutable = pythonExecutable,
            BootstrapScript = bootstrapScript,
            ProgramRoot = programRoot,
            DataRoot = dataRoot,
            CacheRoot = cacheRoot,
            BrowserRoot = browserRoot,
        };
    }

    public static PythonDatabaseEndpoints ValidateDatabaseEndpoints(
        PythonServiceMode mode,
        PythonDatabaseEndpoints endpoints)
    {
        ValidateSensitiveAscii(endpoints.ProbeDsn, "probe DSN", 4096);
        if (mode is PythonServiceMode.Normal)
        {
            ValidateSensitiveAscii(endpoints.ApplicationDsn, "application DSN", 4096);
            if (!string.IsNullOrEmpty(endpoints.SetupToken))
            {
                ValidateSensitiveAscii(endpoints.SetupToken, "setup token", 256);
            }
        }

        return endpoints;
    }

    public static OwnedProcessLaunchSpec CreateLaunchSpec(
        InstanceDataRootLease lease,
        InstanceSecrets secrets,
        WindowsPythonOptions options,
        PythonDatabaseEndpoints endpoints,
        string controlToken)
    {
        var mode = GetWireMode(options.Mode);
        var target = options.Mode is PythonServiceMode.Maintenance ? "maintenance" : "web";
        var arguments = new List<string>
        {
            "-I",
            "-B",
            options.BootstrapScript,
            "--app-root",
            options.ProgramRoot,
            "--target",
            target,
            "--",
            "--mode",
            mode,
            "--instance-id",
            lease.InstanceId.ToString("D"),
        };
        if (options.Mode is PythonServiceMode.Maintenance)
        {
            arguments.AddRange(new[]
            {
                "--program-root", options.ProgramRoot,
                "--data-root", options.DataRoot,
            });
        }

        arguments.AddRange(new[]
        {
            "--port",
            options.Port.ToString(CultureInfo.InvariantCulture),
        });

        var variables = new List<ProcessEnvironmentVariable>
        {
            Sensitive("GOOFISH_LAUNCHER_TOKEN", controlToken),
            Public("GOOFISH_PORTABLE_MODE", "portable"),
            Public("GOOFISH_PORTABLE_PROGRAM_ROOT", options.ProgramRoot),
            Public("GOOFISH_PORTABLE_DATA_ROOT", options.DataRoot),
            Public("GOOFISH_PORTABLE_CACHE_ROOT", options.CacheRoot),
            Public("GOOFISH_PORTABLE_BROWSER_ROOT", options.BrowserRoot),
            Public("TEMP", options.CacheRoot),
            Public("TMP", options.CacheRoot),
        };
        if (options.Mode is PythonServiceMode.Maintenance)
        {
            variables.Add(Sensitive("GOOFISH_PORTABLE_DATABASE_URL", endpoints.ProbeDsn));
        }
        else
        {
            variables.AddRange(new[]
            {
                Sensitive("GOOFISH_PORTABLE_DATABASE_URL", endpoints.ApplicationDsn),
                Sensitive("GOOFISH_PORTABLE_PROBE_DATABASE_URL", endpoints.ProbeDsn),
                Sensitive("ENCRYPTION_MASTER_KEY", secrets.EncryptionMasterKey),
                Sensitive("SECRET_KEY", secrets.SecretKey),
                Sensitive("GOOFISH_PORTABLE_SETUP_TOKEN", endpoints.SetupToken ?? string.Empty),
            });
        }

        if (variables.Any(variable => !AllowedEnvironmentNames.Contains(variable.Name)))
        {
            throw new PythonLifecycleException("Python 环境变量超出固定允许列表。");
        }

        return new OwnedProcessLaunchSpec(
            options.PythonExecutable,
            options.DataRoot,
            arguments,
            variables,
            StartupGracePeriod: TimeSpan.FromMilliseconds(350));
    }

    public static string GetWireMode(PythonServiceMode mode)
    {
        return mode switch
        {
            PythonServiceMode.Maintenance => "maintenance",
            PythonServiceMode.Normal => "normal",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private static ProcessEnvironmentVariable Sensitive(string name, string value) => new(name, value, Sensitive: true);

    private static ProcessEnvironmentVariable Public(string name, string value) => new(name, value, Sensitive: false);

    private static void ValidateSensitiveAscii(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length < 32 ||
            value.Length > maximumLength ||
            value.Any(character => character is < '!' or > '~'))
        {
            throw new ArgumentException($"{name} 必须是 32 到 {maximumLength} 个可打印 ASCII 字符。", name);
        }
    }

    private static string RequireExistingFile(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException($"{name} 必须是存在的绝对文件路径。", name);
        }

        var path = Path.GetFullPath(value);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{name} 不存在。", path);
        }

        return path;
    }

    private static string RequireExistingDirectory(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException($"{name} 必须是存在的绝对目录。", name);
        }

        var path = Path.GetFullPath(value);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"{name} 不存在。");
        }

        return path;
    }

    private static void EnsureStrictDescendant(string root, string path, string message)
    {
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(message);
        }
    }

    private static bool PathsOverlap(string first, string second)
    {
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
            || first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureExistingPathHasNoReparsePoints(string path, string name)
    {
        var current = Path.GetPathRoot(path)
            ?? throw new ArgumentException($"无法解析{name}盘符。", name);
        foreach (var segment in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new PythonLifecycleException($"无法核验{name}安全性。", exception);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PythonLifecycleException($"{name}路径包含重解析点。");
            }
        }
    }
}

internal sealed class PythonLoopbackControlClient : IDisposable
{
    private sealed class OwnedHttpResponse(HttpClient client, HttpResponseMessage response) : IDisposable
    {
        public HttpResponseMessage Response { get; } = response;

        public void Dispose()
        {
            Response.Dispose();
            client.Dispose();
        }
    }

    private const int MaximumResponseBytes = 64 * 1024;
    private readonly int _port;
    private readonly TimeSpan _requestTimeout;
    private readonly WindowsLoopbackPeerVerifier _peerVerifier;

    public PythonLoopbackControlClient(
        int port,
        TimeSpan requestTimeout,
        WindowsLoopbackPeerVerifier? peerVerifier = null)
    {
        _port = port;
        _requestTimeout = requestTimeout;
        _peerVerifier = peerVerifier ?? new WindowsLoopbackPeerVerifier();
    }

    public async Task<PythonReadinessResult> CheckReadinessAsync(
        OwnedProcessIdentity expectedIdentity,
        Guid instanceId,
        string controlToken,
        string expectedAppVersion,
        string expectedMode,
        int supportedSchemaMinimum,
        int supportedSchemaMaximum,
        CancellationToken cancellationToken)
    {
        if (expectedIdentity.InstanceId != instanceId)
        {
            throw new PythonControlException("Python 控制身份与实例不匹配。");
        }

        using var operationDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationDeadline.CancelAfter(_requestTimeout);
        cancellationToken = operationDeadline.Token;
        using var request = CreateRequest(HttpMethod.Get, "internal/ready", instanceId, controlToken);
        OwnedHttpResponse response;
        try
        {
            response = await SendAsync(request, expectedIdentity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or TimeoutException)
        {
            return new PythonReadinessResult(PythonReadinessState.Unreachable, null, null);
        }

        using (response)
        {
            if (response.Response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                await DrainBoundedAsync(response.Response, cancellationToken).ConfigureAwait(false);
                return new PythonReadinessResult(PythonReadinessState.Rejected, null, null);
            }

            JsonDocument document;
            try
            {
                document = await ReadJsonAsync(response.Response, cancellationToken).ConfigureAwait(false);
            }
            catch (PythonControlException)
            {
                return new PythonReadinessResult(PythonReadinessState.ContractMismatch, null, null);
            }

            using (document)
            {
                var root = document.RootElement;
                if (!TryReadInt(root, "protocol_version", out var protocolVersion) || protocolVersion != 1 ||
                    !TryReadString(root, "instance_id", out var observedInstanceId) ||
                    !string.Equals(observedInstanceId, instanceId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
                    !TryReadString(root, "app_version", out var appVersion) ||
                    !string.Equals(appVersion, expectedAppVersion, StringComparison.Ordinal) ||
                    !TryReadString(root, "mode", out var mode) ||
                    !string.Equals(mode, expectedMode, StringComparison.Ordinal) ||
                    !TryReadBoolean(root, "ready", out var ready) ||
                    !TryReadNestedString(root, "database", "status", out var databaseStatus) ||
                    !TryReadNestedString(root, "schema", "status", out var schemaStatus) ||
                    !TryReadNestedNullableInt(root, "schema", "version", out var schemaVersion))
                {
                    return new PythonReadinessResult(PythonReadinessState.ContractMismatch, null, null);
                }

                var compatible = response.Response.StatusCode is HttpStatusCode.OK &&
                    ready &&
                    string.Equals(databaseStatus, "available", StringComparison.Ordinal) &&
                    string.Equals(schemaStatus, "compatible", StringComparison.Ordinal) &&
                    schemaVersion is not null &&
                    schemaVersion.Value >= supportedSchemaMinimum &&
                    schemaVersion.Value <= supportedSchemaMaximum;
                var failureReason = TryReadString(root, "failure_reason", out var reason) ? reason : null;
                if (failureReason is not (null or "database_unavailable" or "schema_uninitialized" or "schema_invalid" or "schema_incompatible"))
                {
                    failureReason = "backend_failure";
                }
                bool? setupRequired = null;
                if (expectedMode == "normal")
                {
                    if (!TryReadBoolean(root, "setup_required", out var setup))
                    {
                        return new PythonReadinessResult(PythonReadinessState.ContractMismatch, null, null);
                    }
                    setupRequired = setup;
                }
                return new PythonReadinessResult(
                    compatible ? PythonReadinessState.Ready : PythonReadinessState.NotReady,
                    failureReason,
                    schemaVersion,
                    setupRequired);
            }
        }
    }

    public async Task RequestShutdownAsync(
        OwnedProcessIdentity expectedIdentity,
        Guid instanceId,
        string controlToken,
        CancellationToken cancellationToken)
    {
        if (expectedIdentity.InstanceId != instanceId)
        {
            throw new PythonControlException("Python 停止控制身份与实例不匹配。");
        }

        using var operationDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationDeadline.CancelAfter(_requestTimeout);
        cancellationToken = operationDeadline.Token;
        using var request = CreateRequest(HttpMethod.Post, "internal/shutdown", instanceId, controlToken);
        using var response = await SendAsync(request, expectedIdentity, cancellationToken).ConfigureAwait(false);
        if (response.Response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            await DrainBoundedAsync(response.Response, cancellationToken).ConfigureAwait(false);
            throw new PythonControlException("Python 关闭控制身份被拒绝。");
        }

        using var document = await ReadJsonAsync(response.Response, cancellationToken).ConfigureAwait(false);
        var maintenanceAccepted = TryReadString(document.RootElement, "status", out var status)
            && status == "shutdown_requested";
        var webAccepted = TryReadString(document.RootElement, "state", out var state)
            && state is "Draining" or "Committing" or "Stopped";
        if (response.Response.StatusCode is not HttpStatusCode.Accepted || (!maintenanceAccepted && !webAccepted))
        {
            throw new PythonControlException("Python 关闭响应未满足固定 202 契约。");
        }
    }

    public void Dispose()
    {
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string relativePath,
        Guid instanceId,
        string controlToken)
    {
        var request = new HttpRequestMessage(method, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", controlToken);
        request.Headers.TryAddWithoutValidation("X-Goofish-Instance-Id", instanceId.ToString("D"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<OwnedHttpResponse> SendAsync(
        HttpRequestMessage request,
        OwnedProcessIdentity expectedIdentity,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        try
        {
            var client = CreateClient(expectedIdentity);
            try
            {
                var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    deadline.Token).ConfigureAwait(false);
                return new OwnedHttpResponse(client, response);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Python loopback HTTP 请求超时。", exception);
        }
    }

    private HttpClient CreateClient(OwnedProcessIdentity expectedIdentity)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = _requestTimeout,
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (!string.Equals(context.DnsEndPoint.Host, "127.0.0.1", StringComparison.Ordinal) ||
                    context.DnsEndPoint.Port != _port)
                {
                    throw new IOException("Python 控制连接目标超出固定 IPv4 loopback 端点。");
                }

                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, _port), cancellationToken).ConfigureAwait(false);
                    _peerVerifier.Verify(socket, _port, expectedIdentity);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_port}/", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new PythonControlException("Python loopback HTTP 响应超过 64 KiB 上限。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw new PythonControlException("Python loopback HTTP 响应超过 64 KiB 上限。");
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            throw new PythonControlException("Python loopback HTTP 响应为空。");
        }

        try
        {
            return JsonDocument.Parse(
                buffer.ToArray(),
                new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
        }
        catch (JsonException exception)
        {
            throw new PythonControlException("Python loopback HTTP 响应不是有效的有界 JSON。", exception);
        }
    }

    private static async Task DrainBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (PythonControlException)
        {
        }
    }

    private static bool TryReadString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        return element.ValueKind is JsonValueKind.Object &&
            element.TryGetProperty(property, out var candidate) &&
            candidate.ValueKind is JsonValueKind.String &&
            (value = candidate.GetString() ?? string.Empty).Length <= 1024;
    }

    private static bool TryReadInt(JsonElement element, string property, out int value)
    {
        value = default;
        return element.ValueKind is JsonValueKind.Object &&
            element.TryGetProperty(property, out var candidate) &&
            candidate.ValueKind is JsonValueKind.Number &&
            candidate.TryGetInt32(out value);
    }

    private static bool TryReadBoolean(JsonElement element, string property, out bool value)
    {
        value = default;
        if (element.ValueKind is not JsonValueKind.Object ||
            !element.TryGetProperty(property, out var candidate) ||
            candidate.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = candidate.GetBoolean();
        return true;
    }

    private static bool TryReadNestedString(
        JsonElement element,
        string parent,
        string property,
        out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(parent, out var nested) && TryReadString(nested, property, out value);
    }

    private static bool TryReadNestedNullableInt(
        JsonElement element,
        string parent,
        string property,
        out int? value)
    {
        value = default;
        if (!element.TryGetProperty(parent, out var nested) ||
            nested.ValueKind is not JsonValueKind.Object ||
            !nested.TryGetProperty(property, out var candidate))
        {
            return false;
        }

        if (candidate.ValueKind is JsonValueKind.Null)
        {
            return true;
        }

        if (candidate.ValueKind is JsonValueKind.Number && candidate.TryGetInt32(out var integer))
        {
            value = integer;
            return true;
        }

        return false;
    }
}

internal sealed class PythonHttpStopProtocol : IProcessStopProtocol
{
    private readonly PythonLoopbackControlClient _controlClient;
    private readonly object _sync = new();
    private OwnedProcessIdentity? _identity;
    private Guid _instanceId;
    private string? _controlToken;

    public PythonHttpStopProtocol(PythonLoopbackControlClient controlClient)
    {
        _controlClient = controlClient;
    }

    public void Configure(OwnedProcessIdentity identity, Guid instanceId, string controlToken)
    {
        lock (_sync)
        {
            _identity = identity;
            _instanceId = instanceId;
            _controlToken = controlToken;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _identity = null;
            _instanceId = Guid.Empty;
            _controlToken = null;
        }
    }

    public Task RequestStopAsync(OwnedProcessIdentity identity, CancellationToken cancellationToken)
    {
        Guid instanceId;
        string controlToken;
        lock (_sync)
        {
            if (_identity != identity || _instanceId == Guid.Empty || string.IsNullOrEmpty(_controlToken))
            {
                throw new IOException("Python HTTP 停止控制上下文与当前进程身份不匹配。");
            }

            instanceId = _instanceId;
            controlToken = _controlToken;
        }

        return _controlClient.RequestShutdownAsync(identity, instanceId, controlToken, cancellationToken);
    }
}

internal sealed class PythonControlException : IOException
{
    public PythonControlException(string message)
        : base(message)
    {
    }

    public PythonControlException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal enum PythonIdentityRunState
{
    Running,
    NotRunning,
    Unknown,
}

internal static class PythonProcessIdentityProbe
{
    public static PythonIdentityRunState GetRunState(OwnedProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited)
            {
                return PythonIdentityRunState.NotRunning;
            }

            var observedStart = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            if (observedStart.UtcTicks != identity.StartedAtUtc.UtcTicks)
            {
                return PythonIdentityRunState.NotRunning;
            }

            var observedExecutable = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(observedExecutable))
            {
                return PythonIdentityRunState.Unknown;
            }

            return string.Equals(
                Path.GetFullPath(observedExecutable),
                identity.ExecutablePath,
                StringComparison.OrdinalIgnoreCase)
                ? PythonIdentityRunState.Running
                : PythonIdentityRunState.NotRunning;
        }
        catch (ArgumentException)
        {
            return PythonIdentityRunState.NotRunning;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return PythonIdentityRunState.Unknown;
        }
    }
}

internal static class PythonPathGuard
{
    public static void EnsureSafeInstancePath(InstanceDataRootLease lease, string path, bool allowMissingLeaf)
    {
        lease.EnsureHeld();
        var root = Path.GetFullPath(lease.InstanceRoot);
        var target = Path.GetFullPath(path);
        if (target != root && !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new PythonLifecycleException("Python 状态路径超出当前实例数据根。");
        }

        var current = root;
        foreach (var segment in target[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
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

                throw new PythonLifecycleException("Python 必需状态路径不存在。", exception);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new PythonLifecycleException("无法核验 Python 状态路径安全性。", exception);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PythonLifecycleException("Python 状态路径包含重解析点。");
            }
        }
    }
}

internal static class PythonStateFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static T Read<T>(string path, int maximumBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > maximumBytes)
            {
                throw new PythonLifecycleException("Python 状态文件大小无效。");
            }

            return JsonSerializer.Deserialize<T>(stream, Options)
                ?? throw new PythonLifecycleException("Python 状态文件内容为空。");
        }
        catch (PythonLifecycleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new PythonLifecycleException("Python 状态文件无效或不可读。", exception);
        }
    }

    public static void WriteReplace<T>(string path, T value)
    {
        Write(path, value, overwrite: true);
    }

    public static void WriteNew<T>(string path, T value)
    {
        Write(path, value, overwrite: false);
    }

    private static void Write<T>(string path, T value, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new PythonLifecycleException("无法解析 Python 状态目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, Options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Exception? cleanupError = null;
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                cleanupError = cleanupException;
            }

            if (cleanupError is not null)
            {
                throw new PythonLifecycleException(
                    "Python 状态原子提交与唯一暂存清理均失败。",
                    new AggregateException(exception, cleanupError));
            }

            throw new PythonLifecycleException("Python 状态文件原子提交失败。", exception);
        }
    }
}

internal sealed record PythonRunCredentials(
    Guid CredentialId,
    string ControlToken,
    string? SetupToken)
{
    public override string ToString() => "PythonRunCredentials([REDACTED])";
}

internal sealed class WindowsPythonRunCredentialStore
{
    internal const string RelativeCredentialPath = "config/python-control.dpapi";
    private const int FormatVersion = 2;
    private const int MaximumFileBytes = 64 * 1024;
    private const string PendingPhase = "pending";
    private const string BoundPhase = "bound";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly WindowsCurrentUserDataProtector _protector = new();
    private readonly IPythonCredentialFileCommitter _committer;

    public WindowsPythonRunCredentialStore()
        : this(new PythonCredentialFileCommitter())
    {
    }

    internal WindowsPythonRunCredentialStore(IPythonCredentialFileCommitter committer)
    {
        _committer = committer ?? throw new ArgumentNullException(nameof(committer));
    }

    public string GetCredentialPath(InstanceDataRootLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureHeld();
        return Path.Combine(lease.InstanceRoot, "config", "python-control.dpapi");
    }

    public PythonRunCredentials CreatePending(
        InstanceDataRootLease lease,
        WindowsPythonOptions options,
        string? setupToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(options);
        lease.EnsureHeld();
        ValidateConfiguration(lease, options);
        var path = GetCredentialPath(lease);
        EnsureCredentialDirectory(lease, path);
        if (TryGetAttributes(path, out _))
        {
            throw new PythonLifecycleException("Python 运行控制凭据已存在，拒绝覆盖或重新生成。");
        }

        var credentials = new PythonRunCredentials(
            Guid.NewGuid(),
            GenerateToken(),
            string.IsNullOrEmpty(setupToken) ? null : setupToken);
        ValidateCredentials(credentials, options.Mode);
        var payload = CreatePayload(lease, options, credentials, PendingPhase, identity: null);
        WriteProtected(path, lease.InstanceId, payload, overwrite: false);
        return credentials;
    }

    public void Bind(
        InstanceDataRootLease lease,
        WindowsPythonOptions options,
        PythonRunCredentials credentials,
        OwnedProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lease.EnsureHeld();
        ValidateConfiguration(lease, options);
        ValidateIdentity(lease, options, identity);
        var path = GetCredentialPath(lease);
        var current = ReadProtected(path, lease);
        ValidatePayloadConfiguration(current, lease, options);
        if (current.Phase != PendingPhase || current.Identity is not null ||
            current.CredentialId != credentials.CredentialId ||
            !FixedTimeEquals(current.ControlToken, credentials.ControlToken) ||
            !FixedTimeEquals(current.SetupToken, credentials.SetupToken))
        {
            throw new PythonLifecycleException("Python 待绑定控制凭据与本次启动上下文不匹配。");
        }

        var bound = current with { Phase = BoundPhase, Identity = identity };
        WriteProtected(path, lease.InstanceId, bound, overwrite: true);
    }

    public PythonRunCredentials LoadBound(
        InstanceDataRootLease lease,
        WindowsPythonOptions options,
        PythonRuntimeRecord runtime)
    {
        lease.EnsureHeld();
        ValidateConfiguration(lease, options);
        var payload = ReadProtected(GetCredentialPath(lease), lease);
        ValidatePayloadConfiguration(payload, lease, options);
        ValidatePayloadBinding(payload, runtime);
        var credentials = new PythonRunCredentials(payload.CredentialId, payload.ControlToken, payload.SetupToken);
        ValidateCredentials(credentials, options.Mode);
        return credentials;
    }

    public void DeleteBound(InstanceDataRootLease lease, PythonRuntimeRecord runtime)
    {
        lease.EnsureHeld();
        var path = GetCredentialPath(lease);
        var payload = ReadProtected(path, lease);
        ValidatePayloadBinding(payload, runtime);
        DeleteExistingFile(lease, path);
    }

    public void DeletePending(
        InstanceDataRootLease lease,
        WindowsPythonOptions options,
        PythonRunCredentials credentials)
    {
        lease.EnsureHeld();
        var path = GetCredentialPath(lease);
        var payload = ReadProtected(path, lease);
        ValidatePayloadConfiguration(payload, lease, options);
        if (payload.Phase != PendingPhase || payload.Identity is not null ||
            payload.CredentialId != credentials.CredentialId ||
            !FixedTimeEquals(payload.ControlToken, credentials.ControlToken) ||
            !FixedTimeEquals(payload.SetupToken, credentials.SetupToken))
        {
            throw new PythonLifecycleException("Python 待删除控制凭据与本次未启动上下文不匹配。");
        }

        DeleteExistingFile(lease, path);
    }

    private static PythonCredentialPayload CreatePayload(
        InstanceDataRootLease lease,
        WindowsPythonOptions options,
        PythonRunCredentials credentials,
        string phase,
        OwnedProcessIdentity? identity)
    {
        return new PythonCredentialPayload(
            FormatVersion,
            lease.InstanceId,
            credentials.CredentialId,
            phase,
            PythonConfiguration.GetWireMode(options.Mode),
            options.AppVersion,
            options.Port,
            options.PythonExecutable,
            options.ProgramRoot,
            options.DataRoot,
            identity,
            credentials.ControlToken,
            credentials.SetupToken,
            DateTimeOffset.UtcNow);
    }

    private void WriteProtected(
        string path,
        Guid instanceId,
        PythonCredentialPayload payload,
        bool overwrite)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var entropy = CreateEntropy(instanceId);
        byte[]? ciphertext = null;
        byte[]? envelopeBytes = null;
        try
        {
            ciphertext = _protector.Protect(plaintext, entropy);
            envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(
                new PythonCredentialEnvelope(FormatVersion, instanceId, Convert.ToBase64String(ciphertext)),
                JsonOptions);
            if (envelopeBytes.Length <= 0 || envelopeBytes.Length > MaximumFileBytes)
            {
                throw new PythonLifecycleException("Python 控制凭据文件超过 64 KiB 上限。");
            }

            WriteSecureAtomic(path, envelopeBytes, overwrite);
        }
        catch (InstanceSecretsException exception)
        {
            throw new PythonLifecycleException("Windows 当前用户保护 Python 控制凭据失败。", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(entropy);
            if (ciphertext is not null)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }

            if (envelopeBytes is not null)
            {
                CryptographicOperations.ZeroMemory(envelopeBytes);
            }
        }
    }

    private PythonCredentialPayload ReadProtected(string path, InstanceDataRootLease lease)
    {
        lease.EnsureHeld();
        var instanceId = lease.InstanceId;
        EnsureSafeExistingFile(lease, path);
        var envelopeBytes = ReadBoundedFile(path);
        byte[]? ciphertext = null;
        byte[]? plaintext = null;
        var entropy = CreateEntropy(instanceId);
        try
        {
            PythonCredentialEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<PythonCredentialEnvelope>(envelopeBytes, JsonOptions)
                    ?? throw new PythonLifecycleException("Python 控制凭据外层格式为空。");
            }
            catch (JsonException exception)
            {
                throw new PythonLifecycleException("Python 控制凭据外层格式无效。", exception);
            }

            if (envelope.FormatVersion != FormatVersion || envelope.InstanceId != instanceId ||
                string.IsNullOrWhiteSpace(envelope.ProtectedPayload))
            {
                throw new PythonLifecycleException("Python 控制凭据外层版本、实例或密文字段无效。");
            }

            try
            {
                ciphertext = Convert.FromBase64String(envelope.ProtectedPayload);
            }
            catch (FormatException exception)
            {
                throw new PythonLifecycleException("Python 控制凭据密文不是有效 Base64。", exception);
            }

            if (ciphertext.Length <= 0 || ciphertext.Length > MaximumFileBytes)
            {
                throw new PythonLifecycleException("Python 控制凭据密文大小无效。");
            }

            plaintext = _protector.Unprotect(ciphertext, entropy);
            PythonCredentialPayload payload;
            try
            {
                payload = JsonSerializer.Deserialize<PythonCredentialPayload>(plaintext, JsonOptions)
                    ?? throw new PythonLifecycleException("Python 控制凭据保护载荷为空。");
            }
            catch (JsonException exception)
            {
                throw new PythonLifecycleException("Python 控制凭据保护载荷结构无效。", exception);
            }

            if (payload.FormatVersion != FormatVersion || payload.InstanceId != instanceId ||
                payload.CredentialId == Guid.Empty || payload.Phase is not (PendingPhase or BoundPhase))
            {
                throw new PythonLifecycleException("Python 控制凭据保护载荷版本、实例或阶段无效。");
            }

            return payload;
        }
        catch (InstanceSecretsException exception)
        {
            throw new PythonLifecycleException("Windows 当前用户无法解密 Python 控制凭据。", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelopeBytes);
            CryptographicOperations.ZeroMemory(entropy);
            if (ciphertext is not null)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private void WriteSecureAtomic(string targetPath, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new PythonLifecycleException("无法解析 Python 控制凭据目录。");
        var temporaryPath = Path.Combine(directory, $".python-control.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query))
            {
                var currentUser = identity.User
                    ?? throw new PythonLifecycleException("无法确定当前 Windows 用户 SID，未写 Python 控制凭据。");
                var security = new FileSecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(
                    currentUser,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
                using var stream = new FileInfo(temporaryPath).Create(
                    FileMode.CreateNew,
                    FileSystemRights.FullControl,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough,
                    security);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (!overwrite && TryGetAttributes(targetPath, out _))
            {
                throw new PythonLifecycleException("Python 运行控制凭据已存在，拒绝覆盖或重新生成。");
            }

            _committer.Commit(temporaryPath, targetPath, overwrite);
        }
        catch (PythonLifecycleException)
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
                throw new PythonLifecycleException(
                    "Python 控制凭据原子提交失败，且唯一暂存清理失败。",
                    new AggregateException(exception, cleanupException));
            }

            throw new PythonLifecycleException("Python 控制凭据原子提交失败；目标文件未被不完整内容覆盖。", exception);
        }
    }

    private static void ValidatePayloadConfiguration(
        PythonCredentialPayload payload,
        InstanceDataRootLease lease,
        WindowsPythonOptions options)
    {
        if (payload.InstanceId != lease.InstanceId ||
            payload.Mode != PythonConfiguration.GetWireMode(options.Mode) ||
            payload.AppVersion != options.AppVersion ||
            payload.Port != options.Port ||
            !string.Equals(payload.PythonExecutable, options.PythonExecutable, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(payload.ProgramRoot, options.ProgramRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(payload.DataRoot, options.DataRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new PythonLifecycleException("Python 控制凭据与当前实例、组件或端口配置不匹配。");
        }
    }

    private static void ValidatePayloadBinding(PythonCredentialPayload payload, PythonRuntimeRecord runtime)
    {
        if (payload.Phase != BoundPhase || payload.Identity is null ||
            payload.CredentialId != runtime.CredentialId ||
            payload.InstanceId != runtime.InstanceId ||
            payload.Mode != runtime.Mode || payload.AppVersion != runtime.AppVersion || payload.Port != runtime.Port ||
            !string.Equals(payload.ProgramRoot, runtime.ProgramRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(payload.DataRoot, runtime.DataRoot, StringComparison.OrdinalIgnoreCase) ||
            !IdentityEquals(payload.Identity, runtime.Identity))
        {
            throw new PythonLifecycleException("Python 控制凭据与持久运行身份不匹配。");
        }
    }

    private static void ValidateIdentity(
        InstanceDataRootLease lease,
        WindowsPythonOptions options,
        OwnedProcessIdentity identity)
    {
        if (identity.ProcessId <= 0 || identity.RunId == Guid.Empty || identity.InstanceId != lease.InstanceId ||
            !string.Equals(identity.InstanceRoot, lease.InstanceRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identity.ExecutablePath, options.PythonExecutable, StringComparison.OrdinalIgnoreCase))
        {
            throw new PythonLifecycleException("Python 进程身份不能绑定到当前实例控制凭据。");
        }
    }

    private static bool IdentityEquals(OwnedProcessIdentity left, OwnedProcessIdentity right)
    {
        return left.ProcessId == right.ProcessId &&
            left.StartedAtUtc.UtcTicks == right.StartedAtUtc.UtcTicks &&
            left.InstanceId == right.InstanceId && left.RunId == right.RunId &&
            string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.InstanceRoot, right.InstanceRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateConfiguration(InstanceDataRootLease lease, WindowsPythonOptions options)
    {
        if (options.Port is < 1024 or > 65535 || string.IsNullOrWhiteSpace(options.AppVersion) ||
            !string.Equals(options.DataRoot, lease.InstanceRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(options.PythonExecutable))
        {
            throw new PythonLifecycleException("Python 控制凭据配置无效。");
        }
    }

    private static void ValidateCredentials(PythonRunCredentials credentials, PythonServiceMode mode)
    {
        ValidateToken(credentials.ControlToken, "控制 token");
        if (mode is PythonServiceMode.Maintenance && credentials.SetupToken is not null)
        {
            throw new PythonLifecycleException("维护模式不得持有首次引导凭据。");
        }

        if (credentials.SetupToken is not null)
        {
            ValidateToken(credentials.SetupToken, "首次引导 token");
            if (FixedTimeEquals(credentials.ControlToken, credentials.SetupToken))
            {
                throw new PythonLifecycleException("首次引导凭据不能复用 Python 控制 token。");
            }
        }
    }

    private static void ValidateToken(string token, string name)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 256 ||
            token.Any(character => character is < '!' or > '~'))
        {
            throw new PythonLifecycleException($"Python {name} 格式无效。");
        }
    }

    private static bool FixedTimeEquals(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        try
        {
            return leftBytes.Length == rightBytes.Length &&
                CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static byte[] ReadBoundedFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > MaximumFileBytes)
            {
                throw new PythonLifecycleException("Python 控制凭据文件为空或超过 64 KiB 上限。");
            }

            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw new PythonLifecycleException("Python 控制凭据文件在读取期间发生变化。");
            }

            return bytes;
        }
        catch (PythonLifecycleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PythonLifecycleException("无法读取 Python 控制凭据文件。", exception);
        }
    }

    private static void EnsureCredentialDirectory(InstanceDataRootLease lease, string credentialPath)
    {
        var directory = Path.GetDirectoryName(credentialPath)
            ?? throw new PythonLifecycleException("无法解析 Python 控制凭据目录。");
        PythonPathGuard.EnsureSafeInstancePath(lease, directory, allowMissingLeaf: true);
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PythonLifecycleException("无法准备 Python 控制凭据目录。", exception);
        }

        PythonPathGuard.EnsureSafeInstancePath(lease, directory, allowMissingLeaf: false);
    }

    private static void EnsureSafeExistingFile(InstanceDataRootLease lease, string path)
    {
        PythonPathGuard.EnsureSafeInstancePath(lease, path, allowMissingLeaf: false);
        if (!TryGetAttributes(path, out var attributes) ||
            (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new PythonLifecycleException("Python 控制凭据路径不是安全的普通文件。");
        }

        EnsurePrivateAcl(path);
    }

    private static void EnsurePrivateAcl(string path)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            var currentUser = identity.User
                ?? throw new PythonLifecycleException("无法确定当前 Windows 用户 SID。");
            var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (!security.AreAccessRulesProtected || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
                !owner.Equals(currentUser))
            {
                throw new PythonLifecycleException("Python 控制凭据文件 ACL 未绑定当前 Windows 用户。");
            }

            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .ToArray();
            if (!rules.Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                    rule.IdentityReference.Equals(currentUser) &&
                    (rule.FileSystemRights & FileSystemRights.ReadData) != 0) ||
                rules.Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                    !rule.IdentityReference.Equals(currentUser)))
            {
                throw new PythonLifecycleException("Python 控制凭据文件 ACL 包含非当前用户访问或缺少读取权限。");
            }
        }
        catch (PythonLifecycleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new PythonLifecycleException("无法核验 Python 控制凭据文件 ACL。", exception);
        }
    }

    private static void DeleteExistingFile(InstanceDataRootLease lease, string path)
    {
        PythonPathGuard.EnsureSafeInstancePath(lease, path, allowMissingLeaf: false);
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PythonLifecycleException("无法删除已核验的 Python 控制凭据。", exception);
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
            throw new PythonLifecycleException("无法核验 Python 控制凭据路径属性。", exception);
        }
    }

    private static void CleanupTemporaryFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static byte[] CreateEntropy(Guid instanceId) =>
        Encoding.UTF8.GetBytes($"ai-goofish-monitor/python-control/v2/{instanceId:D}");

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            return Convert.ToBase64String(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private sealed record PythonCredentialEnvelope(int FormatVersion, Guid InstanceId, string ProtectedPayload);

    private sealed record PythonCredentialPayload(
        int FormatVersion,
        Guid InstanceId,
        Guid CredentialId,
        string Phase,
        string Mode,
        string AppVersion,
        int Port,
        string PythonExecutable,
        string ProgramRoot,
        string DataRoot,
        OwnedProcessIdentity? Identity,
        string ControlToken,
        string? SetupToken,
        DateTimeOffset CreatedAtUtc);

}

internal interface IPythonCredentialFileCommitter
{
    void Commit(string temporaryPath, string targetPath, bool overwrite);
}

internal sealed class PythonCredentialFileCommitter : IPythonCredentialFileCommitter
{
    public void Commit(string temporaryPath, string targetPath, bool overwrite)
    {
        File.Move(temporaryPath, targetPath, overwrite);
    }
}
