using System.Text.Json;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Shell;
using CabinetOS.Tests.Support;
using static CabinetOS.Tests.ContextMenuTests;

namespace CabinetOS.Tests;

/// <summary>
/// The in-menu edit mode of Phase 18, step 2 (docs/ui.md, "Editing the menu"): the target's
/// <c>items</c> list, edited and written back as the core's <c>set_value</c> takes it. The model
/// holds every rule, so the view only draws it.
/// </summary>
public class ContextMenuEditTests
{
    private static ContextMenuEditModel Edit(MenuTargetKind kind = MenuTargetKind.File, ContextMenuConfig? config = null, List<CommandInfo>? registry = null) =>
        new(kind, (config ?? ContextMenuConfig.Defaults).For(kind).Items, registry ?? Registry(), Keys);

    private static List<CommandInfo> WithProgramAndPlugin()
    {
        var registry = Registry();
        registry.Add(Command("program.code", "Open in Code", source: new("program", null, "code"), target: "core"));
        var reader = new CommandSource("plugin", "reader", "Reader");
        registry.Add(Command("reader.size", "Size", source: reader, target: "core"));
        // A plugin's command for everywhere may sit in the menu too: the plugins' own menu group lists only file-pane ones.
        registry.Add(Command("reader.about", "About Reader", source: reader, target: "core", when: null));
        registry.Add(Command("view.toggleSidebar", "Toggle Sidebar", "ctrl+b", when: null));
        return registry;
    }

    [Theory]
    [InlineData(MenuTargetKind.File, "File menu", "contextMenu.file.items")]
    [InlineData(MenuTargetKind.Folder, "Folder menu", "contextMenu.folder.items")]
    [InlineData(MenuTargetKind.Background, "Empty space menu", "contextMenu.background.items")]
    [InlineData(MenuTargetKind.MultiSelect, "Selection menu", "contextMenu.multiSelect.items")]
    public void Each_target_has_its_name_and_its_setting(MenuTargetKind kind, string name, string path)
    {
        var edit = Edit(kind);

        Assert.Equal((name, path), (edit.TargetName, edit.SettingPath));
        Assert.Equal(ContextMenuConfig.Defaults.For(kind).Items, edit.Items);
        Assert.Equal(0, edit.Focus);
        Assert.False(edit.IsChanged);
    }

    [Fact]
    public void The_rows_read_as_the_menu_shows_them()
    {
        var config = ContextMenuConfig.Defaults with
        {
            File = new([], [new("pane.openSelected"), MenuItemConfig.Divider, new("program.code", [".md", ".txt"]), new("hex.view"), new("terminal.new")]),
        };
        var rows = Edit(config: config, registry: WithProgramAndPlugin()).Rows;

        Assert.Equal(["Open", "Separator", "Open in Code", "hex.view", "Open in Terminal"], rows.Select(r => r.Title));
        Assert.Equal(("\uE8E5", "ENTER"), (rows[0].Glyph, rows[0].Keys));
        Assert.True(rows[1].IsSeparator);
        Assert.Equal(".md, .txt", rows[2].Detail);
        // An ID no command has stays a row, so it can be taken out; the menu leaves it out.
        Assert.False(rows[3].Known);
        Assert.Equal("no command has this ID", rows[3].Detail);
        // As in the menu: the terminal's entry shows no keys.
        Assert.Null(rows[4].Keys);
    }

    [Fact]
    public void A_moved_row_keeps_its_extensions_and_the_focus_goes_with_it()
    {
        var filtered = new MenuItemConfig("program.code", [".md"]);
        var config = ContextMenuConfig.Defaults with { File = new([], [new("pane.openSelected"), filtered, new("terminal.new")]) };
        var edit = Edit(config: config, registry: WithProgramAndPlugin());

        edit.SetFocus(1);
        Assert.True(edit.MoveFocused(1));
        Assert.Equal(["pane.openSelected", "terminal.new", "program.code"], edit.Items.Select(i => i.Command));
        Assert.Same(filtered, edit.Items[2]);
        Assert.Equal(2, edit.Focus);
        Assert.True(edit.IsChanged);

        // At the end it stays; a drag past either end lands at that end.
        Assert.False(edit.MoveFocused(1));
        Assert.True(edit.Move(2, -5));
        Assert.Equal(["program.code", "pane.openSelected", "terminal.new"], edit.Items.Select(i => i.Command));
        Assert.Equal(0, edit.Focus);

        // Back where it was: nothing to save.
        Assert.True(edit.Move(0, 1));
        Assert.False(edit.IsChanged);
    }

    [Fact]
    public void Removing_a_row_puts_the_focus_on_the_row_that_took_its_place()
    {
        var edit = Edit();

        edit.SetFocus(1);
        Assert.True(edit.Remove(edit.Focus));
        Assert.Equal("Open|Copy to other pane|Open in Terminal", edit.Describe());
        Assert.Equal(1, edit.Focus);

        // The last row: the focus goes up.
        Assert.True(edit.Remove(2));
        Assert.Equal(1, edit.Focus);
        Assert.True(edit.Remove(0));
        Assert.True(edit.Remove(0));
        Assert.Equal(-1, edit.Focus);
        Assert.Empty(edit.Items);
        Assert.False(edit.Remove(0));
        Assert.False(edit.MoveFocus(1));
    }

