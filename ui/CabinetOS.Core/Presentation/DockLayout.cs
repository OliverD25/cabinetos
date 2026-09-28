namespace CabinetOS.Core.Presentation;

/// <summary>Where the Tool Dock goes: under the panes, or beside them.</summary>
public enum DockPlacement
{
    /// <summary>Under the panes (<c>ui.layout</c> <c>classic</c> and <c>rail</c>).</summary>
    Bottom,

    /// <summary>Right of the panes (<c>ui.layout</c> <c>right</c>).</summary>
    Right,
}

/// <summary>
/// The Tool Dock's size (docs/design/README.md, "Integrated terminal"): by
/// default <c>clamp(120px, 30%, 240px)</c> of the main column's height at the
/// bottom and <c>clamp(220px, 32%, 380px)</c> of its width on the right. The
/// splitter moves it within limits that leave the panes room.
/// </summary>
public static class DockLayout
{
    /// <summary>The gap between the panes and the dock, which is also the splitter.</summary>
    public const double Gap = 8;

    /// <summary>The placement for a <c>ui.layout</c> value.</summary>
    public static DockPlacement PlacementFor(string? layout) =>
        layout == "right" ? DockPlacement.Right : DockPlacement.Bottom;

    /// <summary>The design's size in a main column <paramref name="available"/> pixels high (bottom) or wide (right).</summary>
    public static double DefaultSize(DockPlacement placement, double available) => placement == DockPlacement.Bottom
        ? Math.Clamp(available * 0.30, 120, 240)
        : Math.Clamp(available * 0.32, 220, 380);

    /// <summary>
    /// A size the user dragged to: at least the design's minimum, and at most
    /// what leaves the panes 160 px of height (bottom) or 320 px of width (right).
    /// </summary>
    public static double Clamp(DockPlacement placement, double size, double available)
    {
        var (minimum, panes) = placement == DockPlacement.Bottom ? (120.0, 160.0) : (220.0, 320.0);
        var maximum = Math.Max(minimum, available - panes - Gap);
        return Math.Clamp(size, minimum, maximum);
    }
}
