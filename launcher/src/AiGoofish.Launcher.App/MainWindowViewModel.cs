using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using AiGoofish.Launcher.App.Simulation;
using AiGoofish.Launcher.Core;
using AiGoofish.Launcher.Platform.Windows;
using Avalonia.Threading;
using Avalonia.Media;

namespace AiGoofish.Launcher.App;

internal enum LauncherPage
{
    Overview,
    Ai,
    Settings,
    Diagnostics,
    Logs,
}

public sealed class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const int UiLogCapacity = 300;
    private const int UiActivityCapacity = 100;
    private bool _isActivityView = true;
    private readonly bool _isSimulation;
    private readonly string _bundleRoot;
    private readonly PortableBundleDescriptor? _verifiedBundle;
    private readonly Func<PortableBundleDescriptor, PortableRestoreSession>? _restoreSessionFactory;
    private readonly ILauncherPreferencesStore? _launcherPreferencesStore;
    private readonly Func<Task>? _autoStartAction;
    private readonly SemaphoreSlim _realOperationGate = new(1, 1);
    private readonly LauncherBusinessConnectionViewModel _businessConnection;
    private LauncherCoordinator? _coordinator;
    private RealPortableStackHost? _realHost;
    private IPortableWebPortUiHost? _webPortUiHost;
    private PortableBundleDescriptor? _bundle;
    private string _serviceState;
    private string _phaseMessage;
    private string _primaryButtonText;
    private bool _isPrimaryEnabled;
    private bool _isCancelEnabled;
    private bool _isStopEnabled;
    private string _cancelHint;
    private string _serviceDetail;
    private string _managementUrl = "尚未启动";
    private int _managementPageOpening;
    private bool _setupRequired;
    private bool _shutdownInProgress;
    private bool _backupInFlight;
    private bool _restoreInFlight;
    private bool _newInstallReady;
    private bool _restoreStartedFromNewInstall;
    private PortableRestoreSession? _restoreSession;
    private CancellationTokenSource? _restoreCancellation;
    private string _observationSummary = "状态尚未采样。";
    private string _freshnessText = "尚无观测时间。";
    private int _initializationStarted;
    private int _shutdownRequested;
    private bool _disposed;
    private bool _openManagementPageOnReady = true;
    private bool _autoStartOnLauncherOpen;
    private string _launcherPreferencesStatus = "本地偏好尚未保存。";
    private LauncherPreferences _launcherPreferences = new();
    private bool _verifiedStoppedExistingForAutoStart;
    private int _autoStartAttempted;
    private bool _preferencesLoaded;
    private bool _managementPageOpenAttemptedForReadyGeneration;
    private string _webPortInput = "58000";
    private bool _automaticWebPort;
    private string _webPortError = string.Empty;
    private string _webPortStatus = "等待实例端口配置。";
    private int _effectiveWebPort;
    private int? _pendingWebPort;
    private long _webPortRevision;
    private bool _webPortSettingsLoaded;
    private bool _webPortOperationInFlight;
    private bool _webPortIntentUnresolved;
    private bool _webPortRefreshFailed;
    private long _coordinatorGeneration;
    private LauncherPage _selectedPage;
    private readonly HashSet<long> _seenStartupDiagnosticSequences = new();
    private readonly Queue<long> _startupDiagnosticSequenceOrder = new();

    public MainWindowViewModel(
        bool isSimulation = false,
        string? bundleRoot = null,
        PortableBundleDescriptor? verifiedBundle = null,
        Func<PortableBundleDescriptor, PortableRestoreSession>? restoreSessionFactory = null)
        : this(isSimulation, bundleRoot, verifiedBundle, restoreSessionFactory, userClientFactory: null)
    {
    }

    internal MainWindowViewModel(
        bool isSimulation,
        string? bundleRoot,
        PortableBundleDescriptor? verifiedBundle,
        Func<PortableBundleDescriptor, PortableRestoreSession>? restoreSessionFactory,
        Func<CancellationToken, Task<ILauncherBusinessUserClient>>? userClientFactory,
        ILauncherPreferencesStore? preferencesStore = null,
        Func<Task>? autoStartAction = null,
        Func<string, bool>? businessOpenTargetForAcceptance = null,
        TimeSpan? businessPairingPollIntervalForAcceptance = null)
    {
        _isSimulation = isSimulation;
        var executableDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var defaultBundleRoot = string.Equals(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(executableDirectory)),
            "launcher", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(Path.Combine(executableDirectory, ".."))
            : executableDirectory;
        _bundleRoot = Path.GetFullPath(bundleRoot ?? defaultBundleRoot);
        if (verifiedBundle is not null && !Path.GetFullPath(verifiedBundle.BundleRoot).Equals(_bundleRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("已验证发行描述符与窗口包根目录不一致。", nameof(verifiedBundle));
        }
        _verifiedBundle = verifiedBundle;
        _restoreSessionFactory = restoreSessionFactory;
        _launcherPreferencesStore = isSimulation ? null : preferencesStore ?? new LauncherPreferencesStore(_bundleRoot);
        _autoStartAction = autoStartAction;
        _launcherPreferencesStatus = _launcherPreferencesStore is null
            ? "模拟模式不会读取或保存真实 Launcher 偏好。"
            : "发行包预检通过后读取本地偏好。";
        if (_launcherPreferencesStore is null)
        {
            _preferencesLoaded = true;
        }
        else if (preferencesStore is not null)
        {
            // An explicitly injected store is a test seam; production defers disk I/O until bundle preflight.
            try
            {
                ApplyLauncherPreferences(preferencesStore.Load());
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceWarning($"加载注入的 Launcher 偏好失败；异常类型：{exception.GetType().Name}");
                ApplyLauncherPreferences(new LauncherPreferences());
            }
        }
        _businessConnection = new LauncherBusinessConnectionViewModel(
            userClientFactory ?? CreateBusinessUserClientAsync,
            businessOpenTargetForAcceptance ?? OpenBusinessTarget,
            isSimulation,
            pairingPollInterval: businessPairingPollIntervalForAcceptance);
        Components = new ObservableCollection<ComponentStatusRow>();
        Logs = new ObservableCollection<LogRow>();
        Activities = new ObservableCollection<LogRow>();
        LogView = new LauncherLogViewModel(Logs, Activities);
        Logs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLogs));
        Activities.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasActivities));
        if (isSimulation)
        {
            _serviceState = "未开始";
            _phaseMessage = "点击“开始模拟演练”可验证启动、取消和停止交互。";
            _primaryButtonText = "开始模拟演练";
            _isPrimaryEnabled = true;
            _cancelHint = "取消仅在标记为安全的阶段可用。";
            _serviceDetail = "显式测试模式；不读取 current.json，不启动真实服务。";
            CreateSimulationCoordinator();
        }
        else
        {
            _serviceState = "正在校验发行包";
            _phaseMessage = "正在从 Launcher 所在目录验证 current.json、组件身份与完整 manifest。";
            _primaryButtonText = "等待预检";
            _cancelHint = "预检完成前不会创建实例或启动服务。";
            _serviceDetail = "未完成预检；不会回退到模拟模式。";
            Components.Add(new ComponentStatusRow("postgres", "PostgreSQL", "等待发行包预检"));
            Components.Add(new ComponentStatusRow("database-provision", "业务数据库准备", "等待发行包预检"));
            Components.Add(new ComponentStatusRow("python-web", "Python Web 服务", "等待发行包预检"));
            StartupDiagnostics.Current.Recorded += OnStartupDiagnostic;
            foreach (var entry in StartupDiagnostics.Current.Snapshot()) OnStartupDiagnostic(StartupDiagnostics.Current, entry);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ComponentStatusRow> Components { get; }

    public ObservableCollection<LogRow> Logs { get; }
    public ObservableCollection<LogRow> Activities { get; }
    public LauncherLogViewModel LogView { get; }
    public bool HasLogs => Logs.Count > 0;
    public bool HasActivities => Activities.Count > 0;
    public bool IsActivityView => _isActivityView;
    public bool IsRawLogView => !_isActivityView;

    internal void SelectLogView(bool activity)
    {
        if (_isActivityView == activity) return;
        _isActivityView = activity;
        OnPropertyChanged(nameof(IsActivityView));
        OnPropertyChanged(nameof(IsRawLogView));
    }

    private void AddActivity(LogRow row)
    {
        while (Activities.Count >= UiActivityCapacity) Activities.RemoveAt(Activities.Count - 1);
        Activities.Insert(0, row);
    }

    public LauncherBusinessConnectionViewModel BusinessConnection => _businessConnection;

    public bool IsSimulation => _isSimulation;

    public bool IsOverviewPage => _selectedPage is LauncherPage.Overview;
    public bool IsAiPage => _selectedPage is LauncherPage.Ai;
    public bool IsSettingsPage => _selectedPage is LauncherPage.Settings;
    public bool IsDiagnosticsPage => _selectedPage is LauncherPage.Diagnostics;
    public bool IsLogsPage => _selectedPage is LauncherPage.Logs;

    public string CurrentPageTitle => _selectedPage switch
    {
        LauncherPage.Overview => "首页",
        LauncherPage.Ai => "账号与 AI 连接",
        LauncherPage.Settings => "启动器设置",
        LauncherPage.Diagnostics => "诊断与备份",
        LauncherPage.Logs => "运行日志",
        _ => "首页",
    };

    public string CurrentPageDescription => _selectedPage switch
    {
        LauncherPage.Overview => "查看本地服务状态，准备好后打开管理界面。",
        LauncherPage.Ai => "连接当前 Web 账号，管理默认 AI 配置。",
        LauncherPage.Settings => "调整启动偏好与本机服务端口。",
        LauncherPage.Diagnostics => "查看状态，按需创建备份或导出诊断摘要。",
        LauncherPage.Logs => "查看本机组件的只读运行记录。",
        _ => string.Empty,
    };

    internal void SelectPage(LauncherPage page)
    {
        if (_selectedPage == page)
        {
            return;
        }

        _selectedPage = page;
        OnPropertyChanged(nameof(IsOverviewPage));
        OnPropertyChanged(nameof(IsAiPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsDiagnosticsPage));
        OnPropertyChanged(nameof(IsLogsPage));
        OnPropertyChanged(nameof(CurrentPageTitle));
        OnPropertyChanged(nameof(CurrentPageDescription));
    }

    public string ModeBadge => _isSimulation ? "模拟测试" : "真实本地实例";

    public string HeaderTitle => _isSimulation ? "本地运行控制演练" : "本地服务控制";

    public string HeaderDescription => _isSimulation
        ? "显式测试模式，仅验证状态机、反馈与有界日志。"
        : "手动启动本机 PostgreSQL 与 Web；不会自动登录或发送 AI 测试请求，任务行为遵循 Web 设置。";

    public string StatusSectionTitle => _isSimulation ? "模拟演练状态" : "实例状态";

    public string ComponentSectionTitle => _isSimulation ? "模拟组件" : "本地组件";

    public string ComponentSectionDetail => _isSimulation
        ? "名称和状态均带“模拟”标识。"
        : "就绪必须同时通过进程身份、数据库和 schema 契约。";

    public string LogTitle => _isSimulation ? "只读演练日志" : "只读启动日志";

    public bool CanEditLauncherPreferences => !_isSimulation && _preferencesLoaded && _launcherPreferencesStore is not null;

    public bool CanConfigureWebPort => !_isSimulation && _webPortUiHost is not null && _webPortSettingsLoaded;

    private bool IsRealHostWebPortStateKnown => _isSimulation || _webPortUiHost is null ||
        (_webPortSettingsLoaded && !_webPortIntentUnresolved && !_webPortRefreshFailed && !_webPortOperationInFlight);

    public bool HasWebPortPanel => !_isSimulation && _webPortUiHost is not null;

    public bool AutomaticWebPort
    {
        get => _automaticWebPort;
        set
        {
            if (SetField(ref _automaticWebPort, value))
                OnPropertyChanged(nameof(CanEnterWebPort));
        }
    }

    public bool CanEnterWebPort => CanStageWebPort && !AutomaticWebPort;

    public string WebPortInput
    {
        get => _webPortInput;
        set
        {
            if (SetField(ref _webPortInput, value ?? string.Empty))
            {
                WebPortError = string.Empty;
                NotifyWebPortActions();
            }
        }
    }

    public string WebPortError
    {
        get => _webPortError;
        private set
        {
            if (SetField(ref _webPortError, value))
            {
                OnPropertyChanged(nameof(HasWebPortError));
            }
        }
    }

    public bool HasWebPortError => !string.IsNullOrWhiteSpace(WebPortError);

    public string WebPortStatus { get => _webPortStatus; private set => SetField(ref _webPortStatus, value); }

    public string EffectiveWebPortText => _effectiveWebPort == 0 ? "尚未读取" : _effectiveWebPort.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string PendingWebPortText => _pendingWebPort is { } pending
        ? pending.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "无";

    public bool HasPendingWebPort => _pendingWebPort is not null;

    public bool CanStageWebPort => CanConfigureWebPort && !_webPortOperationInFlight && !_shutdownInProgress &&
        !_backupInFlight && !_restoreInFlight && !_webPortIntentUnresolved && !_webPortRefreshFailed && IsCoordinatorStableForPortEdit();

    public bool CanApplyWebPort => CanStageWebPort && HasPendingWebPort &&
        IsCoordinatorConfirmedStopped();

    public bool OpenManagementPageOnReady
    {
        get => _openManagementPageOnReady;
        set => SaveLauncherPreference(_launcherPreferences with { OpenManagementPage = value }, ref _openManagementPageOnReady, value, nameof(OpenManagementPageOnReady));
    }

    public bool AutoStartOnLauncherOpen
    {
        get => _autoStartOnLauncherOpen;
        set => SaveLauncherPreference(_launcherPreferences with { AutoStartOnLauncherOpen = value }, ref _autoStartOnLauncherOpen, value, nameof(AutoStartOnLauncherOpen));
    }

    public string LauncherPreferencesStatus { get => _launcherPreferencesStatus; private set => SetField(ref _launcherPreferencesStatus, value); }

    public WindowCloseChoice CloseChoicePreference => _launcherPreferences.CloseChoice;

    public string ServiceState
    {
        get => _serviceState;
        private set
        {
            if (SetField(ref _serviceState, value))
            {
                NotifyPromptAppearance();
            }
        }
    }

    public string PhaseMessage { get => _phaseMessage; private set => SetField(ref _phaseMessage, value); }

    public string ServiceDetail { get => _serviceDetail; private set => SetField(ref _serviceDetail, value); }

    public string ObservationSummary { get => _observationSummary; private set => SetField(ref _observationSummary, value); }

    public string FreshnessText { get => _freshnessText; private set => SetField(ref _freshnessText, value); }

    public string PrimaryButtonText
    {
        get => _primaryButtonText;
        private set
        {
            if (SetField(ref _primaryButtonText, value))
            {
                NotifyPromptAppearance();
                OnPropertyChanged(nameof(DisplayPrimaryButtonText));
            }
        }
    }

    // Presentation only: execution still goes through the existing primary-action gate.
    public string DisplayPrimaryButtonText => SetupRequired && PrimaryButtonText == "打开管理页"
        ? "设置登录密码" : PrimaryButtonText;

    private bool HasBlockingIssue => _primaryButtonText is "需要诊断" or "无法启动" ||
        _serviceState.Contains("拒绝", StringComparison.Ordinal) ||
        _serviceState.Contains("失败", StringComparison.Ordinal);

    public IBrush PromptBackground => Brush.Parse(HasBlockingIssue ? "#FFF7E4" : "#FFFFFF");
    public IBrush PromptBorderBrush => Brush.Parse(HasBlockingIssue ? "#EAD39C" : "#DFE3E8");
    public IBrush PromptForeground => Brush.Parse(HasBlockingIssue ? "#5F4C2C" : "#344458");
    public IBrush StatusAccentBrush => Brush.Parse(HasBlockingIssue ||
        _serviceState.Contains("异常", StringComparison.Ordinal) ||
        _serviceState.Contains("未完全停止", StringComparison.Ordinal)
            ? "#D97706"
            : _serviceState.Contains("运行中", StringComparison.Ordinal)
                ? "#16A34A"
                : _serviceState.Contains("启动中", StringComparison.Ordinal) ||
                    _serviceState.Contains("校验", StringComparison.Ordinal) ||
                    _serviceState.Contains("恢复", StringComparison.Ordinal)
                    ? "#0868BB"
                    : "#9AA7B4");

    private void NotifyPromptAppearance()
    {
        OnPropertyChanged(nameof(PromptBackground));
        OnPropertyChanged(nameof(PromptBorderBrush));
        OnPropertyChanged(nameof(PromptForeground));
        OnPropertyChanged(nameof(StatusAccentBrush));
    }

    public bool IsPrimaryEnabled { get => _isPrimaryEnabled; private set => SetField(ref _isPrimaryEnabled, value); }

    public bool IsCancelEnabled { get => _isCancelEnabled; private set => SetField(ref _isCancelEnabled, value); }

    public bool IsStopEnabled { get => _isStopEnabled; private set => SetField(ref _isStopEnabled, value); }

    public string CancelHint { get => _cancelHint; private set => SetField(ref _cancelHint, value); }

    public string ManagementUrl
    {
        get => _managementUrl;
        private set
        {
            if (SetField(ref _managementUrl, value))
            {
                OnPropertyChanged(nameof(IsManagementReady));
            }
        }
    }

    public bool IsManagementReady => Uri.TryCreate(_managementUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" && uri.Host is "127.0.0.1";

    public bool SetupRequired
    {
        get => _setupRequired;
        private set
        {
            if (SetField(ref _setupRequired, value))
            {
                NotifySetupProperties();
            }
        }
    }

    public bool CanOpenSetup => SetupRequired && _realHost is not null && !_webPortIntentUnresolved && !_webPortRefreshFailed;

    public bool CanCreateBusinessBackup =>
        !_isSimulation && !_backupInFlight && !_shutdownInProgress &&
        Volatile.Read(ref _shutdownRequested) == 0 &&
        !_webPortIntentUnresolved && !_webPortRefreshFailed &&
        _coordinator?.Snapshot.State is LauncherState.Running && _realHost is not null;

    public bool CanUpgradeSchema => CanCreateBusinessBackup && _realHost?.CurrentSchemaVersion == 1;

    public bool CanRestoreBusinessBackup =>
        !_isSimulation && _bundle is not null && !_restoreInFlight && !_backupInFlight && !_shutdownInProgress &&
        !_webPortIntentUnresolved && !_webPortRefreshFailed &&
        Volatile.Read(ref _shutdownRequested) == 0 &&
        (_coordinator is null
            ? IsNewInstallRestoreAllowed(_isSimulation, _bundle is not null, _realHost is not null, !_newInstallReady)
            : _coordinator.Snapshot.Components.Count > 0 &&
              _coordinator.Snapshot.IsQuiescent);

    public static bool IsNewInstallRestoreAllowed(
        bool isSimulation,
        bool bundleVerified,
        bool activeHostAttached,
        bool activeOrLegacyDataPresent) =>
        !isSimulation && bundleVerified && !activeHostAttached && !activeOrLegacyDataPresent;

    public bool IsRestorePreviewReady => _restoreSession?.Snapshot.State is PortableRestoreState.AwaitingUserConfirmation;

    public bool CanCancelBusinessRestore => _restoreSession is not null && !_restoreInFlight && !_shutdownInProgress;

    public string RestorePreviewText { get; private set; } = string.Empty;

    public bool CanExportDiagnostic => !_isSimulation && !_disposed && !_shutdownInProgress;

    internal void ShowDiagnosticUiFailure() =>
        PhaseMessage = "诊断摘要操作未能完成。请重试；详细错误信息不会显示在此界面。";

    public bool RequiresCloseChoice => !_isSimulation &&
        (_coordinator?.Snapshot.HasUnconfirmedStartupWork is true ||
         _coordinator?.Snapshot.Components.Any(component =>
             component.State is not (ComponentRuntimeState.Stopped or ComponentRuntimeState.Unknown)) is true);

    public string CreateDiagnosticPreview()
    {
        using var preview = CreateDiagnosticPreviewTicket();
        return preview.PreviewText;
    }

    internal LauncherDiagnosticPreviewTicket CreateDiagnosticPreviewTicket()
    {
        var generation = Interlocked.Read(ref _coordinatorGeneration);
        var coordinator = _coordinator;
        var hostIdentity = (object?)_realHost ?? (object?)_webPortUiHost ?? this;
        if (!CanExportDiagnostic ||
            Volatile.Read(ref _shutdownRequested) != 0 || _disposed)
        {
            throw new InvalidOperationException("当前没有可导出的真实实例状态。");
        }

        var snapshot = coordinator?.Snapshot ?? new LauncherSnapshot(null, LauncherState.NotStarted,
            "preflight", "", false, false, DateTimeOffset.UtcNow, Array.Empty<ComponentSnapshot>());
        var contents = LauncherDiagnosticExporter.CreatePreview(snapshot, coordinator?.DiagnosticEvents);
        if (!ReferenceEquals(coordinator, _coordinator) ||
            !ReferenceEquals(hostIdentity, (object?)_realHost ?? (object?)_webPortUiHost ?? this) ||
            generation != Interlocked.Read(ref _coordinatorGeneration))
        {
            throw new InvalidOperationException("Host 状态在创建诊断预览时发生变化；请重新打开诊断。");
        }

        return new LauncherDiagnosticPreviewTicket(
            contents,
            coordinator,
            hostIdentity,
            generation);
    }

    public string SetupHelp => SetupRequired
        ? "默认账号：admin。首次使用请设置登录密码。"
        : "首次设置帮助仅在服务明确报告需要创建管理员时显示。";

    public async Task InitializeAsync()
    {
        if (_isSimulation || _disposed || Interlocked.Exchange(ref _initializationStarted, 1) != 0)
        {
            return;
        }

        await _realOperationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            StartupDiagnostics.Current.Record(StartupStage.PackageVerification, StartupEventKind.Begin);
            var bundle = _verifiedBundle ?? (Program.DevelopmentRequest is { } development &&
                string.Equals(development.SessionRoot, _bundleRoot, StringComparison.OrdinalIgnoreCase)
                ? await development.LoadAsync().ConfigureAwait(false)
                : await PortableBundleDescriptor.LoadAndVerifyAsync(_bundleRoot).ConfigureAwait(false));
            StartupDiagnostics.Current.Record(StartupStage.PackageVerification, StartupEventKind.Completed);
            if (Volatile.Read(ref _shutdownRequested) != 0 || _disposed)
            {
                return;
            }

            _bundle = bundle;
            if (_launcherPreferencesStore is not null)
            {
                LauncherPreferences loadedPreferences;
                try
                {
                    loadedPreferences = await Task.Run(_launcherPreferencesStore.Load).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Trace.TraceWarning($"加载 Launcher 本地偏好失败；异常类型：{exception.GetType().Name}");
                    loadedPreferences = new LauncherPreferences();
                }
                await Dispatcher.UIThread.InvokeAsync(() => ApplyLauncherPreferences(loadedPreferences));
            }
            if (!RealPortableStackHost.HasPersistentInstance(bundle))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    MarkNewInstallReady();
                    ShowManualStartReady("发行包与组件 manifest 已校验。当前没有持久实例；请手动点击“一键启动”。");
                });
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ServiceState = "正在恢复已有实例";
                ServiceDetail = $"发行包 {bundle.ReleaseId} · 应用 {bundle.AppVersion} · schema {bundle.SchemaMinimum}–{bundle.SchemaMaximum}";
                PhaseMessage = "检测到持久实例，正在核验原 PostgreSQL 与 Python 身份；不会自动启动替代服务。";
                PrimaryButtonText = "正在核验";
                IsPrimaryEnabled = false;
                IsStopEnabled = false;
            });

            StartupDiagnostics.Current.Record(StartupStage.InstanceRecovery, StartupEventKind.Begin);
            var host = await Task.Run(() => RealPortableStackHost.OpenExistingForRecovery(bundle)).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => AttachRealHost(host));
            var recovery = await host.TryRecoverExistingAsync().ConfigureAwait(false);
            if (recovery.Succeeded)
            {
                var readiness = await host.RefreshReadinessAsync().ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ManagementUrl = IsRealHostWebPortStateKnown ? host.ManagementUrl : "端口状态待核验";
                    SetupRequired = readiness.SetupRequired is true;
                    PhaseMessage = recovery.Message;
                    NotifySetupProperties();
                    if (IsRealHostWebPortStateKnown)
                    {
                        TryOpenManagementWhenReady(readiness);
                    }
                });
                await SetBusinessHostReadyAsync(IsRealHostWebPortStateKnown &&
                    readiness.State is PythonReadinessState.Ready && readiness.SetupRequired is not true);
                return;
            }

            var allStopped = recovery.Snapshot.IsQuiescent;
            if ((recovery.ErrorCode is "EXISTING_INSTANCE_STOPPED" or "INSTANCE_NOT_INITIALIZED") && allStopped)
            {
                if (recovery.ErrorCode == "EXISTING_INSTANCE_STOPPED") MarkVerifiedStoppedExistingForAutoStart();
                await Dispatcher.UIThread.InvokeAsync(() => ShowManualStartReady(
                    recovery.ErrorCode == "INSTANCE_NOT_INITIALIZED" ? recovery.Message : "发行包与既有实例已校验，所有组件均确认停止。"));
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ServiceState = "已有实例恢复被拒绝";
                PhaseMessage = recovery.Message;
                PrimaryButtonText = "需要诊断";
                IsPrimaryEnabled = false;
                IsStopEnabled = true;
                CancelHint = "Launcher 保持实例锁；不会启动第二套服务。可尝试“停止服务”正常停止已核验且可控的组件。";
            });
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Current.Record(_bundle is null ? StartupStage.PackageVerification : StartupStage.InstanceRecovery,
                StartupEventKind.Failed, exception: exception);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ServiceState = _bundle is null ? "发行包不可用" : "已有实例恢复被拒绝";
                ServiceDetail = _bundle is null
                    ? "缺少或不匹配的 current.json、manifest、组件目录或文件完整性；未启动任何服务。"
                    : "持久实例未通过完整身份与就绪核验；不会自动启动、停止、provision 或覆盖现有服务。";
                PhaseMessage = $"{(_bundle is null ? "预检" : "恢复")}失败：{exception.Message}";
                PrimaryButtonText = _bundle is null ? "无法启动" : "需要诊断";
                IsPrimaryEnabled = false;
                IsStopEnabled = _realHost is not null;
            });
        }
        finally
        {
            _realOperationGate.Release();
        }
    }

    public async Task TryAutoStartVerifiedStoppedExistingAsync()
    {
        if (_isSimulation || !_autoStartOnLauncherOpen || !_verifiedStoppedExistingForAutoStart ||
            Interlocked.Exchange(ref _autoStartAttempted, 1) != 0)
        {
            return;
        }

        _verifiedStoppedExistingForAutoStart = false;
        if (_autoStartAction is not null)
        {
            await _autoStartAction().ConfigureAwait(false);
        }
        else
        {
            await RunPrimaryActionAsync().ConfigureAwait(false);
        }
    }

    internal void MarkVerifiedStoppedExistingForAutoStart() => _verifiedStoppedExistingForAutoStart = true;

    public async Task RunPrimaryActionAsync()
    {
        if (_isSimulation)
        {
            await RunSimulationPrimaryActionAsync().ConfigureAwait(false);
            return;
        }

        if (Volatile.Read(ref _shutdownRequested) != 0 || _disposed)
        {
            PhaseMessage = "Launcher 正在关闭，不能再启动本地组件。";
            return;
        }
        if (_webPortUiHost is not null && !IsRealHostWebPortStateKnown)
        {
            PhaseMessage = "本实例 Web 端口状态未知；已禁用启动与管理页入口，请重新核验端口配置。";
            return;
        }
        if (_restoreSession is not null || _restoreInFlight)
        {
            PhaseMessage = "恢复会话尚未结束；先确认切换或取消恢复，不能启动其他实例操作。";
            return;
        }

        if (!await _realOperationGate.WaitAsync(0).ConfigureAwait(false))
        {
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "已有启动、停止或关闭操作正在进行，请等待当前操作完成。");
            return;
        }

        try
        {
            if (Volatile.Read(ref _shutdownRequested) != 0 || _disposed)
            {
                return;
            }

            if (_coordinator?.Snapshot.State is LauncherState.Running)
            {
                await Dispatcher.UIThread.InvokeAsync(OpenManagementPage);
                return;
            }

            if (_bundle is null && _webPortUiHost is null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "发行包尚未通过预检，不能启动。");
                return;
            }

            _managementPageOpenAttemptedForReadyGeneration = false;

            if (_webPortUiHost is null)
            {
                var host = await Task.Run(() => RealPortableStackHost.Create(_bundle!)).ConfigureAwait(false);
                if (Volatile.Read(ref _shutdownRequested) != 0 || _disposed)
                {
                    await host.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() => AttachRealHost(host));
            }

            if (!IsRealHostWebPortStateKnown)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ManagementUrl = "端口状态待核验";
                    PhaseMessage = "本实例 Web 端口配置未能核验；服务未启动，请重新打开 Launcher 后检查。";
                    IsPrimaryEnabled = false;
                    NotifyWebPortActions();
                });
                return;
            }

            var uiHost = _webPortUiHost!;
            var started = await ExecuteHostOperationAsync(uiHost,
                () => uiHost.Runtime.StartAsync(), portMaintenance: false).ConfigureAwait(false);
            if (!started.Operation.Succeeded)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    PhaseMessage = started.Operation.Message;
                    if (started.Operation.ErrorCode == "WEB_PORT_UNAVAILABLE")
                    {
                        WebPortStatus = $"有效端口 {started.EffectivePort} 无法绑定；服务尚未启动。";
                        WebPortError = $"端口 {started.EffectivePort} 不可用，请暂存并应用可用端口。";
                    }
                });
                return;
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ManagementUrl = IsRealHostWebPortStateKnown ? uiHost.ManagementUrl : "端口状态待核验";
                SetupRequired = started.SetupRequired;
                NotifySetupProperties();
                if (started.PortChanged)
                    WebPortStatus = $"原端口不可用，已自动改用 {started.EffectivePort}；当前地址为 {uiHost.ManagementUrl}。";
                if (IsRealHostWebPortStateKnown)
                    TryOpenManagementWhenReady(ready: true, setupRequired: started.SetupRequired);
            });
            await SetBusinessHostReadyAsync(IsRealHostWebPortStateKnown && !started.SetupRequired);

        }
        catch (Exception exception)
        {
            StartupDiagnostics.Current.Record(StartupStage.Start, StartupEventKind.Failed, exception: exception);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PhaseMessage = exception is ILauncherSafeFailure { SafeDiagnosticCode: { } code }
                    ? LauncherSafeFailureMessages.For(code)
                    : "启动未完成；详细错误信息已省略。请保留数据并导出诊断摘要后寻求支持。";
                IsPrimaryEnabled = true;
                IsStopEnabled = _realHost is not null;
            });
        }
        finally
        {
            _realOperationGate.Release();
        }
    }

    public async Task RunStopActionAsync()
    {
        if (_restoreSession is not null || _restoreInFlight)
        {
            PhaseMessage = "恢复会话尚未结束；不能并行停止或接管恢复目标。";
            return;
        }
        if (!await _realOperationGate.WaitAsync(0).ConfigureAwait(false))
        {
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "已有启动、停止或关闭操作正在进行；启动中请使用安全取消。");
            return;
        }

        try
        {
        var coordinator = _coordinator;
        if (coordinator is null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "当前没有已加载的本地组件。");
            return;
        }

        try
        {
            await SetBusinessHostReadyAsync(false);
            var result = await (_webPortUiHost is { } host
                ? host.Runtime.StopAsync() : coordinator.StopAsync()).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PhaseMessage = $"停止未完成：{exception.Message}";
                IsStopEnabled = true;
            });
        }
        }
        finally
        {
            _realOperationGate.Release();
        }
    }

    public async Task CreateBusinessBackupAsync(string selectedDirectory, string passphrase)
    {
        if (_isSimulation || _disposed || Volatile.Read(ref _shutdownRequested) != 0)
        {
            PhaseMessage = "真实运行实例就绪后才能创建业务备份。";
            return;
        }

        if (!await _realOperationGate.WaitAsync(0).ConfigureAwait(false))
        {
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "已有启动、停止、关闭或备份操作正在进行。请等待完成后重试。");
            return;
        }

        try
        {
            var host = _realHost;
            if (host is null || _coordinator?.Snapshot.State is not LauncherState.Running || !CanUseBackupPassphrase(passphrase))
            {
                await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "备份未开始：实例必须处于已核验运行状态，口令须为 12 到 1024 个 UTF-8 字节。");
                return;
            }

            var destinationFolder = Path.GetFullPath(selectedDirectory);
            if (!Directory.Exists(destinationFolder) || IsWithinDirectory(host.DataRoot, destinationFolder))
            {
                await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "备份未开始：请选择数据目录之外的现有文件夹。");
                return;
            }

            var destination = CreateNewBackupPath(destinationFolder, DateTimeOffset.Now);
            _backupInFlight = true;
            await SetBusinessHostReadyAsync(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PhaseMessage = "正在停写并创建加密业务备份。Web 与 PostgreSQL 将安全停止；过程可能需要较长时间。";
                OnPropertyChanged(nameof(CanCreateBusinessBackup));
                OnPropertyChanged(nameof(CanUpgradeSchema));
            });

            try
            {
                var result = await host.CreateBusinessBackupAndStopAsync(destination, passphrase).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = $"业务备份已创建并通过内部校验。SHA-256：{result.Sha256} · 文件：{result.Destination} · Web 与 PostgreSQL 已停止。请手动启动服务以继续使用。");
            }
            catch (Exception exception)
            {
                Trace.TraceError($"业务备份未确认成功；异常类型：{exception.GetType().Name}");
                await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "业务备份未确认成功。请以实例状态卡为准，检查所选目录中的文件后再决定是否重试。未显示内部错误详情。");
            }
        }
        finally
        {
            _backupInFlight = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                OnPropertyChanged(nameof(CanCreateBusinessBackup));
                OnPropertyChanged(nameof(CanUpgradeSchema));
            });
            _realOperationGate.Release();
        }
    }

    public async Task UpgradeSchemaAsync(string selectedDirectory, string passphrase)
    {
        if (!await _realOperationGate.WaitAsync(0).ConfigureAwait(false))
        {
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "已有生命周期操作正在进行，请稍后重试升级。");
            return;
        }
        try
        {
            var host = _realHost;
            if (!CanUpgradeSchema || host is null || !CanUseBackupPassphrase(passphrase))
            {
                await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "升级未开始：须运行真实 schema v1 实例并输入有效备份口令。");
                return;
            }
            var folder = Path.GetFullPath(selectedDirectory);
            if (!Directory.Exists(folder) || IsWithinDirectory(host.DataRoot, folder))
            {
                await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "升级未开始：请选择数据目录之外的现有备份文件夹。");
                return;
            }
            var destination = CreateNewBackupPath(folder, DateTimeOffset.Now);
            _backupInFlight = true;
            await SetBusinessHostReadyAsync(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PhaseMessage = "正在停写、备份并升级到 schema v2；完成前不要关闭 Launcher。";
                OnPropertyChanged(nameof(CanCreateBusinessBackup));
                OnPropertyChanged(nameof(CanUpgradeSchema));
            });
            try
            {
                var result = await host.UpgradeSchemaWithBackupAndStopAsync(destination, passphrase)
                    .ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage =
                    $"schema v{result.SchemaVersion} 升级已核验；备份 SHA-256：{result.Backup.Sha256} · 文件：{result.Backup.Destination}。服务已停止，请手动启动。");
            }
            catch (Exception exception)
            {
                Trace.TraceError($"schema 升级未确认成功；异常类型：{exception.GetType().Name}");
                await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage =
                    "升级未确认成功，服务保持停止。备份文件可能已创建；请检查所选文件夹并保留现状，不要反复点击升级。");
            }
        }
        finally
        {
            _backupInFlight = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                OnPropertyChanged(nameof(CanCreateBusinessBackup));
                OnPropertyChanged(nameof(CanUpgradeSchema));
            });
            _realOperationGate.Release();
        }
    }

    public async Task RestoreBusinessBackupAsync(string archiveFile, string passphrase, string backupTrustConfirmationText)
    {
        if (_isSimulation || _disposed || Volatile.Read(ref _shutdownRequested) != 0 ||
            _webPortIntentUnresolved || _webPortRefreshFailed)
        {
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage =
                _webPortIntentUnresolved || _webPortRefreshFailed
                    ? "Web 端口事务或界面绑定尚未核验；恢复入口已禁用。请保留数据并查看诊断信息。"
                    : "真实发行包预检完成且所有服务确认停止后才能恢复业务备份。");
            return;
        }

        if (!await _realOperationGate.WaitAsync(0).ConfigureAwait(false))
        {
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "已有启动、停止、关闭、备份或恢复操作正在进行。");
            return;
        }

        try
        {
            var bundle = _bundle;
            var coordinator = _coordinator;
            if (bundle is null || _restoreSession is not null || !CanUseBackupPassphrase(passphrase))
            {
                await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "恢复未开始：发行包须已验证、口令须为 12 到 1024 个 UTF-8 字节，且当前无其他恢复会话。");
                return;
            }

            _restoreInFlight = true;
            await SetBusinessHostReadyAsync(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                OnPropertyChanged(nameof(CanRestoreBusinessBackup));
                PhaseMessage = "正在重新采样所有组件；恢复仅允许在 Web 与 PostgreSQL 均确认停止时进行。";
            });

            if (coordinator is null)
            {
                if (!IsNewInstallRestoreAllowed(_isSimulation, bundle is not null, _realHost is not null, !_newInstallReady))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "恢复已阻止：既有实例尚未完成身份与组件状态核验。");
                    return;
                }

                // New installation has no active host/instance. The restore catalog creates
                // its own isolated target and never overwrites the stable data container.
                if (RealPortableStackHost.HasPersistentInstance(bundle!))
                {
                    _newInstallReady = false;
                    await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "恢复已阻止：发现既有数据，需先通过 Launcher 身份与停机核验。");
                    return;
                }
            }
            else
            {
                var snapshot = await coordinator.RefreshObservationAsync().ConfigureAwait(false);
                if (snapshot.Components.Count == 0 || !snapshot.IsQuiescent)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "恢复已阻止：至少一个组件仍在运行或状态未知。请先安全停止全部组件，再重新开始。");
                    return;
                }
            }

            // Release the current Host's active-instance lease before the restore session
            // later attempts its independently verified source lease guard.
            if (_realHost is not null)
            {
                await ReleaseStoppedHostAsync().ConfigureAwait(false);
            }

            _restoreSession = _restoreSessionFactory?.Invoke(bundle!) ?? RealPortableStackHost.CreateBusinessRestoreSession(bundle!);
            _restoreStartedFromNewInstall = coordinator is null;
            _newInstallReady = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsPrimaryEnabled = false;
                IsStopEnabled = false;
                OnPropertyChanged(nameof(CanCancelBusinessRestore));
            });
            _restoreCancellation = new CancellationTokenSource();
            var restoreSnapshot = await _restoreSession.RestoreAndPreviewAsync(
                archiveFile,
                passphrase,
                backupTrustConfirmationText,
                _restoreCancellation.Token).ConfigureAwait(false);
            if (restoreSnapshot.State is not PortableRestoreState.AwaitingUserConfirmation || restoreSnapshot.Preview is null)
            {
                throw new InvalidOperationException("恢复维护审计未生成可确认的差异预览。");
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RestorePreviewText = FormatRestorePreview(restoreSnapshot.Preview);
                OnPropertyChanged(nameof(RestorePreviewText));
                OnPropertyChanged(nameof(IsRestorePreviewReady));
                PhaseMessage = "恢复核验已通过；旧数据仍为活动数据。阅读差异与数据损失说明后，明确确认才会切换活动实例。";
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "恢复已取消；活动实例未更改，隔离目标按诊断策略保留。");
        }
        catch (PortableRestoreException exception)
        {
            Trace.TraceError($"业务恢复失败；错误码：{exception.Code}");
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = $"业务恢复未完成（{SafeRestoreErrorCode(exception.Code)}）。旧活动数据未切换；隔离目标保留供诊断。");
        }
        catch (Exception exception)
        {
            Trace.TraceError($"业务恢复未确认成功；异常类型：{exception.GetType().Name}");
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "业务恢复未确认成功。旧活动数据未切换；请检查诊断记录后再决定是否重试。");
        }
        finally
        {
            _restoreCancellation?.Dispose();
            _restoreCancellation = null;
            _restoreInFlight = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                OnPropertyChanged(nameof(CanRestoreBusinessBackup));
                OnPropertyChanged(nameof(CanCancelBusinessRestore));
            });
            _realOperationGate.Release();
        }
    }

    public async Task<bool> ConfirmBusinessRestoreAsync(string confirmationText)
    {
        if (_restoreSession is null || !IsRestorePreviewReady)
        {
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "没有已通过维护核验的恢复预览；活动实例保持不变。");
            return false;
        }

        if (!await _realOperationGate.WaitAsync(0).ConfigureAwait(false))
        {
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "已有 Launcher 操作正在进行；尚未提交活动实例切换。");
            return false;
        }

        try
        {
            _restoreInFlight = true;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                OnPropertyChanged(nameof(CanRestoreBusinessBackup));
                OnPropertyChanged(nameof(CanCancelBusinessRestore));
            });
            var result = await _restoreSession.ConfirmActivationAsync(confirmationText).ConfigureAwait(false);
            if (result.State is not PortableRestoreState.Activated)
            {
                throw new InvalidOperationException("恢复会话未确认活动指针切换。");
            }

            await _restoreSession.DisposeAsync().ConfigureAwait(false);
            _restoreSession = null;
            _newInstallReady = false;
            _restoreStartedFromNewInstall = false;
            RestorePreviewText = string.Empty;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                OnPropertyChanged(nameof(RestorePreviewText));
                OnPropertyChanged(nameof(IsRestorePreviewReady));
                OnPropertyChanged(nameof(CanCancelBusinessRestore));
                ServiceState = "恢复副本已设为活动数据";
                ServiceDetail = "旧实例完整保留；新活动实例尚未启动。";
                PhaseMessage = "活动实例已按确认切换。请检查说明后手动启动服务；不会自动恢复抓取、AI 或通知任务。";
                ManagementUrl = "尚未启动";
                PrimaryButtonText = "一键启动";
                IsPrimaryEnabled = true;
                IsStopEnabled = false;
                FreshnessText = "活动数据已切换；启动后重新采样。";
                foreach (var component in Components) component.Status = "已停止";
            });
            return true;
        }
        catch (PortableRestoreException exception)
        {
            Trace.TraceError($"活动实例切换失败；错误码：{exception.Code}");
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = $"活动实例切换未完成（{SafeRestoreErrorCode(exception.Code)}）。旧活动数据仍保留。");
            return false;
        }
        catch (Exception exception)
        {
            Trace.TraceError($"活动实例切换未确认成功；异常类型：{exception.GetType().Name}");
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "活动实例切换未确认成功；请刷新状态。旧实例完整保留。");
            return false;
        }
        finally
        {
            _restoreInFlight = false;
            await Dispatcher.UIThread.InvokeAsync(() => OnPropertyChanged(nameof(CanRestoreBusinessBackup)));
            _realOperationGate.Release();
        }
    }

    public async Task CancelBusinessRestoreAsync()
    {
        if (!await _realOperationGate.WaitAsync(0).ConfigureAwait(false))
        {
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "恢复或其他操作仍在执行；请等待安全点。活动指针未切换。");
            return;
        }

        try
        {
            if (_restoreSession is null) return;
            var state = _restoreSession.Snapshot.State;
            if (state is PortableRestoreState.AwaitingUserConfirmation or PortableRestoreState.FailedTargetRetained)
            {
                await _restoreSession.CancelAndRetainTargetAsync().ConfigureAwait(false);
            }
            await _restoreSession.DisposeAsync().ConfigureAwait(false);
            _restoreSession = null;
            _newInstallReady = _restoreStartedFromNewInstall;
            _restoreStartedFromNewInstall = false;
            RestorePreviewText = string.Empty;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                OnPropertyChanged(nameof(RestorePreviewText));
                OnPropertyChanged(nameof(IsRestorePreviewReady));
                OnPropertyChanged(nameof(CanRestoreBusinessBackup));
                OnPropertyChanged(nameof(CanCancelBusinessRestore));
                PhaseMessage = "已取消活动切换；隔离恢复目标保留供诊断，旧活动数据可重新启动。";
                IsPrimaryEnabled = _bundle is not null;
                IsStopEnabled = false;
            });
        }
        catch (Exception exception)
        {
            Trace.TraceError($"结束恢复会话失败；异常类型：{exception.GetType().Name}");
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "无法安全结束恢复会话；请保留 Launcher 窗口并检查诊断记录。");
        }
        finally
        {
            _realOperationGate.Release();
        }
    }

    internal static string FormatRestorePreview(PortableRestorePreview preview)
    {
        var counts = string.Join("\n", preview.TableCounts.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"  {SanitizePreviewLabel(item.Key)}：{item.Value:N0}"));
        var sourceInstanceId = Guid.TryParse(preview.SourceInstanceId, out var sourceId)
            ? sourceId.ToString("D")
            : "备份未提供有效来源实例 ID";
        return $"来源实例：{sourceInstanceId}\n" +
            $"恢复目标：{preview.TargetInstanceId}\n" +
            $"备份 SHA-256：{preview.BackupSha256}\n" +
            $"备份时间：{preview.BackupCreatedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "备份未提供时间"}\n" +
            $"恢复文件数：{preview.RestoredFileCount:N0} · 总大小：{(preview.RestoredFileBytes is { } bytes ? FormatBytes(bytes) : "未知")}\n" +
            $"已撤销会话数：{preview.RevokedSessionCount:N0}\n" +
            $"表行数：\n{counts}\n\n" +
            "切换后会使用备份时的数据；备份之后新增、修改或删除的数据不会与旧实例自动合并。原活动实例完整保留，可在未切换时重新启动。恢复副本中的旧登录会话已撤销，需要重新登录。切换不会自动启动服务或任务。";
    }

    private static string SanitizePreviewLabel(string value) =>
        value.Length is > 0 and <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? value
            : "未识别表";

    private static string SafeRestoreErrorCode(string code) =>
        code.Length is > 0 and <= 64 && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? code
            : "RESTORE_FAILED";

    internal static bool CanUseBackupPassphrase(string passphrase) =>
        !string.IsNullOrEmpty(passphrase) && System.Text.Encoding.UTF8.GetByteCount(passphrase) is >= 12 and <= 1024;

    public void ReportBackupUiFailure() => PhaseMessage = "备份窗口未能完成操作。请确认目标文件夹可访问后重试。";

    public void ReportRestoreUiFailure() => PhaseMessage = "恢复窗口未能完成操作。活动实例未切换；请重新选择备份并检查本机诊断记录。";

    internal static string CreateNewBackupPath(string selectedDirectory, DateTimeOffset timestamp)
    {
        var folder = Path.GetFullPath(selectedDirectory);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var name = $"business-backup-{timestamp:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.gfbk";
            var candidate = Path.Combine(folder, name);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("无法为备份生成不重名的目标文件名。");
    }

    private static bool IsWithinDirectory(string parentDirectory, string candidateDirectory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parentDirectory), Path.GetFullPath(candidateDirectory));
        return relative == "." || (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative));
    }

    private void MarkNewInstallReady()
    {
        if (_newInstallReady) return;
        _newInstallReady = true;
        OnPropertyChanged(nameof(CanRestoreBusinessBackup));
    }

    public void RequestCancellation()
    {
        if (_coordinator?.RequestStartupCancellation() is not true)
        {
            PhaseMessage = "当前没有可取消的启动操作。";
        }
    }

    public async Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _shutdownRequested, 1);
        _coordinator?.RequestStartupCancellation();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _shutdownInProgress = true;
            OnPropertyChanged(nameof(CanCreateBusinessBackup));
            OnPropertyChanged(nameof(CanUpgradeSchema));
            IsPrimaryEnabled = false;
            IsStopEnabled = false;
            IsCancelEnabled = false;
            PhaseMessage = _isSimulation ? "正在等待安全点并停止模拟组件。" : "正在等待安全点并正常停止 Web 与 PostgreSQL。";
        });

        try
        {
            await _realOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _shutdownRequested, 0);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _shutdownInProgress = false;
                PhaseMessage = "已取消退出，当前操作继续；窗口保持打开。";
                IsPrimaryEnabled = true;
                IsStopEnabled = true;
            });
            return false;
        }

        try
        {
            try
            {
                _restoreCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The restore already reached its completion path.
            }

            await DisposeRestoreSessionForShutdownAsync().ConfigureAwait(false);
            if (_coordinator is null)
            {
                return true;
            }

            var result = await _coordinator.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                Interlocked.Exchange(ref _shutdownRequested, 0);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _shutdownInProgress = false;
                    PhaseMessage = $"暂不能关闭：{result.Message}。窗口保持打开；没有强制结束进程。";
                    IsPrimaryEnabled = true;
                    IsStopEnabled = true;
                });
                return false;
            }

            if (!_isSimulation)
            {
                await ReleaseStoppedHostAsync().ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _shutdownRequested, 0);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _shutdownInProgress = false;
                    PhaseMessage = "服务已停止，但你取消了退出；窗口保持打开，可再次启动服务。";
                    IsPrimaryEnabled = true;
                    IsStopEnabled = false;
                });
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _shutdownRequested, 0);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _shutdownInProgress = false;
                PhaseMessage = $"关闭前收尾失败：{exception.Message}。窗口保持打开。";
                IsPrimaryEnabled = true;
                IsStopEnabled = true;
            });
            return false;
        }
        finally
        {
            _realOperationGate.Release();
        }
    }

    private async Task DisposeRestoreSessionForShutdownAsync()
    {
        var session = _restoreSession;
        if (session is null) return;

        var state = session.Snapshot.State;
        if (state is PortableRestoreState.AwaitingUserConfirmation or PortableRestoreState.FailedTargetRetained)
        {
            await session.CancelAndRetainTargetAsync().ConfigureAwait(false);
        }
        await session.DisposeAsync().ConfigureAwait(false);
        _restoreSession = null;
        RestorePreviewText = string.Empty;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(RestorePreviewText));
            OnPropertyChanged(nameof(IsRestorePreviewReady));
            OnPropertyChanged(nameof(CanCancelBusinessRestore));
        });
    }

    public void PrepareBackgroundWindow()
    {
        PhaseMessage = "Launcher 窗口已最小化到任务栏；本机服务继续运行。双击任务栏按钮可恢复窗口。";
    }

    public async Task StageWebPortAsync()
    {
        var host = _webPortUiHost;
        if (host is null || _isSimulation || _disposed || Volatile.Read(ref _shutdownRequested) != 0)
        {
            WebPortError = "当前没有可配置的便携实例。";
            return;
        }
        if (!await _realOperationGate.WaitAsync(0).ConfigureAwait(false))
        {
            WebPortStatus = "实例操作正在进行；端口未暂存。";
            return;
        }

        try
        {
            if (!ReferenceEquals(_webPortUiHost, host) || !CanStageWebPort)
            {
                WebPortStatus = "当前服务状态不稳定；端口未暂存。";
                return;
            }
            var candidatePort = _effectiveWebPort;
            if (!AutomaticWebPort && (!int.TryParse(WebPortInput, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out candidatePort) ||
                candidatePort is < 1024 or > 65535))
            {
                WebPortError = "请输入 1024 到 65535 之间的整数端口。";
                return;
            }
            if (candidatePort == host.PostgresPort)
            {
                WebPortError = $"Web 端口不能与本实例 PostgreSQL 端口 {host.PostgresPort} 相同。";
                return;
            }

            _webPortOperationInFlight = true;
            NotifyWebPortActions();
            var staged = host.StageWebPort(_webPortRevision, candidatePort, AutomaticWebPort);
            ApplyWebPortSettings(host, staged, updateManagementUrl: false);
            WebPortError = string.Empty;
            WebPortStatus = staged.Automatic
                ? "自动模式已保存；下次启动优先复用当前端口，占用时选择其他可用端口。运行中的服务不变。"
                : staged.PendingPort is null
                ? $"Web 端口已在 {staged.EffectivePort} 生效；没有待应用更改。"
                : $"已暂存端口 {staged.PendingPort}；当前访问地址仍使用 {staged.EffectivePort}。停止全部服务后可确认应用。";
        }
        catch (Exception exception) when (
            exception is PortableWebPortSettingsException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            WebPortError = exception.Message;
            WebPortStatus = "端口未暂存；当前生效地址未更改。";
        }
        finally
        {
            _webPortOperationInFlight = false;
            _realOperationGate.Release();
            NotifyWebPortActions();
        }
    }

    public async Task ApplyPendingWebPortAsync()
    {
        var host = _webPortUiHost;
        if (host is null || !CanApplyWebPort || _disposed || Volatile.Read(ref _shutdownRequested) != 0)
        {
            WebPortStatus = "端口应用需要已暂存候选且全部服务确认停止。";
            return;
        }

        if (!await _realOperationGate.WaitAsync(0).ConfigureAwait(false)) return;
        _webPortOperationInFlight = true;
        WebPortError = string.Empty;
        WebPortStatus = "正在核验服务停止状态并执行端口事务。";
        NotifyWebPortActions();
        try
        {
            var result = await ExecuteHostOperationAsync(host,
                () => host.ApplyPendingWebPortAsync(_webPortRevision)).ConfigureAwait(false);
            var settings = host.WebPortSettings;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyWebPortSettings(host, settings, updateManagementUrl: true);
                if (_webPortRefreshFailed)
                {
                    ManagementUrl = "界面绑定待核验";
                    WebPortStatus = _webPortIntentUnresolved
                        ? "端口事务仍待核验，Launcher 界面刷新失败；请保留数据并重新打开 Launcher。不要重复切换。"
                        : result.Applied
                        ? $"端口已提交为 {EffectiveWebPortText}，但 Launcher 界面尚未刷新；不要重复切换，请重新打开 Launcher。"
                        : $"旧端口 {EffectiveWebPortText} 已恢复，但 Launcher 界面尚未刷新；请重新打开 Launcher。";
                    WebPortError = "Coordinator 界面刷新未完成；服务端端口状态已重新读取。";
                }
                else if (result.Applied)
                {
                    WebPortStatus = $"端口已应用；当前访问地址为 {host.ManagementUrl}。需要重新配对 Launcher 账号。";
                    WebPortError = string.Empty;
                }
                else
                {
                    WebPortStatus = $"候选未应用；旧访问地址已恢复，候选端口 {result.PendingPort} 仍待处理。";
                    WebPortError = result.Message;
                }
            });
        }
        catch (Exception exception)
        {
            PortableWebPortSettings? refreshedSettings = null;
            Exception? readFailure = null;
            try
            {
                refreshedSettings = host.WebPortSettings;
            }
            catch (Exception failure)
            {
                readFailure = failure;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (refreshedSettings is not null)
                {
                    ApplyWebPortSettings(host, refreshedSettings, updateManagementUrl: true);
                    if (_webPortIntentUnresolved)
                    {
                        WebPortStatus = "端口事务或界面刷新仍待核验；自动操作已禁用。请保留数据并重新打开 Launcher，不要重复切换。";
                        ManagementUrl = "端口切换待核验";
                    }
                    else
                    {
                        WebPortStatus = "端口未应用；当前有效状态已重新读取。";
                    }
                }
                else
                {
                    _webPortSettingsLoaded = false;
                    _webPortIntentUnresolved = true;
                    _effectiveWebPort = 0;
                    _pendingWebPort = null;
                    _webPortRevision = 0;
                    WebPortInput = string.Empty;
                    ManagementUrl = "端口状态待核验";
                    WebPortStatus = "无法读取端口事务状态；端口操作和管理地址已禁用，请保留数据并查看诊断信息。";
                    NotifyWebPortActions();
                    Trace.TraceError($"Web端口状态重新读取失败；异常类型：{readFailure?.GetType().Name ?? "Unknown"}");
                }
                WebPortError = exception.Message;
            });
        }
        finally
        {
            _realOperationGate.Release();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _webPortOperationInFlight = false;
                NotifyWebPortActions();
            });
        }
    }

    private bool IsCoordinatorStableForPortEdit()
    {
        if (_coordinator is null || _webPortIntentUnresolved || !HasExpectedServices(_coordinator.Snapshot))
        {
            return false;
        }

        var snapshot = _coordinator.Snapshot;
        var components = snapshot.Components;
        return !snapshot.HasUnconfirmedStartupWork && (snapshot.State is LauncherState.Running &&
                   components.All(component => component.State is ComponentRuntimeState.Running) ||
               snapshot.State is (LauncherState.NotStarted or LauncherState.Stopped or LauncherState.Failed) &&
                   components.All(component => component.State is ComponentRuntimeState.Stopped));
    }

    private static bool HasExpectedServices(LauncherSnapshot snapshot) =>
        snapshot.Components.Count == 2 &&
        snapshot.Components.Any(component => component.Id == "postgres") &&
        snapshot.Components.Any(component => component.Id == "python-web");

    private bool IsCoordinatorConfirmedStopped() => _coordinator is not null &&
        HasExpectedServices(_coordinator.Snapshot) &&
        _coordinator.Snapshot.IsQuiescent;

    private void ApplyWebPortSettings(
        IPortableWebPortUiHost host,
        PortableWebPortSettings settings,
        bool updateManagementUrl)
    {
        if (!ReferenceEquals(_webPortUiHost, host))
        {
            return;
        }

        _effectiveWebPort = settings.EffectivePort;
        _pendingWebPort = settings.PendingPort;
        _webPortRevision = settings.Revision;
        AutomaticWebPort = settings.Automatic;
        WebPortInput = (settings.PendingPort ?? settings.EffectivePort)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        _webPortSettingsLoaded = true;
        _webPortIntentUnresolved = settings.Applying is not null;
        if (_webPortIntentUnresolved)
        {
            ManagementUrl = "端口切换待核验";
        }
        else if (updateManagementUrl)
        {
            ManagementUrl = host.ManagementUrl;
        }

        OnPropertyChanged(nameof(EffectiveWebPortText));
        OnPropertyChanged(nameof(PendingWebPortText));
        OnPropertyChanged(nameof(HasPendingWebPort));
        OnPropertyChanged(nameof(CanConfigureWebPort));
        NotifyWebPortActions();
    }

    private void NotifyWebPortActions()
    {
        OnPropertyChanged(nameof(CanEnterWebPort));
        OnPropertyChanged(nameof(HasWebPortPanel));
        OnPropertyChanged(nameof(CanConfigureWebPort));
        OnPropertyChanged(nameof(CanStageWebPort));
        OnPropertyChanged(nameof(CanApplyWebPort));
        OnPropertyChanged(nameof(EffectiveWebPortText));
        OnPropertyChanged(nameof(PendingWebPortText));
        OnPropertyChanged(nameof(HasPendingWebPort));
    }

    public void OpenManagementPage()
    {
        if (!IsRealHostWebPortStateKnown || _webPortIntentUnresolved || _webPortRefreshFailed ||
            _realHost is null || _coordinator?.Snapshot.State is not LauncherState.Running)
        {
            PhaseMessage = "管理页仅在本地服务确认运行后可打开。";
            return;
        }

        _ = OpenManagementPageCoreAsync();
    }

    private async Task OpenManagementPageCoreAsync()
    {
        if (Interlocked.Exchange(ref _managementPageOpening, 1) != 0) return;
        var host = _realHost;
        var generation = Interlocked.Read(ref _coordinatorGeneration);
        try
        {
            if (host is null || _disposed || Volatile.Read(ref _shutdownRequested) != 0) return;
            var target = await host.CreateManagementBrowserUrlAsync();
            if (!ReferenceEquals(host, _realHost) || generation != Interlocked.Read(ref _coordinatorGeneration) ||
                _disposed || Volatile.Read(ref _shutdownRequested) != 0 || _webPortIntentUnresolved || _webPortRefreshFailed ||
                _coordinator?.Snapshot.State is not LauncherState.Running) return;
            TryOpen(target, "管理页");
        }
        catch (Exception exception)
        {
            Trace.TraceError($"打开首次设置/管理页失败；异常类型：{exception.GetType().Name}");
            PhaseMessage = "管理页暂时无法打开，请确认服务运行后重试。无需查找设置码。";
        }
        finally
        {
            Interlocked.Exchange(ref _managementPageOpening, 0);
        }
    }

    public bool TrySaveCloseChoicePreference(WindowCloseChoice choice)
    {
        if (choice is not (WindowCloseChoice.Background or WindowCloseChoice.Exit) ||
            !CanEditLauncherPreferences || _launcherPreferencesStore is null)
        {
            return false;
        }

        var updated = _launcherPreferences with { CloseChoice = choice };
        if (!_launcherPreferencesStore.TrySave(updated))
        {
            LauncherPreferencesStatus = "关闭偏好未能保存；本次选择仍会生效。";
            return false;
        }

        _launcherPreferences = updated;
        LauncherPreferencesStatus = "Launcher 偏好已保存。";
        return true;
    }

    private void ApplyLauncherPreferences(LauncherPreferences preferences)
    {
        _launcherPreferences = preferences;
        _openManagementPageOnReady = preferences.OpenManagementPage;
        _autoStartOnLauncherOpen = preferences.AutoStartOnLauncherOpen;
        _preferencesLoaded = true;
        OnPropertyChanged(nameof(OpenManagementPageOnReady));
        OnPropertyChanged(nameof(AutoStartOnLauncherOpen));
        OnPropertyChanged(nameof(CanEditLauncherPreferences));
        LauncherPreferencesStatus = !string.IsNullOrEmpty(_launcherPreferencesStore?.LoadWarning)
            ? _launcherPreferencesStore.LoadWarning!
            : "Launcher 偏好已读取。自动启动只适用于身份已核验且明确停止的既有实例。";
    }

    public void OpenDataFolder()
    {
        TryOpenExistingFolder(_realHost?.DataRoot ?? Path.Combine(_bundleRoot, "data"), "数据目录");
    }

    public void OpenLogFolder()
    {
        TryOpenExistingFolder(StartupDiagnostics.Current.DirectoryPath ?? _realHost?.LogRoot ?? Path.Combine(_bundleRoot, "data", "logs"), "Launcher 启动日志目录");
    }

    public async Task RefreshStatusAsync()
    {
        if (!await _realOperationGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            var host = _realHost;
            var coordinator = _coordinator;
            if (coordinator is null)
            {
                // Initialization may have left a diagnostic in PhaseMessage. A later
                // automatic refresh must not erase the only actionable failure text.
                if (ServiceState == "正在校验发行包")
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        PhaseMessage = "组件尚未加载，无法采样运行状态。");
                }
                return;
            }

            var snapshot = await coordinator.RefreshObservationAsync().ConfigureAwait(false);
            PythonReadinessResult? readiness = null;
            if (host is not null && snapshot.State is LauncherState.Running)
            {
                readiness = await host.RefreshReadinessAsync().ConfigureAwait(false);
            }

            var drive = new DriveInfo(Path.GetPathRoot(_bundleRoot)!);
            var freeBytes = drive.AvailableFreeSpace;
            var memoryBytes = Environment.WorkingSet;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ServiceState = StateText(snapshot.State, snapshot.IsSimulation);
                PhaseMessage = snapshot.Message;
                ObservationSummary = $"本应用进程内存 {FormatBytes(memoryBytes)} · {Path.GetPathRoot(_bundleRoot)} 可用空间 {FormatBytes(freeBytes)}";
                FreshnessText = $"组件采样 {snapshot.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Freshness(snapshot.ObservedAt)}";
                if (readiness?.State is PythonReadinessState.Ready)
                {
                    SetupRequired = readiness.SetupRequired is true;
                    PhaseMessage = SetupRequired
                        ? "服务就绪，仍需完成首次管理员设置。"
                        : "服务、数据库与 schema 就绪；首次设置已完成。";
                    NotifySetupProperties();
                    TryOpenManagementWhenReady(readiness);
                }
                else if (readiness is not null)
                {
                    PhaseMessage = "组件已采样，但服务未满足完整就绪契约。";
                }
            });
            var businessReady = !_isSimulation && host is not null && snapshot.State is LauncherState.Running &&
                readiness?.State is PythonReadinessState.Ready && readiness.SetupRequired is not true;
            await SetBusinessHostReadyAsync(businessReady);
            if (businessReady)
            {
                await RunBusinessOnUiAsync(() => _businessConnection.RefreshSessionAsync()).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"状态采样失败；异常类型：{exception.GetType().Name}");
            await SetBusinessHostReadyAsync(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FreshnessText = "本次采样失败；显示值可能已过期。";
                PhaseMessage = "状态刷新失败。请查看本机诊断记录后重试。";
            });
        }
        finally
        {
            _realOperationGate.Release();
        }
    }

    public async Task ExportDiagnosticAsync(string directory)
    {
        LauncherDiagnosticPreviewTicket? preview = null;
        try
        {
            preview = CreateDiagnosticPreviewTicket();
            await ExportDiagnosticPreviewAsync(directory, preview).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"诊断导出准备失败；异常类型：{exception.GetType().Name}");
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "诊断预览已失效或不可用；请重新打开诊断并重试。");
        }
        finally
        {
            preview?.Dispose();
        }
    }

    internal async Task<bool> ExportDiagnosticPreviewAsync(
        string directory,
        LauncherDiagnosticPreviewTicket preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!await _realOperationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "Launcher 正在切换服务状态；请重新打开诊断预览后再导出。");
            return false;
        }

        try
        {
            var coordinator = _coordinator;
            var hostIdentity = (object?)_realHost ?? (object?)_webPortUiHost ?? this;
            if (!CanExportDiagnostic || _disposed || Volatile.Read(ref _shutdownRequested) != 0 ||
                !preview.Matches(coordinator, hostIdentity, Interlocked.Read(ref _coordinatorGeneration)))
            {
                await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "Host 状态已变化；旧诊断预览已作废，请重新打开诊断。");
                return false;
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            var target = Path.Combine(directory, $"ai-goofish-launcher-diagnostic-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
            await LauncherDiagnosticExporter.SaveNewFileAtomicallyAsync(target, preview.Contents, cancellationToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = $"脱敏诊断摘要已导出：{target}（含安全启动事件，不含原始日志、配置、凭据或业务数据）。");
            return true;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"诊断导出失败；异常类型：{exception.GetType().Name}");
            await Dispatcher.UIThread.InvokeAsync(() => PhaseMessage = "诊断导出失败；没有覆盖现有文件。请检查目标目录并重试。");
            return false;
        }
        finally
        {
            _realOperationGate.Release();
        }
    }

    public void ShowAdvancedUnavailable()
    {
        OpenAdvancedSettings();
    }

    public Task BeginBusinessPairingAsync() =>
        !_webPortSettingsLoaded || _webPortIntentUnresolved || _webPortRefreshFailed || _realHost is null ||
        _coordinator?.Snapshot.State is not LauncherState.Running || SetupRequired
            ? SetUnavailablePairingStatusAsync()
            : _businessConnection.BeginPairingAsync(BuildLauncherAuthorizationUri(_realHost.ManagementUrl));

    public Task CancelBusinessPairingAsync() => _businessConnection.CancelPairingAsync();

    public Task LogoutBusinessUserAsync() => _businessConnection.LogoutAsync();

    public Task SaveBusinessAiConfigurationAsync() => _businessConnection.SaveAsync();

    public Task RefreshBusinessAiConfigurationAsync() => _businessConnection.RefreshConfigurationAsync();

    public Task RemoveBusinessApiKeyAsync(bool confirmed) => _businessConnection.RemoveApiKeyAsync(confirmed);

    public void ClearBusinessAiDrafts() => _businessConnection.ClearDrafts();

    internal void CheckBusinessConfigurationLocally() => _businessConnection.CheckConfigurationLocally();

    internal Task RefreshBusinessAiHealthAsync() => _businessConnection.RefreshAiHealthAsync();

    internal LauncherManualAiTestPreview? CreateManualAiTestPreview(bool useDraft) =>
        _businessConnection.CreateManualTestPreview(useDraft);

    internal Task RunManualAiTestAsync(LauncherManualAiTestPreview preview, bool confirmed) =>
        _businessConnection.RunManualAiTestAsync(preview, explicitlyConfirmed: confirmed);

    internal void CancelManualAiTest() => _businessConnection.CancelManualAiTest();

    public void OpenAdvancedSettings()
    {
        if (_webPortIntentUnresolved || _webPortRefreshFailed ||
            _realHost is null || _coordinator?.Snapshot.State is not LauncherState.Running || SetupRequired)
        {
            PhaseMessage = "请先启动服务并完成首次设置，再进入 Web 系统设置。";
            return;
        }

        try
        {
            _ = OpenBusinessTarget(BuildWebSettingsUri(_realHost.ManagementUrl));
        }
        catch (ArgumentException)
        {
            PhaseMessage = "本机管理地址未通过安全校验，未打开浏览器。";
        }
    }

    internal static string BuildLauncherAuthorizationUri(string managementUrl) =>
        BuildFixedLocalWebUri(managementUrl, "/api/launcher/authorize").AbsoluteUri;

    internal static string BuildWebSettingsUri(string managementUrl) =>
        BuildFixedLocalWebUri(managementUrl, "/#settings").AbsoluteUri;

    private static Uri BuildFixedLocalWebUri(string managementUrl, string fixedPath)
    {
        if (!Uri.TryCreate(managementUrl, UriKind.Absolute, out var management) ||
            management.Scheme != Uri.UriSchemeHttp || management.Host != "127.0.0.1" ||
            management.Port is < 1024 or > 65535 || !string.IsNullOrEmpty(management.UserInfo) ||
            management.AbsolutePath != "/" || !string.IsNullOrEmpty(management.Query) ||
            !string.IsNullOrEmpty(management.Fragment))
        {
            throw new ArgumentException("本机管理地址无效。", nameof(managementUrl));
        }

        var result = new Uri(management, fixedPath);
        if (result.Host != "127.0.0.1" || result.Port != management.Port ||
            !string.IsNullOrEmpty(result.UserInfo) || !string.IsNullOrEmpty(result.Query))
        {
            throw new ArgumentException("本机管理地址无效。", nameof(managementUrl));
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Exchange(ref _shutdownRequested, 1);
        try { _restoreCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        _coordinator?.RequestStartupCancellation();
        await _realOperationGate.WaitAsync();
        try
        {
            await DisposeRestoreSessionForShutdownAsync();
            if (_businessConnection.HostReady)
            {
                await SetBusinessHostReadyAsync(false);
            }
            await _businessConnection.DisposeAsync();
            _disposed = true;
            StartupDiagnostics.Current.Recorded -= OnStartupDiagnostic;
            DetachCoordinator();
            _coordinator?.Dispose();
            _coordinator = null;
        }
        finally
        {
            _realOperationGate.Release();
        }
    }

    private void CreateSimulationCoordinator()
    {
        var database = new SimulatedComponent("demo-database", "模拟数据库组件", TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(450));
        var backend = new SimulatedComponent("demo-backend", "模拟后台组件", TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(550));
        _coordinator = new LauncherCoordinator(
            new[]
            {
                new LauncherComponentRegistration(database, StartupCancellationIsSafe: true),
                new LauncherComponentRegistration(backend, StartupCancellationIsSafe: true),
            },
            isSimulation: true,
            logCapacity: 500);
        AttachCoordinator(_coordinator);
        Components.Clear();
        foreach (var component in _coordinator.Snapshot.Components)
        {
            Components.Add(new ComponentStatusRow(component.Id, component.DisplayName, "未检测"));
        }
    }

    private async Task RunSimulationPrimaryActionAsync()
    {
        var state = _coordinator!.Snapshot.State;
        try
        {
            if (state is LauncherState.Running or LauncherState.Failed or LauncherState.StopFailed)
            {
                await _coordinator.StopAsync().ConfigureAwait(false);
            }
            else
            {
                await _coordinator.StartAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PhaseMessage = $"操作未执行：{exception.Message}";
                IsPrimaryEnabled = true;
            });
        }
    }

    // Caller owns the UI gate; the Host owns the lifecycle transaction.
    // Keep subscriptions and runtime identity intact while invalidating old user credentials.
    private async Task<T> ExecuteHostOperationAsync<T>(
        IPortableWebPortUiHost host, Func<Task<T>> action, bool portMaintenance = true)
    {
        if (_shutdownInProgress || Volatile.Read(ref _shutdownRequested) != 0 ||
            _backupInFlight || _restoreInFlight || _restoreSession is not null ||
            !ReferenceEquals(_webPortUiHost, host) || !ReferenceEquals(_coordinator, host.Coordinator))
            throw new InvalidOperationException("Host 或 UI 操作状态已变化；端口未应用。");
        var coordinator = host.Coordinator;
        await _businessConnection.PrepareForHostReplacementAsync(portMaintenance).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _webPortOperationInFlight = portMaintenance;
            _webPortRefreshFailed = false;
            // Invalidate pre-transaction previews and queued old snapshots,
            // without changing the Coordinator or detaching its subscriptions.
            Interlocked.Increment(ref _coordinatorGeneration);
            NotifyWebPortActions();
        });
        try { return await action().ConfigureAwait(false); }
        finally
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(_webPortUiHost, host) || !ReferenceEquals(_coordinator, coordinator) ||
                        !ReferenceEquals(host.Coordinator, coordinator))
                        throw new InvalidOperationException("端口操作不得改变 Coordinator 身份。");
                    var settings = host.WebPortSettings;
                    ApplyWebPortSettings(host, settings, updateManagementUrl: settings.Applying is null);
                    ApplySnapshot(coordinator.Snapshot);
                });
                var live = coordinator.Snapshot.State is LauncherState.Running &&
                    host.WebPortSettings.Applying is null && !host.SetupRequired;
                await SetBusinessHostReadyAsync(live).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _webPortRefreshFailed = true;
                    MarkWebPortSettingsUnknown(exception);
                });
                Trace.TraceError($"Web端口事务后界面刷新失败；异常类型：{exception.GetType().Name}");
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _webPortOperationInFlight = false;
                    ApplySnapshot(coordinator.Snapshot);
                    NotifyWebPortActions();
                });
            }
        }
    }

    private void AttachRealHost(RealPortableStackHost host)
    {
        _realHost = host;
        var webPortUiHost = new RealPortableWebPortUiHost(host);
        _webPortUiHost = webPortUiHost;
        _coordinator = host.Coordinator;
        AttachCoordinator(_coordinator);
        Components.Clear();
        foreach (var component in _coordinator.Snapshot.Components)
        {
            Components.Add(new ComponentStatusRow(
                component.Id,
                component.DisplayName,
                ComponentStateText(component.State, simulation: false)));
        }

        try
        {
            ApplyWebPortSettings(webPortUiHost, host.WebPortSettings, updateManagementUrl: true);
            WebPortStatus = _webPortIntentUnresolved
                ? "Web 端口切换 intent 未完成；不会自动启动或停止任何服务。"
                : "端口配置已读取。运行中暂存只影响后续确认应用。";
        }
        catch (Exception exception)
        {
            MarkWebPortSettingsUnknown(exception);
        }
    }

    internal void AttachWebPortUiHostForAcceptance(IPortableWebPortUiHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_isSimulation || _realHost is not null ||
            (_webPortUiHost is not null && !ReferenceEquals(_webPortUiHost, host)))
        {
            throw new InvalidOperationException("Web 端口 fake 仅能附加到未启动的非模拟 Smoke ViewModel。");
        }

        if (_webPortUiHost is null)
        {
            if (_coordinator is not null)
            {
                throw new InvalidOperationException("Smoke ViewModel 已绑定其他 Coordinator。");
            }

            _webPortUiHost = host;
            _coordinator = host.Coordinator;
            AttachCoordinator(_coordinator);
            Components.Clear();
            foreach (var component in _coordinator.Snapshot.Components)
            {
                Components.Add(new ComponentStatusRow(component.Id, component.DisplayName,
                    ComponentStateText(component.State, simulation: false)));
            }
        }
        else if (!ReferenceEquals(_coordinator, host.Coordinator))
        {
            throw new InvalidOperationException("Smoke fake Host 的 Coordinator 已变化；端口事务不得更换 Coordinator。");
        }

        try
        {
            ApplyWebPortSettings(host, host.WebPortSettings, updateManagementUrl: true);
            WebPortStatus = _webPortIntentUnresolved
                ? "Web 端口切换 intent 未完成；不会自动启动或停止任何服务。"
                : "端口配置已读取。运行中暂存只影响后续确认应用。";
        }
        catch (Exception exception)
        {
            MarkWebPortSettingsUnknown(exception);
        }
        ServiceState = StateText(_coordinator.Snapshot.State, simulation: false);
        NotifyWebPortActions();
    }

    private void MarkWebPortSettingsUnknown(Exception exception)
    {
        _webPortSettingsLoaded = false;
        _webPortIntentUnresolved = true;
        _effectiveWebPort = 0;
        _pendingWebPort = null;
        _webPortRevision = 0;
        WebPortInput = string.Empty;
        WebPortError = $"无法读取本实例 Web 端口配置：{exception.Message}";
        WebPortStatus = "端口状态未知；拒绝启动、暂存、应用及打开管理页。";
        ManagementUrl = "端口状态待核验";
        IsPrimaryEnabled = false;
        _ = SetBusinessHostReadyAsync(false);
        NotifyWebPortActions();
    }

    private void AttachCoordinator(LauncherCoordinator coordinator)
    {
        Interlocked.Increment(ref _coordinatorGeneration);
        coordinator.SnapshotChanged += OnSnapshotChanged;
        coordinator.LogAdded += OnLogAdded;
    }

    private void DetachCoordinator()
    {
        if (_coordinator is null)
        {
            return;
        }

        Interlocked.Increment(ref _coordinatorGeneration);
        _coordinator.SnapshotChanged -= OnSnapshotChanged;
        _coordinator.LogAdded -= OnLogAdded;
    }

    private async Task ReleaseStoppedHostAsync()
    {
        var host = _realHost;
        if (host is null)
        {
            return;
        }

        await SetBusinessHostReadyAsync(false).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(DetachCoordinator);
        await host.DisposeAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _realHost = null;
            _webPortUiHost = null;
            _coordinator = null;
            _webPortSettingsLoaded = false;
            _webPortIntentUnresolved = false;
            _effectiveWebPort = 0;
            _pendingWebPort = null;
            _webPortRevision = 0;
            SetupRequired = false;
            ManagementUrl = "尚未启动";
            PrimaryButtonText = "一键启动";
            IsPrimaryEnabled = true;
            IsStopEnabled = false;
            NotifySetupProperties();
            NotifyWebPortActions();
        });
    }

    private void OnSnapshotChanged(object? sender, LauncherSnapshot snapshot)
    {
        var generation = Interlocked.Read(ref _coordinatorGeneration);
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != Interlocked.Read(ref _coordinatorGeneration) || !ReferenceEquals(sender, _coordinator))
            {
                return;
            }
            ApplySnapshot(snapshot);
        });
    }

    internal void OnStartupDiagnostic(object? sender, StartupDiagnosticRecord entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Numbering happens before publication; concurrent process readers can publish
            // out of order. A maximum-sequence watermark would silently drop valid failures.
            if (_disposed || !_seenStartupDiagnosticSequences.Add(entry.Sequence)) return;
            _startupDiagnosticSequenceOrder.Enqueue(entry.Sequence);
            while (_startupDiagnosticSequenceOrder.Count > 512)
                _seenStartupDiagnosticSequences.Remove(_startupDiagnosticSequenceOrder.Dequeue());
            while (Logs.Count >= UiLogCapacity) Logs.RemoveAt(0);
            var activity = LauncherActivityText.FromDiagnostic(entry);
            Logs.Add(new LogRow(entry.OccurredAtUtc.ToLocalTime().ToString("HH:mm:ss"),
                activity.Level,
                entry.Component.ToString(), StartupDiagnostics.Describe(entry)));
            AddActivity(activity);
        });
    }

    private void OnLogAdded(object? sender, LauncherLogEntry entry)
    {
        var generation = Interlocked.Read(ref _coordinatorGeneration);
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != Interlocked.Read(ref _coordinatorGeneration) || !ReferenceEquals(sender, _coordinator))
            {
                return;
            }

            while (Logs.Count >= UiLogCapacity)
            {
                Logs.RemoveAt(0);
            }

            var row = new LogRow(entry.Timestamp.ToString("HH:mm:ss"), LevelText(entry.Level), entry.Component, entry.Message);
            Logs.Add(row);
            // Real lifecycle summaries come from the structured startup journal. Retain
            // coordinator warnings/errors too; simulation has no real startup journal.
            if (_isSimulation && entry.Level is not LauncherLogLevel.Debug ||
                entry.Level is LauncherLogLevel.Warning or LauncherLogLevel.Error)
                AddActivity(row);
        });
    }

    private void ApplySnapshot(LauncherSnapshot snapshot)
    {
        if (snapshot.IsSimulation || snapshot.State is not LauncherState.Running)
        {
            _ = SetBusinessHostReadyAsync(false);
        }

        ServiceState = snapshot.State is LauncherState.Failed && snapshot.Phase is "observation:component-failed"
            ? "运行期间组件状态异常"
            : StateText(snapshot.State, snapshot.IsSimulation);
        PhaseMessage = snapshot.Message;
        IsCancelEnabled = snapshot.State is LauncherState.Starting && snapshot.SafeToCancel;
        CancelHint = snapshot.State is LauncherState.Starting && !snapshot.SafeToCancel
            ? "当前步骤不可中断；取消请求会等待下一个安全点。"
            : "取消仅在标记为安全的阶段可用；停止不会强杀未知进程。";
        if (snapshot.IsSimulation)
        {
            (PrimaryButtonText, IsPrimaryEnabled) = snapshot.State switch
            {
                LauncherState.Starting => ("模拟演练启动中", false),
                LauncherState.Cancelling => ("正在回收模拟组件", false),
                LauncherState.Running => ("停止模拟演练", true),
                LauncherState.Stopping => ("正在停止模拟演练", false),
                LauncherState.BackingUp => ("正在备份", false),
                LauncherState.Failed => ("清理剩余模拟组件", true),
                LauncherState.StopFailed => ("重试清理模拟组件", true),
                _ => ("开始模拟演练", true),
            };
            IsStopEnabled = false;
        }
        else
        {
            (PrimaryButtonText, IsPrimaryEnabled, IsStopEnabled) = snapshot.State switch
            {
                LauncherState.Starting => ("正在启动", false, false),
                LauncherState.Recovering => ("正在恢复", false, false),
                LauncherState.Cancelling => ("正在安全回收", false, false),
                LauncherState.Running => ("打开管理页", true, true),
                LauncherState.Stopping => ("正在停止", false, false),
                LauncherState.BackingUp => ("正在备份", false, false),
                LauncherState.Failed when snapshot.Phase is "observation:component-failed" => ("组件状态异常", false, true),
                LauncherState.Failed => ("启动未完成", false, true),
                LauncherState.StopFailed => ("组件未完全停止", false, true),
                _ => ("一键启动", true, false),
            };
        }

        if (_shutdownInProgress || _webPortOperationInFlight)
        {
            IsPrimaryEnabled = false;
            IsStopEnabled = false;
            IsCancelEnabled = false;
        }

        if (_webPortUiHost is not null && !IsRealHostWebPortStateKnown)
        {
            IsPrimaryEnabled = false;
        }

        foreach (var step in snapshot.StartupSteps)
        {
            var row = Components.FirstOrDefault(item => item.Id == step.Id);
            if (row is null)
            {
                row = new ComponentStatusRow(step.Id, step.DisplayName, string.Empty);
                Components.Add(row);
            }
            row.Status = !step.IsQuiescent && step.State is not StartupStepState.Executing
                ? "辅助进程未确认退出"
                : step.State switch
                {
                    StartupStepState.NotRun => "待核验",
                    StartupStepState.Executing => "准备中",
                    StartupStepState.Completed => "已完成",
                    _ => "准备失败",
                };
        }

        foreach (var component in snapshot.Components)
        {
            var row = Components.FirstOrDefault(item => item.Id == component.Id);
            if (row is not null)
            {
                row.Status = ComponentStateText(component.State, snapshot.IsSimulation);
            }
        }

        if (_restoreSession is not null || _restoreInFlight)
        {
            IsPrimaryEnabled = false;
            IsStopEnabled = false;
        }

        OnPropertyChanged(nameof(CanCreateBusinessBackup));
        OnPropertyChanged(nameof(CanUpgradeSchema));
        NotifyWebPortActions();
    }

    private async Task<ILauncherBusinessUserClient> CreateBusinessUserClientAsync(CancellationToken cancellationToken)
    {
        var host = _realHost;
        if (_isSimulation || !_webPortSettingsLoaded || _webPortIntentUnresolved || _webPortRefreshFailed || host is null ||
            _coordinator?.Snapshot.State is not LauncherState.Running ||
            SetupRequired || !_businessConnection.HostReady)
        {
            throw new InvalidOperationException("Launcher 用户 client 仅可在当前真实 Host Ready 且首次设置完成时创建。");
        }

        var readiness = await host.RefreshReadinessAsync(cancellationToken).ConfigureAwait(false);
        if (readiness.State is not PythonReadinessState.Ready || readiness.SetupRequired is true ||
            _realHost != host || _coordinator?.Snapshot.State is not LauncherState.Running)
        {
            throw new InvalidOperationException("本地服务已离开 Ready 状态，请刷新运行状态后重试。");
        }

        var context = await host.CreateLauncherUserClientContextAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new PortableLauncherBusinessUserClient(context);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private bool OpenBusinessTarget(string target)
    {
        try
        {
            if (!_webPortSettingsLoaded || _webPortIntentUnresolved || _webPortRefreshFailed || _realHost is null ||
                _coordinator?.Snapshot.State is not LauncherState.Running ||
                !Uri.TryCreate(target, UriKind.Absolute, out var requested) ||
                (!string.Equals(target, BuildLauncherAuthorizationUri(_realHost.ManagementUrl), StringComparison.Ordinal) &&
                 !string.Equals(target, BuildWebSettingsUri(_realHost.ManagementUrl), StringComparison.Ordinal)) ||
                requested.Host != "127.0.0.1" || requested.Scheme != Uri.UriSchemeHttp ||
                requested.Port != new Uri(_realHost.ManagementUrl).Port || !string.IsNullOrEmpty(requested.UserInfo) ||
                !string.IsNullOrEmpty(requested.Query))
            {
                PhaseMessage = "Launcher 拒绝打开非预期的浏览器地址。";
                return false;
            }

            return TryOpen(target, requested.AbsolutePath == "/api/launcher/authorize" ? "Launcher 授权页" : "Web 系统设置");
        }
        catch (ArgumentException)
        {
            PhaseMessage = "本机管理地址未通过安全校验，未打开浏览器。";
            return false;
        }
    }

    private async Task SetUnavailablePairingStatusAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
            PhaseMessage = "请先启动本地服务并完成 Web 首次管理员设置。Launcher 不会代替用户登录。");
    }

    private async Task SetBusinessHostReadyAsync(bool ready)
    {
        await RunBusinessOnUiAsync(() => _businessConnection.SetHostReadyAsync(ready)).ConfigureAwait(false);
    }

    private static async Task RunBusinessOnUiAsync(Func<Task> operationFactory)
    {
        Task? operation = null;
        await Dispatcher.UIThread.InvokeAsync(() => operation = operationFactory());
        await (operation ?? throw new InvalidOperationException("Launcher UI 操作未排入 Dispatcher。"))
            .ConfigureAwait(false);
    }

    private void TryOpenExistingFolder(string path, string label)
    {
        if (!Directory.Exists(path))
        {
            PhaseMessage = $"{label}尚不存在；首次启动成功准备数据目录后再打开。";
            return;
        }

        TryOpen(path, label);
    }

    private bool TryOpen(string target, string label)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            if (process is null)
            {
                PhaseMessage = $"系统没有接受打开{label}的请求。";
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            Trace.TraceError($"打开{label}失败；异常类型：{exception.GetType().Name}");
            PhaseMessage = $"无法打开{label}，请检查系统默认浏览器或文件关联后重试。";
            return false;
        }
    }

    private void NotifySetupProperties()
    {
        OnPropertyChanged(nameof(DisplayPrimaryButtonText));
        OnPropertyChanged(nameof(CanOpenSetup));
        OnPropertyChanged(nameof(SetupHelp));
    }

    private static string StateText(LauncherState state, bool simulation)
    {
        var prefix = simulation ? "模拟" : string.Empty;
        return state switch
        {
            LauncherState.NotStarted => "未开始",
            LauncherState.Starting => prefix + "启动中",
            LauncherState.Recovering => prefix + "恢复核验中",
            LauncherState.Cancelling => prefix + "取消中",
            LauncherState.Running => prefix + "组件运行中",
            LauncherState.Failed => prefix + "启动失败",
            LauncherState.Stopping => prefix + "停止中",
            LauncherState.BackingUp => prefix + "备份中",
            LauncherState.Stopped => prefix + "组件已停止",
            LauncherState.StopFailed => prefix + "组件未完全停止",
            _ => "未知",
        };
    }

    private static string ComponentStateText(ComponentRuntimeState state, bool simulation)
    {
        var suffix = simulation ? "（模拟）" : string.Empty;
        return state switch
        {
            ComponentRuntimeState.Unknown => "未知",
            ComponentRuntimeState.Stopped => "已停止" + suffix,
            ComponentRuntimeState.Starting => "启动中" + suffix,
            ComponentRuntimeState.Running => "运行中" + suffix,
            ComponentRuntimeState.Stopping => "停止中" + suffix,
            ComponentRuntimeState.Failed => "失败" + suffix,
            _ => "未知",
        };
    }

    private static string LevelText(LauncherLogLevel level) => level switch
    {
        LauncherLogLevel.Debug => "调试",
        LauncherLogLevel.Information => "信息",
        LauncherLogLevel.Warning => "警告",
        LauncherLogLevel.Error => "错误",
        _ => "未知",
    };

    private static string Freshness(DateTimeOffset observedAt)
    {
        var age = DateTimeOffset.UtcNow - observedAt;
        return age <= TimeSpan.FromSeconds(30) ? "新鲜" : $"已过期 {Math.Max(1, (int)age.TotalSeconds)} 秒";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.0} {units[index]}";
    }

    private void ShowManualStartReady(string message)
    {
        var bundle = _bundle ?? throw new InvalidOperationException("发行包尚未加载。 ");
        ServiceState = StateText(LauncherState.Stopped, simulation: false);
        ServiceDetail = $"发行包 {bundle.ReleaseId} · 应用 {bundle.AppVersion} · schema {bundle.SchemaMinimum}–{bundle.SchemaMaximum}";
        PhaseMessage = message;
        PrimaryButtonText = "一键启动";
        IsPrimaryEnabled = _webPortUiHost is null || IsRealHostWebPortStateKnown;
        if (_webPortUiHost is not null && !IsRealHostWebPortStateKnown)
        {
            ManagementUrl = "端口状态待核验";
            WebPortStatus = "端口状态未知；服务已确认停止，启动和管理页入口已禁用。";
            PhaseMessage = "已确认实例组件停止，但 Web 端口配置未能核验；请重新打开 Launcher 检查。";
        }
        IsStopEnabled = false;
        CancelHint = "预览版固定使用 PG 55432 / Web 58000；端口冲突会安全拒绝启动，不接管其他监听者。";
        foreach (var component in Components)
        {
            component.Status = "已停止";
        }
    }

    private void SaveLauncherPreference(LauncherPreferences updated, ref bool field, bool value, string propertyName)
    {
        if (field == value || !CanEditLauncherPreferences || _launcherPreferencesStore is null)
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        if (_launcherPreferencesStore.TrySave(updated))
        {
            _launcherPreferences = updated;
            LauncherPreferencesStatus = "Launcher 偏好已保存。";
            return;
        }

        field = !value;
        OnPropertyChanged(propertyName);
        LauncherPreferencesStatus = "本地偏好未能保存；已恢复原设置。请检查数据目录权限和磁盘空间。";
    }

    private void TryOpenManagementWhenReady(PythonReadinessResult readiness) =>
        TryOpenManagementWhenReady(readiness.State is PythonReadinessState.Ready, readiness.SetupRequired is true);

    private void TryOpenManagementWhenReady(bool ready, bool setupRequired)
    {
        if (_managementPageOpenAttemptedForReadyGeneration || _isSimulation || !IsRealHostWebPortStateKnown || _realHost is null ||
            !ready)
        {
            return;
        }

        _managementPageOpenAttemptedForReadyGeneration = true;
        if (_openManagementPageOnReady)
        {
            _ = OpenManagementPageCoreAsync();
        }
    }

    internal void TryOpenManagementWhenReadyForAcceptance() =>
        TryOpenManagementWhenReady(new PythonReadinessResult(PythonReadinessState.Ready, null, 1, false));

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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ComponentStatusRow : INotifyPropertyChanged
{
    private string _status;

    public ComponentStatusRow(string id, string displayName, string status)
    {
        Id = id;
        DisplayName = displayName;
        _status = status;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string DisplayName { get; }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }
}

public sealed record LogRow(string Time, string Level, string Component, string Message)
{
    public string LevelBrush => Level switch
    {
        "错误" => "#AC3636",
        "警告" => "#956600",
        _ => "#705799",
    };
}
