namespace CabinetOS.Core.Listing;

/// <summary>How moving the focus changes the selection.</summary>
public enum SelectMode
{
    /// <summary>Only the new focused row is selected (a plain arrow key or click).</summary>
    Single,

    /// <summary>The rows from the anchor to the new focus are selected (Shift).</summary>
    Extend,

    /// <summary>The focus moves and the selection stays (Ctrl).</summary>
    FocusOnly,

    /// <summary>
    /// Total Commander's Shift: the rows the focus leaves on its way are
    /// marked, not the one it lands on; when it cannot move (the end of the
    /// list), the row it stays on. If the row it leaves was marked, they are
    /// unmarked instead.
    /// </summary>
    MarkPassed,

    /// <summary>Total Commander's Shift with Home or End: <see cref="MarkPassed"/> with the row it lands on.</summary>
    MarkThrough,
}

/// <summary>How the keyboard marks rows (<c>panes.selection</c>, docs/config.md).</summary>
public enum SelectionStyle
{
    /// <summary>As in Explorer: a key that moves the focus selects the row it moves to.</summary>
    Windows,

    /// <summary>
    /// As in Total Commander: keys that move the focus keep the marks, Shift
    /// with them marks the rows passed over, and a new listing starts unmarked.
    /// </summary>
    Commander,
}

/// <summary>
/// One pane's selection, as indexes into its listing: the focus (the row the
/// keyboard is on), the anchor where a Shift range starts, and the selected
/// rows. It follows the Windows list rules, plus Total Commander's Insert,
/// which toggles the focused row and moves down without touching the others.
/// In <see cref="SelectionStyle.Commander"/> style the keys mark as Total
/// Commander's do (<see cref="KeyMode"/>); the mouse keeps the Windows rules.
/// </summary>
public sealed class SelectionModel
{
    private readonly HashSet<int> _selected = [];

    /// <summary>Raised after every change.</summary>
    public event Action? Changed;

    /// <summary>How the keyboard marks rows.</summary>
    public SelectionStyle Style { get; private set; }

    /// <summary>Rows in the listing.</summary>
    public int Count { get; private set; }

    /// <summary>The focused row, or -1 when the listing is empty.</summary>
    public int Focus { get; private set; } = -1;

    /// <summary>Where a Shift range starts.</summary>
    public int Anchor { get; private set; } = -1;

    /// <summary>How many rows are selected.</summary>
    public int SelectedCount => _selected.Count;

    /// <summary>The selected rows in no particular order (cheap, for sums).</summary>
    public IEnumerable<int> SelectedUnordered => _selected;

    /// <summary>The selected rows in listing order.</summary>
    public IReadOnlyList<int> Selected => [.. _selected.Order()];

    /// <summary>Whether row <paramref name="index"/> is selected.</summary>
    public bool IsSelected(int index) => _selected.Contains(index);

    /// <summary>
    /// What a command acts on: the selected rows, or the focused row when none
    /// is selected (so F5 on a row never does nothing).
    /// </summary>
    public IReadOnlyList<int> Targets() => _selected.Count > 0 ? Selected : Focus >= 0 ? [Focus] : [];

    /// <summary>
    /// Changes how the keyboard marks. The marks stay; the Windows style
    /// selects the focused row when nothing is selected, as it always has one.
    /// </summary>
    public void SetStyle(SelectionStyle style)
    {
        if (style == Style)
        {
            return;
        }
        Style = style;
        if (style == SelectionStyle.Windows && _selected.Count == 0 && Focus >= 0)
        {
            _selected.Add(Focus);
        }
        Raise();
    }

    /// <summary>
    /// How a key that moves the focus selects, in this style: Shift and Ctrl
    /// as they are held, and whether the key goes to the list's first or last
    /// row (Home, End).
    /// </summary>
    public SelectMode KeyMode(bool shift, bool ctrl, bool toListEnd = false) => Style switch
    {
        SelectionStyle.Commander when shift => toListEnd ? SelectMode.MarkThrough : SelectMode.MarkPassed,
        SelectionStyle.Commander => SelectMode.FocusOnly,
        _ when shift => SelectMode.Extend,
        _ => ctrl ? SelectMode.FocusOnly : SelectMode.Single,
    };

