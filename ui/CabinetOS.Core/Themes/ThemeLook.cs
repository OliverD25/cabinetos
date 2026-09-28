using System.Globalization;

namespace CabinetOS.Core.Themes;

/// <summary>A colour: alpha, red, green, blue.</summary>
public readonly record struct Argb(byte A, byte R, byte G, byte B)
{
    /// <summary>Opaque white.</summary>
    public static readonly Argb White = new(0xFF, 0xFF, 0xFF, 0xFF);

    /// <summary>Opaque black.</summary>
    public static readonly Argb Black = new(0xFF, 0x00, 0x00, 0x00);

    /// <summary>Reads a theme colour, <c>#RRGGBB</c> or <c>#RRGGBBAA</c>; false for anything else.</summary>
    public static bool TryParse(string? text, out Argb color)
    {
        color = default;
        if (text is not { Length: 7 or 9 } || text[0] != '#'
            || !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }
        color = text.Length == 7
            ? new Argb(0xFF, (byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : new Argb((byte)value, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8));
        return true;
    }

    /// <summary>How much it covers what is under it: 0 to 1.</summary>
    public double Opacity => A / 255.0;

    /// <summary>The same colour with this alpha.</summary>
    public Argb WithAlpha(byte alpha) => this with { A = alpha };

    /// <summary>The same colour with its alpha multiplied by <paramref name="factor"/>.</summary>
    public Argb ScaleAlpha(double factor) => this with { A = Clamp(A * factor) };

    /// <summary>The colour moved toward <paramref name="other"/> by <paramref name="amount"/> (0 to 1); the alpha stays.</summary>
    public Argb Mix(Argb other, double amount) =>
        new(A, Clamp(R + ((other.R - R) * amount)), Clamp(G + ((other.G - G) * amount)), Clamp(B + ((other.B - B) * amount)));

    /// <summary>The red, green and blue parts multiplied by <paramref name="factor"/> (darker below 1); the alpha stays.</summary>
    public Argb ScaleRgb(double factor) => new(A, Clamp(R * factor), Clamp(G * factor), Clamp(B * factor));

    /// <summary>CSS order, for web pages: <c>#RRGGBBAA</c>.</summary>
    public string ToCss() => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}{A:X2}");

    /// <summary>XAML order: <c>#AARRGGBB</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"#{A:X2}{R:X2}{G:X2}{B:X2}");

    private static byte Clamp(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}

/// <summary>An in-app Acrylic brush: its tint, how much tint and luminosity it lays over the blur, and its colour without blur.</summary>
public sealed record AcrylicLook(Argb Tint, double TintOpacity, double LuminosityOpacity, Argb Fallback);

/// <summary>The two stops of a two-colour gradient brush, in order.</summary>
public sealed record GradientLook(Argb First, Argb Second);

/// <summary>The tint over the Mica backdrop.</summary>
public sealed record MicaLook(Argb Tint, double Opacity);

/// <summary>
/// The shades of the accent colour that WinUI's own controls use
/// (<c>SystemAccentColor</c>, <c>SystemAccentColorLight1</c> to <c>Light3</c>,
/// <c>SystemAccentColorDark1</c> to <c>Dark3</c>).
/// </summary>
public sealed record AccentShades(Argb Base, Argb Light1, Argb Light2, Argb Light3, Argb Dark1, Argb Dark2, Argb Dark3);

/// <summary>The capability levels' colours: low, medium, high.</summary>
public sealed record LevelColors(Argb Low, Argb Medium, Argb High)
{
    /// <summary>The design's colours (docs/design/README.md, "Permission levels").</summary>
    public static readonly LevelColors Design = new(new Argb(0xFF, 0x6C, 0xCB, 0x5F), new Argb(0xFF, 0xF2, 0xC0, 0x63), new Argb(0xFF, 0xF2, 0x7A, 0x6C));

    /// <summary>The colour of <paramref name="level"/> (<c>low</c>, <c>medium</c>, <c>high</c>); grey for an unknown one.</summary>
    public Argb For(string level) => level switch
    {
        "low" => Low,
        "medium" => Medium,
        "high" => High,
        _ => new Argb(0xFF, 0x8B, 0x8B, 0x8B),
    };
}

/// <summary>The terminal page's colours, as CSS colours (<c>#RRGGBBAA</c>).</summary>
public sealed record TerminalLook(string Foreground, string Background, string Cursor, string Selection, IReadOnlyList<string> Ansi);

/// <summary>
/// Everything the window paints from one theme (docs/ui.md, "Themes"): each
/// design token's colour, the Acrylic brushes, the two-stop gradients, the
/// accent and its shades, the Mica tint, the file-type and folder colours,
/// the capability levels, and the terminal's colours.
/// </summary>
public sealed class ThemeLook
{
    /// <summary>The theme's ID.</summary>
    public required string Id { get; init; }

    /// <summary>The theme's name.</summary>
    public required string Name { get; init; }

    /// <summary>Whether WinUI's own controls are drawn in light mode.</summary>
    public required bool IsLight { get; init; }

    /// <summary>Whether the accent is the Windows accent colour (the theme's is null).</summary>
    public required bool FollowsSystemAccent { get; init; }

    /// <summary>The accent the window draws with: the theme's, else the system's shade for this mode.</summary>
    public required Argb Accent { get; init; }

    /// <summary>The accent's shades, for WinUI's own controls.</summary>
    public required AccentShades AccentShades { get; init; }

    /// <summary>The tint over Mica, or null for plain Mica.</summary>
    public required MicaLook? Mica { get; init; }

    /// <summary>Every solid design token, by resource key (<c>CbLayerFillBrush</c>).</summary>
    public required IReadOnlyDictionary<string, Argb> Brushes { get; init; }

    /// <summary>The Acrylic brushes, by resource key.</summary>
    public required IReadOnlyDictionary<string, AcrylicLook> Acrylics { get; init; }

    /// <summary>The two-stop gradient brushes, by resource key.</summary>
    public required IReadOnlyDictionary<string, GradientLook> Gradients { get; init; }

    /// <summary>The stroke of file glyphs by extension, without the dot (<c>md</c>).</summary>
    public required IReadOnlyDictionary<string, Argb> FileTypes { get; init; }

    /// <summary>The capability levels in the plugin list and the review dialog.</summary>
    public required LevelColors Levels { get; init; }

    /// <summary>The terminal page's colours.</summary>
    public required TerminalLook Terminal { get; init; }
}

/// <summary>A theme colour that is not <c>#RRGGBB</c> or <c>#RRGGBBAA</c>: which key, and what it said.</summary>
public sealed class ThemeFormatException(string key, string? value)
    : FormatException($"the theme's {key} is not a colour: {value ?? "null"}")
{
    /// <summary>The key, such as <c>palette.textPrimary</c>.</summary>
    public string Key { get; } = key;
}
