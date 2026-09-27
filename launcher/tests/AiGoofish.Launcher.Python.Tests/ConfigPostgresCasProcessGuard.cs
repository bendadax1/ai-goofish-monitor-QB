using System.Diagnostics;
using System.Runtime.InteropServices;

internal sealed record ConfigCasProcessEvidence(
    int ProcessId,
    int ParentProcessId,
    long? StartedAtUtcTicks,
    string Name,
    string? ImagePath);

internal sealed record ConfigCasAncestorIdentity(int ProcessId, long StartedAtUtcTicks);

internal static class ConfigPostgresCasProcessGuard
{
    private static readonly HashSet<string> RelevantNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "postgres", "pg_ctl", "python", "pythonw",
    };

    public static void RequireNoOwnedProcesses(
        string repositoryRoot,
        string pythonExecutable,
        string postgresRoot,
        string fixtureRoot)
    {
        var repository = Path.GetFullPath(repositoryRoot);
        var python = Path.GetFullPath(pythonExecutable);
        var postgres = Path.GetFullPath(postgresRoot);
        var fixture = Path.GetFullPath(fixtureRoot);
        var processes = ReadProcessEvidence();
        var ancestors = ReadCurrentProcessAncestors();
        var conflicts = FindConflicts(processes, ancestors, repository, python, postgres, fixture);
        if (conflicts.Count != 0)
            throw new IOException("A repository-owned or target-runtime process is visible before the isolated acceptance: " + string.Join(",", conflicts));
    }

    internal static IReadOnlyList<string> FindConflicts(
        IReadOnlyList<ConfigCasProcessEvidence> processes,
        IReadOnlyDictionary<int, ConfigCasAncestorIdentity> runnerAncestors,
        string repositoryRoot,
        string pythonExecutable,
        string postgresExecutable,
        string fixtureRoot)
    {
        var repository = Normalize(repositoryRoot);
        var python = Normalize(pythonExecutable);
        var postgres = Normalize(postgresExecutable);
        var fixture = Normalize(fixtureRoot);
        var conflicts = new List<string>();
        foreach (var process in processes)
        {
            if (!RelevantNames.Contains(process.Name))
                continue;
            if (process.StartedAtUtcTicks is { } startedAt &&
                runnerAncestors.TryGetValue(process.ProcessId, out var ancestor) &&
                ancestor.StartedAtUtcTicks == startedAt)
                continue;
            if (string.IsNullOrWhiteSpace(process.ImagePath))
            {
                if (IsPostgresName(process.Name))
                    throw new IOException("Unable to establish the image identity of a PostgreSQL process.");
                continue; // Port ownership and the target PGDATA marker are checked independently.
            }

            var image = Normalize(process.ImagePath);
            var inRepository = IsWithin(image, repository);
            var inFixture = IsWithin(image, fixture);
            if (inRepository || inFixture || string.Equals(image, python, StringComparison.OrdinalIgnoreCase) || IsWithin(image, postgres))
                conflicts.Add(process.Name + ":" + process.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return conflicts;
    }

    private static IReadOnlyList<ConfigCasProcessEvidence> ReadProcessEvidence()
    {
        var parentIds = ReadParentProcessIds();
        var evidence = new List<ConfigCasProcessEvidence>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try { name = process.ProcessName; }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
                if (!RelevantNames.Contains(name)) continue;
                string? image;
                image = TryGetImagePath(process.Id);
                if (!parentIds.TryGetValue(process.Id, out var parentId))
                {
                    if (IsPostgresName(name))
                        throw new IOException("Unable to establish the parent identity of a PostgreSQL process.");
                    continue;
                }
                evidence.Add(new ConfigCasProcessEvidence(process.Id, parentId, TryGetStartTimeUtcTicks(process), name, image));
            }
        }
        return evidence;
    }

    internal static IReadOnlyDictionary<int, ConfigCasAncestorIdentity> ReadCurrentProcessAncestors()
    {
        var parents = ReadParentProcessIds();
        var ancestors = new Dictionary<int, ConfigCasAncestorIdentity>();
        var seen = new HashSet<int>();
        var current = Environment.ProcessId;
        var depth = 0;
        for (; depth < 64 && parents.TryGetValue(current, out var parent) && parent > 0; depth++)
        {
            if (!seen.Add(parent)) throw new IOException("A cycle was found in the process parent chain.");
            var startTime = TryGetStartTimeUtcTicks(parent);
            if (startTime is { } startedAt)
                ancestors.Add(parent, new ConfigCasAncestorIdentity(parent, startedAt));
            current = parent;
        }
        if (depth == 64 && parents.TryGetValue(current, out var unresolvedParent) && unresolvedParent > 0)
            throw new IOException("The process parent chain exceeded the verification limit.");
        return ancestors;
    }

    private static long? TryGetStartTimeUtcTicks(Process process)
    {
        try { return process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            return null;
        }
    }

    private static long? TryGetStartTimeUtcTicks(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return TryGetStartTimeUtcTicks(process);
        }
        catch (ArgumentException) { return null; }
    }

    private static Dictionary<int, int> ReadParentProcessIds()
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new IOException("Unable to snapshot process parent identities.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            var parents = new Dictionary<int, int>();
            if (!Process32First(snapshot, ref entry)) throw new IOException("Unable to read process parent identities.");
            while (true)
            {
                parents[checked((int)entry.ProcessId)] = checked((int)entry.ParentProcessId);
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
                if (Process32Next(snapshot, ref entry)) continue;
                if (Marshal.GetLastWin32Error() != 18) throw new IOException("Unable to complete process parent identity enumeration.");
                break;
            }
            return parents;
        }
        finally { CloseHandle(snapshot); }
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsWithin(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsPostgresName(string name) =>
        string.Equals(name, "postgres", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "pg_ctl", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetImagePath(int processId)
    {
        var processHandle = OpenProcess(0x1000, false, (uint)processId);
        if (processHandle == IntPtr.Zero || processHandle == new IntPtr(-1)) return null;
        try
        {
            var buffer = new System.Text.StringBuilder(32768);
            var length = (uint)buffer.Capacity;
            return QueryFullProcessImageName(processHandle, 0, buffer, ref length) && length > 0
                ? buffer.ToString(0, checked((int)length))
                : null;
        }
        finally { CloseHandle(processHandle); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder imageName, ref uint size);
}

internal static class ConfigPostgresCasResourceGuard
{
    internal static void RequireAvailable(
        string postgresDataRoot,
        int postgresPort,
        int webPort,
        Action<string> requireDataPathUnclaimed,
        Action<int> requirePortAvailable)
    {
        requireDataPathUnclaimed(postgresDataRoot);
        requirePortAvailable(postgresPort);
        requirePortAvailable(webPort);
    }
}
