using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiGoofish.Launcher.Core;

namespace AiGoofish.Launcher.App;

public static class LauncherDiagnosticExporter
{
    public const int MaximumBytes = 16 * 1024;
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] CreatePreview(
        LauncherSnapshot snapshot,
        IReadOnlyList<LauncherDiagnosticEvent>? diagnosticEvents = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        diagnosticEvents ??= Array.Empty<LauncherDiagnosticEvent>();
        if (diagnosticEvents.Count > BoundedDiagnosticEventBuffer.MaximumCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(diagnosticEvents), "诊断事件数量超过 64 条上限。");
        }

        var startup = StartupDiagnostics.Current.Snapshot().TakeLast(8).ToArray();
        var report = new
        {
            format = "ai-goofish-launcher-diagnostic-v1",
            createdAtUtc = DateTimeOffset.UtcNow,
            launcherVersion = LauncherVersion.Current,
            operatingSystem = "Windows",
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            state = snapshot.State.ToString(),
            observedAtUtc = snapshot.ObservedAt,
            components = snapshot.Components
                .Where(component => IsKnownComponent(component.Id))
                .Select(component => new { id = component.Id, state = component.State.ToString() })
                .ToArray(),
            events = diagnosticEvents.Select(ToExportEvent).ToArray(),
            startupSteps = snapshot.StartupSteps
                .Where(step => step.Id is "postgres-initialize" or "database-provision")
                .Select(step => new { id = step.Id, state = step.State.ToString(), quiescent = step.IsQuiescent })
                .ToArray(),
            startup,
            startupJournalAvailable = StartupDiagnostics.Current.StorageAvailable,
            privacy = "只含白名单启动事件，不含配置、原始日志、路径、账号、凭据、请求内容或业务数据。",
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, ExportJsonOptions);
        while (bytes.Length > MaximumBytes && startup.Length > 0)
        {
            startup = startup.Skip(1).ToArray();
            bytes = JsonSerializer.SerializeToUtf8Bytes(report with { startup = startup }, ExportJsonOptions);
        }
        if (bytes.Length > MaximumBytes)
        {
            throw new InvalidOperationException("诊断摘要超过安全大小上限。");
        }

        return bytes;
    }

    public static async Task SaveNewFileAtomicallyAsync(string destinationPath, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (contents.Length is <= 0 or > MaximumBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(contents), "诊断文件为空或超过 16 KiB 上限。");
        }

        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("诊断目标必须位于现有目录中。", nameof(destinationPath));
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("所选诊断目录不存在。");
        }

        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination);
        }
        catch
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceError($"诊断临时文件清理失败；异常类型：{cleanupException.GetType().Name}");
            }

            throw;
        }
    }

    private static bool IsKnownComponent(string id) => id is "postgres" or "database-provision" or "python-web";

    private static object ToExportEvent(LauncherDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        var code = diagnosticEvent.Code switch
        {
            LauncherDiagnosticCode.StartupComponentFailed => "STARTUP_COMPONENT_FAILED",
            LauncherDiagnosticCode.ComponentNotReady => "COMPONENT_NOT_READY",
            LauncherDiagnosticCode.ComponentStopFailed => "COMPONENT_STOP_FAILED",
            LauncherDiagnosticCode.ComponentStopUnconfirmed => "COMPONENT_STOP_UNCONFIRMED",
            LauncherDiagnosticCode.ComponentStateReadFailed => "COMPONENT_STATE_READ_FAILED",
            LauncherDiagnosticCode.PostgresInitInterrupted => "POSTGRES_INIT_INTERRUPTED",
            LauncherDiagnosticCode.PostgresMarkerMissing => "POSTGRES_MARKER_MISSING",
            LauncherDiagnosticCode.PostgresDataUnverified => "POSTGRES_DATA_UNVERIFIED",
            LauncherDiagnosticCode.DatabaseProvisionInterrupted => "DATABASE_PROVISION_INTERRUPTED",
            _ => throw new ArgumentOutOfRangeException(nameof(diagnosticEvent), "不支持的诊断错误码。"),
        };
        var component = diagnosticEvent.Component switch
        {
            LauncherDiagnosticComponent.Launcher => "launcher",
            LauncherDiagnosticComponent.Postgres => "postgres",
            LauncherDiagnosticComponent.DatabaseProvision => "database-provision",
            LauncherDiagnosticComponent.PythonWeb => "python-web",
            LauncherDiagnosticComponent.PythonMaintenance => "python-maintenance",
            _ => throw new ArgumentOutOfRangeException(nameof(diagnosticEvent), "不支持的诊断组件。"),
        };
        var severity = diagnosticEvent.Severity switch
        {
            LauncherDiagnosticSeverity.Information => "information",
            LauncherDiagnosticSeverity.Warning => "warning",
            LauncherDiagnosticSeverity.Error => "error",
            _ => throw new ArgumentOutOfRangeException(nameof(diagnosticEvent), "不支持的诊断严重度。"),
        };

        return new
        {
            occurredAtUtc = diagnosticEvent.OccurredAtUtc,
            code,
            component,
            severity,
        };
    }
}
