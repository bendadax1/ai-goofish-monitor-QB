namespace AiGoofish.Launcher.Core;

public enum LauncherDiagnosticComponent
{
    Launcher,
    Postgres,
    DatabaseProvision,
    PythonWeb,
    PythonMaintenance,
}

public enum LauncherDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public enum LauncherDiagnosticCode
{
    StartupComponentFailed,
    ComponentNotReady,
    ComponentStopFailed,
    ComponentStopUnconfirmed,
    ComponentStateReadFailed,
    PostgresInitInterrupted,
    PostgresMarkerMissing,
    PostgresDataUnverified,
    DatabaseProvisionInterrupted,
}

/// <summary>
/// Allows a component to provide a fixed, user-safe failure category for UI and
/// diagnostic export. Implementations must not include paths, secrets or raw output.
/// </summary>
public interface ILauncherSafeFailure
{
    LauncherDiagnosticCode? SafeDiagnosticCode { get; }
}

public static class LauncherSafeFailureMessages
{
    public static string For(LauncherDiagnosticCode code) => code switch
    {
        LauncherDiagnosticCode.PostgresInitInterrupted => "首次 PostgreSQL 初始化未完成。数据目录已保留，未自动重试或重建。请保留现有数据并导出诊断摘要；如需恢复，请使用已验证备份恢复到独立的新实例。",
        LauncherDiagnosticCode.PostgresMarkerMissing => "PostgreSQL 数据目录缺少可信初始化标记。数据已保留，未补写标记或重建。请保留现有数据并导出诊断摘要；如需恢复，请使用已验证备份恢复到独立的新实例。",
        LauncherDiagnosticCode.PostgresDataUnverified => "无法确认 PostgreSQL 数据目录状态。数据已保留，未执行修复。请勿手动删除或修改文件；导出诊断摘要并寻求支持，或从已验证备份恢复到独立的新实例。",
        LauncherDiagnosticCode.DatabaseProvisionInterrupted => "首次业务数据库准备未完成或缺少可信完成记录。数据库与数据目录已保留，未自动重跑。请导出诊断摘要并寻求支持；如需恢复，请使用已验证备份恢复到独立的新实例。",
        _ => throw new ArgumentOutOfRangeException(nameof(code), "不支持的安全失败类别。"),
    };
}

/// <summary>
/// A deliberately data-free diagnostic event. It cannot carry exception text, paths,
/// credentials, user input, or process output.
/// </summary>
public sealed record LauncherDiagnosticEvent
{
    public LauncherDiagnosticEvent(
        DateTimeOffset occurredAt,
        LauncherDiagnosticCode code,
        LauncherDiagnosticComponent component,
        LauncherDiagnosticSeverity severity)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        if (!Enum.IsDefined(component))
        {
            throw new ArgumentOutOfRangeException(nameof(component));
        }

        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        OccurredAtUtc = occurredAt.ToUniversalTime();
        Code = code;
        Component = component;
        Severity = severity;
    }

    public DateTimeOffset OccurredAtUtc { get; }

    public LauncherDiagnosticCode Code { get; }

    public LauncherDiagnosticComponent Component { get; }

    public LauncherDiagnosticSeverity Severity { get; }
}

public sealed class BoundedDiagnosticEventBuffer
{
    public const int MaximumCapacity = 64;

    private readonly Queue<LauncherDiagnosticEvent> _events;
    private readonly object _sync = new();

    public BoundedDiagnosticEventBuffer(int capacity = MaximumCapacity)
    {
        if (capacity <= 0 || capacity > MaximumCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), $"诊断事件容量必须介于 1 和 {MaximumCapacity} 之间。");
        }

        Capacity = capacity;
        _events = new Queue<LauncherDiagnosticEvent>(capacity);
    }

    public int Capacity { get; }

    public void Add(LauncherDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);

        lock (_sync)
        {
            while (_events.Count >= Capacity)
            {
                _events.Dequeue();
            }

            _events.Enqueue(diagnosticEvent);
        }
    }

    public IReadOnlyList<LauncherDiagnosticEvent> Snapshot()
    {
        lock (_sync)
        {
            return _events.ToArray();
        }
    }
}
