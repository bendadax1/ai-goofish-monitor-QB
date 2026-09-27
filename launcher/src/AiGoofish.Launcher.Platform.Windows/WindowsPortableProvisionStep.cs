using AiGoofish.Launcher.Core;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AiGoofish.Launcher.Platform.Windows;

public sealed class WindowsPortableProvisionStep : ILauncherStartupStep
{
    private const string ProgressRelativePath = "config/postgres-provision-progress.json";
    private const string CompleteRelativePath = "config/postgres-provision.json";
    private const int StateFileLimit = 64 * 1024;
    private readonly InstanceDataRootLease _lease;
    private readonly InstanceSecrets _secrets;
    private readonly string _pythonExecutable;
    private readonly string _bootstrapScript;
    private readonly string _programRoot;
    private readonly string _dataRoot;
    private readonly string _temporaryRoot;
    private readonly string _pgData;
    private readonly int _postgresPort;
    private readonly bool _newClusterForThisSession;
    private readonly TimeSpan _timeout;
    private readonly Guid _sessionId = Guid.NewGuid();
    private OwnedProcessIdentity? _helperIdentity;

    public WindowsPortableProvisionStep(
        InstanceDataRootLease lease,
        InstanceSecrets secrets,
        string pythonExecutable,
        string bootstrapScript,
        string programRoot,
        string dataRoot,
        string temporaryRoot,
        string pgData,
        int postgresPort,
        bool newClusterForThisSession,
        TimeSpan timeout)
    {
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _pythonExecutable = Path.GetFullPath(pythonExecutable);
        _bootstrapScript = Path.GetFullPath(bootstrapScript);
        _programRoot = Path.GetFullPath(programRoot);
        _dataRoot = Path.GetFullPath(dataRoot);
        _temporaryRoot = Path.GetFullPath(temporaryRoot);
        _pgData = Path.GetFullPath(pgData);
        _postgresPort = postgresPort;
        _newClusterForThisSession = newClusterForThisSession;
        _timeout = timeout;
        if (!File.Exists(_pythonExecutable) || !File.Exists(_bootstrapScript) || !Directory.Exists(_programRoot) ||
            !Directory.Exists(_dataRoot) || !Directory.Exists(_temporaryRoot) ||
            postgresPort is < 1024 or > 65535 || timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("数据库准备组件路径、端口或超时无效。");
        }
    }

    public string Id => "database-provision";

    public string DisplayName => "业务数据库准备";

