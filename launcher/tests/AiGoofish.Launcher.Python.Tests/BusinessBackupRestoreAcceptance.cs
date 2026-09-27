using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiGoofish.Launcher.Core;
using AiGoofish.Launcher.Platform.Windows;

internal static class BusinessBackupRestoreAcceptance
{
    private const string FixtureParentRelativePath = ".tmp/tests/portable-backup-restore-host-e2e";
    private const string LegacyRetainedFixtureLeaf = "20260923-luna";
    private const string SyntheticUsername = "portable_restore_fixture_admin";
    private const string KeyCheckRelativePath = "assets/portable-backup-restore-key-check.bin";
    private const long ExpectedGrowthBytes = 1024L * 1024 * 1024;
    private const int MaximumProbeOutputBytes = 1024 * 1024;
    private static readonly Regex ReleaseIdPattern = new(
        "\\Aacceptance-frozen-[0-9]{8}-p1-r(?:[3-9]|[1-9][0-9]+)\\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsAllowedReleaseId(string releaseId)
    {
        if (releaseId is null)
            return false;

        if (!ReleaseIdPattern.IsMatch(releaseId))
            return false;

        var datePart = releaseId.Substring("acceptance-frozen-".Length, 8);
        return DateOnly.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    public static bool TryParseWebPort(string value, out int webPort)
    {
        webPort = 0;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out webPort) &&
            IsAllowedWebPort(webPort);
    }

    private static bool IsAllowedWebPort(int webPort) =>
        webPort is >= 1024 and <= 65535 &&
        webPort != PortableInstanceCatalog.DefaultPostgresPort &&
        webPort != PortableInstanceCatalog.DefaultWebPort;

    public static void RunFixturePathSelfTests(string repository)
    {
        var testParent = Path.GetFullPath(Path.Combine(repository, ".tmp", "tests", "portable-backup-fixture-path-selftest"));
        EnsureNoReparsePoints(testParent);
        var createdTestParent = !Directory.Exists(testParent);
        Directory.CreateDirectory(testParent);
        EnsureNoReparsePoints(testParent);
        var testRoot = Path.Combine(testParent, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(testRoot);
            var releaseId = "acceptance-frozen-20260924-p1-r3";
            var fixture = GetFixturePath(testRoot, releaseId);
            var expectedFixture = Path.GetFullPath(Path.Combine(
                testRoot,
                FixtureParentRelativePath.Replace('/', Path.DirectorySeparatorChar),
                releaseId));
            if (!string.Equals(fixture, expectedFixture, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The r3 fixture path is not release-scoped under the approved parent.");

            var isolated = GetFixturePath(testRoot, releaseId, "retry-1");
            if (isolated != Path.Combine(Path.GetDirectoryName(fixture)!, "run-retry-1", releaseId))
                throw new InvalidOperationException("The retry fixture escaped its run-scoped parent.");
            foreach (var invalid in new[] { "", "..", "../outside", "a/b", "A", "a b", "C:", new string('a', 65) })
            {
                var rejected = false;
                try { _ = GetFixturePath(testRoot, releaseId, invalid); }
                catch (IOException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("Unsafe fixture run id accepted.");
            }

            var legacyFixture = Path.Combine(testRoot,
                FixtureParentRelativePath.Replace('/', Path.DirectorySeparatorChar),
                LegacyRetainedFixtureLeaf);
            Directory.CreateDirectory(legacyFixture);
            var sentinelPath = Path.Combine(legacyFixture, "retained-pass-fixture-sentinel.txt");
            var sentinelBytes = Encoding.UTF8.GetBytes("retained r1 fixture must remain untouched\n");
            File.WriteAllBytes(sentinelPath, sentinelBytes);
            var originalSentinel = File.ReadAllBytes(sentinelPath);
            if (string.Equals(fixture, Path.GetFullPath(legacyFixture), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The r3 fixture path collides with the retained legacy fixture.");

            var fixtureParent = Path.GetDirectoryName(fixture)!;
            var firstClaim = AcquireFixtureClaim(fixtureParent, releaseId);
            try
            {
                ExpectFixtureClaimRejection(fixtureParent, releaseId);
                if (Directory.Exists(fixture) || File.Exists(fixture))
                    throw new InvalidOperationException("The concurrent-claim self-test unexpectedly created the release fixture.");
            }
            finally
            {
                firstClaim.Dispose();
            }

            var resumedClaim = AcquireFixtureClaim(fixtureParent, releaseId);
            resumedClaim.Dispose();

            var mutexName = GetFixtureClaimMutexName(fixtureParent, releaseId);
            using (var abandonmentAnchor = new Mutex(initiallyOwned: false, mutexName))
            {
                SimulateAbandonedFixtureClaim(abandonmentAnchor);
                using var recoveredClaim = AcquireFixtureClaim(fixtureParent, releaseId);
                if (!recoveredClaim.WasAbandoned)
                    throw new InvalidOperationException("An abandoned fixture claim was not detected during safe recovery.");
            }

            ExpectFixtureModeRejection(fixtureExists: true, resumeRetainedFixture: false, "duplicate one-shot fixture");
            ExpectFixtureModeRejection(fixtureExists: false, resumeRetainedFixture: true, "resume without retained fixture");
            RequireFixtureMode(fixtureExists: true, resumeRetainedFixture: true);
            if (!File.ReadAllBytes(sentinelPath).AsSpan().SequenceEqual(originalSentinel))
                throw new InvalidOperationException("The retained legacy fixture sentinel changed during the release-scope self-test.");

            foreach (var invalidReleaseId in new[]
            {
                "acceptance-frozen-20260924-p1-r2",
                "acceptance-frozen-20260924-p1-r3/../outside",
                "acceptance-frozen-20260924-p1-r3\\\\outside",
                "acceptance-frozen-20260924-p1-r3\n",
                "acceptance-frozen-20260230-p1-r3",
            })
            {
                if (IsAllowedReleaseId(invalidReleaseId))
                    throw new InvalidOperationException("Release-id validation accepted an unsafe or unsupported value.");
                ExpectInvalidFixturePath(testRoot, invalidReleaseId);
            }
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                EnsureNoReparsePoints(testRoot);
                Directory.Delete(testRoot, recursive: true);
            }
            if (createdTestParent && Directory.Exists(testParent) && !Directory.EnumerateFileSystemEntries(testParent).Any())
                Directory.Delete(testParent);
        }

        Console.WriteLine("BUSINESS_BACKUP_RESTORE_FIXTURE_PATH_SELFTEST=PASS");
    }

    public static void RunWebPortFixturePolicySelfTests()
    {
        foreach (var value in new[] { "58124", "65535", "1024" })
        {
            if (!TryParseWebPort(value, out _))
                throw new InvalidOperationException("The explicit Web-port parser rejected a valid candidate.");
        }
        foreach (var value in new[] { "", "abc", "-1", "+58124", "1023", "65536", "55432", "58000" })
        {
            if (TryParseWebPort(value, out _))
                throw new InvalidOperationException("The explicit Web-port parser accepted an invalid or reserved candidate.");
        }

        var instanceId = Guid.NewGuid();
        var valid = new PortableWebPortSettings(instanceId, 3, 58124, null, null);
        ValidateRetainedWebPortSettings(valid, instanceId, 58124, reparsePointDetected: false);
        ExpectRetainedWebPortSettingsRejection(valid with { EffectivePort = 58125 }, instanceId, 58124, false,
            "effective port mismatch");
        ExpectRetainedWebPortSettingsRejection(valid with { InstanceId = Guid.NewGuid() }, instanceId, 58124, false,
            "instance identity mismatch");
        ExpectRetainedWebPortSettingsRejection(valid with { Revision = 2 }, instanceId, 58124, false,
            "revision mismatch");
        ExpectRetainedWebPortSettingsRejection(valid with { PendingPort = 58125 }, instanceId, 58124, false,
            "pending port");
        ExpectRetainedWebPortSettingsRejection(valid with
        {
            Applying = new PortableWebPortApplyIntent(Guid.NewGuid(), 58000, 58124, 2),
        }, instanceId, 58124, false, "applying intent");
        ExpectRetainedWebPortSettingsRejection(valid, instanceId, 58124, true, "reparse point");
        Console.WriteLine("BUSINESS_BACKUP_RESTORE_WEB_PORT_SELFTEST=PASS");
    }

    public static void RunProcessGuardSelfTests()
    {
        if (CaptureVerifiedRunnerPythonAncestors().Count == 0)
            throw new InvalidOperationException("The safe no-dialog Python runner was not verified in the current process ancestry.");

        var ancestors = new Dictionary<int, ConfigCasAncestorIdentity>
        {
            [701] = new ConfigCasAncestorIdentity(701, 123456789),
        };
        RequireProcessGuardResult(
            new ProcessEvidence(701, "python", 123456789), ancestors, expectedAllowed: true, "exact runner Python ancestor");
        RequireProcessGuardResult(
            new ProcessEvidence(701, "pythonw", 123456789), ancestors, expectedAllowed: true, "exact runner Pythonw ancestor");
        RequireProcessGuardResult(
            new ProcessEvidence(701, "python", 123456790), ancestors, expectedAllowed: false, "reused PID with different creation time");
        RequireProcessGuardResult(
            new ProcessEvidence(701, "python", null), ancestors, expectedAllowed: false, "unknown creation time");
        RequireProcessGuardResult(
            new ProcessEvidence(702, "python", 123456789), ancestors, expectedAllowed: false, "unrelated Python process");
        RequireProcessGuardResult(
            new ProcessEvidence(701, "postgres", 123456789), ancestors, expectedAllowed: false, "PostgreSQL ancestor");
        Console.WriteLine("BUSINESS_BACKUP_RESTORE_PROCESS_GUARD_SELFTEST=PASS");
    }

    private static void RequireProcessGuardResult(
        ProcessEvidence process,
        IReadOnlyDictionary<int, ConfigCasAncestorIdentity> ancestors,
        bool expectedAllowed,
        string caseName)
    {
        var actualAllowed = IsVerifiedRunnerPythonAncestor(process, ancestors);
        if (actualAllowed != expectedAllowed)
            throw new InvalidOperationException("The runner process guard mishandled " + caseName + ".");
    }

    private static bool IsVerifiedRunnerPythonAncestor(
        ProcessEvidence process,
        IReadOnlyDictionary<int, ConfigCasAncestorIdentity> ancestors)
    {
        if (!IsPythonProcessName(process.Name) || process.StartedAtUtcTicks is not { } startedAt ||
            !ancestors.TryGetValue(process.ProcessId, out var ancestor))
            return false;

        return ancestor.ProcessId == process.ProcessId && ancestor.StartedAtUtcTicks == startedAt;
    }

    private static IReadOnlyDictionary<int, ConfigCasAncestorIdentity> CaptureVerifiedRunnerPythonAncestors()
    {
        var ancestors = ConfigPostgresCasProcessGuard.ReadCurrentProcessAncestors();
        var verified = new Dictionary<int, ConfigCasAncestorIdentity>();
        foreach (var ancestor in ancestors.Values)
        {
            try
            {
                using var process = Process.GetProcessById(ancestor.ProcessId);
                if (!IsPythonProcessName(process.ProcessName))
                    continue;
                var startedAt = process.StartTime.ToUniversalTime().Ticks;
                if (startedAt == ancestor.StartedAtUtcTicks)
                    verified.Add(ancestor.ProcessId, ancestor);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // An ancestor that cannot be identified by PID, image name, and creation time is not exempted.
            }
        }

        return verified;
    }

    private static bool IsPythonProcessName(string name) =>
        string.Equals(name, "python", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "pythonw", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(
        string bundleRootArgument,
        string requestedReleaseId,
        int requestedWebPort,
        bool resumeRetainedFixture,
        string? fixtureRunId = null)
    {
        if (!IsAllowedWebPort(requestedWebPort))
            throw new IOException("The explicit test Web port is invalid or reserved.");

        var repository = Path.GetFullPath(Environment.GetEnvironmentVariable("AIGOOFISH_REPOSITORY_ROOT")
            ?? throw new InvalidOperationException("Explicit repository root is required."));
        var bundleRoot = Path.GetFullPath(bundleRootArgument);
        var fixture = GetFixturePath(repository, requestedReleaseId, fixtureRunId);
        var parent = Path.GetDirectoryName(fixture)!;
        var probeScript = Path.Combine(repository, "launcher", "tests", "AiGoofish.Launcher.Python.Tests", "portable_backup_restore_probe.py");
        var state = new RunState(fixture);
        var stage = "preflight";
        var fixtureCreated = false;
        var preserveExistingFixture = false;
        RetentionWindow? retainedWindow = null;
        var ownedProcessIdentities = new List<OwnedProcessIdentity>();
        RealPortableStackHost? sourceHost = null;
        PortableRestoreSession? restoreSession = null;
        RealPortableStackHost? targetHost = null;
        InstanceSecrets? sourceSecrets = null;
        string? backupPassphrase = null;
        var succeeded = false;
        FixtureClaim? fixtureClaim = null;
        IReadOnlyDictionary<int, ConfigCasAncestorIdentity> runnerPythonAncestors =
            new Dictionary<int, ConfigCasAncestorIdentity>();

        try
        {
            stage = "preflight-retention";
            RunRetentionMetadataSelfTests(repository);
            RequireWindows();
            runnerPythonAncestors = CaptureVerifiedRunnerPythonAncestors();
            stage = "preflight-location";
            RequireReleaseBundleLocation(repository, bundleRoot, requestedReleaseId);
            RequireExactFixturePath(repository, requestedReleaseId, fixture, fixtureRunId);
            EnsureNoReparsePoints(parent);
            EnsureNoReparsePoints(fixture);
            EnsureNoReparsePoints(bundleRoot);
            Directory.CreateDirectory(parent);
            EnsureNoReparsePoints(parent);
            fixtureClaim = AcquireFixtureClaim(parent, requestedReleaseId);
            var fixtureExists = Directory.Exists(fixture) || File.Exists(fixture);
            stage = "preflight-fixture-mode";
            RequireFixtureMode(fixtureExists, resumeRetainedFixture);
            CheckFreeSpace(fixture);
            stage = "preflight-processes";
            RequireNoRunningProcesses(runnerPythonAncestors, "postgres", "pg_ctl", "python", "pythonw");
            stage = "preflight-ports";
            RequireLoopbackPortsAvailable(PortableInstanceCatalog.DefaultPostgresPort, requestedWebPort);
            if (!File.Exists(probeScript))
                throw new FileNotFoundException("The test-only private-stdio probe is missing.");

            stage = "preflight-bundle";
            var verifiedBundle = await PortableBundleDescriptor.LoadAndVerifyAsync(bundleRoot).ConfigureAwait(false);
            if (!string.Equals(verifiedBundle.ReleaseId, requestedReleaseId, StringComparison.Ordinal))
                throw new InvalidOperationException("The verified bundle release id does not match the explicitly requested P1 package.");

            if (resumeRetainedFixture)
            {
                retainedWindow = ValidateRetainedFixture(fixture, parent, requestedWebPort);
                fixtureCreated = true;
                preserveExistingFixture = true;
                state.EnablePersistence();
            }
            else
            {
                Directory.CreateDirectory(parent);
                EnsureNoReparsePoints(parent);
                Directory.CreateDirectory(fixture);
                EnsureNoReparsePoints(fixture);
                fixtureCreated = true;
                RestrictFixtureDirectory(fixture);
                Directory.CreateDirectory(Path.Combine(fixture, "diagnostics"));
                Directory.CreateDirectory(Path.Combine(fixture, "backup-output"));
                Directory.CreateDirectory(Path.Combine(fixture, "python-temp"));
                state.EnablePersistence();
                WriteRetentionMetadata(fixture, state, "IN_PROGRESS", retainedWindow: null);
            }
            state.Log("PREFLIGHT_PASS bundle=" + verifiedBundle.ReleaseId);
            state.Log("FIXTURE=" + fixture);
            state.Log("BUNDLE_CURRENT_SHA256=" + HashFile(Path.Combine(bundleRoot, "current.json")));
            state.Log("BUNDLE_MANIFEST_SHA256=" + HashFile(Path.Combine(bundleRoot, "bundle-manifest.json")));

            // Keep every executable component rooted in the verified frozen
            // package, while redirecting only its user-data container here.
            var testBundle = verifiedBundle with { BundleRoot = Path.Combine(fixture, "bundle") };
            var originalPointer = ReadPointerSnapshot(testBundle.BundleRoot);
            state.Log("ACTIVE_POINTER_BEFORE=" + originalPointer.Description);

            if (!resumeRetainedFixture)
            {
                stage = "fixture-web-port-seed";
                SeedFreshFixtureWebPortSettings(testBundle.BundleRoot, requestedWebPort);
            }

            stage = "source-create";
            sourceHost = RealPortableStackHost.Create(testBundle);
            var sourceRoot = sourceHost.DataRoot;
            var sourceLease = GetPrivateField<InstanceDataRootLease>(sourceHost, "_lease");
            sourceSecrets = new WindowsInstanceSecretsStore().Load(sourceLease);

            stage = resumeRetainedFixture ? "resume-source-web-start" : "source-web-start";
            await RequireStartedAsync(sourceHost, ownedProcessIdentities).ConfigureAwait(false);
            // Business assets belong to an initialized instance; pre-init traces
            // must remain a production refusal, not be exempted for this fixture.
            if (!resumeRetainedFixture)
            {
                SeedSyntheticFiles(sourceRoot);
                var keySeal = await RunProbeAsync(
                    verifiedBundle,
                    state,
                    probeScript,
                    Path.Combine(fixture, "python-temp"),
                    new { action = "seal_key_fixture", data_root = sourceRoot, master_key = sourceSecrets.EncryptionMasterKey }).ConfigureAwait(false);
                if (!keySeal.RootElement.TryGetProperty("sealed", out var sealedValue) || !sealedValue.GetBoolean())
                    throw new InvalidDataException("The synthetic key-encrypted business file was not created.");
            }

            if (resumeRetainedFixture)
            {
                if (sourceHost.SetupRequired)
                    throw new InvalidOperationException("The retained source synthetic admin is missing or the setup gate reopened.");
            }
            else
            {
                if (!sourceHost.SetupRequired || string.IsNullOrWhiteSpace(sourceHost.SetupToken))
                    throw new InvalidOperationException("The isolated source did not enter the first-admin fixture state.");
                await CreateSyntheticAdminAsync(sourceHost, CreateSyntheticPassword()).ConfigureAwait(false);
            }
            var sourceReadiness = await sourceHost.RefreshReadinessAsync().ConfigureAwait(false);
            if (sourceReadiness.State is not PythonReadinessState.Ready || sourceReadiness.SetupRequired is not false)
                throw new InvalidOperationException("The isolated source Web readiness did not reach Ready with completed synthetic setup.");
            if (sourceHost.SetupRequired)
                throw new InvalidOperationException("The refreshed source readiness still reports first-admin setup required.");
            if (resumeRetainedFixture)
            {
                var keyCheck = await RunProbeAsync(
                    verifiedBundle,
                    state,
                    probeScript,
                    Path.Combine(fixture, "python-temp"),
                    new { action = "verify_key_fixture", data_root = sourceRoot, master_key = sourceSecrets.EncryptionMasterKey }).ConfigureAwait(false);
                if (!keyCheck.RootElement.GetProperty("key_check").GetBoolean())
                    throw new InvalidDataException("The retained synthetic encrypted business-key fixture did not validate.");
            }

            stage = "source-snapshot";
            var sourceFiles = SnapshotBusinessFiles(sourceRoot);
            if (!sourceFiles.ContainsKey(KeyCheckRelativePath) || sourceFiles.Count < 4)
                throw new InvalidDataException("The synthetic business file set was not fully seeded.");
            var sourceCountsResult = await RunProbeAsync(
                verifiedBundle,
                state,
                probeScript,
                Path.Combine(fixture, "python-temp"),
                new
                {
                    action = "table_counts",
                    admin_dsn = MakeDsn("aigoofish_bootstrap", sourceSecrets.PostgresBootstrapAdminPassword,
                        PortableInstanceCatalog.DefaultPostgresPort, "aigoofish"),
                }).ConfigureAwait(false);
            var sourceTableCounts = ParseCounts(sourceCountsResult.RootElement.GetProperty("table_counts"));
            if (sourceTableCounts.Count == 0 || !sourceTableCounts.TryGetValue("users", out var syntheticUserCount) || syntheticUserCount != 1)
                throw new InvalidDataException("The source PostgreSQL fixture does not contain exactly one synthetic administrator.");
            var sourceSecretPath = new WindowsInstanceSecretsStore().GetSecretsPath(sourceLease);
            var sourceProtectedSecretsHash = HashFile(sourceSecretPath);
            var sourcePostgresData = Path.Combine(sourceRoot, "postgres", "cluster");
            string sourceMarkerHash;
            Dictionary<string, FileFingerprint> sourcePostgresTree;
            var sourceInstanceId = sourceLease.InstanceId;

            backupPassphrase = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var archive = Path.Combine(fixture, "backup-output", "source-fixture.gfbk");
            stage = "host-business-backup";
            var backup = await sourceHost.CreateBusinessBackupAndStopAsync(archive, backupPassphrase).ConfigureAwait(false);
            if (!File.Exists(archive) || !string.Equals(HashFile(archive), backup.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("The Host backup result hash does not match the published archive.");
            AddIdentity(ownedProcessIdentities, sourceHost.PostgresProcessIdentity);
            AddIdentity(ownedProcessIdentities, sourceHost.PythonProcessIdentity);
            if (ownedProcessIdentities.Any(identity => PythonProcessIdentityProbe.GetRunState(identity) is not PythonIdentityRunState.NotRunning))
                throw new InvalidOperationException("A source process identity is not confirmed stopped after Host backup.");

            stage = "source-host-release";
            await sourceHost.DisposeAsync().ConfigureAwait(false);
            sourceHost = null;
            var sourceMarkerPath = Path.Combine(sourcePostgresData, ".aigoofish-cluster.json");
            sourceMarkerHash = HashFile(sourceMarkerPath);
            sourcePostgresTree = SnapshotDirectoryTree(sourcePostgresData);
            state.Log("SOURCE_PGDATA_BASELINE_CAPTURED_AFTER_HOST_SMART_STOP");
            using (var sourceLeaseAfterStop = InstanceDataRootLease.Acquire(sourceRoot))
            {
                var reloaded = new WindowsInstanceSecretsStore().Load(sourceLeaseAfterStop);
                RequireSameBusinessKeys(sourceSecrets, reloaded, "source DPAPI readback");
            }
            state.Log("HOST_BACKUP_PASS sha256=" + backup.Sha256 + " files=" + backup.FileCount);

            stage = "archive-secret-scan";
            foreach (var value in sourceSecrets.Values().Append(backupPassphrase))
            {
                var raw = Encoding.UTF8.GetBytes(value);
                try
                {
                    if (ContainsBytes(archive, raw))
                        throw new InvalidDataException("An unprotected source secret occurs in the encrypted backup bytes.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(raw);
                }
            }

            stage = "restore-session";
            restoreSession = RealPortableStackHost.CreateBusinessRestoreSession(testBundle);
            var previewSnapshot = await restoreSession.RestoreAndPreviewAsync(
                archive,
                backupPassphrase,
                PortableRestoreSession.RequiredBackupTrustText).ConfigureAwait(false);
            var preview = previewSnapshot.Preview
                ?? throw new InvalidDataException("The restore session returned no maintenance preview.");
            if (previewSnapshot.State is not PortableRestoreState.AwaitingUserConfirmation ||
                !preview.MaintenanceAuditPassed || !Guid.TryParse(preview.SourceInstanceId, out var previewSourceId) ||
                previewSourceId != sourceInstanceId ||
                preview.TargetInstanceId == sourceInstanceId ||
                !string.Equals(preview.BackupSha256, backup.Sha256, StringComparison.Ordinal) ||
                preview.RestoredFileCount != sourceFiles.Count)
                throw new InvalidDataException("The restored target identity or maintenance summary is invalid.");
            RequireCountsEqual(sourceTableCounts, preview.TableCounts);
            var targetRoot = ResolveTargetRoot(testBundle.BundleRoot, preview.TargetRelativeRoot);
            var restoredFiles = SnapshotBusinessFiles(targetRoot);
            RequireSnapshotsEqual(sourceFiles, restoredFiles, "restored business files");
            var targetSecretPath = Path.Combine(targetRoot, WindowsInstanceSecretsStore.RelativeSecretsPath.Replace('/', Path.DirectorySeparatorChar));
            var targetProtectedSecretsHash = HashFile(targetSecretPath);
            if (string.Equals(sourceProtectedSecretsHash, targetProtectedSecretsHash, StringComparison.Ordinal))
                throw new InvalidDataException("The destination DPAPI envelope was copied from the source instead of re-protected.");

            var restoreTarget = GetPrivateField<PortableRestoreTarget>(restoreSession, "_target");
            var targetSecrets = new WindowsInstanceSecretsStore().Load(restoreTarget.Lease);
            RequireSameBusinessKeys(sourceSecrets, targetSecrets, "target-user DPAPI import/readback", requireFreshDatabaseCredentials: true);
            var targetKeyCheck = await RunProbeAsync(
                verifiedBundle,
                state,
                probeScript,
                Path.Combine(fixture, "python-temp"),
                new { action = "verify_key_fixture", data_root = targetRoot, master_key = targetSecrets.EncryptionMasterKey }).ConfigureAwait(false);
            if (!targetKeyCheck.RootElement.GetProperty("key_check").GetBoolean())
                throw new InvalidDataException("The restored master key could not decrypt the synthetic business-key fixture.");

            var beforeConfirmPointer = ReadPointerSnapshot(testBundle.BundleRoot);
            RequirePointerUnchanged(originalPointer, beforeConfirmPointer, "before explicit activation confirmation");
            if (!Directory.Exists(sourceRoot) ||
                HashFile(Path.Combine(sourceRoot, "config", "instance-secrets.dpapi")) != sourceProtectedSecretsHash ||
                HashFile(Path.Combine(sourceRoot, "postgres", "cluster", ".aigoofish-cluster.json")) != sourceMarkerHash)
                throw new InvalidDataException("The old source instance changed during isolated restore.");
            RequireSnapshotsEqual(sourcePostgresTree, SnapshotDirectoryTree(sourcePostgresData), "old source PostgreSQL cluster before activation");
            RequireSnapshotsEqual(sourceFiles, SnapshotBusinessFiles(sourceRoot), "old source business files before activation");

            var beforeBadConfirmation = ReadPointerSnapshot(testBundle.BundleRoot);
            try
            {
                _ = await restoreSession.ConfirmActivationAsync("确认").ConfigureAwait(false);
                throw new InvalidOperationException("A partial activation phrase was unexpectedly accepted.");
            }
            catch (PortableRestoreException exception) when (exception.Code == "USER_CONFIRMATION_REQUIRED")
            {
                RequirePointerUnchanged(beforeBadConfirmation, ReadPointerSnapshot(testBundle.BundleRoot), "after rejected partial confirmation");
            }

            // ConfirmActivation independently proves that the candidate is
            // still stopped and owns its lease before the active-pointer CAS.
            stage = "explicit-activation-confirmation";
            var activated = await restoreSession.ConfirmActivationAsync(
                PortableRestoreSession.RequiredConfirmationText).ConfigureAwait(false);
            if (activated.State is not PortableRestoreState.Activated)
                throw new InvalidOperationException("Explicit activation did not commit the candidate pointer.");
            await restoreSession.DisposeAsync().ConfigureAwait(false);
            restoreSession = null;

            var afterConfirmPointer = ReadPointerSnapshot(testBundle.BundleRoot);
            if (afterConfirmPointer.Bytes is null)
                throw new InvalidDataException("The active pointer was not created after explicit confirmation.");
            var selected = new PortableInstanceCatalog(testBundle.BundleRoot).ResolveActiveInstance();
            if (selected.InstanceId != preview.TargetInstanceId ||
                !string.Equals(Path.GetFullPath(selected.DataRoot), Path.GetFullPath(targetRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The active pointer does not resolve to the confirmed restore target.");
            if (string.Equals(originalPointer.Hash, afterConfirmPointer.Hash, StringComparison.Ordinal))
                throw new InvalidDataException("The active pointer did not change after explicit confirmation.");

            stage = "old-source-preservation-after-confirmation";
            if (!Directory.Exists(sourceRoot) ||
                HashFile(Path.Combine(sourceRoot, "config", "instance-secrets.dpapi")) != sourceProtectedSecretsHash ||
                HashFile(Path.Combine(sourceRoot, "postgres", "cluster", ".aigoofish-cluster.json")) != sourceMarkerHash)
                throw new InvalidDataException("The old source instance was overwritten after activation.");
            RequireSnapshotsEqual(sourceFiles, SnapshotBusinessFiles(sourceRoot), "old source business files after activation");
            RequireSnapshotsEqual(sourcePostgresTree, SnapshotDirectoryTree(sourcePostgresData), "old source PostgreSQL cluster after activation");

            stage = "confirmed-target-web-start";
            targetHost = RealPortableStackHost.Create(testBundle);
            if (!string.Equals(Path.GetFullPath(targetHost.DataRoot), Path.GetFullPath(targetRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The regular Host factory did not select the confirmed restore target.");
            await RequireStartedAsync(targetHost, ownedProcessIdentities).ConfigureAwait(false);
            var targetReadiness = await targetHost.RefreshReadinessAsync().ConfigureAwait(false);
            if (targetReadiness.State is not PythonReadinessState.Ready || targetReadiness.SetupRequired is not false)
                throw new InvalidOperationException("The confirmed target did not reach normal Web Ready with restored setup state.");
            var targetCountsResult = await RunProbeAsync(
                verifiedBundle,
                state,
                probeScript,
                Path.Combine(fixture, "python-temp"),
                new
                {
                    action = "table_counts",
                    admin_dsn = MakeDsn("aigoofish_bootstrap", targetSecrets.PostgresBootstrapAdminPassword,
                        selected.PostgresPort, "aigoofish"),
                }).ConfigureAwait(false);
            RequireCountsEqual(sourceTableCounts, ParseCounts(targetCountsResult.RootElement.GetProperty("table_counts")));
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            using (var http = new HttpClient(handler) { BaseAddress = new Uri(targetHost.ManagementUrl), Timeout = TimeSpan.FromSeconds(8) })
            {
                using var rootResponse = await http.GetAsync(string.Empty).ConfigureAwait(false);
                if (rootResponse.StatusCode is not (HttpStatusCode.SeeOther or HttpStatusCode.Redirect) ||
                    rootResponse.Headers.Location?.ToString() != "/login")
                    throw new InvalidOperationException("The restored business Web root did not route to login.");
                using var loginResponse = await http.GetAsync("login").ConfigureAwait(false);
                if (loginResponse.StatusCode is not HttpStatusCode.OK)
                    throw new InvalidOperationException("The restored business login page did not return HTTP 200.");
            }
            state.Log("RESTORE_PREVIEW_PASS source=" + sourceInstanceId.ToString("D") +
                " target=" + preview.TargetInstanceId.ToString("D") +
                " tables=" + preview.TableCounts.Count + " files=" + preview.RestoredFileCount +
                " revoked_sessions=" + preview.RevokedSessionCount);
            foreach (var item in preview.TableCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                state.Log("TABLE " + item.Key + " rows=" + item.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            state.Log("FILES_AND_DPAPI_KEYS_PASS count=" + restoredFiles.Count + " master_key_check=PASS");
            state.Log("POINTER_CAS_PASS before_hash=" + (originalPointer.Hash ?? "absent") + " after_hash=" + afterConfirmPointer.Hash);
            state.Log("OLD_SOURCE_PRESERVED_PASS root=" + sourceRoot);

            stage = "target-normal-stop";
            var targetPostgresIdentity = targetHost.PostgresProcessIdentity;
            var targetPythonIdentity = targetHost.PythonProcessIdentity;
            var stop = await targetHost.Coordinator.StopAsync().ConfigureAwait(false);
            if (!stop.Succeeded || targetHost.Coordinator.Snapshot.State is not LauncherState.Stopped)
                throw new InvalidOperationException("The confirmed target did not stop through normal Host shutdown.");
            await targetHost.DisposeAsync().ConfigureAwait(false);
            targetHost = null;
            RequireIdentityStopped(targetPostgresIdentity);
            RequireIdentityStopped(targetPythonIdentity);
            ownedProcessIdentities.Clear();

            stage = "final-preservation-audit";
            using (var sourceLeaseFinal = InstanceDataRootLease.Acquire(sourceRoot))
            {
                var finalSecrets = new WindowsInstanceSecretsStore().Load(sourceLeaseFinal);
                RequireSameBusinessKeys(sourceSecrets, finalSecrets, "final old source DPAPI");
            }
            using (var targetLeaseFinal = InstanceDataRootLease.Acquire(targetRoot))
            {
                var finalSecrets = new WindowsInstanceSecretsStore().Load(targetLeaseFinal);
                RequireSameBusinessKeys(sourceSecrets, finalSecrets, "final restored target DPAPI", requireFreshDatabaseCredentials: true);
            }
            if (GetProcessesByNames("postgres", "pg_ctl", "python", "pythonw")
                    .Any(process => !IsVerifiedRunnerPythonAncestor(process, runnerPythonAncestors)))
                throw new InvalidOperationException("A PostgreSQL or Python process remains after the owned Host shutdown.");
            state.Log("NORMAL_WEB_START_STOP_PASS readiness=Ready setup_required=false login_http=200");
            state.Log("NO_PG_PYTHON_PROCESSES_REMAIN");
            succeeded = true;
        }
        catch (Exception exception)
        {
            state.Log("FAILED stage=" + stage + " type=" + exception.GetType().Name +
                " hresult=0x" + exception.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            for (Exception? detail = exception; detail is not null; detail = detail.InnerException)
            {
                state.Log("FAILURE_DETAIL type=" + detail.GetType().Name +
                    " method=" + (detail.TargetSite?.Name ?? "unknown"));
                foreach (var frame in new StackTrace(detail).GetFrames().Take(12))
                    state.Log("FAILURE_FRAME=" + frame.GetMethod()?.DeclaringType?.FullName + "." + frame.GetMethod()?.Name);
            }
            foreach (var diagnostic in (sourceHost ?? targetHost)?.Coordinator.DiagnosticEvents ?? [])
                state.Log("FAILURE_DIAGNOSTIC=" + diagnostic.Component + "/" + diagnostic.Code + "/" + diagnostic.Severity);
            if (ownedProcessIdentities.Count > 0)
                state.Log("OWNED_PROCESS_IDENTITIES=" + string.Join(",", ownedProcessIdentities.Select(identity =>
                    identity.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + identity.InstanceId.ToString("D"))));
            state.LogActiveProbeIds();
            Console.Error.WriteLine("BACKUP_RESTORE_HOST_E2E_FAILED stage=" + stage + " type=" + exception.GetType().Name);
            if (fixtureCreated)
                Console.Error.WriteLine("FIXTURE_RETAINED=" + fixture);
        }
        finally
        {
            if (targetHost is not null)
            {
                await TryOwnedStopAsync(targetHost, state, "target").ConfigureAwait(false);
            }
            if (sourceHost is not null)
            {
                await TryOwnedStopAsync(sourceHost, state, "source").ConfigureAwait(false);
            }
            if (restoreSession is not null)
            {
                try
                {
                    await restoreSession.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    state.Log("RESTORE_SESSION_DISPOSE_UNCONFIRMED type=" + cleanupError.GetType().Name);
                    succeeded = false;
                }
            }

            if (succeeded && preserveExistingFixture)
            {
                try
                {
                    EnsureNoReparsePoints(fixture);
                    WriteRetentionMetadata(fixture, state, "PASS", retainedWindow);
                    Console.Error.WriteLine("FIXTURE_RETAINED_FOR_REVIEW=" + fixture);
                }
                catch (Exception retentionError)
                {
                    succeeded = false;
                    Console.Error.WriteLine("FIXTURE_RETENTION_METADATA_FAILED type=" + retentionError.GetType().Name + " path=" + fixture);
                }
            }
            else if (succeeded && fixtureCreated)
            {
                try
                {
                    EnsureNoReparsePoints(fixture);
                    var fixtureBytes = EnumeratePlainFiles(fixture).Sum(path => new FileInfo(path).Length);
                    Directory.Delete(fixture, recursive: true);
                    state.Log("FIXTURE_CLEANED bytes=" + fixtureBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var parentInfo = new DirectoryInfo(parent);
                    if (parentInfo.Exists && !Directory.EnumerateFileSystemEntries(parent).Any())
                        parentInfo.Delete();
                }
                catch (Exception cleanupError)
                {
                    succeeded = false;
                    Console.Error.WriteLine("FIXTURE_CLEANUP_FAILED type=" + cleanupError.GetType().Name + " path=" + fixture);
                }
            }
            else if (fixtureCreated)
            {
                if (!preserveExistingFixture)
                {
                    try
                    {
                        WriteRetentionMetadata(fixture, state, "FAILED", retainedWindow: null);
                    }
                    catch (Exception retentionError)
                    {
                        Console.Error.WriteLine("FIXTURE_RETENTION_METADATA_FAILED type=" + retentionError.GetType().Name);
                    }
                }
                Console.Error.WriteLine("FIXTURE_RETAINED=" + fixture);
            }

            try
            {
                fixtureClaim?.Dispose();
            }
            catch (Exception claimReleaseError)
            {
                succeeded = false;
                Console.Error.WriteLine("FIXTURE_CLAIM_RELEASE_FAILED type=" + claimReleaseError.GetType().Name);
            }
        }

        Console.WriteLine("BUSINESS_BACKUP_RESTORE_HOST_E2E=" + (succeeded ? "PASS" : "FAILED"));
        return succeeded ? 0 : 1;
    }

    private static async Task RequireStartedAsync(RealPortableStackHost host, List<OwnedProcessIdentity> identities)
    {
        var result = await host.StartAsync().ConfigureAwait(false);
        if (!result.Succeeded || host.Coordinator.Snapshot.State is not LauncherState.Running)
        {
            AddIdentity(identities, host.PostgresProcessIdentity);
            AddIdentity(identities, host.PythonProcessIdentity);
            throw new IOException("The owned Launcher Host did not reach Running.");
        }
        AddIdentity(identities, host.PostgresProcessIdentity);
        AddIdentity(identities, host.PythonProcessIdentity);
    }

    private static async Task CreateSyntheticAdminAsync(RealPortableStackHost host, string password)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(host.ManagementUrl), Timeout = TimeSpan.FromSeconds(8) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "setup");
        request.Headers.Add("Origin", host.ManagementUrl.TrimEnd('/'));
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            username = SyntheticUsername,
            password,
            setup_token = host.SetupToken,
        });
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        try
        {
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode is not HttpStatusCode.Created)
                throw new IOException("The loopback-only synthetic first-admin setup request did not return HTTP 201.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    private static async Task<JsonDocument> RunProbeAsync(
        PortableBundleDescriptor bundle,
        RunState state,
        string script,
        string temporaryRoot,
        object request)
    {
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
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--app-root");
        start.ArgumentList.Add(bundle.ProgramRoot);
        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                start.Environment[name] = value;
        start.Environment["TEMP"] = temporaryRoot;
        start.Environment["TMP"] = temporaryRoot;
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new IOException("The fixed Python fixture probe did not start.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new IOException("The fixed Python fixture probe could not start.", exception);
        }

        state.ProbeStarted(process.Id);

        var errorDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var outputTask = ReadBoundedOutputAsync(process.StandardOutput.BaseStream, MaximumProbeOutputBytes);
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request);
        try
        {
            if (requestBytes.Length > 16 * 1024)
                throw new InvalidDataException("The bounded test-only probe request exceeded 16 KiB.");
            await process.StandardInput.BaseStream.WriteAsync(requestBytes).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync().ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            state.ProbeExited(process.Id);
            await errorDrain.ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException("The fixed Python fixture probe failed or returned oversized output.");
            try
            {
                return JsonDocument.Parse(output);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The fixed Python fixture probe returned invalid JSON.", exception);
            }
        }
        catch (TimeoutException)
        {
            state.Log("PROBE_EXIT_UNCONFIRMED pid=" + process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            throw new IOException("The fixed Python fixture probe did not exit before its bounded timeout; no process was terminated.");
        }
        finally
        {
            if (process.HasExited)
                state.ProbeExited(process.Id);
            CryptographicOperations.ZeroMemory(requestBytes);
        }
    }

    private static async Task<string> ReadBoundedOutputAsync(Stream source, int maximumBytes)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 4096));
        var buffer = new byte[4096];
        var exceeded = false;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (output.Length + read > maximumBytes)
                {
                    exceeded = true;
                    continue;
                }
                if (!exceeded)
                    await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }
            if (exceeded)
                throw new InvalidDataException("The fixed Python fixture probe exceeded the bounded stdout limit.");
            return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, checked((int)output.Length));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
        }
    }

    private static async Task TryOwnedStopAsync(RealPortableStackHost host, RunState state, string label)
    {
        try
        {
            var result = await host.Coordinator.StopAsync().ConfigureAwait(false);
            if (!result.Succeeded || host.Coordinator.Snapshot.State is not LauncherState.Stopped)
            {
                state.Log(label.ToUpperInvariant() + "_STOP_UNCONFIRMED; preserving owned host and fixture");
                return;
            }
            await host.DisposeAsync().ConfigureAwait(false);
            state.Log(label.ToUpperInvariant() + "_STOPPED");
        }
        catch (Exception exception)
        {
            state.Log(label.ToUpperInvariant() + "_STOP_UNCONFIRMED type=" + exception.GetType().Name);
        }
    }

    private static void SeedSyntheticFiles(string dataRoot)
    {
        WriteFixtureFile(dataRoot, "assets/中文恢复样本.txt", "仅用于隔离备份恢复演练。\n");
        WriteFixtureFile(dataRoot, "state/e2e-fixture.json", "{\"fixture\":\"backup-restore\",\"version\":1}\n");
        WriteFixtureFile(dataRoot, "results/e2e-fixture.txt", "synthetic-result-only\n");
        WriteFixtureFile(dataRoot, "config/app.env", "LOG_LEVEL=INFO\n");
    }

    private static void WriteFixtureFile(string root, string relativePath, string contents)
    {
        var fullRoot = Path.GetFullPath(root);
        var destination = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Synthetic business fixture escaped its owned data root.");
        EnsureNoReparsePoints(Path.GetDirectoryName(destination)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = new UTF8Encoding(false).GetBytes(contents);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
        CryptographicOperations.ZeroMemory(bytes);
    }

    private static Dictionary<string, FileFingerprint> SnapshotBusinessFiles(string dataRoot)
    {
        var result = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var directoryName in new[] { "state", "assets", "results" })
        {
            var directory = Path.Combine(dataRoot, directoryName);
            if (!Directory.Exists(directory))
                continue;
            AddDirectoryFiles(dataRoot, directory, result);
        }
        var appEnvironment = Path.Combine(dataRoot, "config", "app.env");
        if (File.Exists(appEnvironment))
            AddFile(dataRoot, appEnvironment, result);
        return result;
    }

    private static void AddDirectoryFiles(string root, string directory, Dictionary<string, FileFingerprint> result)
    {
        EnsureNoReparsePoints(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A business fixture contains a reparse point.");
            if ((attributes & FileAttributes.Directory) != 0)
                AddDirectoryFiles(root, entry, result);
            else
                AddFile(root, entry, result);
            if (result.Count > 10_000)
                throw new IOException("The business fixture exceeded the test file limit.");
        }
    }

    private static Dictionary<string, FileFingerprint> SnapshotDirectoryTree(string directory)
    {
        var result = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        AddDirectoryFiles(Path.GetFullPath(directory), Path.GetFullPath(directory), result);
        return result;
    }

    private static void AddFile(string root, string path, Dictionary<string, FileFingerprint> result)
    {
        EnsureNoReparsePoints(path);
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var digest = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!result.TryAdd(relative, new FileFingerprint(stream.Length, digest)))
            throw new IOException("Business fixture contains a case-insensitive path collision.");
    }

    private static void RequireSnapshotsEqual(
        IReadOnlyDictionary<string, FileFingerprint> expected,
        IReadOnlyDictionary<string, FileFingerprint> actual,
        string description)
    {
        if (expected.Count != actual.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw new InvalidDataException("The " + description + " file set or SHA-256 values do not match.");
    }

    private static Dictionary<string, long> ParseCounts(JsonElement element)
    {
        if (element.ValueKind is not JsonValueKind.Object)
            throw new InvalidDataException("The authenticated backup table counts are invalid.");
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is not JsonValueKind.Number || !property.Value.TryGetInt64(out var count) || count < 0 ||
                !result.TryAdd(property.Name, count))
                throw new InvalidDataException("The authenticated backup table count entry is invalid.");
        }
        return result;
    }

    private static void RequireCountsEqual(IReadOnlyDictionary<string, long> expected, IReadOnlyDictionary<string, long> actual)
    {
        if (expected.Count != actual.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var count) || pair.Value != count))
            throw new InvalidDataException("One or more restored PostgreSQL table counts differ from the authenticated backup snapshot.");
    }

    private static void RequireSameBusinessKeys(
        InstanceSecrets expected,
        InstanceSecrets actual,
        string description,
        bool requireFreshDatabaseCredentials = false)
    {
        var expectedMaster = Encoding.UTF8.GetBytes(expected.EncryptionMasterKey);
        var actualMaster = Encoding.UTF8.GetBytes(actual.EncryptionMasterKey);
        var expectedSecret = Encoding.UTF8.GetBytes(expected.SecretKey);
        var actualSecret = Encoding.UTF8.GetBytes(actual.SecretKey);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expectedMaster, actualMaster) ||
                !CryptographicOperations.FixedTimeEquals(expectedSecret, actualSecret))
                throw new InvalidDataException("The " + description + " business-key values do not match.");
            if (requireFreshDatabaseCredentials &&
                (expected.ApplicationDatabasePassword == actual.ApplicationDatabasePassword ||
                expected.ProbeDatabasePassword == actual.ProbeDatabasePassword ||
                expected.PostgresBootstrapAdminPassword == actual.PostgresBootstrapAdminPassword))
                throw new InvalidDataException("The restored target reused a source PostgreSQL login credential.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedMaster);
            CryptographicOperations.ZeroMemory(actualMaster);
            CryptographicOperations.ZeroMemory(expectedSecret);
            CryptographicOperations.ZeroMemory(actualSecret);
        }
    }

    private static string ResolveTargetRoot(string bundleRoot, string relativeRoot)
    {
        var dataContainer = Path.Combine(Path.GetFullPath(bundleRoot), "data");
        var targetRoot = Path.GetFullPath(Path.Combine(dataContainer, relativeRoot));
        var expectedParent = Path.Combine(dataContainer, "instances");
        if (!string.Equals(Path.GetDirectoryName(targetRoot), expectedParent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(targetRoot).StartsWith("restore-", StringComparison.Ordinal))
            throw new IOException("The restore target is outside its unique owned instances root.");
        EnsureNoReparsePoints(targetRoot);
        return targetRoot;
    }

    private static PointerSnapshot ReadPointerSnapshot(string bundleRoot)
    {
        var catalog = new PortableInstanceCatalog(bundleRoot);
        var path = catalog.ActivePointerPath;
        if (!File.Exists(path))
            return new PointerSnapshot(null, null, "absent");
        EnsureNoReparsePoints(path);
        var bytes = File.ReadAllBytes(path);
        return new PointerSnapshot(bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), "sha256=" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static string MakeDsn(string role, string password, int port, string database) =>
        $"postgresql://{role}:{Uri.EscapeDataString(password)}@127.0.0.1:{port}/{database}";

    private static void RequirePointerUnchanged(PointerSnapshot expected, PointerSnapshot actual, string stage)
    {
        if (!string.Equals(expected.Hash, actual.Hash, StringComparison.Ordinal) ||
            expected.Bytes is null != (actual.Bytes is null) ||
            expected.Bytes is not null && actual.Bytes is not null && !expected.Bytes.AsSpan().SequenceEqual(actual.Bytes))
            throw new InvalidDataException("The active pointer changed " + stage + ".");
    }

    private static T GetPrivateField<T>(object target, string name) where T : class
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().Name, name);
        return field.GetValue(target) as T
            ?? throw new InvalidOperationException("A required test-only Host observation was unavailable.");
    }

    private static void AddIdentity(List<OwnedProcessIdentity> identities, OwnedProcessIdentity? identity)
    {
        if (identity is not null && !identities.Any(item => item.ProcessId == identity.ProcessId && item.RunId == identity.RunId))
            identities.Add(identity);
    }

    private static void RequireIdentityStopped(OwnedProcessIdentity? identity)
    {
        if (identity is not null && PythonProcessIdentityProbe.GetRunState(identity) is not PythonIdentityRunState.NotRunning)
            throw new InvalidOperationException("An owned process identity is not confirmed stopped after normal Host shutdown.");
    }

    private static void CheckFreeSpace(string targetPath)
    {
        var diskRoot = Path.GetPathRoot(targetPath) ?? throw new IOException("Cannot resolve the fixture disk root.");
        var drive = new DriveInfo(diskRoot);
        var reserve = Math.Max(10L * 1024 * 1024 * 1024, (long)(drive.TotalSize * 0.05));
        if (drive.AvailableFreeSpace - ExpectedGrowthBytes < reserve)
            throw new IOException("The bounded backup/restore estimate would cross the low-disk safety floor.");
    }

    private static void RequireNoRunningProcesses(
        IReadOnlyDictionary<int, ConfigCasAncestorIdentity> runnerPythonAncestors,
        params string[] names)
    {
        var running = GetProcessesByNames(names)
            .Where(process => !IsVerifiedRunnerPythonAncestor(process, runnerPythonAncestors))
            .ToArray();
        if (running.Length != 0)
            throw new IOException("A PostgreSQL or Python process is already present; the one-shot test will not inspect or stop unknown processes.");
    }

    private static List<ProcessEvidence> GetProcessesByNames(params string[] names)
    {
        var processes = new List<ProcessEvidence>();
        foreach (var name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    var processId = process.Id;
                    var processName = name;
                    long? startedAtUtcTicks = null;
                    try
                    {
                        processName = process.ProcessName;
                        startedAtUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                    }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Missing creation-time evidence prevents the runner-ancestor exemption.
                    }

                    processes.Add(new ProcessEvidence(processId, processName, startedAtUtcTicks));
                }
            }
        }
        return processes;
    }

    private static void RequireLoopbackPortsAvailable(params int[] ports)
    {
        var listeners = new List<TcpListener>();
        try
        {
            foreach (var port in ports)
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listeners.Add(listener);
            }
        }
        catch (SocketException exception)
        {
            throw new IOException("A fixed source-instance loopback port is not available; no listener was terminated.", exception);
        }
        finally
        {
            foreach (var listener in listeners)
                listener.Stop();
        }
    }

