using System.Buffers;
using System.Buffers.Binary;

namespace CabinetOS.Core.Ipc;

/// <summary>A frame longer than the protocol allows; the connection must end.</summary>
public sealed class FrameTooLargeException(long length)
    : IOException($"a frame of {length} bytes is larger than the {Framing.MaxFrameBytes}-byte limit")
{
    /// <summary>The length that was announced or attempted.</summary>
    public long Length { get; } = length;
}

/// <summary>
/// The control channel's framing (docs/ipc.md, "The pipe"): a 4-byte
/// little-endian length, then that many bytes of UTF-8 JSON, 16 MiB at most.
/// </summary>
public static class Framing
{
    /// <summary>The largest payload of one frame: 16 MiB.</summary>
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Writes one frame with a single write, so frames from two writers can
    /// never interleave byte by byte.
    /// </summary>
    public static async ValueTask WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length > MaxFrameBytes)
        {
            throw new FrameTooLargeException(payload.Length);
        }
        var length = payload.Length + 4;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)payload.Length);
            payload.Span.CopyTo(buffer.AsSpan(4));
            await stream.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads one frame. Returns null when the stream ends cleanly between two
    /// frames; throws <see cref="EndOfStreamException"/> when it ends inside
    /// one, and <see cref="FrameTooLargeException"/> for a length over the limit.
    /// </summary>
    public static async ValueTask<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        var read = await stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }
        if (read < 4)
        {
            throw new EndOfStreamException("the stream ended inside a frame header");
        }
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > MaxFrameBytes)
        {
            throw new FrameTooLargeException(length);
        }
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }
}
