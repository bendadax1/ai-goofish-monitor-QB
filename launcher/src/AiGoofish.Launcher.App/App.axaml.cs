using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AiGoofish.Launcher.App;

public sealed partial class App : Application
{
    private MainWindow? _mainWindow;
    private WindowsTrayService? _trayService;
    private string? _trayFailureReason;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            _mainWindow = new MainWindow(Program.SimulationRequested, Program.DevelopmentRequest?.SessionRoot);
            if (Program.DevelopmentRequest is not null)
                _mainWindow.Title = "开发隔离模式 · 非发行验证 · 闲鱼监控 Launcher";
            desktop.MainWindow = _mainWindow;
            _trayService = new WindowsTrayService(this, RestoreMainWindow, RequestSafeExit);
            if (!_trayService.TryInstall())
            {
                System.Diagnostics.Trace.TraceWarning($"系统托盘不可用；异常类型/原因：{_trayService.FailureReason}");
            }

            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal bool IsTrayAvailable => _trayService?.IsInstalled is true;

    internal string TrayUnavailableReason => _trayFailureReason ?? _trayService?.FailureReason ?? "系统托盘未初始化。";

    internal void ShowMainWindow() => RestoreMainWindow();

    internal bool TryHideMainWindowToTray()
    {
        if (!IsTrayAvailable || _mainWindow is null)
        {
            return false;
        }

        try
        {
            _mainWindow.Hide();
            _trayFailureReason = null;
            return true;
        }
        catch (Exception exception)
        {
            _trayFailureReason = $"隐藏到系统托盘失败（{exception.GetType().Name}）。";
            System.Diagnostics.Trace.TraceError($"隐藏到系统托盘失败；异常类型：{exception.GetType().Name}");
            return false;
        }
    }

    private void RestoreMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState is WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private Task RequestSafeExit()
    {
        if (_mainWindow is not null)
        {
            RestoreMainWindow();
            _mainWindow.RequestSafeExit();
        }
        return Task.CompletedTask;
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        try
        {
            _trayService?.Dispose();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"应用退出时移除系统托盘失败；异常类型：{exception.GetType().Name}");
        }
        finally
        {
            _trayService = null;
            _mainWindow = null;
            _trayFailureReason = null;
        }
    }
}
