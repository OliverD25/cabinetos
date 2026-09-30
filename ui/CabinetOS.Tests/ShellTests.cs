using CabinetOS.Core.Ipc;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Shell;
using CabinetOS.Core.Tabs;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// The shell of Phase 16 (the creator's SHELL_REDESIGN.md; docs/ui.md, "The
/// top row", "The breadcrumb row", "Find in pane", "Quick Open"): what the
/// window decides without a window, so it is tested here.
/// </summary>
public class ShellTests
{
    // ----- The breadcrumb row -----

    [Fact]
    public void The_handout_path_in_a_dual_pane_collapses_to_drive_ellipsis_parent_and_current()
    {
        // SHELL_REDESIGN.md §7: C:\Users\dev\Projects\fileforge in a 380 px pane. The rule counts
        // parts, not pixels, so the width does not change it, and no segment is cut short on its own.
        var segments = Breadcrumbs.Segments(@"C:\Users\dev\Projects\fileforge", dual: true);

        Assert.Equal("C: \u203A \u2026 \u203A Projects \u203A fileforge", Breadcrumbs.Text(segments));
        Assert.Equal([@"C:\", @"C:\Users\dev", @"C:\Users\dev\Projects", @"C:\Users\dev\Projects\fileforge"], segments.Select(s => s.Path));
        Assert.Equal([false, true, false, false], segments.Select(s => s.IsEllipsis));
        Assert.DoesNotContain(segments, s => s.Label.EndsWith('\u2026') && !s.IsEllipsis);
    }

    [Fact]
    public void One_pane_shows_five_parts_whole_and_collapses_from_six()
    {
        var five = Breadcrumbs.Segments(@"C:\Users\dev\Projects\fileforge", dual: false);
        Assert.Equal("C: \u203A Users \u203A dev \u203A Projects \u203A fileforge", Breadcrumbs.Text(five));
        Assert.DoesNotContain(five, s => s.IsEllipsis);

        var six = Breadcrumbs.Segments(@"C:\Users\dev\Projects\fileforge\src", dual: false);
        Assert.Equal("C: \u203A \u2026 \u203A fileforge \u203A src", Breadcrumbs.Text(six));
        // The "…" goes to the last folder it hides.
        Assert.Equal(@"C:\Users\dev\Projects", six[1].Path);
    }

    [Theory]
    [InlineData(@"C:\a\b", true, "C: \u203A a \u203A b")]
    [InlineData(@"C:\a\b\c", true, "C: \u203A \u2026 \u203A b \u203A c")]
    [InlineData(@"C:\", true, "C:")]
    [InlineData(@"D:\", false, "D:")]
    [InlineData(@"C:\Users\", true, "C: \u203A Users")]
    [InlineData(@"C:\Users\dev\Projects\fileforge\", true, "C: \u203A \u2026 \u203A Projects \u203A fileforge")]
    [InlineData(@"\\server\share\a\b", true, "\\\\server \u203A \u2026 \u203A a \u203A b")]
    public void Paths_of_every_length_keep_their_rule(string path, bool dual, string expected) =>
        Assert.Equal(expected, Breadcrumbs.Text(Breadcrumbs.Segments(path, dual)));

    [Fact]
    public void A_path_of_one_part_and_an_empty_path_have_no_ellipsis()
    {
        var root = Assert.Single(Breadcrumbs.Segments(@"C:\", dual: true));
        Assert.Equal(("C:", @"C:\", false), (root.Label, root.Path, root.IsEllipsis));
        Assert.Empty(Breadcrumbs.Segments("", dual: true));
    }

    [Fact]
    public void Forward_is_drawn_at_thirty_percent_without_a_forward_entry()
    {
        var fresh = NavState.From(backCount: 2, forwardCount: 0, locked: false, path: @"C:\Users");
        Assert.Equal((true, false, true), (fresh.Back, fresh.Forward, fresh.Up));
        Assert.Equal(0.3, NavState.Opacity(fresh.Forward));
        Assert.Equal(1.0, NavState.Opacity(fresh.Back));

        // After Back there is a forward entry; a locked tab stays on its folder; a drive's root has no Up.
        Assert.True(NavState.From(1, 1, false, @"C:\Users").Forward);
        Assert.Equal((false, false), (NavState.From(1, 1, true, @"C:\Users").Back, NavState.From(1, 1, true, @"C:\Users").Forward));
        Assert.False(NavState.From(0, 0, false, @"C:\").Up);
    }

    // ----- The top row -----

    // The default look's clusters: menu 36 + icon + pill on the left; five 36 px buttons, a divider and
    // Windows' three 46 px caption buttons on the right.
    private const double DefaultLeftEnd = 8 + 36 + 8 + 16 + 8 + 150;
    private const double DefaultRight = (5 * 36) + 13 + (3 * 46);

    [Fact]
    public void At_924_px_the_command_center_is_at_least_200_px_and_covers_neither_cluster()
    {
        var place = TopRowLayout.Place(924, DefaultLeftEnd, 924 - DefaultRight);

        Assert.True(place.Visible);
        Assert.True(place.Width >= 200, $"{place.Width}");
        Assert.True(place.Left >= DefaultLeftEnd + TopRowLayout.Gap, $"{place.Left}");
        Assert.True(place.Left + place.Width <= 924 - DefaultRight - TopRowLayout.Gap, $"{place.Left + place.Width}");
    }

    [Fact]
    public void Below_640_px_the_command_center_hides()
    {
        Assert.False(TopRowLayout.Place(639, 60, 600).Visible);
        Assert.False(TopRowLayout.Place(400, 10, 390).Visible);
        // At 640 px it shows when the clusters leave room for it.
        Assert.True(TopRowLayout.Place(640, 60, 600).Visible);
    }

    [Fact]
    public void A_wide_window_centres_a_box_of_at_most_380_px_and_a_crowded_one_hides_it()
    {
        var wide = TopRowLayout.Place(1600, DefaultLeftEnd, 1600 - DefaultRight);
        Assert.Equal((380.0, 610.0), (wide.Width, wide.Left));
        Assert.Equal(0.34 * 1000, TopRowLayout.PreferredWidth(1000), 6);
        Assert.Equal((200.0, 380.0), (TopRowLayout.PreferredWidth(300), TopRowLayout.PreferredWidth(3000)));
        // 700 px leaves a narrower box between the clusters; 660 px less than the least width: hidden, nothing overlaps.
        var narrow = TopRowLayout.Place(700, DefaultLeftEnd, 700 - DefaultRight);
        Assert.True(narrow.Visible);
        Assert.True(narrow.Width is >= TopRowLayout.LeastWidth and < 200, $"{narrow.Width}");
        Assert.True(narrow.Left >= DefaultLeftEnd + TopRowLayout.Gap && narrow.Left + narrow.Width <= 700 - DefaultRight - TopRowLayout.Gap);
        Assert.False(TopRowLayout.Place(660, DefaultLeftEnd, 660 - DefaultRight).Visible);
    }

    [Fact]
    public void The_hamburger_lists_its_seven_commands_with_the_registry_s_titles_and_first_keys()
    {
        static CommandInfo Command(string id, string title, params string[] keys) =>
            new(id, "Any", title, keys, keys, new CommandSource("core", null, null), "ui", null, false);
        var registry = new[]
        {
            Command("keys.open", "Open Keyboard Shortcuts", "ctrl+k ctrl+s"),
            Command("tab.new", "New Tab", "ctrl+t"),
            Command("marketplace.browse", "Browse Plugins and Themes", "ctrl+shift+x"),
            Command("file.newFolder", "New Folder", "f7"),
            // Rebound by the user: the menu shows the key in effect.
            Command("search.focus", "Find in Pane", "ctrl+alt+f", "alt+f7"),
            Command("go.toPath", "Go to Path\u2026", "ctrl+l"),
            Command("view.toggleSidebar", "Toggle Sidebar", "ctrl+b"),
            Command("help.about", "About CabinetOS"),
        };

        var menu = ShellMenu.Build(registry);

        Assert.Equal(["tab.new", "file.newFolder", "search.focus", "go.toPath", "view.toggleSidebar", "marketplace.browse", "keys.open"], menu.Select(m => m.CommandId));
        Assert.Equal(["New Tab", "New Folder", "Find in Pane", "Go to Path\u2026", "Toggle Sidebar", "Browse Plugins and Themes", "Open Keyboard Shortcuts"], menu.Select(m => m.Title));
        Assert.Equal(["Ctrl+T", "F7", "Ctrl+Alt+F", "Ctrl+L", "Ctrl+B", "Ctrl+Shift+X", "Ctrl+K Ctrl+S"], menu.Select(m => m.Keys));
        // An older core without one of them: the menu leaves it out.
        Assert.Equal(6, ShellMenu.Build(registry.Where(c => c.Id != "go.toPath")).Count);
    }

    [Fact]
    public void A_gt_typed_in_Quick_Open_switches_to_the_commands()
    {
        Assert.Equal((PaletteMode.Commands, "tab", true), PaletteInput.Read(PaletteMode.Files, ">tab"));
        Assert.Equal((PaletteMode.Commands, "", true), PaletteInput.Read(PaletteMode.Files, ">"));
        Assert.Equal((PaletteMode.Commands, "new tab", true), PaletteInput.Read(PaletteMode.Files, ">  new tab"));
        // Only at the start, only from the files, and the command palette's own text stays as typed.
        Assert.Equal((PaletteMode.Files, "a>b", false), PaletteInput.Read(PaletteMode.Files, "a>b"));
        Assert.Equal((PaletteMode.Commands, ">x", false), PaletteInput.Read(PaletteMode.Commands, ">x"));
        // Backspace in the commands' empty box goes back to the files; with text it stays.
        Assert.Equal(PaletteMode.Files, PaletteInput.AfterBackspace(PaletteMode.Commands, ""));
        Assert.Equal(PaletteMode.Commands, PaletteInput.AfterBackspace(PaletteMode.Commands, "t"));
        Assert.Equal(PaletteMode.Files, PaletteInput.AfterBackspace(PaletteMode.Files, ""));
        Assert.Equal("readme", PaletteInput.FileQuery("  readme "));
        Assert.Null(PaletteInput.FileQuery("   "));
    }

    // ----- Quick Open -----

    private static FileSearchResultsReply Hits(params string[] paths) =>
        new([.. paths.Select(p => new FileHit(p.TrimEnd('\\'), p.EndsWith('\\') ? "directory" : "file"))], FileSearchResultsReply.FromIndex, 120, true);

    [Fact]
    public async Task Quick_Open_asks_the_core_under_the_workspace_and_shows_names_with_their_folders()
    {
        var core = new FakeChannel(_ => Hits(@"C:\repo\docs\ui.md", @"C:\repo\src\ui\", @"C:\repo\README.md"));
        var model = new QuickOpenModel(core);
        model.Open(@"C:\repo");

        Assert.True(await model.SearchAsync("  ui "));

        var sent = Assert.IsType<SearchRequest>(Assert.Single(core.Requests));
        Assert.Equal(("ui", @"C:\repo", 50u), (sent.Query, sent.Root, sent.Limit));
        Assert.Equal(
            [new QuickOpenRow(@"C:\repo\docs\ui.md", "ui.md", @"repo\docs", false), new QuickOpenRow(@"C:\repo\src\ui", "ui", @"repo\src", true), new QuickOpenRow(@"C:\repo\README.md", "README.md", "repo", false)],
            model.Rows);
        Assert.Equal((0, "3 results"), (model.Highlight, model.CountText));
        model.MoveHighlight(5);
        Assert.Equal(@"C:\repo\README.md", model.Highlighted?.Path);
        model.MoveHighlight(-9);
        Assert.Equal(0, model.Highlight);
    }

    [Fact]
    public async Task Quick_Open_sends_nothing_for_blank_text_and_drops_answers_that_came_too_late()
    {
        var answers = new Queue<CoreReply>([Hits(@"C:\a.txt"), new ErrorReply(ErrorCodes.Io, "the index is gone")]);
        var core = new FakeChannel(_ => answers.Dequeue());
        var model = new QuickOpenModel(core);
        model.Open("");

        Assert.True(await model.SearchAsync("   "));
        Assert.Empty(core.Requests);
        Assert.Equal((-1, ""), (model.Highlight, model.CountText));

        // No workspace: every indexed volume. An error shows where the count goes.
        Assert.True(await model.SearchAsync("a"));
        Assert.Null(Assert.IsType<SearchRequest>(core.Requests[0]).Root);
        Assert.True(await model.SearchAsync("ab"));
        Assert.Equal(("the index is gone", -1), (model.CountText, model.Highlight));

        // An answer for a Quick Open that closed meanwhile is dropped.
        var late = new TaskCompletionSource<CoreReply>();
        var slow = new QuickOpenModel(new SlowChannel(late.Task));
        slow.Open(@"C:\");
        var asking = slow.SearchAsync("x");
        slow.Close();
        late.SetResult(Hits(@"C:\x.txt"));
        Assert.False(await asking);
        Assert.Empty(slow.Rows);
    }

    private sealed class SlowChannel(Task<CoreReply> reply) : ICoreChannel
    {
        public Task<CoreReply> RequestAsync(CoreRequest request, CancellationToken cancellationToken = default) => reply;
    }

    // ----- Find in pane -----

    private static SelectionModel Selection(int count, int focus = 0, SelectionStyle style = SelectionStyle.Windows)
    {
        var selection = new SelectionModel();
        selection.SetStyle(style);
        selection.Reset(count, focus);
        return selection;
    }

    [Fact]
    public void A_filter_shows_its_rows_only_and_the_selection_it_hides_comes_back_when_it_ends()
    {
        var selection = Selection(10, focus: 1);
        selection.SetMarks([1, 2, 7], mark: true);

        selection.SetVisible([2, 5, 7, 8]);

        Assert.Equal((4, true), (selection.ShownCount, selection.IsFiltered));
        // Row 1 is hidden: it is kept aside, out of the counts and the commands.
        Assert.Equal([2, 7], selection.Selected);
        Assert.Equal([2, 7], selection.Targets());
        Assert.Equal([1, 2, 7], selection.AllSelected.Order());
        // The hidden focus moved to the nearest shown row.
        Assert.Equal(2, selection.Focus);
        Assert.Equal((5, 1, -1), (selection.IndexAt(1), selection.PositionOf(5), selection.PositionOf(3)));

        // What was selected while it held stays selected after it: Esc restores the list and the selection.
        selection.Toggle(8);
        selection.SetVisible(null);
        Assert.False(selection.IsFiltered);
        Assert.Equal(10, selection.ShownCount);
        Assert.Equal([1, 2, 7, 8], selection.Selected);
    }

    [Fact]
    public void The_cursor_s_own_selection_is_no_mark_it_moves_with_the_cursor_and_is_not_kept_aside()
    {
        // Windows style: the cursor row is selected alone. A filter that hides it moves the cursor
        // and its selection to the nearest shown row; the old row is not selected again afterwards.
        var selection = Selection(10, focus: 3);
        selection.SetVisible([5, 6]);
        Assert.Equal(5, selection.Focus);
        Assert.Equal([5], selection.Selected);
        selection.SetVisible(null);
        Assert.Equal([5], selection.Selected);

        // A shown cursor row stays as it is.
        selection.SetVisible([5, 9]);
        Assert.Equal((5, 2), (selection.Focus, selection.ShownCount));
        Assert.Equal([5], selection.Selected);
    }

    [Fact]
    public void Keys_and_commands_under_a_filter_see_the_shown_rows_only()
    {
        var selection = Selection(10, focus: 0);
        selection.SetVisible([1, 4, 6, 9]);

        // Down goes to the next shown row, End to the last, a page counts shown rows.
        var (down, mode) = PendingCursorKeys.Target(selection, CursorKey.Down, shift: false, ctrl: false, rowsPerPage: 2);
        Assert.Equal((4, SelectMode.Single), (down, mode));
        Assert.Equal(9, PendingCursorKeys.Target(selection, CursorKey.PageDown, false, false, rowsPerPage: 20).Index);
        selection.MoveTo(down, mode);
        // Shift+Down from 4 to 6 selects the two shown rows, not row 5.
        selection.MoveTo(6, SelectMode.Extend);
        Assert.Equal([4, 6], selection.Selected);

        // Ctrl+A marks the shown rows; a hidden row is not selected; Insert moves to the next shown row.
        selection.SelectAll();
        Assert.Equal([1, 4, 6, 9], selection.Selected);
        selection.Clear();
        selection.MoveTo(4, SelectMode.FocusOnly);
        selection.ToggleFocusAndAdvance();
        Assert.Equal(6, selection.Focus);
        Assert.Equal([4], selection.Selected);
        // A move to a hidden row lands on the nearest shown one.
        selection.MoveTo(7, SelectMode.Single);
        Assert.Equal(9, selection.Focus);
        // Num * turns the shown files' marks around (the cursor's own selection is no mark); hidden rows stay unmarked.
        selection.Invert(_ => false);
        Assert.Equal([1, 4, 6, 9], selection.Selected);
        selection.SetVisible(null);
        Assert.Equal([1, 4, 6, 9], selection.Selected);
    }

    [Fact]
    public void Total_Commander_s_marking_passes_over_the_shown_rows_only()
    {
        var selection = Selection(10, focus: 0, SelectionStyle.Commander);
        selection.SetVisible([0, 3, 5, 8]);
        selection.MoveTo(5, SelectMode.MarkPassed);
        Assert.Equal([0, 3], selection.Selected);
    }

    [Fact]
    public void A_filter_with_no_match_keeps_the_selection_and_gives_the_focus_back_when_it_ends()
    {
        var selection = Selection(5, focus: 3);
        selection.SetVisible([]);
        Assert.Equal((0, -1), (selection.ShownCount, selection.Focus));
        Assert.Empty(selection.Targets());
        Assert.Equal(-1, selection.IndexAt(0));
        selection.MoveTo(2, SelectMode.Single);
        Assert.Equal(-1, selection.Focus);

        selection.SetVisible(null);
        Assert.Equal(3, selection.Focus);
        Assert.Equal([3], selection.Selected);
    }

    [Fact]
    public void A_filter_that_ends_on_no_match_gives_back_the_row_the_cursor_was_moved_to_under_it()
    {
        // Find "alpha", Enter on its first match, then a text with no match and Esc: the cursor stays on the match.
        var selection = Selection(10, focus: 0);
        selection.SetVisible([2, 4]);
        selection.MoveTo(4, SelectMode.Single);
        selection.SetVisible([]);
        Assert.Equal(-1, selection.Focus);

        selection.SetVisible(null);
        Assert.Equal(4, selection.Focus);
        Assert.Equal([4], selection.Selected);
    }

    [Fact]
    public void A_new_listing_ends_the_filter()
    {
        var selection = Selection(10);
        selection.SetMarks([4], mark: true);
        selection.SetVisible([1, 2]);
        selection.Restore(10, [1], focus: 1, anchor: 1);
        Assert.False(selection.IsFiltered);
        Assert.Equal([1], selection.Selected);
        selection.SetVisible([1, 2]);
        selection.Reset(3, 0);
        Assert.False(selection.IsFiltered);
    }

    [Fact]
    public async Task The_find_asks_the_core_for_names_that_contain_the_text_and_filters_one_pane_only()
    {
        var left = Selection(6, focus: 0);
        var right = Selection(6, focus: 0);
        var core = new FakeChannel(request => request is MatchEntriesRequest match
            ? new EntryMatchesReply(match.ListingId, 1, [[1, 2], [4, 1]])
            : new ErrorReply("unexpected", ""));
        var find = new PaneFind(core, left);
        var changes = 0;
        find.Changed += () => changes++;

        find.Open();
        Assert.True(find.IsOpen);
        Assert.False(find.IsFiltering);
        Assert.Equal("", find.Count);

        Assert.True(await find.SetQueryAsync("rep", listingId: 7, generation: 1, count: 6));

        var asked = Assert.IsType<MatchEntriesRequest>(Assert.Single(core.Requests));
        Assert.Equal(("*rep*", false, 7UL), (asked.Patterns, asked.FilesOnly, asked.ListingId));
        Assert.Equal([1, 2, 4], left.Visible!);
        Assert.Equal(3, find.Matches);
        Assert.Equal("1 of 3", find.Count);
        left.MoveTo(4, SelectMode.Single);
        Assert.Equal("3 of 3", find.Count);
        // The other pane is untouched.
        Assert.False(right.IsFiltered);
        Assert.Equal(6, right.ShownCount);
        Assert.True(changes >= 2);

        // Esc closes: every row again.
        find.Close();
        Assert.Equal((false, false, 6), (find.IsOpen, left.IsFiltered, left.ShownCount));
    }

    [Fact]
    public async Task A_find_with_no_match_shows_nothing_and_a_late_answer_is_dropped()
    {
        var selection = Selection(4);
        var answers = new Queue<CoreReply>();
        var core = new FakeChannel(_ => answers.Dequeue());
        var find = new PaneFind(core, selection);
        find.Open();

        answers.Enqueue(new EntryMatchesReply(7, 1, []));
        Assert.True(await find.SetQueryAsync("zzz", 7, 1, 4));
        Assert.Equal((0, "0 of 0"), (selection.ShownCount, find.Count));

        // An answer about an older listing (a refresh came meanwhile) changes nothing.
        answers.Enqueue(new EntryMatchesReply(7, 1, [[0, 1]]));
        Assert.False(await find.SetQueryAsync("a", 7, generation: 2, 4));
        Assert.Equal(0, selection.ShownCount);

        // An empty query shows every row; the widget stays open.
        Assert.True(await find.SetQueryAsync("", 7, 2, 4));
        Assert.Equal((false, true), (selection.IsFiltered, find.IsOpen));
    }

    [Theory]
    [InlineData("rep", "*rep*")]
    [InlineData("a;b", "*a?b*")]
    [InlineData("x|y", "*x?y*")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void The_pattern_is_the_query_anywhere_in_the_name(string? query, string? pattern) =>
        Assert.Equal(pattern, PaneFind.Pattern(query));

    // ----- Tabs -----

    [Fact]
    public void A_tab_behind_keeps_its_folder_selection_scroll_and_find_text()
    {
        var first = new PaneTab(@"C:\work");
        var strip = new TabStrip(first);
        // The pane parks its state in the tab it leaves (PaneModel.CaptureInto).
        first.CursorName = "report.txt";
        first.MarkedNames = ["a.txt", "report.txt"];
        first.ScrollOffset = 240;
        first.FindQuery = "rep";

        strip.Add(first.Duplicate(@"C:\other"));
        Assert.Equal(1, strip.ActiveIndex);
        Assert.Null(strip.Active.FindQuery);
        Assert.Equal(0, strip.Active.ScrollOffset);
        strip.Select(0);

        Assert.Same(first, strip.Active);
        Assert.Equal((@"C:\work", "report.txt", 240.0, "rep"), (first.Path, first.CursorName, first.ScrollOffset, first.FindQuery));
        Assert.Equal(["a.txt", "report.txt"], first.MarkedNames);
    }

    [Fact]
    public void Closing_the_last_tab_changes_nothing()
    {
        var strip = new TabStrip(new PaneTab(@"C:\"));
        Assert.Null(strip.Remove(0));
        Assert.Equal(1, strip.Count);
    }
}

/// <summary>The keys of the redesign, from the real core (docs/keybindings.md); runs when the core is built.</summary>
[Collection(HandleTests.Name)]
public class ShellKeysEndToEndTests
{
    [Fact]
    public async Task The_real_core_seeds_the_redesign_s_keys()
    {
        var coreExe = EndToEndTests.FindCoreOrSkip();
        var root = Repo.NewTempFolder("e2e-shell-keys");
        try
        {
            await using var core = await EndToEndTests.StartCoreAsync(coreExe, root);
            await core.Client.HelloAsync();
            var commands = (await core.Client.RequestAsync<CommandsReply>(new ListCommandsRequest())).Commands.ToDictionary(c => c.Id);
            if (!commands.ContainsKey("quickOpen.show"))
            {
                Assert.Skip("This core is older than Phase 16: build it again.");
            }
            Assert.Equal(["ctrl+p"], commands["quickOpen.show"].Keys);
            Assert.Equal(["ctrl+comma"], commands["settings.open"].Keys);
            Assert.Empty(commands["menu.show"].Keys);
            Assert.Equal(Enumerable.Range(1, 9).Select(n => $"ctrl+{n}"), commands["tab.select"].Keys);
            Assert.Equal(("filesView", "ui"), (commands["tab.select"].When, commands["tab.select"].Target));
            Assert.Equal(["ctrl+alt+p"], commands["terminal.insertPath"].Keys);
            Assert.Equal(["ctrl+f", "alt+f7"], commands["search.focus"].Keys);
            Assert.Equal("Find in Pane", commands["search.focus"].Title);
            Assert.Equal(["ctrl+t"], commands["tab.new"].Keys);
            Assert.Equal(["ctrl+w"], commands["tab.close"].Keys);
            Assert.Equal(["ctrl+tab"], commands["tab.next"].Keys);
            Assert.Equal(["ctrl+shift+tab"], commands["tab.previous"].Keys);
            Assert.Equal(["alt+left"], commands["go.back"].Keys);
            Assert.Equal(["alt+right"], commands["go.forward"].Keys);
            Assert.Equal(["ctrl+shift+p"], commands["palette.show"].Keys);

            // The keymap the window gets has them, and Ctrl+P runs Quick Open wherever the keyboard is.
            var keymap = Keymap.From((await core.Client.RequestAsync<KeymapReply>(new GetKeymapRequest())).ToData());
            Assert.Equal(("ctrl+p", (string?)null), (keymap.FirstFor("quickOpen.show")!.Keys.ToString(), keymap.FirstFor("quickOpen.show")!.When));
            Assert.Equal("ctrl+comma", keymap.FirstFor("settings.open")!.Keys.ToString());
            await core.ShutdownAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }
    }
}
