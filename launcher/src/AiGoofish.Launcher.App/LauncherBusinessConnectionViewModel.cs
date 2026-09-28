using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using AiGoofish.Launcher.Platform.Windows;
using Avalonia.Threading;

namespace AiGoofish.Launcher.App;

internal interface ILauncherBusinessUserClient : IDisposable
{
    event Action? UserDataInvalidated;

    Task<LauncherPairingPrompt> BeginPairingAsync(CancellationToken cancellationToken = default);
    Task<LauncherPairingExchangeResult> ExchangePairingOnceAsync(CancellationToken cancellationToken = default);
    Task<LauncherUserIdentity> GetCurrentUserAsync(CancellationToken cancellationToken = default);
    Task<LauncherAiConfiguration> GetAiConfigurationAsync(CancellationToken cancellationToken = default);
    Task<LauncherAiHealthSnapshot> GetAiHealthAsync(CancellationToken cancellationToken = default);
    Task<LauncherAiConfiguration> SaveAiConfigurationAsync(
        int configRevision,
        string configId,
        string? baseUrl,
        string? modelName,
        string? replacementApiKey,
        bool removeApiKey = false,
        CancellationToken cancellationToken = default,
        string? tokensParameter = null,
        int? tokensLimit = null);
    Task<LauncherAiManualTestResult> RunManualAiTestAsync(
        bool explicitlyConfirmed,
        string requestId,
        string expectedConfigId,
        int expectedConfigRevision,
        string? baseUrl,
        string? modelName,
        string? apiKey,
        CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
    void ClearUserDataCache();
}

internal sealed class PortableLauncherBusinessUserClient : ILauncherBusinessUserClient
{
    private readonly PortableLauncherUserClient _client;

    public PortableLauncherBusinessUserClient(PortableLauncherUserClientContext context) =>
        _client = new PortableLauncherUserClient(context);

    public event Action? UserDataInvalidated
    {
        add => _client.UserDataInvalidated += value;
        remove => _client.UserDataInvalidated -= value;
    }

    public Task<LauncherPairingPrompt> BeginPairingAsync(CancellationToken cancellationToken = default) =>
        _client.BeginPairingAsync(cancellationToken);

    public Task<LauncherPairingExchangeResult> ExchangePairingOnceAsync(CancellationToken cancellationToken = default) =>
        _client.ExchangePairingOnceAsync(cancellationToken);

    public Task<LauncherUserIdentity> GetCurrentUserAsync(CancellationToken cancellationToken = default) =>
        _client.GetCurrentUserAsync(cancellationToken);

    public Task<LauncherAiConfiguration> GetAiConfigurationAsync(CancellationToken cancellationToken = default) =>
        _client.GetAiConfigurationAsync(cancellationToken);

    public Task<LauncherAiHealthSnapshot> GetAiHealthAsync(CancellationToken cancellationToken = default) =>
        _client.GetAiHealthAsync(cancellationToken);

    public Task<LauncherAiConfiguration> SaveAiConfigurationAsync(
        int configRevision,
        string configId,
        string? baseUrl,
        string? modelName,
        string? replacementApiKey,
        bool removeApiKey = false,
        CancellationToken cancellationToken = default,
        string? tokensParameter = null,
        int? tokensLimit = null) =>
        _client.SaveAiConfigurationAsync(
            configRevision, configId, baseUrl, modelName, replacementApiKey, removeApiKey, cancellationToken, tokensParameter, tokensLimit);

    public Task<LauncherAiManualTestResult> RunManualAiTestAsync(
        bool explicitlyConfirmed,
        string requestId,
        string expectedConfigId,
        int expectedConfigRevision,
        string? baseUrl,
        string? modelName,
        string? apiKey,
        CancellationToken cancellationToken = default) =>
        _client.RunManualAiTestAsync(
            explicitlyConfirmed, requestId, expectedConfigId, expectedConfigRevision, baseUrl, modelName, apiKey, cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken = default) => _client.LogoutAsync(cancellationToken);

    public void ClearUserDataCache() => _client.ClearUserDataCache();

    public void Dispose() => _client.Dispose();
}

internal sealed record LauncherManualAiTestPreview(
    long Generation,
    long ClientEpoch,
    string UserId,
    string ConfigId,
    int ConfigRevision,
    bool UseDraft,
    string TargetOrigin,
    string ModelName,
    bool UsesReplacementApiKey);

public sealed class LauncherBusinessConnectionViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private string _tokensParameter = string.Empty;
    private string _tokensLimit = string.Empty;
    private bool _tokensDirty;
    public string TokensParameter
    {
        get => _tokensParameter;
        set { if (SetField(ref _tokensParameter, value ?? string.Empty)) OnAdvancedDraftChanged(); }
    }
    public string TokensLimit
    {
        get => _tokensLimit;
        set { if (SetField(ref _tokensLimit, value ?? string.Empty)) OnAdvancedDraftChanged(); }
    }
    public bool HasAdvancedDraft => _tokensDirty;
    private void OnAdvancedDraftChanged()
    {
        _tokensDirty = true;
        ClearServerValidationError();
        InvalidateAiActionCards();
        NotifyActions();
    }
    private static readonly TimeSpan PairingPollInterval = TimeSpan.FromSeconds(2);
    private readonly Func<CancellationToken, Task<ILauncherBusinessUserClient>> _clientFactory;
    private readonly Func<string, bool> _openExternal;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private System.Threading.Timer? _healthFreshnessTimer;
    private ILauncherBusinessUserClient? _client;
    private Action? _clientInvalidatedHandler;
    private long _clientEpoch;
    private CancellationTokenSource? _pairingCancellation;
    private Task? _pairingPollTask;
    private LauncherPairingPrompt? _pairingPrompt;
    private LauncherUserIdentity? _identity;
    private LauncherAiConfiguration? _configuration;
    private bool _hostReady;
    private bool _isPairing;
    private bool _isSaving;
    private bool _isCheckingConfiguration;
    private bool _isHealthLoading;
    private bool _isManualTestRunning;
    private bool _manualTestRequiresConfigurationRefresh;
    private bool _baseUrlDirty;
    private bool _modelDirty;
    private bool _removeKeyRequested;
    private string _status = "启动并完成 Web 首次设置后，可在此连接当前账号。";
    private string _pairingCode = string.Empty;
    private string _pairingExpiry = string.Empty;
    private string _accountSummary = string.Empty;
    private string _baseUrl = string.Empty;
    private string _modelName = string.Empty;
    private string _apiKeyDraft = string.Empty;
    private string _keyState = "尚未读取";
    private string _configSource = "尚未读取";
    private string _effectiveState = "尚未读取";
    private string _baseUrlError = string.Empty;
    private string _modelNameError = string.Empty;
    private string _apiKeyError = string.Empty;
    private string _serverValidationError = string.Empty;
    private string _validationSummary = string.Empty;
    private string _validationFocusTarget = string.Empty;
    private LauncherAiHealthSnapshot? _aiHealth;
    private LauncherAiManualTestResult? _manualTestResult;
    private string _manualTestSource = string.Empty;
    private string _healthMessage = "尚未读取健康缓存。";
    private string _healthObservedText = "尚无健康观测时间。";
    private string _manualTestMessage = string.Empty;
    private string _manualTestTime = string.Empty;
    private string _manualTestLatency = string.Empty;
    private CancellationTokenSource? _manualTestCancellation;
    private CancellationTokenSource? _healthCancellation;
    private long _aiCardGeneration;
    private long _healthRequestGeneration;

