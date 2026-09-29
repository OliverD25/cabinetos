using CabinetOS.Core.Listing;

namespace CabinetOS.Tests;

/// <summary>A pane's selection: Windows list rules plus Total Commander's Insert.</summary>
public class SelectionModelTests
{
    private static SelectionModel Create(int count = 10, int focus = 0)
    {
        var selection = new SelectionModel();
        selection.Reset(count, focus);
        return selection;
    }

    [Fact]
    public void A_new_listing_selects_only_the_focused_row()
    {
        var selection = Create(focus: 3);
        Assert.Equal(3, selection.Focus);
        Assert.Equal([3], selection.Selected);

        selection.Reset(0, 0);
        Assert.Equal(-1, selection.Focus);
        Assert.Empty(selection.Targets());
    }

    [Fact]
    public void A_plain_move_selects_the_new_row_only_and_shift_extends_from_the_anchor()
    {
        var selection = Create(focus: 2);
        selection.MoveTo(4, SelectMode.Extend);
        Assert.Equal([2, 3, 4], selection.Selected);
        selection.MoveTo(1, SelectMode.Extend);
        Assert.Equal([1, 2], selection.Selected);
        Assert.Equal(2, selection.Anchor);

        selection.MoveTo(7, SelectMode.Single);
        Assert.Equal([7], selection.Selected);
        Assert.Equal(7, selection.Anchor);
    }

    [Fact]
    public void Ctrl_moves_the_focus_and_keeps_the_selection()
    {
        var selection = Create(focus: 2);
        selection.MoveTo(5, SelectMode.FocusOnly);
        Assert.Equal(5, selection.Focus);
        Assert.Equal([2], selection.Selected);
        Assert.Equal([2], selection.Targets());
    }

    [Fact]
    public void Ctrl_click_toggles_one_row_and_shift_click_selects_a_range()
    {
        var selection = Create(focus: 1);
        selection.Toggle(4);
        selection.Toggle(6);
        Assert.Equal([1, 4, 6], selection.Selected);
        selection.Toggle(4);
        Assert.Equal([1, 6], selection.Selected);

        selection.MoveTo(8, SelectMode.Extend);
        Assert.Equal([4, 5, 6, 7, 8], selection.Selected);
    }

    [Fact]
    public void Insert_toggles_the_focused_row_and_moves_down_like_total_commander()
    {
        var selection = Create(count: 3, focus: 0);
        selection.Toggle(0);
        Assert.Empty(selection.Selected);

        selection.ToggleFocusAndAdvance();
        selection.ToggleFocusAndAdvance();
        Assert.Equal([0, 1], selection.Selected);
        Assert.Equal(2, selection.Focus);
        selection.ToggleFocusAndAdvance();
        selection.ToggleFocusAndAdvance();
        Assert.Equal([0, 1], selection.Selected);
        Assert.Equal(2, selection.Focus);
    }

    [Fact]
    public void Select_all_keeps_the_focus_and_a_command_acts_on_the_selection()
    {
        var selection = Create(count: 4, focus: 2);
        selection.SelectAll();
        Assert.Equal(4, selection.SelectedCount);
        Assert.Equal(2, selection.Focus);
        Assert.Equal([0, 1, 2, 3], selection.Targets());

        selection.Toggle(2);
        selection.Toggle(0);
        selection.Toggle(1);
        selection.Toggle(3);
        Assert.Equal(0, selection.SelectedCount);
        Assert.Equal([3], selection.Targets());
    }

    [Fact]
    public void A_refresh_keeps_the_entries_found_again_or_falls_back_to_the_focus()
    {
        var selection = Create();
        selection.Restore(5, [1, 3, 9], focus: 3, anchor: 1);
        Assert.Equal([1, 3], selection.Selected);
        Assert.Equal((3, 1), (selection.Focus, selection.Anchor));

        selection.Restore(5, [], focus: 7, anchor: -1);
        Assert.Equal(4, selection.Focus);
        Assert.Equal([4], selection.Selected);
    }

    [Fact]
    public void Shift_after_a_refresh_extends_from_the_anchor_the_refresh_carried()
    {
        var selection = Create();
        selection.MoveTo(2, SelectMode.Single);
        selection.MoveTo(4, SelectMode.Extend);
        Assert.Equal([2, 3, 4], selection.Selected);

        // A file appeared at the top: the pane found the same entries one row lower (by their IDs).
        selection.Restore(11, [3, 4, 5], focus: 5, anchor: 3);
        selection.MoveTo(7, SelectMode.Extend);

        Assert.Equal([3, 4, 5, 6, 7], selection.Selected);
        Assert.Equal((7, 3), (selection.Focus, selection.Anchor));
    }

