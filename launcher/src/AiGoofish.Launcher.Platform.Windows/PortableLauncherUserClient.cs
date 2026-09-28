using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiGoofish.Launcher.Platform.Windows;

/// <summary>
/// A short-lived, in-memory handle to this Host run's local control channel.
/// The control token is deliberately never exposed as a property or included in diagnostics.
/// </summary>
public sealed class PortableLauncherUserClientContext : IDisposable
{
    private readonly object _sync = new();
    private readonly Func<long, bool> _isGenerationCurrent;
    private readonly Action<PortableLauncherUserClientContext>? _onDispose;
    private byte[]? _controlToken;
    private bool _disposed;

    internal PortableLauncherUserClientContext(
        Guid instanceId,
        int webPort,
        long generation,
        string controlToken,
        Func<long, bool> isGenerationCurrent,
        Action<PortableLauncherUserClientContext>? onDispose = null)
    {
        if (instanceId == Guid.Empty || webPort is < 1024 or > 65535 || generation <= 0 ||
            string.IsNullOrWhiteSpace(controlToken))
        {
            throw new ArgumentException("便携 Launcher 用户上下文无效。", nameof(instanceId));
        }

        InstanceId = instanceId;
        WebPort = webPort;
        Generation = generation;
        _controlToken = Encoding.UTF8.GetBytes(controlToken);
        _isGenerationCurrent = isGenerationCurrent ?? throw new ArgumentNullException(nameof(isGenerationCurrent));
        _onDispose = onDispose;
    }

    public Guid InstanceId { get; }

    public int WebPort { get; }

    internal long Generation { get; }

    internal Uri BaseAddress => new($"http://127.0.0.1:{WebPort}/", UriKind.Absolute);

    internal event Action? Invalidated;

    internal void EnsureCurrent()
    {
        var invalid = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_controlToken is null || !_isGenerationCurrent(Generation))
            {
                ClearTokenCore();
                invalid = true;
            }
        }

        if (invalid)
        {
            Invalidate();
            throw new InvalidOperationException("便携实例已停止或重启，请重新连接后再操作。");
        }
    }

    internal void AddControlAuthorization(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureCurrent();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_controlToken is null)
            {
                throw new InvalidOperationException("便携实例已停止或重启，请重新连接后再操作。");
            }

            var token = Encoding.UTF8.GetString(_controlToken!);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-Goofish-Instance-Id", InstanceId.ToString("D"));
        }
    }

    internal void AddUserAuthorization(HttpRequestMessage request, ReadOnlySpan<char> accessToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureCurrent();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.ToString());
        request.Headers.Add("X-Goofish-Instance-Id", InstanceId.ToString("D"));
        request.Headers.Add("Origin", BaseAddress.GetLeftPart(UriPartial.Authority));
    }

    internal void Invalidate()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            ClearTokenCore();
            _disposed = true;
        }

        Invalidated?.Invoke();
        _onDispose?.Invoke(this);
    }

    public void Dispose()
    {
        Invalidate();
        GC.SuppressFinalize(this);
    }

    public override string ToString() => "PortableLauncherUserClientContext([REDACTED])";

    private void ClearTokenCore()
    {
        if (_controlToken is not null)
        {
            CryptographicOperations.ZeroMemory(_controlToken);
            _controlToken = null;
        }
    }
}

public sealed record LauncherPairingPrompt(string PairingCode, DateTimeOffset ExpiresAtUtc, int ExpiresInSeconds)
{
    public override string ToString() => $"LauncherPairingPrompt([PAIRING CODE REDACTED], ExpiresAtUtc={ExpiresAtUtc:O}, ExpiresInSeconds={ExpiresInSeconds})";
}

public sealed record LauncherPairingExchangeResult(bool IsPending, DateTimeOffset? ExpiresAtUtc);

public sealed record LauncherUserIdentity(
    string UserId,
    string Username,
    bool IsActive,
    bool CanReadAi,
    bool CanWriteAi,
    Guid InstanceId)
{
    public override string ToString() => $"LauncherUserIdentity([USER REDACTED], InstanceId={InstanceId:D})";
}

public sealed record LauncherAiConfiguration(
    bool ApiKeySet,
    string? BaseUrl,
    bool BaseUrlRedacted,
    string? ModelName,
    int ConfigRevision,
    string ConfigId,
    string? ConfigSource,
    string? EffectiveState,
    bool? IsMultiUserMode,
    bool? NeedsSetup)
{
    public string? TokensParameter { get; init; }
    public int? TokensLimit { get; init; }
    public override string ToString() =>
        $"LauncherAiConfiguration(ApiKeySet={ApiKeySet}, ModelName={ModelName ?? "[none]"}, ConfigRevision={ConfigRevision}, API key=[REDACTED])";
}

public sealed record LauncherAiManualTestResult(
    bool Success,
    string Status,
    string Message,
    int? LatencyMilliseconds,
    DateTimeOffset? CheckedAtUtc)
{
    public override string ToString() =>
        $"LauncherAiManualTestResult(Success={Success}, Status={Status}, LatencyMilliseconds={LatencyMilliseconds?.ToString() ?? "unknown"}, CheckedAtUtc={CheckedAtUtc?.ToString("O") ?? "unknown"})";
}

public sealed record LauncherAiHealthSnapshot(
    string Freshness,
    string Category,
    DateTimeOffset? ObservedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    int? RemainingTtlSeconds,
    string ConfigId,
    int ConfigRevision,
    string Source,
    int? LatencyMilliseconds)
{
    public override string ToString() =>
        $"LauncherAiHealthSnapshot(Freshness={Freshness}, Category={Category}, ObservedAtUtc={ObservedAtUtc?.ToString("O") ?? "unknown"}, ConfigRevision={ConfigRevision}, LatencyMilliseconds={LatencyMilliseconds?.ToString() ?? "unknown"})";
}

