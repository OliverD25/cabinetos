using System.Collections.Specialized;
using System.Text.Json;
using CabinetOS.Core.Settings;
using CabinetOS.Core.Sidebar;
using CabinetOS.Core.Tools;
using CabinetOS.Tests.Support;
using Json.Schema;

namespace CabinetOS.Tests;

/// <summary>The activity rail's decisions (docs/ui.md, "The activity rail and the sidebar").</summary>
public class RailTests
{
    private static readonly RailTool Notes = new("notes", "Quick Notes");
    private static readonly RailTool Bookmarks = new("bookmarks", "Bookmarks");

    [Fact]
    public void The_default_rail_is_explorer_search_marketplace_terminal_and_then_the_tools()
    {
        var rail = new RailModel([Notes, Bookmarks], []);

        Assert.Equal(["explorer", "search", "marketplace", "terminal", "notes", "bookmarks"], rail.Order);
        Assert.True(rail.IsDefaultOrder);
        Assert.Equal(["Explorer", "Search", "Marketplace", "Terminal", "Quick Notes", "Bookmarks"], rail.Buttons.Select(b => b.Title));
        Assert.Equal([RailKind.Explorer, RailKind.Search, RailKind.Marketplace, RailKind.Terminal, RailKind.Tool, RailKind.Tool], rail.Buttons.Select(b => b.Kind));
        Assert.Equal(["\uE8B7", "\uE721", "\uE719", "\uE756", "", ""], rail.Buttons.Select(b => b.Glyph));
        Assert.Equal("QN", rail.Find("notes")!.Initials);
        Assert.Equal("notes", rail.Find("notes")!.ToolId);
        Assert.Null(rail.Find("explorer")!.ToolId);
    }

    [Fact]
    public void A_tool_named_like_a_built_in_button_gets_no_button()
    {
        var rail = new RailModel([new RailTool("search", "Fake Search"), new RailTool("terminal", "Fake Terminal"), Notes], []);

        Assert.Equal(["explorer", "search", "marketplace", "terminal", "notes"], rail.Order);
        Assert.Equal(RailKind.Search, rail.Find("search")!.Kind);
    }

    [Fact]
    public void The_saved_order_comes_first_and_what_it_does_not_name_follows_in_the_default_order()
    {
        var rail = new RailModel([Notes, Bookmarks], ["terminal", "bookmarks", "gone", "explorer", "terminal"]);

        // Unknown IDs and repeats are dropped; search, marketplace and notes were not named.
        Assert.Equal(["terminal", "bookmarks", "explorer", "search", "marketplace", "notes"], rail.Order);
        Assert.False(rail.IsDefaultOrder);

        rail.SetOrder([]);
        Assert.True(rail.IsDefaultOrder);
    }

    [Fact]
    public void Tools_read_after_the_start_get_their_buttons_where_the_saved_order_puts_them_and_the_badges_stay()
    {
        // The window reads the tools once, after it started: the saved order names a tool that is not known yet.
        var rail = new RailModel([], ["notes", "terminal"]);
        rail.SetBadge("notes", "dot");
        Assert.Equal(["terminal", "explorer", "search", "marketplace"], rail.Order);
        var changes = 0;
        rail.Changed += () => changes++;

        rail.SetTools([Notes, Bookmarks]);

        Assert.Equal(["notes", "terminal", "explorer", "search", "marketplace", "bookmarks"], rail.Order);
        Assert.Equal("dot", rail.BadgeOf("notes"));
        Assert.Equal(1, changes);
        Assert.False(rail.IsDefaultOrder);
    }

    [Fact]
    public void A_button_moves_one_place_and_stops_at_the_ends()
    {
        var rail = new RailModel([Notes], []);
        var changes = 0;
        rail.Changed += () => changes++;

        Assert.False(rail.Move("explorer", -1));
        Assert.False(rail.Move("notes", 1));
        Assert.False(rail.Move("nothing", 1));
        Assert.True(rail.Move("terminal", -1));
        Assert.Equal(["explorer", "search", "terminal", "marketplace", "notes"], rail.Order);
        Assert.Equal(1, changes);
    }

