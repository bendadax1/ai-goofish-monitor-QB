using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AiGoofish.Launcher.Platform.Windows;

namespace AiGoofish.Launcher.App;

internal static class PortableUninstaller
{
    private const uint YesNoCancelQuestion = 0x00000003 | 0x00000020;
    private const uint OkCancelWarningDefaultCancel = 0x00000001 | 0x00000030 | 0x00000100;
    private const uint ErrorIcon = 0x00000010;
    private const int Yes = 6;
    private const int No = 7;
    private const int Ok = 1;

    internal static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 0)
            return 2;

        try
        {
            var applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            var root = string.Equals(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(applicationDirectory)),
                "launcher", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(Path.Combine(applicationDirectory, ".."))
                : applicationDirectory;
            _ = PortableBundleDescriptor.LoadAndVerifyAsync(root).GetAwaiter().GetResult();

            var answer = MessageBoxW(IntPtr.Zero,
                "请选择卸载范围：\n\n是：移除程序，保留 data/ 数据库、配置和 backups/ 备份（推荐）。\n\n" +
                "否：完全删除程序、data/ 和 backups/。\n\n取消：不执行任何操作。\n\n" +
                $"便携包位置：{root}",
                "卸载闲鱼监控便携版", YesNoCancelQuestion);
            if (answer is not (Yes or No)) return 0;

            var mode = answer == Yes ? "preserve" : "complete";
            if (mode == "complete" && MessageBoxW(IntPtr.Zero,
                "完全删除会永久移除本便携目录中的数据库、登录态、配置、任务记录和备份。" +
                "请先确认已在其他位置保留需要的数据。\n\n确定继续完全删除吗？",
                "再次确认删除数据", OkCancelWarningDefaultCancel) != Ok)
                return 0;

            using var resource = typeof(PortableUninstaller).Assembly.GetManifestResourceStream("PortableUninstallHelper")
                ?? throw new IOException("卸载助手资源缺失。");
            using var reader = new StreamReader(resource, new UTF8Encoding(false, true));
            var script = reader.ReadToEnd();
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell)) throw new IOException("找不到 Windows PowerShell 卸载助手。");
            var start = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-WindowStyle");
            start.ArgumentList.Add("Hidden");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            start.Environment["AIGOOFISH_UNINSTALL_ROOT"] = root;
            start.Environment["AIGOOFISH_UNINSTALL_MODE"] = mode;
            start.Environment["AIGOOFISH_UNINSTALL_PARENT_PID"] = Environment.ProcessId.ToString();
            start.Environment["AIGOOFISH_UNINSTALL_SHOW_UI"] = "1";
            using var helper = Process.Start(start) ?? throw new IOException("卸载助手未能启动。");
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidOperationException or PortableBundleException or System.Security.SecurityException)
        {
            _ = MessageBoxW(IntPtr.Zero, $"卸载未启动：{error.Message}", "卸载闲鱼监控便携版", ErrorIcon);
            return 1;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
