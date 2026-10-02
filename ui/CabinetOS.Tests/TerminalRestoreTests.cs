using System.Text.Json;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Settings;
using CabinetOS.Core.Terminal;

namespace CabinetOS.Tests;

/// <summary>
/// The rules of the terminal's restoration after a restart (<see cref="TerminalRestore"/>, unit 5 of the terminal sprint): whether
/// to restore, which sessions to start from the saved layout and the profiles that exist now, what to try again when the core
/// refuses one, and which tabs come to the front.
/// </summary>
public class TerminalRestoreTests
{
    private const string Home = @"C:\Users\me";

    private static readonly string[] Profiles = ["pwsh", "cmd", "wsl"];

    private static SavedSession Session(string profile, string? folder, int pane, TerminalMode mode = TerminalMode.Locked) => new(profile, folder, pane, mode);

    private static TerminalLayout Layout(IReadOnlyList<SavedSession> items, int? front = null, int? left = null, int? right = null) => new(items, front, left, right);

    // ----- Whether to restore -----

    [Fact]
    public void The_restore_setting_is_read_from_the_terminal_section_and_is_on_unless_it_says_false()
    {
        using var off = JsonDocument.Parse("""{"terminal":{"restore":false}}""");
        Assert.False(UiSettings.FromConfig(off.RootElement).TerminalRestore);
        using var on = JsonDocument.Parse("""{"terminal":{"restore":true,"split":true}}""");
        Assert.True(UiSettings.FromConfig(on.RootElement).TerminalRestore);
        // Missing, or of the wrong kind: the default, which brings the tabs back.
        using var missing = JsonDocument.Parse("""{"terminal":{"split":false}}""");
        Assert.True(UiSettings.FromConfig(missing.RootElement).TerminalRestore);
        using var odd = JsonDocument.Parse("""{"terminal":{"restore":"no"}}""");
        Assert.True(UiSettings.FromConfig(odd.RootElement).TerminalRestore);
        Assert.True(UiSettings.Defaults.TerminalRestore);
    }

    [Fact]
    public void Saved_sessions_come_back_when_the_setting_allows_and_there_are_any()
    {
        var saved = Layout([Session("cmd", @"E:\x", 0)]);

        Assert.Equal(RestoreKind.Restore, TerminalRestore.Decide(true, 0, 0, saved).Kind);
        var off = TerminalRestore.Decide(false, 0, 0, saved);
        Assert.Equal(RestoreKind.Nothing, off.Kind);
        Assert.Contains("terminal.restore", off.Reason);
        var none = TerminalRestore.Decide(true, 0, 0, TerminalLayout.Empty);
        Assert.Equal(RestoreKind.Nothing, none.Kind);
        Assert.Contains("no sessions", none.Reason);
    }

    [Fact]
    public void The_core_s_own_sessions_win_over_the_saved_layout_and_a_shown_session_means_nothing_to_restore()
    {
        var saved = Layout([Session("cmd", @"E:\x", 0), Session("pwsh", @"E:\y", 1)]);

        // The core stayed while the window restarted: those are shown, whatever the setting says, and nothing is read from the file.
        Assert.Equal(RestoreKind.Adopt, TerminalRestore.Decide(true, 0, 2, saved).Kind);
        Assert.Equal(RestoreKind.Adopt, TerminalRestore.Decide(false, 0, 1, saved).Kind);
        Assert.Equal(RestoreKind.Adopt, TerminalRestore.Decide(true, 0, 1, TerminalLayout.Empty).Kind);
        // A session the window shows already: the dock is not first shown by a restart.
        Assert.Equal(RestoreKind.Nothing, TerminalRestore.Decide(true, 1, 0, saved).Kind);
        Assert.Equal(RestoreKind.Nothing, TerminalRestore.Decide(true, 1, 3, saved).Kind);
    }

    // ----- Which sessions to start -----

