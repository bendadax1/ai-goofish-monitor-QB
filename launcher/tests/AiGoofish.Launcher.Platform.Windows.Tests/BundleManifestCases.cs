using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using AiGoofish.Launcher.Core;
using System.Text;
using System.Text.Json;
using AiGoofish.Launcher.Platform.Windows;

internal static class BundleManifestCases
{
    public static async Task TestPreflightFailureDiagnosticsAsync(ProcessTestWorkspace workspace)
    {
        var root = workspace.CreateCaseRoot("bundle-diagnostic-privacy");
        Directory.CreateDirectory(root);
        async Task Expect(string path, StartupFailureKind kind, StartupFileRole role, int? native = null)
        {
            try { await PortableBundleDescriptor.LoadAndVerifyAsync(path); }
            catch (PortableBundleException error)
            {
                Console.WriteLine($"PREFLIGHT_CASE expected={kind}/{role} actual={error.FailureKind}/{error.FileRole} hresult=0x{error.HResult:X8}");
                Assert(error.FailureKind == kind && error.FileRole == role, "预检必须保留准确失败分类与文件角色");
                using var journal = new StartupDiagnosticSession();
                journal.Record(StartupStage.PackageVerification, StartupEventKind.Failed, exception: error);
                var entry = journal.Snapshot().Single();
                Assert(entry.Failures[0].Kind == kind && entry.Failures[0].FileRole == role,
                    "日志不得把预检原因折叠为 InvalidState");
                if (native is not null) Assert(entry.Failures[0].NativeCode == native, "必须保留系统错误码");
                Assert(!JsonSerializer.Serialize(entry).Contains(root, StringComparison.OrdinalIgnoreCase) &&
                    !error.Message.Contains(root, StringComparison.OrdinalIgnoreCase), "错误提示与日志不得包含任意路径");
                if (kind == StartupFailureKind.AccessDenied)
                    Assert(error.Message.Contains("访问被拒绝") && !error.Message.Contains("不存在"), "拒绝访问不能误报缺失");
                return;
            }
            throw new InvalidOperationException("预期预检失败");
        }
        await Expect(Path.Combine(root, "missing"), StartupFailureKind.MissingFile, StartupFileRole.BundleRoot);
        await Expect(root, StartupFailureKind.MissingFile, StartupFileRole.CurrentMetadata, 2);
        var fixture = BundleFixture.Create(root);
        var current = Path.Combine(root, "current.json");
        var currentBytes = File.ReadAllBytes(current);
        File.WriteAllText(current, "invalid-json", new UTF8Encoding(false));
        await Expect(root, StartupFailureKind.InvalidData, StartupFileRole.CurrentMetadata);
        File.WriteAllBytes(current, currentBytes);
        using (var locked = new FileStream(current, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Expect(root, StartupFailureKind.Io, StartupFileRole.CurrentMetadata, 32);

        // Only this newly-created synthetic file: deny content reads, preserve
        // metadata and WRITE_DAC, then restore the exact original DACL in finally.
        var info = new FileInfo(current);
        var originalAcl = info.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
        var deniedAcl = info.GetAccessControl(AccessControlSections.Access);
        var identity = WindowsIdentity.GetCurrent().User ?? throw new IOException("Missing test identity");
        deniedAcl.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.ReadData, AccessControlType.Deny));
        try
        {
            info.SetAccessControl(deniedAcl);
            await Expect(root, StartupFailureKind.AccessDenied, StartupFileRole.CurrentMetadata, 5);
        }
        finally
        {
            var restoredAcl = new FileSecurity();
            restoredAcl.SetSecurityDescriptorBinaryForm(originalAcl, AccessControlSections.Access);
            info.SetAccessControl(restoredAcl);
        }
        File.WriteAllText(Path.Combine(root, "app", fixture.AppId, "web_server.py"), "WEB", new UTF8Encoding(false));
        await Expect(root, StartupFailureKind.InvalidData, StartupFileRole.BundleFile);
    }

    public static async Task TestWindowsPathsAndCompleteManifestAsync(ProcessTestWorkspace workspace)
    {
        var fixture = BundleFixture.Create(workspace.CreateCaseRoot("bundle-windows-paths"), windowsPaths: true);
        fixture.AddManifestFile("createdump.exe", "fixture of the manifest-covered .NET runtime helper");
        var descriptor = await PortableBundleDescriptor.LoadAndVerifyAsync(fixture.Root);
        Assert(descriptor.ProgramRoot == Path.Combine(fixture.Root, "app", fixture.AppId), "Windows 转义路径必须解析到 app 组件");
        Assert(fixture.Files.Any(path => path == "THIRD_PARTY_LICENSE_GAPS.md"), "根 notice 必须纳入 manifest");
        Assert(fixture.Files.Any(path => path.StartsWith("third-party-notices/", StringComparison.Ordinal)), "第三方 notices 目录必须纳入 manifest");
    }

    public static async Task TestUnauthorizedExecutableRejectedAsync(ProcessTestWorkspace workspace)
    {
        var fixture = BundleFixture.Create(workspace.CreateCaseRoot("bundle-extra-executable"));
        fixture.AddManifestFile("tools/injected.exe", "not an executable");
        await AssertThrowsAsync<PortableBundleException>(
            () => PortableBundleDescriptor.LoadAndVerifyAsync(fixture.Root));
    }

