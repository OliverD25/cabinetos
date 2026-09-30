namespace CabinetOS.Core.Shell;

/// <summary>
/// Which of a pane's Back, Forward and Up buttons work (docs/ui.md, "The
/// breadcrumb row"). A button that does not is drawn at 30 %, as the
/// creator's SHELL_REDESIGN.md §2 asks of Forward without a forward entry.
/// A locked tab stays on its folder, so its Back and Forward do not work.
/// </summary>
public sealed record NavState(bool Back, bool Forward, bool Up)
{
    /// <summary>The opacity of a button that does not work.</summary>
    public const double DisabledOpacity = 0.3;

    /// <summary>The buttons of a tab with <paramref name="backCount"/> and <paramref name="forwardCount"/> folders in its history, showing <paramref name="path"/>.</summary>
    public static NavState From(int backCount, int forwardCount, bool locked, string path) =>
        new(backCount > 0 && !locked, forwardCount > 0 && !locked, Presentation.DisplayFormat.Parent(path) is not null);

    /// <summary>A button's opacity: whole when it works, <see cref="DisabledOpacity"/> when not.</summary>
    public static double Opacity(bool works) => works ? 1 : DisabledOpacity;
}
