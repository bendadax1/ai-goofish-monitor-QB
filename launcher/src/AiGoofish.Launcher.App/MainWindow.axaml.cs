using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using AiGoofish.Launcher.Platform.Windows;

namespace AiGoofish.Launcher.App;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly bool _skipInitialization;
    private bool _allowClose;
    private bool _closeInProgress;
    private bool _safeExitRequested;
    private CancellationTokenSource? _samplingCancellation;

    internal Func<Task<bool>>? WebPortApplyConfirmationForAcceptance { get; set; }
    internal Func<Window, TextBox, Button, Button, Task>? DiagnosticPreviewShownForAcceptance { get; set; }
    internal Func<Task<string?>>? DiagnosticFolderSelectionForAcceptance { get; set; }
    internal Action<LauncherDiagnosticPreviewTicket>? DiagnosticPreviewTicketDisposedForAcceptance { get; set; }

    public MainWindow()
        : this(isSimulation: false)
    {
    }

    public MainWindow(bool isSimulation, string? bundleRoot = null, PortableBundleDescriptor? verifiedBundle = null)
        : this(new MainWindowViewModel(isSimulation, bundleRoot, verifiedBundle), skipInitialization: false)
    {
    }

    internal MainWindow(MainWindowViewModel viewModel, bool skipInitialization)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _skipInitialization = skipInitialization;
        DataContext = _viewModel;
        Closing += OnClosing;
        Closed += OnClosed;
        Opened += OnOpened;
    }

    private void OnNavigationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string pageName } &&
            Enum.TryParse<LauncherPage>(pageName, out var page))
        {
            _viewModel.SelectPage(page);
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        var bypassCloseChoice = _safeExitRequested;
        _safeExitRequested = false;
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_closeInProgress)
        {
            return;
        }

        _closeInProgress = true;
        try
        {
            var rememberChoice = false;
            if (ShouldAskCloseChoice(_viewModel.RequiresCloseChoice, bypassCloseChoice))
            {
                var choice = _viewModel.CloseChoicePreference;
                if (choice is WindowCloseChoice.Ask)
                {
                    var app = Application.Current as App;
                    var dialog = new CloseChoiceWindow(app?.IsTrayAvailable is true);
                    var result = await dialog.ShowDialog<CloseChoiceResult>(this);
                    choice = result.Choice;
                    rememberChoice = result.Remember;
                }

                if (choice is WindowCloseChoice.Ask)
                {
                    return;
                }

                if (choice is WindowCloseChoice.Background)
                {
                    var app = Application.Current as App;
                    if (app?.TryHideMainWindowToTray() is true)
                    {
                        if (rememberChoice)
                        {
                            _viewModel.TrySaveCloseChoicePreference(WindowCloseChoice.Background);
                        }
                        return;
                    }

                    var reason = app?.TrayUnavailableReason ?? "系统托盘未初始化。";
                    var minimizeToTaskbar = await new TrayUnavailableWindow(reason).ShowDialog<bool>(this);
                    if (!minimizeToTaskbar)
                    {
                        return;
                    }

                    _viewModel.PrepareBackgroundWindow();
                    WindowState = WindowState.Minimized;
                    if (rememberChoice)
                    {
                        _viewModel.TrySaveCloseChoicePreference(WindowCloseChoice.Background);
                    }
                    return;
                }

                if (rememberChoice && choice is WindowCloseChoice.Exit)
                {
                    _viewModel.TrySaveCloseChoicePreference(WindowCloseChoice.Exit);
                }
            }

            using var shutdownCancellation = new CancellationTokenSource();
            var shutdownTask = _viewModel.ShutdownAsync(shutdownCancellation.Token);
            var firstWait = await Task.WhenAny(shutdownTask, Task.Delay(TimeSpan.FromSeconds(60)));
            if (firstWait != shutdownTask)
            {
                var keepWaiting = await new ShutdownWaitWindow().ShowDialog<bool>(this);
                if (!keepWaiting)
                {
                    shutdownCancellation.Cancel();
                    await shutdownTask;
                    if (bypassCloseChoice)
                    {
                        (Application.Current as App)?.ShowMainWindow();
                    }
                    return;
                }
            }

            if (await shutdownTask)
            {
                _allowClose = true;
                Close();
            }
            else if (bypassCloseChoice)
            {
                (Application.Current as App)?.ShowMainWindow();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"关闭前收尾失败：{exception}");
            if (bypassCloseChoice)
            {
                (Application.Current as App)?.ShowMainWindow();
            }
        }
        finally
        {
            _closeInProgress = false;
        }
    }

    internal void RequestSafeExit()
    {
        if (_allowClose || _closeInProgress)
        {
            return;
        }

        _safeExitRequested = true;
        Close();
    }

    internal static bool ShouldAskCloseChoice(bool requiresChoice, bool safeExitRequested) =>
        requiresChoice && !safeExitRequested;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.RunPrimaryActionAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"主操作失败：{exception}");
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.RequestCancellation();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"请求取消失败：{exception}");
        }
    }

    private async void OnStopClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.RunStopActionAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"停止操作失败：{exception}");
        }
    }

    private void OnOpenManagementClick(object? sender, RoutedEventArgs e) => _viewModel.OpenManagementPage();

    private void OnOpenDataClick(object? sender, RoutedEventArgs e) => _viewModel.OpenDataFolder();

    private void OnOpenLogsClick(object? sender, RoutedEventArgs e) => _viewModel.OpenLogFolder();

    private async void OnDiagnosticClick(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanExportDiagnostic)
        {
            return;
        }

        LauncherDiagnosticPreviewTicket? previewTicket = null;
        try
        {
            previewTicket = _viewModel.CreateDiagnosticPreviewTicket();
            var cancelPreviewButton = new Button { Name = "DiagnosticPreviewCancelButton", Content = "取消", MinWidth = 90 };
            var exportPreviewButton = new Button { Name = "DiagnosticPreviewExportButton", Content = "选择位置并导出", MinWidth = 130 };
            var previewTextBox = new TextBox
            {
                Name = "DiagnosticPreviewTextBox",
                Text = previewTicket.PreviewText,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
                FontFamily = "Consolas",
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                Margin = new Avalonia.Thickness(0, 10),
            };
            ScrollViewer.SetVerticalScrollBarVisibility(previewTextBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(previewTextBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            Grid.SetRow(previewTextBox, 1);
            var previewButtons = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Spacing = 8,
                Children =
                {
                    cancelPreviewButton,
                    exportPreviewButton,
                },
            };
            Grid.SetRow(previewButtons, 2);
            var preview = new Window
            {
                Title = "诊断摘要预览",
                Width = 720,
                Height = 560,
                MinWidth = 520,
                MinHeight = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new Grid
                {
                    Margin = new Avalonia.Thickness(22),
                    RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "以下 JSON 最多 16 KiB。仅含版本、粗粒度运行状态、组件状态、采样时间和固定错误码事件；不含自由文本日志、异常内容、配置、文件路径、账号、凭据、请求内容或业务数据。文件只保存在你选择的位置，不会上传。",
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        },
                        previewTextBox,
                        previewButtons,
                    },
                },
            };
            cancelPreviewButton.Click += (_, _) => preview.Close(false);
            exportPreviewButton.Click += (_, _) => preview.Close(true);
            var previewResult = preview.ShowDialog<bool>(this);
            if (DiagnosticPreviewShownForAcceptance is { } previewShown)
            {
                await previewShown(preview, previewTextBox, cancelPreviewButton, exportPreviewButton);
            }

            var accepted = await previewResult;
            if (!accepted)
            {
                return;
            }
            string? selectedDirectory;
            if (DiagnosticFolderSelectionForAcceptance is { } folderSelection)
            {
                selectedDirectory = await folderSelection();
            }
            else
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "选择诊断摘要保存位置",
                    AllowMultiple = false,
                });
                var folderUri = folders.FirstOrDefault()?.Path;
                selectedDirectory = folderUri is { IsFile: true } ? folderUri.LocalPath : null;
            }

            if (!string.IsNullOrWhiteSpace(selectedDirectory))
            {
                await _viewModel.ExportDiagnosticPreviewAsync(selectedDirectory, previewTicket);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"诊断导出 UI 失败；异常类型：{exception.GetType().Name}");
            _viewModel.ShowDiagnosticUiFailure();
        }
        finally
        {
            if (previewTicket is { } ticket)
            {
                ticket.Dispose();
                DiagnosticPreviewTicketDisposedForAcceptance?.Invoke(ticket);
            }
        }
    }

    private async void OnAdvancedClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_viewModel.BusinessConnection.HasUnsavedChanges)
            {
                var discard = await ConfirmDiscardBusinessDraftsAsync();
                if (!discard)
                {
                    return;
                }
                _viewModel.ClearBusinessAiDrafts();
            }

            _viewModel.ShowAdvancedUnavailable();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"打开 Web 系统设置失败；异常类型：{exception.GetType().Name}");
        }
    }

    private async Task<bool> ConfirmDiscardBusinessDraftsAsync()
    {
        var dialog = new Window
        {
            Title = "放弃未保存的 AI 配置？",
            Width = 500,
            Height = 240,
            MinWidth = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(22),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "你有尚未保存的 AI 连接修改。继续打开 Web 设置会清除 Launcher 中的模型、地址和密钥草稿。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "返回编辑", MinWidth = 100, IsCancel = true },
                            new Button { Content = "放弃并打开 Web", MinWidth = 140, IsDefault = true },
                        },
                    },
                },
            },
        };
        var buttons = ((StackPanel)((StackPanel)dialog.Content!).Children[1]).Children.OfType<Button>().ToArray();
        buttons[0].Click += (_, _) => dialog.Close(false);
        buttons[1].Click += (_, _) => dialog.Close(true);
        return await dialog.ShowDialog<bool>(this);
    }

    private async void OnBusinessPairingClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.BeginBusinessPairingAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"Launcher 账号配对 UI 失败；异常类型：{exception.GetType().Name}");
        }
    }

    private async void OnBusinessPairingCancelClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.CancelBusinessPairingAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"取消 Launcher 配对失败；异常类型：{exception.GetType().Name}");
        }
    }

    private void OnBusinessValidationSummaryClick(object? sender, RoutedEventArgs e)
    {
        var target = _viewModel.BusinessConnection.ValidationFocusTarget switch
        {
            "baseUrl" => this.FindControl<TextBox>("BusinessBaseUrlTextBox"),
            "modelName" => this.FindControl<TextBox>("BusinessModelNameTextBox"),
            "apiKey" => this.FindControl<TextBox>("BusinessApiKeyTextBox"),
            _ => null,
        };
        if (target?.Focus() is not true)
        {
            this.FindControl<Button>("BusinessValidationSummaryButton")?.Focus();
        }
    }

    private async void OnBusinessLogoutClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.LogoutBusinessUserAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"退出 Launcher 用户会话失败；异常类型：{exception.GetType().Name}");
        }
    }

    private async void OnBusinessAiSaveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_viewModel.BusinessConnection.RequiresBaseUrlTargetConfirmation &&
                !await ConfirmBaseUrlTargetAsync())
            {
                return;
            }
            await _viewModel.SaveBusinessAiConfigurationAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"保存 Launcher AI 配置失败；异常类型：{exception.GetType().Name}");
        }
    }

    private async void OnWebPortStageClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.StageWebPortAsync();
            if (_viewModel.HasWebPortError)
            {
                this.FindControl<TextBox>("WebPortTextBox")?.Focus();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"暂存 Web 端口失败；异常类型：{exception.GetType().Name}");
        }
    }

    private async void OnWebPortApplyClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!_viewModel.CanApplyWebPort || !await ConfirmWebPortApplyAsync())
            {
                return;
            }

            await _viewModel.ApplyPendingWebPortAsync();
            if (_viewModel.HasWebPortError)
            {
                this.FindControl<TextBox>("WebPortTextBox")?.Focus();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"应用 Web 端口 UI 失败；异常类型：{exception.GetType().Name}");
        }
    }

    private async Task<bool> ConfirmWebPortApplyAsync()
    {
        if (WebPortApplyConfirmationForAcceptance is { } confirmationForAcceptance)
        {
            return await confirmationForAcceptance().ConfigureAwait(false);
        }

        var dialog = new Window
        {
            Title = "确认应用 Web 端口",
            Width = 600,
            Height = 360,
            MinWidth = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(22),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"当前生效地址：http://127.0.0.1:{_viewModel.EffectiveWebPortText}/\n候选地址：http://127.0.0.1:{_viewModel.PendingWebPortText}/",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    },
                    new TextBlock
                    {
                        Text = "只有全部组件确认停止后才能应用。Launcher 会用正常 Host 生命周期启动候选端口，并在 Ready 与进程身份验证通过后提交；若端口占用或启动失败，会尝试恢复旧地址。PostgreSQL 端口和活动实例指针不变。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = "应用期间当前 Launcher 账号、AI 健康缓存及未保存的 AI 草稿会清除；恢复后需重新配对。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Foreground = Avalonia.Media.Brushes.DarkOrange,
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "取消", MinWidth = 100, IsCancel = true },
                            new Button { Content = "确认应用", MinWidth = 120, IsDefault = true },
                        },
                    },
                },
            },
        };
        var buttons = ((StackPanel)((StackPanel)dialog.Content!).Children[^1]).Children.OfType<Button>().ToArray();
        buttons[0].Click += (_, _) => dialog.Close(false);
        buttons[1].Click += (_, _) => dialog.Close(true);
        return await dialog.ShowDialog<bool>(this);
    }

    private async void OnBusinessAiRefreshClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.RefreshBusinessAiConfigurationAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"重新读取 Launcher AI 配置失败；异常类型：{exception.GetType().Name}");
        }
    }

    private async void OnBusinessAiHealthRefreshClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.RefreshBusinessAiHealthAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"读取 AI 健康缓存失败；异常类型：{exception.GetType().Name}");
        }
    }

    private void OnBusinessAiCheckClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.CheckBusinessConfigurationLocally();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"本地检查 AI 配置失败；异常类型：{exception.GetType().Name}");
        }
    }

    private async void OnBusinessManualAiTestClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!await ChooseManualAiTestConfigurationAsync())
            {
                return;
            }

            var useDraft = _manualAiTestUseDraft;
            var preview = _viewModel.CreateManualAiTestPreview(useDraft);
            if (preview is null)
            {
                return;
            }

            if (!await ConfirmManualAiTestAsync(preview))
            {
                return;
            }

            await _viewModel.RunManualAiTestAsync(preview, confirmed: true);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"手动 AI 测试 UI 失败；异常类型：{exception.GetType().Name}");
        }
    }

    private bool _manualAiTestUseDraft;

    private async Task<bool> ChooseManualAiTestConfigurationAsync()
    {
        var dialog = new Window
        {
            Title = "选择 AI 测试配置",
            Width = 520,
            Height = 270,
            MinWidth = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(22),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "选择本次请求使用的配置。草稿仅使用当前窗口中尚未保存的修改；此选择不会保存配置。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "取消", MinWidth = 90 },
                            new Button { Content = "使用已保存配置…", MinWidth = 140 },
                            new Button { Content = "使用当前草稿…", MinWidth = 130 },
                        },
                    },
                },
            },
        };
        var actions = (StackPanel)((StackPanel)dialog.Content!).Children[1];
        var buttons = actions.Children.OfType<Button>().ToArray();
        buttons[0].Click += (_, _) => dialog.Close(false);
        buttons[1].Click += (_, _) =>
        {
            _manualAiTestUseDraft = false;
            dialog.Close(true);
        };
        buttons[2].Click += (_, _) =>
        {
            _manualAiTestUseDraft = true;
            dialog.Close(true);
        };
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task<bool> ConfirmManualAiTestAsync(LauncherManualAiTestPreview preview)
    {
        var dialog = new Window
        {
            Title = "确认发送一次 AI 测试请求",
            Width = 600,
            Height = 380,
            MinWidth = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(22),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"本次将向 {preview.TargetOrigin} 发送一次固定测试文本请求。\n模型：{preview.ModelName}\n配置来源：{(preview.UseDraft ? "当前未保存草稿" : "已保存配置")}\n请求次数：1 次；最大输出：10 tokens。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = "请求会发送到上述第三方 AI 服务。服务商可能记录请求并按其规则收费；取消等待或超时后结果可能未知，系统不会自动重试。此测试不会保存配置或更新健康缓存。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Foreground = Avalonia.Media.Brushes.DarkOrange,
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "不发送", MinWidth = 100 },
                            new Button { Content = "确认并发送一次", MinWidth = 150, IsDefault = true },
                        },
                    },
                },
            },
        };
        var actions = (StackPanel)((StackPanel)dialog.Content!).Children[2];
        var buttons = actions.Children.OfType<Button>().ToArray();
        buttons[0].Click += (_, _) => dialog.Close(false);
        buttons[1].Click += (_, _) => dialog.Close(true);
        return await dialog.ShowDialog<bool>(this);
    }

    private void OnBusinessManualAiTestCancelClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.CancelManualAiTest();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"取消手动 AI 测试等待失败；异常类型：{exception.GetType().Name}");
        }
    }

    private async void OnBusinessApiKeyRemoveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_viewModel.BusinessConnection.RequiresBaseUrlTargetConfirmation &&
                !await ConfirmBaseUrlTargetAsync())
            {
                return;
            }

            var dialog = new Window
            {
                Title = "移除 API Key",
                Width = 480,
                Height = 230,
                MinWidth = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(22),
                    Spacing = 14,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "确认移除当前用户默认 AI 配置中的 API Key？此操作只保存配置，不会发送 AI 请求。",
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            Spacing = 8,
                            Children =
                            {
                                new Button { Content = "取消", MinWidth = 90, IsCancel = true },
                                new Button { Content = "确认移除", MinWidth = 100, IsDefault = true },
                            },
                        },
                    },
                },
            };
            var buttons = ((StackPanel)((StackPanel)dialog.Content!).Children[1]).Children.OfType<Button>().ToArray();
            buttons[0].Click += (_, _) => dialog.Close(false);
            buttons[1].Click += (_, _) => dialog.Close(true);
            var confirmed = await dialog.ShowDialog<bool>(this);
            if (!confirmed)
            {
                return;
            }

            await _viewModel.RemoveBusinessApiKeyAsync(confirmed: true);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"移除 Launcher API Key 失败；异常类型：{exception.GetType().Name}");
        }
    }

    private async Task<bool> ConfirmBaseUrlTargetAsync()
    {
        var target = _viewModel.BusinessConnection.BaseUrlTargetSummary;
        var dialog = new Window
        {
            Title = "确认 AI 服务目标变更",
            Width = 540,
            Height = 265,
            MinWidth = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(22),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"你正在把默认 AI 服务目标更改为：{target}。请确认该地址可信。已有 API Key 不会自动转发到新目标；如果当前配置已设置 Key，后端会要求重新绑定或明确移除。Launcher 不会发送测试请求。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "返回检查", MinWidth = 100, IsCancel = true },
                            new Button { Content = "确认目标", MinWidth = 100, IsDefault = true },
                        },
                    },
                },
            },
        };
        var buttons = ((StackPanel)((StackPanel)dialog.Content!).Children[1]).Children.OfType<Button>().ToArray();
        buttons[0].Click += (_, _) => dialog.Close(false);
        buttons[1].Click += (_, _) => dialog.Close(true);
        return await dialog.ShowDialog<bool>(this);
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.RefreshStatusAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"刷新服务状态失败：{exception}");
        }
    }

    private async void OnBusinessBackupClick(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanCreateBusinessBackup)
        {
            return;
        }

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择业务备份文件夹（必须位于数据目录之外）",
                AllowMultiple = false,
            });
            var folderUri = folders.FirstOrDefault()?.Path;
            if (folderUri is null || !folderUri.IsFile)
            {
                return;
            }

            var folderPath = folderUri.LocalPath;
            var dialog = new BackupPassphraseDialog();
            var passphrase = await dialog.ShowDialog<string?>(this);
            if (passphrase is null)
            {
                return;
            }

            try
            {
                await _viewModel.CreateBusinessBackupAsync(folderPath, passphrase);
            }
            finally
            {
                passphrase = string.Empty;
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"业务备份 UI 操作失败；异常类型：{exception.GetType().Name}");
            _viewModel.ReportBackupUiFailure();
        }
    }

    private async void OnBusinessRestoreClick(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanRestoreBusinessBackup) return;

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择可信的加密业务备份",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("闲鱼监控业务备份") { Patterns = ["*.gfbk"] },
                ],
            });
            var selected = files.FirstOrDefault()?.Path;
            if (selected is null || !selected.IsFile) return;

            var credentials = await ShowRestoreCredentialsDialogAsync();
            if (credentials is null) return;
            var passphrase = credentials.Passphrase;
            try
            {
                await _viewModel.RestoreBusinessBackupAsync(
                    selected.LocalPath,
                    passphrase,
                    credentials.TrustConfirmation);
            }
            finally
            {
                passphrase = string.Empty;
                credentials = null;
            }

            if (!_viewModel.IsRestorePreviewReady) return;
            var confirmation = await ShowRestorePreviewDialogAsync(_viewModel.RestorePreviewText);
            if (confirmation is null)
            {
                await _viewModel.CancelBusinessRestoreAsync();
                return;
            }

            await _viewModel.ConfirmBusinessRestoreAsync(confirmation);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"业务恢复 UI 操作失败；异常类型：{exception.GetType().Name}");
            _viewModel.ReportRestoreUiFailure();
        }
    }

    private async void OnCancelRestoreClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.CancelBusinessRestoreAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"取消业务恢复失败；异常类型：{exception.GetType().Name}");
        }
    }

    private async Task<RestoreCredentials?> ShowRestoreCredentialsDialogAsync()
    {
        var passphrase = RestoreDialogControls.CreatePassphraseInput();
        var trust = new TextBox { PlaceholderText = PortableRestoreSession.RequiredBackupTrustText, MinWidth = 440 };
        var start = new Button { Content = "开始隔离恢复", MinWidth = 130, IsEnabled = false };
        var dialog = new Window
        {
            Title = "验证并隔离恢复",
            Width = 620,
            Height = 340,
            MinWidth = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(22),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "备份中的数据库 SQL 会在全新隔离目标执行。只恢复你信任来源的备份；旧活动数据不会被覆盖。恢复过程不会启动 Web 或业务任务。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new TextBlock { Text = "备份口令（不会显示或写入日志）" },
                    passphrase,
                    new TextBlock { Text = $"输入完整文字以确认信任备份来源：{PortableRestoreSession.RequiredBackupTrustText}", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    trust,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "取消", MinWidth = 90, IsCancel = true },
                            start,
                        },
                    },
                },
            },
        };
        var panel = (StackPanel)dialog.Content!;
        var actions = (StackPanel)panel.Children[^1];
        var cancel = (Button)actions.Children[0];
        RestoreCredentials? result = null;
        void UpdateEnabled() => start.IsEnabled =
            MainWindowViewModel.CanUseBackupPassphrase(passphrase.Text ?? string.Empty) &&
            RestoreDialogControls.IsExactConfirmation(trust.Text, PortableRestoreSession.RequiredBackupTrustText);
        passphrase.TextChanged += (_, _) => UpdateEnabled();
        trust.TextChanged += (_, _) => UpdateEnabled();
        cancel.Click += (_, _) => dialog.Close(null);
        start.Click += (_, _) =>
        {
            if (!start.IsEnabled) return;
            result = new RestoreCredentials(passphrase.Text ?? string.Empty, trust.Text ?? string.Empty);
            passphrase.Text = string.Empty;
            trust.Text = string.Empty;
            dialog.Close(result);
        };
        var accepted = await dialog.ShowDialog<RestoreCredentials?>(this);
        return accepted ?? result;
    }

    private async Task<string?> ShowRestorePreviewDialogAsync(string previewText)
    {
        var confirmation = new TextBox { PlaceholderText = PortableRestoreSession.RequiredConfirmationText, MinWidth = 520 };
        var activate = new Button { Content = "确认切换活动数据", MinWidth = 160, IsEnabled = false };
        var dialog = new Window
        {
            Title = "恢复差异与切换确认",
            Width = 720,
            Height = 680,
            MinWidth = 620,
            MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "以下是隔离恢复和维护审计产生的差异预览。备份之后新增的数据不会包含在恢复副本；旧实例保留，但恢复副本中的登录会话已撤销。切换仅改变活动实例选择，不会自动启动服务。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new TextBox
                    {
                        Text = previewText,
                        IsReadOnly = true,
                        AcceptsReturn = true,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        FontFamily = "Consolas",
                        MinHeight = 390,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                    },
                    new TextBlock { Text = $"如要切换，请输入完整文字：{PortableRestoreSession.RequiredConfirmationText}", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    confirmation,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "取消并保留旧数据", MinWidth = 150, IsCancel = true },
                            activate,
                        },
                    },
                },
            },
        };
        var panel = (StackPanel)dialog.Content!;
        var actions = (StackPanel)panel.Children[^1];
        var cancel = (Button)actions.Children[0];
        string? result = null;
        void UpdateEnabled() => activate.IsEnabled =
            RestoreDialogControls.IsExactConfirmation(confirmation.Text, PortableRestoreSession.RequiredConfirmationText);
        confirmation.TextChanged += (_, _) => UpdateEnabled();
        cancel.Click += (_, _) => dialog.Close(null);
        activate.Click += (_, _) =>
        {
            if (!activate.IsEnabled) return;
            result = confirmation.Text;
            confirmation.Text = string.Empty;
            dialog.Close(result);
        };
        return await dialog.ShowDialog<string?>(this) ?? result;
    }

    private async void OnCopySetupTokenClick(object? sender, RoutedEventArgs e)
    {
        var token = _viewModel.GetSetupTokenForExplicitCopy();
        if (token is null || Clipboard is null)
        {
            return;
        }

        try
        {
            await Clipboard.SetTextAsync(token);
            _viewModel.MarkSetupTokenCopied();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"复制一次性设置口令失败：{exception}");
        }
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_skipInitialization)
        {
            return;
        }

        try
        {
            await _viewModel.InitializeAsync();
            await _viewModel.TryAutoStartVerifiedStoppedExistingAsync();
            await _viewModel.RefreshStatusAsync();
            _samplingCancellation = new CancellationTokenSource();
            _ = SampleStatusLoopAsync(_samplingCancellation.Token);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"Launcher 预检失败：{exception}");
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _samplingCancellation?.Cancel();
        _samplingCancellation?.Dispose();
        _samplingCancellation = null;
        Closing -= OnClosing;
        Closed -= OnClosed;
        Opened -= OnOpened;
        await _viewModel.DisposeAsync();
    }

    private sealed record RestoreCredentials(string Passphrase, string TrustConfirmation);

    private async Task SampleStatusLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                await _viewModel.RefreshStatusAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"周期状态采样失败；异常类型：{exception.GetType().Name}");
        }
    }
}

public static class RestoreDialogControls
{
    public static TextBox CreatePassphraseInput() => new()
    {
        PasswordChar = '●',
        PlaceholderText = "输入备份口令",
        MinWidth = 440,
    };

    public static bool IsExactConfirmation(string? entered, string required) =>
        string.Equals(entered, required, StringComparison.Ordinal);
}