    public static async Task TestDataEntryRejectedAsync(ProcessTestWorkspace workspace)
    {
        var fixture = BundleFixture.Create(workspace.CreateCaseRoot("bundle-data-entry"));
        fixture.AddManifestFile("data/cache/payload.txt", "must not be packaged");
        await AssertThrowsAsync<PortableBundleException>(
            () => PortableBundleDescriptor.LoadAndVerifyAsync(fixture.Root));
    }

    public static Task TestLeasePrecedesInstanceWritesAsync(ProcessTestWorkspace workspace)
    {
        var root = workspace.CreateCaseRoot("host-lock-order");
        var dataRoot = Path.Combine(root, "data");
        using var competingLease = InstanceDataRootLease.Acquire(dataRoot);
        var descriptor = new PortableBundleDescriptor(
            root,
            "test-release",
            "1.0.0",
            1,
            1,
            Path.Combine(root, "app"),
            Path.Combine(root, "runtime", "python.exe"),
            Path.Combine(root, "browser"),
            Path.Combine(root, "postgres"),
            Path.Combine(root, "AiGoofish.Launcher.App.exe"));

        AssertThrows<InstanceAlreadyLockedException>(() => RealPortableStackHost.Create(descriptor));
        Assert(!Directory.Exists(Path.Combine(dataRoot, "cache")), "锁冲突前不得创建 cache");
        Assert(!Directory.Exists(Path.Combine(dataRoot, "logs")), "锁冲突前不得创建 logs");
        return Task.CompletedTask;
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"预期抛出 {typeof(TException).Name}。");
    }

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"预期抛出 {typeof(TException).Name}。");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class BundleFixture
    {
        private readonly object _current;
        private readonly bool _windowsPaths;
        private readonly List<string> _files;

        private BundleFixture(string root, string appId, object current, bool windowsPaths, List<string> files)
        {
            Root = root;
            AppId = appId;
            _current = current;
            _windowsPaths = windowsPaths;
            _files = files;
        }

        public string Root { get; }

        public string AppId { get; }

        public IReadOnlyList<string> Files => _files;

        public static BundleFixture Create(string root, bool windowsPaths = false)
        {
            const string appId = "app-1.0.0-test";
            const string runtimeId = "python-test";
            const string browserId = "browser-test";
            const string postgresId = "postgres-test";
            static string CurrentPath(string value, bool useWindowsPaths) =>
                useWindowsPaths ? value.Replace('/', '\\') : value;

            var current = new
            {
                format_version = 1,
                release_status = "preview-integration",
                release_id = "test-release",
                platform = "windows-x64",
                app = new { exact_id = appId, relative_dir = CurrentPath($"app/{appId}", windowsPaths), version = "1.0.0", schema_min = 1, schema_max = 1 },
                runtime = new { exact_id = runtimeId, relative_dir = CurrentPath($"runtime/{runtimeId}", windowsPaths) },
                browser = new { exact_id = browserId, relative_dir = CurrentPath($"browsers/{browserId}", windowsPaths) },
                postgres = new { exact_id = postgresId, relative_dir = CurrentPath($"postgres/{postgresId}", windowsPaths) },
                launcher = new { exact_id = "launcher-test", relative_dir = ".", executable = "AiGoofish.Launcher.App.exe" },
            };
            var fixture = new BundleFixture(root, appId, current, windowsPaths, new List<string>());
            fixture.WriteFile("AiGoofish.Launcher.App.exe", "launcher");
            fixture.WriteFile("AiGoofish.Launcher.App.dll", "launcher library");
            fixture.WriteFile($"app/{appId}/scripts/portable/python-bootstrap.py", "bootstrap");
            fixture.WriteFile($"app/{appId}/web_server.py", "web");
            fixture.WriteFile($"runtime/{runtimeId}/python.exe", "python");
            fixture.WriteFile($"runtime/{runtimeId}/python313.dll", "python dll");
            fixture.WriteFile($"runtime/{runtimeId}/python313.zip", "python stdlib");
            fixture.WriteFile($"runtime/{runtimeId}/python-runtime-manifest.json", "{}");
            fixture.WriteFile($"browsers/{browserId}/chrome-win64/chrome.exe", "chrome");
            fixture.WriteFile($"browsers/{browserId}/browser-runtime-manifest.json", "{}");
            fixture.WriteFile($"postgres/{postgresId}/bin/postgres.exe", "postgres");
            fixture.WriteFile("THIRD_PARTY_LICENSE_GAPS.md", "preview gaps");
            fixture.WriteFile("third-party-notices/inventory.json", "{}");
            fixture.WriteFile("third-party-notices/python/LICENSE.txt", "license");
            fixture.WriteMetadata();
            return fixture;
        }

        public void AddManifestFile(string relativePath, string content)
        {
            WriteFile(relativePath, content);
            WriteMetadata();
        }

        private void WriteFile(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _files.Add(relativePath);
        }

        private void WriteMetadata()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(
                Path.Combine(Root, "current.json"),
                JsonSerializer.Serialize(_current),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var entries = _files.Select(relativePath =>
            {
                var diskPath = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var bytes = File.ReadAllBytes(diskPath);
                return new
                {
                    path = _windowsPaths ? relativePath.Replace('/', '\\') : relativePath,
                    size = bytes.LongLength,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                };
            }).ToArray();
            var manifest = new
            {
                format_version = 1,
                release_status = "preview-integration",
                current = _current,
                files = entries,
            };
            File.WriteAllText(
                Path.Combine(Root, "bundle-manifest.json"),
                JsonSerializer.Serialize(manifest),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}