    [Theory]
    // (the button, sidebar open, view on show, marketplace open) -> action, view, leave marketplace
    [InlineData("explorer", false, "explorer", false, RailAction.ShowView, "explorer", false)]
    [InlineData("explorer", true, "explorer", false, RailAction.CloseSidebar, null, false)]
    [InlineData("search", true, "explorer", false, RailAction.ShowView, "search", false)]
    [InlineData("notes", true, "notes", false, RailAction.CloseSidebar, null, false)]
    [InlineData("notes", false, "notes", false, RailAction.ShowView, "notes", false)]
    [InlineData("explorer", true, "explorer", true, RailAction.ShowView, "explorer", true)]
    [InlineData("marketplace", true, "explorer", false, RailAction.ToggleMarketplace, null, false)]
    [InlineData("marketplace", true, "explorer", true, RailAction.ToggleMarketplace, null, false)]
    [InlineData("terminal", false, "explorer", false, RailAction.ToggleTerminal, null, false)]
    public void A_click_closes_the_sidebar_on_the_view_on_show_and_switches_to_any_other(
        string id, bool open, string shown, bool marketplace, RailAction action, string? view, bool leave)
    {
        var rail = new RailModel([Notes], []);

        var click = RailModel.Click(rail.Find(id)!, open, shown, marketplace);

        Assert.Equal(new RailClick(action, view, leave), click);
    }

    [Fact]
    public void The_pill_is_on_the_view_on_show_the_open_marketplace_and_the_shown_terminal()
    {
        var rail = new RailModel([Notes], []);
        bool Active(string id, bool open, string shown, bool market = false, bool terminal = false) =>
            RailModel.IsActive(rail.Find(id)!, open, shown, market, terminal);

        Assert.True(Active("explorer", true, "explorer"));
        Assert.False(Active("explorer", false, "explorer"));
        Assert.False(Active("search", true, "explorer"));
        Assert.True(Active("notes", true, "notes"));
        // The marketplace takes the main column, and the view buttons give up their pill while it is open.
        Assert.False(Active("explorer", true, "explorer", market: true));
        Assert.True(Active("marketplace", true, "explorer", market: true));
        Assert.False(Active("marketplace", true, "explorer"));
        Assert.True(Active("terminal", false, "explorer", terminal: true));
        Assert.False(Active("terminal", true, "explorer"));
    }

    [Fact]
    public void A_badge_is_set_changed_and_cleared_and_the_model_says_when_it_changed()
    {
        var rail = new RailModel([Notes], []);
        var changes = 0;
        rail.Changed += () => changes++;

        Assert.True(rail.SetBadge("notes", "dot"));
        Assert.False(rail.SetBadge("notes", "dot"));
        Assert.True(rail.SetBadge("notes", "spinner"));
        Assert.Equal("spinner", rail.BadgeOf("notes"));
        Assert.Null(rail.BadgeOf("search"));
        Assert.True(rail.SetBadge("notes", null));
        Assert.False(rail.SetBadge("notes", null));
        Assert.Empty(rail.Badges);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void A_plugin_cannot_fill_memory_with_badges_for_views_that_do_not_exist()
    {
        var rail = new RailModel([], []);

        for (var i = 0; i < RailModel.MaxBadges + 10; i++)
        {
            rail.SetBadge($"view-{i}", "dot");
        }

        Assert.Equal(RailModel.MaxBadges, rail.Badges.Count);
        // One that is there can still change or go.
        Assert.True(rail.SetBadge("view-0", "spinner"));
        Assert.True(rail.SetBadge("view-0", null));
        Assert.True(rail.SetBadge("view-new", "dot"));
    }

    [Theory]
    [InlineData("Markdown Preview", "MP")]
    [InlineData("Notes", "NO")]
    [InlineData("Git Lens Extra", "GL")]
    [InlineData("git-lens tool", "GT")]
    [InlineData("X", "X")]
    [InlineData("   ", "?")]
    public void A_tool_without_a_glyph_shows_two_letters(string name, string initials) =>
        Assert.Equal(initials, RailModel.InitialsOf(name));
}

/// <summary>The divider's snap and the remembered width.</summary>
public class SidebarSizingTests
{
    [Fact]
    public void Dragging_under_150_pixels_closes_the_sidebar_and_150_or_more_keeps_it_open()
    {
        Assert.Equal(new SidebarDrag(true, 150), SidebarSizing.Drag(149.9, 1400));
        Assert.Equal(new SidebarDrag(true, 150), SidebarSizing.Drag(0, 1400));
        Assert.Equal(new SidebarDrag(false, 150), SidebarSizing.Drag(150, 1400));
        Assert.Equal(new SidebarDrag(false, 300), SidebarSizing.Drag(300, 1400));
    }

