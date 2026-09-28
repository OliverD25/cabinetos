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
}
