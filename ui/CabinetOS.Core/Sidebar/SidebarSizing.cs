namespace CabinetOS.Core.Sidebar;

/// <summary>What a drag of the sidebar's divider decides: the sidebar snaps shut, or it takes this width.</summary>
public readonly record struct SidebarDrag(bool Close, double Width);

/// <summary>
/// The sidebar's width in the rail layout (docs/ui.md, "The activity rail and
/// the sidebar"). The divider moves it; dragging it under
/// <see cref="SnapBelow"/> pixels snaps the sidebar shut, and the last width
/// above that is kept for when it opens again (<c>ui.sidebarWidth</c>).
/// </summary>
public static class SidebarSizing
{
    /// <summary>A width under this closes the sidebar; the narrowest an open sidebar can be is this.</summary>
    public const double SnapBelow = 150;

    /// <summary>The widest the sidebar gets.</summary>
    public const double MaxWidth = 480;

    /// <summary>The widest for a window this wide: half of it, never above <see cref="MaxWidth"/>, never under the snap width.</summary>
    public static double MaxFor(double windowWidth) => Math.Max(SnapBelow, Math.Min(MaxWidth, windowWidth / 2));

    /// <summary>What dragging the divider to <paramref name="proposed"/> pixels does in a window <paramref name="windowWidth"/> wide.</summary>
    public static SidebarDrag Drag(double proposed, double windowWidth) =>
        proposed < SnapBelow
            ? new SidebarDrag(true, SnapBelow)
            : new SidebarDrag(false, Math.Min(proposed, MaxFor(windowWidth)));

    /// <summary>
    /// The width to show: the remembered one kept within the limits for this
    /// window, or <paramref name="defaultWidth"/> (the design's clamp) when
    /// none was remembered.
    /// </summary>
    public static double Effective(double? remembered, double defaultWidth, double windowWidth) =>
        remembered is { } width
            ? Math.Clamp(width, SnapBelow, MaxFor(windowWidth))
            : defaultWidth;

    /// <summary>The value <c>ui.sidebarWidth</c> takes for a width: whole pixels.</summary>
    public static uint ToSetting(double width) => (uint)Math.Round(Math.Clamp(width, SnapBelow, MaxWidth));
}
