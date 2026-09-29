using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiGoofish.Launcher.App;

public sealed class BackupPassphraseDialog : Window
{
    private readonly TextBox _passphrase;
    private readonly TextBox _confirmation;
    private readonly TextBlock _validation;

    public BackupPassphraseDialog(bool forSchemaUpgrade = false)
    {
        Title = forSchemaUpgrade ? "备份并升级数据结构" : "加密业务备份";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MinWidth = 480;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#F3F6FA"));

        _passphrase = CreatePasswordBox("BackupPassphraseBox", "输入备份口令");
        _confirmation = CreatePasswordBox("BackupPassphraseConfirmBox", "再次输入以确认");
        _validation = new TextBlock
        {
            Name = "BackupPassphraseValidation",
            Foreground = new SolidColorBrush(Color.Parse("#9F1D20")),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        var confirmButton = new Button
        {
            Name = "ConfirmBackupPassphraseButton",
            Content = "确认并继续",
            MinHeight = 42,
            Padding = new Thickness(18, 8),
            Background = new SolidColorBrush(Color.Parse("#1D4ED8")),
            Foreground = Brushes.White,
        };
        confirmButton.Click += (_, _) => Confirm();

        var cancelButton = new Button
        {
            Name = "CancelBackupPassphraseButton",
            Content = "取消",
            MinHeight = 42,
            Padding = new Thickness(18, 8),
        };
        cancelButton.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = forSchemaUpgrade
                        ? "将先停写并创建加密备份，再把当前数据库从 v1 升级到 v2。升级后旧程序不能直接读取新数据库；失败时保持停机并保留备份。"
                        : "备份将安全停止 Web 与 PostgreSQL，过程可能需要较长时间。备份仅写入刚才选择的文件夹。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#24334A")),
                },
                new TextBlock
                {
                    Text = "请使用 12 到 1024 个 UTF-8 字节的口令，并妥善保存。忘记口令将无法恢复备份。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#46576D")),
                },
                _passphrase,
                _confirmation,
                _validation,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, confirmButton },
                },
            },
        };
        Closed += (_, _) => ClearFields();
    }

    private static TextBox CreatePasswordBox(string name, string watermark) => new()
    {
        Name = name,
        PlaceholderText = watermark,
        PasswordChar = '●',
        MinHeight = 40,
        MaxLength = 1024,
    };

    private void Confirm()
    {
        var first = _passphrase.Text ?? string.Empty;
        var second = _confirmation.Text ?? string.Empty;
        if (!MainWindowViewModel.CanUseBackupPassphrase(first))
        {
            ShowValidation("备份口令必须包含 12 到 1024 个 UTF-8 字节。");
            return;
        }
        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            ShowValidation("两次输入的口令不一致。");
            return;
        }

        ClearFields();
        Close(first);
    }

    private void ShowValidation(string message)
    {
        _validation.Text = message;
        _validation.IsVisible = true;
    }

    private void ClearFields()
    {
        _passphrase.Text = string.Empty;
        _confirmation.Text = string.Empty;
        _validation.Text = string.Empty;
    }
}