    private static string GetFixturePath(string repository, string requestedReleaseId, string? fixtureRunId = null)
    {
        if (!IsAllowedReleaseId(requestedReleaseId))
            throw new IOException("Release id must use the acceptance-frozen-YYYYMMDD-p1-rN format with N >= 3.");
        if (fixtureRunId is not null && (fixtureRunId.Length is < 1 or > 64 ||
            fixtureRunId.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-')))
            throw new IOException("Fixture run id must contain only 1-64 lowercase ASCII letters, digits or hyphens.");

        var expected = Path.GetFullPath(Path.Combine(
            repository,
            FixtureParentRelativePath.Replace('/', Path.DirectorySeparatorChar),
            fixtureRunId is null ? string.Empty : "run-" + fixtureRunId,
            requestedReleaseId));
        return expected;
    }

    private static void RequireExactFixturePath(string repository, string requestedReleaseId, string fixture, string? fixtureRunId = null)
    {
        var expected = GetFixturePath(repository, requestedReleaseId, fixtureRunId);
        if (!string.Equals(expected, Path.GetFullPath(fixture), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The test fixture path is outside the approved release-scoped task directory.");
    }

    private static void RequireFixtureMode(bool fixtureExists, bool resumeRetainedFixture)
    {
        if (fixtureExists != resumeRetainedFixture)
            throw new IOException("fixture existence does not match the explicit one-shot or resume mode; refusing to create or reuse it");
    }

    private static void SeedFreshFixtureWebPortSettings(string bundleRoot, int webPort)
    {
        if (!IsAllowedWebPort(webPort))
            throw new IOException("Cannot seed an invalid or reserved fixture Web port.");

        EnsureNoReparsePoints(bundleRoot);
        Directory.CreateDirectory(bundleRoot);
        EnsureNoReparsePoints(bundleRoot);
        var catalog = new PortableInstanceCatalog(bundleRoot);
        var active = catalog.ResolveActiveInstance();
        if (active.InstanceId is not null || active.WebPort != PortableInstanceCatalog.DefaultWebPort ||
            active.PostgresPort != PortableInstanceCatalog.DefaultPostgresPort)
            throw new IOException("Fresh fixture has an unexpected active instance or default port selection.");

        EnsureNoReparsePoints(active.DataRoot);
        if (Directory.Exists(active.DataRoot) && Directory.EnumerateFileSystemEntries(active.DataRoot).Any())
            throw new IOException("Fresh fixture data root is not empty before Web-port seeding.");

        using var lease = InstanceDataRootLease.Acquire(active.DataRoot);
        var store = new PortableWebPortSettingsStore();
        var current = store.Read(lease, active.WebPort, active.PostgresPort);
        if (current.InstanceId != lease.InstanceId || current.Revision != 0 ||
            current.EffectivePort != PortableInstanceCatalog.DefaultWebPort ||
            current.PendingPort is not null || current.Applying is not null)
            throw new IOException("Fresh fixture Web-port settings are not in the expected empty state.");

        var staged = store.Stage(
            lease, current.Revision, webPort, current.EffectivePort, active.PostgresPort);
        if (staged.PendingPort != webPort || staged.Revision != 1)
            throw new IOException("The explicit fixture Web-port candidate was not staged as expected.");
        var applying = store.BeginApply(
            lease, staged.Revision, staged.EffectivePort, active.PostgresPort);
        var intent = applying.Applying
            ?? throw new IOException("The fixture Web-port seed did not produce an apply intent.");
        if (applying.Revision != 2 || intent.FromPort != PortableInstanceCatalog.DefaultWebPort ||
            intent.CandidatePort != webPort)
            throw new IOException("The fixture Web-port seed intent has an unexpected identity or revision.");

        var committed = store.CommitApply(lease, intent.AttemptId, applying.EffectivePort, active.PostgresPort);
        ValidateRetainedWebPortSettings(committed, lease.InstanceId, webPort, reparsePointDetected: false);
    }

    private static void ValidateRetainedWebPortSettings(
        PortableWebPortSettings settings,
        Guid expectedInstanceId,
        int expectedWebPort,
        bool reparsePointDetected)
    {
        if (reparsePointDetected)
            throw new IOException("The retained Web-port settings path contains a reparse point.");
        if (!IsAllowedWebPort(expectedWebPort) || settings.InstanceId != expectedInstanceId ||
            settings.Revision != 3 || settings.EffectivePort != expectedWebPort ||
            settings.PendingPort is not null || settings.Applying is not null)
            throw new IOException("The retained Web-port settings identity, revision, effective port, or transaction state is invalid.");
    }

    private static void ExpectRetainedWebPortSettingsRejection(
        PortableWebPortSettings settings,
        Guid expectedInstanceId,
        int expectedWebPort,
        bool reparsePointDetected,
        string caseName)
    {
        try
        {
            ValidateRetainedWebPortSettings(settings, expectedInstanceId, expectedWebPort, reparsePointDetected);
        }
        catch (IOException)
        {
            return;
        }

        throw new InvalidOperationException("Retained Web-port validation accepted " + caseName + ".");
    }

    private static FixtureClaim AcquireFixtureClaim(string parent, string releaseId)
    {
        if (!IsAllowedReleaseId(releaseId))
            throw new IOException("Cannot claim a fixture for an invalid release id.");

        EnsureNoReparsePoints(parent);
        return FixtureClaim.Acquire(GetFixtureClaimMutexName(parent, releaseId));
    }

    private static string GetFixtureClaimMutexName(string parent, string releaseId)
    {
        if (!IsAllowedReleaseId(releaseId))
            throw new IOException("Cannot name a fixture claim for an invalid release id.");

        var fixturePath = Path.GetFullPath(Path.Combine(parent, releaseId));
        var normalizedPath = fixturePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
        return OperatingSystem.IsWindows()
            ? "Local\\AIGOOFISH_BACKUP_RESTORE_" + hash
            : "AIGOOFISH_BACKUP_RESTORE_" + hash;
    }

    private static void SimulateAbandonedFixtureClaim(Mutex mutex)
    {
        using var acquired = new ManualResetEventSlim();
        Exception? acquisitionFailure = null;
        var owner = new Thread(() =>
        {
            try
            {
                if (!mutex.WaitOne(0))
                    throw new IOException("The self-test could not acquire the fixture mutex before simulating abandonment.");
            }
            catch (Exception exception)
            {
                acquisitionFailure = exception;
            }
            finally
            {
                acquired.Set();
            }
            // Returning without ReleaseMutex simulates owner-thread termination.
        })
        {
            IsBackground = true,
            Name = "AiGoofish backup fixture abandonment self-test",
        };
        owner.Start();
        if (!acquired.Wait(TimeSpan.FromSeconds(10)) || !owner.Join(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The abandoned fixture claim self-test owner thread did not terminate promptly.");
        if (acquisitionFailure is not null)
            throw new InvalidOperationException("The abandoned fixture claim self-test could not acquire its mutex.", acquisitionFailure);
    }

    private static void ExpectFixtureClaimRejection(string parent, string releaseId)
    {
        try
        {
            using var unexpectedClaim = AcquireFixtureClaim(parent, releaseId);
        }
        catch (IOException)
        {
            return;
        }

        throw new InvalidOperationException("Fixture claim validation accepted a concurrent owner or unrecognized marker.");
    }

    private static void ExpectFixtureModeRejection(bool fixtureExists, bool resumeRetainedFixture, string caseName)
    {
        try
        {
            RequireFixtureMode(fixtureExists, resumeRetainedFixture);
        }
        catch (IOException)
        {
            return;
        }

        throw new InvalidOperationException("Fixture-mode validation accepted " + caseName + ".");
    }

    private static void ExpectInvalidFixturePath(string repository, string releaseId)
    {
        try
        {
            _ = GetFixturePath(repository, releaseId);
        }
        catch (IOException)
        {
            return;
        }

        throw new InvalidOperationException("Fixture-path validation accepted an invalid release id.");
    }

    private sealed class FixtureClaim : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly ManualResetEventSlim _releaseRequested = new();
        private readonly ManualResetEventSlim _acquisitionCompleted = new();
        private readonly Thread _ownerThread;
        private Exception? _acquisitionFailure;
        private Exception? _releaseFailure;
        private int _disposed;
        private int _wasAbandoned;

        private FixtureClaim(string mutexName)
        {
            _mutex = new Mutex(initiallyOwned: false, mutexName);
            _ownerThread = new Thread(OwnMutexUntilReleased)
            {
                IsBackground = true,
                Name = "AiGoofish backup fixture claim owner",
            };
            try
            {
                _ownerThread.Start();
            }
            catch
            {
                _mutex.Dispose();
                _releaseRequested.Dispose();
                _acquisitionCompleted.Dispose();
                throw;
            }

            if (!_acquisitionCompleted.Wait(TimeSpan.FromSeconds(30)))
            {
                _releaseRequested.Set();
                if (!_ownerThread.Join(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The fixture claim owner thread did not finish its bounded acquisition attempt.");
                _releaseRequested.Dispose();
                _acquisitionCompleted.Dispose();
                throw new TimeoutException("The fixture claim owner thread did not report its acquisition result.");
            }

            if (_acquisitionFailure is not null)
            {
                if (!_ownerThread.Join(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The failed fixture claim owner thread did not terminate promptly.", _acquisitionFailure);
                _releaseRequested.Dispose();
                _acquisitionCompleted.Dispose();
                throw new IOException("A backup/restore fixture run already holds the release-scoped claim.", _acquisitionFailure);
            }
        }

        public bool WasAbandoned => Volatile.Read(ref _wasAbandoned) != 0;

        public static FixtureClaim Acquire(string mutexName) => new(mutexName);

        private void OwnMutexUntilReleased()
        {
            var ownsMutex = false;
            try
            {
                try
                {
                    ownsMutex = _mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                    Volatile.Write(ref _wasAbandoned, 1);
                }

                if (!ownsMutex)
                    _acquisitionFailure = new IOException("A backup/restore fixture run already holds the release-scoped claim.");
                _acquisitionCompleted.Set();

                if (ownsMutex)
                    _releaseRequested.Wait();
            }
            catch (Exception exception)
            {
                if (_acquisitionCompleted.IsSet)
                    _releaseFailure = exception;
                else
                    _acquisitionFailure = exception;
                _acquisitionCompleted.Set();
            }
            finally
            {
                if (ownsMutex)
                {
                    try
                    {
                        _mutex.ReleaseMutex();
                    }
                    catch (Exception exception)
                    {
                        _releaseFailure = exception;
                    }
                }

                try
                {
                    _mutex.Dispose();
                }
                catch (Exception exception)
                {
                    _releaseFailure = exception;
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _releaseRequested.Set();
            if (!_ownerThread.Join(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The fixture claim owner thread did not release the mutex promptly.");
            _releaseRequested.Dispose();
            _acquisitionCompleted.Dispose();
            if (_releaseFailure is not null)
                throw new IOException("The release-scoped fixture claim could not be released cleanly.", _releaseFailure);
        }
    }

    private static void RequireReleaseBundleLocation(string repository, string bundleRoot, string requestedReleaseId)
    {
        if (!IsAllowedReleaseId(requestedReleaseId))
            throw new IOException("Release id must use the acceptance-frozen-YYYYMMDD-p1-rN format with N >= 3.");

        var expectedDistributionRoot = Path.GetFullPath(Path.Combine(repository, "launcher", "dist"));
        var observedParent = Path.GetDirectoryName(Path.GetFullPath(bundleRoot));
        var expectedLeaf = "portable-" + requestedReleaseId;
        if (!string.Equals(observedParent, expectedDistributionRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(bundleRoot), expectedLeaf, StringComparison.Ordinal))
        {
            throw new IOException("The candidate bundle must be launcher/dist/portable-{release-id}.");
        }

        if (!Directory.Exists(bundleRoot))
            throw new DirectoryNotFoundException("The requested frozen bundle directory does not exist.");
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Directory.Exists(full) || !File.Exists(full) ? new DirectoryInfo(full) as FileSystemInfo : new FileInfo(full);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The test path contains a reparse point.");
            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
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
        var verified = new DirectoryInfo(path).GetAccessControl();
        if (!verified.AreAccessRulesProtected || verified.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .Any(rule => rule.AccessControlType is AccessControlType.Allow && rule.IdentityReference != sid))
            throw new IOException("The private fixture DACL did not restrict access to the current Windows user.");
    }

    private static RetentionWindow ValidateRetainedFixture(string fixture, string expectedParent, int expectedWebPort)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(fixture)), Path.GetFullPath(expectedParent), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The retained fixture is outside its approved parent.");
        EnsureNoReparsePoints(fixture);
        RequireCurrentUserFixtureAcl(fixture);
        RequireOnlyEntries(fixture,
            ["backup-output", "bundle", "diagnostics", "python-temp"],
            ["retention.json"]);

        var retentionPath = Path.Combine(fixture, "retention.json");
        if (new FileInfo(retentionPath).Length is <= 0 or > 16 * 1024)
            throw new IOException("The retained fixture metadata is missing or oversized.");
        RetentionWindow retentionWindow;
        using (var retention = JsonDocument.Parse(File.ReadAllBytes(retentionPath)))
        {
            retentionWindow = ParseRetentionWindow(retention.RootElement, DateTimeOffset.UtcNow);
            var metadata = retention.RootElement;
            if (metadata.GetProperty("purpose").GetString() != "single real Host-to-PostgreSQL backup and restore acceptance fixture" ||
                !metadata.GetProperty("cleanup_condition").GetString()!.Contains("owned PostgreSQL/Python processes", StringComparison.Ordinal))
                throw new IOException("The retained fixture purpose or cleanup metadata is invalid.");
        }

        var bundleRoot = Path.Combine(fixture, "bundle");
        RequireOnlyEntries(bundleRoot, ["data"], []);
        var dataRoot = Path.Combine(bundleRoot, "data");
        EnsureNoReparsePoints(dataRoot);
        using var lease = InstanceDataRootLease.Acquire(dataRoot);
        RequireOnlyEntries(dataRoot,
            ["assets", "cache", "config", "launcher", "logs", "postgres", "results", "state"],
            [InstanceDataRootLease.LockFileName]);
        var launcherRoot = Path.Combine(dataRoot, "launcher");
        RequireOnlyEntries(launcherRoot, [],
            [Path.GetFileName(PortableWebPortSettingsStore.RelativePath),
                Path.GetFileName(PortableWebPortSettingsStore.LockRelativePath)]);
        RequireNoReparseEvidence(reparsePointDetected: false);
        var webPortSettings = new PortableWebPortSettingsStore().Read(
            lease, PortableInstanceCatalog.DefaultWebPort, PortableInstanceCatalog.DefaultPostgresPort);
        ValidateRetainedWebPortSettings(webPortSettings, lease.InstanceId, expectedWebPort, reparsePointDetected: false);
        if (new FileInfo(Path.Combine(dataRoot, PortableWebPortSettingsStore.LockRelativePath.Replace('/', Path.DirectorySeparatorChar))).Length != 0)
            throw new IOException("The retained Web-port lock file is not the expected empty lock identity file.");
        RequireOnlyEntries(Path.Combine(dataRoot, "config"), [],
            ["app.env", "instance-secrets.dpapi", "postgres-provision.json", "postgres-runtime.json", "python-runtime.json"]);
        RequireOnlyEntries(Path.Combine(dataRoot, "cache"), ["temp"], []);
        RequireOnlyEntries(Path.Combine(dataRoot, "assets"), ["avatars"],
            ["中文恢复样本.txt", Path.GetFileName(KeyCheckRelativePath)]);
        RequireOnlyEntries(Path.Combine(dataRoot, "state"), ["task_stats"], ["e2e-fixture.json"]);
        RequireOnlyEntries(Path.Combine(dataRoot, "results"), ["images"], ["e2e-fixture.txt"]);
        RequireOnlyEntries(Path.Combine(dataRoot, "logs"), ["exports", "tasks"], ["error.log", "fetcher.log", "system.log"]);
        RequireOnlyEntries(Path.Combine(dataRoot, "postgres"), ["cluster"], []);

        foreach (var directory in new[]
        {
            Path.Combine(fixture, "backup-output"),
            Path.Combine(fixture, "python-temp"),
        })
        {
            RequireOnlyEntries(directory, [], []);
        }
        RequireOnlyEntries(Path.Combine(fixture, "diagnostics"), [], ["acceptance.log"]);

        var pointerPath = new PortableInstanceCatalog(bundleRoot).ActivePointerPath;
        if (File.Exists(pointerPath) || Directory.Exists(Path.Combine(dataRoot, "instances")) ||
            File.Exists(Path.Combine(fixture, "backup-output", "source-fixture.gfbk")))
            throw new IOException("The retained source already contains an activation pointer, restore target, or backup; refusing to reuse it.");

        var pgData = Path.Combine(dataRoot, "postgres", "cluster");
        var markerPath = Path.Combine(pgData, ".aigoofish-cluster.json");
        using var marker = JsonDocument.Parse(File.ReadAllBytes(markerPath));
        if (File.ReadAllText(Path.Combine(pgData, "PG_VERSION")).Trim() != "17" ||
            marker.RootElement.GetProperty("engine_version").GetString() != "17.11")
            throw new IOException("The retained source cluster is not the owned PG17 fixture.");

        var instanceId = lease.InstanceId;
        if (marker.RootElement.GetProperty("instance_id").GetGuid() != instanceId)
            throw new IOException("The retained PostgreSQL marker does not match its instance lease.");
        RequireStoppedRuntime(Path.Combine(dataRoot, "config", "postgres-runtime.json"), instanceId);
        RequireStoppedRuntime(Path.Combine(dataRoot, "config", "python-runtime.json"), instanceId);
        _ = new WindowsInstanceSecretsStore().Load(lease);

        var files = SnapshotBusinessFiles(dataRoot);
        var expectedBusinessFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "assets/中文恢复样本.txt",
            KeyCheckRelativePath,
            "state/e2e-fixture.json",
            "results/e2e-fixture.txt",
            "config/app.env",
        };
        if (files.Count != expectedBusinessFiles.Count || files.Keys.Any(path => !expectedBusinessFiles.Contains(path)))
            throw new IOException("The retained instance contains missing or unexpected business fixture files.");

        return retentionWindow;
    }

    private static void RequireNoReparseEvidence(bool reparsePointDetected)
    {
        if (reparsePointDetected)
            throw new IOException("A retained fixture entry is a reparse point.");
    }

    private static void RequireCurrentUserFixtureAcl(string path)
    {
        var currentSid = WindowsIdentity.GetCurrent().User
            ?? throw new IOException("The current Windows SID could not be resolved for fixture ACL validation.");
        var security = new DirectoryInfo(path).GetAccessControl();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().ToArray();
        if (!security.AreAccessRulesProtected || rules.Length == 0 ||
            rules.Any(rule => rule.AccessControlType is AccessControlType.Allow && rule.IdentityReference != currentSid))
            throw new IOException("The retained fixture DACL is not restricted to the current Windows user.");
    }

    private static void RequireOnlyEntries(string directory, IReadOnlyCollection<string> expectedDirectories, IReadOnlyCollection<string> expectedFiles)
    {
        EnsureNoReparsePoints(directory);
        var actualDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actualFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The retained fixture contains a reparse point.");
            var name = Path.GetFileName(entry);
            if ((attributes & FileAttributes.Directory) != 0)
                actualDirectories.Add(name);
            else
                actualFiles.Add(name);
        }
        if (!actualDirectories.SetEquals(expectedDirectories) || !actualFiles.SetEquals(expectedFiles))
            throw new IOException("The retained fixture has an unexpected or missing directory entry: " + Path.GetFileName(directory));
    }

    private static void RequireStoppedRuntime(string path, Guid expectedInstanceId)
    {
        EnsureNoReparsePoints(path);
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        if (root.GetProperty("state").GetString() != "Stopped" ||
            root.GetProperty("instance_id").GetGuid() != expectedInstanceId ||
            root.GetProperty("identity").GetProperty("instance_id").GetGuid() != expectedInstanceId ||
            root.GetProperty("identity").GetProperty("process_id").GetInt32() <= 0)
            throw new IOException("The retained source runtime identity is not a stopped process for the owned instance.");
    }

    private static string CreateSyntheticPassword()
    {
        var random = Convert.ToBase64String(RandomNumberGenerator.GetBytes(30)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return "Fixture-9!" + random;
    }

    private static bool ContainsBytes(string path, byte[] needle)
    {
        if (needle.Length == 0)
            return false;
        EnsureNoReparsePoints(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[1024 * 1024 + needle.Length - 1];
        var carry = 0;
        try
        {
            while (true)
            {
                var read = input.Read(buffer, carry, 1024 * 1024);
                var available = carry + read;
                for (var start = 0; start <= available - needle.Length; start++)
                    if (buffer.AsSpan(start, needle.Length).SequenceEqual(needle))
                        return true;
                if (read == 0)
                    return false;
                carry = Math.Min(needle.Length - 1, available);
                if (carry > 0)
                    Buffer.BlockCopy(buffer, available - carry, buffer, 0, carry);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static string HashFile(string path)
    {
        EnsureNoReparsePoints(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    private static void WriteRetentionMetadata(
        string fixture,
        RunState state,
        string outcome,
        RetentionWindow? retainedWindow)
    {
        EnsureNoReparsePoints(fixture);
        long bytes = 0;
        foreach (var file in EnumeratePlainFiles(fixture))
            bytes = checked(bytes + new FileInfo(file).Length);
        var metadataPath = Path.Combine(fixture, "retention.json");
        var window = retainedWindow ?? ReadExistingRetentionWindow(metadataPath) ?? CreateRetentionWindow(DateTimeOffset.UtcNow);
        var metadata = CreateRetentionMetadataJson(outcome, bytes, window);
        var temporaryPath = Path.Combine(fixture, ".retention-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(output, new UTF8Encoding(false)))
            {
                writer.Write(metadata);
                writer.Write('\n');
                writer.Flush();
                output.Flush(flushToDisk: true);
            }

            EnsureNoReparsePoints(temporaryPath);
            File.Move(temporaryPath, metadataPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
        state.Log("RETENTION bytes_before_metadata=" + bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " review_after_utc=" + window.ExpiresUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static RetentionWindow? ReadExistingRetentionWindow(string metadataPath)
    {
        if (!File.Exists(metadataPath))
            return null;

        EnsureNoReparsePoints(metadataPath);
        using var document = JsonDocument.Parse(File.ReadAllBytes(metadataPath));
        return ParseRetentionWindow(document.RootElement, DateTimeOffset.MaxValue, requireNotExpired: false);
    }

    private static RetentionWindow ParseRetentionWindow(
        JsonElement metadata,
        DateTimeOffset now,
        bool requireNotExpired = true)
    {
        if (!metadata.TryGetProperty("created_utc", out var createdElement) ||
            !metadata.TryGetProperty("review_or_cleanup_after_utc", out var expiryElement) ||
            !DateTimeOffset.TryParse(createdElement.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var created) ||
            !DateTimeOffset.TryParse(expiryElement.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var expires))
            throw new IOException("The retained fixture creation or expiry timestamp is invalid.");

        created = created.ToUniversalTime();
        expires = expires.ToUniversalTime();
        DateTimeOffset maximumExpiry;
        try
        {
            maximumExpiry = created.AddDays(7);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new IOException("The retained fixture creation timestamp is outside the supported range.", exception);
        }

        if (created > now || expires <= created || expires > maximumExpiry || (requireNotExpired && expires <= now))
            throw new IOException("The retained fixture expiry must be valid, unexpired, and no more than seven days after creation.");

        return new RetentionWindow(created, expires);
    }

    private static RetentionWindow CreateRetentionWindow(DateTimeOffset createdUtc)
    {
        var created = createdUtc.ToUniversalTime();
        return new RetentionWindow(created, created.AddDays(7));
    }

    private static string CreateRetentionMetadataJson(string outcome, long bytes, RetentionWindow window) =>
        JsonSerializer.Serialize(new
        {
            purpose = "single real Host-to-PostgreSQL backup and restore acceptance fixture",
            outcome,
            bytes_before_this_metadata = bytes,
            created_utc = window.CreatedUtc,
            review_or_cleanup_after_utc = window.ExpiresUtc,
            cleanup_condition = "only after all owned PostgreSQL/Python processes are proven stopped and the retained failure diagnosis is no longer needed",
        }, new JsonSerializerOptions { WriteIndented = true });

    private static void RunRetentionMetadataSelfTests(string repository)
    {
        var created = new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
        var now = created.AddDays(1);
        using var valid = JsonDocument.Parse(CreateRetentionTestJson(created, created.AddDays(7)));
        var parsed = ParseRetentionWindow(valid.RootElement, now);
        if (parsed.CreatedUtc != created || parsed.ExpiresUtc != created.AddDays(7))
            throw new InvalidOperationException("Retention metadata self-test failed to preserve its original timestamps.");
        using var rewritten = JsonDocument.Parse(CreateRetentionMetadataJson("PASS", 0, parsed));
        var rewrittenWindow = ParseRetentionWindow(rewritten.RootElement, now);
        if (rewrittenWindow != parsed)
            throw new InvalidOperationException("Retention metadata rewrite self-test changed the original creation or expiry timestamp.");

        using var extended = JsonDocument.Parse(CreateRetentionTestJson(created, created.AddDays(7).AddTicks(1)));
        ExpectRetentionRejection(extended.RootElement, now, "extension beyond seven days");
        using var expired = JsonDocument.Parse(CreateRetentionTestJson(created, created.AddHours(12)));
        ExpectRetentionRejection(expired.RootElement, now, "expired fixture");
        using var futureCreated = JsonDocument.Parse(CreateRetentionTestJson(now.AddMinutes(1), now.AddDays(1)));
        ExpectRetentionRejection(futureCreated.RootElement, now, "future creation time");
        TestRunStatePersistenceGate(repository);
        Console.WriteLine("RETENTION_METADATA_SELFTEST=PASS");
    }

    private static void TestRunStatePersistenceGate(string repository)
    {
        var parent = Path.GetFullPath(Path.Combine(repository, ".tmp", "tests", "portable-backup-retention-selftest"));
        var testRoot = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        EnsureNoReparsePoints(parent);
        Directory.CreateDirectory(Path.Combine(testRoot, "diagnostics"));
        try
        {
            var state = new RunState(testRoot);
            state.Log("PREFLIGHT_FAILURE_MUST_NOT_PERSIST");
            var logPath = Path.Combine(testRoot, "diagnostics", "acceptance.log");
            if (File.Exists(logPath))
                throw new InvalidOperationException("RunState persisted a preflight failure before fixture ownership was established.");

            state.EnablePersistence();
            state.Log("OWNED_FIXTURE_LOG_MUST_PERSIST");
            var persisted = File.ReadAllText(logPath, Encoding.UTF8);
            if (persisted.Contains("PREFLIGHT_FAILURE_MUST_NOT_PERSIST", StringComparison.Ordinal) ||
                !persisted.Contains("OWNED_FIXTURE_LOG_MUST_PERSIST", StringComparison.Ordinal))
                throw new InvalidOperationException("RunState persistence gate did not preserve only owned fixture diagnostics.");
        }
        finally
        {
            EnsureNoReparsePoints(testRoot);
            Directory.Delete(testRoot, recursive: true);
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
    }

    private static string CreateRetentionTestJson(DateTimeOffset created, DateTimeOffset expires) =>
        JsonSerializer.Serialize(new { created_utc = created, review_or_cleanup_after_utc = expires });

    private static void ExpectRetentionRejection(JsonElement metadata, DateTimeOffset now, string caseName)
    {
        try
        {
            _ = ParseRetentionWindow(metadata, now);
        }
        catch (IOException)
        {
            return;
        }

        throw new InvalidOperationException("Retention metadata self-test accepted " + caseName + ".");
    }

    private readonly record struct RetentionWindow(DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc);

    private static IEnumerable<string> EnumeratePlainFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            EnsureNoReparsePoints(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Retained fixture contains a reparse point.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
                else
                    yield return entry;
            }
        }
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The real Host-to-PostgreSQL restore E2E is Windows-only.");
    }

    private sealed record FileFingerprint(long Size, string Sha256);
    private sealed record ProcessEvidence(int ProcessId, string Name, long? StartedAtUtcTicks);
    private sealed record PointerSnapshot(byte[]? Bytes, string? Hash, string Description);

    private sealed class RunState(string fixture)
    {
        private readonly string _path = Path.Combine(fixture, "diagnostics", "acceptance.log");
        private readonly HashSet<int> _activeProbeIds = new();
        private bool _persistenceEnabled;

        public void EnablePersistence() => _persistenceEnabled = true;

        public void ProbeStarted(int processId)
        {
            lock (_activeProbeIds)
                _activeProbeIds.Add(processId);
            Log("PROBE_STARTED pid=" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public void ProbeExited(int processId)
        {
            lock (_activeProbeIds)
                _activeProbeIds.Remove(processId);
            Log("PROBE_EXITED pid=" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public void LogActiveProbeIds()
        {
            string active;
            lock (_activeProbeIds)
                active = string.Join(",", _activeProbeIds.Order());
            if (active.Length > 0)
                Log("ACTIVE_PROBE_IDS=" + active);
        }

        public void Log(string value)
        {
            Console.WriteLine(value);
            if (_persistenceEnabled && Directory.Exists(Path.GetDirectoryName(_path)))
                File.AppendAllText(_path, value + "\n", new UTF8Encoding(false));
        }
    }
}
