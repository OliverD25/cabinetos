using System.Runtime.InteropServices.WindowsRuntime;
using CabinetOS.Core.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

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
/// <c>search:&lt;text&gt;</c> types into the search field,
/// <c>open:&lt;name&gt;</c> presses Enter on a row (a file may open in a Tool Extension),
/// <c>terminal:&lt;text&gt;</c> types into the shown shell (<c>{enter}</c> is Enter),
/// <c>crash:terminal</c> or <c>crash:tool:&lt;id&gt;</c> ends that page's browser process,
/// <c>dock:&lt;pixels&gt;</c> drags the dock's splitter to that size (and saves it, as a drag does),
/// <c>mode:light|dark|windows</c> makes the window take Windows as set to that mode (a theme of
/// kind <c>system</c> follows) without changing the PC's setting,
/// <c>click:&lt;name&gt;</c> presses the first shown button or menu item (of an open menu too) with that name as UI Automation reports it
/// (the keyboard moves to it, then its automation peer invokes it), <c>focus:&lt;label&gt;</c> logs where the keyboard is,
/// <c>scroll:&lt;pages&gt;</c> presses PageDown in the active pane 30 times a second (<c>scroll:&lt;pages&gt;/&lt;n&gt;</c>: once every n frames)
/// and logs the frame table of the run when <c>CABINETOS_UI_FRAMESTATS=1</c>,
/// <c>until:running|conflict|terminal|search|tool</c> waits for a job, a shell, an answer or a tool page, <c>wait:&lt;ms&gt;</c> waits, and
/// <c>shot:&lt;name&gt;</c> renders the window's content to <c>&lt;name&gt;.png</c>.
/// The window draws its own content, so this works when the screen is locked
/// or off; the Mica backdrop is not part of it (a stand-in colour is).
/// WebView2 pages are drawn by WebView2 itself, and open dialogs and menus
/// (the popup layer) on their own, and all are laid over their place.
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

    /// <summary>
    /// Renders <paramref name="element"/> to <c>&lt;folder&gt;\&lt;name&gt;.png</c>.
    /// A WebView2 draws outside XAML, so <c>RenderTargetBitmap</c> leaves it
    /// empty: each page of <paramref name="pages"/> that is on screen is
    /// captured by WebView2 itself and drawn over its place. A dialog sits in
    /// the popup layer, outside the window's content: each of
    /// <paramref name="dialogs"/> is rendered on its own and laid over the
    /// image, scrim included. The backdrop (Mica) is not part of the window's
    /// content either; with a <paramref name="backdrop"/> colour the image is
    /// laid over it, opaque, as a stand-in for Mica and its tint.
    /// </summary>
    public static async Task RenderAsync(FrameworkElement element, string name, IEnumerable<WebViewHost>? pages = null, Windows.UI.Color? backdrop = null,
        IEnumerable<UIElement>? dialogs = null)
    {
        if (Folder is not { } folder)
        {
            return;
        }
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        foreach (var page in pages ?? [])
        {
            await DrawPageAsync(page, element, pixels, bitmap.PixelWidth, bitmap.PixelHeight);
        }
        foreach (var dialog in dialogs ?? [])
        {
            await DrawDialogAsync(dialog, element, pixels, bitmap.PixelWidth, bitmap.PixelHeight);
        }
        if (backdrop is { } under)
        {
            // Premultiplied BGRA "over" an opaque colour.
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var clear = 255 - pixels[i + 3];
                pixels[i] = (byte)(pixels[i] + (under.B * clear / 255));
                pixels[i + 1] = (byte)(pixels[i + 1] + (under.G * clear / 255));
                pixels[i + 2] = (byte)(pixels[i + 2] + (under.R * clear / 255));
                pixels[i + 3] = 255;
            }
        }
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

    private static async Task DrawPageAsync(WebViewHost page, FrameworkElement root, byte[] pixels, int width, int height)
    {
        if (page.View is not { ActualWidth: > 0, ActualHeight: > 0 } view || !IsShown(view, root))
        {
            return;
        }
        using var stream = new InMemoryRandomAccessStream();
        if (!await page.CaptureAsync(stream))
        {
            return;
        }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var scale = width / root.ActualWidth;
        var bounds = view.TransformToVisual(root).TransformBounds(new Rect(0, 0, view.ActualWidth, view.ActualHeight));
        var left = (int)Math.Round(bounds.X * scale);
        var top = (int)Math.Round(bounds.Y * scale);
        var pageWidth = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        var pageHeight = Math.Max(1, (int)Math.Round(bounds.Height * scale));
        var transform = new BitmapTransform { ScaledWidth = (uint)pageWidth, ScaledHeight = (uint)pageHeight, InterpolationMode = BitmapInterpolationMode.Fant };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var source = data.DetachPixelData();
        // Premultiplied "over": the page's transparent parts keep the window's fill.
        for (var y = 0; y < pageHeight; y++)
        {
            var targetY = top + y;
            if (targetY < 0 || targetY >= height)
            {
                continue;
            }
            for (var x = 0; x < pageWidth; x++)
            {
                var targetX = left + x;
                if (targetX < 0 || targetX >= width)
                {
                    continue;
                }
                var from = ((y * pageWidth) + x) * 4;
                var to = ((targetY * width) + targetX) * 4;
                var alpha = source[from + 3];
                for (var channel = 0; channel < 4; channel++)
                {
                    pixels[to + channel] = (byte)(source[from + channel] + (pixels[to + channel] * (255 - alpha) / 255));
                }
            }
        }
    }

    // A dialog of the popup layer, rendered on its own and laid over the image at its place ("over", premultiplied).
    private static async Task DrawDialogAsync(UIElement dialog, FrameworkElement root, byte[] pixels, int width, int height)
    {
        var bitmap = new RenderTargetBitmap();
        try
        {
            await bitmap.RenderAsync(dialog);
        }
        catch (ArgumentException)
        {
            // Not rendered yet (its first frame): the image has no dialog.
            return;
        }
        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0)
        {
            return;
        }
        var source = (await bitmap.GetPixelsAsync()).ToArray();
        var scale = width / root.ActualWidth;
        var at = dialog.TransformToVisual(null).TransformPoint(new Point(0, 0));
        var left = (int)Math.Round(at.X * scale);
        var top = (int)Math.Round(at.Y * scale);
        for (var y = 0; y < bitmap.PixelHeight; y++)
        {
            var targetY = top + y;
            if (targetY < 0 || targetY >= height)
            {
                continue;
            }
            for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                var targetX = left + x;
                if (targetX < 0 || targetX >= width)
                {
                    continue;
                }
                var from = ((y * bitmap.PixelWidth) + x) * 4;
                var to = ((targetY * width) + targetX) * 4;
                var alpha = source[from + 3];
                for (var channel = 0; channel < 4; channel++)
                {
                    pixels[to + channel] = (byte)(source[from + channel] + (pixels[to + channel] * (255 - alpha) / 255));
                }
            }
        }
    }

    private static bool IsShown(UIElement element, UIElement root)
    {
        for (var current = element as DependencyObject; current is not null && current != root; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: Visibility.Collapsed })
            {
                return false;
            }
        }
        return true;
    }
}
