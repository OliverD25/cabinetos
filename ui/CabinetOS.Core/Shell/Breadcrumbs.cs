using CabinetOS.Core.Presentation;

namespace CabinetOS.Core.Shell;

/// <summary>
/// One segment of a pane's path row: a folder's name and where a click
/// goes. <see cref="IsEllipsis"/> marks the "…" of a collapsed path, which
/// stands for the folders it hides and goes to the last of them.
/// </summary>
public sealed record CrumbSegment(string Label, string Path, bool IsEllipsis = false);

/// <summary>
/// The segments of a pane's path row (docs/ui.md, "The pane's rows"; the
/// creator's SHELL_REDESIGN.md v2 §2): one per part of the path, or, when the
/// path has more than 5 parts in dual mode (8 with one pane),
/// <c>Drive › … › parent › current</c>. A segment is never cut short on its
/// own: the rule counts parts, not pixels, and a row still too narrow scrolls.
/// </summary>
public static class Breadcrumbs
{
    /// <summary>The most parts a path shows whole with two panes.</summary>
    public const int DualLimit = 5;

    /// <summary>The most parts a path shows whole with one pane.</summary>
    public const int SingleLimit = 8;

    /// <summary>The label of the segment that stands for the hidden folders.</summary>
    public const string Ellipsis = "…";

    /// <summary>
    /// The segments for <paramref name="path"/> in a pane of a window with two
    /// panes (<paramref name="dual"/>) or one. A closing backslash changes
    /// nothing; an empty path has no segment.
    /// </summary>
    public static IReadOnlyList<CrumbSegment> Segments(string path, bool dual)
    {
        var parts = DisplayFormat.Crumbs(path);
        if (parts.Count <= (dual ? DualLimit : SingleLimit))
        {
            return [.. parts.Select(p => new CrumbSegment(p.Label, p.Path))];
        }
        // The "…" goes to the last folder it hides: the parent's parent.
        var hidden = parts[^3];
        return
        [
            new CrumbSegment(parts[0].Label, parts[0].Path),
            new CrumbSegment(Ellipsis, hidden.Path, IsEllipsis: true),
            new CrumbSegment(parts[^2].Label, parts[^2].Path),
            new CrumbSegment(parts[^1].Label, parts[^1].Path),
        ];
    }

    /// <summary>The row as text, with <c>›</c> between the segments: <c>C: › … › Projects › fileforge</c> (logs and tests).</summary>
    public static string Text(IReadOnlyList<CrumbSegment> segments) => string.Join(" › ", segments.Select(s => s.Label));
}
