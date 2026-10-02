using CabinetOS.Core.Presentation;

namespace CabinetOS.Tests;

/// <summary>
/// A click from the terminal into a pane: WinUI's second focus move, to the
/// window's root, is refused, so the pane the click focused keeps the
/// keyboard (the real-key runs of 2026-10-02).
/// </summary>
public class ClickFocusTests
{
    [Fact]
    public void The_pointers_move_from_the_clicked_pane_to_the_windows_root_is_refused()
    {
        Assert.True(ClickFocus.RefuseMove(byPointer: true, toWindowRoot: true, fromWindowControl: true, fromPage: false));
    }

    [Fact]
    public void A_move_to_any_control_of_the_window_goes_ahead()
    {
        Assert.False(ClickFocus.RefuseMove(byPointer: true, toWindowRoot: false, fromWindowControl: true, fromPage: false));
        Assert.False(ClickFocus.RefuseMove(byPointer: true, toWindowRoot: false, fromWindowControl: true, fromPage: true));
    }

    [Fact]
    public void Only_the_pointer_is_refused_the_program_and_the_keys_are_not()
    {
        Assert.False(ClickFocus.RefuseMove(byPointer: false, toWindowRoot: true, fromWindowControl: true, fromPage: false));
    }

    [Fact]
    public void A_page_that_has_xamls_focus_is_left_to_its_own_hand_over()
    {
        Assert.False(ClickFocus.RefuseMove(byPointer: true, toWindowRoot: true, fromWindowControl: true, fromPage: true));
    }

    [Fact]
    public void With_no_control_focused_there_is_nothing_to_keep()
    {
        Assert.False(ClickFocus.RefuseMove(byPointer: true, toWindowRoot: true, fromWindowControl: false, fromPage: false));
    }
}
