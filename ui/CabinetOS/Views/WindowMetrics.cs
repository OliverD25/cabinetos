using CabinetOS.Core.Presentation;
using CabinetOS.Core.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CabinetOS.Views;

/// <summary>
/// The sizes and chrome elements the window lays out with now (docs/ui.md,
/// "Metrics and chrome"): the theme in effect's, which the window takes when
/// it applies a theme. A view reads them when it builds an element, and the
/// window has every view lay itself out again when they change; a row the
/// repeater recycles compares <see cref="Version"/> when it is bound again.
/// </summary>
internal static class WindowMetrics
{
    /// <summary>The Size column's figures under hairlines: the handout's Fira Code where installed, else Windows' own fixed-width font.</summary>
    public const string FiguresFontFamily = "Fira Code, Cascadia Mono, Consolas";

    /// <summary>The sizes in effect.</summary>
    public static ThemeMetrics Current { get; private set; } = MetricsMapper.Default;

    /// <summary>The chrome elements in effect.</summary>
    public static ChromeLook Chrome { get; private set; } = ChromeLook.None;

    /// <summary>Counts the changes, so an element can tell it was built with older sizes.</summary>
    public static int Version { get; private set; }

    /// <summary>Takes a theme's sizes and chrome; false when nothing changed (the window then lays out nothing again).</summary>
    public static bool Take(ThemeMetrics metrics, ChromeLook chrome)
    {
        if (metrics.Equals(Current) && chrome == Chrome)
        {
            return false;
        }
        Current = metrics;
        Chrome = chrome;
        Version++;
        _columns = null;
        return true;
    }

    /// <summary>The widths the user gave Modified, Type and Size (<c>ui.columns</c>), shared by both panes; null for the theme's.</summary>
    public static ColumnWidths? UserColumns { get; private set; }

    /// <summary>Counts the changes of <see cref="UserColumns"/>, so a row the repeater recycles can tell its columns are older.</summary>
    public static int ColumnsVersion { get; private set; }

    /// <summary>The file lists' column widths now: the theme's sizes with the user's widths.</summary>
    public static ColumnLayout Columns => _columns ??= new ColumnLayout(Current, UserColumns);

    /// <summary>Takes the user's column widths (null: the theme's); false when they are the ones in effect.</summary>
    public static bool TakeColumns(ColumnWidths? widths)
    {
        if (widths == UserColumns)
        {
            return false;
        }
        UserColumns = widths;
        ColumnsVersion++;
        _columns = null;
        return true;
    }

    /// <summary>The same radius on all four corners.</summary>
    public static CornerRadius Corners(double radius) => new(radius);

    /// <summary>Only the top two corners rounded (a tab).</summary>
    public static CornerRadius TopCorners(double radius) => new(radius, radius, 0, 0);

    /// <summary>
    /// A radius the design gives something that is neither a control nor a
    /// surface (an icon tile, an info box): it follows <c>radiusSurface</c>
    /// in proportion (the design's own at the default look's 8 px) and is
    /// never below <c>radiusControl</c>. Commander Compact's square surfaces
    /// make these the controls' 2 px.
    /// </summary>
    public static CornerRadius Inner(double design) => new(Math.Max(Current.RadiusControl, design * Current.RadiusSurface / 8));

    /// <summary><paramref name="x"/> at the sides, <paramref name="y"/> above and below.</summary>
    public static Thickness Pad(double x, double y = 0) => new(x, y, x, y);

    /// <summary>
    /// A file list's four columns, the column headers' and every row's alike
    /// (docs/ui.md, "Column widths"): the theme's weights of Name, Modified and
    /// Type and Size's width, or the user's widths of Modified, Type and Size
    /// with Name taking the rest; and the narrowest Name.
    /// </summary>
    public static void SetColumns(ColumnDefinition name, ColumnDefinition modified, ColumnDefinition type, ColumnDefinition size)
    {
        var sizes = Columns.GridSizes();
        name.Width = Length(sizes.Name);
        name.MinWidth = sizes.NameMin;
        modified.Width = Length(sizes.Modified);
        type.Width = Length(sizes.Type);
        size.Width = Length(sizes.Size);

        static GridLength Length(ColumnSize column) => new(column.Value, column.IsStar ? GridUnitType.Star : GridUnitType.Pixel);
    }

    private static ColumnLayout? _columns;

    /// <summary>
    /// The space after the Modified and Type texts: the default look keeps 8 px
    /// there; a theme that sets <c>columnGap</c> spaces the columns by it instead.
    /// </summary>
    public static Thickness TextGap => new(0, 0, Current.ColumnGap > 0 ? 0 : 8, 0);

    /// <summary>
    /// The top padding that centres one line of text at <paramref name="fontSize"/>
    /// in a text box <paramref name="height"/> high with a 1 px border: 3 for the
    /// default look's 13 px text in 26 px, 0 when the box is as low as the line.
    /// </summary>
    public static double TextTop(double height, double fontSize) => Math.Max(0, Math.Floor((height - 2 - Math.Ceiling(fontSize * 1.33)) / 2));

    /// <summary>The Size column's font: fixed-width figures under hairlines, else null for the window's own (the default look).</summary>
    public static FontFamily? FiguresFont => Chrome.Hairlines ? _figuresFont ??= new FontFamily(FiguresFontFamily) : null;

    private static FontFamily? _figuresFont;
}
