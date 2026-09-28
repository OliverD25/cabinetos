using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Themes;

/// <summary>
/// What a theme of kind <c>system</c> shows while Windows is in light mode.
/// Its own palette and terminal are its dark-mode colours (docs/themes.md:
/// the light look is the window's choice), so the window uses these instead;
/// its accent and Mica tint stay the theme's. The colours are Windows 11's
/// own light-mode ones (WinUI's Fluent theme resources and the shared
/// colour palette), so light mode looks like a first-party app too
/// (Constitution Article 3). The terminal is Windows Terminal's
/// "One Half Light" scheme (MIT).
/// </summary>
public static class SystemLight
{
    /// <summary>The light-mode palette: dark text, light layers, and the hover fills as dark overlays.</summary>
    public static ThemePalette Palette { get; } = new(
        TextPrimary: "#1B1B1B",
        // Darker than Fluent's #9E alpha: rows dim this further, as the design dims their details.
        TextSecondary: "#000000B0",
        TextTertiary: "#00000080",
        TextDisabled: "#0000005C",
        LayerFill: "#FFFFFF80",
        LayerStroke: "#0000000F",
        LayerStrokeActive: "#00000029",
        // Also the rows' hover fill, which must darken a light layer.
        ControlFill: "#00000009",
        ControlFillHover: "#0000000F",
        AcrylicTint: "#FCFCFCD9",
        TerminalBackground: "#FFFFFF99",
        FolderIcon: "#F2C063",
        FolderIconFront: "#F8D98A",
        FileTypeColors: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["md"] = "#0078D4",
            ["rs"] = "#CA5010",
            ["toml"] = "#8764B8",
            ["exe"] = "#107C10",
            ["dll"] = "#107C10",
            ["bin"] = "#C42B1C",
            ["pdf"] = "#C42B1C",
            ["zip"] = "#C19C00",
        },
        PermissionLow: "#0F7B0F",
        PermissionMedium: "#9D5D00",
        PermissionHigh: "#C42B1C");

    /// <summary>Windows Terminal's "One Half Light".</summary>
    public static ThemeTerminal Terminal { get; } = new(
        Foreground: "#383A42",
        Background: "#FAFAFA",
        Cursor: "#4F525D",
        Ansi:
        [
            "#383A42", "#E45649", "#50A14F", "#C18301", "#0184BC", "#A626A4", "#0997B3", "#FAFAFA",
            "#4F525D", "#DF6C75", "#98C379", "#E4C07A", "#61AFEF", "#C577DD", "#56B5C1", "#FFFFFF",
        ]);
}
