using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace CabinetOS.Services;

/// <summary>
/// The shell's icons, asked for by key (<c>get_icon</c>, protocol 9) and
/// kept for the window's life: a key names the same pixels for as long as it
/// exists (a changed program gets a new key, docs/ipc.md). One request per
/// key and size; rows show the built-in glyph until theirs arrives.
/// </summary>
internal sealed class IconCache(ICoreChannel core)
{
    private const string Target = "cabinetos_ui::icons";

    private readonly Dictionary<(string Key, uint Size), ImageSource> _icons = [];
    private readonly Dictionary<string, ImageSource> _anySize = new(StringComparer.Ordinal);
    private readonly HashSet<(string Key, uint Size)> _asked = [];
    private bool _unavailable;

    /// <summary>Raised on the UI thread with a key whose icon arrived.</summary>
    public event Action<string>? Loaded;

    /// <summary>The pixel size asked for (from the screen's scale; see <c>IconSizes</c>).</summary>
    public uint Size { get; set; } = 16;

    /// <summary>The icon for <paramref name="key"/>, or null while it is on its way (it is asked for once).</summary>
    public ImageSource? Get(string key)
    {
        var entry = (key, Size);
        if (_icons.TryGetValue(entry, out var icon))
        {
            return icon;
        }
        if (!_unavailable && _asked.Add(entry))
        {
            _ = LoadAsync(key, Size);
        }
        // After a move to a screen with another scale, the old size is better than the glyph until the new one comes.
        return _anySize.GetValueOrDefault(key);
    }

    /// <summary>A core was started again: its <c>path:</c> keys may be forgotten, so failed keys are asked anew.</summary>
    public void Reset()
    {
        _asked.RemoveWhere(entry => !_icons.ContainsKey(entry));
        _unavailable = false;
    }

    private async Task LoadAsync(string key, uint size)
    {
        CoreReply reply;
        try
        {
            reply = await core.RequestAsync(new GetIconRequest(key, size));
        }
        catch (IOException error)
        {
            Diag.Debug(Target, "cannot ask for an icon", new LogField("key", key), new LogField("error", error.Message));
            return;
        }
        switch (reply)
        {
            case IconReply icon:
                try
                {
                    // The base64 text becomes a PNG stream on a worker thread; the await comes back to this one.
                    var (png, _) = await IconBytes.DecodeAsync(icon.PngBase64);
                    using var stream = png;
                    var started = FrameParts.Start();
                    var bitmap = new BitmapImage { DecodePixelWidth = (int)size, DecodePixelHeight = (int)size };
                    var decoded = bitmap.SetSourceAsync(stream);
                    FrameParts.Stop(FramePart.Icons, started);
                    await decoded;
                    started = FrameParts.Start();
                    _icons[(key, size)] = bitmap;
                    _anySize[key] = bitmap;
                    FrameParts.Stop(FramePart.Icons, started);
                    Loaded?.Invoke(key);
                }
                catch (Exception error) when (error is FormatException or System.Runtime.InteropServices.COMException)
                {
                    Diag.Info(Target, "an icon could not be decoded", new LogField("key", key), new LogField("error", error.Message));
                }
                break;
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                // A core before protocol 9: the built-in glyphs stay.
                _unavailable = true;
                break;
            case ErrorReply error:
                Diag.Debug(Target, "no icon", new LogField("key", key), new LogField("code", error.Code));
                break;
        }
    }
}
