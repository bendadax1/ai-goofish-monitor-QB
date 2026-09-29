using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiGoofish.Launcher.Platform.Windows;

internal sealed record PortableBusinessRestoreResult(
    Guid TargetInstanceId,
    string SourceInstanceId,
    string ArchiveSha256,
    int SchemaVersion,
    IReadOnlyDictionary<string, long> TableCounts,
    int RestoredFileCount,
    int RevokedSessionCount,
    string HandoffDirectory,
    string Status);

internal static class PortableBusinessRestoreRunner
{
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromMinutes(45);
    private const int MaximumResponseFrameBytes = 64 * 1024;

    public static async Task<PortableBusinessRestoreResult> RunAsync(
        PortableBundleDescriptor bundle,
        PortableRestoreTarget target,
        string archiveFile,
        string passphrase,
        string postgresAdminPassword,
        Func<CancellationToken, Task> verifyMaintenance,
        CancellationToken cancellationToken,
        Func<ProcessStartInfo, Process>? processFactory = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(verifyMaintenance);
        target.Lease.EnsureHeld();
        if (string.IsNullOrWhiteSpace(archiveFile) || !Path.IsPathFullyQualified(archiveFile) ||
            string.IsNullOrWhiteSpace(passphrase) || Encoding.UTF8.GetByteCount(passphrase) is < 12 or > 1024 ||
            string.IsNullOrWhiteSpace(postgresAdminPassword) ||
            !File.Exists(archiveFile) || (File.GetAttributes(archiveFile) & FileAttributes.ReparsePoint) != 0)
        {
            throw new PortableRestoreException("RESTORE_INPUT_INVALID", "恢复文件、口令或目标参数无效。");
        }

        var normalizedArchive = Path.GetFullPath(archiveFile);
        var stableContainer = Directory.GetParent(Directory.GetParent(target.DataRoot)?.FullName ?? string.Empty)?.FullName
            ?? throw new PortableRestoreException("RESTORE_PATH_INVALID", "恢复目标稳定数据容器无效。");
        if (IsWithin(stableContainer, normalizedArchive))
        {
            throw new PortableRestoreException("RESTORE_ARCHIVE_IN_DATA", "备份文件必须位于稳定数据容器之外。");
        }

        var cache = Path.Combine(target.DataRoot, "cache");
        var temporaryRoot = Path.Combine(cache, "temp");
        Directory.CreateDirectory(temporaryRoot);
        PortableInstanceCatalog.EnsureSafePath(target.DataRoot, temporaryRoot, allowMissingLeaf: false);
        var instancesRoot = Path.GetDirectoryName(target.DataRoot)
            ?? throw new PortableRestoreException("RESTORE_PATH_INVALID", "恢复目标目录无效。");
        var postgresData = Path.Combine(target.DataRoot, "postgres", "cluster");
        var session = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var start = new ProcessStartInfo
        {
            FileName = bundle.PythonExecutable,
            WorkingDirectory = target.DataRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in new[]
        {
            "-I", "-B", Path.Combine(bundle.ProgramRoot, "scripts", "portable", "python-bootstrap.py"),
            "--app-root", bundle.ProgramRoot, "--target", "restore",
        })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                start.Environment[name] = value;
            }
        }

        start.Environment["TEMP"] = temporaryRoot;
        start.Environment["TMP"] = temporaryRoot;
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";

        Process process;
        try
        {
            process = (processFactory ?? (info => new Process { StartInfo = info }))(start);
            if (!process.Start())
            {
                process.Dispose();
                throw new PortableRestoreException("RESTORE_HELPER_START_FAILED", "固定恢复辅助进程未启动。");
            }
        }
        catch (PortableRestoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new PortableRestoreException("RESTORE_HELPER_START_FAILED", "无法启动固定恢复辅助进程。", exception);
        }

        using (process)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(RestoreTimeout);
            var errorDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            try
            {
                var request = new Dictionary<string, object>
                {
                    ["type"] = "restore",
                    ["archive_file"] = normalizedArchive,
                    ["passphrase"] = passphrase,
                    ["postgres_root"] = bundle.PostgresRoot,
                    ["pgdata"] = postgresData,
                    ["instance_id"] = target.InstanceId.ToString("D"),
                    ["admin_dsn"] = $"postgresql://{WindowsPostgresOptions.BootstrapAdminRole}:{Uri.EscapeDataString(postgresAdminPassword)}@127.0.0.1:{target.PostgresPort}/postgres",
                    ["target_data_root"] = target.DataRoot,
                    ["staging_parent"] = instancesRoot,
                    ["handoff_parent"] = instancesRoot,
                    ["session"] = session,
                };
                var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request);
                request.Clear();
                try
                {
                    await process.StandardInput.BaseStream.WriteAsync(requestBytes, deadline.Token).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.WriteAsync("\n"u8.ToArray(), deadline.Token).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.FlushAsync(deadline.Token).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(requestBytes);
                }

                var checks = 0;
                PortableBusinessRestoreResult? result = null;
                while (true)
                {
                    var line = await ReadBoundedLineAsync(process.StandardOutput, MaximumResponseFrameBytes, deadline.Token)
                        .ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    using var document = JsonDocument.Parse(line);
                    var frame = document.RootElement;
                    if (frame.ValueKind is not JsonValueKind.Object ||
                        !frame.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String ||
                        !frame.TryGetProperty("session", out var echoedSession) || echoedSession.GetString() != session)
                    {
                        throw new PortableRestoreException("RESTORE_PROTOCOL_INVALID", "恢复辅助进程身份或响应格式无效。");
                    }

                    var type = typeElement.GetString();
                    if (type == "verify")
                    {
                        if (frame.EnumerateObject().Count() != 3 || checks >= 4 ||
                            !frame.TryGetProperty("counter", out var counter) || counter.ValueKind != JsonValueKind.Number ||
                            counter.GetInt32() != checks + 1)
                        {
                            throw new PortableRestoreException("RESTORE_PROOF_INVALID", "恢复停写复核顺序或次数无效。");
                        }

                        await verifyMaintenance(deadline.Token).ConfigureAwait(false);
                        checks++;
                        var acknowledgement = JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            type = "verified", session, counter = checks,
                        });
                        try
                        {
                            await process.StandardInput.BaseStream.WriteAsync(acknowledgement, deadline.Token).ConfigureAwait(false);
                            await process.StandardInput.BaseStream.WriteAsync("\n"u8.ToArray(), deadline.Token).ConfigureAwait(false);
                            await process.StandardInput.BaseStream.FlushAsync(deadline.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(acknowledgement);
                        }

                        continue;
                    }

                    if (type == "complete" && result is null)
                    {
                        result = ParseComplete(frame, target, instancesRoot, checks, session);
                        continue;
                    }

                    throw new PortableRestoreException("RESTORE_PROTOCOL_INVALID", "恢复辅助进程返回了无效或重复响应。");
                }

                process.StandardInput.Close();
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                await errorDrain.ConfigureAwait(false);
                if (process.ExitCode != 0 || checks != 4 || result is null)
                {
                    throw new PortableRestoreException("RESTORE_HELPER_FAILED", "隔离恢复辅助进程未完成四次维护复核。");
                }

                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PortableRestoreException("RESTORE_TIMEOUT", "隔离恢复超过允许时限；新目标保留供诊断。");
            }
            finally
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch (IOException)
                    {
                        // The child may have already closed its private pipe.
                    }

                    try
                    {
                        using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await process.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
    }

    private static PortableBusinessRestoreResult ParseComplete(
        JsonElement frame,
        PortableRestoreTarget target,
        string handoffParent,
        int proofCount,
        string session)
    {
        var fieldCount = frame.EnumerateObject().Count();
        var schemaVersion = 1;
        if (fieldCount == 11 &&
            (!frame.TryGetProperty("schema_version", out var schemaElement) ||
             !schemaElement.TryGetInt32(out schemaVersion) || schemaVersion is not (1 or 2)))
            throw new PortableRestoreException("RESTORE_RESULT_INVALID", "恢复摘要 schema 版本无效。");
        // Legacy v1 helpers emitted ten fields; they could not restore v2.
        if (proofCount != 4 || fieldCount is not (10 or 11) ||
            !TryGetString(frame, "instance_id", out var instanceText) ||
            !Guid.TryParseExact(instanceText, "D", out var instanceId) || instanceId != target.InstanceId ||
            !TryGetString(frame, "source_instance_id", out var sourceInstance) ||
            !Guid.TryParseExact(sourceInstance, "D", out var sourceInstanceId) || sourceInstanceId == target.InstanceId ||
            !TryGetString(frame, "archive_sha256", out var archiveSha256) || archiveSha256.Length != 64 ||
            archiveSha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            !TryGetString(frame, "status", out var status) || status != "awaiting_target_user_dpapi_and_switch_confirmation" ||
            !TryGetString(frame, "handoff_directory", out var handoffDirectory) || !Path.IsPathFullyQualified(handoffDirectory) ||
            !frame.TryGetProperty("tables", out var tablesElement) || tablesElement.ValueKind != JsonValueKind.Object ||
            !frame.TryGetProperty("files", out var filesElement) || !filesElement.TryGetInt32(out var fileCount) || fileCount < 0 ||
            !frame.TryGetProperty("revoked_sessions", out var revokedElement) || !revokedElement.TryGetInt32(out var revoked) || revoked < 0)
        {
            throw new PortableRestoreException("RESTORE_RESULT_INVALID", "恢复完成摘要未通过固定契约校验。");
        }

        var expectedPrefix = Path.Combine(handoffParent, ".restore-handoff-");
        var normalizedHandoff = Path.GetFullPath(handoffDirectory);
        if (!string.Equals(Path.GetDirectoryName(normalizedHandoff), handoffParent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(normalizedHandoff).StartsWith(Path.GetFileName(expectedPrefix), StringComparison.Ordinal) ||
            !string.Equals(sourceInstance, sourceInstanceId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            throw new PortableRestoreException("RESTORE_HANDOFF_INVALID", "恢复密钥交接路径或来源身份无效。");
        }

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var property in tablesElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out var count) || count < 0 ||
                string.IsNullOrWhiteSpace(property.Name) || !counts.TryAdd(property.Name, count))
            {
                throw new PortableRestoreException("RESTORE_RESULT_INVALID", "恢复表行数摘要无效。");
            }
        }

        // "files" counts only business assets, not the three archive payloads.
        // A database-only backup is valid; non-negative counts were checked above.
        if (counts.Count == 0)
        {
            throw new PortableRestoreException("RESTORE_RESULT_INVALID", "恢复结果缺少业务表或文件证据。");
        }

        return new PortableBusinessRestoreResult(
            instanceId,
            sourceInstance,
            archiveSha256,
            schemaVersion,
            counts,
            fileCount,
            revoked,
            normalizedHandoff,
            status);
    }

    private static bool TryGetString(JsonElement frame, string property, out string value)
    {
        if (frame.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String &&
            element.GetString() is { } text && text.Length is > 0 and <= 4096)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, int maximumBytes, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maximumBytes, 4096));
        var one = new char[1];
        while (true)
        {
            var count = await reader.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (builder.Length == 0)
                {
                    return null;
                }

                throw new PortableRestoreException("RESTORE_PROTOCOL_INVALID", "恢复辅助进程响应缺少行结束符。");
            }

            if (one[0] == '\n')
            {
                if (builder.Length > 0 && builder[^1] == '\r')
                {
                    builder.Length--;
                }

                return builder.ToString();
            }

            if (builder.Length >= maximumBytes)
            {
                throw new PortableRestoreException("RESTORE_PROTOCOL_OVERSIZED", "恢复辅助进程响应超过允许大小。");
            }

            builder.Append(one[0]);
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "." || (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathFullyQualified(relative));
    }
}
