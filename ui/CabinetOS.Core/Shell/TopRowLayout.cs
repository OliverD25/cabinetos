namespace CabinetOS.Core.Shell;

/// <summary>Where the command center goes in the top row, or that it is hidden.</summary>
public sealed record CommandCenterPlace(bool Visible, double Left, double Width);

/// <summary>
/// The top row's command center (docs/ui.md, "The top row"; the creator's
/// SHELL_REDESIGN.md §1): centred in the window, <c>clamp(200px, 34%,
/// 380px)</c> wide, hidden below 640 px of window width (Ctrl+P still works).
/// It never covers the clusters at its sides: when the centred box would, it
/// moves over and narrows into the room between them, and when less than
/// <see cref="LeastWidth"/> is left it hides too.
/// </summary>
public static class TopRowLayout
{
    /// <summary>Below this window width the command center hides.</summary>
    public const double HideBelow = 640;

    /// <summary>The narrowest the centred box is made.</summary>
    public const double MinWidth = 200;

    /// <summary>The widest it gets.</summary>
    public const double MaxWidth = 380;

    /// <summary>Its share of the window's width, between the two.</summary>
    public const double Share = 0.34;

    /// <summary>The space it keeps from the clusters at its sides.</summary>
    public const double Gap = 8;

    /// <summary>
    /// The least room it is shown in: its glyph, a few letters of the
    /// workspace's name and the Ctrl+P hint. Less than that, and the box hides.
    /// </summary>
    public const double LeastWidth = 120;

    /// <summary>The width the design gives the box in a window <paramref name="windowWidth"/> wide.</summary>
    public static double PreferredWidth(double windowWidth) => Math.Clamp(windowWidth * Share, MinWidth, MaxWidth);

    /// <summary>
    /// The box in a window <paramref name="windowWidth"/> wide whose left
    /// cluster (menu, icon, pill) ends at <paramref name="leftEnd"/> and whose
    /// right cluster (view buttons, caption buttons) starts at <paramref name="rightStart"/>.
    /// </summary>
    public static CommandCenterPlace Place(double windowWidth, double leftEnd, double rightStart)
    {
        if (windowWidth < HideBelow)
        {
            return new CommandCenterPlace(false, 0, 0);
        }
        var low = leftEnd + Gap;
        var high = rightStart - Gap;
        var width = Math.Min(PreferredWidth(windowWidth), high - low);
        if (width < LeastWidth)
        {
            return new CommandCenterPlace(false, 0, 0);
        }
        var left = (windowWidth - width) / 2;
        left = Math.Clamp(left, low, high - width);
        return new CommandCenterPlace(true, left, width);
    }
}
