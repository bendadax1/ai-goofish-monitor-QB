using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using AiGoofish.Launcher.Core;
using AiGoofish.Launcher.Platform.Windows;

internal static class RealPortableStack
{
    public static async Task<int> RunAsync()
    {
        var repository = Environment.GetEnvironmentVariable("AIGOOFISH_REPOSITORY_ROOT")
            ?? throw new InvalidOperationException("Missing explicit repository root");
        repository = Path.GetFullPath(repository);
        var parent = Path.Combine(repository, ".tmp", "tests", "portable-real-stack");
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var app = Path.Combine(root, "app", "fixture");
        var cache = Path.Combine(data, "cache");
        var python = Path.Combine(repository, ".tmp", "dependencies", "portable-python", "python-3.13.15-e67c6b779c81-windows-x64", "python.exe");
        var pgRoot = Path.Combine(repository, ".tmp", "dependencies", "portable-pg", "postgresql-17.11-3-windows-x64");
        var browser = Path.Combine(repository, ".tmp", "dependencies", "portable-browser", "chromium-1.57.0-r1200-4b4d412c65ff-win64");
        var pgdata = Path.Combine(data, "postgres", "cluster");
        InstanceDataRootLease? lease = null;
        WindowsPostgresComponent? postgres = null;
        WindowsPythonComponent? service = null;
        OwnedProcessIdentity? activePythonIdentity = null;
        var pythonStartAttempted = false;
        Process? helper = null;
        var pgStarted = false;
        var stage = "preflight";
        var success = false;
        try
        {
            var disk = new DriveInfo(Path.GetPathRoot(repository)!);
            if (disk.AvailableFreeSpace < 10L * 1024 * 1024 * 1024 || disk.AvailableFreeSpace < disk.TotalSize * .05)
                throw new IOException("low disk floor");
            EnsureNoReparse(repository);
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(app);
            // Copy program code only; never config, state, account data or runtimes.
            foreach (var file in Directory.EnumerateFiles(Path.Combine(repository, "src"), "*.py", SearchOption.AllDirectories))
                CopySource(repository, app, file);
            foreach (var file in new[] { "portable_server.py", "portable_schema.py", "portable_provision.py", "portable_web.py", "scripts/portable/python-bootstrap.py" })
                CopySource(repository, app, Path.Combine(repository, file));
            foreach (var directory in new[] { "static", "templates", "images" })
                Directory.CreateDirectory(Path.Combine(app, directory));
            await WriteSeedFixturesAsync(repository, app);
            // Normal service can import its real routes; test does not open business pages.
            lease = InstanceDataRootLease.Acquire(data);
            var secrets = new WindowsInstanceSecretsStore().CreateNew(lease, pgdata);
            var pgPort = Port();
            postgres = new WindowsPostgresComponent(lease, secrets, new WindowsPostgresOptions(
                pgRoot, pgdata, pgPort, "17.11", TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15)));
            stage = "postgres-init";
            await postgres.EnsureInitializedAsync();
            stage = "postgres-start";
            pgStarted = true;
            await postgres.StartAsync(CancellationToken.None);
            var bootstrap = Path.Combine(app, "scripts", "portable", "python-bootstrap.py");
            var versionLine = File.ReadLines(Path.Combine(app, "src", "version.py"))
                .First(line => line.StartsWith("VERSION = \"", StringComparison.Ordinal));
            var appVersion = versionLine.Split('"')[1];
            stage = "provision";
            var start = new ProcessStartInfo(python) {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = data,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "WINDIR", "PATH" })
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            start.Environment["TEMP"] = cache;
            start.Environment["TMP"] = cache;
            start.Environment["GOOFISH_PORTABLE_ADMIN_DATABASE_URL"] = Dsn("aigoofish_bootstrap", secrets.PostgresBootstrapAdminPassword, pgPort, "postgres");
            start.Environment["GOOFISH_PORTABLE_APP_DATABASE_PASSWORD"] = secrets.ApplicationDatabasePassword;
            start.Environment["GOOFISH_PORTABLE_PROBE_DATABASE_PASSWORD"] = secrets.ProbeDatabasePassword;
            foreach (var arg in new[] { "-I", "-B", bootstrap, "--app-root", app, "--target", "provision", "--", "--pgdata", pgdata, "--instance-id", lease.InstanceId.ToString("D") })
                start.ArgumentList.Add(arg);
            helper = Process.Start(start) ?? throw new IOException("provision process not started");
            var stdout = helper.StandardOutput.ReadToEndAsync();
            var stderr = helper.StandardError.ReadToEndAsync();
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
            await Task.WhenAll(stdout, stderr);
            if (helper.ExitCode != 0) throw new IOException("provision returned nonzero");
            helper.Dispose(); helper = null;
            var applicationDsn = Dsn("aigoofish_app", secrets.ApplicationDatabasePassword, pgPort, "aigoofish");
            var probeDsn = Dsn("aigoofish_probe", secrets.ProbeDatabasePassword, pgPort, "aigoofish");
            var setupToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            foreach (var mode in new[] { PythonServiceMode.Maintenance, PythonServiceMode.Normal })
            {
                var endpoints = new PythonDatabaseEndpoints(
                    applicationDsn,
                    probeDsn,
                    mode is PythonServiceMode.Normal ? setupToken : null);
                stage = "python-" + mode;
                var options = new WindowsPythonOptions(
                    python, bootstrap, app, data, cache, browser, Port(), appVersion, mode,
                    1, 2, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15));
                service = new WindowsPythonComponent(lease, secrets, options, endpoints);
                pythonStartAttempted = true;
                await service.StartAsync(CancellationToken.None);
                if (await service.GetStateAsync(CancellationToken.None) != ComponentRuntimeState.Running)
                    throw new IOException("real Python did not reach Running");

