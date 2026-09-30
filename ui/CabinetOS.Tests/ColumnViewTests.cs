using System.Text.Json;
using CabinetOS.Core.Tabs;

namespace CabinetOS.Tests;

/// <summary>
/// The column view (Phase 19f, ADR 0016; docs/ui.md, "The column view"):
/// the column stack without a window, a tab's mode in <c>ui.tabs</c>, and
/// the folder the window's state reports for a tab in the column view.
/// </summary>
public class ColumnViewTests
{
    // A column's item is the name of its listing, so a test sees which ones go.
    private static ColumnStack<string> Stack(string first = @"C:\data") => new(first, "L-data");

    private static string[] Released(IReadOnlyList<DroppedColumn<string>> dropped) => [.. dropped.Select(d => $"{d.Column}:{d.Folder}:{d.Item}")];

    [Fact]
    public void Opening_a_folder_adds_a_column_right_of_the_keyboard_and_moves_the_keyboard_there()
    {
        var stack = Stack();

        Assert.Empty(stack.Open(@"C:\data\a", "L-a"));
        Assert.Empty(stack.Open(@"C:\data\a\b", "L-b"));

        Assert.Equal([@"C:\data", @"C:\data\a", @"C:\data\a\b"], stack.Folders);
        Assert.Equal(3, stack.Count);
        Assert.Equal(2, stack.Keyboard);
        Assert.Equal(@"C:\data\a\b", stack.Deepest);
        Assert.Equal(@"C:\data\a\b", stack.KeyboardFolder);
        Assert.Equal(["L-data", "L-a", "L-b"], Enumerable.Range(0, stack.Count).Select(stack.ItemAt));
    }

    [Fact]
    public void Left_and_right_move_the_keyboard_and_keep_every_column()
    {
        var stack = Stack();
        stack.Open(@"C:\data\a", "L-a");
        stack.Open(@"C:\data\a\b", "L-b");

        Assert.True(stack.MoveLeft());
        Assert.True(stack.MoveLeft());
        Assert.False(stack.MoveLeft());
        Assert.Equal(0, stack.Keyboard);
        Assert.Equal(@"C:\data", stack.KeyboardFolder);
        // The deepest column stays the tab's path while the keyboard is left of it.
        Assert.Equal(@"C:\data\a\b", stack.Deepest);
        Assert.Equal(3, stack.Count);

        Assert.True(stack.MoveRight());
        Assert.True(stack.MoveRight());
        Assert.False(stack.MoveRight());
        Assert.Equal(2, stack.Keyboard);
        Assert.True(stack.MoveTo(1));
        Assert.False(stack.MoveTo(1));
        Assert.False(stack.MoveTo(3));
        Assert.False(stack.MoveTo(-1));
    }

    [Fact]
    public void Opening_a_row_left_of_the_deepest_drops_the_columns_right_of_it_deepest_first()
    {
        var stack = Stack();
        stack.Open(@"C:\data\a", "L-a");
        stack.Open(@"C:\data\a\b", "L-b");
        stack.MoveLeft();
        stack.MoveLeft();

        var dropped = stack.Open(@"C:\data\a", "L-a2");

        Assert.Equal([@"2:C:\data\a\b:L-b", @"1:C:\data\a:L-a"], Released(dropped));
        Assert.Equal([@"C:\data", @"C:\data\a"], stack.Folders);
        Assert.Equal(1, stack.Keyboard);
        Assert.Equal("L-a2", stack.ItemAt(1));

        // Opened again from there, the depth is three again and nothing more goes.
        Assert.Empty(stack.Open(@"C:\data\a\b", "L-b2"));
        Assert.Equal(3, stack.Count);
        Assert.Equal(@"C:\data\a\b", stack.Deepest);
    }

    [Fact]
    public void The_keyboard_column_holds_nothing_of_its_own_and_its_empty_slot_is_reported_too()
    {
        // The pane holds the keyboard column's listing, so its slot is empty; a drop still names the column.
        var stack = new ColumnStack<string>(@"C:\data");
        stack.Open(@"C:\data\a");
        stack.SetItem(0, "L-data");
        stack.MoveLeft();

        var dropped = stack.DropAfter(0);

        Assert.Equal([@"1:C:\data\a:"], Released(dropped));
        Assert.Null(dropped[0].Item);
        Assert.Equal(0, stack.Keyboard);
    }

    [Fact]
    public void A_drop_that_takes_the_keyboard_column_moves_the_keyboard_to_the_last_one_left()
    {
        var stack = Stack();
        stack.Open(@"C:\data\a", "L-a");
        stack.Open(@"C:\data\a\b", "L-b");

        var dropped = stack.DropAfter(0);

        Assert.Equal(2, dropped.Count);
        Assert.Equal(0, stack.Keyboard);
        Assert.Equal(@"C:\data", stack.Deepest);
    }

