using System.Runtime.InteropServices.WindowsRuntime;
using CabinetOS.Core.Presentation;
using Windows.Storage.Streams;

namespace CabinetOS.Tests;

/// <summary>An icon's PNG is decoded from base64 away from the UI thread (docs/ui.md, "Scrolling").</summary>
public class IconBytesTests
{
    [Fact]
    public async Task The_png_comes_back_as_a_stream_at_its_start_made_on_another_thread()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        // A thread of its own stands for the UI thread: a pool thread as the caller could
        // itself run the decode once it awaits, and the IDs would match by chance.
        using var ui = new Support.UiThread();
        var (caller, stream, thread) = await ui.RunAsync(async () =>
        {
            var id = Environment.CurrentManagedThreadId;
            var (decoded, on) = await IconBytes.DecodeAsync(Convert.ToBase64String(png));
            return (id, decoded, on);
        });

        using (stream)
        {
            Assert.NotEqual(caller, thread);
            Assert.Equal(0UL, stream.Position);
            Assert.Equal((ulong)png.Length, stream.Size);
            var buffer = new Windows.Storage.Streams.Buffer((uint)png.Length);
            var read = await stream.ReadAsync(buffer, (uint)png.Length, InputStreamOptions.None);
            Assert.Equal(png, read.ToArray());
        }
    }

    [Fact]
    public async Task Text_that_is_not_base64_is_a_format_error()
    {
        await Assert.ThrowsAsync<FormatException>(() => IconBytes.DecodeAsync("not base64!"));
    }
}