                var startedIdentity = service.ProcessSnapshot.Identity
                    ?? throw new IOException("real Python identity missing");
                activePythonIdentity = startedIdentity;
                await service.DisposeAsync(); service = null;
                service = new WindowsPythonComponent(lease, secrets, options, endpoints);
                var reconnected = await service.TryReconnectAsync(CancellationToken.None);
                if (!reconnected.Succeeded || service.ProcessSnapshot.Identity != startedIdentity)
                    throw new IOException("real Python reconnect failed");
                if (await service.GetStateAsync(CancellationToken.None) != ComponentRuntimeState.Running)
                    throw new IOException("reconnected real Python did not remain Ready");
                if (mode is PythonServiceMode.Normal && service.SetupToken != setupToken)
                    throw new IOException("normal reconnect did not restore setup token");
                if (mode is PythonServiceMode.Maintenance && service.SetupToken is not null)
                    throw new IOException("maintenance reconnect exposed setup token");
                await service.StopAsync(CancellationToken.None);
                if (await service.GetStateAsync(CancellationToken.None) != ComponentRuntimeState.Stopped)
                    throw new IOException("real Python did not stop");
                activePythonIdentity = null;
                pythonStartAttempted = false;
                await service.DisposeAsync(); service = null;
                Console.WriteLine("PASS real embedded Python " + mode + " dispose, reconnect, ready and exit");
            }
            stage = "postgres-stop";
            await postgres.StopAsync(CancellationToken.None);
            pgStarted = false;
            success = true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"REAL_STACK_FAILED stage={stage} category={exception.GetType().Name}");
            if (exception is PythonLifecycleException lifecycle)
                Console.Error.WriteLine($"REAL_STACK_SAFE_REASON={lifecycle.Message}");
        }
        finally
        {
            if (service is not null)
            {
                activePythonIdentity ??= service.ProcessSnapshot.Identity;
                var stopped = service.ProcessSnapshot.Identity is null
                    ? ComponentRuntimeState.Unknown
                    : await service.GetStateAsync(CancellationToken.None);
                if (stopped != ComponentRuntimeState.Stopped && service.ProcessSnapshot.Identity is not null)
                {
                    try { await service.StopAsync(CancellationToken.None); }
                    catch { success = false; Console.Error.WriteLine("REAL_STACK_PYTHON_CLEANUP_UNCONFIRMED"); }
                    stopped = await service.GetStateAsync(CancellationToken.None);
                }
                await service.DisposeAsync();
            }
            if (pythonStartAttempted && activePythonIdentity is null)
            {
                success = false;
                pgStarted = false; // Spawn outcome is unknown; preserve PG and fixture for diagnosis.
                Console.Error.WriteLine("REAL_STACK_PYTHON_START_OUTCOME_UNKNOWN");
            }
            if (activePythonIdentity is not null)
            {
                var runState = PythonProcessIdentityProbe.GetRunState(activePythonIdentity);
                if (runState is PythonIdentityRunState.NotRunning)
                {
                    activePythonIdentity = null;
                }
                else
                {
                    success = false;
                    pgStarted = false; // Never stop PG or remove files beneath a live/unknown Python process.
                    Console.Error.WriteLine("REAL_STACK_PYTHON_IDENTITY_STILL_ACTIVE");
                }
            }
            if (helper is not null)
            {
                if (!helper.HasExited) { success = false; pgStarted = false; Console.Error.WriteLine("REAL_STACK_HELPER_STILL_ACTIVE"); }
                helper.Dispose();
            }
            if (postgres is not null)
            {
                if (pgStarted)
                {
                    try { await postgres.StopAsync(CancellationToken.None); }
                    catch { success = false; Console.Error.WriteLine("REAL_STACK_PG_CLEANUP_UNCONFIRMED"); }
                }
                await postgres.DisposeAsync();
            }
            lease?.Dispose();
            if (success)
            {
                EnsureNoReparse(root);
                if (Path.GetDirectoryName(Path.GetFullPath(root)) != parent) throw new IOException("test cleanup boundary");
                foreach (var file in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)) EnsureNoReparse(file);
                Directory.Delete(root, recursive: true);
            }
        }
        if (success) Console.WriteLine("PORTABLE_REAL_STACK=PASS");
        else Console.Error.WriteLine("RETAINED_TEST_ROOT=" + root);
        return success ? 0 : 1;
    }

    private static string Dsn(string user, string password, int port, string db) => $"postgresql://{user}:{Uri.EscapeDataString(password)}@127.0.0.1:{port}/{db}";
    private static async Task WriteSeedFixturesAsync(string repository, string app)
    {
        var entries = new List<object>();
        foreach (var relative in new[] { "prompts/base_prompt.txt", "prompts/bayes/bayes_v1.json" })
        {
            var start = new ProcessStartInfo("git") {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = repository,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add("show"); start.ArgumentList.Add("HEAD:" + relative);
            using var process = Process.Start(start) ?? throw new IOException("seed fixture source unavailable");
            using var content = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(content);
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Task.WhenAll(copy, error);
            if (process.ExitCode != 0) throw new IOException("tracked seed source unavailable");
            var bytes = content.ToArray();
            var destination = Path.Combine(app, "defaults", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, bytes);
            entries.Add(new { path = relative, kind = relative.EndsWith(".json") ? "bayes" : "prompt",
                size = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
        }
        File.WriteAllText(Path.Combine(app,"defaults","seed_manifest.json"),
            JsonSerializer.Serialize(new { format_version = 1, files = entries }), new UTF8Encoding(false));
    }
    private static int Port() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static void CopySource(string repository, string app, string file)
    {
        EnsureNoReparse(file);
        var relative = Path.GetRelativePath(repository, file);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) throw new IOException("source escapes repository");
        var destination = Path.Combine(app, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination, overwrite: false);
    }
    private static void EnsureNoReparse(string path)
    {
        FileSystemInfo? info = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path);
        while (info is not null)
        {
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("reparse path rejected");
            info = info is FileInfo file ? file.Directory : ((DirectoryInfo)info).Parent;
        }
    }
}
