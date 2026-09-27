using System.Text.Json;
using System.Text.RegularExpressions;
using AiGoofish.Launcher.Platform.Windows;

namespace AiGoofish.Launcher.App;

/// <summary>Explicit development layout; never falls back from failed release verification.</summary>
internal sealed class DevelopmentLaunchRequest
{
    private const string MarkerName = ".launcher-development.json";
    private readonly string _bundleRoot;

    public DevelopmentLaunchRequest(string repositoryRoot, string bundleRoot, string sessionId)
    {
        if (!Path.IsPathFullyQualified(repositoryRoot) || !Path.IsPathFullyQualified(bundleRoot))
            throw new ArgumentException("开发入口要求绝对仓库路径和组件包路径。");
        if (!Regex.IsMatch(sessionId, "^[a-z0-9][a-z0-9-]{0,47}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("开发会话标识只允许小写字母、数字和短横线。");
        var repository = Path.GetFullPath(repositoryRoot);
        if (!File.Exists(Path.Combine(repository, "launcher", "global.json")) || !File.Exists(Path.Combine(repository, "AGENTS.md")))
            throw new InvalidOperationException("开发入口要求明确的源码仓库，不能指向发行包或业务目录。");
        _bundleRoot = Path.GetFullPath(bundleRoot);
        var candidates = Path.Combine(repository, "launcher", "dist") + Path.DirectorySeparatorChar;
        if (!_bundleRoot.StartsWith(candidates, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("开发组件来源必须是本仓库 launcher/dist 下已验证的候选目录。");
        SessionRoot = Path.Combine(repository, ".tmp", "tests", "launcher-development", sessionId);
        EnsurePlainPath(SessionRoot);
        EnsurePlainPath(_bundleRoot);
    }

    public string SessionRoot { get; }

    public async Task<PortableBundleDescriptor> LoadAsync()
    {
        // Verify the frozen runtime inputs, then only redirect mutable instance state.
        // The freshly compiled C# App/Core/Windows assemblies remain in the build output.
        var verified = await PortableBundleDescriptor.LoadAndVerifyAsync(_bundleRoot).ConfigureAwait(false);
        EnsurePlainPath(SessionRoot);
        var markerPath = Path.Combine(SessionRoot, MarkerName);
        if (Directory.Exists(SessionRoot))
        {
            if (!File.Exists(markerPath) || new FileInfo(markerPath).Length > 4096)
                throw new InvalidOperationException("已有开发目录缺少可信标记；不会接管或清空该目录。");
            EnsurePlainPath(markerPath);
            using var marker = JsonDocument.Parse(await File.ReadAllBytesAsync(markerPath).ConfigureAwait(false));
            if (marker.RootElement.GetProperty("formatVersion").GetInt32() != 1 ||
                marker.RootElement.GetProperty("releaseId").GetString() != verified.ReleaseId)
                throw new InvalidOperationException("开发会话与组件候选不匹配；请使用新的会话标识。");
        }
        else
        {
            Directory.CreateDirectory(SessionRoot);
            EnsurePlainPath(SessionRoot);
            await using var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await marker.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, releaseId = verified.ReleaseId })).ConfigureAwait(false);
            await marker.FlushAsync().ConfigureAwait(false);
        }
        return verified with { BundleRoot = SessionRoot };
    }

    private static void EnsurePlainPath(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("开发路径不允许符号链接或重解析点。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
