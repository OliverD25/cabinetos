using System.Collections.Frozen;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Themes;

/// <summary>How a metric is written (docs/themes.md, "Metrics").</summary>
public enum MetricUnit
{
    /// <summary>Whole device-independent pixels.</summary>
    Pixels,

    /// <summary>A number that may have a fraction: a line height, an opacity, a column's weight.</summary>
    Number,

    /// <summary>A share of the window's width (the sidebar) or height (the bottom terminal dock).</summary>
    Percent,
}

/// <summary>One named size of the theme format: its unit, its bounds, and the default look's value.</summary>
public sealed record MetricSpec(string Name, MetricUnit Unit, double Min, double Max, double Default);

/// <summary>
/// Which of the three chrome elements the window shows (docs/themes.md,
/// "Chrome"): the function-key bar, striped file lists, and hairline
/// separators instead of floating cards.
/// </summary>
public sealed record ChromeLook(bool FkeyBar, bool RowStripes, bool Hairlines)
{
    /// <summary>The default look: none of them.</summary>
    public static readonly ChromeLook None = new(false, false, false);

    /// <summary>The theme's <c>chrome</c>; each key left out is off.</summary>
    public static ChromeLook From(ThemeChrome? chrome) =>
        chrome is null ? None : new ChromeLook(chrome.FkeyBar == true, chrome.RowStripes == true, chrome.Hairlines == true);
}

/// <summary>
/// Every size the window lays out with, one per metric name of the theme
/// format: the theme's value, or the default look's where the theme leaves
/// it out (docs/ui.md, "Metrics and chrome"). Two are equal when every
/// value is, so the window lays itself out again only when a size changed.
/// </summary>
public sealed class ThemeMetrics : IEquatable<ThemeMetrics>
{
    private readonly double[] _values;
    private readonly bool[] _set;

    internal ThemeMetrics(double[] values, bool[] set, IReadOnlyList<string> ignored)
    {
        _values = values;
        _set = set;
        Ignored = ignored;
    }

    /// <summary>Names in the theme's <c>metrics</c> that are not metrics (a newer format's); they change nothing.</summary>
    public IReadOnlyList<string> Ignored { get; }

    /// <summary>The value of the metric <paramref name="name"/>, such as <c>rowHeight</c>.</summary>
    public double this[string name] => _values[MetricsMapper.IndexOf(name)];

    /// <summary>Whether the theme set <paramref name="name"/> itself.</summary>
    public bool IsSet(string name) => _set[MetricsMapper.IndexOf(name)];

    /// <summary>Whether the theme set any metric: a density preset.</summary>
    public bool IsPreset => Array.IndexOf(_set, true) >= 0;

    // ----- Global -----
    public double FontSize => this["fontSize"];
    public double LineHeight => this["lineHeight"];
    public double BackdropOpacity => this["backdropOpacity"];
    public double RadiusControl => this["radiusControl"];
    public double RadiusSurface => this["radiusSurface"];
    public double Gap => this["gap"];
    public double BodyPadding => this["bodyPadding"];

    // ----- Title bar -----
    public double TitleBarHeight => this["titleBarHeight"];
    public double TabHeight => this["tabHeight"];
    public double TabPaddingX => this["tabPaddingX"];
    public double TabMinWidth => this["tabMinWidth"];
    public double TabFontSize => this["tabFontSize"];
    public double TabRadius => this["tabRadius"];
    public double CaptionButtonWidth => this["captionButtonWidth"];

    // ----- Command bar -----
    public double CommandBarHeight => this["commandBarHeight"];
    public double IconButtonSize => this["iconButtonSize"];
    public double FieldHeight => this["fieldHeight"];
    public double ToggleHeight => this["toggleHeight"];

