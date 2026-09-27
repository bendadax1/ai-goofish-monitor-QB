using AiGoofish.Launcher.Core;

namespace AiGoofish.Launcher.Platform.Windows;

/// <summary>
/// A durable permission to perform first initialization, not a substitute for
/// the PostgreSQL/provision completion records. Missing legacy records never
/// grant permission. Consumed before any database initialization can run.
/// </summary>
internal sealed class PortableInitializationState(InstanceDataRootLease lease)
{
    internal const string RelativePath = "launcher/initialization.json";
    private const string Allocated = "Allocated";
    private const string Started = "InitializationStarted";

    private string StatePath => Path.Combine(lease.InstanceRoot, RelativePath);

    public bool IsAllocated
    {
        get
        {
            var record = Read();
            if (record?.Phase != Allocated) return false;
            ValidateUninitializedLayout();
            return true;
        }
    }

    public void RecordAllocation()
    {
        EnsureCanAllocate();
        PythonPathGuard.EnsureSafeInstancePath(lease, StatePath, allowMissingLeaf: true);
        PythonStateFile.WriteNew(StatePath, new InitializationRecord(1, lease.InstanceId, Allocated));
    }

    public void EnsureCanAllocate()
    {
        lease.EnsureHeld();
        if (Read() is not null) throw Refused();
        ValidateUninitializedLayout();
    }

    public void BeginInitialization()
    {
        // Call only from the coordinator's non-cancellable database step, after
        // its all-stopped preflight. Never restore this permission on failure.
        if (!IsAllocated) throw Refused();
        PythonStateFile.WriteReplace(StatePath, new InitializationRecord(1, lease.InstanceId, Started));
    }

    private InitializationRecord? Read()
    {
        lease.EnsureHeld();
        PythonPathGuard.EnsureSafeInstancePath(lease, StatePath, allowMissingLeaf: true);
        try
        {
            var attributes = File.GetAttributes(StatePath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw Refused();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PythonLifecycleException("无法核验实例初始化阶段；保留数据并拒绝自动初始化。", exception);
        }

        var record = PythonStateFile.Read<InitializationRecord>(StatePath, 4096);
        if (record.FormatVersion != 1 || record.InstanceId != lease.InstanceId ||
            record.Phase is not (Allocated or Started)) throw Refused();
        return record;
    }

    private void ValidateUninitializedLayout()
    {
        lease.EnsureHeld();
        // A positive allowlist: even an empty PGDATA, helper intent, runtime,
        // business file, unknown entry or reparse point invalidates allocation.
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "config", "launcher", "cache", "cache/temp", "logs" };
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            InstanceDataRootLease.LockFileName, WindowsInstanceSecretsStore.RelativeSecretsPath,
            RelativePath, PortableWebPortSettingsStore.RelativePath, PortableWebPortSettingsStore.LockRelativePath,
            "launcher/preferences.json",
        };
        var pending = new Stack<string>();
        pending.Push(lease.InstanceRoot);
        try
        {
            while (pending.TryPop(out var directory))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var relative = Path.GetRelativePath(lease.InstanceRoot, entry).Replace('\\', '/');
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw Refused();
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        // Inactive restore candidates are not this legacy instance.
                        // Catalog validation remains mandatory before activation.
                        if (relative.Equals("instances", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!directories.Contains(relative)) throw Refused();
                        pending.Push(entry);
                    }
                    else if (!files.Contains(relative)) throw Refused();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PythonLifecycleException("无法核验未初始化实例布局；保留数据并拒绝自动初始化。", exception);
        }
    }

    private static PythonLifecycleException Refused() => new(
        "实例初始化阶段与目录证据不一致；不会自动重试、补造标记或重建数据库。",
        LauncherDiagnosticCode.PostgresDataUnverified);

    private sealed record InitializationRecord(int FormatVersion, Guid InstanceId, string Phase);
}
