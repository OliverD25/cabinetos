using CabinetOS.Core.Themes;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CabinetOS.Services;

/// <summary>
/// The file-type strokes of the row glyphs (docs/design/README.md, "Color";
/// the theme's <c>fileTypeColors</c>): one brush per extension, shared by
/// every row, so a new theme recolours all rows at once.
/// </summary>
internal static class ThemeBrushes
{
    // The design's colours, until the core's theme arrives (the default theme's).
    private static readonly Dictionary<string, SolidColorBrush> FileTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["md"] = Brush(0x60, 0xCD, 0xFF),
        ["rs"] = Brush(0xF0, 0x90, 0x6C),
        ["toml"] = Brush(0xC9, 0xB6, 0xFF),
        ["exe"] = Brush(0x6C, 0xCB, 0x5F),
        ["dll"] = Brush(0x6C, 0xCB, 0x5F),
        ["bin"] = Brush(0xE0, 0x70, 0x5E),
        ["pdf"] = Brush(0xE0, 0x70, 0x5E),
        ["zip"] = Brush(0xF2, 0xC0, 0x63),
    };

    /// <summary>The stroke for files with <paramref name="extension"/> (no dot), or null for the plain one.</summary>
    public static SolidColorBrush? FileType(string extension) => FileTypes.GetValueOrDefault(extension);

    /// <summary>Takes the theme's file-type colours; an extension new to the theme gets a brush of its own.</summary>
    public static void Apply(ThemeLook look)
    {
        foreach (var (extension, color) in look.FileTypes)
        {
            var value = Color.FromArgb(color.A, color.R, color.G, color.B);
            if (FileTypes.TryGetValue(extension, out var brush))
            {
                brush.Color = value;
            }
            else
            {
                FileTypes[extension] = new SolidColorBrush(value);
            }
        }
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(0xFF, r, g, b));
}
