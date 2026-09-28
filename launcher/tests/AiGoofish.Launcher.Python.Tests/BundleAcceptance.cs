using System.Net;
using System.Net.Sockets;
using AiGoofish.Launcher.Core;
using AiGoofish.Launcher.Platform.Windows;

internal static class BundleAcceptance
{
    public static async Task<int> RunAsync(string bundleRoot)
    {
        var repository = Environment.GetEnvironmentVariable("AIGOOFISH_REPOSITORY_ROOT")
            ?? throw new InvalidOperationException("Explicit repository test root is required");
        var parent = Path.Combine(Path.GetFullPath(repository), ".tmp", "tests", "bundle-acceptance");
        var fixture = Path.Combine(parent, "中文 空格-" + Guid.NewGuid().ToString("N"));
        RealPortableStackHost? host = null;
        var success = false;
        var stage = "manifest";
        try
        {
            Console.WriteLine("BUNDLE_TEST_ROOT=" + Path.GetFullPath(bundleRoot));
            Console.WriteLine("BUNDLE_CURRENT_EXISTS=" + File.Exists(Path.Combine(Path.GetFullPath(bundleRoot), "current.json")));
            // Verify the actual packaged bytes, then reuse its read-only components
            // with a dedicated test data root. Never write to the delivered data root.
            var bundle = await PortableBundleDescriptor.LoadAndVerifyAsync(Path.GetFullPath(bundleRoot));
            var disk = new DriveInfo(Path.GetPathRoot(fixture)!);
            const long expectedGrowth = 256L * 1024 * 1024;
            if (disk.AvailableFreeSpace - expectedGrowth < Math.Max(10L * 1024 * 1024 * 1024, disk.TotalSize * .05))
                throw new IOException("Test growth would cross the disk floor");
            EnsurePlainPath(parent);
            Directory.CreateDirectory(fixture);
            bundle = bundle with { BundleRoot = fixture };
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                stage = "host-create-" + attempt;
                host = RealPortableStackHost.Create(bundle);
                stage = "start-" + attempt;
                var started = await host.StartAsync();
                if (!started.Succeeded ||
                    host.Coordinator.Snapshot.State != LauncherState.Running)
                {
                    var logText = string.Join("\n", host.Coordinator.Logs.Select(entry => entry.Message));
                    if (IsRestrictedTokenFailure(logText))
                    {
                        Console.Error.WriteLine("BUNDLE_E2E_BLOCKED=pg_ctl cannot create a restricted Windows token (Win32 error 87); the test host is sandbox-restricted. No real Host-to-PostgreSQL acceptance is claimed.");
                    }
                    foreach (var entry in host.Coordinator.Logs)
                        Console.Error.WriteLine("BUNDLE_LOG=" + entry.Message);
                    throw new IOException("Packaged host failed to become ready");
                }
                if (!host.SetupRequired || string.IsNullOrWhiteSpace(host.SetupToken))
                    throw new IOException("New test instance did not require setup");
                stage = "http-" + attempt;
                using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
                using var http = new HttpClient(handler) { BaseAddress = new Uri(host.ManagementUrl), Timeout = TimeSpan.FromSeconds(5) };
                using var root = await http.GetAsync("");
                if (root.StatusCode != HttpStatusCode.SeeOther || root.Headers.Location?.ToString() != "/setup")
                    throw new IOException("First-run redirect missing");
                var setupUrl = new Uri(await host.CreateManagementBrowserUrlAsync());
                if (setupUrl.GetLeftPart(UriPartial.Authority) != http.BaseAddress.GetLeftPart(UriPartial.Authority) ||
                    setupUrl.AbsolutePath != "/setup" || !setupUrl.Fragment.StartsWith("#ticket=", StringComparison.Ordinal) ||
                    setupUrl.AbsoluteUri.Contains(host.SetupToken, StringComparison.Ordinal))
                    throw new IOException("Browser setup authorization must use a scoped ticket, not the setup credential");
                var browserTicket = setupUrl.Fragment["#ticket=".Length..];
                http.DefaultRequestHeaders.Add("Origin", http.BaseAddress.GetLeftPart(UriPartial.Authority));
                using var authorize = await http.PostAsync("setup/authorize", new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { ticket = browserTicket }), System.Text.Encoding.UTF8, "application/json"));
                if (authorize.StatusCode != HttpStatusCode.OK)
                    throw new IOException("Packaged browser setup ticket was not accepted");
                using var replay = await http.PostAsync("setup/authorize", new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { ticket = browserTicket }), System.Text.Encoding.UTF8, "application/json"));
                if (replay.StatusCode != HttpStatusCode.Unauthorized)
                    throw new IOException("Packaged browser setup ticket was reusable");
                http.DefaultRequestHeaders.Remove("Origin");
                using var setup = await http.GetAsync("setup");
                var html = await setup.Content.ReadAsStringAsync();
                if (setup.StatusCode != HttpStatusCode.OK || !html.Contains("<form", StringComparison.Ordinal))
                    throw new IOException("Packaged setup page is unavailable");
                stage = "stop-" + attempt;
                var stopped = await host.Coordinator.StopAsync();
                if (!stopped.Succeeded || host.Coordinator.Snapshot.State != LauncherState.Stopped)
                    throw new IOException("Packaged services were not safely stopped");
                if (attempt == 1)
                {
                    stage = "automatic-cancel-after-stage";
                    var previousPort = host.WebPortSettings.EffectivePort;
                    host.StageWebPort(host.WebPortSettings.Revision, previousPort, automatic: true);
                    using (var occupant = new TcpListener(IPAddress.Loopback, previousPort))
                    using (var cancellation = new CancellationTokenSource())
                    {
                        occupant.Server.ExclusiveAddressUse = true;
                        occupant.Start();
                        var staged = false;
                        host.AutomaticPortStagedForAcceptance = () =>
                        {
                            var settings = host.WebPortSettings;
                            if (settings.PendingPort is null || settings.Applying is not null)
                                throw new IOException("Cancellation injection must be after Stage and before BeginApply");
                            staged = true;
                            cancellation.Cancel();
                        };
                        LauncherStartResult cancelled;
                        try { cancelled = await host.Runtime.StartAsync(cancellation.Token); }
                        finally { host.AutomaticPortStagedForAcceptance = null; }
                        var rolledBack = host.WebPortSettings;
                        if (!staged || !cancelled.Operation.Cancelled || cancelled.Operation.Succeeded ||
                            rolledBack.PendingPort is not null || rolledBack.Applying is not null ||
                            rolledBack.EffectivePort != previousPort || !rolledBack.Automatic ||
                            !host.Coordinator.Snapshot.IsQuiescent || !occupant.Server.IsBound)
                            throw new IOException("Cancellation after Stage must clear automatic pending state and preserve the occupant");
                        var retry = await host.Runtime.StartAsync();
                        if (!retry.Operation.Succeeded || host.WebPortSettings.EffectivePort == previousPort ||
                            !occupant.Server.IsBound || !(await host.Runtime.StopAsync()).Succeeded)
                            throw new IOException("Cancelled automatic startup must remain retryable");
                    }
                    Console.WriteLine("PASS automatic cancellation precisely after Stage / rollback / retry");
                    stage = "same-host-restart";
                    var restart = await host.StartStoppedExistingAsync();
                    if (!restart.Succeeded || (await host.RefreshReadinessAsync()).State is not PythonReadinessState.Ready)
                        throw new IOException("The same initialized Host did not restart safely");
                    if (!(await host.Coordinator.StopAsync()).Succeeded)
                        throw new IOException("The restarted Host did not stop safely");
                    Console.WriteLine("PASS same initialized Host stop/restart/ready/stop");
                }
                await host.DisposeAsync();
                host = null;
                Console.WriteLine($"PASS packaged host cycle {attempt}: manifest/PG/provision/Web/setup/stop");
            }
            success = true;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"BUNDLE_ACCEPTANCE_FAILED stage={stage} category={error.GetType().Name}");
            if (Directory.Exists(fixture))
            {
                var diagnostic = $"Stage: {stage}\nException: {error.GetType().Name}\n" +
                    string.Join("\n", (host?.Coordinator.DiagnosticEvents ?? []).TakeLast(8)
                        .Select(item => $"Diagnostic: {item.Component}/{item.Code}/{item.Severity}"));
                try { await File.WriteAllTextAsync(Path.Combine(fixture, "BUNDLE_FAILURE.txt"), diagnostic, new System.Text.UTF8Encoding(false)); }
                catch (IOException) { Console.Error.WriteLine("BUNDLE_FAILURE_RECORD=FAILED"); }
                catch (UnauthorizedAccessException) { Console.Error.WriteLine("BUNDLE_FAILURE_RECORD=DENIED"); }
            }
            if (IsRestrictedTokenFailure(error.ToString()))
            {
                Console.Error.WriteLine("BUNDLE_E2E_BLOCKED=pg_ctl cannot create a restricted Windows token (Win32 error 87); the test host is sandbox-restricted. No real Host-to-PostgreSQL acceptance is claimed.");
            }
            if (stage == "manifest") Console.Error.WriteLine("BUNDLE_MANIFEST_ERROR=" + error.Message);
        }
        finally
        {
            if (host is not null)
            {
                try
                {
                    var stopped = await host.Coordinator.StopAsync();
                    if (!stopped.Succeeded) throw new IOException("Stop not confirmed");
                    await host.DisposeAsync();
                }
                catch (Exception error)
                {
                    success = false;
                    Console.Error.WriteLine("BUNDLE_CLEANUP_UNCONFIRMED=" + error.GetType().Name);
                }
            }
            if (success && Directory.Exists(fixture))
            {
                EnsurePlainPath(fixture);
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(fixture)), parent, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Cleanup boundary rejected");
                foreach (var path in Directory.EnumerateFileSystemEntries(fixture, "*", SearchOption.AllDirectories))
                    EnsurePlainPath(path);
                Directory.Delete(fixture, recursive: true);
            }
            else if (Directory.Exists(fixture))
                Console.Error.WriteLine("RETAINED_TEST_FIXTURE=" + fixture);
        }
        Console.WriteLine("BUNDLE_ACCEPTANCE=" + (success ? "PASS" : "FAILED"));
        return success ? 0 : 1;
    }


    private static void EnsurePlainPath(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Test path contains a reparse point");
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static bool IsRestrictedTokenFailure(string value) =>
        value.Contains("could not create restricted token", StringComparison.OrdinalIgnoreCase)
        || value.Contains("restricted token", StringComparison.OrdinalIgnoreCase) && value.Contains("error code 87", StringComparison.OrdinalIgnoreCase)
        || value.Contains("restricted token", StringComparison.OrdinalIgnoreCase) && value.Contains("Win32Exception", StringComparison.OrdinalIgnoreCase);
}
