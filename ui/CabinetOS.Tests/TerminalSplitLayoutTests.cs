using CabinetOS.Core.Terminal;

namespace CabinetOS.Tests;

/// <summary>The split mirror's rules (<see cref="TerminalSplitLayout"/>): which sessions each half shows, how wide the halves are, one pane shown, the hint, and where the tab keys act.</summary>
public class TerminalSplitLayoutTests
{
    // Sessions 1 and 2 belong to the left pane, 3 to the right; LastShown says what each pane shows.
    private static readonly TabFacts[] Tabs =
    [
        new(1, 0, true, 5),
        new(2, 0, true, 9),
        new(3, 1, true, 7),
    ];

    private static readonly PaneSpan Left = new(0, 596);
    private static readonly PaneSpan Right = new(604, 596);

    [Fact]
    public void The_split_is_on_when_the_setting_is_on_and_the_dock_sits_under_the_panes()
    {
        Assert.True(TerminalSplitLayout.Active(setting: true, dockUnderPanes: true));
        Assert.False(TerminalSplitLayout.Active(setting: false, dockUnderPanes: true));
        // Beside the panes there is nothing to be under: one view, and the setting stays for the way back.
        Assert.False(TerminalSplitLayout.Active(setting: true, dockUnderPanes: false));
    }

    [Fact]
    public void Each_half_shows_the_session_its_pane_showed_last_and_lists_only_its_own_tabs()
    {
        var halves = TerminalSplitLayout.Halves(Tabs, dual: true, Left, Right, 1200);

        Assert.Equal(2, halves.Count);
        Assert.Equal((0, 2ul), (halves[0].Pane, halves[0].Session));
        Assert.Equal([1ul, 2ul], halves[0].Tabs);
        Assert.Equal((1, 3ul), (halves[1].Pane, halves[1].Session));
        Assert.Equal([3ul], halves[1].Tabs);
        Assert.All(halves, half => Assert.False(half.Empty));
    }

    [Fact]
    public void A_session_that_just_opened_or_was_clicked_is_the_one_its_half_shows()
    {
        // LastShown grows each time a tab is shown: 4 is newest.
        TabFacts[] tabs = [.. Tabs, new(4, 1, true, 12)];
        Assert.Equal(4ul, TerminalSplitLayout.ShownIn(tabs, 1));
        Assert.Equal(2ul, TerminalSplitLayout.ShownIn(tabs, 0));
        // Ties (never shown) go to the newest session.
        Assert.Equal(6ul, TerminalSplitLayout.ShownIn([new TabFacts(5, 0, true, 0), new TabFacts(6, 0, true, 0)], 0));
    }

    [Fact]
    public void An_ended_shell_stays_in_its_half_until_its_tab_closes()
    {
        TabFacts[] tabs = [new(1, 0, true, 3), new(2, 0, false, 8)];

        Assert.Equal(2ul, TerminalSplitLayout.ShownIn(tabs, 0));
    }

    [Fact]
    public void The_halves_are_as_wide_as_the_panes_and_lie_under_them()
    {
        var halves = TerminalSplitLayout.Halves(Tabs, dual: true, new PaneSpan(0, 700), new PaneSpan(708, 492), 1200);

        Assert.Equal((0d, 700d), (halves[0].X, halves[0].Width));
        Assert.Equal((708d, 492d), (halves[1].X, halves[1].Width));
        // The divider is the panes': the first column ends where the left pane does, the gap is theirs.
        Assert.Equal((700d, 8d), TerminalSplitLayout.Columns(halves));
    }

    [Fact]
    public void When_the_panes_divider_moves_the_halves_follow()
    {
        var before = TerminalSplitLayout.Halves(Tabs, dual: true, new PaneSpan(0, 596), new PaneSpan(604, 596), 1200);
        var after = TerminalSplitLayout.Halves(Tabs, dual: true, new PaneSpan(0, 400), new PaneSpan(408, 792), 1200);

        Assert.Equal((596d, 8d), TerminalSplitLayout.Columns(before));
        Assert.Equal((400d, 8d), TerminalSplitLayout.Columns(after));
        Assert.Equal(792d, after[1].Width);
    }

    [Fact]
    public void A_span_that_leaves_the_dock_is_held_inside_it()
    {
        var halves = TerminalSplitLayout.Halves(Tabs, dual: true, new PaneSpan(-3, 500), new PaneSpan(900, 600), 1000);

        Assert.Equal((0d, 500d), (halves[0].X, halves[0].Width));
        Assert.Equal((900d, 100d), (halves[1].X, halves[1].Width));
        // A dock not laid out yet (width 0) still gives a half a width, so nothing divides by zero.
        var unmeasured = TerminalSplitLayout.Halves([], dual: false, Left, Right, 0)[0];
        Assert.Equal((0d, 1d), (unmeasured.X, unmeasured.Width));
    }

    [Fact]
    public void With_one_pane_shown_there_is_one_half_across_the_dock_and_the_right_panes_sessions_wait()
    {
        var halves = TerminalSplitLayout.Halves(Tabs, dual: false, new PaneSpan(0, 1200), Right, 1200);

        var half = Assert.Single(halves);
        Assert.Equal((0, 0d, 1200d, 2ul), (half.Pane, half.X, half.Width, half.Session));
        Assert.Equal([1ul, 2ul], half.Tabs);
        Assert.Equal((1200d, 0d), TerminalSplitLayout.Columns(halves));
        // The right pane's session is still there, and shows again with the second pane.
        Assert.Equal(3ul, TerminalSplitLayout.ShownIn(Tabs, 1));
        Assert.Equal(2, TerminalSplitLayout.Halves(Tabs, dual: true, Left, Right, 1200).Count);
    }

