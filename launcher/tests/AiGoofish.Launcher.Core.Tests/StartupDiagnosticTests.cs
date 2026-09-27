using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AiGoofish.Launcher.Core;

internal static class StartupDiagnosticTests
{
    public static Task RunAsync()
    {
        var repository = Environment.GetEnvironmentVariable("AIGOOFISH_REPOSITORY_ROOT")
            ?? throw new InvalidOperationException("诊断验收要求显式仓库根目录。");
        var root = Path.Combine(repository, ".tmp", "tests", "launcher-refactor", "journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var succeeded = false;
        try
        {
            using (var session = new StartupDiagnosticSession())
            {
                session.Record(StartupStage.Entry, StartupEventKind.Begin);
                var blocked = Path.Combine(root, "not-a-directory");
                File.WriteAllText(blocked, "fixture", new UTF8Encoding(false));
                Require(!session.TryOpen(Path.Combine(blocked, "child")), "落盘失败应明确降级");
                Require(session.Snapshot().Any(item => item.Kind is StartupEventKind.StorageUnavailable), "落盘失败必须留在内存诊断");
                Require(session.TryOpen(Path.Combine(root, "fallback")), "应允许独立备用目录");
                session.Recorded += (_, _) => throw new IOException("observer-fixture-secret");
                session.Record(StartupStage.Start, StartupEventKind.Failed, LauncherDiagnosticComponent.PythonWeb,
                    new InvalidOperationException("password=fixture-secret; Cookie=fixture-cookie; F:\\private\\data",
                        new SocketException(10013)), Guid.NewGuid());
                var observed = session.Snapshot().Last();
                Require(observed.Failures.Length == 2 && observed.Failures[1].NativeCode == 10013, "保留安全异常链与系统错误码");
                observed.Failures[0] = new StartupFailure(StartupFailureKind.None, 0, null);
                Require(session.Snapshot().Last().Failures[0].Kind is StartupFailureKind.InvalidState, "快照不可修改内部证据");
                Require(StartupDiagnostics.ClassifyOutput("FATAL: password authentication failed for user fixture-secret") == ProcessOutputHint.AuthenticationFailed, "进程输出分类");
                session.Record(StartupStage.Process, StartupEventKind.ProcessExited, exitCode: 23);
            }
            var files = Directory.GetFiles(Path.Combine(root, "fallback"), "*.jsonl");
            Require(files.Length == 1, "普通启动仅写一个段");
            var bytes = File.ReadAllBytes(files[0]);
            Require(!bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "必须 UTF-8 无 BOM");
            var text = Encoding.UTF8.GetString(bytes);
            Require(!text.Contains("fixture-secret") && !text.Contains("fixture-cookie") && !text.Contains("private"), "禁止异常原文、凭据和路径进入日志");
            foreach (var line in File.ReadLines(files[0], Encoding.UTF8))
            {
                using var json = JsonDocument.Parse(line);
                Require(json.RootElement.GetProperty("SessionId").GetGuid() != Guid.Empty, "可关联的会话 ID");
            }
            using (var bounded = new StartupDiagnosticSession(1024, 2))
            {
                Require(bounded.TryOpen(Path.Combine(root, "bounded")), "有界日志初始化");
                Parallel.For(0, 300, _ => bounded.Record(StartupStage.Start, StartupEventKind.Begin));
                Require(!bounded.StorageAvailable, "容量耗尽须可见，不得继续写或覆盖历史");
                Require(!bounded.TryOpen(Path.Combine(root, "capacity-bypass")), "换目录不能绕过单会话容量上限");
                Require(bounded.Snapshot().Count == 128, "内存严格有界");
            }
            var segments = Directory.GetFiles(Path.Combine(root, "bounded"), "*.jsonl");
            Require(segments.Length == 2 && segments.All(file => new FileInfo(file).Length <= 1024), "段数和大小上限");
            using (var reopened = new StartupDiagnosticSession())
            {
                Require(reopened.TryOpen(Path.Combine(root, "fallback")), "后续会话应创建新文件");
                reopened.Record(StartupStage.Entry, StartupEventKind.Begin);
            }
            Require(File.ReadAllBytes(files[0]).SequenceEqual(bytes), "不覆盖旧会话日志");
            succeeded = true;
            return Task.CompletedTask;
        }
        finally
        {
            // Only this freshly generated synthetic fixture; failures remain for diagnosis.
            if (succeeded) Directory.Delete(root, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
