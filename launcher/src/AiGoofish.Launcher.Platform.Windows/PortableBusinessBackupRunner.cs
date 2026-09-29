using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiGoofish.Launcher.Platform.Windows;

public sealed record PortableBusinessBackupResult(string Destination, string Sha256, int FileCount);

internal static class PortableBusinessBackupRunner
{
    private static readonly TimeSpan BackupTimeout = TimeSpan.FromMinutes(30);

    internal static void ValidateDestination(string dataRoot, string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination))
        {
            throw new InvalidOperationException("备份目标必须是绝对路径。");
        }
        var full = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException("备份目标目录无效。");
        if (!Directory.Exists(parent) || File.Exists(full) || Directory.Exists(full))
        {
            throw new InvalidOperationException("备份目标必须是已有目录内的新文件，不覆盖既有内容。");
        }
        var relative = Path.GetRelativePath(dataRoot, full);
        if (relative != "." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            relative != ".." && !Path.IsPathFullyQualified(relative))
        {
            throw new InvalidOperationException("备份目标不得位于实例数据根内。");
        }
        for (var path = parent; path is not null; path = Path.GetDirectoryName(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("备份目标路径包含重解析点。");
            }
        }
    }

    internal static async Task<PortableBusinessBackupResult> RunAsync(
        PortableBundleDescriptor bundle,
        InstanceDataRootLease lease,
        InstanceSecrets secrets,
        int postgresPort,
        string destination,
        string passphrase,
        Func<CancellationToken, Task> verifyQuiesced,
        CancellationToken cancellationToken)
    {
        ValidateDestination(lease.InstanceRoot, destination);
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
        foreach (var value in new[] { "-I", "-B", Path.Combine(bundle.ProgramRoot, "scripts", "portable", "python-bootstrap.py"),
                     "--app-root", bundle.ProgramRoot, "--target", "backup" })
        {
            start.ArgumentList.Add(value);
        }
        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                start.Environment[name] = value;
            }
        }
        var temp = Path.Combine(lease.InstanceRoot, "cache", "temp");
        start.Environment["TEMP"] = temp;
        start.Environment["TMP"] = temp;
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("备份辅助进程未启动。");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("无法启动内置 Python 备份入口。", exception);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(BackupTimeout);
        var stderrDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
        try
        {
            var request = new Dictionary<string, object>
            {
                ["type"] = "backup",
                ["data_root"] = lease.InstanceRoot,
                ["postgres_root"] = bundle.PostgresRoot,
                ["pgdata"] = Path.Combine(lease.InstanceRoot, "postgres", "cluster"),
                ["instance_id"] = lease.InstanceId.ToString("D"),
                ["app_version"] = bundle.AppVersion,
                ["admin_dsn"] = $"postgresql://aigoofish_bootstrap:{Uri.EscapeDataString(secrets.PostgresBootstrapAdminPassword)}@127.0.0.1:{postgresPort}/aigoofish",
                ["recovery_keys"] = new Dictionary<string, string>
                {
                    ["encryption_master_key"] = secrets.EncryptionMasterKey,
                    ["secret_key"] = secrets.SecretKey,
                },
                ["destination"] = Path.GetFullPath(destination),
                ["passphrase"] = passphrase,
                ["session"] = session,
            };
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            request.Clear();
            var checks = 0;
            string? digest = null;
            var fileCount = 0;
            while (true)
            {
                var line = await PortableSchemaMigrationRunner.ReadBoundedLineAsync(
                    process.StandardOutput, deadline.Token).ConfigureAwait(false);
                if (line is null)
                {
                    throw new InvalidOperationException("备份辅助进程协议中断或输出超限。");
                }
                using var document = JsonDocument.Parse(line);
                var frame = document.RootElement;
                if (frame.ValueKind != JsonValueKind.Object ||
                    !frame.TryGetProperty("type", out var kind) ||
                    !frame.TryGetProperty("session", out var echoedSession) ||
                    echoedSession.GetString() != session)
                {
                    throw new InvalidOperationException("备份辅助进程协议身份不匹配。");
                }
                if (kind.GetString() == "verify")
                {
                    if (!frame.TryGetProperty("counter", out var counter) || counter.GetInt32() != checks + 1 || checks >= 3)
                    {
                        throw new InvalidOperationException("备份停写复核次数或顺序无效。");
                    }
                    await verifyQuiesced(deadline.Token).ConfigureAwait(false);
                    checks++;
                    await process.StandardInput.WriteLineAsync(
                        JsonSerializer.Serialize(new { type = "verified", session, counter = checks }).AsMemory(),
                        deadline.Token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                    continue;
                }
                if (kind.GetString() == "complete" && checks == 3 &&
                    frame.TryGetProperty("sha256", out var hash) &&
                    frame.TryGetProperty("files", out var files))
                {
                    digest = hash.GetString();
                    fileCount = files.GetInt32();
                    break;
                }
                throw new InvalidOperationException("备份辅助进程未完成强制停写复核。");
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await stderrDrain.ConfigureAwait(false);
            if (process.ExitCode != 0 || digest is null || digest.Length != 64 ||
                digest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                fileCount < 3 || !File.Exists(destination) ||
                (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("备份辅助进程未交付可验证的全新加密包。");
            }
            using var archive = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(archive, deadline.Token).ConfigureAwait(false)).ToLowerInvariant();
            if (!string.Equals(actual, digest, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("加密备份 SHA-256 复核失败。");
            }
            return new PortableBusinessBackupResult(Path.GetFullPath(destination), digest, fileCount);
        }
        finally
        {
            if (!process.HasExited)
            {
                // Let the Python finally block remove its own ACL-protected
                // plaintext stage after a refused proof or broken pipe.
                try
                {
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // A broken pipe means the child already closed its reader.
                }
                try
                {
                    using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(cleanupDeadline.Token).ConfigureAwait(false);
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
