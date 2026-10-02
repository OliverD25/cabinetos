using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Themes;

/// <summary>
/// Turns a theme from the core (docs/themes.md) into the colours of every
/// design token the window uses (docs/ui.md, "Themes"). Each token comes
/// from one palette key: the key itself where the roles match, else the key
/// with the alpha the design gives that token, so the shipped
/// <c>default</c> theme reproduces the design's tokens exactly and a theme
/// changes all of them coherently. The UI applies the result; the mapping
/// itself runs without a window, so it is tested.
/// </summary>
public static class ThemeMapper
{
    /// <summary>How far the terminal's right badge turns the accent's hue (docs/themes.md, "palette").</summary>
    public const double RightBadgeHueShift = 150;

    /// <summary>
    /// Maps <paramref name="theme"/>. <paramref name="systemAccent"/> is the
    /// Windows accent's shades, used when the theme's accent is null;
    /// <paramref name="systemIsLight"/> is Windows' mode, which a theme of
    /// kind <c>system</c> follows: in light mode with the colours of
    /// <see cref="SystemLight"/>. Throws <see cref="ThemeFormatException"/>
    /// for a colour that is not one.
    /// </summary>
    public static ThemeLook Map(ColorTheme theme, AccentShades systemAccent, bool systemIsLight = false)
    {
        var followsMode = theme.FollowsSystemMode;
        var isLight = followsMode ? systemIsLight : theme.IsLight;
        var (p, terminal) = followsMode && systemIsLight ? (SystemLight.Palette, SystemLight.Terminal) : (theme.Palette, theme.Terminal);
        var text = Parse(p.TextPrimary, "palette.textPrimary");
        var secondary = Parse(p.TextSecondary, "palette.textSecondary");
        var tertiary = Parse(p.TextTertiary, "palette.textTertiary");
        var disabled = Parse(p.TextDisabled, "palette.textDisabled");
        var layerFill = Parse(p.LayerFill, "palette.layerFill");
        var layerStroke = Parse(p.LayerStroke, "palette.layerStroke");
        var layerStrokeActive = Parse(p.LayerStrokeActive, "palette.layerStrokeActive");
        var controlFill = Parse(p.ControlFill, "palette.controlFill");
        var controlFillHover = Parse(p.ControlFillHover, "palette.controlFillHover");
        var acrylic = Parse(p.AcrylicTint, "palette.acrylicTint");
        var terminalFill = Parse(p.TerminalBackground, "palette.terminalBackground");
        var folder = Parse(p.FolderIcon, "palette.folderIcon");
        var folderFront = Parse(p.FolderIconFront, "palette.folderIconFront");
        var levels = new LevelColors(
            Parse(p.PermissionLow, "palette.permissionLow"),
            Parse(p.PermissionMedium, "palette.permissionMedium"),
            Parse(p.PermissionHigh, "palette.permissionHigh"));

        var followsSystem = theme.Accent is null;
        var shades = followsSystem ? systemAccent : Shades(Parse(theme.Accent, "accent"), isLight);
        // What WinUI fills accent controls with: Light2 in dark mode, Dark1 in light mode.
        var accent = (isLight ? shades.Dark1 : shades.Light2).WithAlpha(0xFF);

        // The terminal's pane badges: the left is the accent, the right the accent turned by 150 degrees round the
        // colour wheel at the same lightness, unless the theme names its own.
        var leftBadge = p.TerminalLeftBadge is null ? accent : Parse(p.TerminalLeftBadge, "palette.terminalLeftBadge");
        var rightBadge = p.TerminalRightBadge is null ? accent.RotateHue(RightBadgeHueShift) : Parse(p.TerminalRightBadge, "palette.terminalRightBadge");

        // Text-derived overlays: the design's white at an alpha, here the theme's text at that alpha.
        Argb Text(byte alpha) => text.ScaleAlpha(alpha / 255.0);
        var metrics = MetricsMapper.Map(theme.Metrics);

        // The surfaces under dialogs and notes: the Acrylic tint, opaque, and a step darker: the
        // design's #202020 under #2C2C2C in dark mode, Windows' #F3F3F3 under #FCFCFC in light mode.
        var surface = acrylic.WithAlpha(0xFF);
        var surfaceDark = surface.ScaleRgb(isLight ? 0xF3 / (double)0xFC : 0x20 / (double)0x2C);

        var brushes = new Dictionary<string, Argb>(StringComparer.Ordinal)
        {
            ["CbLayerFillBrush"] = layerFill,
            ["CbLayerStrokeBrush"] = layerStroke,
            ["CbLayerStrokeActiveBrush"] = layerStrokeActive,
            ["CbDividerBrush"] = Text(0x0F),
            ["CbControlFillBrush"] = controlFill,
            ["CbControlStrokeBrush"] = layerStroke,
            ["CbHoverFillBrush"] = controlFill,
            ["CbCrumbHoverFillBrush"] = controlFillHover,
            ["CbPressedFillBrush"] = Text(0x0A),
            ["CbSelectedFillBrush"] = Text(0x14),
            ["CbTextPrimaryBrush"] = text,
            ["CbTextSecondaryBrush"] = secondary,
            ["CbTextTertiaryBrush"] = tertiary,
            ["CbTextDisabledBrush"] = disabled,
            ["CbRowTextBrush"] = Text(0xE6),
            // Dates, types and sizes are textSecondary, dimmed as the design dims row details.
            ["CbRowMetaBrush"] = secondary.ScaleAlpha(0x99 / (double)0xC8),
            ["CbPaneTitleInactiveBrush"] = Text(0xB3),
            ["CbStatusTextBrush"] = secondary.ScaleAlpha(0x99 / (double)0xC8),
            ["CbHintTextBrush"] = tertiary.ScaleAlpha(0x73 / (double)0x8B),
            ["CbKeycapFillBrush"] = Text(0x14),
            ["CbKeycapTextBrush"] = Text(0xE6),
            ["CbBadgeFillBrush"] = Text(0x14),
            ["CbPencilHoverFillBrush"] = Text(0x1F),
            ["CbOverlayStrokeBrush"] = Text(0x1A),
            ["CbPaletteInputFillBrush"] = controlFill,
            ["CbPaletteInputStrokeBrush"] = Text(0x14),
            ["CbTabActiveFillBrush"] = Text(0x14),
            ["CbUsageTrackBrush"] = Text(0x1A),
            ["CbErrorTextBrush"] = levels.High,
            ["CbCursorStrokeBrush"] = Text(0x4D),
            ["CbNoteFillBrush"] = surfaceDark.WithAlpha(0xF2),
            ["CbProgressTrackBrush"] = Text(0x1F),
            ["CbPillFillBrush"] = Text(0x14),
            ["CbPillTrackBrush"] = Text(0x26),
            ["CbCardFillBrush"] = layerFill,
            ["CbMenuHoverFillBrush"] = controlFillHover,
            ["CbPluginBadgeFillBrush"] = accent.WithAlpha(0x26),
            ["CbTerminalFillBrush"] = terminalFill,
            ["CbTerminalStrokeBrush"] = Text(0x14),
            ["CbCloseHoverFillBrush"] = Text(0x1A),
            ["CbDialogFillBrush"] = surface,
            ["CbDialogFooterFillBrush"] = surfaceDark,
            ["CbDialogTextBrush"] = Text(0xCC),
            ["CbAccentBrush"] = accent,
            ["CbTerminalLeftBadgeBrush"] = leftBadge,
            ["CbTerminalRightBadgeBrush"] = rightBadge,
            // Text on accent fills: the design's near-black on its light blue, white on a dark accent.
            ["CbOnAccentBrush"] = OnAccent(accent),
            ["CbFolderBrush"] = folder,
            ["CbFolderFrontBrush"] = folderFront,
            ["CbRunningBrush"] = levels.Low,
            // The design's rating star is its medium level's yellow.
            ["CbRatingStarBrush"] = levels.Medium,
            // The chrome's shades (docs/ui.md, "Metrics and chrome"): the Commander Compact
            // handout's white at .08, .10, .03, .06, .025 and .12, and the function keys' .05, .12, .8.
            ["CbHairlineBrush"] = Text(0x14),
            ["CbHairlineStrongBrush"] = Text(0x1A),
            ["CbBarFillBrush"] = Text(0x08),
            ["CbHeaderActiveFillBrush"] = Text(0x0F),
            ["CbRowStripeBrush"] = Text(0x06),
            ["CbStripedSelectedFillBrush"] = Text(0x1F),
            ["CbFkeyFillBrush"] = Text(0x0D),
            ["CbFkeyHoverFillBrush"] = Text(0x1F),
            ["CbFkeyLabelBrush"] = Text(0xCC),
            // The shell of Phase 16 (the creator's SHELL_REDESIGN.md): the lines under the top row, the
            // tab strips and the breadcrumb rows at the theme's hairlineOpacity; the workspace pill's
            // accent at 18 %; a tab strip's front bar in the inactive pane (30 %) and its tabs behind
            // (65 %); the active pane's breadcrumb row (the accent at 6 %), its separators (35 %), its
            // segments before the last (70 %) and their hover (10 %); the find widget's border (14 %).
            ["CbShellHairlineBrush"] = Text((byte)Math.Round(metrics.HairlineOpacity * 255)),
            ["CbWorkspacePillFillBrush"] = accent.WithAlpha(0x2E),
            ["CbTabFrontBarInactiveBrush"] = Text(0x4D),
            ["CbTabInactiveTextBrush"] = Text(0xA6),
            ["CbCrumbRowActiveFillBrush"] = accent.WithAlpha(0x0F),
            ["CbCrumbSeparatorBrush"] = Text(0x59),
            ["CbCrumbTextBrush"] = Text(0xB3),
            ["CbCrumbSegmentHoverBrush"] = Text(0x1A),
            ["CbFindStrokeBrush"] = Text(0x24),
        };

        // The palette's Acrylic alpha is how much luminosity it lays over the blur (0xB8, the design's .72);
        // menus and the transfer flyout sit a little denser, and the flyout a little darker.
        var flyoutTint = surface.ScaleRgb(isLight ? 0xF9 / (double)0xFC : 0x28 / (double)0x2C);
        var acrylics = new Dictionary<string, AcrylicLook>(StringComparer.Ordinal)
        {
            ["CbPaletteAcrylicBrush"] = new(surface, 0.15, acrylic.Opacity, surface.WithAlpha(0xF2)),
            ["CbMenuAcrylicBrush"] = new(surface, 0.15, Math.Min(1, acrylic.Opacity + 0.06), surface.WithAlpha(0xF5)),
            ["CbFlyoutAcrylicBrush"] = new(flyoutTint, 0.15, Math.Min(1, acrylic.Opacity + 0.10), flyoutTint.WithAlpha(0xF7)),
        };

        var gradients = new Dictionary<string, GradientLook>(StringComparer.Ordinal)
        {
            // A text field's lighter bottom edge over its stroke.
            ["CbFieldStrokeBrush"] = new(Text(0x33), layerStroke),
            ["CbKeycapStrokeBrush"] = new(Text(0x38), Text(0x1A)),
            // The palette's input: an accent bottom edge.
            ["CbPaletteInputAccentBrush"] = new(accent, Text(0x14)),
            // The title bar's app tile: accent to the design's violet.
            ["CbAppTileBrush"] = new(accent, new Argb(0xFF, 0x8A, 0x5C, 0xF6)),
        };

        var fileTypes = new Dictionary<string, Argb>(StringComparer.OrdinalIgnoreCase);
        foreach (var (extension, value) in p.FileTypeColors)
        {
            fileTypes[extension.TrimStart('.')] = Parse(value, $"palette.fileTypeColors.{extension}");
        }

        return new ThemeLook
        {
            Id = theme.Id,
            Name = theme.Name,
            IsLight = isLight,
            FollowsSystemMode = followsMode,
            FollowsSystemAccent = followsSystem,
            Accent = accent,
            AccentShades = shades,
            Mica = theme.Mica is { } mica
                ? new MicaLook(Parse(mica.Tint, "mica.tint").WithAlpha(0xFF), Math.Clamp(mica.Opacity, 0, 1))
                : DenserMica(metrics.BackdropOpacity, isLight),
            Brushes = brushes,
            Acrylics = acrylics,
            Gradients = gradients,
            FileTypes = fileTypes,
            Levels = levels,
            Terminal = Terminal(terminal, accent),
            Metrics = metrics,
            Chrome = ChromeLook.From(theme.Chrome),
        };
    }