    [Theory]
    [InlineData(1400, 480)]
    [InlineData(900, 450)]
    [InlineData(600, 300)]
    // A window narrower than 300 px still gives the snap width, so the limits never cross.
    [InlineData(200, 150)]
    public void The_widest_sidebar_is_half_the_window_and_at_most_480(double window, double widest)
    {
        Assert.Equal(widest, SidebarSizing.MaxFor(window));
        Assert.Equal(widest, SidebarSizing.Drag(2000, window).Width);
    }

    [Fact]
    public void The_shown_width_is_the_remembered_one_kept_within_the_limits_or_the_design_width()
    {
        Assert.Equal(224, SidebarSizing.Effective(null, 224, 1400));
        Assert.Equal(320, SidebarSizing.Effective(320, 224, 1400));
        Assert.Equal(150, SidebarSizing.Effective(20, 224, 1400));
        Assert.Equal(300, SidebarSizing.Effective(450, 224, 600));
    }

    [Fact]
    public void The_setting_is_whole_pixels_within_the_limits()
    {
        Assert.Equal(233U, SidebarSizing.ToSetting(232.6));
        Assert.Equal(150U, SidebarSizing.ToSetting(10));
        Assert.Equal(480U, SidebarSizing.ToSetting(9000));
    }
}

/// <summary>Which hidden web page of the sidebar stays awake.</summary>
public class WarmPagesTests
{
    private static PageStep Resume(string page) => new(page, PageChange.Resume);

    private static PageStep Suspend(string page) => new(page, PageChange.Suspend);

    [Fact]
    public void The_page_hidden_last_stays_warm_and_the_ones_before_it_are_suspended()
    {
        var pages = new WarmPages();

        Assert.Empty(pages.Show("a"));
        Assert.Empty(pages.Show("b"));
        Assert.Equal("a", pages.Warm);

        Assert.Equal([Suspend("a")], pages.Show("c"));
        Assert.Equal("b", pages.Warm);
        Assert.True(pages.IsSuspended("a"));
        Assert.False(pages.IsSuspended("b"));
    }

    [Fact]
    public void A_suspended_page_wakes_first_when_it_comes_back()
    {
        var pages = new WarmPages();
        pages.Show("a");
        pages.Show("b");
        pages.Show("c");

        Assert.Equal([Resume("a"), Suspend("b")], pages.Show("a"));
        Assert.Equal("c", pages.Warm);
        Assert.False(pages.IsSuspended("a"));
    }

    [Fact]
    public void A_native_view_hides_the_page_that_was_on_show_and_it_becomes_the_warm_one()
    {
        var pages = new WarmPages();
        pages.Show("a");
        pages.Show("b");

        // Explorer (no page) comes on show: b is hidden and warm; a, hidden before it, is suspended.
        Assert.Equal([Suspend("a")], pages.Show(null));
        Assert.Equal("b", pages.Warm);
        Assert.Null(pages.Shown);

        // b comes back without a wake-up: it was never suspended.
        Assert.Empty(pages.Show("b"));
        Assert.Null(pages.Warm);
        Assert.Empty(pages.Show("b"));
    }

