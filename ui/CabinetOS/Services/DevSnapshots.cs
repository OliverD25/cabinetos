using System.Runtime.InteropServices.WindowsRuntime;
using CabinetOS.Core.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace CabinetOS.Services;

/// <summary>
/// A development aid, off unless <c>CABINETOS_UI_SNAPSHOT</c> names a folder:
/// once the first folders are shown it renders the window's content to
/// <c>window.png</c>, and with <c>CABINETOS_UI_SNAPSHOT_QUERY</c> also opens
/// the palette with that text and renders <c>palette.png</c>. It draws the
/// XAML tree itself, so it works when the screen is locked or off; the Mica
/// backdrop is not part of that tree and comes out transparent.
/// </summary>
internal static class DevSnapshots
{
    public const string FolderEnv = "CABINETOS_UI_SNAPSHOT";
    public const string QueryEnv = "CABINETOS_UI_SNAPSHOT_QUERY";

    public static string? Folder => Environment.GetEnvironmentVariable(FolderEnv) is { Length: > 0 } folder ? folder : null;

    public static string? Query => Environment.GetEnvironmentVariable(QueryEnv);

    /// <summary>Renders <paramref name="element"/> to <c>&lt;folder&gt;\&lt;name&gt;.png</c>.</summary>
    public static async Task RenderAsync(UIElement element, string name)
    {
        if (Folder is not { } folder)
        {
            return;
        }
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".png");
        using (var file = File.Create(path))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file.AsRandomAccessStream());
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
        }
        Diag.Info("cabinetos_ui::snapshot", "snapshot written", new LogField("path", path),
            new LogField("width", bitmap.PixelWidth), new LogField("height", bitmap.PixelHeight));
    }
}
