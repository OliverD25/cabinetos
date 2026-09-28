using System.Runtime.InteropServices.WindowsRuntime;
using CabinetOS.Core.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace CabinetOS.Services;

/// <summary>One step of a snapshot run: what to do, and its argument.</summary>
internal sealed record SnapshotStep(string Kind, string Argument);

/// <summary>
/// A development aid, off unless <c>CABINETOS_UI_SNAPSHOT</c> names a folder.
/// Once the first folders are shown, the window runs the steps of
/// <c>CABINETOS_UI_SNAPSHOT_STEPS</c> (default <c>shot:window</c>), separated
/// by <c>;</c>:
/// <c>cmd:&lt;command&gt; [json]</c> runs a command through the router and waits for it,
/// <c>cmd-nowait:</c> runs one that waits for the user (a dialog, a rename),
/// <c>path:&lt;folder&gt;</c> goes there in the active pane, <c>pane:0|1</c> makes a pane active,
/// <c>select:&lt;name&gt;</c> selects a row, <c>selectall</c> selects every row,
/// <c>menu:&lt;name&gt;</c> opens the context menu on a row (<c>menu:*</c> on the empty space),
/// <c>rename:&lt;text&gt;</c> types a name into the rename box and presses Enter,
/// <c>dismiss</c> closes an open dialog, <c>type:&lt;text&gt;</c> types into the palette,
/// <c>until:running|conflict</c> waits for a job, <c>wait:&lt;ms&gt;</c> waits, and
/// <c>shot:&lt;name&gt;</c> renders the window's content to <c>&lt;name&gt;.png</c>.
/// The window draws its own content, so this works when the screen is locked
/// or off; the Mica backdrop and dialogs (a popup layer) are not part of it.
/// </summary>
internal static class DevSnapshots
{
    public const string FolderEnv = "CABINETOS_UI_SNAPSHOT";
    public const string StepsEnv = "CABINETOS_UI_SNAPSHOT_STEPS";

    public static string? Folder => Environment.GetEnvironmentVariable(FolderEnv) is { Length: > 0 } folder ? folder : null;

    /// <summary>The steps to run, in order.</summary>
    public static IReadOnlyList<SnapshotStep> Steps()
    {
        var text = Environment.GetEnvironmentVariable(StepsEnv);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "shot:window";
        }
        return text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(step => step.Split(':', 2))
            .Select(parts => new SnapshotStep(parts[0], parts.Length > 1 ? parts[1] : ""))
            .ToList();
    }

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
