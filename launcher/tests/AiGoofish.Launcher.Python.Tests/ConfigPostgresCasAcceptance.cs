using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AiGoofish.Launcher.Core;
using AiGoofish.Launcher.Platform.Windows;

internal static class ConfigPostgresCasAcceptance
{
    private const string RequiredR5ReleaseId = "acceptance-frozen-20260924-p1-r5";
    private const string RequiredR5ZipSha256 = "9f01503a2570f4b121143646979461ec83fcd5a6c1e7871567434e6ee605b91d";
    private const string RequiredR6ReleaseId = "acceptance-frozen-20260926-p1-r6";
    private const string RequiredR6ZipSha256 = "b292e8976f01ecd44078fbbee01f6c97071a5540b19e16c3dc8783e9b5433843";
    private const string RequiredR7ReleaseId = "acceptance-frozen-20260927-p1-r7";
    private const string RequiredR7ZipSha256 = "efa7dba165a4a8fd9be9e5ff36111781f4caf3eb32ff05d851e198e7be0d997d";
    private const long ExpectedGrowthBytes = 256L * 1024 * 1024;
    private const int MaximumProbeOutputBytes = 64 * 1024;
    private const string ProbeRelativePath = "tests/portable_config_pg_case.py";
    private static readonly string[] SelfTestSteps =
    [
        "parse-valid", "parse-invalid", "occupied-port", "probe-environment", "fixture-create", "fixture-lease",
        "transaction-read", "transaction-stage", "transaction-begin", "transaction-commit",
        "transaction-verify", "fixture-cleanup", "probe-marker",
    ];

