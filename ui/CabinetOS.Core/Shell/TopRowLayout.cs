namespace CabinetOS.Core.Shell;

/// <summary>
/// The top row of v2 of the shell redesign (docs/ui.md, "The top row"; the
/// creator's SHELL_REDESIGN.md §1): the menu, the app icon and the title
/// "CabinetOS · folder" on the left, the flexible drag space, then the Quick
/// Open chip, the view buttons and the caption buttons. The chip never
/// hides: the title gives way to it, cut short with an ellipsis, so nothing
/// in the row lies over anything else down to the window's least width.
/// </summary>
public static class TopRowLayout
{
    /// <summary>The window's least width: the top row and both panes' toolbars still fit.</summary>
    public const double MinWindowWidth = 600;

    /// <summary>The space the title keeps from the chip on its right.</summary>
    public const double Gap = 8;

    /// <summary>The app's name, the title's first part.</summary>
    public const string AppName = "CabinetOS";

    /// <summary>What stands between the app's name and the folder's.</summary>
    public const string Separator = " · ";

    /// <summary>
    /// The title for the active pane's front tab named <paramref name="folderName"/>:
    /// <c>CabinetOS · fileforge</c>, or the app's name alone while no folder is shown.
    /// </summary>
    public static string Title(string? folderName) =>
        string.IsNullOrWhiteSpace(folderName) ? AppName : AppName + Separator + folderName;

    /// <summary>
    /// The widest the title may be when the left cluster (menu and icon)
    /// ends at <paramref name="leftEnd"/> and the chip starts at
    /// <paramref name="chipLeft"/>: the room between them less the gap,
    /// never below 0. A title wider than that ends with an ellipsis.
    /// </summary>
    public static double TitleRoom(double leftEnd, double chipLeft) => Math.Max(0, chipLeft - Gap - leftEnd);
}
