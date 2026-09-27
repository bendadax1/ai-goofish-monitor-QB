using System.Text;
using System.Text.Json;
using AiGoofish.Launcher.Platform.Windows;

internal static class InitializationStateCases
{
    public static Task RunAsync(ProcessTestWorkspace workspace)
    {
        var root = workspace.CreateCaseRoot("allocated-state");
        Guid instance;
        using (var lease = InstanceDataRootLease.Acquire(root))
        {
            instance = lease.InstanceId;
            var state = new PortableInitializationState(lease);
            Require(!state.IsAllocated, "Missing metadata must not authorize initialization");
            state.RecordAllocation();
            Require(state.IsAllocated, "Allocated state not recognized");
            Reject(state.RecordAllocation);
        }
        using (var lease = InstanceDataRootLease.Acquire(root))
        {
            Require(lease.InstanceId == instance, "Reopen must preserve identity");
            var state = new PortableInitializationState(lease);
            Require(state.IsAllocated, "Allocation must survive reopen");
            state.BeginInitialization();
            Require(!state.IsAllocated, "Started initialization cannot retain permission");
            Reject(state.BeginInitialization);
        }
        using (var lease = InstanceDataRootLease.Acquire(root))
            Require(!new PortableInitializationState(lease).IsAllocated, "Interrupted initialization cannot regrant permission");

        foreach (var evidence in new[]
        {
            "config/postgres-initialization.json", "config/postgres-provision-progress.json",
            "config/postgres-runtime.json", "config/python-runtime.json", "state/business.json",
            "postgres/cluster/PG_VERSION", "postgres/cluster/postmaster.pid", "cache/temp/unknown.tmp",
        })
        {
            using var lease = InstanceDataRootLease.Acquire(workspace.CreateCaseRoot("evidence-" + Guid.NewGuid().ToString("N")));
            var state = new PortableInitializationState(lease);
            state.RecordAllocation();
            var path = Path.Combine(lease.InstanceRoot, evidence);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "synthetic evidence", new UTF8Encoding(false));
            var before = File.ReadAllBytes(Path.Combine(lease.InstanceRoot, PortableInitializationState.RelativePath));
            Reject(() => _ = state.IsAllocated);
            Reject(state.BeginInitialization);
            Require(File.ReadAllText(path) == "synthetic evidence", "Evidence must remain untouched");
            Require(File.ReadAllBytes(Path.Combine(lease.InstanceRoot, PortableInitializationState.RelativePath)).SequenceEqual(before),
                "Refusal must not change initialization metadata");
        }
        using (var lease = InstanceDataRootLease.Acquire(workspace.CreateCaseRoot("empty-pgdata")))
        {
            var state = new PortableInitializationState(lease);
            state.RecordAllocation();
            Directory.CreateDirectory(Path.Combine(lease.InstanceRoot, "postgres", "cluster"));
            Reject(() => _ = state.IsAllocated);
        }
        foreach (var kind in new[] { "identity", "phase", "version", "malformed", "oversized", "directory" })
        {
            using var lease = InstanceDataRootLease.Acquire(workspace.CreateCaseRoot("invalid-" + kind));
            var state = new PortableInitializationState(lease);
            var path = Path.Combine(lease.InstanceRoot, PortableInitializationState.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (kind == "directory") Directory.CreateDirectory(path);
            else File.WriteAllText(path, kind switch
            {
                "malformed" => "{",
                "oversized" => new string('x', 4097),
                _ => JsonSerializer.Serialize(new
                {
                    format_version = kind == "version" ? 2 : 1,
                    instance_id = kind == "identity" ? Guid.NewGuid() : lease.InstanceId,
                    phase = kind == "phase" ? "Completed" : "Allocated",
                }),
            }, new UTF8Encoding(false));
            Reject(() => _ = state.IsAllocated);
        }
        Console.WriteLine("INITIALIZATION_STATE_PASS");
        return Task.CompletedTask;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (PythonLifecycleException) { return; }
        throw new InvalidOperationException("Unsafe initialization was not refused");
    }
}
