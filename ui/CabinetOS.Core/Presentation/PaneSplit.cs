using System.Text.Json;
using CabinetOS.Core.Themes;

namespace CabinetOS.Core.Presentation;

/// <summary>
/// The divider between the two file panes (docs/ui.md, "The divider between the
/// panes"). The left pane's share of the width the two panes have is
/// <c>ui.paneSplit</c>: a number from 0.2 to 0.8, or null for equal panes. It is
/// a share, not pixels, so a resize of the window, the sidebar or the dock keeps
/// the proportion. Neither pane gets narrower than its columns need, in a space
/// wide enough for two panes of that width. Runs without a window, so tested.
/// </summary>
public static class PaneSplit
{
    /// <summary>The share of equal panes, and what null in the setting means.</summary>
    public const double Equal = 0.5;

    /// <summary>The least the left pane's share may be (the core refuses less).</summary>
    public const double MinShare = 0.2;

    /// <summary>The most the left pane's share may be (the core refuses more).</summary>
    public const double MaxShare = 0.8;

    /// <summary>
    /// The narrowest a pane gets from the divider: Name's minimum, the other three
    /// columns at their narrowest, the gaps between the four and the header's padding
    /// (docs/ui.md, "Column widths").
    /// </summary>
    public static double MinPaneWidth(ThemeMetrics metrics) =>
        metrics.NameColumnMinWidth + (3 * ColumnLayout.MinWidth) + (3 * metrics.ColumnGap) + (2 * metrics.ColumnHeaderPaddingX);

    /// <summary>
    /// The shares the divider may take in a space <paramref name="available"/> pixels
    /// wide: 0.2 to 0.8, narrowed so that neither pane is under <paramref name="minPane"/>.
    /// A space too narrow for two panes of that width leaves only the equal split.
    /// </summary>
    public static (double Min, double Max) Range(double available, double minPane)
    {
        if (available <= 0)
        {
            return (Equal, Equal);
        }
        var floor = Math.Max(MinShare, minPane / available);
        return floor >= Equal ? (Equal, Equal) : (floor, 1 - floor);
    }

    /// <summary><paramref name="share"/> kept within <see cref="Range"/>.</summary>
    public static double Clamp(double share, double available, double minPane)
    {
        var (min, max) = Range(available, minPane);
        return Math.Clamp(share, min, max);
    }

    /// <summary>The share to lay out: the setting kept within the range, or equal when there is none.</summary>
    public static double Resolve(double? setting, double available, double minPane) =>
        setting is { } share ? Clamp(share, available, minPane) : Equal;

    /// <summary>
    /// Where a drag leaves the divider: the left pane was <paramref name="startLeftWidth"/>
    /// wide when it started and the pointer moved <paramref name="dx"/> pixels (right is positive).
    /// </summary>
    public static double Drag(double startLeftWidth, double dx, double available, double minPane) =>
        available <= 0 ? Equal : Clamp((startLeftWidth + dx) / available, available, minPane);

    /// <summary>The value <c>ui.paneSplit</c> takes for a share: three decimals, from 0.2 to 0.8.</summary>
    public static double ToSetting(double share) => Math.Round(Math.Clamp(share, MinShare, MaxShare), 3);

    /// <summary><c>ui.paneSplit</c> from the core's configuration; null for equal, or for a value out of range or not a number.</summary>
    public static double? FromConfig(JsonElement config) =>
        config.ValueKind == JsonValueKind.Object
        && config.TryGetProperty("ui", out var ui) && ui.ValueKind == JsonValueKind.Object
        && ui.TryGetProperty("paneSplit", out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var share) && share is >= MinShare and <= MaxShare
            ? share
            : null;
}
