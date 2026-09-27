using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiGoofish.Launcher.App;

public enum WindowCloseChoice
{
    Ask,
    Background,
    Exit,
}

public sealed record CloseChoiceResult(WindowCloseChoice Choice, bool Remember);

public sealed class CloseChoiceWindow : Window
{
    private readonly CheckBox _remember;

    public CloseChoiceWindow(bool trayAvailable = false)
    {
        Title = "关闭 Launcher";
        Width = 480;
        Height = 270;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _remember = new CheckBox
        {
            Content = "记住我的选择",
            Margin = new Thickness(0, 8, 0, 8),
        };
        var content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "服务正在运行时，关闭 Launcher 会做什么？",
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = trayAvailable
                        ? "后台运行会把窗口放入系统托盘，可从托盘重新打开或安全退出。退出会等待服务安全停止。"
                        : "当前系统托盘不可用。后台运行时会先提示，再最小化到任务栏；服务会继续运行。退出会等待服务安全停止。",
                    TextWrapping = TextWrapping.Wrap,
                },
                _remember,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children =
                    {
                        MakeButton(trayAvailable ? "后台运行（托盘）" : "后台运行（任务栏）", WindowCloseChoice.Background),
                        MakeButton("安全退出", WindowCloseChoice.Exit),
                        MakeButton("取消", WindowCloseChoice.Ask),
                    },
                },
            },
        };
        Content = content;
    }

    public CloseChoiceResult Result { get; private set; } = new(WindowCloseChoice.Ask, false);

    private Button MakeButton(string label, WindowCloseChoice choice)
    {
        var button = new Button { Content = label, MinWidth = 86 };
        button.Click += (_, _) =>
        {
            Result = new CloseChoiceResult(choice, _remember.IsChecked is true);
            Close(Result);
        };
        return button;
    }
}

public sealed class TrayUnavailableWindow : Window
{
    public TrayUnavailableWindow(string reason)
    {
        Title = "系统托盘不可用";
        Width = 500;
        Height = 250;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "系统托盘未能启用。若继续后台运行，Launcher 会保留在任务栏，服务继续运行；可从任务栏恢复窗口。",
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = reason,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.DimGray,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children =
                    {
                        MakeButton("保持窗口打开", false),
                        MakeButton("最小化到任务栏", true),
                    },
                },
            },
        };
    }

    private Button MakeButton(string label, bool minimize)
    {
        var button = new Button { Content = label, MinWidth = 120 };
        button.Click += (_, _) => Close(minimize);
        return button;
    }
}

public sealed class ShutdownWaitWindow : Window
{
    public ShutdownWaitWindow()
    {
        Title = "服务仍在安全停止";
        Width = 450;
        Height = 210;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "安全停止已等待 60 秒。可以继续等待，或取消退出并保持 Launcher 打开。取消不会强制结束任何进程。",
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children =
                    {
                        MakeButton("继续等待", true),
                        MakeButton("取消退出", false),
                    },
                },
            },
        };
        Content = content;
    }

    private Button MakeButton(string label, bool keepWaiting)
    {
        var button = new Button { Content = label, MinWidth = 100 };
        button.Click += (_, _) => Close(keepWaiting);
        return button;
    }
}
