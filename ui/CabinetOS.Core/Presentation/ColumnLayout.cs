using System.Buffers;
using System.Text.Json;
using CabinetOS.Core.Themes;

namespace CabinetOS.Core.Presentation;

/// <summary>A file list's columns, left to right.</summary>
public enum ListColumn
{
    /// <summary>The name, with the icon; it takes what the others leave.</summary>
    Name,

    /// <summary>The time modified (a search hit's folder).</summary>
    Modified,

    /// <summary>The type.</summary>
    Type,

    /// <summary>The size.</summary>
    Size,
}

/// <summary>The three dividers of a file list's column headers, each with a grip.</summary>
public enum ColumnDivider
{
    /// <summary>Between Name and Modified.</summary>
    NameModified = 1,

    /// <summary>Between Modified and Type.</summary>
    ModifiedType = 2,

    /// <summary>Between Type and Size.</summary>
    TypeSize = 3,
}

/// <summary>
/// The widths the user gave Modified, Type and Size (<c>ui.columns</c>), in
/// pixels. Both panes share them; the Name column takes the rest.
/// </summary>
public sealed record ColumnWidths(double Modified, double Type, double Size);

/// <summary>The four columns' widths in pixels, as a list of some width lays them out.</summary>
public readonly record struct ColumnSplit(double Name, double Modified, double Type, double Size);

/// <summary>How a grid sizes one column: a share of the space left (star), or pixels.</summary>
public readonly record struct ColumnSize(double Value, bool IsStar);

/// <summary>The four column definitions a file list's grids take, with the narrowest Name.</summary>
public readonly record struct ColumnGridSizes(ColumnSize Name, double NameMin, ColumnSize Modified, ColumnSize Type, ColumnSize Size);

/// <summary>
/// What a fit measured for one column among the rows on screen: its widest
/// text, the room its cell keeps beside a text (the space after the Modified
/// and Type texts), and its heading's text with the sort chevron's room.
/// </summary>
public readonly record struct ColumnMeasure(double WidestText, double CellExtra, double Heading)
{
    /// <summary>The width that shows all of it, in whole pixels, never under <see cref="ColumnLayout.MinWidth"/>.</summary>
    public double Width => Math.Clamp(Math.Ceiling(Math.Max(WidestText + CellExtra, Heading) - 0.01), ColumnLayout.MinWidth, ColumnLayout.MaxWidth);
}

/// <summary>
/// A file list's column widths (docs/ui.md, "Column widths"). With nothing
/// set by the user, the theme's weights and Size width lay the columns out
/// exactly as before: the grids keep the star weights, and
/// <see cref="Resolve"/> gives the same split in numbers. Once the user drags
/// a grip or fits a column, Modified, Type and Size have pixel widths that win
/// over every theme's weights, and Name takes the rest, never less than the
/// theme's <c>nameColumnMinWidth</c>; the theme's <c>columnGap</c> still
/// spaces them. Runs without a window, so tested.
/// </summary>
public sealed class ColumnLayout(ThemeMetrics metrics, ColumnWidths? user)
{
    /// <summary>The narrowest a drag or a fit leaves Modified, Type or Size.</summary>
    public const double MinWidth = 40;

    /// <summary>The widest the core stores a column (<c>ui.columns</c>).</summary>
    public const double MaxWidth = 2000;

    /// <summary>The theme's sizes.</summary>
    public ThemeMetrics Metrics { get; } = metrics;

    /// <summary>The user's widths, or null for the theme's.</summary>
    public ColumnWidths? User { get; } = user;

    /// <summary>The space between two columns: the theme's <c>columnGap</c>.</summary>
    public double Gap => Metrics.ColumnGap;

    /// <summary>The narrowest Name: the theme's <c>nameColumnMinWidth</c>.</summary>
    public double NameMin => Metrics.NameColumnMinWidth;

    /// <summary>The column definitions for the header's and every row's grid: the theme's star weights, or the user's pixels with Name taking the rest.</summary>
    public ColumnGridSizes GridSizes() => User is { } u
        ? new(new(1, true), NameMin, new(u.Modified, false), new(u.Type, false), new(u.Size, false))
        : new(new(Metrics.NameColumnWeight, true), NameMin, new(Metrics.ModifiedColumnWeight, true), new(Metrics.TypeColumnWeight, true),
            new(Metrics.SizeColumnWidth, false));

    /// <summary>
    /// The four widths in a list whose columns and gaps share
    /// <paramref name="available"/> pixels (the grid's width inside its
    /// padding). The user's widths as they are, Name the rest but never under
    /// its minimum; else the theme's star split as the grid makes it: the
    /// weights share what the Size column and the gaps leave, and Name, when
    /// its share is under its minimum, takes the minimum and the others share
    /// the rest.
    /// </summary>
    public ColumnSplit Resolve(double available)
    {
        var gaps = 3 * Gap;
        if (User is { } u)
        {
            return new(Math.Max(NameMin, available - gaps - u.Modified - u.Type - u.Size), u.Modified, u.Type, u.Size);
        }
        var size = Metrics.SizeColumnWidth;
        var space = Math.Max(0, available - gaps - size);
        var (name, modified, type) = (Metrics.NameColumnWeight, Metrics.ModifiedColumnWeight, Metrics.TypeColumnWeight);
        var nameWidth = space * name / (name + modified + type);
        if (nameWidth >= NameMin)
        {
            return new(nameWidth, space * modified / (name + modified + type), space * type / (name + modified + type), size);
        }
        var rest = Math.Max(0, space - NameMin);
        return new(NameMin, rest * modified / (modified + type), rest * type / (modified + type), size);
    }

