using AiGoofish.Launcher.Core;

namespace AiGoofish.Launcher.App;

// Presentation only: translate existing typed events, never inspect process output or call AI.
internal static class LauncherActivityText
{
    public static LogRow FromDiagnostic(StartupDiagnosticRecord entry)
    {
        var stage = entry.Stage switch
        {
            StartupStage.Entry => "启动器初始化",
            StartupStage.PackageVerification => "校验发行包",
            StartupStage.InstanceRecovery => "恢复本地实例",
            StartupStage.Start => "启动服务",
            StartupStage.Stop => "停止服务",
            StartupStage.PostgresInitialize => "初始化数据库",
            StartupStage.DatabaseProvision => "准备业务数据库",
            StartupStage.PythonWeb => "启动 Web 服务",
            StartupStage.Process => "组件进程",
            StartupStage.DiagnosticStorage => "诊断记录",
            _ => "启动器事件",
        };
        var outcome = entry.Kind switch
        {
            StartupEventKind.Begin => "开始",
            StartupEventKind.Completed => "完成",
            StartupEventKind.Failed => "失败，请查看原始日志或诊断报告",
            StartupEventKind.ProcessExited => $"已退出（退出码 {entry.ExitCode?.ToString() ?? "未知"}）",
            StartupEventKind.OutputHint => entry.OutputHint switch
            {
                ProcessOutputHint.AddressInUse => "端口被占用",
                ProcessOutputHint.AccessDenied => "访问被拒绝",
                ProcessOutputHint.MissingModule => "缺少运行依赖",
                ProcessOutputHint.AuthenticationFailed => "身份验证失败",
                ProcessOutputHint.ConnectionRefused => "连接被拒绝",
                ProcessOutputHint.SetupTokenRequired => "需要首次设置授权",
                ProcessOutputHint.FatalError => "检测到异常输出，请查看诊断报告",
                _ => "收到进程提示",
            },
            StartupEventKind.StorageUnavailable => "无法写入磁盘，当前记录仅保留在内存",
            StartupEventKind.CapacityReached => "已达记录容量上限",
            _ => "状态更新",
        };
        var level = entry.Kind switch
        {
            StartupEventKind.Failed or StartupEventKind.StorageUnavailable => "错误",
            StartupEventKind.OutputHint or StartupEventKind.CapacityReached => "警告",
            StartupEventKind.ProcessExited when entry.ExitCode is not 0 => "警告",
            _ => "信息",
        };
        var component = entry.Component switch
        {
            LauncherDiagnosticComponent.Postgres => "PostgreSQL",
            LauncherDiagnosticComponent.DatabaseProvision => "数据库准备",
            LauncherDiagnosticComponent.PythonWeb => "Web 服务",
            LauncherDiagnosticComponent.PythonMaintenance => "数据维护",
            _ => "启动器",
        };
        return new LogRow(entry.OccurredAtUtc.ToLocalTime().ToString("HH:mm:ss"), level, component, $"{stage} · {outcome}");
    }
}