    internal LauncherBusinessConnectionViewModel(
        Func<CancellationToken, Task<ILauncherBusinessUserClient>> clientFactory,
        Func<string, bool> openExternal,
        bool isSimulation = false,
        TimeSpan? pairingPollInterval = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _openExternal = openExternal ?? throw new ArgumentNullException(nameof(openExternal));
        IsSimulation = isSimulation;
        PollInterval = pairingPollInterval ?? PairingPollInterval;
    }

    private TimeSpan PollInterval { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSimulation { get; }

    public bool HostReady
    {
        get => _hostReady;
        private set
        {
            if (SetField(ref _hostReady, value))
            {
                NotifyActions();
            }
        }
    }

    public bool IsPairing { get => _isPairing; private set { if (SetField(ref _isPairing, value)) NotifyActions(); } }
    public bool IsSaving { get => _isSaving; private set { if (SetField(ref _isSaving, value)) NotifyActions(); } }
    public bool IsCheckingConfiguration { get => _isCheckingConfiguration; private set { if (SetField(ref _isCheckingConfiguration, value)) NotifyActions(); } }
    public bool IsHealthLoading { get => _isHealthLoading; private set { if (SetField(ref _isHealthLoading, value)) NotifyActions(); } }
    public bool IsManualTestRunning { get => _isManualTestRunning; private set { if (SetField(ref _isManualTestRunning, value)) NotifyActions(); } }
    public bool IsPaired => _identity is not null;
    public bool CanReadAi => _identity?.CanReadAi is true;
    public bool CanWriteAi => _identity?.CanWriteAi is true;
    public bool CanBeginPairing => HostReady && !IsSimulation && _client is null && !IsPairing && !IsSaving;
    public bool CanCancelPairing => IsPairing;
    public bool CanLogout => _identity is not null && !IsSaving;
    public bool CanSave => HostReady && CanWriteAi && _configuration is not null && !IsSaving && !IsManualTestRunning && !IsHealthLoading &&
        HasUnsavedChanges;
    public bool CanCheckConfiguration => HostReady && CanReadAi && _configuration is not null && !IsCheckingConfiguration && !IsSaving && !IsManualTestRunning;
    public bool CanRefreshAiHealth => HostReady && CanReadAi && _client is not null && !IsHealthLoading && !IsManualTestRunning;
    public bool CanReadAiHealth => CanRefreshAiHealth;
    public bool CanRunManualTest => HostReady && CanWriteAi && _configuration is not null && _client is not null &&
        !IsPairing && !IsSaving && !IsManualTestRunning && !_manualTestRequiresConfigurationRefresh && !_tokensDirty;
    public bool CanCancelManualTest => IsManualTestRunning;
    public bool HasAiHealth => _aiHealth is not null;
    public bool HasManualTestResult => _manualTestResult is not null;
    public bool ShowManualTestStatus => HasManualTestResult || IsManualTestRunning;
    public bool IsHealthHealthy => _aiHealth is { Category: "healthy" } health &&
        IsAiHealthSnapshotCurrentAt(health, _configuration, DateTimeOffset.UtcNow);
    public string HealthMessage { get => _healthMessage; private set => SetField(ref _healthMessage, value); }
    public string HealthObservedText { get => _healthObservedText; private set => SetField(ref _healthObservedText, value); }
    public string HealthFreshnessText => _aiHealth is null ? "新鲜度：尚未读取" :
        IsAiHealthSnapshotCurrentAt(_aiHealth, _configuration, DateTimeOffset.UtcNow) ? "新鲜度：当前缓存" :
        _aiHealth.Freshness switch
        {
            "never_checked" => "新鲜度：尚未检测",
            "unconfigured" => "新鲜度：未配置",
            "config_changed" => "新鲜度：配置已变化",
            "stale" or "current" => "新鲜度：已过期或不可确认",
            _ => "新鲜度：未知",
        };
    public string HealthCategoryText => _aiHealth is null ? "尚无缓存" :
        _aiHealth.Freshness == "current" && !IsAiHealthSnapshotCurrentAt(_aiHealth, _configuration, DateTimeOffset.UtcNow)
            ? "缓存已过期或不可确认"
            : _aiHealth.Category switch
            {
                "healthy" => "连接可用",
                "partial" => "部分可用",
                "timeout" => "最近缓存为超时",
                "auth_failed" => "最近缓存为认证失败",
                "rate_limited" => "最近缓存为限流",
                "unavailable" => "最近缓存为不可用",
                "unconfigured" => "未配置",
                _ => "未知",
            };
    public string HealthSourceText => _aiHealth?.Source == "postgres_user_config" ? "来源：当前用户默认配置" : "来源未知";
    public string ManualTestMessage { get => _manualTestMessage; private set => SetField(ref _manualTestMessage, value); }
    public string ManualTestTime { get => _manualTestTime; private set => SetField(ref _manualTestTime, value); }
    public string ManualTestLatency { get => _manualTestLatency; private set => SetField(ref _manualTestLatency, value); }
    public string ManualTestSource { get => _manualTestSource; private set => SetField(ref _manualTestSource, value); }
    public bool IsManualTestUnknown => _manualTestResult?.Status == "unknown";
    public bool HasUnsavedChanges => _baseUrlDirty || _modelDirty || !string.IsNullOrEmpty(_apiKeyDraft) || _removeKeyRequested || _tokensDirty;
    public bool RequiresBaseUrlTargetConfirmation => _configuration is not null && !_configuration.BaseUrlRedacted &&
        _baseUrlDirty && IsOriginChanged(_configuration.BaseUrl, BaseUrl);
    public string BaseUrlTargetSummary => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
        ? $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}"
        : "无法识别的目标地址";
    public bool CanRefreshConfiguration => HostReady && IsPaired && !IsSaving && !IsManualTestRunning;
    public bool CanRemoveApiKey => HostReady && CanWriteAi && _configuration?.ApiKeySet is true && !IsSaving;
    public bool IsConfigurationVisible => _identity is not null;
    public bool IsBaseUrlRedacted => _configuration?.BaseUrlRedacted is true;
    public bool IsBaseUrlEditable => CanWriteAi && !IsBaseUrlRedacted && !IsSaving && !IsManualTestRunning;
    public bool IsModelEditable => CanWriteAi && !IsSaving && !IsManualTestRunning;
    public bool IsApiKeyEditable => CanWriteAi && !IsSaving && !IsManualTestRunning;
    public bool IsPairingCodeVisible => _pairingPrompt is not null;
    public string PairingCode { get => _pairingCode; private set => SetField(ref _pairingCode, value); }
    public string PairingExpiry { get => _pairingExpiry; private set => SetField(ref _pairingExpiry, value); }
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public string AccountSummary { get => _accountSummary; private set => SetField(ref _accountSummary, value); }
    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            if (SetField(ref _baseUrl, value ?? string.Empty))
            {
                _baseUrlDirty = true;
                InvalidateAiActionCards();
                BaseUrlError = string.Empty;
                ClearServerValidationError();
                NotifyActions();
                NotifyTargetProperties();
            }
        }
    }
    public string ModelName
    {
        get => _modelName;
        set
        {
            if (SetField(ref _modelName, value ?? string.Empty))
            {
                _modelDirty = true;
                InvalidateAiActionCards();
                ModelNameError = string.Empty;
                ClearServerValidationError();
                NotifyActions();
            }
        }
    }
    public string ApiKeyDraft
    {
        get => _apiKeyDraft;
        set
        {
            if (SetField(ref _apiKeyDraft, value ?? string.Empty))
            {
                ClearServerValidationError();
                ApiKeyError = string.Empty;
                InvalidateAiActionCards();
                NotifyActions();
            }
        }
    }
    public string KeyState { get => _keyState; private set => SetField(ref _keyState, value); }
    public string ConfigSource { get => _configSource; private set => SetField(ref _configSource, value); }
    public string EffectiveState { get => _effectiveState; private set => SetField(ref _effectiveState, value); }
    public string ConfigRevisionText => _configuration is null ? "尚未读取" : $"配置版本：{_configuration.ConfigRevision}";
    public string BaseUrlError { get => _baseUrlError; private set { if (SetField(ref _baseUrlError, value)) RebuildValidationSummary(); } }
    public string ModelNameError { get => _modelNameError; private set { if (SetField(ref _modelNameError, value)) RebuildValidationSummary(); } }
    public string ApiKeyError { get => _apiKeyError; private set { if (SetField(ref _apiKeyError, value)) RebuildValidationSummary(); } }
    public string ServerValidationError { get => _serverValidationError; private set { if (SetField(ref _serverValidationError, value)) RebuildValidationSummary(); } }
    public string ValidationSummary { get => _validationSummary; private set => SetField(ref _validationSummary, value); }
    public string ValidationFocusTarget { get => _validationFocusTarget; private set => SetField(ref _validationFocusTarget, value); }
    public bool HasValidationErrors => !string.IsNullOrEmpty(ValidationSummary);
    public bool HasBaseUrlError => !string.IsNullOrEmpty(BaseUrlError);
    public bool HasModelNameError => !string.IsNullOrEmpty(ModelNameError);
    public bool HasApiKeyError => !string.IsNullOrEmpty(ApiKeyError);

    internal void CheckConfigurationLocally()
    {
        if (!CanCheckConfiguration)
        {
            Status = "当前账号或服务状态不允许检查配置。";
            return;
        }

        IsCheckingConfiguration = true;
        try
        {
            var valid = ValidateConfiguration();
            var keyAvailable = _configuration?.ApiKeySet is true || !string.IsNullOrWhiteSpace(_apiKeyDraft);
            if (!keyAvailable)
            {
                ApiKeyError = "当前配置未设置 API Key。";
            }

            if (!valid || !keyAvailable)
            {
                Status = ValidationSummary;
                return;
            }

            var source = HasUnsavedChanges ? "当前未保存草稿" : "已保存配置";
            Status = _configuration?.BaseUrlRedacted is true
                ? $"{source}的本地字段与密钥设置检查通过；Base URL 已脱敏，未检查地址真实性，也未连接服务。"
                : $"{source}的本地格式和密钥设置检查通过；未连接 AI 服务，也未产生请求或费用。";
        }
        finally
        {
            IsCheckingConfiguration = false;
        }
    }

    internal async Task RefreshAiHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!CanReadAiHealth || _client is null || _identity is null)
        {
            return;
        }

        var client = _client;
        var clientEpoch = Interlocked.Read(ref _clientEpoch);
        var cardGeneration = Interlocked.Read(ref _aiCardGeneration);
        var requestGeneration = Interlocked.Increment(ref _healthRequestGeneration);
        var userId = _identity.UserId;
        var healthCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _healthCancellation?.Cancel();
        _healthCancellation = healthCancellation;
        IsHealthLoading = true;
        StopHealthFreshnessTimer();
        _aiHealth = null;
        HealthObservedText = "正在读取后端健康缓存；不会主动探测 AI 服务。";
        HealthMessage = "正在读取缓存状态。";
        NotifyHealthProperties();
        try
        {
            var snapshot = await client.GetAiHealthAsync(healthCancellation.Token);
            if (!IsCurrentAiOperation(client, clientEpoch, userId, cardGeneration) ||
                requestGeneration != Interlocked.Read(ref _healthRequestGeneration))
            {
                return;
            }

            _aiHealth = snapshot;
            HealthMessage = FormatAiHealth(snapshot, _configuration);
            HealthObservedText = FormatAiHealthObserved(snapshot, DateTimeOffset.UtcNow);
            UpdateHealthFreshnessTimer();
            OnPropertyChanged(nameof(IsHealthHealthy));
            NotifyHealthProperties();
        }
        catch (OperationCanceledException) when (healthCancellation.IsCancellationRequested)
        {
            if (requestGeneration == Interlocked.Read(ref _healthRequestGeneration))
            {
                _aiHealth = null;
                HealthMessage = "健康缓存读取已取消。";
                HealthObservedText = "缓存未刷新。";
                NotifyHealthProperties();
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentAiOperation(client, clientEpoch, userId, cardGeneration))
            {
                _aiHealth = null;
                HealthMessage = SafeHealthReadError(exception);
                HealthObservedText = "未使用旧值冒充当前状态。";
                NotifyHealthProperties();
            }
        }
        finally
        {
            if (ReferenceEquals(_healthCancellation, healthCancellation))
            {
                _healthCancellation = null;
            }
            healthCancellation.Dispose();
            if (requestGeneration == Interlocked.Read(ref _healthRequestGeneration))
            {
                IsHealthLoading = false;
            }
        }
    }

    internal LauncherManualAiTestPreview? CreateManualTestPreview(bool useDraft)
    {
        if (_manualTestRequiresConfigurationRefresh)
        {
            Status = "AI 配置版本已变化；请先重新读取配置，再重新核对并确认测试。";
            return null;
        }

        if (!CanRunManualTest || _client is null || _identity is null || _configuration is null)
        {
            Status = "当前账号没有 AI 配置写入权限，或本地服务尚未就绪。";
            return null;
        }

        ClearValidationErrors();
        var selectedBaseUrl = useDraft && _baseUrlDirty ? BaseUrl : _configuration.BaseUrl;
        var selectedModel = useDraft && _modelDirty ? ModelName : _configuration.ModelName ?? string.Empty;
        var hasSelectedKey = (useDraft && !string.IsNullOrWhiteSpace(_apiKeyDraft)) || _configuration.ApiKeySet;

        if (string.IsNullOrWhiteSpace(selectedBaseUrl) || (useDraft && !_baseUrlDirty && _configuration.BaseUrlRedacted))
        {
            BaseUrlError = "Base URL 不可见或未设置；请在 Web 高级设置中确认地址后再测试。";
        }
        else if (ValidateBaseUrl(selectedBaseUrl) is { } baseUrlError)
        {
            BaseUrlError = baseUrlError;
        }

        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            ModelNameError = "模型名称不能为空。";
        }
        else if (selectedModel.Length > 256 || !string.Equals(selectedModel, selectedModel.Trim(), StringComparison.Ordinal) || selectedModel.Any(char.IsControl))
        {
            ModelNameError = "模型名称格式无效。";
        }

        if (!hasSelectedKey)
        {
            ApiKeyError = "所选配置没有已保存或本次新输入的 API Key。";
        }
        else if (useDraft && _apiKeyDraft.Length > 4096)
        {
            ApiKeyDraft = string.Empty;
            ApiKeyError = "API Key 超过长度限制，草稿已清除。";
        }

        var targetChanged = useDraft && _baseUrlDirty && IsOriginChanged(_configuration.BaseUrl, BaseUrl);
        if (targetChanged && string.IsNullOrWhiteSpace(_apiKeyDraft))
        {
            ApiKeyError = "更换协议、主机或端口后，必须为新目标重新输入 API Key。";
        }

        if (HasValidationErrors)
        {
            Status = ValidationSummary;
            return null;
        }

        var targetUri = new Uri(selectedBaseUrl!, UriKind.Absolute);
        var targetOrigin = targetUri.Scheme + "://" + targetUri.Host +
            (targetUri.IsDefaultPort ? string.Empty : ":" + targetUri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new LauncherManualAiTestPreview(
            Interlocked.Read(ref _aiCardGeneration),
            Interlocked.Read(ref _clientEpoch),
            _identity.UserId,
            _configuration.ConfigId,
            _configuration.ConfigRevision,
            useDraft,
            targetOrigin,
            selectedModel!,
            useDraft && !string.IsNullOrWhiteSpace(_apiKeyDraft));
    }

    internal async Task RunManualAiTestAsync(
        LauncherManualAiTestPreview preview,
        bool explicitlyConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (!explicitlyConfirmed)
        {
            Status = "未确认；没有发送 AI 测试请求。";
            return;
        }

        var client = _client;
        var identity = _identity;
        var configuration = _configuration;
        if (preview is null || !CanRunManualTest || client is null || identity is null || configuration is null ||
            preview.Generation != Interlocked.Read(ref _aiCardGeneration) ||
            preview.ClientEpoch != Interlocked.Read(ref _clientEpoch) ||
            preview.UserId != identity.UserId || preview.ConfigId != configuration.ConfigId ||
            preview.ConfigRevision != configuration.ConfigRevision)
        {
            Status = "配置或账号已变化，刚才的确认已失效；请重新核对并确认。没有发送请求。";
            return;
        }

        var requestId = Guid.NewGuid().ToString("N");
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _manualTestCancellation?.Dispose();
        _manualTestCancellation = cancellation;
        var testGeneration = Interlocked.Read(ref _aiCardGeneration);
        var clientEpoch = Interlocked.Read(ref _clientEpoch);
        IsManualTestRunning = true;
        _manualTestResult = null;
        ManualTestMessage = "正在发送一次固定文本测试请求；不会自动重试。";
        ManualTestTime = "等待结果";
        ManualTestLatency = string.Empty;
        ManualTestSource = preview.UseDraft ? "本次确认的 Launcher 草稿" : "当前用户已保存配置";
        NotifyManualTestProperties();
        try
        {
            var test = await client.RunManualAiTestAsync(
                explicitlyConfirmed: true,
                requestId,
                expectedConfigId: preview.ConfigId,
                expectedConfigRevision: preview.ConfigRevision,
                baseUrl: preview.UseDraft && _baseUrlDirty ? BaseUrl : null,
                modelName: preview.UseDraft && _modelDirty ? ModelName : null,
                apiKey: preview.UseDraft && !string.IsNullOrWhiteSpace(_apiKeyDraft) ? _apiKeyDraft : null,
                cancellation.Token);

            if (!IsCurrentAiOperation(client, clientEpoch, identity.UserId, testGeneration))
            {
                return;
            }

            _manualTestResult = test;
            ManualTestMessage = test.Message;
            ManualTestTime = test.CheckedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "时间未知";
            ManualTestLatency = test.LatencyMilliseconds is { } latency ? $"耗时 {latency} ms" : "耗时未知";
            if (Interlocked.Read(ref _aiCardGeneration) == testGeneration)
            {
                ClearApiKeyDraftAfterManualTest();
            }
            Status = test.Status == "unknown"
                ? "测试结果未知；不要自动重试。若要再测，请重新确认后手动发起。"
                : test.Success ? "测试完成。该结果不会写入健康缓存或配置。" : "测试完成，结果仅保留在当前窗口内。";
            NotifyManualTestProperties();
        }
        catch (LauncherUserClientException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            if (IsCurrentAiOperation(client, clientEpoch, identity.UserId, testGeneration))
            {
                InvalidateAiActionCards();
                _manualTestRequiresConfigurationRefresh = true;
                ClearApiKeyDraftAfterManualTest();
                Status = "配置在确认后已变化；本次未发送 AI 请求。请重新读取配置，核对目标并重新确认。";
                NotifyActions();
            }
        }
        catch (Exception)
        {
            if (IsCurrentAiOperation(client, clientEpoch, identity.UserId, testGeneration))
            {
                _manualTestResult = new LauncherAiManualTestResult(
                    false,
                    "unknown",
                    "没有收到可确认结果；服务端可能已处理或计费。请确认后再决定是否手动重测。",
                    null,
                    DateTimeOffset.UtcNow);
                ManualTestMessage = _manualTestResult.Message;
                ManualTestTime = "时间未知";
                ManualTestLatency = "耗时未知";
                Status = "测试结果未知；系统没有自动重试。";
                ClearApiKeyDraftAfterManualTest();
                NotifyManualTestProperties();
            }
        }
        finally
        {
            if (ReferenceEquals(_manualTestCancellation, cancellation))
            {
                _manualTestCancellation = null;
            }
            cancellation.Dispose();
            if (Interlocked.Read(ref _clientEpoch) == clientEpoch)
            {
                IsManualTestRunning = false;
            }
        }
    }

    internal void CancelManualAiTest()
    {
        if (!IsManualTestRunning)
        {
            return;
        }

        Status = "已请求停止等待；服务端可能已处理或计费，不会自动重发。";
        _manualTestCancellation?.Cancel();
    }

    internal async Task SetHostReadyAsync(bool ready)
    {
        if (ready == HostReady)
        {
            return;
        }

        HostReady = ready && !IsSimulation;
        if (!HostReady)
        {
            _pairingCancellation?.Cancel();
            ClearPrivateData("本地服务已停止或正在维护；当前用户信息已清除。");
            await ResetAsync("本地服务已停止或正在维护；当前用户信息已清除。", revoke: true);
        }
        else if (_identity is null)
        {
            Status = "服务与首次设置已就绪。开始配对后，在 Web 页面登录并明确批准。";
        }
    }

    /// <summary>
    /// Clears all user-scoped state even when HostReady is already false. A
    /// replacement Web process must never inherit pairing, AI cache, or drafts.
    /// </summary>
    internal Task PrepareForHostReplacementAsync(bool portMaintenance = true) =>
        ResetAsync(portMaintenance
                ? "本地 Web 端口即将切换；当前账号与 AI 草稿已清除，恢复后需重新配对。"
                : "启动并完成 Web 首次设置后，可在此连接当前账号。",
            revoke: true, cancellationToken: CancellationToken.None);

    internal async Task BeginPairingAsync(string authorizationUri, CancellationToken cancellationToken = default)
    {
        if (!CanBeginPairing)
        {
            Status = IsSimulation ? "模拟模式不连接真实业务账号。" : "仅在本地服务 Ready 且首次设置完成后可以配对。";
            return;
        }

        string? resetStatus = null;
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var client = await _clientFactory(cancellationToken);
            _client = client;
            var clientEpoch = Interlocked.Increment(ref _clientEpoch);
            Action invalidatedHandler = () => OnUserDataInvalidated(client, clientEpoch);
            _clientInvalidatedHandler = invalidatedHandler;
            client.UserDataInvalidated += invalidatedHandler;
            var prompt = await client.BeginPairingAsync(cancellationToken);
            _pairingPrompt = prompt;
            PairingCode = prompt.PairingCode;
            PairingExpiry = FormatExpiry(prompt.ExpiresAtUtc);
            IsPairing = true;
            Status = "请在浏览器登录目标账号，手动输入配对码并明确批准；Launcher 不会替你登录或批准。";
            if (!_openExternal(authorizationUri))
            {
                Status += " 浏览器未能自动打开，请使用 Web 管理页上的授权入口。";
            }

            _pairingCancellation?.Dispose();
            _pairingCancellation = new CancellationTokenSource();
            _pairingPollTask = PollPairingAsync(_pairingCancellation.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            resetStatus = "配对已取消。";
        }
        catch (Exception exception)
        {
            resetStatus = SafeError(exception, "无法开始配对，请确认服务仍处于 Ready 状态。");
        }
        finally
        {
            _operationGate.Release();
        }

        if (resetStatus is not null)
        {
            await ResetAsync(resetStatus, revoke: true);
        }
    }

    internal async Task CancelPairingAsync()
    {
        if (!IsPairing)
        {
            return;
        }

        _pairingCancellation?.Cancel();
        await ResetAsync("配对已取消。", revoke: true);
    }

    internal async Task RefreshSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_identity is null || _client is null || !HostReady)
        {
            return;
        }

        string? resetStatus = null;
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var current = await _client.GetCurrentUserAsync(cancellationToken);
            if (_identity is null || !string.Equals(_identity.UserId, current.UserId, StringComparison.Ordinal))
            {
                resetStatus = "当前账号已变化，请重新配对。";
            }
            else
            {
                _identity = current;
                AccountSummary = FormatAccount(current);
                var latest = await _client.GetAiConfigurationAsync(cancellationToken);
                if (_configuration is not null && HasSavedConfigurationChanged(_configuration, latest))
                {
                    ApplyConfiguration(latest);
                    Status = "检测到 Web 端配置版本变化；Launcher 草稿、健康缓存和手测结果已清除。请核对新配置。";
                }
                NotifyConfigurationState();
            }
        }
        catch (Exception exception)
        {
            resetStatus = SafeError(exception, "当前账号会话已失效，请重新配对。");
        }
        finally
        {
            _operationGate.Release();
        }

        if (resetStatus is not null)
        {
            ClearPrivateData(resetStatus);
            await ResetAsync(resetStatus, revoke: true, cancellationToken: CancellationToken.None);
        }
    }

    internal async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSave || _configuration is null || _client is null)
        {
            return;
        }

        var apiKeyTooLong = _apiKeyDraft.Length > 4096;
        if (!ValidateConfiguration())
        {
            if (apiKeyTooLong)
            {
                ApiKeyDraft = string.Empty;
                ApiKeyError = "API Key 不能超过 4096 个字符；密钥草稿已清除。";
            }
            Status = ValidationSummary;
            return;
        }

        if (_configuration.ApiKeySet && _baseUrlDirty && IsOriginChanged(_configuration.BaseUrl, BaseUrl) &&
            string.IsNullOrEmpty(_apiKeyDraft) && !_removeKeyRequested)
        {
            Status = "Base URL 的协议、主机或端口已改变。请为新目标重新绑定 API Key，或明确移除旧 Key。";
            return;
        }

        var resetAfterSave = false;
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            IsSaving = true;
            Status = "正在保存用户默认 AI 配置；不会发送 AI 请求。";
            var config = await _client.SaveAiConfigurationAsync(
                _configuration.ConfigRevision,
                _configuration.ConfigId,
                _configuration.BaseUrlRedacted || !_baseUrlDirty ? null : BaseUrl,
                _modelDirty ? ModelName : null,
                string.IsNullOrEmpty(_apiKeyDraft) ? null : _apiKeyDraft,
                removeApiKey: _removeKeyRequested,
                cancellationToken,
                tokensParameter: _tokensDirty ? TokensParameter : null,
                tokensLimit: _tokensDirty ? int.Parse(TokensLimit) : null);
            ApplyConfiguration(config);
            Status = FormatEffectiveState(config.EffectiveState);
        }
        catch (Exception exception)
        {
            var conflict = exception is LauncherUserClientException { StatusCode: HttpStatusCode.Conflict };
            var failedBaseUrl = BaseUrl;
            var failedModel = ModelName;
            var baseUrlWasDirty = _baseUrlDirty;
            var modelWasDirty = _modelDirty;
            ApiKeyDraft = string.Empty;
            if (conflict && _client is not null && _identity is not null)
            {
                try
                {
                    var fresh = await _client.GetAiConfigurationAsync(cancellationToken);
                    ApplyConfiguration(fresh);
                    Status = "配置已被 Web 页面或其他窗口修改。已加载最新版本，请核对后重新编辑并保存。";
                }
                catch (Exception refreshException)
                {
                    ClearPrivateData("配置版本冲突，且无法重新读取。请重新配对后重试。");
                    Status = SafeError(refreshException, Status);
                }
            }
            else
            {
                var failure = SafeError(exception, "保存失败。请检查字段错误并重试。");
                Status = failure;
                if (exception is LauncherUserClientException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
                {
                    ClearPrivateData(Status);
                    resetAfterSave = true;
                }
                else
                {
                    _baseUrl = failedBaseUrl;
                    _modelName = failedModel;
                    _baseUrlDirty = baseUrlWasDirty;
                    _modelDirty = modelWasDirty;
                    OnPropertyChanged(nameof(BaseUrl));
                    OnPropertyChanged(nameof(ModelName));
                    ApplyServerValidationError(exception, baseUrlWasDirty, modelWasDirty);
                    NotifyActions();
                }
            }
        }
        finally
        {
            ApiKeyDraft = string.Empty;
            _removeKeyRequested = false;
            IsSaving = false;
            _operationGate.Release();
            NotifyActions();
        }

        if (resetAfterSave)
        {
            await ResetAsync(Status, revoke: true, cancellationToken: CancellationToken.None);
        }
    }

    internal async Task RefreshConfigurationAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRefreshConfiguration || _client is null)
        {
            return;
        }

        string? resetStatus = null;
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var configuration = await _client.GetAiConfigurationAsync(cancellationToken);
            ApplyConfiguration(configuration);
            Status = "已从当前用户配置重新读取；尚未保存的表单草稿已清除。";
        }
        catch (Exception exception)
        {
            resetStatus = SafeError(exception, "无法重新读取 AI 配置，请确认账号会话仍有效。");
            ClearPrivateData(resetStatus);
        }
        finally
        {
            _operationGate.Release();
            NotifyActions();
        }

        if (resetStatus is not null)
        {
            await ResetAsync(resetStatus, revoke: true, cancellationToken: CancellationToken.None);
        }
    }

    internal async Task RemoveApiKeyAsync(bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!confirmed || !CanRemoveApiKey || _configuration is null || _client is null)
        {
            return;
        }

        _removeKeyRequested = true;
        ApiKeyDraft = string.Empty;
        await SaveAsync(cancellationToken);
    }

    internal async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await ResetAsync("已退出当前账号；用户摘要、AI 卡片和草稿已清除。", revoke: true, cancellationToken);
    }

    internal void ClearDrafts()
    {
        ApiKeyDraft = string.Empty;
        _removeKeyRequested = false;
        if (_configuration is not null)
        {
            ApplyConfiguration(_configuration);
        }
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(RequiresBaseUrlTargetConfirmation));
        OnPropertyChanged(nameof(BaseUrlTargetSummary));
    }

    private async Task PollPairingAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _pairingPrompt is { } prompt && DateTimeOffset.UtcNow < prompt.ExpiresAtUtc)
        {
            var pairingCompleted = false;
            try
            {
                await Task.Delay(PollInterval, cancellationToken);
                if (DateTimeOffset.UtcNow >= prompt.ExpiresAtUtc)
                {
                    break;
                }

                await _operationGate.WaitAsync(cancellationToken);
                try
                {
                    if (_client is null || !HostReady)
                    {
                        return;
                    }

                    var exchange = await _client.ExchangePairingOnceAsync(cancellationToken);
                    if (exchange.IsPending)
                    {
                        PairingExpiry = FormatExpiry(prompt.ExpiresAtUtc);
                        continue;
                    }

                    IsPairing = false;
                    _pairingPrompt = null;
                    PairingCode = string.Empty;
                    PairingExpiry = string.Empty;
                    var identity = await _client.GetCurrentUserAsync(cancellationToken);
                    _identity = identity;
                    AccountSummary = FormatAccount(identity);
                    var config = await _client.GetAiConfigurationAsync(cancellationToken);
                    ApplyConfiguration(config);
                    Status = identity.CanWriteAi
                        ? "已配对当前 Web 账号。检查配置只做本地校验；健康卡读取缓存；AI 手测需单独确认。"
                        : "已连接当前账号，但该账号没有 AI 配置写入权限；当前配置只读。";
                    pairingCompleted = true;
                }
                finally
                {
                    _operationGate.Release();
                }

                if (pairingCompleted)
                {
                    await RefreshAiHealthAsync(cancellationToken);
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _pairingPollTask = null;
                Status = SafeError(exception, "配对状态读取失败。请重新开始配对。");
                await ResetAsync(Status, revoke: true);
                return;
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            _pairingPollTask = null;
            await ResetAsync("配对码已过期。请重新发起配对。", revoke: true);
        }
    }

    private async Task ResetAsync(string status, bool revoke, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _clientEpoch);
        _manualTestCancellation?.Cancel();
        _pairingCancellation?.Cancel();
        _pairingCancellation?.Dispose();
        _pairingCancellation = null;
        var pollTask = _pairingPollTask;
        _pairingPollTask = null;
        if (pollTask is not null)
        {
            try { await pollTask; }
            catch (OperationCanceledException) { }
        }

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var client = _client;
            _client = null;
            if (client is not null)
            {
                var invalidatedHandler = _clientInvalidatedHandler;
                _clientInvalidatedHandler = null;
                if (invalidatedHandler is not null)
                {
                    client.UserDataInvalidated -= invalidatedHandler;
                }
                if (revoke)
                {
                    try { await client.LogoutAsync(cancellationToken); }
                    catch (Exception) { /* Local UI data is still cleared; the short-lived server session expires independently. */ }
                }
                else
                {
                    client.ClearUserDataCache();
                }
                client.Dispose();
            }

            ClearPrivateData(status);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void OnUserDataInvalidated(ILauncherBusinessUserClient source, long sourceEpoch)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_client, source) || Interlocked.Read(ref _clientEpoch) != sourceEpoch)
            {
                return;
            }

            _pairingCancellation?.Cancel();
            ClearPrivateData("Launcher 用户授权已失效。请重新配对后查看用户信息。");
            _ = ResetAsync("Launcher 用户授权已失效。请重新配对后查看用户信息。", revoke: false);
        });
    }

    private void ClearPrivateData(string status)
    {
        InvalidateAiActionCards();
        _manualTestRequiresConfigurationRefresh = false;
        IsHealthLoading = false;
        IsManualTestRunning = false;
        _pairingPrompt = null;
        _identity = null;
        _configuration = null;
        ResetAdvancedFields(null);
        IsPairing = false;
        PairingCode = string.Empty;
        PairingExpiry = string.Empty;
        AccountSummary = string.Empty;
        _baseUrl = string.Empty;
        _modelName = string.Empty;
        ApiKeyDraft = string.Empty;
        _baseUrlDirty = false;
        _modelDirty = false;
        _removeKeyRequested = false;
        OnPropertyChanged(nameof(BaseUrl));
        OnPropertyChanged(nameof(ModelName));
        KeyState = "尚未读取";
        ConfigSource = "尚未读取";
        EffectiveState = "尚未读取";
        OnPropertyChanged(nameof(ConfigRevisionText));
        Status = status;
        ClearValidationErrors();
        NotifyConfigurationState();
        NotifyActions();
    }

    private void ApplyConfiguration(LauncherAiConfiguration config)
    {
        _manualTestRequiresConfigurationRefresh = false;
        InvalidateAiActionCards();
        _configuration = config;
        ResetAdvancedFields(config);
        _baseUrl = config.BaseUrlRedacted ? string.Empty : config.BaseUrl ?? string.Empty;
        _modelName = config.ModelName ?? string.Empty;
        ApiKeyDraft = string.Empty;
        _baseUrlDirty = false;
        _modelDirty = false;
        _removeKeyRequested = false;
        OnPropertyChanged(nameof(BaseUrl));
        OnPropertyChanged(nameof(ModelName));
        KeyState = config.ApiKeySet ? "已设置（不回显）" : "未设置";
        ConfigSource = config.ConfigSource == "user_default_api_config" ? "当前用户默认 AI 配置" : "来源未知";
        EffectiveState = FormatEffectiveState(config.EffectiveState);
        ClearValidationErrors();
        OnPropertyChanged(nameof(ConfigRevisionText));
        NotifyConfigurationState();
        NotifyActions();
    }

    private static bool HasSavedConfigurationChanged(LauncherAiConfiguration current, LauncherAiConfiguration latest) =>
        current.ConfigId != latest.ConfigId || current.ConfigRevision != latest.ConfigRevision ||
        current.ApiKeySet != latest.ApiKeySet || current.BaseUrlRedacted != latest.BaseUrlRedacted ||
        current.BaseUrl != latest.BaseUrl || current.ModelName != latest.ModelName ||
        current.TokensParameter != latest.TokensParameter || current.TokensLimit != latest.TokensLimit ||
        current.ConfigSource != latest.ConfigSource || current.EffectiveState != latest.EffectiveState;

    private void NotifyConfigurationState()
    {
        OnPropertyChanged(nameof(IsPaired));
        OnPropertyChanged(nameof(CanReadAi));
        OnPropertyChanged(nameof(CanWriteAi));
        OnPropertyChanged(nameof(IsConfigurationVisible));
        OnPropertyChanged(nameof(IsBaseUrlRedacted));
        OnPropertyChanged(nameof(IsBaseUrlEditable));
        OnPropertyChanged(nameof(IsModelEditable));
        OnPropertyChanged(nameof(IsApiKeyEditable));
        OnPropertyChanged(nameof(IsPairingCodeVisible));
        OnPropertyChanged(nameof(CanCheckConfiguration));
        OnPropertyChanged(nameof(CanReadAiHealth));
        OnPropertyChanged(nameof(CanRefreshAiHealth));
        OnPropertyChanged(nameof(CanRunManualTest));
    }

    private void ResetAdvancedFields(LauncherAiConfiguration? config)
    {
        _tokensParameter = config?.TokensParameter ?? string.Empty;
        _tokensLimit = config is null ? string.Empty : (config.TokensLimit ?? 20000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _tokensDirty = false;
        OnPropertyChanged(nameof(TokensParameter));
        OnPropertyChanged(nameof(TokensLimit));
        OnPropertyChanged(nameof(HasAdvancedDraft));
    }

    private void NotifyActions()
    {
        OnPropertyChanged(nameof(HasAdvancedDraft));
        OnPropertyChanged(nameof(CanBeginPairing));
        OnPropertyChanged(nameof(CanCancelPairing));
        OnPropertyChanged(nameof(CanLogout));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanRefreshConfiguration));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(CanRemoveApiKey));
        OnPropertyChanged(nameof(CanCheckConfiguration));
        OnPropertyChanged(nameof(CanReadAiHealth));
        OnPropertyChanged(nameof(CanRefreshAiHealth));
        OnPropertyChanged(nameof(CanRunManualTest));
        OnPropertyChanged(nameof(CanCancelManualTest));
        OnPropertyChanged(nameof(IsBaseUrlEditable));
        OnPropertyChanged(nameof(IsModelEditable));
        OnPropertyChanged(nameof(IsApiKeyEditable));
    }

    private bool ValidateConfiguration()
    {
        ClearValidationErrors();
        if (_configuration is null)
        {
            return false;
        }

        if (_tokensDirty && ((TokensParameter.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(TokensParameter, @"\A[A-Za-z_][A-Za-z0-9_]{0,63}\z")) ||
            !int.TryParse(TokensLimit, out var parsedLimit) || parsedLimit <= 0))
        {
            ServerValidationError = "tokens 字段名须为字母/下划线开头，最多 64 位；输出上限须为正整数。";
        }

        if (_apiKeyDraft.Length > 4096)
        {
            ApiKeyError = "API Key 不能超过 4096 个字符。";
        }

        if (!_configuration.BaseUrlRedacted)
        {
            var baseUrl = _baseUrlDirty ? BaseUrl : _configuration.BaseUrl ?? string.Empty;
            var urlError = ValidateBaseUrl(baseUrl);
            if (urlError is not null)
            {
                BaseUrlError = urlError;
            }
        }

        var modelName = _modelDirty ? ModelName : _configuration.ModelName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(modelName))
        {
            ModelNameError = "模型名称不能为空。";
        }
        else if (modelName.Length > 256)
        {
            ModelNameError = "模型名称不能超过 256 个字符。";
        }
        else if (!string.Equals(modelName, modelName.Trim(), StringComparison.Ordinal) || modelName.Any(char.IsControl))
        {
            ModelNameError = "模型名称不能包含首尾空白或控制字符。";
        }

        return !HasValidationErrors;
    }

    private static string? ValidateBaseUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Base URL 不能为空。";
        }
        if (value.Length > 2048)
        {
            return "Base URL 不能超过 2048 个字符。";
        }
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return "请输入有效的 HTTP 或 HTTPS 地址，不要包含账号、密码、查询参数或片段。";
        }

        return null;
    }

    private void ApplyServerValidationError(Exception exception, bool baseUrlWasDirty, bool modelWasDirty)
    {
        var statusCode = (exception as LauncherUserClientException)?.StatusCode;
        if (statusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            const string safeMessage = "服务端未接受此字段；请检查格式和密钥目标变更要求。";
            if (baseUrlWasDirty)
            {
                BaseUrlError = safeMessage;
            }
            if (modelWasDirty)
            {
                ModelNameError = safeMessage;
            }
            if (!baseUrlWasDirty && !modelWasDirty)
            {
                ServerValidationError = "服务端未接受本次配置。请重新读取配置或到 Web 系统检查。";
            }
            return;
        }

        ServerValidationError = SafeError(exception, "保存失败。非密钥草稿仍保留，请修改后重试。");
    }

    private void ClearServerValidationError() => ServerValidationError = string.Empty;

    private void ClearValidationErrors()
    {
        BaseUrlError = string.Empty;
        ModelNameError = string.Empty;
        ApiKeyError = string.Empty;
        ServerValidationError = string.Empty;
        ValidationFocusTarget = string.Empty;
        ValidationSummary = string.Empty;
    }

    private void RebuildValidationSummary()
    {
        var errors = new List<string>();
        if (!string.IsNullOrEmpty(BaseUrlError)) errors.Add($"Base URL：{BaseUrlError}");
        if (!string.IsNullOrEmpty(ModelNameError)) errors.Add($"模型名称：{ModelNameError}");
        if (!string.IsNullOrEmpty(ApiKeyError)) errors.Add($"API Key：{ApiKeyError}");
        if (!string.IsNullOrEmpty(ServerValidationError)) errors.Add(ServerValidationError);
        ValidationSummary = errors.Count == 0 ? string.Empty : $"配置未通过校验。{string.Join(" ", errors)} 点击此摘要可定位到首个字段错误。";
        ValidationFocusTarget = !string.IsNullOrEmpty(BaseUrlError) ? "baseUrl" :
            !string.IsNullOrEmpty(ModelNameError) ? "modelName" :
            !string.IsNullOrEmpty(ApiKeyError) ? "apiKey" : string.Empty;
        OnPropertyChanged(nameof(HasBaseUrlError));
        OnPropertyChanged(nameof(HasModelNameError));
        OnPropertyChanged(nameof(HasApiKeyError));
        OnPropertyChanged(nameof(HasValidationErrors));
    }

    private void NotifyTargetProperties()
    {
        OnPropertyChanged(nameof(RequiresBaseUrlTargetConfirmation));
        OnPropertyChanged(nameof(BaseUrlTargetSummary));
    }

    private static bool IsOriginChanged(string? before, string after)
    {
        if (!Uri.TryCreate(before, UriKind.Absolute, out var oldUri) || !Uri.TryCreate(after, UriKind.Absolute, out var newUri))
        {
            return false;
        }

        return !string.Equals(oldUri.Scheme, newUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(oldUri.Host, newUri.Host, StringComparison.OrdinalIgnoreCase) || oldUri.Port != newUri.Port;
    }

    internal static string FormatEffectiveState(string? value) => value switch
    {
        "applies_to_new_requests" => "已保存，对新请求生效；运行中任务不变。",
        "needs_setup" => "应用尚未完成首次设置。",
        _ => "生效状态未知。",
    };

    internal static string FormatAccount(LauncherUserIdentity identity) =>
        $"当前账号：{identity.Username} · {(identity.CanWriteAi ? "可编辑 AI 配置" : "AI 配置只读")}";

    internal static string FormatExpiry(DateTimeOffset expiresAtUtc) =>
        $"有效至 {expiresAtUtc.ToLocalTime():HH:mm:ss}（本地时间）";

    internal static string FormatAiHealth(LauncherAiHealthSnapshot health, LauncherAiConfiguration? currentConfiguration)
    {
        if (currentConfiguration is null || health.ConfigId != currentConfiguration.ConfigId ||
            health.ConfigRevision != currentConfiguration.ConfigRevision)
        {
            return "配置版本已变化；此缓存不能代表当前配置。";
        }

        if (health.Freshness == "current" && !IsAiHealthSnapshotCurrentAt(health, currentConfiguration, DateTimeOffset.UtcNow))
        {
            return "健康缓存已过期或缺少有效 TTL；不代表当前连接状态。";
        }

        return health.Freshness switch
        {
            "never_checked" => "尚未检测；没有探测结果，不代表正常。",
            "unconfigured" => "AI 配置不完整；没有可用健康检测。",
            "config_changed" => "保存配置已变化；原检测结果已失效。",
            "stale" => "健康缓存已过期；不代表当前连接状态。",
            "current" => "当前用户的健康缓存；读取缓存没有发起 AI 请求。",
            _ => "健康状态未知；不视为正常。",
        };
    }

    private static string SafeHealthReadError(Exception exception) => exception is LauncherUserClientException clientException
        ? clientException.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "用户授权已过期；健康缓存已清除，请重新配对。",
            HttpStatusCode.Forbidden => "当前账号无权读取此健康缓存。",
            _ => "健康缓存暂不可用；未将旧状态显示为当前结果。",
        }
        : "健康缓存暂不可用；未将旧状态显示为当前结果。";

    private bool IsCurrentAiOperation(
        ILauncherBusinessUserClient client,
        long clientEpoch,
        string userId,
        long cardGeneration) =>
        ReferenceEquals(_client, client) &&
        Interlocked.Read(ref _clientEpoch) == clientEpoch &&
        Interlocked.Read(ref _aiCardGeneration) == cardGeneration &&
        string.Equals(_identity?.UserId, userId, StringComparison.Ordinal) &&
        HostReady;

    private void InvalidateAiActionCards()
    {
        StopHealthFreshnessTimer();
        Interlocked.Increment(ref _aiCardGeneration);
        Interlocked.Increment(ref _healthRequestGeneration);
        _healthCancellation?.Cancel();
        _healthCancellation = null;
        IsHealthLoading = false;
        _manualTestCancellation?.Cancel();
        _aiHealth = null;
        _manualTestResult = null;
        ManualTestMessage = string.Empty;
        ManualTestTime = string.Empty;
        ManualTestLatency = string.Empty;
        ManualTestSource = string.Empty;
        HealthMessage = "配置草稿已变化；健康缓存和此前手测结果已清除。";
        HealthObservedText = "请在需要时重新读取已保存配置的缓存状态。";
        OnPropertyChanged(nameof(HasAiHealth));
        OnPropertyChanged(nameof(HasManualTestResult));
        OnPropertyChanged(nameof(IsHealthHealthy));
        OnPropertyChanged(nameof(HealthCategoryText));
        OnPropertyChanged(nameof(HealthSourceText));
        OnPropertyChanged(nameof(HealthFreshnessText));
        OnPropertyChanged(nameof(IsManualTestUnknown));
    }

    internal static bool IsAiHealthSnapshotCurrentAt(
        LauncherAiHealthSnapshot snapshot,
        LauncherAiConfiguration? currentConfiguration,
        DateTimeOffset nowUtc)
    {
        if (snapshot.Freshness != "current" || currentConfiguration is null || snapshot.ConfigId != currentConfiguration.ConfigId ||
            snapshot.ConfigRevision != currentConfiguration.ConfigRevision || snapshot.RemainingTtlSeconds is not > 0 ||
            snapshot.ExpiresAtUtc is not { } expiresAtUtc)
        {
            return false;
        }

        return expiresAtUtc.ToUniversalTime() > nowUtc.ToUniversalTime();
    }

    private void UpdateHealthFreshnessTimer()
    {
        StopHealthFreshnessTimer();
        if (_aiHealth is not { ExpiresAtUtc: { } expiresAtUtc } snapshot ||
            _identity is not { } identity || _configuration is not { } configuration ||
            !IsAiHealthSnapshotCurrentAt(snapshot, configuration, DateTimeOffset.UtcNow))
        {
            return;
        }

        var cardGeneration = Interlocked.Read(ref _aiCardGeneration);
        var clientEpoch = Interlocked.Read(ref _clientEpoch);
        var userId = identity.UserId;
        var configId = configuration.ConfigId;
        var configRevision = configuration.ConfigRevision;
        var dueTime = expiresAtUtc.ToUniversalTime() - DateTimeOffset.UtcNow;
        if (dueTime <= TimeSpan.Zero)
        {
            Dispatcher.UIThread.Post(() => ApplyHealthFreshnessExpiry(cardGeneration, clientEpoch, userId, configId, configRevision));
            return;
        }

        _healthFreshnessTimer = new System.Threading.Timer(
            _ =>
            {
                try
                {
                    Dispatcher.UIThread.Post(() =>
                        ApplyHealthFreshnessExpiry(cardGeneration, clientEpoch, userId, configId, configRevision));
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Trace.TraceError($"AI 健康缓存本地到期刷新失败；异常类型：{exception.GetType().Name}");
                }
            },
            null,
            dueTime,
            Timeout.InfiniteTimeSpan);
    }

    private void StopHealthFreshnessTimer()
    {
        _healthFreshnessTimer?.Dispose();
        _healthFreshnessTimer = null;
    }

    private void ApplyHealthFreshnessExpiry(
        long cardGeneration,
        long clientEpoch,
        string userId,
        string configId,
        int configRevision)
    {
        if (Interlocked.Read(ref _aiCardGeneration) != cardGeneration ||
            Interlocked.Read(ref _clientEpoch) != clientEpoch ||
            !HostReady || _identity?.UserId != userId || _configuration?.ConfigId != configId ||
            _configuration?.ConfigRevision != configRevision || _aiHealth is null)
        {
            return;
        }

        if (IsAiHealthSnapshotCurrentAt(_aiHealth, _configuration, DateTimeOffset.UtcNow))
        {
            UpdateHealthFreshnessTimer();
            return;
        }

        HealthMessage = FormatAiHealth(_aiHealth, _configuration);
        HealthObservedText = FormatAiHealthObserved(_aiHealth, DateTimeOffset.UtcNow);
        NotifyHealthProperties();
    }

    private static string FormatAiHealthObserved(LauncherAiHealthSnapshot snapshot, DateTimeOffset nowUtc)
    {
        if (snapshot.ObservedAtUtc is not { } observedAt)
        {
            return snapshot.Freshness switch
            {
                "never_checked" => "尚无观测时间；尚未检测不代表正常。",
                "unconfigured" => "尚无观测时间；配置不完整。",
                _ => "暂无可用观测时间。",
            };
        }

        var remainingSeconds = snapshot.ExpiresAtUtc is { } expiresAtUtc
            ? Math.Max(0, (int)Math.Ceiling((expiresAtUtc.ToUniversalTime() - nowUtc.ToUniversalTime()).TotalSeconds))
            : snapshot.RemainingTtlSeconds;
        return $"缓存观测于 {observedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · TTL {remainingSeconds?.ToString() ?? "—"} 秒";
    }

    private void ClearApiKeyDraftAfterManualTest()
    {
        if (_apiKeyDraft.Length == 0)
        {
            return;
        }

        _apiKeyDraft = string.Empty;
        ApiKeyError = string.Empty;
        OnPropertyChanged(nameof(ApiKeyDraft));
        NotifyActions();
    }

    private void NotifyHealthProperties()
    {
        OnPropertyChanged(nameof(HasAiHealth));
        OnPropertyChanged(nameof(IsHealthHealthy));
        OnPropertyChanged(nameof(HealthCategoryText));
        OnPropertyChanged(nameof(HealthSourceText));
        OnPropertyChanged(nameof(HealthFreshnessText));
    }

    private void NotifyManualTestProperties()
    {
        OnPropertyChanged(nameof(HasManualTestResult));
        OnPropertyChanged(nameof(IsManualTestUnknown));
        OnPropertyChanged(nameof(ShowManualTestStatus));
        NotifyActions();
    }

    private static string SafeError(Exception exception, string fallback)
    {
        if (exception is LauncherUserClientException clientException)
        {
            return clientException.StatusCode switch
            {
                HttpStatusCode.Conflict => "配置版本冲突，请刷新后核对。",
                HttpStatusCode.Unauthorized => "授权已过期，请重新配对。",
                HttpStatusCode.Forbidden => "账号权限已变化，已清除用户配置。",
                HttpStatusCode.TooManyRequests => "授权服务繁忙，请稍后重试。",
                _ => fallback,
            };
        }

        return fallback;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public async ValueTask DisposeAsync()
    {
        StopHealthFreshnessTimer();
        await ResetAsync("Launcher 已关闭。", revoke: true);
    }
}
