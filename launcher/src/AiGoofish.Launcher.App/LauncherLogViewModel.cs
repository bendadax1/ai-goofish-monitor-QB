using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;

namespace AiGoofish.Launcher.App;

public sealed class LauncherLogViewModel : INotifyPropertyChanged
{
    private readonly ObservableCollection<LogRow> _logs;
    private readonly ObservableCollection<LogRow> _activities;
    private LogRow[] _pausedLogs = [];
    private LogRow[] _pausedActivities = [];
    private bool _paused;
    private int _level;
    private string _status = string.Empty;

    public LauncherLogViewModel(ObservableCollection<LogRow> logs, ObservableCollection<LogRow> activities)
    {
        _logs = logs;
        _activities = activities;
        logs.CollectionChanged += (_, _) => { if (!IsPaused) Refresh(); };
        activities.CollectionChanged += (_, _) => { if (!IsPaused) Refresh(); };
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<LogRow> Logs { get; private set; } = [];
    public IReadOnlyList<LogRow> Activities { get; private set; } = [];
    public bool HasLogs => Logs.Count > 0;
    public bool HasActivities => Activities.Count > 0;
    public string PauseLabel => IsPaused ? "继续更新" : "暂停更新";
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
    public bool IsPaused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            if (value) { _pausedLogs = _logs.ToArray(); _pausedActivities = _activities.ToArray(); }
            else { _pausedLogs = []; _pausedActivities = []; }
            _paused = value;
            Changed(nameof(IsPaused)); Changed(nameof(PauseLabel));
            Status = value ? "显示已暂停，后台记录继续；恢复后显示最近记录。" : string.Empty;
            Refresh();
        }
    }
    public int LevelIndex
    {
        get => _level;
        set { if (value is < 0 or > 2 || value == _level) return; _level = value; Changed(nameof(LevelIndex)); Refresh(); }
    }

    private bool Matches(LogRow row) => _level switch
    {
        1 => row.Level is "警告" or "错误",
        2 => row.Level is "错误",
        _ => true,
    };

    private void Refresh()
    {
        Logs = (IsPaused ? _pausedLogs : _logs.ToArray()).Where(Matches).ToArray();
        Activities = (IsPaused ? _pausedActivities : _activities.ToArray()).Where(Matches).ToArray();
        Changed(nameof(Logs)); Changed(nameof(Activities)); Changed(nameof(HasLogs)); Changed(nameof(HasActivities));
    }

    public string CreateExportPreview(bool activity)
    {
        var rows = activity ? Activities : Logs;
        var text = new StringBuilder($"启动器{(activity ? "活动摘要" : "原始日志")} · 当前视图 · {rows.Count} 条\n");
        foreach (var row in rows) text.AppendLine($"[{row.Time}] [{row.Level}] [{row.Component}] {row.Message}");
        var result = text.ToString();
        if (Encoding.UTF8.GetByteCount(result) > 2 * 1024 * 1024)
            throw new InvalidOperationException("当前视图超过 2 MiB，请筛选后再导出。");
        return result;
    }

    public static async Task<string> SaveNewExportAsync(string directory, string preview)
    {
        var bytes = new UTF8Encoding(false).GetBytes(preview);
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidOperationException("导出超过 2 MiB。");
        var path = Path.Combine(Path.GetFullPath(directory), $"launcher-view-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
        var temporary = path + ".tmp";
        var ownsTemporary = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                ownsTemporary = true;
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: false);
            return path;
        }
        finally
        {
            if (ownsTemporary)
            {
                try { File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Trace.TraceError($"日志导出暂存清理失败；异常类型：{exception.GetType().Name}");
                }
            }
        }
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