    [Fact]
    public void A_page_that_was_closed_needs_no_waking_and_is_not_kept_warm()
    {
        var pages = new WarmPages();
        pages.Show("a");
        pages.Show("b");
        pages.Forget("a");

        Assert.Null(pages.Warm);
        Assert.Empty(pages.Show("c"));
        Assert.Equal("b", pages.Warm);
    }
}

/// <summary>The folder tree: lazy reads, one per open row, a reveal that leaves the other rows alone.</summary>
public class FolderTreeTests
{
    // A source that answers from a table, records every request and its token, and can hold an answer back.
    private sealed class FakeFolders : IFolderSource
    {
        private readonly Dictionary<string, List<string[]>> _answers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TaskCompletionSource<FolderListing>> _held = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Asked { get; } = [];

        public Dictionary<string, CancellationToken> Tokens { get; } = new(StringComparer.OrdinalIgnoreCase);

        // The answers to the 1st, 2nd... request for the path; the last one repeats.
        public FakeFolders AddAnswers(string path, params string[][] answers)
        {
            _answers[path] = [.. answers];
            return this;
        }

        public FakeFolders Add(string path, params string[] names) => AddAnswers(path, names);

        public TaskCompletionSource<FolderListing> Hold(string path) =>
            _held[path] = new TaskCompletionSource<FolderListing>(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<FolderListing> ListAsync(string path, CancellationToken cancellationToken)
        {
            var times = Asked.Count(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            Asked.Add(path);
            Tokens[path] = cancellationToken;
            if (_held.Remove(path, out var held))
            {
                using var registration = cancellationToken.Register(() => held.TrySetCanceled(cancellationToken));
                return await held.Task;
            }
            return _answers.TryGetValue(path, out var answers)
                ? new FolderListing(answers[Math.Min(times, answers.Count - 1)])
                : FolderListing.Failed("Access is denied.");
        }
    }

    private static FolderTreeModel Tree(FakeFolders folders)
    {
        var tree = new FolderTreeModel(folders);
        tree.SetRoots([(@"C:\", "C:"), (@"D:\", "D:")]);
        return tree;
    }

    private static string[] Names(FolderTreeModel tree) => tree.Rows.Select(r => new string(' ', r.Depth * 2) + r.Name).ToArray();

    private static FakeFolders Disk() => new FakeFolders()
        .Add(@"C:\", "Users", "Windows")
        .Add(@"C:\Users", "Admin", "Public")
        .Add(@"C:\Users\Admin", "Desktop", "Documents")
        .Add(@"C:\Windows", "System32");

    [Fact]
    public void The_drives_show_and_nothing_is_read_until_a_row_opens()
    {
        var folders = Disk();
        var tree = Tree(folders);

        Assert.Equal(["C:", "D:"], Names(tree));
        Assert.Empty(folders.Asked);
        Assert.Equal(0, tree.Requests);
        Assert.All(tree.Rows, r => Assert.True(r.HasChildren && !r.IsExpanded));
        Assert.Equal("\uE76C", tree.Rows[0].ChevronGlyph);
    }

    [Fact]
    public async Task Opening_a_row_reads_its_folder_once_and_shows_the_sub_folders_under_it()
    {
        var folders = Disk();
        var tree = Tree(folders);

        await tree.ExpandAsync(tree.Rows[0]);
        Assert.Equal(["C:", "  Users", "  Windows", "D:"], Names(tree));
        Assert.Equal([@"C:\"], folders.Asked);
        Assert.False(tree.Rows[0].IsLoading);
        Assert.Equal("\uE70D", tree.Rows[0].ChevronGlyph);

        await tree.ExpandAsync(tree.Rows[1]);
        Assert.Equal(["C:", "  Users", "    Admin", "    Public", "  Windows", "D:"], Names(tree));
        Assert.Equal(new[] { @"C:\", @"C:\Users" }, folders.Asked);
        Assert.Equal(@"C:\Users\Admin", tree.Rows[2].Path);
        Assert.Equal(2, tree.Requests);

        // Opening an open row asks nothing more.
        await tree.ExpandAsync(tree.Rows[1]);
        Assert.Equal(2, tree.Requests);
    }

    [Fact]
    public async Task A_folder_with_thousands_of_sub_folders_opens_and_closes_with_one_change_of_the_rows_each()
    {
        var folders = new FakeFolders().Add(@"C:\", Enumerable.Range(0, 3000).Select(i => $"folder{i:D4}").ToArray());
        var tree = Tree(folders);
        var changes = new List<NotifyCollectionChangedAction>();
        tree.Rows.CollectionChanged += (_, e) => changes.Add(e.Action);

        await tree.ExpandAsync(tree.Rows[0]);

        Assert.Equal(3002, tree.Rows.Count);
        Assert.Equal([NotifyCollectionChangedAction.Add], changes);
        changes.Clear();
        tree.Collapse(tree.Rows[0]);
        Assert.Equal(2, tree.Rows.Count);
        Assert.Equal([NotifyCollectionChangedAction.Remove], changes);
    }

    [Fact]
    public async Task Closing_a_row_drops_every_row_under_it_and_opening_it_again_shows_the_open_ones_at_once()
    {
        var folders = Disk();
        var tree = Tree(folders);
        await tree.ExpandAsync(tree.Rows[0]);
        await tree.ExpandAsync(tree.Rows[1]);

        tree.Collapse(tree.Rows[0]);
        Assert.Equal(["C:", "D:"], Names(tree));

        // The answer is held back: the rows already known show while the folder is read again (one request).
        var held = folders.Hold(@"C:\");
        var reopening = tree.ExpandAsync(tree.Rows[0]);
        Assert.Equal(["C:", "  Users", "    Admin", "    Public", "  Windows", "D:"], Names(tree));
        Assert.True(tree.Rows[0].IsLoading);
        held.SetResult(new FolderListing(["Users", "Windows"]));
        await reopening;

        Assert.Equal(["C:", "  Users", "    Admin", "    Public", "  Windows", "D:"], Names(tree));
        Assert.Equal(3, tree.Requests);
        Assert.False(tree.Rows[0].IsLoading);
    }

    [Fact]
    public async Task A_row_closed_before_the_answer_comes_abandons_its_request()
    {
        var folders = Disk();
        var tree = Tree(folders);
        var held = folders.Hold(@"C:\");

        var opening = tree.ExpandAsync(tree.Rows[0]);
        Assert.True(tree.Rows[0].IsLoading);
        Assert.False(folders.Tokens[@"C:\"].IsCancellationRequested);

        tree.Collapse(tree.Rows[0]);

        Assert.True(folders.Tokens[@"C:\"].IsCancellationRequested);
        Assert.False(tree.Rows[0].IsLoading);
        await opening;
        Assert.Equal(["C:", "D:"], Names(tree));
        Assert.True(held.Task.IsCanceled);
        Assert.True(tree.Rows[0].HasChildren);
    }

    [Fact]
    public async Task Closing_a_row_abandons_the_requests_of_the_rows_under_it_too()
    {
        var folders = Disk();
        var tree = Tree(folders);
        await tree.ExpandAsync(tree.Rows[0]);
        folders.Hold(@"C:\Users");
        _ = tree.ExpandAsync(tree.Rows[1]);
        Assert.True(tree.Rows[1].IsLoading);

        tree.Collapse(tree.Rows[0]);

        Assert.True(folders.Tokens[@"C:\Users"].IsCancellationRequested);
        Assert.False(tree.Find(@"C:\Users")!.IsLoading);
    }

    [Fact]
    public async Task Revealing_a_path_opens_the_folders_on_the_way_and_leaves_the_rows_beside_them()
    {
        var folders = Disk();
        var tree = Tree(folders);

        var windows = await tree.RevealAsync(@"C:\Windows");
        await tree.ExpandAsync(windows!);
        Assert.Equal(["C:", "  Users", "  Windows", "    System32", "D:"], Names(tree));

        var documents = await tree.RevealAsync(@"C:\Users\Admin\Documents");

        Assert.Equal(@"C:\Users\Admin\Documents", documents!.Path);
        Assert.Same(documents, tree.Current);
        Assert.True(documents.IsCurrent);
        Assert.False(windows!.IsCurrent);
        // Windows stayed open; the target itself is not opened.
        Assert.Equal(["C:", "  Users", "    Admin", "      Desktop", "      Documents", "    Public", "  Windows", "    System32", "D:"], Names(tree));
        Assert.False(documents.IsExpanded);
        // One request for each row that was opened, none for the others.
        Assert.Equal(new[] { @"C:\", @"C:\Windows", @"C:\Users", @"C:\Users\Admin" }, folders.Asked);
    }

    [Fact]
    public async Task Revealing_ignores_case_and_a_closing_backslash_and_takes_the_names_from_the_listing()
    {
        var tree = Tree(Disk());

        var node = await tree.RevealAsync(@"c:\users\ADMIN\");

        Assert.Equal(@"C:\Users\Admin", node!.Path);
        Assert.Equal("Admin", node.Name);
        Assert.Same(node, tree.Find(@"C:\USERS\admin"));
    }

    [Fact]
    public async Task A_drive_itself_can_be_revealed_with_or_without_its_backslash()
    {
        var tree = Tree(Disk());

        Assert.Same(tree.Rows[0], await tree.RevealAsync("C:"));
        Assert.Same(tree.Rows[0], await tree.RevealAsync(@"C:\"));
        Assert.Equal(0, tree.Requests);
    }

    [Fact]
    public async Task A_folder_made_after_the_last_read_is_found_by_reading_its_parent_once_more()
    {
        var folders = Disk().AddAnswers(@"C:\Users", ["Admin"], ["Admin", "Later"]);
        var tree = Tree(folders);
        await tree.RevealAsync(@"C:\Users");

        var later = await tree.RevealAsync(@"C:\Users\Later");

        Assert.Equal(@"C:\Users\Later", later!.Path);
        Assert.Equal(2, folders.Asked.Count(p => p == @"C:\Users"));
        Assert.Equal(["C:", "  Users", "    Admin", "    Later", "  Windows", "D:"], Names(tree));
    }

    [Fact]
    public async Task A_path_that_is_not_there_marks_the_deepest_row_and_reads_the_parent_once_more_and_no_more()
    {
        var folders = Disk();
        var tree = Tree(folders);

        var found = await tree.RevealAsync(@"C:\Users\Nobody\deeper");

        Assert.Equal(@"C:\Users", found!.Path);
        Assert.Same(found, tree.Current);
        Assert.Equal(2, folders.Asked.Count(p => p == @"C:\Users"));
    }

    [Fact]
    public async Task A_path_on_no_drive_marks_nothing()
    {
        var tree = Tree(Disk());
        await tree.RevealAsync(@"C:\Windows");
        Assert.NotNull(tree.Current);

        Assert.Null(await tree.RevealAsync(@"\\server\share\folder"));
        Assert.Null(tree.Current);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_read_says_why_stays_closed_and_is_the_deepest_row_of_a_reveal()
    {
        // C:\Secret is not in the table: the source answers "Access is denied."
        var folders = Disk().Add(@"C:\", "Secret", "Users");
        var tree = Tree(folders);
        await tree.ExpandAsync(tree.Rows[0]);
        var secret = tree.Find(@"C:\Secret")!;

        await tree.ExpandAsync(secret);

        Assert.Equal("Access is denied.", secret.Error);
        Assert.False(secret.IsExpanded);
        Assert.False(secret.HasChildren);
        Assert.False(secret.IsLoading);
        Assert.Equal(["C:", "  Secret", "  Users", "D:"], Names(tree));

        var deepest = await tree.RevealAsync(@"C:\Secret\inner\file");
        Assert.Same(secret, deepest);
        Assert.Same(secret, tree.Current);
    }

    [Fact]
    public async Task Reading_a_folder_again_keeps_the_rows_that_were_open_and_puts_new_folders_in_the_listing_order()
    {
        var folders = new FakeFolders()
            .AddAnswers(@"C:\", ["A", "B"], ["A", "Aa", "B"])
            .Add(@"C:\A", "x");
        var tree = Tree(folders);
        await tree.ExpandAsync(tree.Rows[0]);
        await tree.ExpandAsync(tree.Rows[1]);
        Assert.Equal(["C:", "  A", "    x", "  B", "D:"], Names(tree));

        tree.Collapse(tree.Rows[0]);
        await tree.ExpandAsync(tree.Rows[0]);

        Assert.Equal(["C:", "  A", "    x", "  Aa", "  B", "D:"], Names(tree));
    }

    [Fact]
    public async Task A_folder_that_is_gone_after_a_new_read_loses_its_row_and_takes_the_marks_with_it()
    {
        var folders = new FakeFolders().Add(@"C:\", "A", "B").Add(@"C:\A", "x");
        var tree = Tree(folders);
        await tree.RevealAsync(@"C:\A\x");
        Assert.Equal(@"C:\A\x", tree.Current!.Path);
        tree.Collapse(tree.Rows[0]);

        // The folder is read again while the old rows show; A is gone when the answer comes.
        var held = folders.Hold(@"C:\");
        var reopening = tree.ExpandAsync(tree.Rows[0]);
        tree.SetCursor(tree.Find(@"C:\A\x"));
        held.SetResult(new FolderListing(["B"]));
        await reopening;

        Assert.Equal(["C:", "  B", "D:"], Names(tree));
        Assert.Null(tree.Current);
        Assert.Same(tree.Rows[0], tree.Cursor);
    }

    [Fact]
    public async Task The_cursor_keys_move_open_and_close_rows()
    {
        var tree = Tree(Disk());
        tree.SetCursor(tree.Rows[0]);

        // Right opens the drive; Right again goes into it.
        await tree.CursorRightAsync();
        Assert.True(tree.Rows[0].IsExpanded);
        Assert.Same(tree.Rows[0], tree.Cursor);
        await tree.CursorRightAsync();
        Assert.Equal("Users", tree.Cursor!.Name);

        // Left on a closed row goes to the folder above, and on an open one closes it.
        tree.CursorLeft();
        Assert.Equal("C:", tree.Cursor!.Name);
        tree.CursorLeft();
        Assert.False(tree.Rows[0].IsExpanded);
        Assert.Equal(["C:", "D:"], Names(tree));
        tree.CursorLeft();
        Assert.Equal("C:", tree.Cursor!.Name);

        tree.MoveCursor(5);
        Assert.Equal("D:", tree.Cursor!.Name);
        tree.MoveCursor(-9);
        Assert.Equal("C:", tree.Cursor!.Name);
        tree.CursorToEnd();
        Assert.Equal("D:", tree.Cursor!.Name);
        tree.CursorToStart();
        Assert.Equal("C:", tree.Cursor!.Name);
        Assert.Equal(1, tree.Rows.Count(r => r.IsCursor));
    }

    [Fact]
    public async Task Closing_a_row_with_the_cursor_inside_puts_the_cursor_on_the_row_that_closed()
    {
        var tree = Tree(Disk());
        await tree.RevealAsync(@"C:\Users\Admin");
        tree.SetCursor(tree.Find(@"C:\Users\Admin"));

        tree.Collapse(tree.Rows[0]);

        Assert.Same(tree.Rows[0], tree.Cursor);
    }

    [Fact]
    public async Task A_newer_reveal_stops_an_older_one_that_is_still_waiting()
    {
        var folders = Disk();
        var tree = Tree(folders);
        var held = folders.Hold(@"C:\");
        using var newer = new CancellationTokenSource();

        var older = tree.RevealAsync(@"C:\Users\Admin", newer.Token);
        newer.Cancel();
        held.SetResult(new FolderListing(["Users", "Windows"]));

        Assert.Null(await older);
        Assert.Null(tree.Current);
    }

    [Fact]
    public async Task A_drive_that_stays_keeps_its_state_when_the_list_of_drives_changes()
    {
        var tree = Tree(Disk());
        await tree.ExpandAsync(tree.Rows[0]);

        tree.SetRoots([(@"C:\", "C:"), (@"E:\", "E:")]);

        Assert.Equal(["C:", "  Users", "  Windows", "E:"], Names(tree));
    }

    [Theory]
    [InlineData("C:", @"C:\")]
    [InlineData("c:/users/admin/", @"c:\users\admin")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\a\", @"C:\a")]
    public void A_path_has_backslashes_and_no_closing_one_except_on_a_drive(string path, string normal) =>
        Assert.Equal(normal, FolderTreeModel.Normalize(path));
}

/// <summary>The manifest's sidebar key and the rail layout's four settings.</summary>
public class SidebarSettingsTests
{
    private const string Valid = """
        {
          "id": "quick-notes",
          "name": "Quick Notes",
          "version": "0.1.0",
          "author": "CabinetOS",
          "description": "Notes beside the folders.",
          "entry": "index.html",
          "accepts": [],
          "placement": "pane"
          SIDEBAR
        }
        """;

    private static string Manifest(string sidebar) => Valid.Replace("SIDEBAR", sidebar, StringComparison.Ordinal);

    [Fact]
    public void A_tool_has_no_sidebar_page_unless_its_manifest_says_so()
    {
        Assert.False(ToolManifest.Parse(Manifest(""), "quick-notes").Sidebar);
        Assert.False(ToolManifest.Parse(Manifest(", \"sidebar\": false"), "quick-notes").Sidebar);
        Assert.True(ToolManifest.Parse(Manifest(", \"sidebar\": true"), "quick-notes").Sidebar);
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void A_sidebar_key_that_is_not_true_or_false_is_refused_and_the_schema_agrees(string value)
    {
        var json = Manifest($", \"sidebar\": {value}");

        Assert.Contains("`sidebar` must be true or false", Assert.Throws<ToolManifestException>(() => ToolManifest.Parse(json, "quick-notes")).Message);
        Assert.False(Schemas.Tool.Evaluate(JsonDocument.Parse(json).RootElement).IsValid);
    }

    [Fact]
    public void The_schema_accepts_a_sidebar_tool_with_no_files()
    {
        Assert.True(Schemas.Tool.Evaluate(JsonDocument.Parse(Manifest(", \"sidebar\": true")).RootElement).IsValid);
        Assert.True(Schemas.Tool.Evaluate(JsonDocument.Parse(Manifest("")).RootElement).IsValid);
    }

    [Fact]
    public void The_four_rail_settings_are_read_when_the_core_sends_them()
    {
        using var config = JsonDocument.Parse("""
            {"ui":{"layout":"rail","sidebar":true,"rail":["terminal","explorer",7],"sidebarWidth":310,"sidebarView":"notes","sidebarAutoReveal":false}}
            """);

        var settings = UiSettings.FromConfig(config.RootElement);

        Assert.Equal("rail", settings.Layout);
        Assert.Equal(["terminal", "explorer"], settings.Rail);
        Assert.Equal(310, settings.SidebarWidth);
        Assert.Equal("notes", settings.SidebarView);
        Assert.False(settings.SidebarAutoReveal);
    }

    [Fact]
    public void A_core_that_does_not_know_them_leaves_the_defaults()
    {
        using var config = JsonDocument.Parse("""{"ui":{"layout":"classic"}}""");

        var settings = UiSettings.FromConfig(config.RootElement);

        Assert.Null(settings.Rail);
        Assert.Null(settings.SidebarWidth);
        Assert.Null(settings.SidebarView);
        Assert.True(settings.SidebarAutoReveal);
    }

    [Fact]
    public void Values_of_the_wrong_kind_are_ignored()
    {
        using var config = JsonDocument.Parse("""{"ui":{"rail":"explorer","sidebarWidth":"wide","sidebarView":3,"sidebarAutoReveal":"no"}}""");

        var settings = UiSettings.FromConfig(config.RootElement);

        Assert.Null(settings.Rail);
        Assert.Null(settings.SidebarWidth);
        Assert.Null(settings.SidebarView);
        Assert.True(settings.SidebarAutoReveal);
    }
}