    [Fact]
    public void The_sessions_start_in_the_saved_order_with_their_profile_folder_pane_and_mode()
    {
        var saved = Layout([
            Session("pwsh", @"E:\work", 0, TerminalMode.Linked),
            Session("cmd", @"D:\data", 1),
            Session("wsl", @"C:\Users\me\src", 0),
        ]);

        var steps = TerminalRestore.Plan(saved, Profiles, "pwsh", Home);

        Assert.Equal([0, 1, 2], steps.Select(s => s.Index));
        Assert.Equal(["pwsh", "cmd", "wsl"], steps.Select(s => s.Profile));
        Assert.Equal([@"E:\work", @"D:\data", @"C:\Users\me\src"], steps.Select(s => s.Folder));
        Assert.Equal([0, 1, 0], steps.Select(s => s.Pane));
        Assert.Equal([TerminalMode.Linked, TerminalMode.Locked, TerminalMode.Locked], steps.Select(s => s.Mode));
        Assert.All(steps, s => Assert.Empty(s.Fallbacks));
    }

    [Fact]
    public void A_profile_that_no_longer_exists_falls_back_to_the_default_profile_and_says_so()
    {
        var steps = TerminalRestore.Plan(Layout([Session("fish", @"E:\x", 1, TerminalMode.Linked)]), Profiles, "cmd", Home);

        var step = Assert.Single(steps);
        Assert.Equal(("cmd", @"E:\x", 1, TerminalMode.Linked), (step.Profile, step.Folder, step.Pane, step.Mode));
        Assert.Equal([new Fallback("profile", "fish", "cmd")], step.Fallbacks);
        // A profile name is the core's exact word: another case is another name.
        Assert.Equal("cmd", Assert.Single(TerminalRestore.Plan(Layout([Session("PWSH", null, 0)]), Profiles, "cmd", Home)).Profile);
    }

    [Fact]
    public void A_session_saved_without_a_folder_starts_in_the_home_folder()
    {
        var steps = TerminalRestore.Plan(Layout([Session("cmd", null, 0), Session("cmd", "  ", 1)]), Profiles, "cmd", Home);

        Assert.Equal([Home, Home], steps.Select(s => s.Folder));
        // Nothing was lost: no folder was saved, so no folder fell back.
        Assert.All(steps, s => Assert.Empty(s.Fallbacks));
    }

    [Fact]
    public void No_more_sessions_start_than_the_core_keeps()
    {
        var many = Layout([.. Enumerable.Range(0, 40).Select(i => Session("cmd", $@"E:\{i}", i % 2))]);

        var steps = TerminalRestore.Plan(many, Profiles, "cmd", Home);

        Assert.Equal(TerminalRestore.MaxSessions, steps.Count);
        Assert.Equal(32, TerminalRestore.MaxSessions);
        Assert.Equal(31, steps[^1].Index);
        Assert.Empty(TerminalRestore.Plan(TerminalLayout.Empty, Profiles, "cmd", Home));
    }

    // ----- What to try when the core refuses one -----

    [Fact]
    public void A_folder_the_core_refuses_falls_back_to_the_home_folder()
    {
        var step = TerminalRestore.Plan(Layout([Session("cmd", @"E:\gone", 1)]), Profiles, "cmd", Home)[0];

        var again = TerminalRestore.Retry(step, ErrorCodes.SpawnFailed, Home);

        Assert.NotNull(again);
        Assert.Equal((Home, "cmd", 1), (again.Folder, again.Profile, again.Pane));
        Assert.Equal([new Fallback("folder", @"E:\gone", Home)], again.Fallbacks);
        // The home folder that fails too is the end: the session is skipped, not asked for again.
        Assert.Null(TerminalRestore.Retry(again, ErrorCodes.SpawnFailed, Home));
        Assert.Null(TerminalRestore.Retry(again, ErrorCodes.SpawnFailed, Home.ToLowerInvariant()));
    }

