namespace CabinetOS.Core.Shell;

/// <summary>
/// Where a context menu's top-left corner goes (docs/ui.md, "The context menu"): at the point, as Explorer opens it,
/// so the row that was clicked stays in view. Above the point when the menu would not fit below it, and to its left
/// when it would not fit to the right. WinUI does neither for a flyout shown at a point (it measures against the screen,
/// not the window), so the window decides, in the window's own coordinates.
/// </summary>
public static class MenuPlacement
{
    /// <summary>The room kept between a menu and the window's edge.</summary>
    public const double Margin = 4;

    /// <summary>
    /// The corner for a menu <paramref name="width"/> by <paramref name="height"/> asked for at (<paramref name="atX"/>,
    /// <paramref name="atY"/>) in a window <paramref name="windowWidth"/> by <paramref name="windowHeight"/>. A menu
    /// that fits neither way sits against the edge that keeps most of it in the window.
    /// </summary>
    public static (double X, double Y) Corner(double atX, double atY, double width, double height, double windowWidth, double windowHeight) =>
        (Axis(atX, width, windowWidth), Axis(atY, height, windowHeight));

    // After the point when it fits there, else ending at the point when it fits there, else against the far edge. A point
    // outside the window counts as the edge it is past: WinUI ends the process for a flyout shown outside its window.
    private static double Axis(double at, double size, double window)
    {
        at = Math.Clamp(at, 0, Math.Max(0, window));
        if (at + size <= window - Margin)
        {
            return at;
        }
        if (at - size >= Margin)
        {
            return at - size;
        }
        return Math.Max(Margin, window - size - Margin);
    }
}