    public ValueTask<bool> IsQuiescentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_helperIdentity is null ||
            PythonProcessIdentityProbe.GetRunState(_helperIdentity) is PythonIdentityRunState.NotRunning);
    }

    public Task VerifyCompletedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifyCompletedForRecovery();
        return Task.CompletedTask;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _lease.EnsureHeld();
        var completePath = Path.Combine(_lease.InstanceRoot, CompleteRelativePath);
        var progressPath = Path.Combine(_lease.InstanceRoot, ProgressRelativePath);
        if (File.Exists(completePath))
        {
            try
            {
                VerifyCompletedForRecovery();
            }
            catch (PythonLifecycleException exception) when (exception.SafeDiagnosticCode is null)
            {
                throw new PythonLifecycleException(
                    "业务数据库准备完成记录或关联状态无法核验；保留现有数据且拒绝自动修复。",
                    exception,
                    LauncherDiagnosticCode.DatabaseProvisionInterrupted);
            }
            return;
        }

        if (!_newClusterForThisSession || File.Exists(progressPath))
        {
            throw new PythonLifecycleException(
                "集群缺少可信业务数据库准备完成记录；拒绝对既有或中断集群自动重试。",
                LauncherDiagnosticCode.DatabaseProvisionInterrupted);
        }

        var starting = new ProvisionProgress(
            1,
            _lease.InstanceId,
            _sessionId,
            _pgData,
            "Starting",
            null,
            DateTimeOffset.UtcNow);
        PythonStateFile.WriteNew(progressPath, starting);
        var result = await PythonProvisionCommand.RunAsync(
            _pythonExecutable,
            _dataRoot,
            new[]
            {
                "-I", "-B", _bootstrapScript,
                "--app-root", _programRoot,
                "--target", "provision",
                "--",
                "--pgdata", _pgData,
                "--instance-id", _lease.InstanceId.ToString("D"),
            },
            new[]
            {
                new ProcessEnvironmentVariable("TEMP", _temporaryRoot),
                new ProcessEnvironmentVariable("TMP", _temporaryRoot),
                new ProcessEnvironmentVariable("GOOFISH_PORTABLE_ADMIN_DATABASE_URL", Dsn("aigoofish_bootstrap", _secrets.PostgresBootstrapAdminPassword, _postgresPort, "postgres"), true),
                new ProcessEnvironmentVariable("GOOFISH_PORTABLE_APP_DATABASE_PASSWORD", _secrets.ApplicationDatabasePassword, true),
                new ProcessEnvironmentVariable("GOOFISH_PORTABLE_PROBE_DATABASE_PASSWORD", _secrets.ProbeDatabasePassword, true),
            },
            _timeout,
            cancellationToken,
            identity =>
            {
                _helperIdentity = new OwnedProcessIdentity(
                    identity.ProcessId,
                    identity.StartedAtUtc,
                    identity.ExecutablePath,
                    _lease.InstanceRoot,
                    _lease.InstanceId,
                    _sessionId);
                PythonStateFile.WriteReplace(progressPath, starting with
                {
                    State = "Running",
                    Helper = identity,
                    ObservedAtUtc = DateTimeOffset.UtcNow,
                });
            }).ConfigureAwait(false);
        if (result.ExitCode != 0 || !ValidateProvisionOutput(result.StandardOutput))
        {
            throw new PythonLifecycleException(
                "业务数据库准备未通过结果核验；保留中断记录且不自动重跑。",
                LauncherDiagnosticCode.DatabaseProvisionInterrupted);
        }

        PythonStateFile.WriteNew(completePath, new ProvisionComplete(1, _lease.InstanceId, 1, DateTimeOffset.UtcNow));
        try
        {
            File.Delete(progressPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PythonLifecycleException(
                "业务数据库准备已完成，但状态记录未能一致提交；拒绝假定可用。",
                exception,
                LauncherDiagnosticCode.DatabaseProvisionInterrupted);
        }
    }

    public void VerifyCompletedForRecovery()
    {
        _lease.EnsureHeld();
        if (_helperIdentity is not null &&
            PythonProcessIdentityProbe.GetRunState(_helperIdentity) is not PythonIdentityRunState.NotRunning)
        {
            throw new PythonLifecycleException("数据库准备组件已持有辅助进程身份，拒绝将其作为静态完成记录恢复。");
        }

        var completePath = Path.Combine(_lease.InstanceRoot, CompleteRelativePath);
        var progressPath = Path.Combine(_lease.InstanceRoot, ProgressRelativePath);
        if (!File.Exists(completePath))
        {
            throw new PythonLifecycleException(
                "既有实例缺少可信业务数据库准备完成记录；恢复流程不会自动执行 provision。",
                LauncherDiagnosticCode.DatabaseProvisionInterrupted);
        }

        var complete = PythonStateFile.Read<ProvisionComplete>(completePath, StateFileLimit);
        if (complete.FormatVersion != 1 || complete.InstanceId != _lease.InstanceId || complete.SchemaVersion != 1)
        {
            throw new PythonLifecycleException("数据库准备完成记录与当前实例不匹配。");
        }

        if (File.Exists(progressPath))
        {
            throw new PythonLifecycleException(
                "业务数据库准备完成记录旁仍存在中断记录，拒绝假定可用。",
                LauncherDiagnosticCode.DatabaseProvisionInterrupted);
        }

        _helperIdentity = null;
    }

    internal static void RecordRestoredCompletion(
        InstanceDataRootLease lease,
        string pgData,
        int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureHeld();
        if (schemaVersion != 1)
        {
            throw new PythonLifecycleException("恢复目标 schema 版本与 P1 基线不兼容。");
        }

        var normalizedPgData = Path.GetFullPath(pgData);
        PostgresPathGuard.EnsureSafePath(lease, normalizedPgData, allowMissingLeaf: false);
        var markerPath = Path.Combine(normalizedPgData, ".aigoofish-cluster.json");
        var versionPath = Path.Combine(normalizedPgData, "PG_VERSION");
        PostgresPathGuard.EnsureSafePath(lease, markerPath, allowMissingLeaf: false);
        PostgresPathGuard.EnsureSafePath(lease, versionPath, allowMissingLeaf: false);
        try
        {
            if (new FileInfo(markerPath).Length is <= 0 or > 16 * 1024)
            {
                throw new PythonLifecycleException("恢复目标集群标记大小无效。");
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(markerPath));
            var marker = document.RootElement;
            if (marker.GetProperty("format_version").GetInt32() != 1 ||
                marker.GetProperty("instance_id").GetGuid() != lease.InstanceId ||
                marker.GetProperty("cluster_id").GetGuid() == Guid.Empty ||
                marker.GetProperty("engine_version").GetString() is not { } engineVersion ||
                !engineVersion.StartsWith("17.", StringComparison.Ordinal) ||
                File.ReadAllText(versionPath).Trim() != "17")
            {
                throw new PythonLifecycleException("恢复完成记录要求与 lease 一致的全新 PG17 集群。");
            }
        }
        catch (PythonLifecycleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new PythonLifecycleException("无法核验恢复完成记录对应的 PostgreSQL 集群。", exception);
        }

        var store = new WindowsInstanceSecretsStore();
        _ = store.Load(lease);
        var completePath = Path.Combine(lease.InstanceRoot, CompleteRelativePath);
        var progressPath = Path.Combine(lease.InstanceRoot, ProgressRelativePath);
        PythonPathGuard.EnsureSafeInstancePath(lease, completePath, allowMissingLeaf: true);
        PythonPathGuard.EnsureSafeInstancePath(lease, progressPath, allowMissingLeaf: true);
        if (File.Exists(completePath) || File.Exists(progressPath))
        {
            throw new PythonLifecycleException("恢复目标已有 provision 或中断标记；拒绝覆盖恢复完成记录。");
        }

        PythonPathGuard.EnsureSafeInstancePath(lease, Path.GetDirectoryName(completePath)!, allowMissingLeaf: false);
        PythonStateFile.WriteNew(completePath, new ProvisionComplete(1, lease.InstanceId, schemaVersion, DateTimeOffset.UtcNow));
    }

    private static bool ValidateProvisionOutput(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            return root.ValueKind is JsonValueKind.Object &&
                root.TryGetProperty("database", out var database) && database.GetString() == "aigoofish" &&
                root.TryGetProperty("app_role", out var appRole) && appRole.GetString() == "aigoofish_app" &&
                root.TryGetProperty("probe_role", out var probeRole) && probeRole.GetString() == "aigoofish_probe" &&
                root.TryGetProperty("schema_version", out var version) && version.TryGetInt32(out var value) && value == 1;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Dsn(string role, string password, int port, string database)
    {
        return $"postgresql://{role}:{Uri.EscapeDataString(password)}@127.0.0.1:{port}/{database}";
    }

    private sealed record ProvisionProgress(
        int FormatVersion,
        Guid InstanceId,
        Guid SessionId,
        string DataDirectory,
        string State,
        PostgresHelperIdentity? Helper,
        DateTimeOffset ObservedAtUtc);

    private sealed record ProvisionComplete(
        int FormatVersion,
        Guid InstanceId,
        int SchemaVersion,
        DateTimeOffset CompletedAtUtc);
}