    /// <summary>
    /// Where a drag of <paramref name="divider"/>'s grip by <paramref name="dx"/>
    /// pixels (right is positive) leaves the three widths, from the split the
    /// drag started with. The divider follows the pointer and only the two
    /// columns beside it change: Name and Modified, Modified and Type, or Type
    /// and Size. A column keeps at least <see cref="MinWidth"/> (or what it had,
    /// when it had less), and Name at least its minimum, so a drag that would
    /// cut Name under it stops there. Whole pixels, as the setting keeps them.
    /// </summary>
    public ColumnWidths Drag(ColumnDivider divider, ColumnSplit start, double dx)
    {
        var (modified, type, size) = (Math.Round(start.Modified), Math.Round(start.Type), Math.Round(start.Size));
        var move = Math.Round(dx);
        switch (divider)
        {
            case ColumnDivider.NameModified:
                // Right: Name grows and Modified shrinks; left: the other way, as far as Name's minimum.
                var nameRoom = Math.Max(0, start.Name - NameMin);
                move = Math.Clamp(move, -Math.Floor(nameRoom), modified - Floor(modified));
                modified -= move;
                break;
            case ColumnDivider.ModifiedType:
                move = Math.Clamp(move, Floor(modified) - modified, type - Floor(type));
                modified += move;
                type -= move;
                break;
            case ColumnDivider.TypeSize:
                move = Math.Clamp(move, Floor(type) - type, size - Floor(size));
                type += move;
                size -= move;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(divider), divider, "a divider is 1 to 3");
        }
        return new ColumnWidths(modified, type, size);

        // A column narrower than the minimum already keeps what it has: a drag never makes it jump.
        static double Floor(double width) => Math.Min(MinWidth, width);
    }

    /// <summary>
    /// Fits <paramref name="column"/> to <paramref name="measured"/> (a double-click
    /// on its heading or on the grip at its right); the Name column fits Modified,
    /// Type and Size together, so Name gets the most room. The other columns keep
    /// the widths of <paramref name="current"/>. A fit never leaves Name under its
    /// minimum in a list <paramref name="available"/> pixels wide: a column that
    /// would, gets what leaves Name its minimum, and never less than <see cref="MinWidth"/>.
    /// </summary>
    public ColumnWidths Fit(ColumnSplit current, double available, ListColumn column, ColumnMeasure measured)
    {
        var (modified, type, size) = (Math.Round(current.Modified), Math.Round(current.Type), Math.Round(current.Size));
        var room = available - (3 * Gap) - NameMin;
        var others = column switch
        {
            ListColumn.Modified => type + size,
            ListColumn.Type => modified + size,
            ListColumn.Size => modified + type,
            _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Name fits all three: FitAll"),
        };
        var width = Math.Max(MinWidth, Math.Min(measured.Width, Math.Floor(room - others)));
        return column switch
        {
            ListColumn.Modified => new ColumnWidths(width, type, size),
            ListColumn.Type => new ColumnWidths(modified, width, size),
            _ => new ColumnWidths(modified, type, width),
        };
    }

    /// <summary>
    /// Fits Modified, Type and Size together (the Name heading's double-click,
    /// <c>view.fitColumns</c>). When the three do not leave Name its minimum in
    /// a list <paramref name="available"/> pixels wide, each gives up the same
    /// share of its width, down to <see cref="MinWidth"/>.
    /// </summary>
    public ColumnWidths FitAll(double available, ColumnMeasure modified, ColumnMeasure type, ColumnMeasure size)
    {
        double[] widths = [modified.Width, type.Width, size.Width];
        var room = Math.Max(3 * MinWidth, available - (3 * Gap) - NameMin);
        var total = widths.Sum();
        if (total > room)
        {
            var scale = (room - (3 * MinWidth)) / (total - (3 * MinWidth));
            for (var i = 0; i < widths.Length; i++)
            {
                widths[i] = Math.Floor(MinWidth + ((widths[i] - MinWidth) * scale));
            }
        }
        return new ColumnWidths(widths[0], widths[1], widths[2]);
    }

    /// <summary>The user's widths as <c>ui.columns</c> takes them: whole pixels, or null for the theme's.</summary>
    public JsonElement ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (User is { } u)
            {
                writer.WriteStartObject();
                writer.WriteNumber("modified", ToSetting(u.Modified));
                writer.WriteNumber("type", ToSetting(u.Type));
                writer.WriteNumber("size", ToSetting(u.Size));
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNullValue();
            }
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The user's widths from the core's <c>config</c> reply (<c>ui.columns</c>),
    /// or null: absent, <c>null</c>, or not three numbers (the core refuses
    /// anything else, so a window never meets it).
    /// </summary>
    public static ColumnWidths? FromConfig(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("ui", out var ui) || ui.ValueKind != JsonValueKind.Object
            || !ui.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return Pixels(columns, "modified") is { } modified && Pixels(columns, "type") is { } type && Pixels(columns, "size") is { } size
            ? new ColumnWidths(modified, type, size)
            : null;

        static double? Pixels(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var pixels) && pixels > 0
                ? pixels
                : null;
    }

    // Whole pixels within what the core accepts.
    private static uint ToSetting(double width) => (uint)Math.Clamp(Math.Round(width), 24, MaxWidth);
}