    /// <summary>
    /// Plain Mica for a theme without a tint of its own, covering the desktop
    /// as <c>backdropOpacity</c> asks: at the default look's 0.86 (the
    /// design's <c>rgba(32,32,32,.86)</c> stands for plain Mica) and below,
    /// plain Mica (null); above it, Mica's own base colour laid over it,
    /// from the controller's own tint opacity (0.8 dark, 0.5 light) at 0.86
    /// up to opaque at 1. Commander Compact's 0.94 gives 0.91 in dark mode.
    /// </summary>
    public static MicaLook? DenserMica(double backdropOpacity, bool light)
    {
        const double plain = 0.86;
        if (backdropOpacity <= plain)
        {
            return null;
        }
        var (color, micaOwn) = light ? (new Argb(0xFF, 0xF3, 0xF3, 0xF3), 0.5) : (new Argb(0xFF, 0x20, 0x20, 0x20), 0.8);
        var share = (Math.Min(backdropOpacity, 1) - plain) / (1 - plain);
        return new MicaLook(color, Math.Round(micaOwn + ((1 - micaOwn) * share), 3));
    }

    /// <summary>
    /// The text colour on an accent fill: the design's near-black
    /// (<c>#111111</c>) on a light accent such as its <c>#60CDFF</c>, white on
    /// a dark one such as GitHub Light's, whichever contrasts more. A fixed
    /// near-black fell below 4.5:1 on the dark accents of light themes.
    /// </summary>
    public static Argb OnAccent(Argb accent)
    {
        var dark = new Argb(0xFF, 0x11, 0x11, 0x11);
        return accent.ContrastWith(dark) >= accent.ContrastWith(Argb.White) ? dark : Argb.White;
    }