    [Fact]
    public void Shift_after_a_refresh_that_lost_the_anchor_extends_from_the_focus()
    {
        var selection = Create();
        selection.MoveTo(2, SelectMode.Single);
        selection.MoveTo(4, SelectMode.Extend);

        // The anchor's entry was deleted: the pane has no index for it.
        selection.Restore(9, [2, 3], focus: 3, anchor: -1);
        Assert.Equal(3, selection.Anchor);
        selection.MoveTo(1, SelectMode.Extend);

        Assert.Equal([1, 2, 3], selection.Selected);
    }

    [Fact]
    public void Every_change_is_announced()
    {
        var selection = Create();
        var changes = 0;
        selection.Changed += () => changes++;
        selection.MoveTo(1, SelectMode.Single);
        selection.Toggle(2);
        selection.SelectAll();
        selection.ToggleFocusAndAdvance();
        Assert.Equal(4, changes);
    }

    // ----- Total Commander's marking commands (sub-phase 11a) -----

    [Fact]
    public void Space_toggles_the_cursor_row_without_moving_the_cursor()
    {
        var selection = Commander(focus: 4);
        Assert.True(selection.ToggleFocus());
        Assert.Equal([4], selection.Selected);
        Assert.Equal(4, selection.Focus);
        Assert.False(selection.ToggleFocus());
        Assert.Empty(selection.Selected);
        Assert.Equal(4, selection.Focus);

        var empty = new SelectionModel();
        Assert.False(empty.ToggleFocus());
    }

    [Fact]
    public void Invert_turns_the_files_marks_around_and_leaves_the_folders_as_they_are()
    {
        // Rows 0 and 1 are folders.
        var selection = Commander(count: 6);
        selection.Toggle(0);
        selection.Toggle(2);
        selection.Toggle(3);

        selection.Invert(isFolder: i => i < 2);

        Assert.Equal([0, 4, 5], selection.Selected);
    }

    [Fact]
    public void Unselect_all_leaves_nothing_marked_and_a_command_then_acts_on_the_cursor_row()
    {
        var selection = Create(count: 5, focus: 2);
        selection.SelectAll();
        selection.Clear();
        Assert.Empty(selection.Selected);
        Assert.Equal([2], selection.Targets());
    }

    [Fact]
    public void Marking_many_rows_adds_or_removes_them_and_leaves_the_rest()
    {
        var selection = Commander(count: 8);
        selection.SetMarks([1, 2, 3, 9, -1], mark: true);
        Assert.Equal([1, 2, 3], selection.Selected);
        selection.SetMarks([2, 5], mark: false);
        Assert.Equal([1, 3], selection.Selected);

        var changes = 0;
        selection.Changed += () => changes++;
        selection.SetMarks([4], mark: true);
        selection.Invert(_ => false);
        selection.Clear();
        Assert.Equal(3, changes);
    }

    // ----- panes.selection: windows (above) and commander (below) -----

    private static SelectionModel Commander(int count = 10, int focus = 0)
    {
        var selection = new SelectionModel();
        selection.SetStyle(SelectionStyle.Commander);
        selection.Reset(count, focus);
        return selection;
    }

    [Theory]
    [InlineData(SelectionStyle.Windows, false, false, SelectMode.Single)]
    [InlineData(SelectionStyle.Windows, true, false, SelectMode.Extend)]
    [InlineData(SelectionStyle.Windows, false, true, SelectMode.FocusOnly)]
    [InlineData(SelectionStyle.Windows, true, true, SelectMode.Extend)]
    [InlineData(SelectionStyle.Commander, false, false, SelectMode.FocusOnly)]
    [InlineData(SelectionStyle.Commander, true, false, SelectMode.MarkPassed)]
    [InlineData(SelectionStyle.Commander, false, true, SelectMode.FocusOnly)]
    [InlineData(SelectionStyle.Commander, true, true, SelectMode.MarkPassed)]
    public void A_key_that_moves_the_cursor_marks_by_the_style(SelectionStyle style, bool shift, bool ctrl, SelectMode expected)
    {
        var selection = new SelectionModel();
        selection.SetStyle(style);
        Assert.Equal(expected, selection.KeyMode(shift, ctrl));
    }

    [Fact]
    public void Shift_with_home_or_end_marks_through_the_last_row_in_commander_style_only()
    {
        Assert.Equal(SelectMode.MarkThrough, Commander().KeyMode(shift: true, ctrl: false, toListEnd: true));
        Assert.Equal(SelectMode.FocusOnly, Commander().KeyMode(shift: false, ctrl: false, toListEnd: true));
        Assert.Equal(SelectMode.Extend, Create().KeyMode(shift: true, ctrl: false, toListEnd: true));
    }

    [Fact]
    public void In_commander_style_a_new_listing_starts_unmarked_and_a_command_acts_on_the_cursor_row()
    {
        var selection = Commander(focus: 3);
        Assert.Equal(3, selection.Focus);
        Assert.Empty(selection.Selected);
        Assert.Equal([3], selection.Targets());
    }

