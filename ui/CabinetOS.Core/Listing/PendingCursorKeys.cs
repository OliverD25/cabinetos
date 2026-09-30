namespace CabinetOS.Core.Listing;

/// <summary>A key that moves a pane's cursor: one row or one page, up or down.</summary>
public enum CursorKey
{
    /// <summary>One row up.</summary>
    Up,

    /// <summary>One row down.</summary>
    Down,

    /// <summary>One page up.</summary>
    PageUp,

    /// <summary>One page down.</summary>
    PageDown,
}

/// <summary>
/// The cursor keys a pane took since the last frame, to be made at the start
/// of the next one (docs/ui.md, "Scrolling"). A key message arrives at any
/// moment between two frames; a page's rows made at that moment took about
/// 15 ms, ran into the next frame's start, and made it late: with PageDown
/// held, 28 to 30 % of the gaps between frames were over 20 ms. Made at the
/// start of a frame, the same work fits in that frame. Each key is made as it
/// would have been at once, in the order it came, from where the one before
/// left the cursor; anything else that reads the cursor (a command, a click,
/// a typed letter) makes the waiting keys first.
/// </summary>
public sealed class PendingCursorKeys
{
    private readonly Queue<(CursorKey Key, bool Shift, bool Ctrl)> _keys = new();

    /// <summary>Whether no key waits.</summary>
    public bool IsEmpty => _keys.Count == 0;

    /// <summary>How many keys wait.</summary>
    public int Count => _keys.Count;

    /// <summary>A key pressed with <paramref name="shift"/> and <paramref name="ctrl"/> held.</summary>
    public void Add(CursorKey key, bool shift, bool ctrl) => _keys.Enqueue((key, shift, ctrl));

    /// <summary>The oldest waiting key, taken out; false when none waits.</summary>
    public bool TryTake(out (CursorKey Key, bool Shift, bool Ctrl) key) => _keys.TryDequeue(out key);

    /// <summary>Drops every waiting key (the pane shows something else now).</summary>
    public void Clear() => _keys.Clear();

    /// <summary>
    /// Where <paramref name="key"/> moves the cursor of <paramref name="selection"/>,
    /// and how it selects on the way (<see cref="SelectionModel.KeyMode"/>): the
    /// same as the key made at once. A page is <paramref name="rowsPerPage"/> rows.
    /// </summary>
    public static (int Index, SelectMode Mode) Target(SelectionModel selection, CursorKey key, bool shift, bool ctrl, int rowsPerPage)
    {
        var focus = selection.Focus;
        var mode = selection.KeyMode(shift, ctrl);
        return key switch
        {
            CursorKey.Up => (focus - 1, mode),
            CursorKey.Down => (focus + 1, mode),
            CursorKey.PageUp => (focus - rowsPerPage, mode),
            _ => (focus + rowsPerPage, mode),
        };
    }
}
