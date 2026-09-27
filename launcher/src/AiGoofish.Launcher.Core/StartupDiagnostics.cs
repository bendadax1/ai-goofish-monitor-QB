using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiGoofish.Launcher.Core;

public enum StartupStage { Entry, PackageVerification, InstanceRecovery, Start, Stop, PostgresInitialize, DatabaseProvision, PythonWeb, Process, DiagnosticStorage }
public enum StartupEventKind { Begin, Completed, Failed, ProcessExited, OutputHint, StorageUnavailable, CapacityReached }
public enum StartupFailureKind { None, Cancelled, Timeout, AccessDenied, Socket, Io, InvalidState, InvalidData, Native, Other, MissingFile }
public enum StartupFileRole { None, BundleRoot, CurrentMetadata, BundleManifest, ComponentDirectory, ComponentFile, BundleFile }
public interface IStartupFailureContext
{
    StartupFailureKind FailureKind { get; }
    StartupFileRole FileRole { get; }
}
public enum ProcessOutputHint { None, AddressInUse, AccessDenied, MissingModule, AuthenticationFailed, ConnectionRefused, SetupTokenRequired, FatalError }

// Only fixed enums and numeric metadata cross this boundary. Never serialize an Exception,
// its Message/StackTrace, a process line, an executable path, arguments or environment.
public sealed record StartupFailure(StartupFailureKind Kind, int HResult, int? NativeCode,
    StartupFileRole FileRole = StartupFileRole.None);
public sealed record StartupDiagnosticRecord(
    long Sequence, Guid SessionId, Guid? OperationId, DateTimeOffset OccurredAtUtc,
    long ElapsedMilliseconds, StartupStage Stage, StartupEventKind Kind,
    LauncherDiagnosticComponent Component, StartupFailure[] Failures,
    int? ExitCode, ProcessOutputHint OutputHint);

public static class StartupDiagnostics
{
    public static StartupDiagnosticSession Current { get; } = new();

    public static LauncherDiagnosticComponent Component(string id) => id switch
    {
        "postgres" or "postgres-initialize" => LauncherDiagnosticComponent.Postgres,
        "database-provision" => LauncherDiagnosticComponent.DatabaseProvision,
        "python-web" => LauncherDiagnosticComponent.PythonWeb,
        "python-maintenance" => LauncherDiagnosticComponent.PythonMaintenance,
        _ => LauncherDiagnosticComponent.Launcher,
    };

    public static ProcessOutputHint ClassifyOutput(string text)
    {
        if (text.Contains("address already in use", StringComparison.OrdinalIgnoreCase) || text.Contains("10048", StringComparison.Ordinal)) return ProcessOutputHint.AddressInUse;
        if (text.Contains("permission denied", StringComparison.OrdinalIgnoreCase) || text.Contains("access is denied", StringComparison.OrdinalIgnoreCase) || text.Contains("10013", StringComparison.Ordinal)) return ProcessOutputHint.AccessDenied;
        if (text.Contains("ModuleNotFoundError", StringComparison.Ordinal) || text.Contains("No module named", StringComparison.Ordinal)) return ProcessOutputHint.MissingModule;
        if (text.Contains("password authentication failed", StringComparison.OrdinalIgnoreCase)) return ProcessOutputHint.AuthenticationFailed;
        if (text.Contains("connection refused", StringComparison.OrdinalIgnoreCase)) return ProcessOutputHint.ConnectionRefused;
        if (text.Contains("setup", StringComparison.OrdinalIgnoreCase) && text.Contains("token", StringComparison.OrdinalIgnoreCase) && (text.Contains("required", StringComparison.OrdinalIgnoreCase) || text.Contains("missing", StringComparison.OrdinalIgnoreCase))) return ProcessOutputHint.SetupTokenRequired;
        if (text.Contains("FATAL:", StringComparison.OrdinalIgnoreCase) || text.Contains("Traceback (most recent call last)", StringComparison.Ordinal)) return ProcessOutputHint.FatalError;
        return ProcessOutputHint.None;
    }