    public static bool TryParseWebPort(string value, out int port)
    {
        if (!int.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out port) ||
            port is < 1024 or > 65535 || port == PortableInstanceCatalog.DefaultPostgresPort)
        {
            port = 0;
            return false;
        }
        return true;
    }

    public static void RunWebPortHarnessSelfTests(string repositoryRoot)
    {
        var step = "parse-valid";
        try
        {
            foreach (var valid in new[] { "1024", "58001", "65535" })
            {
                if (!TryParseWebPort(valid, out _))
                    throw new InvalidOperationException("Expected a valid explicit Web port to be accepted.");
            }
            step = "parse-invalid";
            foreach (var invalid in new[]
                     {
                         "", "1023", "65536",
                         PortableInstanceCatalog.DefaultPostgresPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         "58001x", " 58001",
                     })
            {
                if (TryParseWebPort(invalid, out _))
                    throw new InvalidOperationException("Expected an invalid explicit Web port to be rejected.");
            }

            step = "occupied-port";
            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                var occupiedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                var rejected = false;
                try { RequirePortAvailable(occupiedPort); }
                catch (IOException) { rejected = true; }
                if (!rejected)
                    throw new InvalidOperationException("The exact occupied candidate port was not rejected.");
            }

            step = "probe-environment";
            var syntheticEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = "synthetic-safe-path",
                ["SystemRoot"] = "synthetic-system-root",
                ["WINDIR"] = "synthetic-windir",
                ["UNAPPROVED_TEST_VALUE"] = "must-not-inherit",
            };
            var probeStart = new ProcessStartInfo();
            ConfigurePrivateProbeEnvironment(probeStart, "synthetic-temp-root",
                name => syntheticEnvironment.TryGetValue(name, out var value) ? value : null);
            if (probeStart.Environment["PATH"] != syntheticEnvironment["PATH"] ||
                probeStart.Environment["SystemRoot"] != syntheticEnvironment["SystemRoot"] ||
                probeStart.Environment["WINDIR"] != syntheticEnvironment["WINDIR"] ||
                probeStart.Environment["TEMP"] != "synthetic-temp-root" ||
                probeStart.Environment["TMP"] != "synthetic-temp-root" ||
                probeStart.Environment.ContainsKey("UNAPPROVED_TEST_VALUE"))
                throw new InvalidOperationException("The private probe environment did not preserve its explicit allowlist.");

            step = "fixture-create";
            if (!Path.IsPathFullyQualified(repositoryRoot) || !Directory.Exists(repositoryRoot))
                throw new ArgumentException("An existing absolute repository root is required.", nameof(repositoryRoot));
            var repository = Path.GetFullPath(repositoryRoot);
            if (!Path.Exists(Path.Combine(repository, ".git")))
                throw new ArgumentException("The explicit root does not contain repository metadata.", nameof(repositoryRoot));
            var parent = Path.Combine(repository, ".tmp", "tests", "config-pg-cas-web-port-selftest");
            var parentExisted = Directory.Exists(parent);
            EnsureSelfTestPathSafe(repository, parent);
            Directory.CreateDirectory(parent);
            EnsureSelfTestPathSafe(repository, parent);
            var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                step = "fixture-lease";
                using var lease = InstanceDataRootLease.Acquire(root);
                var seeded = SeedWebPortSettings(lease,
                    PortableInstanceCatalog.DefaultWebPort,
                    PortableInstanceCatalog.DefaultPostgresPort,
                    58001,
                    nextStep => step = nextStep);
                var persisted = new PortableWebPortSettingsStore().Read(lease,
                    PortableInstanceCatalog.DefaultWebPort,
                    PortableInstanceCatalog.DefaultPostgresPort);
                step = "transaction-verify";
                if (seeded != persisted || seeded.EffectivePort != 58001 || seeded.PendingPort is not null ||
                    seeded.Applying is not null || seeded.Revision != 3)
                    throw new InvalidOperationException("The isolated Web-port transaction did not commit a clean candidate state.");
                TestSelfTestPathRejectsReparsePoint(repository, root);
            }
            finally
            {
                step = "fixture-cleanup";
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
                if (!parentExisted && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                    Directory.Delete(parent);
            }

            step = "probe-marker";
            TestProbeFailureMarkerSanitization();
        }
        catch (Exception exception)
        {
            var category = GetSelfTestFailureCategory(exception);
            if (!SelfTestSteps.Contains(step, StringComparer.Ordinal))
                step = "fixture-create";
            Console.Error.WriteLine($"CONFIG_PG_CAS_PORT_SELFTEST_FAILURE step={step} category={category}");
            throw;
        }
    }

    public static async Task<int> RunRealWebPortHostAcceptanceAsync(string bundleRootArgument)
    {
        var repository = Path.GetFullPath(Environment.GetEnvironmentVariable("AIGOOFISH_REPOSITORY_ROOT")
            ?? throw new InvalidOperationException("Explicit repository root is required."));
        var bundleRoot = Path.GetFullPath(bundleRootArgument);
        var parent = Path.Combine(repository, ".tmp", "tests", "portable-web-port-host");
        var runGate = default(FileStream);
        var fixture = string.Empty;
        var stage = "preflight";
        var category = "Other";
        var success = false;
        try
        {
            RequireWindows();
            EnsureNoReparsePoints(parent);
            Directory.CreateDirectory(parent);
            EnsureNoReparsePoints(parent);
            EnsureNoReparsePoints(bundleRoot);
            runGate = new FileStream(Path.Combine(parent, ".web-port-host-run.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var verifiedBundle = await PortableBundleDescriptor.LoadAndVerifyAsync(bundleRoot).ConfigureAwait(false);
            var expectedZipSha256 = verifiedBundle.ReleaseId switch
            {
                RequiredR5ReleaseId => RequiredR5ZipSha256,
                RequiredR6ReleaseId => RequiredR6ZipSha256,
                RequiredR7ReleaseId => RequiredR7ZipSha256,
                _ => throw new InvalidDataException("The Web-port Host acceptance requires a reviewed frozen bundle identity."),
            };
            var expectedBundleRoot = Path.GetFullPath(Path.Combine(repository, "launcher", "dist", "portable-" + verifiedBundle.ReleaseId));
            var expectedZipPath = expectedBundleRoot + ".zip";
            if (!string.Equals(bundleRoot, expectedBundleRoot, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(expectedZipPath))
                throw new InvalidDataException("The Web-port Host acceptance requires the exact frozen bundle path and adjacent ZIP.");
            using (var zip = File.OpenRead(expectedZipPath))
            {
                var actualZipSha256 = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
                if (!string.Equals(actualZipSha256, expectedZipSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("The frozen ZIP hash does not match its reviewed identity.");
            }

            var postgresPort = PortableInstanceCatalog.DefaultPostgresPort;
            RequirePortAvailable(postgresPort);
            var ports = new HashSet<int> { postgresPort, PortableInstanceCatalog.DefaultWebPort };
            var oldAppliedPort = FindAvailableWebPort(ports);
            var appliedPort = FindAvailableWebPort(ports);
            var oldRollbackPort = FindAvailableWebPort(ports);
            var occupiedCandidatePort = FindAvailableWebPort(ports);
            var disk = new DriveInfo(Path.GetPathRoot(parent)!);
            var minimumFree = Math.Max(10L * 1024 * 1024 * 1024, (long)(disk.TotalSize * .05));
            if (disk.AvailableFreeSpace - ExpectedGrowthBytes < minimumFree)
                throw new IOException("Test growth would cross the disk floor.");

            stage = "candidate-ready";
            fixture = await RunRealWebPortScenarioAsync(
                verifiedBundle, parent, "apply", oldAppliedPort, appliedPort, occupyCandidate: false).ConfigureAwait(false);
            if (fixture.Length != 0)
                throw new IOException("Successful apply fixture could not be cleaned safely.");
            Console.WriteLine("WEB_PORT_HOST_APPLY=PASS");

            stage = "occupied-rollback";
            fixture = await RunRealWebPortScenarioAsync(
                verifiedBundle, parent, "occupied", oldRollbackPort, occupiedCandidatePort, occupyCandidate: true).ConfigureAwait(false);
            if (fixture.Length != 0)
                throw new IOException("Successful rollback fixture could not be cleaned safely.");
            Console.WriteLine("WEB_PORT_HOST_ROLLBACK=PASS");
            stage = "candidate-ready";
            fixture = await RunRealWebPortScenarioAsync(verifiedBundle, parent, "auto",
                FindAvailableWebPort(ports), FindAvailableWebPort(ports), occupyCandidate: false,
                automaticSelection: true).ConfigureAwait(false);
            if (fixture.Length != 0) throw new IOException("Automatic port fixture was not cleaned safely.");
            Console.WriteLine("WEB_PORT_HOST_AUTOMATIC=PASS");
            success = true;
        }
        catch (Exception exception)
        {
            category = GetFailureCategory(exception);
            Console.Error.WriteLine($"WEB_PORT_HOST_FAILURE stage={stage} category={category}");
            if (fixture.Length != 0 && Directory.Exists(fixture))
            {
                try
                {
                    WriteWebPortFailureRecord(fixture, stage, category);
                    Console.Error.WriteLine("WEB_PORT_HOST_FIXTURE_RETAINED=YES");
                }
                catch (Exception recordFailure)
                {
                    Console.Error.WriteLine("WEB_PORT_HOST_FAILURE_RECORD=FAILED:" + recordFailure.GetType().Name);
                }
            }
        }
        finally
        {
            runGate?.Dispose();
        }

        Console.WriteLine("WEB_PORT_HOST_ACCEPTANCE=" + (success ? "PASS" : "FAILED"));
        return success ? 0 : 1;
    }

    private static async Task<string> RunRealWebPortScenarioAsync(
        PortableBundleDescriptor verifiedBundle,
        string parent,
        string scenario,
        int oldPort,
        int candidatePort,
        bool occupyCandidate,
        bool automaticSelection = false)
    {
        var fixture = Path.Combine(parent, scenario + "-" + Guid.NewGuid().ToString("N"));
        var created = false;
        var safeToRemove = false;
        var succeeded = false;
        var stage = "fixture-create";
        Exception? failure = null;
        RealPortableStackHost? host = null;
        var knownIdentities = new List<OwnedProcessIdentity>();
        TcpListener? occupiedListener = null;
        try
        {
            RequireNoMatchingProcesses(verifiedBundle.BundleRoot,
                verifiedBundle with { BundleRoot = fixture }, verifiedBundle.PostgresRoot, fixture);
            RequirePortAvailable(PortableInstanceCatalog.DefaultPostgresPort);
            RequirePortAvailable(oldPort);
            if (!occupyCandidate) RequirePortAvailable(candidatePort);
            created = true;
            Directory.CreateDirectory(fixture);
            RestrictFixtureDirectory(fixture);
            EnsureNoReparsePoints(fixture);
            RequireCurrentUserFixtureAcl(fixture);
            var bundle = verifiedBundle with { BundleRoot = fixture };
            var active = new PortableInstanceCatalog(fixture).ResolveActiveInstance();
            using (var lease = InstanceDataRootLease.Acquire(active.DataRoot))
                _ = SeedWebPortSettings(lease, active.WebPort, active.PostgresPort, oldPort);

            stage = "host-start";
            host = RealPortableStackHost.Create(bundle);
            var started = await host.Coordinator.StartAsync().ConfigureAwait(false);
            if (!started.Succeeded || host.Coordinator.Snapshot.State is not LauncherState.Running)
                throw new IOException("The owned portable Host did not reach Running.");
            RememberProcessIdentities(host, knownIdentities);
            await AssertWebPortHostReadyAsync(host, oldPort).ConfigureAwait(false);
            var initialSettings = host.WebPortSettings;
            if (initialSettings.EffectivePort != oldPort || initialSettings.PendingPort is not null ||
                initialSettings.Applying is not null)
                throw new IOException("The fresh Host did not start on its isolated effective Web port.");

            var stop = await host.Coordinator.StopAsync().ConfigureAwait(false);
            if (!stop.Succeeded || host.Coordinator.Snapshot.State is not LauncherState.Stopped)
                throw new IOException("The owned Host did not safely stop before port application.");
            var staged = host.StageWebPort(initialSettings.Revision,
                automaticSelection ? oldPort : candidatePort, automaticSelection);
            if (staged.PendingPort != (automaticSelection ? (int?)null : candidatePort) || staged.Applying is not null)
                throw new IOException("The Host did not persist the requested pending Web port.");
            if (automaticSelection)
            {
                var unchanged = await host.ApplyAutomaticWebPortAsync(staged.Revision).ConfigureAwait(false);
                if (unchanged.Applied || host.WebPortSettings != staged ||
                    host.Coordinator.Snapshot.State is not LauncherState.Stopped)
                    throw new IOException("Automatic mode must reuse a free effective port without mutation.");
            }
            if (occupyCandidate || automaticSelection)
            {
                occupiedListener = new TcpListener(IPAddress.Loopback, automaticSelection ? oldPort : candidatePort);
                occupiedListener.Server.ExclusiveAddressUse = true;
                occupiedListener.Start();
            }

            stage = "web-port-apply";
            var apply = automaticSelection
                ? await host.ApplyAutomaticWebPortAsync(staged.Revision).ConfigureAwait(false)
                : await host.ApplyPendingWebPortAsync(staged.Revision).ConfigureAwait(false);
            if (automaticSelection)
            {
                candidatePort = apply.EffectivePort;
                if (candidatePort == oldPort || candidatePort == host.PostgresPort ||
                    !host.WebPortSettings.Automatic || occupiedListener?.Server.IsBound is not true)
                    throw new IOException("Automatic selection changed the foreign listener or failed to persist its mode.");
            }
            if (occupyCandidate)
            {
                var currentHost = host ?? throw new InvalidOperationException("The owned Host unexpectedly disappeared.");
                var rolledBack = currentHost.WebPortSettings;
                if (apply.Applied || apply.EffectivePort != oldPort || rolledBack.EffectivePort != oldPort ||
                    rolledBack.PendingPort != candidatePort || rolledBack.Applying is not null ||
                    !currentHost.ManagementUrl.EndsWith($":{oldPort}/", StringComparison.Ordinal))
                    throw new IOException("Occupied candidate did not preserve old effective port and pending intent.");
                (occupiedListener ?? throw new InvalidOperationException("The synthetic occupied candidate listener disappeared.")).Stop();
                occupiedListener = null;
                stage = "rollback-ready";
                await AssertWebPortHostReadyAsync(currentHost, oldPort).ConfigureAwait(false);
                RememberProcessIdentities(currentHost, knownIdentities);
            }
            else
            {
                var currentHost = host ?? throw new InvalidOperationException("The owned Host unexpectedly disappeared.");
                var applied = currentHost.WebPortSettings;
                if (!apply.Applied || apply.EffectivePort != candidatePort || applied.EffectivePort != candidatePort ||
                    applied.PendingPort is not null || applied.Applying is not null ||
                    !currentHost.ManagementUrl.EndsWith($":{candidatePort}/", StringComparison.Ordinal))
                    throw new IOException("The candidate Web port was not committed as effective.");
                stage = "candidate-ready";
                await AssertWebPortHostReadyAsync(currentHost, candidatePort).ConfigureAwait(false);
                RememberProcessIdentities(currentHost, knownIdentities);
            }

            var finalStop = await host.Coordinator.StopAsync().ConfigureAwait(false);
            if (!finalStop.Succeeded || host.Coordinator.Snapshot.State is not LauncherState.Stopped)
                throw new IOException("The owned Host did not safely stop after port acceptance.");
            RememberProcessIdentities(host, knownIdentities);
            if (knownIdentities.Any(identity => PythonProcessIdentityProbe.GetRunState(identity) is not PythonIdentityRunState.NotRunning))
                throw new IOException("A process belonging to the acceptance instance did not confirm exit.");
            await host.DisposeAsync().ConfigureAwait(false);
            host = null;
            safeToRemove = true;
            succeeded = true;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            occupiedListener?.Stop();
            if (host is not null)
            {
                try
                {
                    RememberProcessIdentities(host, knownIdentities);
                    var stop = await host.Coordinator.StopAsync().ConfigureAwait(false);
                    if (!stop.Succeeded || host.Coordinator.Snapshot.State is not LauncherState.Stopped)
                        throw new IOException("Owned Host cleanup stop was not confirmed.");
                    RememberProcessIdentities(host, knownIdentities);
                    if (knownIdentities.Any(identity => PythonProcessIdentityProbe.GetRunState(identity) is not PythonIdentityRunState.NotRunning))
                        throw new IOException("Owned process identity remained active or unknown.");
                    await host.DisposeAsync().ConfigureAwait(false);
                    safeToRemove = true;
                }
                catch
                {
                    safeToRemove = false;
                }
            }

            if (created && safeToRemove && succeeded && Directory.Exists(fixture))
            {
                EnsureNoReparsePoints(fixture);
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(fixture)), parent, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Fixture cleanup boundary rejected.");
                Directory.Delete(fixture, recursive: true);
            }
            else if (created && Directory.Exists(fixture))
            {
                try
                {
                    WriteWebPortFailureRecord(fixture, stage, GetFailureCategory(failure ?? new IOException("cleanup unconfirmed")), failure,
                        host?.Coordinator.DiagnosticEvents);
                    Console.Error.WriteLine("WEB_PORT_HOST_FIXTURE_RETAINED=YES");
                }
                catch (Exception exception) { Console.Error.WriteLine("WEB_PORT_HOST_FAILURE_RECORD=FAILED:" + exception.GetType().Name); }
            }
        }

        return succeeded ? string.Empty : fixture;
    }

    private static async Task AssertWebPortHostReadyAsync(RealPortableStackHost host, int expectedPort)
    {
        var identity = host.PythonProcessIdentity
            ?? throw new IOException("The Web process identity is missing.");
        var postgresIdentity = host.PostgresProcessIdentity
            ?? throw new IOException("The PostgreSQL process identity is missing.");
        if (identity.InstanceId == Guid.Empty ||
            !string.Equals(identity.InstanceRoot, host.DataRoot, StringComparison.OrdinalIgnoreCase) ||
            postgresIdentity.InstanceId != identity.InstanceId ||
            !string.Equals(postgresIdentity.InstanceRoot, host.DataRoot, StringComparison.OrdinalIgnoreCase) ||
            PythonProcessIdentityProbe.GetRunState(identity) is not PythonIdentityRunState.Running ||
            PythonProcessIdentityProbe.GetRunState(postgresIdentity) is not PythonIdentityRunState.Running ||
            host.ManagementUrl != $"http://127.0.0.1:{expectedPort}/")
            throw new IOException("The Web process identity or effective management URL is outside the fixture.");

        // The first-admin setup token must never authorize the Launcher control API.
        // Use the production Host client for authenticated readiness (including peer ownership).
        var readiness = await host.RefreshReadinessAsync().ConfigureAwait(false);
        if (readiness.State is not PythonReadinessState.Ready || readiness.SetupRequired is not true ||
            host.PythonProcessIdentity != identity || host.PostgresProcessIdentity != postgresIdentity)
            throw new IOException("The owned Host did not confirm readiness with stable process identities.");

        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{expectedPort}/internal/ready");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", host.SetupToken
            ?? throw new IOException("The synthetic first-admin readiness token is unavailable."));
        request.Headers.Add("X-Goofish-Instance-Id", identity.InstanceId.ToString("D"));
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            throw new IOException("The first-admin setup token was not rejected by the Launcher control API.");
        using var health = await client.GetAsync($"http://127.0.0.1:{expectedPort}/health").ConfigureAwait(false);
        if (health.StatusCode != HttpStatusCode.OK)
            throw new IOException("The effective Web port did not return public health.");
        await using var body = await health.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.GetProperty("status").GetString() != "alive")
            throw new IOException("The effective Web port returned an invalid public health body.");
    }

    private static void RememberProcessIdentities(RealPortableStackHost host, ICollection<OwnedProcessIdentity> identities)
    {
        foreach (var identity in new[] { host.PostgresProcessIdentity, host.PythonProcessIdentity })
            if (identity is not null && !identities.Contains(identity)) identities.Add(identity);
    }

    private static int FindAvailableWebPort(ISet<int> reserved)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (port is >= 1024 and <= 65535 && !reserved.Contains(port))
            {
                listener.Stop();
                RequirePortAvailable(port);
                reserved.Add(port);
                return port;
            }
        }
        throw new IOException("No distinct loopback acceptance port was available.");
    }

    private static void WriteWebPortFailureRecord(string fixture, string stage, string category, Exception? failure = null,
        IReadOnlyList<LauncherDiagnosticEvent>? diagnostics = null)
    {
        var contents = "Synthetic Web-port Host acceptance failed.\n" +
            $"Stage: {stage}\nCategory: {category}\n" +
            "No real account, scrape, paid AI request, or notification was used.\n" +
            "Retention: review after 7 days; stateful fixtures are not automatically deleted.\n";
        // Persist only bounded exception types and code locations, never messages, credentials or request bodies.
        foreach (var diagnostic in (diagnostics ?? []).TakeLast(8))
            contents += $"Diagnostic: {diagnostic.Component}/{diagnostic.Code}/{diagnostic.Severity}\n";
        var pending = new Queue<Exception>();
        if (failure is not null) pending.Enqueue(failure);
        for (var count = 0; pending.Count > 0 && count < 8; count++)
        {
            var error = pending.Dequeue();
            contents += "Exception: " + error.GetType().Name + "\n";
            foreach (var frame in new StackTrace(error, false).GetFrames().Take(6))
            {
                var method = frame.GetMethod();
                contents += "Method: " + method?.DeclaringType?.Name + "." + method?.Name + "\n";
            }
            if (error is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions.Take(8)) pending.Enqueue(inner);
            else if (error.InnerException is { } inner) pending.Enqueue(inner);
        }
        using var stream = new FileStream(Path.Combine(fixture, "WEB_PORT_HOST_FAILURE.txt"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(contents);
    }


    private static PortableWebPortSettings SeedWebPortSettings(
        InstanceDataRootLease lease,
        int fallbackPort,
        int postgresPort,
        int candidatePort,
        Action<string>? setStep = null)
    {
        var store = new PortableWebPortSettingsStore();
        setStep?.Invoke("transaction-read");
        var initial = store.Read(lease, fallbackPort, postgresPort);
        if (initial.Revision != 0 || initial.PendingPort is not null || initial.Applying is not null)
            throw new IOException("The isolated fixture Web-port settings are not fresh.");

        setStep?.Invoke("transaction-stage");
        var staged = store.Stage(lease, initial.Revision, candidatePort, initial.EffectivePort, postgresPort);
        if (staged.PendingPort != candidatePort || staged.Applying is not null)
            throw new IOException("The isolated fixture Web-port candidate did not stage cleanly.");
        setStep?.Invoke("transaction-begin");
        var applying = store.BeginApply(lease, staged.Revision, initial.EffectivePort, postgresPort);
        var intent = applying.Applying
            ?? throw new IOException("The isolated fixture Web-port apply intent is missing.");
        setStep?.Invoke("transaction-commit");
        var committed = store.CommitApply(lease, intent.AttemptId, initial.EffectivePort, postgresPort);
        setStep?.Invoke("transaction-verify");
        var persisted = store.Read(lease, fallbackPort, postgresPort);
        if (persisted != committed || persisted.EffectivePort != candidatePort ||
            persisted.PendingPort is not null || persisted.Applying is not null)
            throw new IOException("The isolated fixture Web-port transaction did not persist a clean candidate state.");
        return persisted;
    }

    private static string GetSelfTestFailureCategory(Exception exception) => exception switch
    {
        SocketException => "Socket",
        IOException => "Io",
        UnauthorizedAccessException or System.Security.SecurityException => "Security",
        ArgumentException => "Argument",
        InvalidOperationException => "InvalidOperation",
        _ => "Other",
    };

    private static void EnsureSelfTestPathSafe(string repository, string path)
    {
        var normalizedRepository = Path.GetFullPath(repository)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(normalizedRepository + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("The isolated self-test fixture escaped the explicit repository root.");
        EnsureNoReparsePoints(normalizedPath);
    }

    private static void TestSelfTestPathRejectsReparsePoint(string repository, string root)
    {
        var target = Path.Combine(root, "reparse-target");
        var link = Path.Combine(root, "reparse-alias");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        try
        {
            try
            {
                EnsureSelfTestPathSafe(repository, Path.Combine(link, "would-be-created"));
            }
            catch (IOException)
            {
                return;
            }
            throw new InvalidOperationException("The self-test path guard accepted an ancestor reparse point.");
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    public static async Task<int> RunAsync(string bundleRootArgument, int? requestedWebPort = null)
    {
        var repository = Path.GetFullPath(Environment.GetEnvironmentVariable("AIGOOFISH_REPOSITORY_ROOT")
            ?? throw new InvalidOperationException("Explicit repository root is required."));
        var requestedBundleRoot = Path.GetFullPath(bundleRootArgument);
        if (requestedWebPort is { } candidate &&
            !TryParseWebPort(candidate.ToString(System.Globalization.CultureInfo.InvariantCulture), out _))
            throw new ArgumentOutOfRangeException(nameof(requestedWebPort), "Explicit Web port is invalid.");
        var preflightWebPort = requestedWebPort ?? PortableInstanceCatalog.DefaultWebPort;
        var parent = Path.Combine(repository, ".tmp", "tests", "portable-config-pg-host");
        var requestedFixture = Environment.GetEnvironmentVariable("AIGOOFISH_CONFIG_PG_CAS_FIXTURE_ROOT");
        var fixture = string.IsNullOrWhiteSpace(requestedFixture)
            ? Path.Combine(parent, Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(requestedFixture);
        var gatePath = Path.Combine(parent, ".config-pg-cas-run.lock");
        var probe = Path.Combine(repository, ProbeRelativePath.Replace('/', Path.DirectorySeparatorChar));
        RealPortableStackHost? host = null;
        Process? helper = null;
        FileStream? runGate = null;
        var stage = "preflight";
        var fixtureCreated = false;
        var safeToRemove = false;
        var success = false;
        var failureCategory = "Other";

        try
        {
            RequireWindows();
            Directory.CreateDirectory(parent);
            EnsureNoReparsePoints(parent);
            EnsureNoReparsePoints(requestedBundleRoot);
            if (!string.Equals(Path.GetDirectoryName(fixture), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase) ||
                Directory.Exists(fixture) || File.Exists(fixture))
                throw new IOException("The explicitly selected configuration fixture path is invalid or already exists.");
            runGate = new FileStream(gatePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stage = "bundle-verify";
            var bundle = await PortableBundleDescriptor.LoadAndVerifyAsync(requestedBundleRoot).ConfigureAwait(false);
            var postgresRoot = bundle.PostgresRoot;
            stage = "preflight";
            var postgresDataRoot = Path.Combine(fixture, "postgres", "cluster");
            RequireNoMatchingProcesses(repository, bundle, postgresRoot, fixture);
            var disk = new DriveInfo(Path.GetPathRoot(fixture)!);
            var minimumFree = Math.Max(10L * 1024 * 1024 * 1024, (long)(disk.TotalSize * .05));
            if (disk.AvailableFreeSpace - ExpectedGrowthBytes < minimumFree)
                throw new IOException("Test growth would cross the disk floor.");
            RequirePreflightResourcesAvailable(postgresDataRoot, preflightWebPort);
            if (!File.Exists(probe))
                throw new FileNotFoundException("The configuration acceptance probe is missing.");

            stage = "fixture-create";
            fixtureCreated = true;
            Directory.CreateDirectory(fixture);
            RestrictFixtureDirectory(fixture);
            EnsureNoReparsePoints(fixture);
            RequireCurrentUserFixtureAcl(fixture);
            bundle = bundle with { BundleRoot = fixture };

            if (requestedWebPort is { } candidateWebPort)
            {
                var activeFixture = new PortableInstanceCatalog(fixture).ResolveActiveInstance();
                if (activeFixture.WebPort != candidateWebPort)
                {
                    stage = "web-port-seed";
                    using var fixtureLease = InstanceDataRootLease.Acquire(activeFixture.DataRoot);
                    _ = SeedWebPortSettings(fixtureLease, activeFixture.WebPort,
                        activeFixture.PostgresPort, candidateWebPort);
                }
            }

            stage = "host-create";
            RequireNoMatchingProcesses(repository, bundle, postgresRoot, fixture);
            RequirePreflightResourcesAvailable(postgresDataRoot, preflightWebPort);
            host = RealPortableStackHost.Create(bundle);
            stage = "host-start";
            var started = await host.Coordinator.StartAsync().ConfigureAwait(false);
            if (!started.Succeeded || host.Coordinator.Snapshot.State is not LauncherState.Running)
                throw new IOException("The owned portable Host did not reach Running.");
            if (requestedWebPort is { } verifiedWebPort &&
                (host.WebPortSettings.EffectivePort != verifiedWebPort || host.WebPortSettings.PendingPort is not null ||
                 host.WebPortSettings.Applying is not null))
                throw new IOException("The Host did not start from the requested committed Web-port setting.");
            if (host.PostgresProcessIdentity is null || host.PythonProcessIdentity is null)
                throw new IOException("The owned PostgreSQL or Python process identity is missing.");

            stage = "probe";
            var active = new PortableInstanceCatalog(fixture).ResolveActiveInstance();
            var leaseField = typeof(RealPortableStackHost).GetField("_lease",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The test Host lease is unavailable.");
            var lease = leaseField.GetValue(host) as InstanceDataRootLease
                ?? throw new InvalidOperationException("The test Host lease is unavailable.");
            var secrets = new WindowsInstanceSecretsStore().Load(lease);
            var databaseUrl = MakeDsn(
                "aigoofish_app", secrets.ApplicationDatabasePassword, active.PostgresPort, "aigoofish");
            var request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                database_url = databaseUrl,
                encryption_master_key = secrets.EncryptionMasterKey,
                program_root = bundle.ProgramRoot,
                data_root = host.DataRoot,
                cache_root = Path.Combine(host.DataRoot, "cache"),
            });
            var temporaryRoot = Path.Combine(fixture, "probe-temp");
            Directory.CreateDirectory(temporaryRoot);
            EnsureNoReparsePoints(temporaryRoot);
            var start = new ProcessStartInfo
            {
                FileName = bundle.PythonExecutable,
                WorkingDirectory = temporaryRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (var argument in new[] { "-I", "-B", probe, "--app-root", bundle.ProgramRoot })
                start.ArgumentList.Add(argument);
            ConfigurePrivateProbeEnvironment(start, temporaryRoot, Environment.GetEnvironmentVariable);
            helper = Process.Start(start) ?? throw new IOException("The private configuration probe did not start.");

            var outputTask = ReadBoundedOutputAsync(helper.StandardOutput.BaseStream, MaximumProbeOutputBytes);
            var errorTask = ReadBoundedOutputAsync(helper.StandardError.BaseStream, MaximumProbeOutputBytes);
            try
            {
                await helper.StandardInput.BaseStream.WriteAsync(request).ConfigureAwait(false);
                await helper.StandardInput.BaseStream.FlushAsync().ConfigureAwait(false);
                helper.StandardInput.Close();
                await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (!helper.HasExited)
                {
                    try { helper.Kill(entireProcessTree: true); }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    try { await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
                    catch (TimeoutException) { throw new IOException("The owned config probe exit was not confirmed."); }
                }
                throw new IOException("The bounded configuration PostgreSQL probe timed out.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(request);
            }
            var output = await outputTask.ConfigureAwait(false);
            var errorOutput = await errorTask.ConfigureAwait(false);
            if (helper.ExitCode != 0 || output.Length > MaximumProbeOutputBytes)
            {
                if (GetSanitizedProbeFailureMarker(errorOutput) is { } probeFailure)
                    Console.Error.WriteLine(probeFailure);
                if (ContainsExactLine(errorOutput, "portable configuration PostgreSQL probe failed"))
                    Console.Error.WriteLine("CONFIG_PG_CAS_PROBE_ERROR=portable configuration PostgreSQL probe failed (details suppressed by probe contract)");
                throw new IOException("The private configuration PostgreSQL probe failed.");
            }
            using (var result = JsonDocument.Parse(output))
            {
                var root = result.RootElement;
                if (root.GetProperty("simultaneous_writers").GetArrayLength() != 2 ||
                    root.GetProperty("delete_recreate_same_revision_rejected").GetBoolean() is false ||
                    root.GetProperty("default_switch_unique_and_stale_identity_rejected").GetBoolean() is false ||
                    root.GetProperty("advanced_fields_preserved").GetBoolean() is false ||
                    root.GetProperty("empty_key_preserved").GetBoolean() is false ||
                    root.GetProperty("host_change_requires_replacement_or_removal").GetBoolean() is false ||
                    root.GetProperty("new_host_key_saved").GetBoolean() is false ||
                    root.GetProperty("user_isolation").GetBoolean() is false ||
                    root.GetProperty("external_actions").GetString() is not "none")
                    throw new InvalidDataException("The private PostgreSQL configuration probe returned an incomplete result.");
                Console.WriteLine("CONFIG_PG_CAS_EVIDENCE=" + root.GetRawText());
                Console.WriteLine("PASS portable PostgreSQL config CAS: concurrency/recreate/default/key/fields/isolation");
            }

            stage = "host-stop";
            var postgresIdentity = host.PostgresProcessIdentity
                ?? throw new IOException("The owned PostgreSQL process identity is missing before shutdown.");
            var pythonIdentity = host.PythonProcessIdentity
                ?? throw new IOException("The owned Python process identity is missing before shutdown.");
            var stopped = await host.Coordinator.StopAsync().ConfigureAwait(false);
            if (!stopped.Succeeded || host.Coordinator.Snapshot.State is not LauncherState.Stopped)
                throw new IOException("The owned Host did not safely stop after configuration acceptance.");
            var postgresRunState = PythonProcessIdentityProbe.GetRunState(postgresIdentity);
            var pythonRunState = PythonProcessIdentityProbe.GetRunState(pythonIdentity);
            if (postgresRunState is not PythonIdentityRunState.NotRunning ||
                pythonRunState is not PythonIdentityRunState.NotRunning)
                throw new IOException("The owned PostgreSQL/Python process exit could not be confirmed.");
            Console.WriteLine($"CONFIG_PG_CAS_OWNED_STOPPED=postgres:{postgresIdentity.ProcessId},python:{pythonIdentity.ProcessId}");
            await host.DisposeAsync().ConfigureAwait(false);
            host = null;
            safeToRemove = true;
            success = true;
        }
        catch (Exception exception)
        {
            failureCategory = GetFailureCategory(exception);
            Console.Error.WriteLine($"CONFIG_PG_CAS_FAILURE stage={stage} category={failureCategory}");
        }
        finally
        {
            if (helper is not null)
            {
                if (!helper.HasExited)
                {
                    try
                    {
                        helper.Kill(entireProcessTree: true);
                        await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
                    {
                        safeToRemove = false;
                        success = false;
                    }
                }
                helper.Dispose();
            }
            if (host is not null)
            {
                try
                {
                    var stopped = await host.Coordinator.StopAsync().ConfigureAwait(false);
                    if (!stopped.Succeeded || host.Coordinator.Snapshot.State is not LauncherState.Stopped)
                        safeToRemove = false;
                    else
                        safeToRemove = true;
                    await host.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    safeToRemove = false;
                    success = false;
                    Console.Error.WriteLine("CONFIG_PG_CAS_CLEANUP_UNCONFIRMED=" + exception.GetType().Name);
                }
            }

            if (success && safeToRemove && fixtureCreated && Directory.Exists(fixture))
            {
                try
                {
                    EnsureNoReparsePoints(fixture);
                    if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(fixture)), parent, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Fixture cleanup boundary rejected.");
                    Directory.Delete(fixture, recursive: true);
                }
                catch (Exception exception)
                {
                    success = false;
                    Console.Error.WriteLine("CONFIG_PG_CAS_FIXTURE_CLEANUP_FAILED=" + exception.GetType().Name);
                }
            }
            else if (fixtureCreated && Directory.Exists(fixture))
            {
                try
                {
                    WriteFailureRecord(fixture, stage, failureCategory);
                }
                catch (Exception)
                {
                    Console.Error.WriteLine("CONFIG_PG_CAS_FAILURE_RECORD_WRITE=FAILED");
                }
                Console.Error.WriteLine("CONFIG_PG_CAS_FIXTURE_RETAINED=" + fixture);
            }
            runGate?.Dispose();
        }

        Console.WriteLine("CONFIG_PG_CAS_ACCEPTANCE=" + (success ? "PASS" : "FAILED"));
        return success ? 0 : 1;
    }

    private static void ConfigurePrivateProbeEnvironment(
        ProcessStartInfo start,
        string temporaryRoot,
        Func<string, string?> getHostEnvironmentVariable)
    {
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR" })
        {
            if (getHostEnvironmentVariable(name) is { Length: > 0 } value)
                start.Environment[name] = value;
        }
        start.Environment["TEMP"] = temporaryRoot;
        start.Environment["TMP"] = temporaryRoot;
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
    }

    private static string GetFailureCategory(Exception exception) => exception switch
    {
        TimeoutException => "Timeout",
        InvalidDataException => "InvalidData",
        UnauthorizedAccessException or System.Security.SecurityException => "Security",
        PlatformNotSupportedException => "Platform",
        System.Text.Json.JsonException => "Configuration",
        IOException => "Io",
        _ => "Other",
    };

    private static string? GetSanitizedProbeFailureMarker(byte[] errorOutput)
    {
        foreach (var rawLine in Encoding.UTF8.GetString(errorOutput).Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            var fields = line.Split(' ', StringSplitOptions.None);
            if (fields.Length != 3 || fields[0] != "CONFIG_PG_CAS_PROBE_FAILURE" ||
                !fields[1].StartsWith("step=", StringComparison.Ordinal) ||
                !fields[2].StartsWith("exception=", StringComparison.Ordinal))
                continue;

            var step = fields[1]["step=".Length..];
            var exceptionClass = fields[2]["exception=".Length..];
            if (IsAllowedProbeStep(step) && IsAllowedProbeExceptionClass(exceptionClass))
                return $"CONFIG_PG_CAS_PROBE_FAILURE step={step} exception={exceptionClass}";
        }
        return null;
    }

    private static bool ContainsExactLine(byte[] output, string expected) =>
        Encoding.UTF8.GetString(output).Split('\n').Any(line =>
            string.Equals(line.EndsWith('\r') ? line[..^1] : line, expected, StringComparison.Ordinal));

    private static bool IsAllowedProbeStep(string step) => step is
        "arguments" or "request-validate" or "import-fastapi" or "import-sqlalchemy" or
        "import-storage-adapter" or "import-settings-manager" or "engine-create" or "storage-create" or
        "db-identity" or "db-identity-address" or "db-identity-port" or "seed-users" or
        "config-a-create" or "config-b-create" or
        "delete-recreate" or "concurrent-cas" or "settings-update" or "host-change" or
        "user-isolation" or "default-switch" or "cleanup-dispose" or "cleanup-delete-users" or
        "cleanup-engine-dispose" or "cleanup-environment";

    private static void TestProbeFailureMarkerSanitization()
    {
        foreach (var step in new[] { "db-identity", "db-identity-address", "db-identity-port" })
        {
            var marker = Encoding.UTF8.GetBytes(
                $"CONFIG_PG_CAS_PROBE_FAILURE step={step} exception=RuntimeError\n");
            if (GetSanitizedProbeFailureMarker(marker) !=
                $"CONFIG_PG_CAS_PROBE_FAILURE step={step} exception=RuntimeError")
                throw new InvalidOperationException("A fixed database identity diagnostic marker was rejected.");
        }

        foreach (var marker in new[]
                 {
                     "CONFIG_PG_CAS_PROBE_FAILURE step=db-identity-address exception=RuntimeError detail=secret",
                     "CONFIG_PG_CAS_PROBE_FAILURE step=db-identity-port exception=PrivateError",
                     "CONFIG_PG_CAS_PROBE_FAILURE step=db-identity-address exception=RuntimeError\\nraw traceback",
                 })
        {
            if (GetSanitizedProbeFailureMarker(Encoding.UTF8.GetBytes(marker)) is not null)
                throw new InvalidOperationException("An untrusted database identity diagnostic marker was accepted.");
        }
    }

    private static bool IsAllowedProbeExceptionClass(string exceptionClass) => exceptionClass is
        "AssertionError" or "AttributeError" or "DBAPIError" or "FileNotFoundError" or "HTTPException" or
        "ImportError" or
        "IntegrityError" or "InterfaceError" or "JSONDecodeError" or "ModuleNotFoundError" or
        "OperationalError" or "OSError" or "PermissionError" or "ProgrammingError" or
        "RuntimeError" or "SyntaxError" or "TimeoutError" or "TypeError" or "ValueError" or "Other";

    private static void WriteFailureRecord(string fixture, string stage, string category)
    {
        var contents = "Portable configuration CAS Host acceptance failed.\n" +
            $"Stage: {stage}\n" +
            $"Category: {category}\n" +
            "Only synthetic fixture users and keys are present.\n" +
            "Retention: up to 7 days; do not remove while any owned process identity is active or unknown.\n";
        var basePath = Path.Combine(fixture, "CONFIG_PG_CAS_FAILURE.txt");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var path = attempt == 0
                ? basePath
                : Path.Combine(fixture, $"CONFIG_PG_CAS_FAILURE-{Guid.NewGuid():N}.txt");
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another diagnostic already owns this name; reserve a different name on the next pass.
                continue;
            }
            using (stream)
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(contents);
            return;
        }
        throw new IOException("Unable to reserve a unique sanitized CAS failure record name.");
    }

    private static async Task<byte[]> ReadBoundedOutputAsync(Stream source, int maximumBytes)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 4096));
        var buffer = new byte[4096];
        var exceeded = false;
        while (true)
        {
            var read = await source.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
            {
                exceeded = true;
                var remaining = maximumBytes - (int)output.Length;
                if (remaining > 0) output.Write(buffer, 0, remaining);
                continue;
            }
            output.Write(buffer, 0, read);
        }
        if (exceeded) throw new InvalidDataException("Private probe output exceeded its bounded size.");
        return output.ToArray();
    }

    private static string MakeDsn(string username, string password, int port, string database) =>
        $"postgresql://{username}:{Uri.EscapeDataString(password)}@127.0.0.1:{port}/{database}";

    private static void RequirePortAvailable(int port)
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
        }
        catch (System.Net.Sockets.SocketException exception)
        {
            throw new IOException($"Required loopback test port {port} is unavailable.", exception);
        }
    }

    private static void RequireNoMatchingProcesses(
        string repository,
        PortableBundleDescriptor bundle,
        string postgresRoot,
        string fixture)
    {
        ConfigPostgresCasProcessGuard.RequireNoOwnedProcesses(
            repository, bundle.PythonExecutable, postgresRoot, fixture);
    }

    private static void RequireDataPathUnclaimed(string postgresDataRoot)
    {
        RequireNoReparsePointsIncludingAncestors(postgresDataRoot);
        var postmasterPid = Path.Combine(postgresDataRoot, "postmaster.pid");
        try
        {
            _ = File.GetAttributes(postmasterPid);
            throw new IOException("The isolated PostgreSQL data directory already has a postmaster identity marker.");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException("Unable to establish ownership of the isolated PostgreSQL data directory.", exception);
        }
    }

    private static void RequirePreflightResourcesAvailable(string postgresDataRoot, int webPort) =>
        ConfigPostgresCasResourceGuard.RequireAvailable(
            postgresDataRoot,
            PortableInstanceCatalog.DefaultPostgresPort,
            webPort,
            RequireDataPathUnclaimed,
            RequirePortAvailable);

    private static void RequireNoReparsePointsIncludingAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Acceptance path contains a reparse point.");
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException exception)
            {
                throw new IOException("Unable to establish acceptance path reparse-point status.", exception);
            }
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static void RestrictFixtureDirectory(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User
            ?? throw new IOException("The current Windows user SID could not be resolved for the private fixture ACL.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
        RequireCurrentUserFixtureAcl(path);
    }

    private static void RequireCurrentUserFixtureAcl(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User
            ?? throw new IOException("The current Windows user SID could not be resolved for the private fixture ACL.");
        var security = new DirectoryInfo(path).GetAccessControl();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().ToArray();
        if (!security.AreAccessRulesProtected || rules.Length != 1 ||
            rules[0].IdentityReference != sid || rules[0].AccessControlType is not AccessControlType.Allow ||
            !rules[0].FileSystemRights.HasFlag(FileSystemRights.FullControl))
            throw new IOException("The acceptance fixture DACL is not private to the current Windows user.");
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Real portable configuration CAS acceptance requires Windows.");
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Acceptance path contains a reparse point.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current)!;
        }
    }
}