    /// <summary>
    /// The shades WinUI expects around an accent: in dark mode the accent is
    /// its <c>Light2</c> (the design's <c>#60CDFF</c> is the default blue's
    /// Light2), in light mode its <c>Dark1</c>; the others step toward white
    /// and black from there.
    /// </summary>
    public static AccentShades Shades(Argb accent, bool light)
    {
        accent = accent.WithAlpha(0xFF);
        Argb Toward(Argb end, double amount) => accent.Mix(end, amount);
        return light
            ? new AccentShades(
                Base: Toward(Argb.White, 0.15),
                Light1: Toward(Argb.White, 0.35),
                Light2: Toward(Argb.White, 0.55),
                Light3: Toward(Argb.White, 0.75),
                Dark1: accent,
                Dark2: Toward(Argb.Black, 0.2),
                Dark3: Toward(Argb.Black, 0.4))
            : new AccentShades(
                Base: Toward(Argb.Black, 0.35),
                Light1: Toward(Argb.Black, 0.18),
                Light2: accent,
                Light3: Toward(Argb.White, 0.35),
                Dark1: Toward(Argb.Black, 0.5),
                Dark2: Toward(Argb.Black, 0.65),
                Dark3: Toward(Argb.Black, 0.8));
    }

    // The page's cells draw over the translucent panel (palette.terminalBackground), so the
    // scheme's background goes in with alpha 0; xterm.js draws reverse video with its opaque form.
    private static TerminalLook Terminal(ThemeTerminal terminal, Argb accent)
    {
        if (terminal.Ansi.Count != 16)
        {
            throw new ThemeFormatException("terminal.ansi", $"{terminal.Ansi.Count} colours, expected 16");
        }
        return new TerminalLook(
            Parse(terminal.Foreground, "terminal.foreground").ToCss(),
            Parse(terminal.Background, "terminal.background").WithAlpha(0x00).ToCss(),
            Parse(terminal.Cursor, "terminal.cursor").ToCss(),
            accent.WithAlpha(0x4D).ToCss(),
            terminal.Ansi.Select((color, i) => Parse(color, $"terminal.ansi[{i}]").ToCss()).ToList());
    }

    private static Argb Parse(string? value, string key) =>
        Argb.TryParse(value, out var color) ? color : throw new ThemeFormatException(key, value);
}
