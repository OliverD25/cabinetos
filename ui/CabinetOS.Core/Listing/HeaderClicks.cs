using CabinetOS.Core.Presentation;

namespace CabinetOS.Core.Listing;

/// <summary>What the window does with one click on a column heading.</summary>
public enum HeaderTap
{
    /// <summary>Sort the pane by the heading's column (or reverse it, when it is the order already).</summary>
    Sort,

    /// <summary>Nothing: this click is the second half of a double-click.</summary>
    Ignore,
}

/// <summary>
/// Tells the click that sorts a column heading from the clicks of a double-click, which fits the
/// column instead (docs/ui.md, "A pane's order" and "Column widths"). The window cannot know a click
/// is the first of two, and a sort that waited for the double-click time would make every sort slow.
/// So the first click sorts at once. The second click of the same heading within the double-click
/// time sorts nothing, and the double-click's own event asks to undo the sort the first click made,
/// so that a double-click ends with only the fit.
/// </summary>
public sealed class HeaderClicks(Func<long> nowMilliseconds, Func<long> doubleClickMilliseconds)
{
    private ListColumn? _column;
    private long _at;
    private bool _sorted;

    /// <summary>A click on <paramref name="column"/>'s heading.</summary>
    public HeaderTap Tap(ListColumn column)
    {
        var now = nowMilliseconds();
        if (_column == column && now - _at <= doubleClickMilliseconds())
        {
            return HeaderTap.Ignore;
        }
        (_column, _at, _sorted) = (column, now, true);
        return HeaderTap.Sort;
    }

    /// <summary>
    /// A double-click on <paramref name="column"/>'s heading: whether the sort that its first click made
    /// is still to be undone. It is asked for once per double-click.
    /// </summary>
    public bool DoubleTap(ListColumn column)
    {
        var undo = _sorted && _column == column && nowMilliseconds() - _at <= doubleClickMilliseconds();
        _sorted = false;
        return undo;
    }
}