    [Fact]
    public void A_profile_that_cannot_be_linked_now_starts_locked()
    {
        var step = TerminalRestore.Plan(Layout([Session("cmd", @"E:\x", 0, TerminalMode.Linked)]), Profiles, "cmd", Home)[0];

        var again = TerminalRestore.Retry(step, ErrorCodes.NotLinkable, Home);

        Assert.NotNull(again);
        Assert.Equal((TerminalMode.Locked, @"E:\x"), (again.Mode, again.Folder));
        Assert.Equal([new Fallback("mode", "linked", "locked")], again.Fallbacks);
        // Locked already: the refusal cannot be about the mode, and nothing else is tried for it.
        Assert.Null(TerminalRestore.Retry(again, ErrorCodes.NotLinkable, Home));
    }

    [Fact]
    public void A_gone_folder_and_a_gone_link_are_both_dealt_with_and_the_asking_ends()
    {
        var step = TerminalRestore.Plan(Layout([Session("fish", @"E:\gone", 0, TerminalMode.Linked)]), Profiles, "cmd", Home)[0];

        var locked = TerminalRestore.Retry(step, ErrorCodes.NotLinkable, Home)!;
        var home = TerminalRestore.Retry(locked, ErrorCodes.SpawnFailed, Home)!;

        Assert.Equal(["profile", "mode", "folder"], home.Fallbacks.Select(f => f.What));
        Assert.Null(TerminalRestore.Retry(home, ErrorCodes.SpawnFailed, Home));
        Assert.Null(TerminalRestore.Retry(home, ErrorCodes.NotLinkable, Home));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("page")]
    [InlineData("unknown_profile")]
    [InlineData("internal")]
    public void Any_other_refusal_skips_the_session(string code)
    {
        var step = TerminalRestore.Plan(Layout([Session("cmd", @"E:\x", 0, TerminalMode.Linked)]), Profiles, "cmd", Home)[0];

        Assert.Null(TerminalRestore.Retry(step, code, Home));
    }

    // ----- Which tabs come to the front -----

    [Fact]
    public void Each_pane_s_front_tab_is_shown_first_and_the_saved_front_tab_last()
    {
        var saved = Layout(
            [Session("cmd", null, 0), Session("cmd", null, 0), Session("cmd", null, 1), Session("cmd", null, 1)],
            front: 2, left: 0, right: 2);

        var order = TerminalRestore.ShowOrder(saved, [(0, 0), (1, 0), (2, 1), (3, 1)]);

        // The left pane's own front tab is the first session, not the later one; the one view ends on the right pane's.
        Assert.Equal([0, 2, 2], order);
    }

    [Fact]
    public void A_front_tab_that_did_not_start_gives_way_to_its_pane_s_last_session_that_did()
    {
        var saved = Layout(
            [Session("cmd", null, 0), Session("cmd", null, 0), Session("cmd", null, 1), Session("cmd", null, 1)],
            front: 3, left: 1, right: 3);

        // Sessions 1 and 3 failed to start: the left pane shows its remaining one, and the right pane too.
        var order = TerminalRestore.ShowOrder(saved, [(0, 0), (2, 1)]);

        Assert.Equal([0, 2, 2], order);
        // Nothing saved as front: the last session that started is in front.
        var unsaved = TerminalRestore.ShowOrder(Layout([Session("cmd", null, 0), Session("cmd", null, 1)]), [(0, 0), (1, 1)]);
        Assert.Equal([0, 1, 1], unsaved);
        // A pane with no session left is not shown at all.
        Assert.Equal([0, 0], TerminalRestore.ShowOrder(saved, [(0, 0)]));
    }

    [Fact]
    public void A_saved_pane_front_that_belongs_to_the_other_pane_is_not_taken()
    {
        // A hand edit that says the left pane's front tab is a right-pane session.
        var saved = Layout([Session("cmd", null, 0), Session("cmd", null, 1)], front: 0, left: 1);

        var order = TerminalRestore.ShowOrder(saved, [(0, 0), (1, 1)]);

        Assert.Equal([0, 1, 0], order);
    }

    [Fact]
    public void With_no_session_started_nothing_is_shown()
    {
        Assert.Empty(TerminalRestore.ShowOrder(Layout([Session("cmd", null, 0)], front: 0), []));
    }
}