    /// <summary>
    /// A new listing: the focus goes to <paramref name="focus"/>, and only it
    /// is selected; in the Commander style nothing is.
    /// </summary>
    public void Reset(int count, int focus)
    {
        Count = Math.Max(0, count);
        _selected.Clear();
        Focus = Count == 0 ? -1 : Math.Clamp(focus, 0, Count - 1);
        Anchor = Focus;
        if (Focus >= 0 && Style == SelectionStyle.Windows)
        {
            _selected.Add(Focus);
        }
        Raise();
    }

    /// <summary>
    /// The same entries in a refreshed listing, at the indexes the caller
    /// found for them. When none of the selected entries is left, the focused
    /// row is selected (Windows style), so the keyboard still has something
    /// to act on; in the Commander style the focused row is that already.
    /// </summary>
    public void Restore(int count, IEnumerable<int> selected, int focus, int anchor)
    {
        Count = Math.Max(0, count);
        _selected.Clear();
        foreach (var index in selected)
        {
            if ((uint)index < (uint)Count)
            {
                _selected.Add(index);
            }
        }
        Focus = Count == 0 ? -1 : Math.Clamp(focus, 0, Count - 1);
        Anchor = (uint)anchor < (uint)Count ? anchor : Focus;
        if (_selected.Count == 0 && Focus >= 0 && Style == SelectionStyle.Windows)
        {
            _selected.Add(Focus);
        }
        Raise();
    }

    /// <summary>Moves the focus to <paramref name="index"/> (clamped) and selects by <paramref name="mode"/>.</summary>
    public void MoveTo(int index, SelectMode mode)
    {
        if (Count == 0)
        {
            return;
        }
        index = Math.Clamp(index, 0, Count - 1);
        switch (mode)
        {
            case SelectMode.Single:
                _selected.Clear();
                _selected.Add(index);
                Anchor = index;
                break;
            case SelectMode.Extend:
                SelectRange(Anchor < 0 ? index : Anchor, index);
                break;
            case SelectMode.MarkPassed or SelectMode.MarkThrough when Focus >= 0:
                MarkOnTheWay(Focus, index, through: mode == SelectMode.MarkThrough);
                break;
        }
        Focus = index;
        Raise();
    }

    /// <summary>Ctrl+Click: toggles one row and moves the focus and the anchor there.</summary>
    public void Toggle(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            return;
        }
        if (!_selected.Remove(index))
        {
            _selected.Add(index);
        }
        Focus = index;
        Anchor = index;
        Raise();
    }

    /// <summary>Insert: toggles the focused row and moves the focus one row down.</summary>
    public void ToggleFocusAndAdvance()
    {
        if (Focus < 0)
        {
            return;
        }
        if (!_selected.Remove(Focus))
        {
            _selected.Add(Focus);
        }
        Focus = Math.Min(Focus + 1, Count - 1);
        Anchor = Focus;
        Raise();
    }

    /// <summary>Ctrl+A: every row; the focus stays.</summary>
    public void SelectAll()
    {
        for (var i = 0; i < Count; i++)
        {
            _selected.Add(i);
        }
        Raise();
    }

    private void SelectRange(int from, int to)
    {
        _selected.Clear();
        var (low, high) = from <= to ? (from, to) : (to, from);
        for (var i = low; i <= high; i++)
        {
            _selected.Add(i);
        }
    }

    // The rows from `from` towards `to`: without `to`, unless the move goes through it or cannot
    // move at all. The row it leaves decides: unmarked, they are marked; marked, they are unmarked.
    private void MarkOnTheWay(int from, int to, bool through)
    {
        var mark = !_selected.Contains(from);
        var step = to >= from ? 1 : -1;
        var last = through || to == from ? to : to - step;
        for (var i = from; ; i += step)
        {
            if (mark)
            {
                _selected.Add(i);
            }
            else
            {
                _selected.Remove(i);
            }
            if (i == last)
            {
                break;
            }
        }
    }

    private void Raise() => Changed?.Invoke();
}