    // ----- Sidebar -----
    public double SidebarMinWidth => this["sidebarMinWidth"];
    public double SidebarWidthPercent => this["sidebarWidthPercent"];
    public double SidebarMaxWidth => this["sidebarMaxWidth"];
    public double SidebarHeaderFontSize => this["sidebarHeaderFontSize"];
    public double SidebarHeaderPaddingTop => this["sidebarHeaderPaddingTop"];
    public double SidebarHeaderPaddingX => this["sidebarHeaderPaddingX"];
    public double SidebarHeaderPaddingBottom => this["sidebarHeaderPaddingBottom"];
    public double SidebarRowHeight => this["sidebarRowHeight"];
    public double SidebarRowInset => this["sidebarRowInset"];
    public double SidebarRowRadius => this["sidebarRowRadius"];
    public double SelectionBarWidth => this["selectionBarWidth"];
    public double DriveRowPaddingY => this["driveRowPaddingY"];
    public double DriveRowPaddingX => this["driveRowPaddingX"];
    public double TagRadius => this["tagRadius"];
    public double TagFontSize => this["tagFontSize"];

    // ----- Panes -----
    public double PaneHeaderHeight => this["paneHeaderHeight"];
    public double TabRow => this["tabRow"];
    public double ColumnHeaderPaddingY => this["columnHeaderPaddingY"];
    public double ColumnHeaderPaddingX => this["columnHeaderPaddingX"];
    public double NameColumnWeight => this["nameColumnWeight"];
    public double ModifiedColumnWeight => this["modifiedColumnWeight"];
    public double TypeColumnWeight => this["typeColumnWeight"];
    public double NameColumnMinWidth => this["nameColumnMinWidth"];
    public double SizeColumnWidth => this["sizeColumnWidth"];
    public double ColumnGap => this["columnGap"];
    public double RowHeight => this["rowHeight"];
    public double RowPaddingX => this["rowPaddingX"];
    public double RowRadius => this["rowRadius"];
    public double RowIconGap => this["rowIconGap"];
    public double SecondaryFontSize => this["secondaryFontSize"];

    // ----- Editors -----
    public double EditorTabHeight => this["editorTabHeight"];
    public double MarkdownPaddingY => this["markdownPaddingY"];
    public double MarkdownPaddingX => this["markdownPaddingX"];
    public double MarkdownLineHeight => this["markdownLineHeight"];
    public double HexRowHeight => this["hexRowHeight"];
    public double HexColumnGap => this["hexColumnGap"];

    // ----- Terminal -----
    public double TerminalDockMinHeight => this["terminalDockMinHeight"];
    public double TerminalDockHeightPercent => this["terminalDockHeightPercent"];
    public double TerminalDockMaxHeight => this["terminalDockMaxHeight"];
    public double TerminalHeaderHeight => this["terminalHeaderHeight"];
    public double TerminalTabHeight => this["terminalTabHeight"];
    public double TerminalPaddingY => this["terminalPaddingY"];
    public double TerminalPaddingX => this["terminalPaddingX"];
    public double TerminalLineHeight => this["terminalLineHeight"];

    // ----- Marketplace -----
    public double MarketplaceTabHeight => this["marketplaceTabHeight"];
    public double MarketplaceTabRadius => this["marketplaceTabRadius"];
    public double MarketplaceCardGap => this["marketplaceCardGap"];
    public double MarketplaceCardPaddingY => this["marketplaceCardPaddingY"];
    public double MarketplaceCardPaddingX => this["marketplaceCardPaddingX"];
    public double MarketplaceCardRadius => this["marketplaceCardRadius"];

    // ----- Overlays -----
    public double PaletteRowHeight => this["paletteRowHeight"];
    public double MenuRowHeight => this["menuRowHeight"];

    // ----- Status bar -----
    public double StatusBarHeight => this["statusBarHeight"];
    public double StatusBarPaddingX => this["statusBarPaddingX"];
    public double StatusBarGap => this["statusBarGap"];

    // ----- Function-key bar -----
    public double FkeyBarHeight => this["fkeyBarHeight"];
    public double FkeyBarGap => this["fkeyBarGap"];
    public double FkeyButtonRadius => this["fkeyButtonRadius"];
    public double FkeyBarFontSize => this["fkeyBarFontSize"];

    /// <summary>
    /// A pinned folder's row in the sidebar: <c>sidebarRowHeight</c> when the
    /// theme sets it, else the default look's 32 (its 34 is the workspace rows').
    /// </summary>
    public double PinnedRowHeight() => IsSet("sidebarRowHeight") ? SidebarRowHeight : 32;

