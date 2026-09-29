using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiGoofish.Launcher.Platform.Windows;

public sealed record PortableSchemaUpgradeResult(PortableBusinessBackupResult Backup, int SchemaVersion, string Status);

internal static class PortableSchemaMigrationRunner
{
    private static readonly TimeSpan MigrationTimeout = TimeSpan.FromMinutes(10);

    internal static async Task<(int Version, string Status)> RunAsync(
        PortableBundleDescriptor bundle,
        InstanceDataRootLease lease,
        InstanceSecrets secrets,
        int postgresPort,
        PortableBusinessBackupResult backup,
        Func<CancellationToken, Task> verifyQuiesced,
        CancellationToken cancellationToken,
        Func<ProcessStartInfo, Process>? processFactory = null)
    {
        ArgumentNullException.ThrowIfNull(verifyQuiesced);
        lease.EnsureHeld();
        if (backup.Sha256.Length != 64 ||
            backup.Sha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            !File.Exists(backup.Destination))
            throw new InvalidOperationException("升级前备份证明无效。");

        var session = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var start = new ProcessStartInfo
        {
            FileName = bundle.PythonExecutable,
            WorkingDirectory = lease.InstanceRoot,
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
            "--app-root", bundle.ProgramRoot, "--target", "migrate",
        }) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                start.Environment[name] = value;
        var temporaryRoot = Path.Combine(lease.InstanceRoot, "cache", "temp");
        start.Environment["TEMP"] = temporaryRoot;
        start.Environment["TMP"] = temporaryRoot;
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = processFactory?.Invoke(start) ?? new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("升级辅助进程未启动。");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("无法启动内置 Python 升级入口。", exception);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MigrationTimeout);
        var stderrDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
        try
        {
            var request = new Dictionary<string, object>
            {
                ["type"] = "migrate",
                ["session"] = session,
                ["data_root"] = lease.InstanceRoot,
                ["pgdata"] = Path.Combine(lease.InstanceRoot, "postgres", "cluster"),
                ["instance_id"] = lease.InstanceId.ToString("D"),
                ["admin_dsn"] = $"postgresql://aigoofish_bootstrap:{Uri.EscapeDataString(secrets.PostgresBootstrapAdminPassword)}@127.0.0.1:{postgresPort}/aigoofish",
                ["admin_role"] = "aigoofish_bootstrap",
                ["app_role"] = "aigoofish_app",
                ["probe_role"] = "aigoofish_probe",
                ["backup_file"] = backup.Destination,
                ["backup_sha256"] = backup.Sha256,
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
            request.Clear();
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.WriteAsync("\n"u8.ToArray(), deadline.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }

            var checks = 0;
            int? version = null;
            string? status = null;
            while (true)
            {
                var line = await ReadBoundedLineAsync(process.StandardOutput, deadline.Token).ConfigureAwait(false);
                if (line is null) break;
                using var document = JsonDocument.Parse(line);
                var frame = document.RootElement;
                if (frame.ValueKind != JsonValueKind.Object ||
                    !frame.TryGetProperty("type", out var kind) ||
                    !frame.TryGetProperty("session", out var echoed) || echoed.GetString() != session)
                    throw new InvalidOperationException("升级辅助进程协议身份不匹配。");
                if (kind.GetString() == "verify")
                {
                    if (frame.EnumerateObject().Count() != 3 || checks >= 3 ||
                        !frame.TryGetProperty("counter", out var counter) ||
                        counter.GetInt32() != checks + 1)
                        throw new InvalidOperationException("升级维护复核顺序无效。");
                    await verifyQuiesced(deadline.Token).ConfigureAwait(false);
                    checks++;
                    await process.StandardInput.WriteLineAsync(
                        JsonSerializer.Serialize(new { type = "verified", session, counter = checks }).AsMemory(),
                        deadline.Token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                    continue;
                }
                if (kind.GetString() == "complete" && version is null && checks == 3 &&
                    frame.EnumerateObject().Count() == 5 &&
                    frame.TryGetProperty("schema_version", out var versionElement) &&
                    versionElement.TryGetInt32(out var actualVersion) && actualVersion == 2 &&
                    frame.TryGetProperty("status", out var statusElement) &&
                    statusElement.GetString() is "applied" or "already_applied" &&
                    frame.TryGetProperty("checks", out var countElement) && countElement.GetInt32() == checks)
                {
                    version = actualVersion;
                    status = statusElement.GetString();
                    continue;
                }
                throw new InvalidOperationException("升级辅助进程返回无效响应。");
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await stderrDrain.ConfigureAwait(false);
            if (process.ExitCode != 0 || checks != 3 || version != 2 || status is null)
                throw new InvalidOperationException("升级未通过事务、备份和维护证明核验。");
            return (version.Value, status);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.StandardInput.Close(); }
                catch (IOException) { }
                using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false); }
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

    internal static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var one = new char[1];
        while (true)
        {
            var count = await reader.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) return result.Length == 0 ? null : result.ToString();
            if (one[0] == '\n') return result.ToString();
            if (result.Length == 4096)
                throw new InvalidOperationException("升级辅助进程输出超限。");
            result.Append(one[0]);
        }
    }
}
