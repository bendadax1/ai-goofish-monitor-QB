using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiGoofish.Launcher.Platform.Windows;

internal sealed record PostgresInstallation(
    string Root,
    string BinDirectory,
    string PostgresPath,
    string InitDbPath,
    string PgCtlPath,
    string PgIsReadyPath)
{
    public static PostgresInstallation Validate(string root, string expectedVersion)
    {
        var normalizedRoot = Path.GetFullPath(root);
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException("PostgreSQL 安装根必须是存在的绝对路径。");
        }

        var bin = Path.Combine(normalizedRoot, "bin");
        var installation = new PostgresInstallation(
            normalizedRoot,
            bin,
            Path.Combine(bin, "postgres.exe"),
            Path.Combine(bin, "initdb.exe"),
            Path.Combine(bin, "pg_ctl.exe"),
            Path.Combine(bin, "pg_isready.exe"));
        foreach (var path in new[]
        {
            installation.PostgresPath,
            installation.InitDbPath,
            installation.PgCtlPath,
            installation.PgIsReadyPath,
        })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("PostgreSQL 发行组件不完整。", path);
            }
        }

        var version = PostgresCommand.RunAsync(
                installation.PostgresPath,
                bin,
                new[] { "--version" },
                TimeSpan.FromSeconds(5),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        if (version.ExitCode != 0 ||
            !version.StandardOutput.Contains($"{expectedVersion}", StringComparison.Ordinal))
        {
            throw new PostgresLifecycleException("PostgreSQL 二进制版本与锁定组合不匹配。");
        }

        return installation;
    }
}

internal sealed record PostgresCommandResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed record PostgresHelperIdentity(int ProcessId, DateTimeOffset StartedAtUtc, string ExecutablePath);

internal static class PostgresCommand
{
    private const int MaximumCapturedCharacters = 32 * 1024;
    private static readonly string[] EnvironmentAllowList =
    {
        "COMSPEC",
        "NUMBER_OF_PROCESSORS",
        "PATH",
        "PATHEXT",
        "PROCESSOR_ARCHITECTURE",
        "SystemRoot",
        "TEMP",
        "TMP",
        "WINDIR",
    };

    public static async Task<PostgresCommandResult> RunAsync(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<PostgresHelperIdentity>? onStarted = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executablePath),
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var name in EnvironmentAllowList)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new PostgresLifecycleException("PostgreSQL 辅助命令未创建进程。");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new PostgresLifecycleException("PostgreSQL 辅助命令启动失败。", exception);
        }

        onStarted?.Invoke(new PostgresHelperIdentity(
            process.Id,
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
            Path.GetFullPath(executablePath)));
        using var readerCancellation = new CancellationTokenSource();
        var outputTask = ReadCappedAsync(process.StandardOutput, readerCancellation.Token);
        var errorTask = ReadCappedAsync(process.StandardError, readerCancellation.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return new PostgresCommandResult(process.ExitCode, output, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PostgresLifecycleException("PostgreSQL 辅助命令超时；未强制终止该进程。");
        }
        finally
        {
            if (!outputTask.IsCompleted || !errorTask.IsCompleted)
            {
                readerCancellation.Cancel();
            }

            try
            {
                await Task.WhenAll(outputTask, errorTask)
                    .WaitAsync(TimeSpan.FromSeconds(1))
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException)
            {
                // 读取任务已被观察；辅助进程身份由调用方在启动回调中持久化。
            }
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[512];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remaining = MaximumCapturedCharacters - result.Length;
            if (remaining > 0)
            {
                result.Append(buffer, 0, Math.Min(remaining, read));
            }
        }

        return result.ToString();
    }
}

internal sealed class PgCtlSmartStopProtocol : IProcessStopProtocol
{
    private readonly string _pgCtlPath;
    private readonly string _dataDirectory;

    public PgCtlSmartStopProtocol(string pgCtlPath, string dataDirectory)
    {
        _pgCtlPath = Path.GetFullPath(pgCtlPath);
        _dataDirectory = Path.GetFullPath(dataDirectory);
    }

