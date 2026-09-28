namespace CabinetOS.Core.Terminal;

/// <summary>
/// Collects a session's output from the byte pipe's reader thread and hands it
/// to the UI thread in base64 chunks, at most 60 times a second. A shell that
/// prints a million lines then costs 60 messages a second to the terminal
/// page, however small its writes are, and the UI thread never waits for the pipe.
/// </summary>
public sealed class OutputCoalescer(Func<long> nowMilliseconds)
{
    /// <summary>The shortest gap between two flushes: 60 a second.</summary>
    public const int FrameMilliseconds = 16;

    /// <summary>The most bytes one message carries (base64 makes it a third larger).</summary>
    public const int MaxChunkBytes = 192 * 1024;

    private readonly object _gate = new();
    private byte[] _buffer = new byte[16 * 1024];
    private int _count;
    private long _lastFlush = long.MinValue / 2;

    /// <summary>Whether bytes wait for the next flush.</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _count > 0;
            }
        }
    }

    /// <summary>
    /// Adds output; any thread. Returns true when the buffer was empty, so the
    /// caller schedules one flush for the whole burst.
    /// </summary>
    public bool Add(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return false;
        }
        lock (_gate)
        {
            var wasEmpty = _count == 0;
            if (_count + bytes.Length > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _count + bytes.Length));
            }
            bytes.CopyTo(_buffer.AsSpan(_count));
            _count += bytes.Length;
            return wasEmpty;
        }
    }

    /// <summary>How long until the next flush may run; 0 when it may run now.</summary>
    public int MillisecondsUntilDue()
    {
        var wait = _lastFlush + FrameMilliseconds - nowMilliseconds();
        return wait <= 0 ? 0 : (int)wait;
    }

    /// <summary>Takes everything that waited, as base64 chunks in order; records the time.</summary>
    public IReadOnlyList<string> Flush()
    {
        lock (_gate)
        {
            _lastFlush = nowMilliseconds();
            if (_count == 0)
            {
                return [];
            }
            var chunks = new List<string>((_count / MaxChunkBytes) + 1);
            for (var offset = 0; offset < _count; offset += MaxChunkBytes)
            {
                chunks.Add(Convert.ToBase64String(_buffer, offset, Math.Min(MaxChunkBytes, _count - offset)));
            }
            _count = 0;
            if (_buffer.Length > 1024 * 1024)
            {
                // A burst grew the buffer; give the memory back once it is drained.
                _buffer = new byte[16 * 1024];
            }
            return chunks;
        }
    }
}