internal sealed record PythonProvisionCommandResult(int ExitCode, string StandardOutput);

internal static class PythonProvisionCommand
{
    private const int OutputLimit = 64 * 1024;
    private static readonly string[] InheritedEnvironment =
    {
        "COMSPEC", "NUMBER_OF_PROCESSORS", "PATH", "PATHEXT", "PROCESSOR_ARCHITECTURE", "SystemRoot", "WINDIR",
    };

    public static async Task<PythonProvisionCommandResult> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyList<ProcessEnvironmentVariable> variables,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<PostgresHelperIdentity> onStarted)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Clear();
        foreach (var name in InheritedEnvironment)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                info.Environment[name] = value;
            }
        }

        foreach (var variable in variables)
        {
            info.Environment[variable.Name] = variable.Value;
        }

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
            {
                throw new PythonLifecycleException("数据库准备辅助进程未创建。");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new PythonLifecycleException("数据库准备辅助进程启动失败。", exception);
        }

        onStarted(new PostgresHelperIdentity(
            process.Id,
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
            Path.GetFullPath(executable)));
        var stdout = ReadCappedAsync(process.StandardOutput, CancellationToken.None);
        var stderr = ReadCappedAsync(process.StandardError, CancellationToken.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var errors = await stderr.ConfigureAwait(false);
            StartupDiagnostics.Current.Record(StartupStage.DatabaseProvision, StartupEventKind.ProcessExited,
                LauncherDiagnosticComponent.DatabaseProvision, exitCode: process.ExitCode,
                outputHint: StartupDiagnostics.ClassifyOutput(errors));
            return new PythonProvisionCommandResult(process.ExitCode, output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PythonLifecycleException("数据库准备辅助进程超时；未强制终止且保留身份记录。");
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[512];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return result.ToString();
            }

            if (result.Length + read > OutputLimit)
            {
                throw new PythonLifecycleException("数据库准备辅助进程输出超过 64 KiB 上限。");
            }

            result.Append(buffer, 0, read);
        }
    }
}
