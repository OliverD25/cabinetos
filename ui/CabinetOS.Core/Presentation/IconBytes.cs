using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace CabinetOS.Core.Presentation;

/// <summary>
/// An icon's PNG from the core's base64 text (<c>get_icon</c>), turned into a
/// stream on a worker thread, so the UI thread only hands the stream to a
/// bitmap (docs/ui.md, "Scrolling"): a folder of programs asks for an icon
/// per program while it scrolls.
/// </summary>
public static class IconBytes
{
    /// <summary>The PNG in a stream at its start, and the thread that made it; a <see cref="FormatException"/> for text that is not base64.</summary>
    public static Task<(InMemoryRandomAccessStream Stream, int Thread)> DecodeAsync(string base64) => Task.Run(async () =>
    {
        var bytes = Convert.FromBase64String(base64);
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        return (stream, Environment.CurrentManagedThreadId);
    });
}
