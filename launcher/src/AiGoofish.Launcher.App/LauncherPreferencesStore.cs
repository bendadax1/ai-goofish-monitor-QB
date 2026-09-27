using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace AiGoofish.Launcher.App;

public sealed record LauncherPreferences(
    bool OpenManagementPage = true,
    bool AutoStartOnLauncherOpen = false,
    WindowCloseChoice CloseChoice = WindowCloseChoice.Ask);

public interface ILauncherPreferencesStore
{
    LauncherPreferences Load();
    bool TrySave(LauncherPreferences preferences);
    string? LoadWarning { get; }
}

/// <summary>Stores non-business Launcher preferences beside the portable data container.</summary>
public sealed class LauncherPreferencesStore : ILauncherPreferencesStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly SemaphoreSlim IoGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly string _legacyCloseChoicePath;
    private readonly string _mutexName;
    private readonly string _bundleRoot;
    private string? _loadWarning;

    public LauncherPreferencesStore(string bundleRoot, string? legacyCloseChoicePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleRoot);
        _bundleRoot = Path.GetFullPath(bundleRoot);
        var dataRoot = Path.Combine(_bundleRoot, "data", "launcher");
        _path = Path.Combine(dataRoot, "preferences.json");
        _legacyCloseChoicePath = legacyCloseChoicePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AiGoofishLauncher",
                "close-choice.txt");
        _mutexName = $"Local\\AiGoofishLauncherPreferences-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())))}";
    }

    public string? LoadWarning => _loadWarning;

    public LauncherPreferences Load()
    {
        _loadWarning = null;
        IoGate.Wait();
        try
        {
            using var mutex = AcquireFileMutex();
            EnsureSafePreferencePath();
            if (File.Exists(_path))
            {
                try
                {
                    var bytes = File.ReadAllBytes(_path);
                    ValidateDocumentShape(bytes);
                    var document = JsonSerializer.Deserialize<PreferencesDocument>(bytes, JsonOptions);
                    if (document is null || document.SchemaVersion != CurrentSchemaVersion ||
                        !IsValidCloseChoice(document.CloseChoice))
                    {
                        return InvalidConfiguration();
                    }

                    return new LauncherPreferences(
                        document.OpenManagementPage,
                        document.AutoStartOnLauncherOpen,
                        Enum.Parse<WindowCloseChoice>(document.CloseChoice, ignoreCase: false));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    return InvalidConfiguration(exception);
                }
            }

            var imported = LoadLegacyCloseChoice();
            if (imported is not null)
            {
                var migrated = new LauncherPreferences(CloseChoice: imported.Value);
                if (!TrySaveCore(migrated))
                {
                    _loadWarning = "旧版关闭偏好未能迁移；本次使用安全默认值。";
                    return new LauncherPreferences();
                }

                return migrated;
            }

            return new LauncherPreferences();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"读取 Launcher 本地偏好失败，已采用安全默认值；异常类型：{exception.GetType().Name}");
            _loadWarning = "本地偏好无法读取；本次采用安全默认值，自动启动已关闭。";
            return new LauncherPreferences();
        }
        finally
        {
            IoGate.Release();
        }
    }

    public bool TrySave(LauncherPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (!IsValidCloseChoice(preferences.CloseChoice.ToString()))
        {
            return false;
        }

        IoGate.Wait();
        try
        {
            using var mutex = AcquireFileMutex();
            var succeeded = TrySaveCore(preferences);
            if (!succeeded)
            {
                System.Diagnostics.Trace.TraceWarning("保存 Launcher 本地偏好失败；检查目录权限和磁盘空间。异常详情已省略。");
            }
            return succeeded;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError($"获取 Launcher 偏好写锁失败；异常类型：{exception.GetType().Name}");
            return false;
        }
        finally
        {
            IoGate.Release();
        }
    }

    private static void ValidateDocumentShape(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var uniqueProperties = root.ValueKind is JsonValueKind.Object && root.EnumerateObject().All(property => names.Add(property.Name));
        if (root.ValueKind is not JsonValueKind.Object || !uniqueProperties || names.Count != 4 ||
            !root.TryGetProperty("schemaVersion", out var version) || version.ValueKind is not JsonValueKind.Number ||
            !root.TryGetProperty("openManagementPage", out var open) || open.ValueKind is not JsonValueKind.True and not JsonValueKind.False ||
            !root.TryGetProperty("autoStartOnLauncherOpen", out var autoStart) || autoStart.ValueKind is not JsonValueKind.True and not JsonValueKind.False ||
            !root.TryGetProperty("closeChoice", out var close) || close.ValueKind is not JsonValueKind.String)
        {
            throw new JsonException("Launcher 偏好字段缺失或类型错误。");
        }
    }

    private void EnsureSafePreferencePath()
    {
        var paths = new[]
        {
            _bundleRoot,
            Path.Combine(_bundleRoot, "data"),
            Path.Combine(_bundleRoot, "data", "launcher"),
            _path,
        };
        foreach (var path in paths)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) is not 0)
                {
                    throw new UnauthorizedAccessException("Launcher 偏好路径包含重解析点。");
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Missing data directories and the first preferences file are valid.
            }
        }
    }

    private bool TrySaveCore(LauncherPreferences preferences)
    {
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            EnsureSafePreferencePath();
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $"preferences.{Guid.NewGuid():N}.tmp");
            var document = new PreferencesDocument(
                CurrentSchemaVersion,
                preferences.OpenManagementPage,
                preferences.AutoStartOnLauncherOpen,
                preferences.CloseChoice.ToString());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            temporaryPath = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            System.Diagnostics.Trace.TraceError($"写入 Launcher 本地偏好失败；异常类型：{exception.GetType().Name}");
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Trace.TraceWarning($"移除未完成的偏好临时文件失败；异常类型：{exception.GetType().Name}");
                }
            }
        }
    }

    private MutexLease AcquireFileMutex()
    {
        var mutex = new Mutex(initiallyOwned: false, _mutexName);
        try
        {
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                throw new IOException("Launcher 偏好文件正被另一个进程占用。");
            }
            return new MutexLease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                mutex.ReleaseMutex();
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }

    private WindowCloseChoice? LoadLegacyCloseChoice()
    {
        try
        {
            if (!File.Exists(_legacyCloseChoicePath)) return null;
            var text = File.ReadAllText(_legacyCloseChoicePath, Encoding.UTF8);
            return Enum.TryParse<WindowCloseChoice>(text, out var choice) &&
                choice is WindowCloseChoice.Background or WindowCloseChoice.Exit ? choice : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"读取旧版关闭偏好失败；异常类型：{exception.GetType().Name}");
            return null;
        }
    }

    private LauncherPreferences InvalidConfiguration(Exception? exception = null)
    {
        if (exception is not null)
        {
            System.Diagnostics.Trace.TraceWarning($"Launcher 本地偏好无效，已回退安全默认值；异常类型：{exception.GetType().Name}");
        }
        else
        {
            System.Diagnostics.Trace.TraceWarning("Launcher 本地偏好版本或字段无效，已回退安全默认值。");
        }
        _loadWarning = "本地偏好文件无法读取或版本不受支持；本次采用安全默认值，自动启动已关闭。";
        return new LauncherPreferences();
    }

    private static bool IsValidCloseChoice(string? value) =>
        value is nameof(WindowCloseChoice.Ask) or nameof(WindowCloseChoice.Background) or nameof(WindowCloseChoice.Exit);

    private sealed record PreferencesDocument(
        int SchemaVersion,
        bool OpenManagementPage,
        bool AutoStartOnLauncherOpen,
        string CloseChoice);
}
