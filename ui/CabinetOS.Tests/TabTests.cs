using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Tabs;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>Tabs per pane (Phase 12; docs/ui.md, "Tabs"): the tab model, its saved form, the window's state message, and what the strip's markup may hold.</summary>
public class TabTests
{
    private static TabStrip Strip(params string[] folders) => new(folders.Select(f => new PaneTab(f)), 0);

    private static string[] Paths(TabStrip strip) => [.. strip.Tabs.Select(t => t.Path)];

    [Fact]
    public void A_new_tab_opens_right_of_the_tab_in_front_and_comes_to_the_front()
    {
        var strip = Strip(@"C:\a", @"C:\b");
        var changes = 0;
        strip.Changed += () => changes++;

        var index = strip.Add(new PaneTab(@"C:\new"));

        Assert.Equal(1, index);
        Assert.Equal([@"C:\a", @"C:\new", @"C:\b"], Paths(strip));
        Assert.Equal(1, strip.ActiveIndex);
        Assert.Equal(@"C:\new", strip.Active.Path);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void A_tab_opened_in_the_background_keeps_the_front_tab_in_front_wherever_it_lands()
    {
        var strip = Strip(@"C:\a", @"C:\b", @"C:\c");
        strip.Select(1);

        strip.Add(new PaneTab(@"C:\left"), activate: false, at: 0);
        Assert.Equal(@"C:\b", strip.Active.Path);
        Assert.Equal(2, strip.ActiveIndex);

        strip.Add(new PaneTab(@"C:\right"), activate: false, at: 99);
        Assert.Equal(@"C:\b", strip.Active.Path);
        Assert.Equal(@"C:\right", strip.Tabs[^1].Path);
    }

    [Fact]
    public void The_strip_shows_with_one_tab_too()
    {
        // Phase 16: the strip is at the top of every pane (it showed from the second tab on before).
        var strip = Strip(@"C:\a");
        Assert.True(strip.ShowsRow);
        strip.Add(new PaneTab(@"C:\b"));
        Assert.True(strip.ShowsRow);
        strip.Remove(0);
        Assert.True(strip.ShowsRow);
    }

    [Fact]
    public void Closing_the_last_tab_is_refused()
    {
        var strip = Strip(@"C:\only");
        var changes = 0;
        strip.Changed += () => changes++;

        Assert.False(strip.CanClose(0));
        Assert.Null(strip.Remove(0));
        Assert.Equal([@"C:\only"], Paths(strip));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Closing_the_tab_in_front_brings_its_right_neighbour_else_its_left_one()
    {
        var strip = Strip(@"C:\a", @"C:\b", @"C:\c");
        strip.Select(1);
        Assert.Equal(@"C:\b", strip.Remove(1)!.Path);
        Assert.Equal(@"C:\c", strip.Active.Path);

        strip.Select(1);
        strip.Remove(1);
        Assert.Equal(@"C:\a", strip.Active.Path);
        Assert.Equal(0, strip.ActiveIndex);
    }

    [Fact]
    public void Closing_a_tab_behind_the_front_tab_leaves_the_front_tab_in_front()
    {
        var strip = Strip(@"C:\a", @"C:\b", @"C:\c");
        strip.Select(2);
        strip.Remove(0);
        Assert.Equal(@"C:\c", strip.Active.Path);
        Assert.Equal(1, strip.ActiveIndex);

        strip.Remove(0);
        Assert.Equal(@"C:\c", strip.Active.Path);
        Assert.Equal(0, strip.ActiveIndex);
    }

    [Fact]
    public void Next_and_previous_go_round()
    {
        var strip = Strip(@"C:\a", @"C:\b", @"C:\c");
        Assert.Equal(1, strip.NextIndex());
        strip.Select(2);
        Assert.Equal(0, strip.NextIndex());
        Assert.Equal(1, strip.PreviousIndex());
        strip.Select(0);
        Assert.Equal(2, strip.PreviousIndex());
    }

    [Fact]
    public void A_pane_keeps_a_folder_tab_even_while_a_tool_tab_is_open()
    {
        var strip = Strip(@"C:\docs");
        strip.Add(PaneTab.ForTool(@"C:\docs\README.md", "markdown-preview", "Markdown Preview"));

        Assert.False(strip.CanClose(0));
        Assert.Null(strip.Remove(0));
        Assert.True(strip.CanClose(1));
        Assert.Equal("README.md", strip.Remove(1)!.Title);
        Assert.Equal([@"C:\docs"], Paths(strip));
    }

    [Fact]
    public void A_locked_tab_sends_every_other_folder_to_a_new_tab_but_keeps_its_own()
    {
        Assert.True(TabRules.OpensInNewTab(locked: true, @"C:\docs", @"C:\docs\sub"));
        Assert.True(TabRules.OpensInNewTab(locked: true, @"C:\docs", @"D:\"));
        Assert.False(TabRules.OpensInNewTab(locked: true, @"C:\docs", @"C:\docs"));
        Assert.False(TabRules.OpensInNewTab(locked: true, @"C:\docs", @"c:\DOCS\"));
        Assert.False(TabRules.OpensInNewTab(locked: false, @"C:\docs", @"C:\other"));
        // A pane that has no folder yet has nothing to stay on.
        Assert.False(TabRules.OpensInNewTab(locked: true, "", @"C:\docs"));
    }

    [Fact]
    public void The_lock_toggles_on_folder_tabs_only()
    {
        var strip = Strip(@"C:\a");
        strip.Add(PaneTab.ForTool(@"C:\a\x.md", "markdown-preview", "Markdown Preview"));

        Assert.True(strip.ToggleLock(0));
        Assert.True(strip.Tabs[0].Locked);
        Assert.False(strip.ToggleLock(0));
        Assert.False(strip.ToggleLock(1));
        Assert.False(strip.Tabs[1].Locked);
        Assert.False(strip.ToggleLock(7));
    }

    [Fact]
    public void A_moved_tab_leaves_its_pane_and_comes_to_the_front_of_the_other()
    {
        var left = Strip(@"C:\a", @"C:\b");
        var right = Strip(@"D:\x", @"D:\y");
        right.Select(1);
        left.Tabs[1].Locked = true;

        var moved = TabStrip.Move(left, 1, right);

        Assert.Equal(@"C:\b", moved!.Path);
        Assert.Equal([@"C:\a"], Paths(left));
        Assert.Equal([@"D:\x", @"D:\y", @"C:\b"], Paths(right));
        Assert.Equal(@"C:\b", right.Active.Path);
        Assert.True(right.Active.Locked);
    }

    [Fact]
    public void A_pane_s_last_tab_and_a_tool_tab_do_not_move()
    {
        var left = Strip(@"C:\a");
        var right = Strip(@"D:\x");
        Assert.Null(TabStrip.Move(left, 0, right));
        Assert.Equal([@"D:\x"], Paths(right));

        left.Add(PaneTab.ForTool(@"C:\a\x.md", "markdown-preview", "Markdown Preview"));
        Assert.Null(TabStrip.Move(left, 1, right));
        Assert.Null(TabStrip.Move(left, 0, right));
        Assert.Equal(2, left.Count);
        Assert.Equal(1, right.Count);
    }

    [Fact]
    public void Single_pane_mode_takes_the_tool_tabs_out_and_brings_the_nearest_folder_tab_to_the_front()
    {
        var strip = Strip(@"C:\a", @"C:\b");
        strip.Add(PaneTab.ForTool(@"C:\a\x.md", "markdown-preview", "Markdown Preview"), at: 1);
        Assert.Equal([@"C:\a", @"C:\a\x.md", @"C:\b"], Paths(strip));
        Assert.True(strip.Active.IsTool);

        Assert.True(strip.RemoveToolTabs());

        Assert.Equal([@"C:\a", @"C:\b"], Paths(strip));
        Assert.Equal(@"C:\b", strip.Active.Path);
        Assert.False(strip.RemoveToolTabs());
    }

    [Fact]
    public void A_tab_duplicate_keeps_the_folder_and_the_order_and_starts_a_history_of_its_own()
    {
        var tab = new PaneTab(@"C:\docs", locked: true) { Sort = new SortSpec("size", true), Back = [@"C:\"], CursorName = "a.txt" };

        var copy = tab.Duplicate();

        Assert.Equal((@"C:\docs", false, new SortSpec("size", true)), (copy.Path, copy.Locked, copy.Sort));
        Assert.Empty(copy.Back);
        Assert.Null(copy.CursorName);
        Assert.Equal(@"C:\docs\sub", tab.Duplicate(@"C:\docs\sub").Path);
        Assert.Equal("docs", copy.Title);
    }

    [Fact]
    public void The_saved_form_holds_the_folder_tabs_with_their_locks_and_the_tab_in_front()
    {
        var strip = Strip(@"C:\a", @"C:\b", @"C:\c");
        strip.Tabs[1].Locked = true;
        strip.Select(2);

        var config = new TabsConfig(strip.ToConfig(), PaneTabsConfig.Empty);

        Assert.Equal(
            """{"left":{"items":[{"path":"C:\\a","locked":false},{"path":"C:\\b","locked":true},{"path":"C:\\c","locked":false}],"active":2},"right":{"items":[],"active":0}}""",
            config.ToJson().GetRawText());
    }

    [Fact]
    public void Tool_tabs_are_not_saved_and_a_tool_in_front_saves_the_folder_tab_before_it()
    {
        var strip = Strip(@"C:\a", @"C:\b");
        strip.Add(PaneTab.ForTool(@"C:\a\x.md", "markdown-preview", "Markdown Preview"), at: 1);
        Assert.Equal(1, strip.ActiveIndex);

        var saved = strip.ToConfig();

        Assert.Equal([@"C:\a", @"C:\b"], saved.Items.Select(i => i.Path));
        Assert.Equal(0, saved.Active);

        strip.Select(2);
        Assert.Equal(1, strip.ToConfig().Active);
    }

    [Fact]
    public void The_saved_tabs_come_back_as_they_were_read_from_the_config()
    {
        using var reply = JsonDocument.Parse("""
            {"ui":{"tabs":{"left":{"items":[{"path":"C:\\a","locked":false},{"path":"E:\\work","locked":true}],"active":1},
                           "right":{"items":[{"path":"D:\\"}],"active":0}}}}
            """);

        var config = TabsConfig.FromConfig(reply.RootElement);
        var left = config.ForPane(0).ToStrip()!;

        Assert.Equal([@"C:\a", @"E:\work"], Paths(left));
        Assert.Equal((1, true), (left.ActiveIndex, left.Active.Locked));
        Assert.Equal([@"D:\"], Paths(config.ForPane(1).ToStrip()!));
        Assert.False(config.ForPane(1).Items[0].Locked);
        var written = config.ToJson().GetRawText();
        using var again = JsonDocument.Parse("{\"ui\":{\"tabs\":" + written + "}}");
        Assert.Equal(written, TabsConfig.FromConfig(again.RootElement).ToJson().GetRawText());
    }

    [Fact]
    public void Nothing_saved_or_a_tab_in_front_past_the_end_gives_a_pane_it_can_still_open()
    {
        Assert.Null(TabsConfig.FromConfig(JsonDocument.Parse("{}").RootElement).ForPane(0).ToStrip());
        Assert.Null(TabsConfig.FromConfig(JsonDocument.Parse("""{"ui":{"tabs":7}}""").RootElement).ForPane(1).ToStrip());
        Assert.Null(TabsConfig.FromConfig(JsonDocument.Parse("""{"ui":{"tabs":{"left":{"items":[{"path":""},{"locked":true},"x"]}}}}""").RootElement).ForPane(0).ToStrip());

        // The core refuses such a file; a value that got here anyway must not leave the pane pointing at nothing.
        var past = TabsConfig.FromConfig(JsonDocument.Parse("""{"ui":{"tabs":{"left":{"items":[{"path":"C:\\a"}],"active":5}}}}""").RootElement);
        Assert.Equal(0, past.ForPane(0).ToStrip()!.ActiveIndex);
        Assert.Equal(0, past.ToJson().GetProperty("left").GetProperty("active").GetInt32());
    }

    [Fact]
    public void A_pane_of_tools_only_is_not_a_pane()
    {
        Assert.Throws<ArgumentException>(() => new TabStrip([PaneTab.ForTool(@"C:\x.md", "markdown-preview", "Markdown Preview")], 0));
        Assert.Throws<ArgumentException>(() => new TabStrip([], 0));
    }

    private static string Encode(WindowStateRequest request)
    {
        request.Id = "01J9ZQ4X7K3M5N8P2R6S0T1V4W";
        return Encoding.UTF8.GetString(MessageCodec.Encode(request));
    }

    [Fact]
    public void The_window_state_has_the_shape_of_the_protocol_text()
    {
        var left = Strip(@"C:\Users\me");
        left.Add(PaneTab.ForTool(@"E:\work\README.md", "md-preview", "Markdown Preview"));
        var right = Strip(@"D:\");
        right.Tabs[0].Locked = true;

        var request = WindowStateBuilder.Build(0,
            new PaneSnapshot(left.Tabs, left.ActiveIndex, @"C:\Users\me\notes.txt", [@"C:\Users\me\notes.txt"]),
            new PaneSnapshot(right.Tabs, right.ActiveIndex, null, []));
        var json = Encode(request);

        Assert.Equal(
            """{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"window_state","active_pane":"left","panes":{"left":{"tabs":[{"path":"C:\\Users\\me","locked":false},{"path":"E:\\work\\README.md","locked":false,"tool":"md-preview"}],"active":1,"cursor":"C:\\Users\\me\\notes.txt","marked":["C:\\Users\\me\\notes.txt"]},"right":{"tabs":[{"path":"D:\\","locked":true}],"active":0,"marked":[]}}}""",
            json);
        AssertValid(json);
        Assert.Equal("right", WindowStateBuilder.Build(1, new PaneSnapshot(left.Tabs, 0, null, []), new PaneSnapshot(right.Tabs, 0, null, [])).ActivePane);
    }

    [Fact]
    public void The_window_state_carries_at_most_a_thousand_marked_paths()
    {
        var strip = Strip(@"C:\big");
        var marked = Enumerable.Range(0, 5000).Select(i => $@"C:\big\file{i:D4}.txt").ToList();

        var request = WindowStateBuilder.Build(0, new PaneSnapshot(strip.Tabs, 0, marked[0], marked), new PaneSnapshot(strip.Tabs, 0, null, []));

        Assert.Equal(WindowStateBuilder.MaxMarked, request.Panes.Left.Marked.Count);
        Assert.Equal(marked[0], request.Panes.Left.Marked[0]);
        Assert.Equal(5000u, request.Panes.Left.MarkedTotal);
        AssertValid(Encode(request));
    }

    [Fact]
    public void A_cut_list_of_marks_says_how_many_rows_are_marked_and_a_whole_one_says_nothing()
    {
        var strip = Strip(@"C:\big");
        var thousand = Enumerable.Range(0, WindowStateBuilder.MaxMarked).Select(i => $@"C:\big\file{i:D4}.txt").ToList();
        PaneSnapshot Left(IReadOnlyList<string> marked, int? total) => new(strip.Tabs, 0, marked[0], marked, null, total);
        var right = new PaneSnapshot(strip.Tabs, 0, null, []);

        // The window read the first thousand of 5,000 marked rows: the total goes with them, on the wire too.
        var cut = WindowStateBuilder.Build(0, Left(thousand, 5000), right);
        Assert.Equal(5000u, cut.Panes.Left.MarkedTotal);
        var json = Encode(cut);
        Assert.Contains("\"marked_total\":5000", json);
        AssertValid(json);

        // A list that is all of the marks, even one of exactly a thousand, carries no total (older cores and messages stay as they were).
        Assert.Null(WindowStateBuilder.Build(0, Left(thousand, 1000), right).Panes.Left.MarkedTotal);
        Assert.Null(WindowStateBuilder.Build(0, Left(thousand, null), right).Panes.Left.MarkedTotal);
        Assert.Null(WindowStateBuilder.Build(0, Left(["x"], 1), right).Panes.Left.MarkedTotal);
        Assert.DoesNotContain("marked_total", Encode(WindowStateBuilder.Build(0, Left(thousand, 1000), right)));
        Assert.Null(cut.Panes.Right.MarkedTotal);
    }

    [Fact]
    public void The_strip_is_the_windows_own_row_and_nothing_in_its_markup_can_take_the_keyboard()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var markup = XDocument.Load(Path.Combine(Repo.Root, "ui", "CabinetOS", "Views", "PaneTabs.xaml"));

        // WinUI's TabView drew the grey rounded block with a bar floating over it; the design has a flat tab.
        Assert.DoesNotContain(markup.Descendants(), e => e.Name.LocalName is "TabView" or "TabViewItem");

        // The pane keeps the keys: a control of the strip is out of the tab order and takes no focus from a click.
        HashSet<string> focusable = ["UserControl", "ScrollViewer", "Button", "ToggleButton", "RepeatButton", "HyperlinkButton", "TextBox", "ComboBox", "ListView", "GridView", "CheckBox", "RadioButton", "Slider"];
        var controls = markup.Descendants().Where(e => e.Name.Namespace == xaml && focusable.Contains(e.Name.LocalName)).ToList();
        Assert.Contains(controls, e => e.Name.LocalName == "ScrollViewer");
        Assert.Contains(controls, e => (string?)e.Attribute(x + "Name") == "AddButton");
        foreach (var control in controls)
        {
            var who = (string?)control.Attribute(x + "Name") ?? control.Name.LocalName;
            Assert.Equal((who, "False"), (who, (string?)control.Attribute("IsTabStop")));
            Assert.Equal((who, "False"), (who, (string?)control.Attribute("AllowFocusOnInteraction")));
        }

        // The buttons the code builds (the tab's x) take their style, so the style says the same.
        foreach (var style in markup.Descendants(xaml + "Style").Where(s => focusable.Contains((string?)s.Attribute("TargetType") ?? "")))
        {
            var who = (string?)style.Attribute(x + "Key") ?? "a style";
            foreach (var property in new[] { "IsTabStop", "AllowFocusOnInteraction" })
            {
                var setter = style.Elements(xaml + "Setter").SingleOrDefault(s => (string?)s.Attribute("Property") == property);
                Assert.Equal((who, property, "False"), (who, property, (string?)setter?.Attribute("Value")));
            }
        }
    }

    private static void AssertValid(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = Schemas.Request.Evaluate(document.RootElement);
        Assert.True(result.IsValid, $"the request schema refuses {json}");
    }
}
