using System.Security.Cryptography;
using AiGoofish.Launcher.Core;
using AiGoofish.Launcher.Platform.Windows;

internal static class SchemaUpgradeAcceptance
{
    public static async Task<int> RunAsync(string v1FixturePath, string newBundlePath)
    {
        var repository = Path.GetFullPath(Environment.GetEnvironmentVariable("AIGOOFISH_REPOSITORY_ROOT")
            ?? throw new InvalidOperationException("Explicit repository root is required."));
        var parent = Path.Combine(repository, ".tmp", "tests", "b2-schema-upgrade");
        var disk = new DriveInfo(Path.GetPathRoot(parent)!);
        if (disk.AvailableFreeSpace < 10L * 1024 * 1024 * 1024 ||
            disk.AvailableFreeSpace < disk.TotalSize * .05)
            throw new IOException("Low disk floor blocks new upgrade fixture.");
        Directory.CreateDirectory(parent);
        var fixture = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        RealPortableStackHost? target = null;
        PortableRestoreSession? restore = null;
        var success = false;
        var stage = "preflight";
        try
        {
            var newBundle = await PortableBundleDescriptor.LoadAndVerifyAsync(Path.GetFullPath(newBundlePath));
            if (newBundle.SchemaMaximum != 2)
                throw new InvalidOperationException("Fixture requires a v2 target bundle.");
            var targetBundleRoot = Path.Combine(fixture, "target-bundle");
            Directory.CreateDirectory(targetBundleRoot);
            var targetBundle = newBundle with { BundleRoot = targetBundleRoot };
            var fixtureRoot = Path.GetFullPath(v1FixturePath);
            if (Path.GetDirectoryName(fixtureRoot) != parent ||
                !Directory.Exists(fixtureRoot))
                throw new InvalidOperationException("v1 archive fixture is outside the approved test parent.");
            var oldArchive = Path.Combine(fixtureRoot, "v1.gfbk");
            var passphraseFile = Path.Combine(fixtureRoot, "passphrase.txt");
            if (!File.Exists(oldArchive) || !File.Exists(passphraseFile))
                throw new InvalidOperationException("v1 archive fixture is incomplete.");
            var passphrase = File.ReadAllText(passphraseFile);

            stage = "isolated-v1-restore";
            restore = RealPortableStackHost.CreateBusinessRestoreSession(targetBundle);
            var preview = await restore.RestoreAndPreviewAsync(oldArchive, passphrase,
                PortableRestoreSession.RequiredBackupTrustText);
            if (preview.State is not PortableRestoreState.AwaitingUserConfirmation ||
                preview.Preview is null)
                throw new InvalidOperationException("New bundle did not validate the old v1 archive.");
            var activated = await restore.ConfirmActivationAsync(PortableRestoreSession.RequiredConfirmationText);
            if (activated.State is not PortableRestoreState.Activated)
                throw new InvalidOperationException("Isolated v1 restore was not explicitly activated.");
            await restore.DisposeAsync();
            restore = null;

            stage = "v1-target-start";
            target = RealPortableStackHost.Create(targetBundle);
            var started = await target.StartAsync();
            if (!started.Succeeded || target.CurrentSchemaVersion != 1)
                throw new InvalidOperationException("Restored v1 target did not become Ready.");
            stage = "host-upgrade";
            var upgradeArchive = Path.Combine(fixture, "v1-upgrade-safety.gfbk");
            var result = await target.UpgradeSchemaWithBackupAndStopAsync(upgradeArchive, passphrase);
            if (result.SchemaVersion != 2 || result.Status != "applied" ||
                result.Backup.Sha256 != Hash(upgradeArchive) ||
                target.Coordinator.Snapshot.State is not LauncherState.Stopped)
                throw new InvalidOperationException("Launcher did not complete backup, migration and stop.");
            stage = "v2-restart";
            started = await target.StartAsync();
            if (!started.Succeeded || target.CurrentSchemaVersion != 2)
                throw new InvalidOperationException("Migrated v2 target did not become Ready.");
            await target.Coordinator.StopAsync(CancellationToken.None);
            await target.DisposeAsync();
            target = null;
            success = true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"LAUNCHER_SCHEMA_UPGRADE_E2E=FAILED stage={stage} category={exception.GetType().Name}");
        }
        finally
        {
            if (restore is not null)
                try { await restore.DisposeAsync(); } catch { success = false; }
            foreach (var host in new[] { target })
            {
                if (host is null) continue;
                try
                {
                    await host.Coordinator.StopAsync(CancellationToken.None);
                    await host.DisposeAsync();
                }
                catch
                {
                    success = false;
                    Console.Error.WriteLine("LAUNCHER_SCHEMA_UPGRADE_CLEANUP_UNCONFIRMED");
                }
            }
            if (success && Path.GetDirectoryName(Path.GetFullPath(fixture)) == parent)
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(fixture, "*", SearchOption.AllDirectories)
                        .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                        throw new IOException("Synthetic fixture contains a reparse point.");
                    Directory.Delete(fixture, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    success = false;
                    Console.Error.WriteLine("LAUNCHER_SCHEMA_UPGRADE_FIXTURE_CLEANUP_FAILED");
                    Console.Error.WriteLine("RETAINED_SYNTHETIC_FIXTURE=" + fixture);
                }
            }
            else Console.Error.WriteLine("RETAINED_SYNTHETIC_FIXTURE=" + fixture);
        }
        Console.WriteLine("LAUNCHER_SCHEMA_UPGRADE_E2E=" + (success ? "PASS" : "FAILED"));
        return success ? 0 : 1;
    }

    private static string Hash(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
