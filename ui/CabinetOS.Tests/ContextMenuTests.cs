using System.Text.Json;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Shell;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// The right-click menu of Phase 18 (docs/ui.md, "The context menu"): built from
/// <c>contextMenu</c> without a window, so every rule is tested here.
/// </summary>
public class ContextMenuTests
{
    private static readonly CommandSource Core = new("core", null, null);

    private static CommandInfo Command(string id, string title, string? keys = null, CommandSource? source = null, string target = "ui", string? when = "filesView") =>
        new(id, id.Split('.')[0], title, keys is null ? [] : [keys], keys is null ? [] : [keys], source ?? Core, target, when, false);

    /// <summary>The registry's commands the menus use, with their default keys, as the core lists them.</summary>
    private static List<CommandInfo> Registry() =>
    [
        Command("edit.cut", "Cut", "ctrl+x"),
        Command("edit.copy", "Copy", "ctrl+c"),
        Command("edit.paste", "Paste", "ctrl+v"),
        Command("file.rename", "Rename", "f2"),
        Command("file.delete", "Delete to Recycle Bin", "delete"),
        Command("pane.openSelected", "Open Selected Item", "enter"),
        Command("file.openInOtherPane", "Open in Other Pane", "ctrl+enter"),
        Command("file.copyToOtherPane", "Copy to Other Pane", "f5"),
        Command("terminal.new", "New Terminal", when: null),
        Command("file.newFolder", "New Folder", "f7"),
        Command("sidebar.pin", "Pin Folder", when: null),
        Command("file.properties", "Properties", "alt+enter"),
        Command("menu.edit", "Edit Context Menu…", when: null),
        Command("menu.showShell", "Show Windows Context Menu", "ctrl+shift+f10"),
    ];

    // The window shows a binding as "Ctrl+V"; the model only passes it on.
    private static string? Keys(string id) => Registry().Find(c => c.Id == id)?.Keys is [var first, ..] ? first.ToUpperInvariant() : null;

