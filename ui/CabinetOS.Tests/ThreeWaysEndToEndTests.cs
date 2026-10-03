using System.Text.Json.Nodes;
using CabinetOS.Tests.Support;
using static CabinetOS.Tests.Support.WindowRun;

namespace CabinetOS.Tests;

/// <summary>
/// The four settings that were reachable from the file only, on a real window and core (Phase 23, the settings-three-ways
/// skill): <c>ui.layout</c>, <c>panes.showHidden</c>, <c>ui.sidebarAutoReveal</c> and <c>contextMenu.shellMenu</c>. For each one the
/// command, the window's control and an edit of the file change it, and the other two ways show the change. They run only with
/// <c>CABINETOS_UI_E2E=1</c> and a built window (Debug) and core. The "shell state" step logs what each control shows
/// (<c>shell:&lt;label&gt;</c>); the test edits the file when the window logs <c>edit-now</c>, as an editor would.
/// </summary>
public class ThreeWaysEndToEndTests
{
    private static JsonObject Config(Action<JsonObject>? more = null)
    {
        var config = new JsonObject { ["version"] = 1, ["ui"] = new JsonObject { ["dualPane"] = true } };
        more?.Invoke(config);
        return config;
    }

    // A folder of two files, one of them hidden: the listing's size says whether hidden files are listed.
    private static string MakeData(WindowRun run)
    {
        var data = Path.Combine(run.Root, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "visible.txt"), "x");
        var hidden = Path.Combine(data, "secret.txt");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        return data;
    }

    // What an editor does: the file is read, a key changes, the file is written.
    private static void Edit(WindowRun run, Action<JsonObject> change)
    {
        var config = run.ReadConfig();
        change(config);
        run.WriteConfig(config);
    }

    private static JsonObject Section(JsonObject config, string name)
    {
        if (config[name] is not JsonObject section)
        {
            config[name] = section = [];
        }
        return section;
    }

    private static string Key(JsonObject config, string section, string key) => (config[section] as JsonObject)?[key]?.ToJsonString() ?? "null";

    /// <summary>
    /// ui.layout: the three layout commands and Next Layout, the hamburger's Layout submenu (the current one checked) and the file
    /// give the same layout, and the palette's row marks the layout in effect.
    /// </summary>
    [Fact]
    public async Task The_layout_changes_from_its_commands_the_hamburger_s_Layout_menu_and_the_file()
    {
        var run = Prepare("three-ways-layout", _ => Config());
        try
        {
            var process = run.Start("layout", string.Join(';',
                "size:1400x900",
                "shell:start",
                // The command: the activity rail.
                "cmd:view.layoutRail",
                "until:setting:layout=rail",
                "wait:600",
                "shell:rail-by-command",
                // The palette's row marks it.
                "cmd:palette.show",
                "wait:300",
                "type:Layout",
                "wait:600",
                "shell:palette-layout",
                "cmd:overlay.close",
                "wait:300",
                // The menu: Layout, then Classic Layout.
                "cmd:menu.show",
                "wait:500",
                "shell:menu-closed-submenu",
                "click:Layout",
                "wait:400",
                "shell:menu-submenu",
                "click:Classic Layout",
                "until:setting:layout=classic",
                "wait:600",
                "shell:classic-by-menu",
                // The file.
                "shell:edit-now",
                "until:setting:layout=right",
                "wait:600",
                "shell:right-by-file",
                "cmd:menu.show",
                "wait:500",
                "shell:menu-after-file",
                "cmd:overlay.close",
                "wait:300",
                // Next Layout: right, rail, classic.
                "cmd:view.cycleLayout",
                "until:setting:layout=rail",
                "wait:600",
                "shell:cycled-to-rail",
                "cmd:view.cycleLayout",
                "until:setting:layout=classic",
                "wait:600",
                "shell:cycled-to-classic",
                "shot:done"));
            await run.WaitForStateAsync("layout", "shell state", "edit-now");
            Edit(run, config => Section(config, "ui")["layout"] = "right");
            var logs = await run.FinishAsync("layout", process);

            string Shell(string label, string field) => Text(State(logs, "shell state", label), field);
            Assert.Equal(("classic", "false", "Terminal: bottom"), (Shell("start", "layout"), Shell("start", "rail_shown"), Shell("start", "layout_status")));

            // The command wrote the key; the window shows the rail.
            Assert.Equal(("rail", "true", "Activity rail"), (Shell("rail-by-command", "layout"), Shell("rail-by-command", "rail_shown"), Shell("rail-by-command", "layout_status")));
            var palette = Shell("palette-layout", "palette_states").Split('|');
            Assert.Contains("view.layoutRail=current", palette);
            Assert.DoesNotContain("view.layoutClassic=current", palette);

            // The hamburger: Layout with its three, the current one checked; a click on one of them is the same command.
            Assert.Contains("Layout (Classic Layout, Terminal on the Right, Activity Rail [x])", Shell("menu-closed-submenu", "menu"));
            Assert.Equal("Layout: Classic Layout|Terminal on the Right|Activity Rail [x]", Shell("menu-submenu", "menu"));
            Assert.Equal(("classic", "false"), (Shell("classic-by-menu", "layout"), Shell("classic-by-menu", "rail_shown")));

            // The file: the window follows it, and the menu's check moves to the layout the file names.
            Assert.Equal(("right", "Terminal: right", "false"), (Shell("right-by-file", "layout"), Shell("right-by-file", "layout_status"), Shell("right-by-file", "rail_shown")));
            Assert.Contains("Layout (Classic Layout, Terminal on the Right [x], Activity Rail)", Shell("menu-after-file", "menu"));

            Assert.Equal("rail", Shell("cycled-to-rail", "layout"));
            Assert.Equal("classic", Shell("cycled-to-classic", "layout"));
            Assert.Equal("\"classic\"", Key(run.ReadConfig(), "ui", "layout"));
        }
        finally
        {
            run.Stop();
        }
    }

    /// <summary>
    /// panes.showHidden: the command, the hamburger's "Show Hidden Files" (checked while on), the status bar's "hidden" and the file
    /// give the same state, and the panes list again each time.
    /// </summary>
    [Fact]
    public async Task Hidden_files_switch_from_the_command_the_menu_the_status_bar_and_the_file_and_the_panes_list_again()
    {
        var run = Prepare("three-ways-hidden", _ => Config());
        try
        {
            var data = MakeData(run);
            var process = run.Start("hidden", string.Join(';',
                "size:1400x900",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:800",
                "shell:start",
                // The command.
                "cmd:view.toggleHiddenFiles",
                "until:setting:hidden=on",
                "wait:1200",
                "shell:on-by-command",
                "cmd:palette.show",
                "wait:300",
                "type:Hidden Files",
                "wait:600",
                "shell:palette-on",
                "cmd:overlay.close",
                "wait:300",
                // The menu: checked while on; a click turns it off.
                "cmd:menu.show",
                "wait:500",
                "shell:menu-on",
                "click:Show Hidden Files",
                "until:setting:hidden=off",
                "wait:1200",
                "shell:off-by-menu",
                // The file.
                "shell:edit-now",
                "until:setting:hidden=on",
                "wait:1200",
                "shell:on-by-file",
                // The status bar's word.
                "click:Hidden files are shown",
                "until:setting:hidden=off",
                "wait:1200",
                "shell:off-by-pill",
                "cmd:menu.show",
                "wait:500",
                "shell:menu-off",
                "cmd:overlay.close",
                "shot:done"));
            await run.WaitForStateAsync("hidden", "shell state", "edit-now");
            Edit(run, config => Section(config, "panes")["showHidden"] = true);
            var logs = await run.FinishAsync("hidden", process);

            string Shell(string label, string field) => Text(State(logs, "shell state", label), field);
            Assert.Equal(("false", "false", "1"), (Shell("start", "show_hidden"), Shell("start", "hidden_pill"), Shell("start", "pane0_count")));

            Assert.Equal(("true", "true", "2"), (Shell("on-by-command", "show_hidden"), Shell("on-by-command", "hidden_pill"), Shell("on-by-command", "pane0_count")));
            Assert.Contains("view.toggleHiddenFiles=on", Shell("palette-on", "palette_states").Split('|'));
            Assert.Contains("Show Hidden Files [x]", Shell("menu-on", "menu").Split('|'));
            Assert.Equal(("false", "false", "1"), (Shell("off-by-menu", "show_hidden"), Shell("off-by-menu", "hidden_pill"), Shell("off-by-menu", "pane0_count")));

            // An edit of the file: the pill, the listing and (below) the menu follow.
            Assert.Equal(("true", "true", "2"), (Shell("on-by-file", "show_hidden"), Shell("on-by-file", "hidden_pill"), Shell("on-by-file", "pane0_count")));
            Assert.Equal(("false", "false", "1"), (Shell("off-by-pill", "show_hidden"), Shell("off-by-pill", "hidden_pill"), Shell("off-by-pill", "pane0_count")));
            Assert.Contains("Show Hidden Files [ ]", Shell("menu-off", "menu").Split('|'));
            Assert.Equal("false", Key(run.ReadConfig(), "panes", "showHidden"));
        }
        finally
        {
            run.Stop();
        }
    }

    /// <summary>
    /// ui.sidebarAutoReveal: the command, the pin in the Explorer view's header, the hamburger's "Follow the Active Pane" and the file
    /// give the same state; in the classic layout the command works and the menu's row is the control.
    /// </summary>
    [Fact]
    public async Task The_Explorer_following_the_active_pane_switches_from_the_command_the_pin_the_menu_and_the_file()
    {
        var run = Prepare("three-ways-follow", _ => Config(config => Section(config, "ui")["layout"] = "rail"));
        try
        {
            var data = MakeData(run);
            var process = run.Start("follow", string.Join(';',
                "size:1400x900",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "cmd:view.showExplorer",
                "wait:800",
                "shell:start",
                // The command.
                "cmd:sidebar.toggleFollow",
                "until:setting:follow=off",
                "wait:500",
                "shell:off-by-command",
                "cmd:palette.show",
                "wait:300",
                "type:Follow the Active Pane",
                "wait:600",
                "shell:palette-off",
                "cmd:overlay.close",
                "wait:300",
                // The pin in the Explorer view's header.
                "click:Follow the active pane",
                "until:setting:follow=on",
                "wait:500",
                "shell:on-by-pin",
                // The menu: checked while on; a click turns it off.
                "cmd:menu.show",
                "wait:500",
                "shell:menu-on",
                "click:Follow the Active Pane",
                "until:setting:follow=off",
                "wait:500",
                "shell:off-by-menu",
                // The file.
                "shell:edit-now",
                "until:setting:follow=on",
                "wait:500",
                "shell:on-by-file",
                // The classic layout has no tree: the command works, and the menu's row is the control.
                "cmd:view.layoutClassic",
                "until:setting:layout=classic",
                "wait:600",
                "cmd:sidebar.toggleFollow",
                "until:setting:follow=off",
                "wait:500",
                "cmd:menu.show",
                "wait:500",
                "shell:classic-menu",
                "cmd:overlay.close",
                "shot:done"));
            await run.WaitForStateAsync("follow", "shell state", "edit-now");
            Edit(run, config => Section(config, "ui")["sidebarAutoReveal"] = true);
            var logs = await run.FinishAsync("follow", process);

            string Shell(string label, string field) => Text(State(logs, "shell state", label), field);
            Assert.Equal(("rail", "true", "true"), (Shell("start", "layout"), Shell("start", "follow_pin"), Shell("start", "auto_reveal")));
            Assert.Equal(("false", "false"), (Shell("off-by-command", "follow_pin"), Shell("off-by-command", "auto_reveal")));
            Assert.Contains("sidebar.toggleFollow=off", Shell("palette-off", "palette_states").Split('|'));
            Assert.Equal(("true", "true"), (Shell("on-by-pin", "follow_pin"), Shell("on-by-pin", "auto_reveal")));
            Assert.Contains("Follow the Active Pane [x]", Shell("menu-on", "menu").Split('|'));
            Assert.Equal(("false", "false"), (Shell("off-by-menu", "follow_pin"), Shell("off-by-menu", "auto_reveal")));
            Assert.Equal(("true", "true"), (Shell("on-by-file", "follow_pin"), Shell("on-by-file", "auto_reveal")));
            Assert.Contains("Follow the Active Pane [ ]", Shell("classic-menu", "menu").Split('|'));
            Assert.Equal("false", Key(run.ReadConfig(), "ui", "sidebarAutoReveal"));
        }
        finally
        {
            run.Stop();
        }
    }

    /// <summary>
    /// contextMenu.shellMenu: the command, the checkable last row of "Edit Menu…" and the file give the same state.
    /// </summary>
    [Fact]
    public async Task Windows_own_menu_switches_from_the_command_the_last_row_of_Edit_Menu_and_the_file()
    {
        var run = Prepare("three-ways-shellmenu", _ => Config(config => Section(config, "contextMenu")["shellMenu"] = false));
        try
        {
            var data = MakeData(run);
            var process = run.Start("shellmenu", string.Join(';',
                "size:1400x900",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:800",
                "shell:start",
                // The command.
                "cmd:menu.toggleShellMenu",
                "until:setting:shell-menu=on",
                "wait:400",
                "shell:on-by-command",
                "cmd:palette.show",
                "wait:300",
                "type:Windows' Shell Menu",
                "wait:600",
                "shell:palette-on",
                "cmd:overlay.close",
                "wait:300",
                // The edit mode: its last row is the setting, checked while on; a click on it is the command.
                "menu:visible.txt",
                "until:menu",
                "menu-click:Edit Menu…",
                "wait:1500",
                "shell:editor-on",
                "click:Show Windows' own menu with Shift+right-click",
                "until:setting:shell-menu=off",
                "wait:600",
                "shell:off-by-row",
                // The file, while the edit mode is open.
                "shell:edit-now",
                "until:setting:shell-menu=on",
                "wait:600",
                "shell:on-by-file",
                "cmd:overlay.close",
                "wait:500",
                "shot:done"));
            await run.WaitForStateAsync("shellmenu", "shell state", "edit-now");
            Edit(run, config => Section(config, "contextMenu")["shellMenu"] = true);
            var logs = await run.FinishAsync("shellmenu", process);

            string Shell(string label, string field) => Text(State(logs, "shell state", label), field);
            Assert.Equal(("false", "false"), (Shell("start", "shell_menu"), Shell("start", "editor_shell_check")));
            Assert.Equal("true", Shell("on-by-command", "shell_menu"));
            Assert.Contains("menu.toggleShellMenu=on", Shell("palette-on", "palette_states").Split('|'));
            Assert.Equal(("true", "true"), (Shell("editor-on", "shell_menu"), Shell("editor-on", "editor_shell_check")));
            Assert.Equal(("false", "false"), (Shell("off-by-row", "shell_menu"), Shell("off-by-row", "editor_shell_check")));
            Assert.Equal(("true", "true"), (Shell("on-by-file", "shell_menu"), Shell("on-by-file", "editor_shell_check")));
            Assert.Equal("true", Key(run.ReadConfig(), "contextMenu", "shellMenu"));
        }
        finally
        {
            run.Stop();
        }
    }
}