    [Fact]
    public void A_pane_without_a_session_gets_an_empty_half_that_shows_the_hint()
    {
        TabFacts[] onlyLeft = [new(1, 0, true, 1)];
        var halves = TerminalSplitLayout.Halves(onlyLeft, dual: true, Left, Right, 1200);

        Assert.False(halves[0].Empty);
        Assert.True(halves[1].Empty);
        Assert.Null(halves[1].Session);
        Assert.Empty(halves[1].Tabs);
        Assert.Equal("Ctrl+` starts a shell for this pane", TerminalSplitLayout.Hint);
        // No tabs at all: both halves are empty.
        Assert.All(TerminalSplitLayout.Halves([], dual: true, Left, Right, 1200), half => Assert.True(half.Empty));
    }

    [Fact]
    public void Alt_brackets_go_round_the_tabs_of_the_half_that_has_the_keyboard()
    {
        // Left pane: 1 and 2; right pane: 3. The tabs of the other half are not visited.
        Assert.Equal(1ul, TerminalSplitLayout.CycleTarget(Tabs, pane: 0, shown: 2, step: 1));
        Assert.Equal(2ul, TerminalSplitLayout.CycleTarget(Tabs, pane: 0, shown: 1, step: -1));
        Assert.Equal(3ul, TerminalSplitLayout.CycleTarget(Tabs, pane: 1, shown: 3, step: 1));
        // With no split, every tab is in the round.
        Assert.Equal(3ul, TerminalSplitLayout.CycleTarget(Tabs, pane: null, shown: 2, step: 1));
        // A half with no tab has nowhere to go.
        Assert.Null(TerminalSplitLayout.CycleTarget([new TabFacts(1, 0, true, 1)], pane: 1, shown: null, step: 1));
    }

    [Fact]
    public void Ctrl_shift_w_closes_the_shown_tab_of_the_half_that_has_the_keyboard()
    {
        var halves = TerminalSplitLayout.Halves(Tabs, dual: true, Left, Right, 1200);

        Assert.Equal(2ul, TerminalSplitLayout.CloseTarget(halves, keyboardPane: 0));
        Assert.Equal(3ul, TerminalSplitLayout.CloseTarget(halves, keyboardPane: 1));
        // A half that shows the hint has nothing to close, and the other half is left alone.
        var left = TerminalSplitLayout.Halves([new TabFacts(1, 0, true, 1)], dual: true, Left, Right, 1200);
        Assert.Null(TerminalSplitLayout.CloseTarget(left, keyboardPane: 1));
    }

    [Fact]
    public void Closing_the_only_tab_of_a_half_leaves_the_hint_and_the_other_half_alone()
    {
        // The right half has one tab: it closes and the half shows the hint, whatever the left pane has.
        Assert.Null(TerminalSplitLayout.AfterClose(Tabs, pane: 1, closed: 3));
        Assert.Equal(2ul, TerminalSplitLayout.ShownIn(Tabs, 0));
        // A half with two tabs shows the one it showed before.
        Assert.Equal(1ul, TerminalSplitLayout.AfterClose(Tabs, pane: 0, closed: 2));
    }

    [Fact]
    public void Ctrl_shift_t_opens_in_the_half_of_the_active_pane_and_with_one_pane_in_the_left()
    {
        Assert.Equal(0, TerminalSplitLayout.NewTabPane(dual: true, activePane: 0));
        Assert.Equal(1, TerminalSplitLayout.NewTabPane(dual: true, activePane: 1));
        Assert.Equal(0, TerminalSplitLayout.NewTabPane(dual: false, activePane: 1));
    }

    [Fact]
    public void Leaving_the_split_shows_the_session_of_the_half_that_had_the_keyboard()
    {
        Assert.Equal(3ul, TerminalSplitLayout.ShownWhenUnsplit(Tabs, keyboardPane: 1));
        Assert.Equal(2ul, TerminalSplitLayout.ShownWhenUnsplit(Tabs, keyboardPane: 0));
        // The half that had it was empty: the other half's session, else the last tab.
        TabFacts[] onlyLeft = [new(1, 0, true, 4), new(2, 0, true, 6)];
        Assert.Equal(2ul, TerminalSplitLayout.ShownWhenUnsplit(onlyLeft, keyboardPane: 1));
        Assert.Null(TerminalSplitLayout.ShownWhenUnsplit([], keyboardPane: 0));
    }

    [Fact]
    public void Ctrl_backquote_counts_the_active_panes_half_as_showing_its_session()
    {
        // The right half has the keyboard (the shown tab is the right pane's), the left pane is active: the left
        // pane's half shows a session, so Ctrl+` there gives it the keyboard instead of starting a new one.
        Assert.Equal(0, TerminalSplitLayout.SummonShownPane(split: true, Tabs, activePane: 0, shownPane: 1));
        // No split: the shown tab's pane, as before.
        Assert.Equal(1, TerminalSplitLayout.SummonShownPane(split: false, Tabs, activePane: 0, shownPane: 1));
        // The active pane has no session in the split: the shown tab's pane, so the summoning starts one.
        Assert.Equal(0, TerminalSplitLayout.SummonShownPane(split: true, [new TabFacts(1, 0, true, 1)], activePane: 1, shownPane: 0));
    }
}
