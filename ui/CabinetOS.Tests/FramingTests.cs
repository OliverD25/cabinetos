using System.Text;
using CabinetOS.Core.Ipc;

namespace CabinetOS.Tests;

public class FramingTests
{
    [Fact]
    public async Task A_frame_is_a_little_endian_length_then_the_payload()
    {
        using var stream = new MemoryStream();
        await Framing.WriteFrameAsync(stream, Encoding.UTF8.GetBytes("hello"));
        Assert.Equal([5, 0, 0, 0, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o'], stream.ToArray());
    }

    [Fact]
    public async Task Frames_come_back_in_order_and_the_end_reads_as_null()
    {
        using var stream = new MemoryStream();
        foreach (var text in new[] { """{"a":1}""", "", """{"b":"Документи"}""" })
        {
            await Framing.WriteFrameAsync(stream, Encoding.UTF8.GetBytes(text));
        }
        stream.Position = 0;
        Assert.Equal("""{"a":1}""", Encoding.UTF8.GetString((await Framing.ReadFrameAsync(stream))!));
        Assert.Empty((await Framing.ReadFrameAsync(stream))!);
        Assert.Equal("""{"b":"Документи"}""", Encoding.UTF8.GetString((await Framing.ReadFrameAsync(stream))!));
        Assert.Null(await Framing.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task A_frame_over_16_MiB_is_refused_on_both_sides()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<FrameTooLargeException>(
            () => Framing.WriteFrameAsync(stream, new byte[Framing.MaxFrameBytes + 1]).AsTask());
        Assert.Equal(0, stream.Length);

        using var announced = new MemoryStream([0x01, 0x00, 0x00, 0x01]);
        var error = await Assert.ThrowsAsync<FrameTooLargeException>(() => Framing.ReadFrameAsync(announced).AsTask());
        Assert.Equal(Framing.MaxFrameBytes + 1, error.Length);
    }

    [Fact]
    public async Task A_frame_of_exactly_16_MiB_passes()
    {
        using var stream = new MemoryStream();
        await Framing.WriteFrameAsync(stream, new byte[Framing.MaxFrameBytes]);
        stream.Position = 0;
        Assert.Equal(Framing.MaxFrameBytes, (await Framing.ReadFrameAsync(stream))!.Length);
    }

    [Fact]
    public async Task A_stream_that_ends_inside_a_frame_is_an_error()
    {
        using var inHeader = new MemoryStream([5, 0]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Framing.ReadFrameAsync(inHeader).AsTask());

        using var inPayload = new MemoryStream([5, 0, 0, 0, (byte)'h']);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Framing.ReadFrameAsync(inPayload).AsTask());
    }
}
