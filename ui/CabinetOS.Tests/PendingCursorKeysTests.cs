using CabinetOS.Core.Listing;

namespace CabinetOS.Tests;

/// <summary>
/// The cursor keys a pane makes at the start of the next frame (docs/ui.md,
/// "Scrolling"): each one as it would have been made at once, in order.
/// </summary>
public class PendingCursorKeysTests
{
    private static SelectionModel Selection(SelectionStyle style, int count = 200, int focus = 0)
    {
        var selection = new SelectionModel();
        selection.SetStyle(style);
        selection.Reset(count, focus);
        return selection;
    }

    private static void MakeAtOnce(SelectionModel selection, CursorKey key, bool shift, bool ctrl, int rowsPerPage)
    {
        var (index, mode) = PendingCursorKeys.Target(selection, key, shift, ctrl, rowsPerPage);
        selection.MoveTo(index, mode);
    }

    private static void MakeWaiting(PendingCursorKeys keys, SelectionModel selection, int rowsPerPage)
    {
        while (keys.TryTake(out var key))
        {
            MakeAtOnce(selection, key.Key, key.Shift, key.Ctrl, rowsPerPage);
        }
    }

    [Fact]
    public void Keys_wait_in_the_order_they_came_and_are_taken_once()
    {
        var keys = new PendingCursorKeys();
        Assert.True(keys.IsEmpty);
        keys.Add(CursorKey.PageDown, shift: false, ctrl: false);
        keys.Add(CursorKey.Up, shift: true, ctrl: false);

        Assert.Equal(2, keys.Count);
        Assert.True(keys.TryTake(out var first));
        Assert.Equal((CursorKey.PageDown, false, false), first);
        Assert.True(keys.TryTake(out var second));
        Assert.Equal((CursorKey.Up, true, false), second);
        Assert.False(keys.TryTake(out _));
        Assert.True(keys.IsEmpty);
    }

    [Fact]
    public void Clear_drops_the_waiting_keys()
    {
        var keys = new PendingCursorKeys();
        keys.Add(CursorKey.Down, false, false);
        keys.Clear();
        Assert.True(keys.IsEmpty);
    }

    [Theory]
    [InlineData(CursorKey.Up, 49)]
    [InlineData(CursorKey.Down, 51)]
    [InlineData(CursorKey.PageUp, 21)]
    [InlineData(CursorKey.PageDown, 79)]
    public void A_key_moves_a_row_or_a_page_from_the_cursor(CursorKey key, int expected)
    {
        var selection = Selection(SelectionStyle.Windows, focus: 50);
        Assert.Equal((expected, SelectMode.Single), PendingCursorKeys.Target(selection, key, shift: false, ctrl: false, rowsPerPage: 29));
    }

    [Fact]
    public void The_mode_is_the_styles_as_for_a_key_made_at_once()
    {
        var windows = Selection(SelectionStyle.Windows, focus: 10);
        Assert.Equal(SelectMode.Extend, PendingCursorKeys.Target(windows, CursorKey.Down, shift: true, ctrl: false, 29).Mode);
        Assert.Equal(SelectMode.FocusOnly, PendingCursorKeys.Target(windows, CursorKey.Down, shift: false, ctrl: true, 29).Mode);
        var commander = Selection(SelectionStyle.Commander, focus: 10);
        Assert.Equal(SelectMode.MarkPassed, PendingCursorKeys.Target(commander, CursorKey.PageDown, shift: true, ctrl: false, 29).Mode);
        Assert.Equal(SelectMode.FocusOnly, PendingCursorKeys.Target(commander, CursorKey.Down, shift: false, ctrl: false, 29).Mode);
    }

    [Theory]
    [InlineData(SelectionStyle.Windows)]
    [InlineData(SelectionStyle.Commander)]
    public void Keys_made_at_the_next_frame_leave_the_same_cursor_and_marks_as_keys_made_at_once(SelectionStyle style)
    {
        // A held PageDown, Shift+Down twice over a row marked before (Total Commander unmarks it then),
        // Ctrl+Up, and a PageUp past the top: the order and each key's start matter.
        var presses = new (CursorKey Key, bool Shift, bool Ctrl)[]
        {
            (CursorKey.PageDown, false, false), (CursorKey.PageDown, false, false), (CursorKey.Down, true, false),
            (CursorKey.Down, true, false), (CursorKey.Up, false, true), (CursorKey.PageUp, true, false),
            (CursorKey.PageUp, false, false), (CursorKey.PageUp, false, false), (CursorKey.PageUp, false, false),
        };
        var atOnce = Selection(style);
        var later = Selection(style);
        atOnce.SetMarks([59], mark: true);
        later.SetMarks([59], mark: true);

        foreach (var (key, shift, ctrl) in presses)
        {
            MakeAtOnce(atOnce, key, shift, ctrl, rowsPerPage: 29);
        }
        var waiting = new PendingCursorKeys();
        foreach (var (key, shift, ctrl) in presses)
        {
            waiting.Add(key, shift, ctrl);
        }
        MakeWaiting(waiting, later, rowsPerPage: 29);

        Assert.Equal(atOnce.Focus, later.Focus);
        Assert.Equal(atOnce.Selected, later.Selected);
        Assert.Equal(atOnce.Anchor, later.Anchor);
    }
}
