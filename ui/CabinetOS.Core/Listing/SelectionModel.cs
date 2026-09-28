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
}

/// <summary>
/// One pane's selection, as indexes into its listing: the focus (the row the
/// keyboard is on), the anchor where a Shift range starts, and the selected
/// rows. It follows the Windows list rules, plus Total Commander's Insert,
/// which toggles the focused row and moves down without touching the others.
/// </summary>
public sealed class SelectionModel
{
    private readonly HashSet<int> _selected = [];

    /// <summary>Raised after every change.</summary>
    public event Action? Changed;

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

    /// <summary>A new listing: the focus goes to <paramref name="focus"/>, and only it is selected.</summary>
    public void Reset(int count, int focus)
    {
        Count = Math.Max(0, count);
        _selected.Clear();
        Focus = Count == 0 ? -1 : Math.Clamp(focus, 0, Count - 1);
        Anchor = Focus;
        if (Focus >= 0)
        {
            _selected.Add(Focus);
        }
        Raise();
    }

    /// <summary>
    /// The same entries in a refreshed listing, at the indexes the caller
    /// found for them. When none of the selected entries is left, the focused
    /// row is selected, so the keyboard still has something to act on.
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
        if (_selected.Count == 0 && Focus >= 0)
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

    private void Raise() => Changed?.Invoke();
}