public sealed class PortableLauncherUserClient : IDisposable
{
    private static readonly HttpRequestOptionsKey<long> UserSessionGenerationOption = new("LauncherUserSessionGeneration");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    private readonly PortableLauncherUserClientContext _context;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly object _sync = new();
    private byte[]? _pairingVerifier;
    private string? _pairingId;
    private Timer? _pairingExpiryTimer;
    private long _pairingGeneration;
    private byte[]? _accessToken;
    private DateTimeOffset? _accessTokenExpiresAtUtc;
    private Timer? _accessTokenExpiryTimer;
    private long _sessionGeneration;
    private string? _userId;
    private LauncherUserIdentity? _identityCache;
    private LauncherAiConfiguration? _aiCache;
    private bool _disposed;

    public event Action? UserDataInvalidated;

    public PortableLauncherUserClient(PortableLauncherUserClientContext context)
        : this(context, CreateSafeHttpClient(context), ownsHttpClient: true)
    {
    }

    internal PortableLauncherUserClient(
        PortableLauncherUserClientContext context,
        HttpMessageHandler handler)
        : this(context, CreateHttpClient(context, handler), ownsHttpClient: true)
    {
    }

    private PortableLauncherUserClient(
        PortableLauncherUserClientContext context,
        HttpClient httpClient,
        bool ownsHttpClient)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _httpClient.BaseAddress ??= context.BaseAddress;
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _ownsHttpClient = ownsHttpClient;
        _context.Invalidated += HandleContextInvalidated;
    }

    public async Task<LauncherPairingPrompt> BeginPairingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureContextCurrent();
        ClearPendingPairing();
        long pairingGeneration;
        lock (_sync)
        {
            pairingGeneration = _pairingGeneration;
        }

        var verifier = RandomNumberGenerator.GetBytes(32);
        var verifierText = Base64UrlEncode(verifier);
        using var request = CreateControlRequest(HttpMethod.Post, "internal/launcher/pairing");
        request.Content = JsonContent(new { instance_id = _context.InstanceId.ToString("D"), verifier = verifierText });
        try
        {
            using var response = await SendControlAsync(request, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, "创建配对请求");
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = json.RootElement;
            var pairingId = RequireString(root, "pairing_id", 128);
            var pairingCode = RequireString(root, "pairing_code", 12);
            var expiresAt = RequireDateTime(root, "expires_at");
            var expiresIn = RequireInt(root, "expires_in_seconds", 1, 300);
            if (pairingCode.Length != 12 || expiresAt <= DateTimeOffset.UtcNow)
            {
                throw ProtocolError("配对服务返回了无效的配对状态。");
            }

            lock (_sync)
            {
                ThrowIfDisposedCore();
                EnsurePairingGenerationOnlyCore(pairingGeneration);
                _pairingVerifier = verifier;
                _pairingId = pairingId;
                var installedGeneration = AdvanceGeneration(ref _pairingGeneration);
                _pairingExpiryTimer = new Timer(
                    static state =>
                    {
                        var callback = (Tuple<PortableLauncherUserClient, long>)state!;
                        callback.Item1.ExpirePairingIfCurrent(callback.Item2);
                    },
                    Tuple.Create(this, installedGeneration),
                    TimeSpan.FromSeconds(expiresIn),
                    Timeout.InfiniteTimeSpan);
                verifier = [];
            }

            return new LauncherPairingPrompt(pairingCode, expiresAt, expiresIn);
        }
        catch (LauncherUserClientException)
        {
            ClearPendingPairingIfGeneration(pairingGeneration);
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            ClearPendingPairingIfGeneration(pairingGeneration);
            throw ProtocolError("Launcher 配对响应无效，请重新发起配对。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(verifier);
        }
    }

    /// <summary>Polls once. A browser user must explicitly approve the displayed pairing code first.</summary>
    public async Task<LauncherPairingExchangeResult> ExchangePairingOnceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureContextCurrent();
        string pairingId;
        string verifier;
        long pairingGeneration;
        lock (_sync)
        {
            if (_pairingId is null || _pairingVerifier is null)
            {
                throw new InvalidOperationException("当前没有等待授权的配对请求。");
            }

            pairingId = _pairingId;
            verifier = Base64UrlEncode(_pairingVerifier);
            pairingGeneration = _pairingGeneration;
        }

        using var request = CreateControlRequest(HttpMethod.Post, "internal/launcher/pairing/exchange");
        request.Content = JsonContent(new { pairing_id = pairingId, verifier });
        try
        {
            using var response = await SendControlAsync(request, cancellationToken).ConfigureAwait(false);
            EnsurePairingGeneration(pairingGeneration);
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                using var pending = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                if (RequireString(pending.RootElement, "status", 16) != "pending")
                {
                    throw ProtocolError("配对服务返回了无效的等待状态。");
                }

                EnsurePairingGeneration(pairingGeneration);
                return new LauncherPairingExchangeResult(true, null);
            }

            try
            {
                EnsureSuccess(response, "兑换配对授权");
            }
            catch (LauncherUserClientException)
            {
                ClearPendingPairingIfGeneration(pairingGeneration);
                throw;
            }
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = json.RootElement;
            if (RequireString(root, "status", 16) != "exchanged" ||
                RequireString(root, "token_type", 16) != "Bearer" ||
                RequireString(root, "instance_id", 36) != _context.InstanceId.ToString("D"))
            {
                throw ProtocolError("配对服务返回了不匹配的会话上下文。");
            }

            var accessToken = RequireString(root, "access_token", 512);
            if (!accessToken.StartsWith("l1.", StringComparison.Ordinal) || accessToken.Length < 20)
            {
                throw ProtocolError("配对服务返回了无效的短期会话。");
            }

            var expiresAt = RequireDateTime(root, "expires_at");
            if (expiresAt <= DateTimeOffset.UtcNow)
            {
                throw ProtocolError("配对服务返回的会话已过期。");
            }

            lock (_sync)
            {
                ThrowIfDisposedCore();
                EnsurePairingGenerationCore(pairingGeneration);
                ClearUserSessionCore();
                _accessToken = Encoding.UTF8.GetBytes(accessToken);
                _accessTokenExpiresAtUtc = expiresAt;
                var sessionGeneration = AdvanceGeneration(ref _sessionGeneration);
                var remainingSessionTime = expiresAt - DateTimeOffset.UtcNow;
                _accessTokenExpiryTimer = new Timer(
                    static state =>
                    {
                        var callback = (Tuple<PortableLauncherUserClient, long>)state!;
                        callback.Item1.ExpireSessionIfCurrent(callback.Item2);
                    },
                    Tuple.Create(this, sessionGeneration),
                    remainingSessionTime > TimeSpan.Zero ? remainingSessionTime : TimeSpan.Zero,
                    Timeout.InfiniteTimeSpan);
                ClearPendingPairingCore();
            }

            return new LauncherPairingExchangeResult(false, expiresAt);
        }
        catch (LauncherUserClientException)
        {
            ClearPendingPairingIfGeneration(pairingGeneration);
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            ClearPendingPairingIfGeneration(pairingGeneration);
            throw ProtocolError("Launcher 配对响应无效，请重新发起配对。");
        }
        finally
        {
            verifier = string.Empty;
        }
    }

    public async Task<LauncherUserIdentity> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var request = CreateUserRequest(HttpMethod.Get, "api/launcher/me");
        var requestGeneration = GetRequestGeneration(request);
        using var response = await SendUserAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            ClearUserDataCacheIfSessionGeneration(requestGeneration);
        }

        EnsureSuccess(response, "读取当前用户");
        try
        {
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = json.RootElement;
            var identity = new LauncherUserIdentity(
                RequireString(root, "user_id", 256),
                RequireString(root, "username", 256),
                RequireBool(root, "is_active"),
                RequireNestedBool(root, "permissions", "ai_read"),
                RequireNestedBool(root, "permissions", "ai_write"),
                Guid.Parse(RequireString(root, "instance_id", 36)));
            if (identity.InstanceId != _context.InstanceId || !identity.IsActive || !identity.CanReadAi)
            {
                ClearUserDataCacheIfSessionGeneration(requestGeneration);
                throw ProtocolError("当前会话的用户或实例权限不匹配。");
            }

            lock (_sync)
            {
                EnsureSessionGenerationCore(requestGeneration);
                if (_userId is not null && !string.Equals(_userId, identity.UserId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("当前账号已切换，请重新配对后再读取用户数据。");
                }

                _userId = identity.UserId;
                _identityCache = identity;
                return identity;
            }
        }
        catch (LauncherUserClientException)
        {
            ClearUserDataCacheIfSessionGeneration(requestGeneration);
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            ClearUserDataCacheIfSessionGeneration(requestGeneration);
            throw ProtocolError("Launcher 服务返回的用户信息无效。");
        }
    }

    public async Task<LauncherAiConfiguration> GetAiConfigurationAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var request = CreateUserRequest(HttpMethod.Get, "api/launcher/ai");
        var requestGeneration = GetRequestGeneration(request);
        using var response = await SendUserAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            ClearUserDataCacheIfSessionGeneration(requestGeneration);
        }

        EnsureSuccess(response, "读取 AI 配置");
        try
        {
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var config = ParseAiConfiguration(json.RootElement);
            lock (_sync)
            {
                EnsureSessionGenerationCore(requestGeneration);
                _aiCache = config;
            }

            return config;
        }
        catch (LauncherUserClientException)
        {
            ClearUserDataCacheIfSessionGeneration(requestGeneration);
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            ClearUserDataCacheIfSessionGeneration(requestGeneration);
            throw ProtocolError("Launcher 服务返回的 AI 配置信息无效。");
        }
    }

    /// <summary>Reads the current user's cached AI health only; this method never runs a provider probe.</summary>
    public async Task<LauncherAiHealthSnapshot> GetAiHealthAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var request = CreateUserRequest(HttpMethod.Get, "api/launcher/ai/health");
        var requestGeneration = GetRequestGeneration(request);
        using var response = await SendUserAsync(
            request,
            cancellationToken,
            clearOnServerError: false).ConfigureAwait(false);
        EnsureSuccess(response, "读取 AI 健康缓存");

        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureSessionGeneration(requestGeneration);
        return ParseAiHealthSnapshot(json.RootElement);
    }

    /// <summary>Updates only explicitly supplied allowlisted fields. This call never sends an AI request.</summary>
    public async Task<LauncherAiConfiguration> SaveAiConfigurationAsync(
        int configRevision,
        string configId,
        string? baseUrl,
        string? modelName,
        string? replacementApiKey,
        bool removeApiKey = false,
        CancellationToken cancellationToken = default,
        string? tokensParameter = null,
        int? tokensLimit = null)
    {
        ThrowIfDisposed();
        if (configRevision < 0 || configId is null || configId.Length > 128 ||
            (configId.Length == 0 ? configRevision != 0 : string.IsNullOrWhiteSpace(configId)) ||
            (replacementApiKey is not null && (removeApiKey || replacementApiKey.Length > 4096)) ||
            (baseUrl is not null && (baseUrl.Length > 2048 || !IsSafeBaseUrl(baseUrl))) ||
            (modelName is not null && modelName.Length > 256) ||
            (tokensParameter is { Length: > 0 } && !System.Text.RegularExpressions.Regex.IsMatch(tokensParameter, @"\A[A-Za-z_][A-Za-z0-9_]{0,63}\z")) ||
            tokensLimit is <= 0)
        {
            throw new ArgumentException("AI 配置提交字段无效。");
        }

        using var fields = new MemoryStream();
        byte[]? payload = null;
        try
        {
            using (var writer = new Utf8JsonWriter(fields))
            {
                writer.WriteStartObject();
                writer.WriteNumber("config_revision", configRevision);
                writer.WriteString("config_id", configId);
                if (tokensParameter is not null) writer.WriteString("AI_MAX_TOKENS_PARAM_NAME", tokensParameter);
                if (tokensLimit is { } limit) writer.WriteNumber("AI_MAX_TOKENS_LIMIT", limit);
                if (baseUrl is not null)
                {
                    writer.WriteString("OPENAI_BASE_URL", baseUrl);
                }

                if (modelName is not null)
                {
                    writer.WriteString("OPENAI_MODEL_NAME", modelName);
                }

                if (replacementApiKey is not null)
                {
                    writer.WriteString("OPENAI_API_KEY", replacementApiKey);
                }

                if (removeApiKey)
                {
                    writer.WriteBoolean("remove_api_key", true);
                }

                writer.WriteEndObject();
            }

            payload = fields.ToArray();
            using var request = CreateUserRequest(HttpMethod.Put, "api/launcher/ai");
            var requestGeneration = GetRequestGeneration(request);
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await SendUserAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                ClearAiCacheIfSessionGeneration(requestGeneration);
                throw new LauncherUserClientException(HttpStatusCode.Conflict, "AI 配置已被其他页面修改，请刷新并核对后重试。");
            }

            EnsureSuccess(response, "保存 AI 配置");
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = json.RootElement;
            var savedRevision = RequireInt(root, "config_revision", 0, int.MaxValue);
            var savedConfigId = RequirePossiblyEmptyString(root, "config_id", 128);
            var responseMatchesRequest = configId.Length == 0
                ? (savedConfigId.Length == 0 && savedRevision == configRevision) ||
                    (savedRevision > configRevision && !string.IsNullOrWhiteSpace(savedConfigId))
                : savedRevision >= configRevision && string.Equals(savedConfigId, configId, StringComparison.Ordinal);
            if (!responseMatchesRequest)
            {
                throw ProtocolError("保存响应与提交的 AI 配置版本不匹配。");
            }

            EnsureSessionGeneration(requestGeneration);
            var saved = await GetAiConfigurationAsync(cancellationToken).ConfigureAwait(false);
            EnsureSessionGeneration(requestGeneration);
            return saved;
        }
        finally
        {
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }

            if (fields.TryGetBuffer(out var segment) && segment.Array is not null)
            {
                CryptographicOperations.ZeroMemory(segment.Array.AsSpan(segment.Offset, segment.Count));
            }
        }
    }

    /// <summary>
    /// Transports one explicitly confirmed test request. It never retries and never stores the result or draft.
    /// </summary>
    public async Task<LauncherAiManualTestResult> RunManualAiTestAsync(
        bool explicitlyConfirmed,
        string requestId,
        string expectedConfigId,
        int expectedConfigRevision,
        string? baseUrl,
        string? modelName,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!explicitlyConfirmed)
        {
            throw new InvalidOperationException("发送 AI 测试请求前必须取得用户明确确认。");
        }

        if (!IsRequestId(requestId) || expectedConfigId is null || expectedConfigId.Length > 128 ||
            (expectedConfigId.Length > 0 && string.IsNullOrWhiteSpace(expectedConfigId)) || expectedConfigRevision < 0 ||
            (expectedConfigId.Length == 0 && expectedConfigRevision != 0) ||
            (baseUrl is not null && (baseUrl.Length > 2048 || !IsSafeBaseUrl(baseUrl))) ||
            (modelName is not null && modelName.Length > 256) ||
            (apiKey is not null && apiKey.Length > 4096))
        {
            throw new ArgumentException("AI 测试请求字段无效。");
        }

        using var fields = new MemoryStream();
        byte[]? payload = null;
        try
        {
            using (var writer = new Utf8JsonWriter(fields))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("confirmed", true);
                writer.WriteString("request_id", requestId);
                writer.WriteString("expected_config_id", expectedConfigId);
                writer.WriteNumber("expected_config_revision", expectedConfigRevision);
                writer.WritePropertyName("settings");
                writer.WriteStartObject();
                if (baseUrl is not null)
                {
                    writer.WriteString("OPENAI_BASE_URL", baseUrl);
                }

                if (modelName is not null)
                {
                    writer.WriteString("OPENAI_MODEL_NAME", modelName);
                }

                if (apiKey is not null)
                {
                    writer.WriteString("OPENAI_API_KEY", apiKey);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            payload = fields.ToArray();
            using var request = CreateUserRequest(HttpMethod.Post, "api/launcher/ai/test");
            var requestGeneration = GetRequestGeneration(request);
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            try
            {
                using var response = await SendUserAsync(
                    request,
                    cancellationToken,
                    clearOnCancellation: false,
                    clearOnServerError: false).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    throw new LauncherUserClientException(
                        HttpStatusCode.Conflict,
                        "AI 配置已在确认后更改；请刷新配置并重新确认测试目标。");
                }

                EnsureSuccess(response, "发送 AI 手动测试");
                using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                EnsureSessionGeneration(requestGeneration);
                return ParseManualAiTestResult(json.RootElement);
            }
            catch (OperationCanceledException)
            {
                EnsureSessionGeneration(requestGeneration);
                return new LauncherAiManualTestResult(
                    false,
                    "unknown",
                    "测试结果未知；服务端可能已处理请求，请确认后再决定是否重试。",
                    null,
                    DateTimeOffset.UtcNow);
            }
        }
        finally
        {
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }

            if (fields.TryGetBuffer(out var segment) && segment.Array is not null)
            {
                CryptographicOperations.ZeroMemory(segment.Array.AsSpan(segment.Offset, segment.Count));
            }
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        byte[]? accessToken;
        long sessionGeneration;
        long pairingGeneration;
        lock (_sync)
        {
            accessToken = _accessToken?.ToArray();
            sessionGeneration = _sessionGeneration;
            pairingGeneration = _pairingGeneration;
        }

        ClearUserDataCacheIfSessionGeneration(sessionGeneration);
        ClearPendingPairingIfGeneration(pairingGeneration);

        if (accessToken is null)
        {
            return;
        }

        try
        {
            using var request = CreateControlRequest(HttpMethod.Post, "internal/launcher/session/revoke");
            request.Content = JsonContent(new { access_token = Encoding.UTF8.GetString(accessToken) });
            using var response = await SendControlAsync(request, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, "撤销 Launcher 会话");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(accessToken);
        }
    }

    public void ClearUserDataCache()
    {
        bool invalidated;
        lock (_sync)
        {
            invalidated = _identityCache is not null || _aiCache is not null || _accessToken is not null;
            ClearUserSessionCore();
            ClearPendingPairingCore();
        }

        if (invalidated)
        {
            UserDataInvalidated?.Invoke();
        }
    }

    public override string ToString() => "PortableLauncherUserClient([REDACTED])";

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ClearUserSessionCore();
            ClearPendingPairingCore();
        }

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        _context.Invalidated -= HandleContextInvalidated;
        _context.Dispose();
    }

    internal bool HasCachedUserData
    {
        get
        {
            lock (_sync)
            {
                return _identityCache is not null || _aiCache is not null;
            }
        }
    }

    internal long PairingGenerationForTests
    {
        get { lock (_sync) { return _pairingGeneration; } }
    }

    internal long SessionGenerationForTests
    {
        get { lock (_sync) { return _sessionGeneration; } }
    }

    internal bool ExpirePairingIfCurrentForTests(long generation) => ExpirePairingIfCurrent(generation);

    internal bool ExpireSessionIfCurrentForTests(long generation) => ExpireSessionIfCurrent(generation);

    private async Task<HttpResponseMessage> SendUserAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        bool clearOnCancellation = true,
        bool clearOnServerError = true)
    {
        var requestGeneration = GetRequestGeneration(request);
        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            EnsureNoRedirect(response);
            if (!IsCurrentSessionGeneration(requestGeneration))
            {
                response.Dispose();
                throw SessionChanged();
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || IsAccessTokenExpired())
            {
                var errorStatus = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? response.StatusCode
                    : HttpStatusCode.Unauthorized;
                response.Dispose();
                ClearUserDataCacheIfSessionGeneration(requestGeneration);
                throw new LauncherUserClientException(
                    errorStatus,
                    errorStatus is HttpStatusCode.Forbidden
                        ? "当前用户的 Launcher 权限已变化，请重新授权。"
                        : "Launcher 会话已过期，请重新授权。");
            }

            if (clearOnServerError && (int)response.StatusCode >= 500)
            {
                ClearUserDataCacheIfSessionGeneration(requestGeneration);
            }

            return response;
        }
        catch (HttpRequestException exception)
        {
            ClearUserDataCacheIfSessionGeneration(requestGeneration);
            throw new LauncherUserClientException(null, "便携实例本机服务暂不可用。", exception);
        }
        catch (OperationCanceledException)
        {
            if (clearOnCancellation)
            {
                ClearUserDataCacheIfSessionGeneration(requestGeneration);
            }

            throw;
        }
    }

    private async Task<HttpResponseMessage> SendControlAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            EnsureNoRedirect(response);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var statusCode = response.StatusCode;
                response.Dispose();
                ClearUserDataCache();
                ClearPendingPairing();
                _context.Invalidate();
                throw new LauncherUserClientException(statusCode, "本实例控制授权已失效，请重启服务后重新连接。");
            }

            return response;
        }
        catch (HttpRequestException exception)
        {
            throw new LauncherUserClientException(null, "便携实例本机服务暂不可用。", exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private HttpRequestMessage CreateControlRequest(HttpMethod method, string path)
    {
        EnsureContextCurrent();
        var request = new HttpRequestMessage(method, path);
        try
        {
            _context.AddControlAuthorization(request);
            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private HttpRequestMessage CreateUserRequest(HttpMethod method, string path)
    {
        EnsureContextCurrent();
        byte[]? accessToken = null;
        var expired = false;
        long sessionGeneration;
        lock (_sync)
        {
            ThrowIfDisposedCore();
            sessionGeneration = _sessionGeneration;
            if (_accessToken is null || _accessTokenExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                expired = true;
            }
            else
            {
                accessToken = _accessToken.ToArray();
            }
        }

        if (expired || accessToken is null)
        {
            ClearUserDataCacheIfSessionGeneration(sessionGeneration);
            throw new InvalidOperationException("请先完成 Launcher 配对授权。");
        }

        using var token = new ZeroingByteArray(accessToken);
        var request = new HttpRequestMessage(method, path);
        request.Options.Set(UserSessionGenerationOption, sessionGeneration);
        try
        {
            _context.AddUserAuthorization(request, Encoding.UTF8.GetString(token.Bytes));
            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private bool IsAccessTokenExpired()
    {
        lock (_sync)
        {
            return _accessToken is null || _accessTokenExpiresAtUtc <= DateTimeOffset.UtcNow;
        }
    }

    private void EnsureContextCurrent()
    {
        try
        {
            _context.EnsureCurrent();
        }
        catch (InvalidOperationException)
        {
            ClearUserDataCache();
            throw new InvalidOperationException("便携实例已停止或重启，用户授权和缓存已清除。");
        }
    }

    private void HandleContextInvalidated()
    {
        ClearUserDataCache();
    }

    private void ClearAiCacheIfSessionGeneration(long generation)
    {
        lock (_sync)
        {
            if (generation == _sessionGeneration)
            {
                _aiCache = null;
            }
        }
    }

    private void ClearUserDataCacheIfSessionGeneration(long generation)
    {
        bool invalidated;
        lock (_sync)
        {
            if (generation != _sessionGeneration)
            {
                return;
            }

            invalidated = _identityCache is not null || _aiCache is not null || _accessToken is not null;
            ClearUserSessionCore();
        }

        if (invalidated)
        {
            UserDataInvalidated?.Invoke();
        }
    }

    private void ClearPendingPairingIfGeneration(long generation)
    {
        lock (_sync)
        {
            if (generation == _pairingGeneration)
            {
                ClearPendingPairingCore();
            }
        }
    }

    private bool IsCurrentPairingGeneration(long generation)
    {
        lock (_sync)
        {
            return !_disposed && generation == _pairingGeneration && _pairingVerifier is not null && _pairingId is not null;
        }
    }

    private void EnsurePairingGeneration(long generation)
    {
        lock (_sync)
        {
            EnsurePairingGenerationCore(generation);
        }
    }

    private void EnsurePairingGenerationCore(long generation)
    {
        if (_disposed || generation != _pairingGeneration || _pairingVerifier is null || _pairingId is null)
        {
            throw new LauncherUserClientException(HttpStatusCode.Conflict, "配对请求已被替换或取消，此响应已丢弃。");
        }
    }

    private void EnsurePairingGenerationOnlyCore(long generation)
    {
        if (_disposed || generation != _pairingGeneration)
        {
            throw new LauncherUserClientException(HttpStatusCode.Conflict, "配对请求已被替换或取消，此响应已丢弃。");
        }
    }

    private bool IsCurrentSessionGeneration(long generation)
    {
        lock (_sync)
        {
            return generation == _sessionGeneration && _accessToken is not null &&
                _accessTokenExpiresAtUtc > DateTimeOffset.UtcNow;
        }
    }

    private void EnsureSessionGeneration(long generation)
    {
        lock (_sync)
        {
            EnsureSessionGenerationCore(generation);
        }
    }

    private void EnsureSessionGenerationCore(long generation)
    {
        if (_disposed || generation != _sessionGeneration || _accessToken is null ||
            _accessTokenExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw SessionChanged();
        }
    }

    private static long GetRequestGeneration(HttpRequestMessage request) =>
        request.Options.TryGetValue(UserSessionGenerationOption, out var generation)
            ? generation
            : throw new InvalidOperationException("Launcher 用户请求缺少会话代际。");

    private static LauncherUserClientException SessionChanged() =>
        new(HttpStatusCode.Unauthorized, "Launcher 用户会话已退出或切换，此请求结果已丢弃。");

    private void ClearUserSessionCore()
    {
        AdvanceGeneration(ref _sessionGeneration);
        _accessTokenExpiryTimer?.Dispose();
        _accessTokenExpiryTimer = null;
        if (_accessToken is not null)
        {
            CryptographicOperations.ZeroMemory(_accessToken);
            _accessToken = null;
        }

        _accessTokenExpiresAtUtc = null;
        _userId = null;
        _identityCache = null;
        _aiCache = null;
    }

    private void ClearPendingPairing()
    {
        lock (_sync)
        {
            ClearPendingPairingCore();
        }
    }

    private void ClearPendingPairingCore()
    {
        AdvanceGeneration(ref _pairingGeneration);
        _pairingExpiryTimer?.Dispose();
        _pairingExpiryTimer = null;
        if (_pairingVerifier is not null)
        {
            CryptographicOperations.ZeroMemory(_pairingVerifier);
            _pairingVerifier = null;
        }

        _pairingId = null;
    }

    private void ThrowIfDisposed()
    {
        lock (_sync)
        {
            ThrowIfDisposedCore();
        }
    }

    private void ThrowIfDisposedCore() => ObjectDisposedException.ThrowIf(_disposed, this);

    private bool ExpirePairingIfCurrent(long generation)
    {
        lock (_sync)
        {
            if (generation != _pairingGeneration || _pairingId is null || _pairingVerifier is null)
            {
                return false;
            }

            ClearPendingPairingCore();
            return true;
        }
    }

    private bool ExpireSessionIfCurrent(long generation)
    {
        bool invalidated;
        lock (_sync)
        {
            if (generation != _sessionGeneration || _accessToken is null)
            {
                return false;
            }

            invalidated = _identityCache is not null || _aiCache is not null || _accessToken is not null;
            ClearUserSessionCore();
        }

        if (invalidated)
        {
            UserDataInvalidated?.Invoke();
        }

        return true;
    }

    private static long AdvanceGeneration(ref long generation)
    {
        generation = generation == long.MaxValue ? 1 : generation + 1;
        return generation;
    }

    private static bool IsSafeBaseUrl(string baseUrl)
    {
        if (baseUrl.Contains('?', StringComparison.Ordinal) || baseUrl.Contains('#', StringComparison.Ordinal) ||
            !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return true;
    }

    private static LauncherAiConfiguration ParseAiConfiguration(JsonElement root)
    {
        var baseUrl = OptionalString(root, "OPENAI_BASE_URL", 2048);
        var redacted = OptionalBool(root, "OPENAI_BASE_URL_REDACTED") ?? false;
        if (baseUrl is not null && Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed) &&
            (!string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment)))
        {
            baseUrl = null;
            redacted = true;
        }

        var configRevision = RequireInt(root, "config_revision", 0, int.MaxValue);
        var configId = RequirePossiblyEmptyString(root, "config_id", 128);
        if ((configId.Length == 0) != (configRevision == 0))
        {
            throw ProtocolError("Launcher 服务返回的 AI 配置身份和版本无效。");
        }

        return new LauncherAiConfiguration(
            OptionalBool(root, "OPENAI_API_KEY_SET") ?? false,
            baseUrl,
            redacted,
            OptionalString(root, "OPENAI_MODEL_NAME", 256),
            configRevision,
            configId,
            OptionalString(root, "config_source", 128),
            OptionalString(root, "effective_state", 128),
            OptionalBool(root, "IS_MULTI_USER_MODE"),
            OptionalBool(root, "NEEDS_SETUP"))
        {
            TokensParameter = OptionalString(root, "AI_MAX_TOKENS_PARAM_NAME", 64),
            TokensLimit = !root.TryGetProperty("AI_MAX_TOKENS_LIMIT", out var limit) || limit.ValueKind == JsonValueKind.Null ||
                (limit.ValueKind == JsonValueKind.String && limit.GetString() == "")
                ? null : RequireInt(root, "AI_MAX_TOKENS_LIMIT", 1, int.MaxValue),
        };
    }

    private static LauncherAiHealthSnapshot ParseAiHealthSnapshot(JsonElement root)
    {
        var freshness = RequireString(root, "status", 32);
        if (freshness is not ("never_checked" or "unconfigured" or "config_changed" or "stale" or "current"))
        {
            throw ProtocolError("Launcher AI 健康缓存状态无效。");
        }

        var category = RequireString(root, "category", 32);
        if (category is not ("healthy" or "partial" or "timeout" or "auth_failed" or
            "rate_limited" or "unavailable" or "unknown" or "unconfigured"))
        {
            throw ProtocolError("Launcher AI 健康分类无效。");
        }

        var configId = OptionalString(root, "config_id", 128) ?? string.Empty;
        var configRevision = RequireInt(root, "config_revision", 0, int.MaxValue);
        var source = RequireString(root, "source", 64);
        if (!string.Equals(source, "postgres_user_config", StringComparison.Ordinal))
        {
            throw ProtocolError("Launcher AI 健康缓存来源无效。");
        }

        var observedAt = OptionalUtcDateTime(root, "observed_at");
        var expiresAt = OptionalUtcDateTime(root, "expires_at");
        if ((observedAt is null) != (expiresAt is null))
        {
            throw ProtocolError("Launcher AI 健康缓存时间窗口不完整。");
        }

        if (freshness == "never_checked" && (observedAt is not null || category != "unknown") ||
            freshness == "unconfigured" && category != "unconfigured" ||
            freshness == "config_changed" && category != "unknown")
        {
            throw ProtocolError("Launcher AI 健康新鲜度与分类不一致。");
        }

        int? remainingTtl = null;
        if (observedAt is { } observed && expiresAt is { } expires)
        {
            var cacheTtl = (expires - observed).TotalSeconds;
            if (cacheTtl is < 0 or > 305)
            {
                throw ProtocolError("Launcher AI 健康缓存 TTL 无效。");
            }

            if (freshness == "current")
            {
                remainingTtl = Math.Clamp((int)Math.Ceiling((expires - DateTimeOffset.UtcNow).TotalSeconds), 0, 300);
            }
            else if (freshness == "stale")
            {
                remainingTtl = 0;
            }
        }

        int? latency = null;
        if (root.TryGetProperty("latency_ms", out var latencyElement) && latencyElement.ValueKind is not JsonValueKind.Null)
        {
            if (!latencyElement.TryGetInt32(out var value) || value is < 0 or > 120_000)
            {
                throw ProtocolError("Launcher AI 健康缓存耗时无效。");
            }

            latency = value;
        }

        return new LauncherAiHealthSnapshot(
            freshness,
            category,
            observedAt,
            expiresAt,
            remainingTtl,
            configId,
            configRevision,
            source,
            latency);
    }

    private static DateTimeOffset? OptionalUtcDateTime(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind is not JsonValueKind.String || value.GetString()?.Length > 64 ||
            !DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw ProtocolError("Launcher AI 健康缓存时间无效。");
        }

        return parsed.ToUniversalTime();
    }

    private static LauncherAiManualTestResult ParseManualAiTestResult(JsonElement root)
    {
        var success = RequireBool(root, "success");
        var backendMessage = RequireString(root, "message", 512);
        if (!root.TryGetProperty("test", out var test) || test.ValueKind is not JsonValueKind.Object)
        {
            throw ProtocolError("Launcher 测试服务响应无效。");
        }

        var status = RequireString(test, "status", 32);
        var testMessage = RequireString(test, "message", 512);
        var message = GetSafeManualAiTestMessage(status, backendMessage, testMessage);

        int? latency = null;
        if (test.TryGetProperty("latency_ms", out var latencyElement) && latencyElement.ValueKind is not JsonValueKind.Null)
        {
            if (!latencyElement.TryGetInt32(out var value) || value is < 0 or > 120_000)
            {
                throw ProtocolError("Launcher 测试服务返回了无效耗时。");
            }

            latency = value;
        }

        DateTimeOffset? checkedAt = null;
        if (test.TryGetProperty("checked_at", out var checkedAtElement) && checkedAtElement.ValueKind is not JsonValueKind.Null)
        {
            if (checkedAtElement.ValueKind is not JsonValueKind.String || checkedAtElement.GetString()?.Length > 64 ||
                !DateTimeOffset.TryParse(checkedAtElement.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                throw ProtocolError("Launcher 测试服务返回了无效时间。");
            }

            checkedAt = parsed;
        }

        if (success != (status == "success"))
        {
            throw ProtocolError("Launcher 测试服务返回的结果状态不一致。");
        }

        return new LauncherAiManualTestResult(success, status, message, latency, checkedAt);
    }

    private static string GetSafeManualAiTestMessage(string status, string outerMessage, string testMessage)
    {
        return status switch
        {
            "success" => "AI 模型连接测试成功。",
            "rejected" => MapRejectedMessage(testMessage) ?? MapRejectedMessage(outerMessage) ??
                "当前配置不符合测试策略，未发送请求。",
            "auth_failed" => "AI 服务认证失败。",
            "rate_limited" => "AI 服务或手动测试请求已限流。",
            "model_unavailable" => "AI 模型或接口不可用。",
            "error" => "AI 测试请求失败。",
            "unknown" => MapUnknownMessage(testMessage) ?? MapUnknownMessage(outerMessage) ??
                "测试结果未知；服务端可能已处理或计费，请确认后再决定是否重试。",
            _ => throw ProtocolError("Launcher 测试服务返回了未知状态。"),
        };
    }

    private static string? MapRejectedMessage(string text) => text switch
    {
        "目标地址解析超时，已阻止请求。" => "目标地址解析超时，未发送 AI 请求。",
        "目标 URL 不符合安全策略。" => "目标 URL 不符合安全策略，未发送 AI 请求。",
        "AI 配置不完整。" => "AI 配置不完整，未发送请求。",
        "AI 配置字段超出长度限制。" => "AI 配置字段超出长度限制，未发送请求。",
        "当前配置启用了 AI 代理；手动测试暂不支持代理，未发送请求。" => "当前 AI 代理配置不支持手动测试，未发送请求。",
        "目标地址已更改，请为新目标重新输入 API Key。" => "AI 服务目标已变化，请重新输入 API Key 后测试。",
        _ => null,
    };

    private static string? MapUnknownMessage(string text) => text switch
    {
        "请求超时，服务端是否处理或计费未知。" => "AI 请求超时；服务端可能已处理或计费，系统没有自动重试。",
        "网络请求结果未知。" => "网络请求结果未知；服务端可能已处理或计费，系统没有自动重试。",
        "测试任务被取消，服务端是否处理或计费未知。" => "测试任务已取消；服务端可能已处理或计费，系统没有自动重试。",
        "测试请求结果未知。" => "测试结果未知；服务端可能已处理或计费，系统没有自动重试。",
        _ => null,
    };

    private static bool IsRequestId(string requestId) =>
        requestId is { Length: >= 8 and <= 128 } &&
        requestId.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static HttpClient CreateSafeHttpClient(PortableLauncherUserClientContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        return CreateHttpClient(context, handler);
    }

    private static HttpClient CreateHttpClient(PortableLauncherUserClientContext context, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(handler);
        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = context.BaseAddress,
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    private static HttpContent JsonContent<T>(T value) =>
        new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedContentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            throw ProtocolError("Launcher 服务响应大小无效。");
        }

        try
        {
            // JsonDocument retains the supplied byte array as its backing store;
            // parse a disposable copy so the transport buffer can be zeroed below.
            return JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException exception)
        {
            throw new LauncherUserClientException(response.StatusCode, "Launcher 服务返回了无效响应。", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Launcher 会话或实例控制授权已失效。",
            HttpStatusCode.Forbidden => "当前会话没有执行此操作的权限。",
            HttpStatusCode.Conflict => "数据已被其他页面修改，请刷新后核对。",
            (HttpStatusCode)429 => "配对请求过于频繁，请稍后再试。",
            _ => $"{operation}失败（HTTP {(int)response.StatusCode}）。",
        };

        throw new LauncherUserClientException(response.StatusCode, message);
    }

    private static void EnsureNoRedirect(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            response.Dispose();
            throw new LauncherUserClientException(response.StatusCode, "便携服务拒绝了跨路由重定向。");
        }
    }

    private static async Task<byte[]> ReadBoundedContentAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 64 * 1024)
        {
            throw ProtocolError("Launcher 服务响应超过 64 KiB 上限。");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > 64 * 1024)
            {
                throw ProtocolError("Launcher 服务响应超过 64 KiB 上限。");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return output.ToArray();
    }

    private static string RequireString(JsonElement root, string name, int maximumLength)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind is not JsonValueKind.String)
        {
            throw ProtocolError("Launcher 服务响应缺少必要字段。");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength)
        {
            throw ProtocolError("Launcher 服务响应字段无效。");
        }

        return text;
    }

    private static string RequirePossiblyEmptyString(JsonElement root, string name, int maximumLength)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind is not JsonValueKind.String)
        {
            throw ProtocolError("Launcher 服务响应缺少必要字段。");
        }

        var text = value.GetString();
        if (text is null || text.Length > maximumLength || (text.Length > 0 && string.IsNullOrWhiteSpace(text)))
        {
            throw ProtocolError("Launcher 服务响应字段无效。");
        }

        return text;
    }

    private static string? OptionalString(JsonElement root, string name, int maximumLength)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind is not JsonValueKind.String)
        {
            throw ProtocolError("Launcher 服务响应字段无效。");
        }

        var text = value.GetString();
        if (text?.Length > maximumLength)
        {
            throw ProtocolError("Launcher 服务响应字段无效。");
        }

        return text;
    }

    private static bool RequireBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw ProtocolError("Launcher 服务响应缺少必要字段。");
        }

        return value.GetBoolean();
    }

    private static bool? OptionalBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw ProtocolError("Launcher 服务响应字段无效。"),
            }
            : null;

    private static bool RequireNestedBool(JsonElement root, string parent, string child)
    {
        if (!root.TryGetProperty(parent, out var nested) || nested.ValueKind is not JsonValueKind.Object)
        {
            throw ProtocolError("Launcher 服务响应缺少权限信息。");
        }

        return RequireBool(nested, child);
    }

    private static int RequireInt(JsonElement root, string name, int minimum, int maximum)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetInt32(out var number) || number < minimum || number > maximum)
        {
            throw ProtocolError("Launcher 服务响应字段无效。");
        }

        return number;
    }

    private static DateTimeOffset RequireDateTime(JsonElement root, string name)
    {
        var value = RequireString(root, name, 64);
        if (!DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw ProtocolError("Launcher 服务返回了无效的 UTC 时间。");
        }

        return parsed;
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static LauncherUserClientException ProtocolError(string message) => new(null, message);

    private sealed class ZeroingByteArray : IDisposable
    {
        public ZeroingByteArray(byte[] bytes) => Bytes = bytes;

        public byte[] Bytes { get; }

        public void Dispose() => CryptographicOperations.ZeroMemory(Bytes);
    }
}

