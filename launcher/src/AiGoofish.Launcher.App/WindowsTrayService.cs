using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Windows.Input;

namespace AiGoofish.Launcher.App;

/// <summary>
/// Owns the Avalonia tray icon and keeps the native shell integration out of the
/// window's shutdown implementation. The safe-exit callback must run the normal
/// Launcher shutdown sequence; this service never shuts down or kills processes.
/// </summary>
internal sealed class WindowsTrayService : IDisposable
{
    private readonly Application _application;
    private readonly Action _openWindow;
    private readonly Func<Task> _safeExit;
    private TrayIcon? _icon;
    private bool _disposed;

    internal WindowsTrayService(Application application, Action openWindow, Func<Task> safeExit)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _openWindow = openWindow ?? throw new ArgumentNullException(nameof(openWindow));
        _safeExit = safeExit ?? throw new ArgumentNullException(nameof(safeExit));
    }

    internal bool IsInstalled => _icon is not null;

    internal string? FailureReason { get; private set; }

    internal bool TryInstall()
    {
        try
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://AiGoofish.Launcher.App/Assets/launcher-tray.png"));
            return TryInstall(iconStream);
        }
        catch (Exception exception)
        {
            FailureReason = $"托盘图标资源不可用（{exception.GetType().Name}）。";
            System.Diagnostics.Trace.TraceError($"系统托盘图标资源加载失败；异常类型：{exception.GetType().Name}");
            return false;
        }
    }

    internal bool TryInstall(Stream iconStream)
    {
        ArgumentNullException.ThrowIfNull(iconStream);
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsTrayService));
        }

        if (!OperatingSystem.IsWindows() || _application.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime)
        {
            FailureReason = OperatingSystem.IsWindows()
                ? "当前图形会话未提供 Windows 系统托盘服务。"
                : "系统托盘入口仅在 Windows 首发平台启用。";
            return false;
        }

        if (_icon is not null)
        {
            return true;
        }

        TrayIcon? icon = null;
        TrayIcons? icons = null;
        try
        {
            icons = TrayIcon.GetIcons(_application) ?? new TrayIcons();
            icon = new TrayIcon
            {
                Icon = new WindowIcon(iconStream),
                ToolTipText = "闲鱼监控与 AI 推荐 Launcher",
                Menu = CreateMenu(),
                IsVisible = true,
            };

            icons.Add(icon);
            TrayIcon.SetIcons(_application, icons);
            _icon = icon;
            FailureReason = null;
            return true;
        }
        catch (Exception exception)
        {
            if (icon is not null)
            {
                try
                {
                    icons?.Remove(icon);
                    icon.Dispose();
                }
                catch (Exception cleanupException)
                {
                    System.Diagnostics.Trace.TraceError($"托盘初始化回滚失败；异常类型：{cleanupException.GetType().Name}");
                }
            }
            FailureReason = $"系统托盘初始化失败（{exception.GetType().Name}）。";
            System.Diagnostics.Trace.TraceError($"系统托盘初始化失败；异常类型：{exception.GetType().Name}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_icon is null)
        {
            return;
        }

        try
        {
            var icons = TrayIcon.GetIcons(_application);
            icons?.Remove(_icon);
            _icon.Dispose();
            _icon = null;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"移除系统托盘图标失败；异常类型：{exception.GetType().Name}");
            _icon = null;
        }
    }

    internal NativeMenu CreateMenu()
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem("打开窗口")
        {
            Command = new TrayActionCommand(InvokeOpenWindow),
        };
        var exit = new NativeMenuItem("安全退出")
        {
            Command = new TrayActionCommand(() => _ = InvokeSafeExitAsync()),
        };
        menu.Items.Add(open);
        menu.Items.Add(exit);
        return menu;
    }

    private void InvokeOpenWindow()
    {
        try
        {
            _openWindow();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"托盘打开窗口失败；异常类型：{exception.GetType().Name}");
        }
    }

    private async Task InvokeSafeExitAsync()
    {
        try
        {
            await _safeExit();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"托盘安全退出请求失败；异常类型：{exception.GetType().Name}");
        }
    }

    private sealed class TrayActionCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }
}