    [Fact]
    public void A_command_and_a_divider_go_after_the_focused_row_and_take_the_focus()
    {
        var edit = Edit(registry: WithProgramAndPlugin());

        edit.SetFocus(0);
        edit.InsertCommand("program.code");
        edit.InsertSeparator();
        Assert.Equal("Open|Open in Code|-|Open in other pane|Copy to other pane|Open in Terminal", edit.Describe());
        Assert.Equal(2, edit.Focus);

        // An empty list: the first row.
        var empty = new ContextMenuEditModel(MenuTargetKind.Background, [], Registry(), Keys);
        Assert.Equal(-1, empty.Focus);
        empty.InsertCommand("file.newFolder");
        Assert.Equal(("New folder", 0), (empty.Describe(), empty.Focus));
    }

    [Fact]
    public void The_list_is_written_in_the_shape_of_the_defaults()
    {
        var config = ContextMenuConfig.Defaults with
        {
            File = new([], [new("pane.openSelected"), MenuItemConfig.Divider, new("program.code", [".md", ".TXT"])]),
        };
        var edit = Edit(config: config, registry: WithProgramAndPlugin());
        edit.Move(2, 0);

        Assert.Equal("""[{"command":"program.code","extensions":[".md",".TXT"]},{"command":"pane.openSelected"},{"separator":true}]""",
            edit.ToValue().GetRawText());

        // What the window reads back is what it wrote.
        using var written = JsonDocument.Parse("""{"version": 1, "contextMenu": {"file": {"items": """ + edit.ToValue().GetRawText() + "}}}");
        Assert.True(Schemas.Config.Evaluate(written.RootElement).IsValid);
        var read = ContextMenuConfig.FromConfig(written.RootElement).File.Items;
        Assert.Equal(edit.Items.Select(i => (i.Command, i.Separator)), read.Select(i => (i.Command, i.Separator)));
        Assert.Equal([".md", ".TXT"], read[0].Extensions);
        // The icon row stays as the file had it: only the list is written.
        Assert.Equal(ContextMenuConfig.Defaults.File.QuickActions, ContextMenuConfig.FromConfig(written.RootElement).File.QuickActions);
    }

    [Fact]
    public void An_unchanged_list_writes_the_core_s_defaults()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Repo.ConfigSchema));
        var defaults = schema.RootElement.GetProperty("properties").GetProperty("contextMenu").GetProperty("default");

        foreach (var kind in Enum.GetValues<MenuTargetKind>())
        {
            var core = defaults.GetProperty(ContextMenuEditModel.KeyOf(kind)).GetProperty("items");
            Assert.Equal(JsonSerializer.Serialize(core), Edit(kind).ToValue().GetRawText());
        }
    }

    [Fact]
    public void Add_command_offers_what_may_sit_in_a_file_pane_s_menu_and_is_not_there_yet()
    {
        var offered = Edit(registry: WithProgramAndPlugin()).Addable().Select(c => c.Id).ToList();

        // A program, and every plugin command; not a window-wide command of the core's (Toggle Sidebar).
        Assert.Contains("program.code", offered);
        Assert.Contains("reader.size", offered);
        Assert.Contains("reader.about", offered);
        Assert.DoesNotContain("view.toggleSidebar", offered);
        Assert.Contains("file.newFolder", offered);
        Assert.Contains("menu.showShell", offered);
        // The list has them already, or they have their own places.
        Assert.DoesNotContain("pane.openSelected", offered);
        Assert.DoesNotContain("file.properties", offered);
        Assert.DoesNotContain("menu.edit", offered);
        // By the title the menu gives them.
        var titles = Edit(registry: WithProgramAndPlugin()).Addable().Select(ContextMenuEditModel.TitleOf).ToList();
        Assert.Equal(titles.Order(StringComparer.CurrentCultureIgnoreCase), titles);
        Assert.Contains("Cut", titles);
    }

    [Theory]
    [InlineData("up", MenuEditAction.FocusUp)]
    [InlineData("down", MenuEditAction.FocusDown)]
    [InlineData("alt+up", MenuEditAction.MoveUp)]
    [InlineData("alt+down", MenuEditAction.MoveDown)]
    [InlineData("delete", MenuEditAction.Remove)]
    [InlineData("insert", MenuEditAction.AddCommand)]
    [InlineData("ctrl+s", MenuEditAction.Save)]
    [InlineData("escape", MenuEditAction.Cancel)]
    [InlineData("enter", MenuEditAction.None)]
    [InlineData("tab", MenuEditAction.None)]
    [InlineData("shift+delete", MenuEditAction.None)]
    public void Every_step_has_a_key(string keys, MenuEditAction action)
    {
        Assert.True(KeyCombo.TryParse(keys, out var combo));
        Assert.Equal(action, ContextMenuEditModel.ActionFor(combo.Value));
    }

    [Fact]
    public void Around_the_list_the_icon_row_stays_and_the_plugins_properties_and_edit_menu_follow()
    {
        var registry = WithProgramAndPlugin();
        var shown = ContextMenuEditModel.Fixed(ContextMenuConfig.Defaults, FileFacts(), registry, Keys);

        Assert.Equal("Cut|Copy|Paste|Rename|Delete", shown.DescribeQuickActions());
        Assert.Equal("Size|Properties|Edit Menu…", shown.DescribeItems());
        Assert.Equal(ContextMenuEntryKind.Separator, shown.Items[0].Kind);

        var space = ContextMenuEditModel.Fixed(ContextMenuConfig.Defaults, new(MenuTargetKind.Background, @"C:\work"), registry, Keys);
        Assert.Empty(space.QuickActions);
        Assert.Equal("Properties|Edit Menu…", space.DescribeItems());
    }
}