    [Fact]
    public void In_commander_style_a_plain_or_ctrl_key_moves_the_cursor_and_keeps_the_marks()
    {
        var selection = Commander();
        selection.ToggleFocusAndAdvance();
        selection.ToggleFocusAndAdvance();
        Assert.Equal([0, 1], selection.Selected);

        selection.MoveTo(7, selection.KeyMode(shift: false, ctrl: false));
        selection.MoveTo(4, selection.KeyMode(shift: false, ctrl: true));

        Assert.Equal(4, selection.Focus);
        Assert.Equal([0, 1], selection.Selected);
        Assert.Equal([0, 1], selection.Targets());
    }

    [Fact]
    public void In_commander_style_shift_marks_the_rows_the_cursor_leaves()
    {
        var selection = Commander();
        selection.MoveTo(1, SelectMode.MarkPassed);
        Assert.Equal([0], selection.Selected);
        selection.MoveTo(2, SelectMode.MarkPassed);
        Assert.Equal([0, 1], selection.Selected);

        // A page down: every row it passes over, not the one it lands on.
        selection.MoveTo(5, SelectMode.MarkPassed);
        Assert.Equal([0, 1, 2, 3, 4], selection.Selected);
        Assert.Equal(5, selection.Focus);

        // Up: the row it leaves is marked first.
        selection.MoveTo(9, SelectMode.FocusOnly);
        selection.MoveTo(8, SelectMode.MarkPassed);
        Assert.Equal([0, 1, 2, 3, 4, 9], selection.Selected);
    }

    [Fact]
    public void In_commander_style_shift_over_marked_rows_unmarks_them()
    {
        var selection = Commander();
        selection.SelectAll();

        // The row the cursor leaves is marked, so the rows passed over are unmarked.
        selection.MoveTo(3, SelectMode.MarkPassed);

        Assert.Equal([3, 4, 5, 6, 7, 8, 9], selection.Selected);
        Assert.Equal(3, selection.Focus);
    }

    [Fact]
    public void In_commander_style_shift_at_the_end_of_the_list_marks_the_row_the_cursor_stays_on()
    {
        var selection = Commander(count: 3, focus: 1);
        selection.MoveTo(2, SelectMode.MarkPassed);
        Assert.Equal([1], selection.Selected);

        // The cursor cannot go further down: the last row itself is marked, and again unmarked.
        selection.MoveTo(3, SelectMode.MarkPassed);
        Assert.Equal([1, 2], selection.Selected);
        selection.MoveTo(3, SelectMode.MarkPassed);
        Assert.Equal([1], selection.Selected);
        Assert.Equal(2, selection.Focus);
    }

    [Fact]
    public void Shift_with_home_or_end_marks_every_row_to_the_end_of_the_list()
    {
        var down = Commander(count: 6, focus: 2);
        down.MoveTo(5, SelectMode.MarkThrough);
        Assert.Equal([2, 3, 4, 5], down.Selected);
        Assert.Equal(5, down.Focus);

        var up = Commander(count: 6, focus: 3);
        up.MoveTo(0, SelectMode.MarkThrough);
        Assert.Equal([0, 1, 2, 3], up.Selected);
        Assert.Equal(0, up.Focus);

        // Back over the marked rows: they are unmarked, as with Shift and the arrows.
        down.MoveTo(0, SelectMode.MarkThrough);
        Assert.Empty(down.Selected);
    }

    [Fact]
    public void In_commander_style_a_refresh_keeps_the_marks_and_marks_nothing_else()
    {
        var selection = Commander();
        selection.Restore(5, [1, 3, 9], focus: 3, anchor: 1);
        Assert.Equal([1, 3], selection.Selected);

        selection.Restore(5, [], focus: 7, anchor: -1);
        Assert.Equal(4, selection.Focus);
        Assert.Empty(selection.Selected);
    }

    [Fact]
    public void The_mouse_keeps_the_windows_rules_in_commander_style()
    {
        var selection = Commander();
        selection.ToggleFocusAndAdvance();
        selection.ToggleFocusAndAdvance();

        // A click: that row alone. Ctrl+Click toggles one, Shift+Click selects a range.
        selection.MoveTo(5, SelectMode.Single);
        Assert.Equal([5], selection.Selected);
        selection.Toggle(7);
        Assert.Equal([5, 7], selection.Selected);
        selection.MoveTo(9, SelectMode.Extend);
        Assert.Equal([7, 8, 9], selection.Selected);
    }

    [Fact]
    public void Switching_the_style_keeps_the_marks_and_windows_selects_the_cursor_row_when_none_is_marked()
    {
        var selection = Create(focus: 2);
        selection.SetStyle(SelectionStyle.Commander);
        Assert.Equal([2], selection.Selected);
        selection.Toggle(2);
        Assert.Empty(selection.Selected);

        selection.SetStyle(SelectionStyle.Windows);
        Assert.Equal([2], selection.Selected);
        Assert.Equal(SelectionStyle.Windows, selection.Style);
    }
}
