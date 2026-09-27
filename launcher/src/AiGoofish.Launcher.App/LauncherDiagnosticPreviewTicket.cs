using System.Text;
using AiGoofish.Launcher.Core;

namespace AiGoofish.Launcher.App;

/// <summary>
/// Holds the exact safe bytes shown in the diagnostic preview and the coordinator
/// generation that produced them. The ticket contains no logs or free-form messages.
/// </summary>
internal sealed class LauncherDiagnosticPreviewTicket : IDisposable
{
    private byte[]? _contents;

    public LauncherDiagnosticPreviewTicket(
        byte[] contents,
        LauncherCoordinator? coordinator,
        object hostIdentity,
        long coordinatorGeneration)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(hostIdentity);
        if (contents.Length is <= 0 or > LauncherDiagnosticExporter.MaximumBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(contents), "诊断预览字节为空或超过 16 KiB 上限。");
        }

        _contents = contents.ToArray();
        Coordinator = coordinator;
        HostIdentity = hostIdentity;
        CoordinatorGeneration = coordinatorGeneration;
    }

    public LauncherCoordinator? Coordinator { get; }

    public object HostIdentity { get; }

    public long CoordinatorGeneration { get; }

    public ReadOnlyMemory<byte> Contents => _contents is { } contents
        ? contents
        : throw new ObjectDisposedException(nameof(LauncherDiagnosticPreviewTicket));

    public string PreviewText => Encoding.UTF8.GetString(Contents.Span);

    public bool Matches(LauncherCoordinator? coordinator, object? hostIdentity, long coordinatorGeneration) =>
        _contents is not null &&
        ReferenceEquals(Coordinator, coordinator) &&
        ReferenceEquals(HostIdentity, hostIdentity) &&
        CoordinatorGeneration == coordinatorGeneration;

    public void Dispose()
    {
        var contents = Interlocked.Exchange(ref _contents, null);
        if (contents is not null)
        {
            Array.Clear(contents);
        }
    }
}