public sealed class LauncherUserClientException : InvalidOperationException
{
    public LauncherUserClientException(HttpStatusCode? statusCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }

    public override string ToString() => $"{nameof(LauncherUserClientException)}({StatusCode?.ToString() ?? "local"}): {Message}";
}

internal static class LauncherUserContextGuard
{
    internal static void EnsureReady(
        PythonReadinessResult? readiness,
        OwnedProcessIdentity? readinessIdentity,
        OwnedProcessIdentity? identity,
        string? controlToken)
    {
        if (identity is null || string.IsNullOrEmpty(controlToken) ||
            !SameIdentity(readinessIdentity, identity) ||
            readiness?.State is not PythonReadinessState.Ready || readiness.SetupRequired is true)
        {
            throw new PythonLifecycleException("Launcher 用户授权仅能在本实例服务就绪且无需首次设置时建立。");
        }
    }

    internal static void EnsureOwned(
        Guid expectedInstanceId,
        OwnedProcessIdentity expectedIdentity,
        OwnedProcessOperationResult refreshed,
        OwnedProcessIdentity? observedIdentity,
        PythonRuntimeRecord runtime)
    {
        if (expectedInstanceId == Guid.Empty || !refreshed.Succeeded ||
            refreshed.Snapshot.State is not OwnedProcessState.Running ||
            !SameIdentity(refreshed.Snapshot.Identity, expectedIdentity) ||
            !SameIdentity(observedIdentity, expectedIdentity) ||
            !SameIdentity(runtime.Identity, expectedIdentity) || runtime.InstanceId != expectedInstanceId)
        {
            throw new PythonLifecycleException("创建 Launcher 用户上下文时无法核验本实例 Python 进程身份。");
        }
    }

    internal static void EnsureControlCredentialMatches(string protectedCredential, string liveCredential)
    {
        var left = Encoding.UTF8.GetBytes(protectedCredential);
        var right = Encoding.UTF8.GetBytes(liveCredential);
        try
        {
            if (left.Length != right.Length || !CryptographicOperations.FixedTimeEquals(left, right))
            {
                throw new PythonLifecycleException("当前进程控制凭据与受保护实例凭据不匹配。");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(left);
            CryptographicOperations.ZeroMemory(right);
        }
    }

    private static bool SameIdentity(OwnedProcessIdentity? left, OwnedProcessIdentity? right) =>
        left is not null && right is not null && left.ProcessId == right.ProcessId &&
        left.StartedAtUtc.UtcTicks == right.StartedAtUtc.UtcTicks && left.InstanceId == right.InstanceId &&
        left.RunId == right.RunId &&
        string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.InstanceRoot, right.InstanceRoot, StringComparison.OrdinalIgnoreCase);
}
