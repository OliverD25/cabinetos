using System.Globalization;
using CabinetOS.Core.Themes;
using CabinetOS.Core.Tools;

namespace CabinetOS.Core.QuickView;

/// <summary>
/// The window's look as a viewer page gets it in <c>quickview-show</c> and <c>quickview-theme</c> (ADR 0023,
/// decision 2.2): dark or light, the panel's background, the two text colours and the accent of the theme in effect,
/// each opaque <c>#RRGGBB</c> (a translucent text colour is laid over the background first), and the font.
/// </summary>
public static class QuickViewTheme
{
    /// <summary>The font every page gets: the window's own.</summary>
    public const string Font = "Segoe UI Variable";

    /// <summary>The look of <paramref name="look"/>, or a plain dark or light one before a theme is applied.</summary>
    public static QuickViewLook From(ThemeLook? look, bool lightFallback)
    {
        var light = look?.IsLight ?? lightFallback;
        var background = look is not null && look.Acrylics.TryGetValue("CbPaletteAcrylicBrush", out var acrylic)
            ? Opaque(acrylic.Fallback, light ? Argb.White : Argb.Black)
            : light ? new Argb(0xFF, 0xF3, 0xF3, 0xF3) : new Argb(0xFF, 0x20, 0x20, 0x20);
        Argb Token(string key, Argb fallback) =>
            look is not null && look.Brushes.TryGetValue(key, out var value) ? Opaque(value, background) : fallback;
        var text = Token("CbTextPrimaryBrush", light ? Argb.Black : Argb.White);
        var secondary = Token("CbTextSecondaryBrush", light ? new Argb(0xFF, 0x5C, 0x5C, 0x5C) : new Argb(0xFF, 0xC5, 0xC5, 0xC5));
        var accent = look is not null ? Opaque(look.Accent, background) : light ? new Argb(0xFF, 0x00, 0x5F, 0xB8) : new Argb(0xFF, 0x60, 0xCD, 0xFF);
        return new QuickViewLook(light ? "light" : "dark", Hex(background), Hex(text), Hex(secondary), Hex(accent), Font);
    }

    /// <summary><paramref name="colour"/> laid over <paramref name="under"/> by its alpha.</summary>
    public static Argb Opaque(Argb colour, Argb under)
    {
        if (colour.A == 0xFF)
        {
            return colour;
        }
        byte Mix(byte top, byte bottom) => (byte)Math.Round(((top * colour.A) + (bottom * (255 - colour.A))) / 255.0);
        return new Argb(0xFF, Mix(colour.R, under.R), Mix(colour.G, under.G), Mix(colour.B, under.B));
    }

    private static string Hex(Argb colour) => string.Create(CultureInfo.InvariantCulture, $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}");
}