    public async Task RequestStopAsync(OwnedProcessIdentity identity, CancellationToken cancellationToken)
    {
        var result = await PostgresCommand.RunAsync(
            _pgCtlPath,
            Path.GetDirectoryName(_pgCtlPath)!,
            new[] { "stop", "-D", _dataDirectory, "-m", "smart", "-W" },
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException($"pg_ctl smart stop 请求失败，退出码 {result.ExitCode}。");
        }
    }
}

internal static class SecurePasswordFile
{
    public static void WriteNew(string path, string password)
    {
        var normalizedPath = Path.GetFullPath(path);
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var currentUser = identity.User
            ?? throw new PostgresLifecycleException("无法确定当前 Windows 用户 SID，未写 initdb 口令文件。");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        using var stream = new FileInfo(normalizedPath).Create(
            FileMode.CreateNew,
            FileSystemRights.FullControl,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough,
            security);

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            stream.Write(passwordBytes);
            stream.WriteByte((byte)'\n');
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }
}

internal static class PostgresPathGuard
{
    // The locked Windows PostgreSQL build still uses MAX_PATH-limited native
    // file/directory operations. Reserve room for the unique password filename
    // and PostgreSQL's own subdirectories; .NET long-path support is insufficient.
    internal const int MaximumNewInstanceRootLength = 198;

    internal static void EnsureNewInstancePathLength(string instanceRoot)
    {
        if (Path.GetFullPath(instanceRoot).Length > MaximumNewInstanceRootLength)
            throw new PostgresLifecycleException(
                "实例目录过长，内置 PostgreSQL 无法可靠初始化。请将便携包放到更短的目录后重试；未启动初始化，也未修改现有数据库。");
    }

    public static void EnsureSafePath(InstanceDataRootLease lease, string path, bool allowMissingLeaf)
    {
        lease.EnsureHeld();
        var root = Path.GetFullPath(lease.InstanceRoot);
        var target = Path.GetFullPath(path);
        if (target != root && !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new PostgresLifecycleException("PostgreSQL 路径超出当前实例数据根。");
        }

        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PostgresLifecycleException("无法核验 PostgreSQL 实例数据根。", exception);
        }

        if ((rootAttributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
        {
            throw new PostgresLifecycleException("PostgreSQL 实例数据根不是安全的普通目录。");
        }

        var current = root;
        foreach (var segment in target[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                if (allowMissingLeaf)
                {
                    return;
                }

                throw new PostgresLifecycleException("PostgreSQL 必需路径不存在。", exception);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new PostgresLifecycleException("无法核验 PostgreSQL 路径安全性。", exception);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PostgresLifecycleException("PostgreSQL 路径包含重解析点。");
            }
        }
    }
}

internal static class PostgresStateFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static T Read<T>(string path, int maximumBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > maximumBytes)
            {
                throw new PostgresLifecycleException("PostgreSQL 状态文件大小无效。");
            }

            return JsonSerializer.Deserialize<T>(stream, Options)
                ?? throw new PostgresLifecycleException("PostgreSQL 状态文件内容为空。");
        }
        catch (PostgresLifecycleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new PostgresLifecycleException("PostgreSQL 状态文件无效或不可读。", exception);
        }
    }

    public static void WriteNew<T>(string path, T value) => Write(path, value, overwrite: false);

    public static void WriteReplace<T>(string path, T value) => Write(path, value, overwrite: true);

    private static void Write<T>(string path, T value, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new PostgresLifecycleException("无法解析 PostgreSQL 状态目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, Options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Exception? commitError = null;
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                commitError = cleanupException;
            }

            if (commitError is not null)
            {
                throw new PostgresLifecycleException(
                    "PostgreSQL 状态文件原子提交与唯一暂存清理均失败。",
                    new AggregateException(exception, commitError));
            }

            throw new PostgresLifecycleException("PostgreSQL 状态文件原子提交失败。", exception);
        }
    }
}
