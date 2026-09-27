using System.Text;

namespace AiGoofish.Launcher.Core;

public sealed class BoundedLogBuffer
{
    public const int MaxMessageUtf8Bytes = 4096;
    private const string TruncationSuffix = "…";
    private static readonly int TruncationSuffixBytes = Encoding.UTF8.GetByteCount(TruncationSuffix);

    private readonly Queue<LauncherLogEntry> _entries;
    private readonly object _sync = new();

    public BoundedLogBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "日志容量必须大于零。");
        }

        Capacity = capacity;
        _entries = new Queue<LauncherLogEntry>(capacity);
    }

    public int Capacity { get; }

    public void Add(LauncherLogEntry entry)
    {
        var boundedEntry = BoundEntry(entry);

        lock (_sync)
        {
            while (_entries.Count >= Capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(boundedEntry);
        }
    }

    public static LauncherLogEntry BoundEntry(LauncherLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry with { Message = BoundMessage(entry.Message) };
    }

    public IReadOnlyList<LauncherLogEntry> Snapshot()
    {
        lock (_sync)
        {
            return _entries.ToArray();
        }
    }

    private static string BoundMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Encoding.UTF8.GetByteCount(message) <= MaxMessageUtf8Bytes)
        {
            return message;
        }

        var maxContentBytes = MaxMessageUtf8Bytes - TruncationSuffixBytes;
        var builder = new StringBuilder(maxContentBytes);
        var contentBytes = 0;
        foreach (var rune in message.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (contentBytes + runeBytes > maxContentBytes)
            {
                break;
            }

            builder.Append(rune);
            contentBytes += runeBytes;
        }

        builder.Append(TruncationSuffix);
        return builder.ToString();
    }
}