    public static string Describe(StartupDiagnosticRecord entry) =>
        $"启动诊断 #{entry.Sequence} · {entry.Stage}/{entry.Kind} · {entry.Component}" +
        (entry.Failures.Length == 0 ? "" : $" · {string.Join(" → ", entry.Failures.Select(item => $"{item.Kind}(0x{item.HResult:X8}, native={item.NativeCode?.ToString() ?? "-"}, role={item.FileRole})"))}") +
        (entry.ExitCode is { } exit ? $" · exit={exit}" : "") +
        (entry.OutputHint is not ProcessOutputHint.None ? $" · {entry.OutputHint}" : "");
}

/// <summary>Process-lifetime journal, independent of UI, Host and backend readiness.</summary>
public sealed class StartupDiagnosticSession : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object _sync = new();
    private readonly Queue<StartupDiagnosticRecord> _records = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly int _segmentBytes;
    private readonly int _maximumSegments;
    private Stream? _stream;
    private string? _directory;
    private long _sequence;
    private int _segment;
    private bool _disposed;
    private bool _storageStopped;
    private bool _capacityReached;

    public StartupDiagnosticSession(int segmentBytes = 256 * 1024, int maximumSegments = 4)
    {
        if (segmentBytes < 1024 || segmentBytes > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(segmentBytes));
        if (maximumSegments is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maximumSegments));
        _segmentBytes = segmentBytes;
        _maximumSegments = maximumSegments;
    }

    public Guid SessionId { get; } = Guid.NewGuid();
    public string? DirectoryPath { get { lock (_sync) return _directory; } }
    public bool StorageAvailable { get { lock (_sync) return _stream is not null && !_storageStopped && !_disposed; } }
    public event EventHandler<StartupDiagnosticRecord>? Recorded;

    public IReadOnlyList<StartupDiagnosticRecord> Snapshot()
    {
        lock (_sync) return _records.Select(item => item with { Failures = item.Failures.ToArray() }).ToArray();
    }

    public bool TryOpen(string directory)
    {
        Exception? failure = null;
        lock (_sync)
        {
            if (_disposed || _stream is not null || _capacityReached) return false;
            try
            {
                var root = Path.GetFullPath(directory);
                EnsurePlainPath(root);
                Directory.CreateDirectory(root);
                EnsurePlainPath(root);
                _directory = root;
                _stream = OpenSegment();
                _storageStopped = false;
                foreach (var item in _records)
                    if (!WriteRecord(item)) break;
                return !_storageStopped;
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                failure = exception;
                CloseStream();
                _directory = null;
            }
        }
        Record(StartupStage.DiagnosticStorage, StartupEventKind.StorageUnavailable, exception: failure);
        return false;
    }

    public void Record(StartupStage stage, StartupEventKind kind,
        LauncherDiagnosticComponent component = LauncherDiagnosticComponent.Launcher,
        Exception? exception = null, Guid? operationId = null, int? exitCode = null,
        ProcessOutputHint outputHint = ProcessOutputHint.None)
    {
        if (!Enum.IsDefined(stage) || !Enum.IsDefined(kind) || !Enum.IsDefined(component) || !Enum.IsDefined(outputHint))
            throw new ArgumentOutOfRangeException(nameof(stage));
        StartupDiagnosticRecord entry;
        StartupDiagnosticRecord? storageFailure = null;
        lock (_sync)
        {
            if (_disposed) return;
            entry = Create(stage, kind, component, exception, operationId, exitCode, outputHint);
            Add(entry);
            if (_stream is not null && !_storageStopped)
            {
                try
                {
                    if (!WriteRecord(entry))
                    {
                        storageFailure = Create(StartupStage.DiagnosticStorage, StartupEventKind.CapacityReached,
                            LauncherDiagnosticComponent.Launcher, null, null, null, ProcessOutputHint.None);
                        Add(storageFailure);
                    }
                }
                catch (Exception error) when (IsStorageFailure(error))
                {
                    _storageStopped = true;
                    CloseStream();
                    storageFailure = Create(StartupStage.DiagnosticStorage, StartupEventKind.StorageUnavailable,
                        LauncherDiagnosticComponent.Launcher, error, null, null, ProcessOutputHint.None);
                    Add(storageFailure);
                }
            }
        }
        Publish(entry);
        if (storageFailure is not null) Publish(storageFailure);
    }

    private StartupDiagnosticRecord Create(StartupStage stage, StartupEventKind kind,
        LauncherDiagnosticComponent component, Exception? exception, Guid? operationId, int? exitCode, ProcessOutputHint hint) =>
        new(++_sequence, SessionId, operationId, DateTimeOffset.UtcNow,
            (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds, stage, kind, component,
            Classify(exception).ToArray(), exitCode, hint);

    private static IEnumerable<StartupFailure> Classify(Exception? error)
    {
        for (var depth = 0; error is not null && depth < 4; depth++, error = error.InnerException)
        {
            var kind = error switch
            {
                IStartupFailureContext context when Enum.IsDefined(context.FailureKind) => context.FailureKind,
                OperationCanceledException => StartupFailureKind.Cancelled,
                TimeoutException => StartupFailureKind.Timeout,
                UnauthorizedAccessException => StartupFailureKind.AccessDenied,
                SocketException => StartupFailureKind.Socket,
                Win32Exception => StartupFailureKind.Native,
                JsonException or InvalidDataException => StartupFailureKind.InvalidData,
                FileNotFoundException or DirectoryNotFoundException => StartupFailureKind.MissingFile,
                IOException => StartupFailureKind.Io,
                InvalidOperationException => StartupFailureKind.InvalidState,
                _ => StartupFailureKind.Other,
            };
            var role = error is IStartupFailureContext file && Enum.IsDefined(file.FileRole)
                ? file.FileRole : StartupFileRole.None;
            var nativeCode = error is Win32Exception native ? native.NativeErrorCode
                : (error.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000)
                    ? (int?)(error.HResult & 0xFFFF) : null;
            yield return new StartupFailure(kind, error.HResult, nativeCode, role);
        }
    }

    private void Add(StartupDiagnosticRecord entry)
    {
        while (_records.Count >= 128) _records.Dequeue();
        _records.Enqueue(entry);
    }

    private Stream OpenSegment()
    {
        EnsurePlainPath(_directory!);
        return new FileStream(Path.Combine(_directory!, $"startup-{SessionId:N}-{++_segment:D2}.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
    }

    private bool WriteRecord(StartupDiagnosticRecord entry)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
        if (_stream!.Length + bytes.Length > _segmentBytes)
        {
            CloseStream();
            if (_segment >= _maximumSegments) { _storageStopped = true; _capacityReached = true; return false; }
            _stream = OpenSegment();
        }
        _stream.Write(bytes);
        _stream.Flush();
        return true;
    }

    private static bool IsStorageFailure(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    private static void EnsurePlainPath(string path)
    {
        for (var item = new DirectoryInfo(path); item is not null; item = item.Parent)
        {
            try
            {
                if ((File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("诊断目录不允许重解析点。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private void Publish(StartupDiagnosticRecord entry)
    {
        foreach (var handler in Recorded?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((EventHandler<StartupDiagnosticRecord>)handler)(this, entry with { Failures = entry.Failures.ToArray() }); }
            catch { /* A diagnostic observer must never break process cleanup. */ }
        }
    }

    private void CloseStream()
    {
        try { _stream?.Dispose(); }
        catch (Exception error) when (IsStorageFailure(error)) { _storageStopped = true; }
        finally { _stream = null; }
    }

    public void Dispose()
    {
        lock (_sync) { _disposed = true; CloseStream(); }
    }
}