    private static ContextMenuFacts FileFacts(string name = "notes.md") =>
        new(MenuTargetKind.File, @"C:\work", @"C:\work\" + name, EntryIsFolder: false, SelectedCount: 1);

    private static string Shape(IEnumerable<ContextMenuEntry> entries) => string.Join("|", entries.Select(e => e.Kind switch
    {
        ContextMenuEntryKind.Separator => "-",
        ContextMenuEntryKind.Header => $"[{e.Title}]",
        _ => e.Title,
    }));

    [Fact]
    public void A_fresh_config_shows_the_file_menu_of_phase_5()
    {
        var view = ContextMenuModel.Build(ContextMenuConfig.Defaults, FileFacts(), Registry(), Keys);

        Assert.Equal("Cut|Copy|Paste|Rename|Delete", view.DescribeQuickActions());
        Assert.Equal(["Cut (CTRL+X)", "Copy (CTRL+C)", "Paste (CTRL+V)", "Rename (F2)", "Delete to the Recycle Bin (DELETE)"], view.QuickActions.Select(q => q.Tooltip));
        Assert.Equal("Open|Open in other pane|Copy to other pane|Open in Terminal|-|Properties|Edit Menu…", Shape(view.Items));
        Assert.Empty(view.Unknown);
        var byTitle = view.Items.Where(i => i.Kind == ContextMenuEntryKind.Item).ToDictionary(i => i.Title);
        // A file opens with its program; only a folder opens in the other pane.
        Assert.True(byTitle["Open"].IsEnabled);
        Assert.False(byTitle["Open in other pane"].IsEnabled);
        Assert.Equal(("ENTER", "F5", "ALT+ENTER"), (byTitle["Open"].Keys, byTitle["Copy to other pane"].Keys, byTitle["Properties"].Keys));
        // Ctrl+` toggles the terminal pane; the menu's entry makes a new shell, so it shows no keys.
        Assert.Null(byTitle["Open in Terminal"].Keys);
        Assert.Equal(@"C:\work", byTitle["Open in Terminal"].Args!.Value.GetProperty("cwd").GetString());
        Assert.Null(byTitle["Properties"].Args);
        Assert.Equal(("\uE8E5", "\uE946", "\uE70F"), (byTitle["Open"].Glyph, byTitle["Properties"].Glyph, byTitle["Edit Menu…"].Glyph));
        // Nothing on the clipboard: Paste waits.
        Assert.False(view.QuickActions[2].IsEnabled);
        Assert.True(view.QuickActions[3].IsEnabled);
    }

    [Fact]
    public void A_folder_opens_in_the_other_pane_and_its_terminal_starts_inside_it()
    {
        var facts = new ContextMenuFacts(MenuTargetKind.Folder, @"C:\work", @"C:\work\src", EntryIsFolder: true, SelectedCount: 1, ClipboardEmpty: false);
        var view = ContextMenuModel.Build(ContextMenuConfig.Defaults, facts, Registry(), Keys);
        var byTitle = view.Items.Where(i => i.Kind == ContextMenuEntryKind.Item).ToDictionary(i => i.Title);

        Assert.True(byTitle["Open in other pane"].IsEnabled);
        Assert.Equal(@"C:\work\src", byTitle["Open in Terminal"].Args!.Value.GetProperty("cwd").GetString());
        Assert.True(view.QuickActions[2].IsEnabled);
    }

    [Fact]
    public void Several_rows_keep_the_menu_but_not_rename_and_one_pane_cannot_copy_across()
    {
        var facts = new ContextMenuFacts(MenuTargetKind.MultiSelect, @"C:\work", @"C:\work\a.txt", SelectedCount: 3, DualPane: false);
        var view = ContextMenuModel.Build(ContextMenuConfig.Defaults, facts, Registry(), Keys);

        Assert.Equal("Open|Open in other pane|Copy to other pane|Open in Terminal|-|Properties|Edit Menu…", Shape(view.Items));
        Assert.False(view.QuickActions.Single(q => q.CommandId == "file.rename").IsEnabled);
        Assert.False(view.Items.Single(i => i.CommandId == "file.copyToOtherPane").IsEnabled);
    }

    [Fact]
    public void The_empty_space_has_the_folder_s_short_menu()
    {
        var facts = new ContextMenuFacts(MenuTargetKind.Background, @"C:\work", ClipboardEmpty: false);
        var view = ContextMenuModel.Build(ContextMenuConfig.Defaults, facts, Registry(), Keys);

        Assert.Empty(view.QuickActions);
        Assert.Equal("Paste|New folder|Pin this folder to the sidebar|-|Properties|Edit Menu…", Shape(view.Items));
        var properties = view.Items.Single(i => i.CommandId == "file.properties");
        Assert.Equal("folder", properties.Args!.Value.GetProperty("scope").GetString());
        // Alt+Enter is the focused row's Properties, not the folder's.
        Assert.Null(properties.Keys);
        Assert.Equal(@"C:\work", view.Items.Single(i => i.CommandId == "sidebar.pin").Args!.Value.GetProperty("path").GetString());

        var pinned = ContextMenuModel.Build(ContextMenuConfig.Defaults, facts with { PanePinned = true }, Registry(), Keys);
        Assert.Equal("Paste|New folder|-|Properties|Edit Menu…", Shape(pinned.Items));
    }

    [Fact]
    public void An_unknown_command_is_left_out_and_reported_and_its_divider_goes_with_it()
    {
        var config = ContextMenuConfig.Defaults with
        {
            File = new(["edit.cut", "hex.view"], [new("pane.openSelected"), MenuItemConfig.Divider, new("gitlens.blame"), MenuItemConfig.Divider, MenuItemConfig.Divider, new("terminal.new"), MenuItemConfig.Divider]),
        };
        var view = ContextMenuModel.Build(config, FileFacts(), Registry(), Keys);

        Assert.Equal("Cut", view.DescribeQuickActions());
        Assert.Equal("Open|-|Open in Terminal|-|Properties|Edit Menu…", Shape(view.Items));
        Assert.Equal(["hex.view", "gitlens.blame"], view.Unknown);
    }

    [Fact]
    public void Properties_and_edit_menu_keep_their_place_whatever_the_file_lists()
    {
        var config = ContextMenuConfig.Defaults with
        {
            File = new([], [new("file.properties"), new("menu.edit"), new("pane.openSelected")]),
        };
        var view = ContextMenuModel.Build(config, FileFacts(), Registry(), Keys);
        Assert.Equal("Open|-|Properties|Edit Menu…", Shape(view.Items));

        // An older core without menu.edit: no entry for it.
        var older = ContextMenuModel.Build(config, FileFacts(), [.. Registry().Where(c => c.Id != "menu.edit")], Keys);
        Assert.Equal("Open|-|Properties", Shape(older.Items));
    }

    [Theory]
    [InlineData("notes.md", true)]
    [InlineData("NOTES.MD", true)]
    [InlineData("main.rs", true)]
    [InlineData("a.txt", false)]
    [InlineData("README", false)]
    [InlineData("trailing.", false)]
    public void An_extensions_filter_matches_the_file_s_extension_without_case(string name, bool shown)
    {
        var config = ContextMenuConfig.Defaults with
        {
            File = new([], [new("pane.openSelected"), new("program.code", [".md", ".RS"])]),
        };
        var registry = Registry();
        registry.Add(Command("program.code", "Open in Code", source: new("program", null, "code"), target: "core"));
        var view = ContextMenuModel.Build(config, FileFacts(name), registry, Keys);

        Assert.Equal(shown, view.Items.Any(i => i.CommandId == "program.code"));
    }

    [Fact]
    public void Several_rows_match_only_when_every_one_is_a_file_with_a_listed_extension()
    {
        var config = ContextMenuConfig.Defaults with
        {
            MultiSelect = new([], [new("program.code", [".md", ".txt"]), new("pane.openSelected")]),
        };
        var registry = Registry();
        registry.Add(Command("program.code", "Open in Code", source: new("program", null, "code"), target: "core"));
        bool Shown(SelectionExtensions selection)
        {
            var facts = new ContextMenuFacts(MenuTargetKind.MultiSelect, @"C:\work", @"C:\work\a.md", SelectedCount: 2, Selection: () => selection);
            return ContextMenuModel.Build(config, facts, registry, Keys).Items.Any(i => i.CommandId == "program.code");
        }

        Assert.True(Shown(new(new HashSet<string> { ".md", ".txt" }, false, false)));
        Assert.False(Shown(new(new HashSet<string> { ".md", ".rs" }, false, false)));
        Assert.False(Shown(new(new HashSet<string> { ".md" }, true, false)));
        Assert.False(Shown(new(new HashSet<string> { ".md" }, false, true)));

        // Without a filter, the selection is never read: a menu over 100,000 marked rows lists none of them.
        var asked = 0;
        var facts = new ContextMenuFacts(MenuTargetKind.MultiSelect, @"C:\work", @"C:\work\a.md", SelectedCount: 100_000,
            Selection: () => { asked++; return new(new HashSet<string>(), false, false); });
        ContextMenuModel.Build(ContextMenuConfig.Defaults, facts, registry, Keys);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void A_folder_and_the_empty_space_have_no_extension_to_match()
    {
        var filtered = new MenuTargetConfig([], [new("pane.openSelected", [".md"])]);
        var config = ContextMenuConfig.Defaults with { Folder = filtered, Background = filtered };
        var folder = ContextMenuModel.Build(config, new(MenuTargetKind.Folder, @"C:\work", @"C:\work\docs.md", EntryIsFolder: true), Registry(), Keys);
        var space = ContextMenuModel.Build(config, new(MenuTargetKind.Background, @"C:\work"), Registry(), Keys);

        Assert.DoesNotContain(folder.Items, i => i.CommandId == "pane.openSelected");
        Assert.DoesNotContain(space.Items, i => i.CommandId == "pane.openSelected");
    }

    [Fact]
    public void Plugins_follow_after_a_divider_with_their_name_and_ask_for_the_paths_when_they_run()
    {
        var registry = Registry();
        var reader = new CommandSource("plugin", "reader", "Reader");
        registry.Add(Command("reader.size", "Size", source: reader, target: "core"));
        registry.Add(Command("reader.stats", "Stats", "ctrl+alt+s", source: reader, target: "core"));
        // Everywhere, not a file pane's: not in the menu.
        registry.Add(Command("reader.about", "About Reader", source: reader, target: "core", when: null));
        var view = ContextMenuModel.Build(ContextMenuConfig.Defaults, FileFacts(), registry, Keys);

        Assert.Equal("Open|Open in other pane|Copy to other pane|Open in Terminal|-|[From plugins]|Size|Stats|-|Properties|Edit Menu…", Shape(view.Items));
        var size = view.Items.Single(i => i.CommandId == "reader.size");
        Assert.Equal(("Reader", true, (JsonElement?)null), (size.Badge, size.WantsTargets, size.Args));

        // One the file lists stands where the file puts it, and not again in the group.
        var config = ContextMenuConfig.Defaults with { File = new([], [new("reader.stats"), new("pane.openSelected")]) };
        var listed = ContextMenuModel.Build(config, FileFacts(), registry, Keys);
        Assert.Equal("Stats|Open|-|[From plugins]|Size|-|Properties|Edit Menu…", Shape(listed.Items));
        Assert.Equal("Reader", listed.Items[0].Badge);

        // The empty space has no plugin group, as before Phase 18.
        var space = ContextMenuModel.Build(ContextMenuConfig.Defaults, new(MenuTargetKind.Background, @"C:\work"), registry, Keys);
        Assert.DoesNotContain(space.Items, i => i.Kind == ContextMenuEntryKind.Header);
    }

    [Fact]
    public void A_program_needs_a_focused_entry_and_shows_its_title()
    {
        var registry = Registry();
        registry.Add(Command("program.code", "Open in Code", source: new("program", null, "code"), target: "core"));
        var config = new ContextMenuConfig(false,
            new([], [new("program.code")]),
            new([], [new("program.code")]),
            ContextMenuConfig.Defaults.Folder,
            ContextMenuConfig.Defaults.MultiSelect);

        var file = ContextMenuModel.Build(config, FileFacts(), registry, Keys).Items[0];
        Assert.Equal(("Open in Code", true, false), (file.Title, file.IsEnabled, file.WantsTargets));
        Assert.Null(file.Glyph);
        var space = ContextMenuModel.Build(config, new(MenuTargetKind.Background, @"C:\work"), registry, Keys).Items[0];
        Assert.False(space.IsEnabled);
    }

    [Fact]
    public void The_config_is_read_per_target_and_what_is_missing_keeps_its_default()
    {
        using var document = JsonDocument.Parse("""
            {"contextMenu": {"shellMenu": true,
              "file": {"items": [{"command": "program.code", "extensions": [".md"]}, {"separator": true}, {"command": "pane.openSelected"}]},
              "folder": {"quickActions": ["edit.copy"]},
              "background": "not an object"}}
            """);
        var config = ContextMenuConfig.FromConfig(document.RootElement);

        Assert.True(config.ShellMenu);
        Assert.Equal(ContextMenuConfig.Defaults.File.QuickActions, config.File.QuickActions);
        Assert.Equal([new MenuItemConfig("program.code", [".md"]).Command, null, "pane.openSelected"], config.File.Items.Select(i => i.Command));
        Assert.Equal([".md"], config.File.Items[0].Extensions);
        Assert.True(config.File.Items[1].Separator);
        Assert.Equal(["edit.copy"], config.Folder.QuickActions);
        Assert.Equal(ContextMenuConfig.Defaults.Folder.Items, config.Folder.Items);
        Assert.Equal(ContextMenuConfig.Defaults.Background, config.Background);
        Assert.Equal(ContextMenuConfig.Defaults.MultiSelect, config.MultiSelect);

        using var empty = JsonDocument.Parse("""{"ui": {}}""");
        Assert.Equal(ContextMenuConfig.Defaults, ContextMenuConfig.FromConfig(empty.RootElement));
    }

    [Fact]
    public void The_window_s_defaults_are_the_core_s()
    {
        // The core writes its defaults into the schema: a change on one side shows here.
        using var schema = JsonDocument.Parse(File.ReadAllText(Repo.ConfigSchema));
        var defaults = schema.RootElement.GetProperty("properties").GetProperty("contextMenu").GetProperty("default");
        using var config = JsonDocument.Parse($$"""{"contextMenu": {{defaults.GetRawText()}}}""");
        var read = ContextMenuConfig.FromConfig(config.RootElement);

        foreach (var kind in Enum.GetValues<MenuTargetKind>())
        {
            Assert.Equal(ContextMenuConfig.Defaults.For(kind).QuickActions, read.For(kind).QuickActions);
            Assert.Equal(ContextMenuConfig.Defaults.For(kind).Items, read.For(kind).Items);
        }
        Assert.Equal(ContextMenuConfig.Defaults.ShellMenu, read.ShellMenu);
    }

    [Theory]
    [InlineData(@"C:\a\notes.MD", ".md")]
    [InlineData(@"C:\a.b\README", "")]
    [InlineData(@"C:\a\.gitignore", ".gitignore")]
    [InlineData(@"C:\a\c.tar.gz", ".gz")]
    [InlineData("x.", "")]
    public void The_extension_starts_at_the_last_dot_of_the_name(string path, string expected) =>
        Assert.Equal(expected, ContextMenuModel.ExtensionOf(path));
}