    /// <summary>The sidebar's width in a window <paramref name="windowWidth"/> wide: its share, kept between its narrowest and widest.</summary>
    public double SidebarWidth(double windowWidth) =>
        Math.Clamp(windowWidth * SidebarWidthPercent / 100, SidebarMinWidth, Math.Max(SidebarMinWidth, SidebarMaxWidth));

    /// <summary>The bottom terminal dock's height over a main column <paramref name="available"/> high: its share, kept between its lowest and highest.</summary>
    public double BottomDockHeight(double available) =>
        Math.Clamp(available * TerminalDockHeightPercent / 100, TerminalDockMinHeight, Math.Max(TerminalDockMinHeight, TerminalDockMaxHeight));

    /// <inheritdoc/>
    public bool Equals(ThemeMetrics? other) => other is not null && _values.AsSpan().SequenceEqual(other._values) && _set.AsSpan().SequenceEqual(other._set);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ThemeMetrics);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in _values)
        {
            hash.Add(value);
        }
        return hash.ToHashCode();
    }
}

/// <summary>
/// Turns a theme's <c>metrics</c> into every size the window lays out with
/// (docs/ui.md, "Metrics and chrome"). One table names each metric of the
/// theme format, in the handout's order, with its unit, its bounds and the
/// default look's value (docs/themes.md, "Metrics"); a test holds it equal
/// to the theme schema. A metric the theme leaves out gets the default
/// look's value. The core refuses a theme with a value outside the bounds;
/// the window holds values to them as well, so no size can break its
/// layout, and rounds pixels to whole ones. Runs without a window, so tested.
/// </summary>
public static class MetricsMapper
{
    /// <summary>Every metric, in the handout's order.</summary>
    public static IReadOnlyList<MetricSpec> Specs { get; } =
    [
        // Global
        Px("fontSize", 8, 32, 13),
        Num("lineHeight", 1, 2.5, 1.4),
        Num("backdropOpacity", 0, 1, 0.86),
        Px("radiusControl", 0, 16, 4),
        Px("radiusSurface", 0, 16, 8),
        Px("gap", 0, 48, 8),
        Px("bodyPadding", 0, 64, 8),
        // Title bar
        Px("titleBarHeight", 14, 80, 40),
        Px("tabHeight", 14, 80, 32),
        Px("tabPaddingX", 0, 64, 14),
        Px("tabMinWidth", 40, 240, 96),
        Px("tabFontSize", 8, 32, 12),
        Px("tabRadius", 0, 16, 8),
        Px("captionButtonWidth", 24, 96, 46),
        // Command bar
        Px("commandBarHeight", 14, 80, 48),
        Px("iconButtonSize", 14, 80, 32),
        Px("fieldHeight", 14, 80, 32),
        Px("toggleHeight", 14, 80, 32),
        // Sidebar
        Px("sidebarMinWidth", 100, 600, 180),
        Pct("sidebarWidthPercent", 5, 50, 20),
        Px("sidebarMaxWidth", 100, 600, 224),
        Px("sidebarHeaderFontSize", 8, 32, 11),
        Px("sidebarHeaderPaddingTop", 0, 64, 14),
        Px("sidebarHeaderPaddingX", 0, 64, 12),
        Px("sidebarHeaderPaddingBottom", 0, 64, 4),
        Px("sidebarRowHeight", 14, 80, 34),
        Px("sidebarRowInset", 0, 16, 4),
        Px("sidebarRowRadius", 0, 16, 4),
        Px("selectionBarWidth", 0, 8, 3),
        Px("driveRowPaddingY", 0, 64, 6),
        Px("driveRowPaddingX", 0, 64, 10),
        Px("tagRadius", 0, 16, 12),
        Px("tagFontSize", 8, 32, 12),
        // Panes
        Px("paneHeaderHeight", 14, 80, 36),
        Px("tabRow", 14, 80, 32),
        Px("columnHeaderPaddingY", 0, 64, 4),
        Px("columnHeaderPaddingX", 0, 64, 14),
        Num("nameColumnWeight", 0.1, 10, 1),
        Num("modifiedColumnWeight", 0.1, 10, 0.55),
        Num("typeColumnWeight", 0.1, 10, 0.45),
        Px("nameColumnMinWidth", 0, 400, 120),
        Px("sizeColumnWidth", 32, 160, 64),
        Px("columnGap", 0, 48, 0),
        Px("rowHeight", 14, 80, 30),
        Px("rowPaddingX", 0, 64, 10),
        Px("rowRadius", 0, 16, 4),
        Px("rowIconGap", 0, 48, 10),
        Px("secondaryFontSize", 8, 32, 12),
        // Editors
        Px("editorTabHeight", 14, 80, 36),
        Px("markdownPaddingY", 0, 64, 28),
        Px("markdownPaddingX", 0, 64, 40),
        Num("markdownLineHeight", 1, 2.5, 1.6),
        Px("hexRowHeight", 14, 80, 22),
        Px("hexColumnGap", 0, 48, 18),
        // Terminal
        Px("terminalDockMinHeight", 60, 800, 120),
        Pct("terminalDockHeightPercent", 10, 80, 30),
        Px("terminalDockMaxHeight", 60, 800, 240),
        Px("terminalHeaderHeight", 14, 80, 34),
        Px("terminalTabHeight", 14, 80, 26),
        Px("terminalPaddingY", 0, 64, 10),
        Px("terminalPaddingX", 0, 64, 12),
        Num("terminalLineHeight", 1, 2.5, 1.6),
        // Marketplace
        Px("marketplaceTabHeight", 14, 80, 32),
        Px("marketplaceTabRadius", 0, 16, 4),
        Px("marketplaceCardGap", 0, 48, 10),
        Px("marketplaceCardPaddingY", 0, 64, 14),
        Px("marketplaceCardPaddingX", 0, 64, 14),
        Px("marketplaceCardRadius", 0, 16, 8),
        // Overlays
        Px("paletteRowHeight", 14, 80, 36),
        Px("menuRowHeight", 14, 80, 32),
        // Status bar
        Px("statusBarHeight", 14, 80, 26),
        Px("statusBarPaddingX", 0, 64, 14),
        Px("statusBarGap", 0, 48, 16),
        // Function-key bar: the default look has none; these are the handout's sizes for any theme that shows it.
        Px("fkeyBarHeight", 14, 80, 24),
        Px("fkeyBarGap", 0, 48, 1),
        Px("fkeyButtonRadius", 0, 16, 2),
        Px("fkeyBarFontSize", 8, 32, 11),
    ];

