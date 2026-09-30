using System.Buffers;
using System.Text.Json;

namespace CabinetOS.Core.Presentation;

/// <summary>The compact overlay's size in device-independent pixels (<c>ui.compactOverlay</c>): the window's whole outer size.</summary>
public readonly record struct CompactSize(int Width, int Height);

/// <summary>
/// The compact overlay (docs/ui.md, "Compact overlay"): the window as a small always-on-top drawer.
/// What it decides without a window is here, with tests: the size it opens at (the saved one, or
/// 480 by 640, raised to the window's minimum and cut to the work area), where it opens, and the
/// size and JSON of <c>ui.compactOverlay</c>.
/// </summary>
public static class CompactOverlayLayout
{
    /// <summary>The width the drawer has when <c>ui.compactOverlay</c> is null.</summary>
    public const int DefaultWidth = 480;

    /// <summary>The height the drawer has when <c>ui.compactOverlay</c> is null.</summary>
    public const int DefaultHeight = 640;

    /// <summary>The window's minimum width while the drawer is on (600 in the full window).</summary>
    public const int MinWidth = 360;

    /// <summary>The window's minimum height: the full window's, which the drawer keeps.</summary>
    public const int MinHeight = 480;

    /// <summary>The least the core accepts for either side of <c>ui.compactOverlay</c>.</summary>
    public const int MinSetting = 240;

    /// <summary>The most the core accepts for either side of <c>ui.compactOverlay</c>.</summary>
    public const int MaxSetting = 4000;

    /// <summary>The size of the drawer when the user has not resized it.</summary>
    public static CompactSize Default => new(DefaultWidth, DefaultHeight);

    /// <summary>
    /// The size the drawer opens at in a work area <paramref name="workAreaWidth"/> by
    /// <paramref name="workAreaHeight"/> DIPs: <paramref name="saved"/> or the default, never under
    /// the window's minimum and never larger than the work area (the minimum wins on a screen smaller
    /// than it). <c>Saved</c> says whether the size came from the file, for the log.
    /// </summary>
    public static (CompactSize Size, bool Saved) Decide(CompactSize? saved, double workAreaWidth, double workAreaHeight)
    {
        var wanted = saved ?? Default;
        return (new CompactSize(Fit(wanted.Width, MinWidth, workAreaWidth), Fit(wanted.Height, MinHeight, workAreaHeight)), saved is not null);

        static int Fit(int size, int minimum, double room) => Math.Clamp(size, minimum, Math.Max(minimum, (int)Math.Floor(room)));
    }

    /// <summary>
    /// Where a drawer <paramref name="size"/> pixels wide (or high) opens along one axis: where the
    /// window was (<paramref name="position"/>), moved only as far as keeps it inside the work area
    /// that starts at <paramref name="areaStart"/> and is <paramref name="areaSize"/> long. Pixels.
    /// </summary>
    public static int Place(int position, int size, int areaStart, int areaSize) =>
        Math.Clamp(position, areaStart, Math.Max(areaStart, areaStart + areaSize - size));

    /// <summary>A window's outer size in pixels as <c>ui.compactOverlay</c> keeps it: whole DIPs, within the core's bounds.</summary>
    public static CompactSize ToSetting(int widthPixels, int heightPixels, double scale)
    {
        var factor = scale > 0 ? scale : 1;
        return new CompactSize(
            Math.Clamp((int)Math.Round(widthPixels / factor), MinSetting, MaxSetting),
            Math.Clamp((int)Math.Round(heightPixels / factor), MinSetting, MaxSetting));
    }

    /// <summary><paramref name="size"/> as <c>set_value ui.compactOverlay</c> takes it.</summary>
    public static JsonElement ToJson(CompactSize size)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("width", size.Width);
            writer.WriteNumber("height", size.Height);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The size from the core's <c>config</c> reply (<c>ui.compactOverlay</c>), or null: absent,
    /// <c>null</c>, or not two whole numbers within the core's bounds (the core refuses anything else,
    /// so a window never meets it).
    /// </summary>
    public static CompactSize? FromConfig(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("ui", out var ui) || ui.ValueKind != JsonValueKind.Object
            || !ui.TryGetProperty("compactOverlay", out var overlay) || overlay.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return Side(overlay, "width") is { } width && Side(overlay, "height") is { } height ? new CompactSize(width, height) : null;

        static int? Side(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var size) && size is >= MinSetting and <= MaxSetting
                ? size
                : null;
    }
}