    [Fact]
    public void The_parent_of_the_first_column_comes_first_and_takes_the_keyboard()
    {
        var stack = Stack(@"C:\data\a");
        stack.Open(@"C:\data\a\b", "L-b");
        stack.MoveLeft();

        stack.Prepend(@"C:\data", "L-data");

        Assert.Equal([@"C:\data", @"C:\data\a", @"C:\data\a\b"], stack.Folders);
        Assert.Equal(0, stack.Keyboard);
        Assert.Equal(@"C:\data\a\b", stack.Deepest);
        Assert.Equal(1, stack.IndexOf(@"c:\DATA\a\"));
        Assert.Equal(-1, stack.IndexOf(@"C:\elsewhere"));
    }

    [Fact]
    public void Another_folder_starts_over_and_releases_every_column()
    {
        var stack = Stack();
        stack.Open(@"C:\data\a", "L-a");
        stack.MoveLeft();

        var dropped = stack.Reset(@"D:\other", "L-other");

        Assert.Equal([@"1:C:\data\a:L-a", @"0:C:\data:L-data"], Released(dropped));
        Assert.Equal([@"D:\other"], stack.Folders);
        Assert.Equal(0, stack.Keyboard);
        Assert.Equal("L-other", stack.ItemAt(0));
    }

    [Fact]
    public void A_tab_in_the_column_view_is_saved_with_its_mode_and_a_list_tab_without_one()
    {
        var strip = new TabStrip([new PaneTab(@"C:\a"), new PaneTab(@"C:\b\c", mode: TabMode.Columns)], 1);
        var tabs = new TabsConfig(strip.ToConfig(), PaneTabsConfig.Empty);

        var json = tabs.ToJson();

        Assert.Equal(
            """{"left":{"items":[{"path":"C:\\a","locked":false},{"path":"C:\\b\\c","locked":false,"mode":"columns"}],"active":1},"right":{"items":[],"active":0}}""",
            json.GetRawText());
        using var config = JsonDocument.Parse("""{"ui":{"tabs":""" + json.GetRawText() + "}}");
        var read = TabsConfig.FromConfig(config.RootElement);
        Assert.Equal([TabMode.Files, TabMode.Columns], read.Left.Items.Select(i => i.Mode));
        var restored = read.Left.ToStrip()!;
        Assert.Equal(TabMode.Columns, restored.Active.Mode);
        Assert.Equal(TabMode.Files, restored.Tabs[0].Mode);
    }

    [Fact]
    public void A_mode_the_window_does_not_know_is_read_as_the_list()
    {
        using var config = JsonDocument.Parse("""{"ui":{"tabs":{"left":{"items":[{"path":"C:\\x","mode":"tree"},{"path":"C:\\y","mode":7}],"active":0}}}}""");

        var read = TabsConfig.FromConfig(config.RootElement);

        Assert.Equal([TabMode.Files, TabMode.Files], read.Left.Items.Select(i => i.Mode));
    }

    [Fact]
    public void A_new_tab_from_a_tab_in_the_column_view_shows_columns_too()
    {
        var tab = new PaneTab(@"C:\data\a", mode: TabMode.Columns);

        Assert.Equal(TabMode.Columns, tab.Duplicate().Mode);
        Assert.Equal(TabMode.Files, new PaneTab(@"C:\x").Duplicate().Mode);
    }

    [Fact]
    public void The_window_state_reports_the_keyboard_column_s_folder_for_the_tab_in_front()
    {
        var left = new TabStrip([new PaneTab(@"C:\other"), new PaneTab(@"C:\data\a\b", mode: TabMode.Columns)], 1);
        var right = new TabStrip(new PaneTab(@"D:\"));

        var request = WindowStateBuilder.Build(0,
            new PaneSnapshot(left.Tabs, 1, @"C:\data\a\file.txt", [@"C:\data\a\file.txt"], FrontFolder: @"C:\data\a"),
            new PaneSnapshot(right.Tabs, 0, null, []));

        Assert.Equal([@"C:\other", @"C:\data\a"], request.Panes.Left.Tabs.Select(t => t.Path));
        Assert.Equal(@"C:\data\a\file.txt", request.Panes.Left.Cursor);
        Assert.Equal([@"D:\"], request.Panes.Right.Tabs.Select(t => t.Path));
        // The tab itself keeps the deepest folder: the row, the title and ui.tabs follow that one.
        Assert.Equal(@"C:\data\a\b", left.Active.Path);
    }
}