    private static readonly FrozenDictionary<string, int> Indexes =
        Specs.Select((spec, i) => (spec.Name, i)).ToFrozenDictionary(p => p.Name, p => p.i, StringComparer.Ordinal);

    /// <summary>The default look: every metric at its default.</summary>
    public static ThemeMetrics Default { get; } = Map(null);

    /// <summary>
    /// Maps a theme's <c>metrics</c> (null: the theme has none). A name that
    /// is not a metric is left out and listed in <see cref="ThemeMetrics.Ignored"/>.
    /// </summary>
    public static ThemeMetrics Map(IReadOnlyDictionary<string, double>? metrics)
    {
        var values = new double[Specs.Count];
        var set = new bool[Specs.Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = Specs[i].Default;
        }
        var ignored = new List<string>();
        foreach (var (name, value) in metrics ?? new Dictionary<string, double>())
        {
            if (!Indexes.TryGetValue(name, out var i) || !double.IsFinite(value))
            {
                ignored.Add(name);
                continue;
            }
            var spec = Specs[i];
            var held = Math.Clamp(value, spec.Min, spec.Max);
            values[i] = spec.Unit == MetricUnit.Pixels ? Math.Round(held, MidpointRounding.AwayFromZero) : held;
            set[i] = true;
        }
        return new ThemeMetrics(values, set, ignored);
    }

    internal static int IndexOf(string name) =>
        Indexes.TryGetValue(name, out var i) ? i : throw new ArgumentException($"{name} is not a metric", nameof(name));

    private static MetricSpec Px(string name, double min, double max, double value) => new(name, MetricUnit.Pixels, min, max, value);

    private static MetricSpec Num(string name, double min, double max, double value) => new(name, MetricUnit.Number, min, max, value);

    private static MetricSpec Pct(string name, double min, double max, double value) => new(name, MetricUnit.Percent, min, max, value);
}
