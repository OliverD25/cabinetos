namespace CabinetOS.Core.Presentation;

/// <summary>
/// Which crumbs the address bar shows: the root (the drive or share) or
/// not, the ellipsis for the crumbs left out or not, and the first of the
/// last crumbs shown (every crumb from it to the end is shown).
/// </summary>
public sealed record CrumbLayout(bool ShowRoot, int FirstTail, bool Ellipsis);

/// <summary>
/// Fits the crumbs of a path into the address bar (docs/ui.md, "Edge
/// cases"): all of them when they fit; else the root, an ellipsis and as
/// many of the last crumbs as fit, the last one always, since it is the
/// folder shown; when even that is too wide, the ellipsis and the last one.
/// </summary>
public static class CrumbFit
{
    /// <summary>The layout for crumbs of <paramref name="widths"/> (the root first) in <paramref name="available"/> pixels.</summary>
    public static CrumbLayout Fit(IReadOnlyList<double> widths, double available, double separator, double ellipsis)
    {
        if (widths.Count == 0)
        {
            return new CrumbLayout(true, 1, false);
        }
        var all = widths.Sum() + (separator * (widths.Count - 1));
        if (all <= available || widths.Count == 1)
        {
            return new CrumbLayout(true, 1, false);
        }
        // The root, the ellipsis, and the tail; a separator after the root and after the ellipsis.
        var room = available - widths[0] - separator - ellipsis - separator;
        var first = widths.Count - 1;
        var used = widths[first];
        while (first > 1 && used + separator + widths[first - 1] <= room)
        {
            first--;
            used += separator + widths[first];
        }
        return used <= room ? new CrumbLayout(true, first, true) : new CrumbLayout(false, widths.Count - 1, true);
    }
}
