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

    // ----- Gap 5: the terminal's defaults -----

    // Two cmd shells (the second may be linked), cmd the default: a shell every Windows has, and no prompt hook to wait for.
    private static JsonObject TerminalConfig(Action<JsonObject>? more = null) => Config(config =>
    {
        config["terminal"] = new JsonObject
        {
            ["defaultProfile"] = "cmd",
            ["profiles"] = new JsonArray(
                new JsonObject { ["name"] = "cmd", ["command"] = "cmd.exe" },
                new JsonObject { ["name"] = "hooked", ["command"] = "cmd.exe", ["linkable"] = true }),
        };
        more?.Invoke(config);
    });

    private const string MenuWithCmdDefault = "cmd (default)|hooked|-|Default Profile…|Restore Tabs on Start [x]|New Terminals Start Linked [ ]";

    /// <summary>
    /// terminal.defaultProfile: the picker "Terminal: Default Profile", the dock chevron's "Default Profile…" (the same picker) and the file
    /// give the same shell; the palette's row names it, the chevron menu marks it, and a new terminal starts with it.
    /// </summary>
    [Fact]
    public async Task The_default_shell_changes_from_the_palette_s_picker_the_dock_s_chevron_menu_and_the_file()
    {
        var run = Prepare("three-ways-profile", _ => TerminalConfig());
        try
        {
            var data = MakeData(run);
            var process = run.Start("profile", string.Join(';',
                "size:1400x900",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "key:ctrl+backquote",
                "until:terminals:1",
                "wait:800",
                "settings-state:start",
                // The command's pick list: type the name, Enter.
                "cmd-nowait:terminal.chooseDefaultProfile",
                "until:prompt",
                "settings-state:list",
                "type:hooked",
                "accept",
                "until:setting:default-profile=hooked",
                "wait:600",
                "settings-state:by-command",
                "cmd:palette.show",
                "wait:300",
                "type:Default Profile",
                "wait:600",
                "settings-state:palette",
                "cmd:overlay.close",
                "wait:300",
                // A new terminal starts with the shell chosen.
                "cmd:terminal.new",
                "until:terminals:2",
                "wait:800",
                "terminal-state:new-default",
                // The dock's chevron menu: its "Default Profile…" row opens the same list.
                "click:Other shells",
                "wait:500",
                "click:Default Profile…",
                "until:prompt",
                "type:cmd",
                "accept",
                "until:setting:default-profile=cmd",
                "wait:600",
                "settings-state:by-menu",
                // The file.
                "settings-state:edit-now",
                "until:setting:default-profile=hooked",
                "wait:600",
                "settings-state:by-file",
                "shot:done"));
            await run.WaitForStateAsync("profile", "settings state", "edit-now");
            Edit(run, config => Section(config, "terminal")["defaultProfile"] = "hooked");
            var logs = await run.FinishAsync("profile", process);

            string Settings(string label, string field) => Text(State(logs, "settings state", label), field);
            Assert.Equal(("cmd", MenuWithCmdDefault), (Settings("start", "default_profile"), Settings("start", "dock_menu")));
            // The pick list names the profiles, and the default one starts highlighted.
            Assert.Equal(("true", "*cmd|hooked"), (Settings("list", "prompt_open"), Settings("list", "prompt_rows")));

            Assert.Equal(("hooked", "cmd|hooked (default)|-|Default Profile…|Restore Tabs on Start [x]|New Terminals Start Linked [ ]"),
                (Settings("by-command", "default_profile"), Settings("by-command", "dock_menu")));
            Assert.Contains("terminal.chooseDefaultProfile=hooked", Settings("palette", "palette_states").Split('|'));
            // The second terminal is the default shell's: the profile the command chose.
            Assert.Contains(" hooked [", Text(State(logs, "terminal state", "new-default"), "tabs").Split(" | ")[1]);

            Assert.Equal(("cmd", MenuWithCmdDefault), (Settings("by-menu", "default_profile"), Settings("by-menu", "dock_menu")));
            Assert.Equal("hooked", Settings("by-file", "default_profile"));
            Assert.Contains("hooked (default)", Settings("by-file", "dock_menu"));
            Assert.Equal("\"hooked\"", Key(run.ReadConfig(), "terminal", "defaultProfile"));
        }
        finally
        {
            run.Stop();
        }
    }

    /// <summary>
    /// terminal.restore: the command, the dock chevron's check row "Restore Tabs on Start" and the file give the same state, and the
    /// palette's row says on or off.
    /// </summary>
    [Fact]
    public async Task Restoring_the_terminal_tabs_switches_from_the_command_the_dock_s_chevron_menu_and_the_file()
    {
        var run = Prepare("three-ways-restore", _ => TerminalConfig());
        try
        {
            var data = MakeData(run);
            var process = run.Start("restore", string.Join(';',
                "size:1400x900",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "key:ctrl+backquote",
                "until:terminals:1",
                "wait:800",
                "settings-state:start",
                "cmd:terminal.toggleRestore",
                "until:setting:restore=off",
                "wait:600",
                "settings-state:off-by-command",
                "cmd:palette.show",
                "wait:300",
                "type:Restore Tabs",
                "wait:600",
                "settings-state:palette-off",
                "cmd:overlay.close",
                "wait:300",
                "click:Other shells",
                "wait:500",
                "click:Restore Tabs on Start",
                "until:setting:restore=on",
                "wait:600",
                "settings-state:on-by-menu",
                "settings-state:edit-now",
                "until:setting:restore=off",
                "wait:600",
                "settings-state:off-by-file",
                "shot:done"));
            await run.WaitForStateAsync("restore", "settings state", "edit-now");
            Edit(run, config => Section(config, "terminal")["restore"] = false);
            var logs = await run.FinishAsync("restore", process);

            string Settings(string label, string field) => Text(State(logs, "settings state", label), field);
            Assert.Equal(("true", MenuWithCmdDefault), (Settings("start", "restore"), Settings("start", "dock_menu")));
            Assert.Equal(("false", "cmd (default)|hooked|-|Default Profile…|Restore Tabs on Start [ ]|New Terminals Start Linked [ ]"),
                (Settings("off-by-command", "restore"), Settings("off-by-command", "dock_menu")));
            Assert.Contains("terminal.toggleRestore=off", Settings("palette-off", "palette_states").Split('|'));
            Assert.Equal(("true", MenuWithCmdDefault), (Settings("on-by-menu", "restore"), Settings("on-by-menu", "dock_menu")));
            Assert.Equal("false", Settings("off-by-file", "restore"));
            Assert.Contains("Restore Tabs on Start [ ]", Settings("off-by-file", "dock_menu"));
            Assert.Equal("false", Key(run.ReadConfig(), "terminal", "restore"));
        }
        finally
        {
            run.Stop();
        }
    }

    /// <summary>
    /// terminal.defaultMode: the one toggle command, the dock chevron's check row "New Terminals Start Linked" and the file give the same
    /// mode; the palette's row says linked or locked, and a new terminal of a linkable shell starts in it.
    /// </summary>
    [Fact]
    public async Task The_mode_new_terminals_start_in_switches_from_the_command_the_dock_s_chevron_menu_and_the_file()
    {
        var run = Prepare("three-ways-mode", _ => TerminalConfig());
        try
        {
            var data = MakeData(run);
            var process = run.Start("mode", string.Join(';',
                "size:1400x900",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "key:ctrl+backquote",
                "until:terminals:1",
                "wait:800",
                "settings-state:start",
                "cmd:terminal.toggleDefaultMode",
                "until:setting:default-mode=linked",
                "wait:600",
                "settings-state:linked-by-command",
                "cmd:palette.show",
                "wait:300",
                "type:New Terminals Start",
                "wait:600",
                "settings-state:palette-linked",
                "cmd:overlay.close",
                "wait:300",
                // A new terminal of a shell that can be linked starts linked.
                "cmd:terminal.new {\"profile\":\"hooked\"}",
                "until:terminals:2",
                "wait:800",
                "terminal-state:new-linked",
                "click:Other shells",
                "wait:500",
                "click:New Terminals Start Linked",
                "until:setting:default-mode=locked",
                "wait:600",
                "settings-state:locked-by-menu",
                "settings-state:edit-now",
                "until:setting:default-mode=linked",
                "wait:600",
                "settings-state:linked-by-file",
                "shot:done"));
            await run.WaitForStateAsync("mode", "settings state", "edit-now");
            Edit(run, config => Section(config, "terminal")["defaultMode"] = "linked");
            var logs = await run.FinishAsync("mode", process);

            string Settings(string label, string field) => Text(State(logs, "settings state", label), field);
            Assert.Equal(("locked", MenuWithCmdDefault), (Settings("start", "default_mode"), Settings("start", "dock_menu")));
            Assert.Equal(("linked", "cmd (default)|hooked|-|Default Profile…|Restore Tabs on Start [x]|New Terminals Start Linked [x]"),
                (Settings("linked-by-command", "default_mode"), Settings("linked-by-command", "dock_menu")));
            Assert.Contains("terminal.toggleDefaultMode=linked", Settings("palette-linked", "palette_states").Split('|'));
            Assert.EndsWith("hooked [Left] Linked", Text(State(logs, "terminal state", "new-linked"), "tabs").Split(" | ")[1].TrimStart('*'));
            Assert.Equal(("locked", MenuWithCmdDefault), (Settings("locked-by-menu", "default_mode"), Settings("locked-by-menu", "dock_menu")));
            Assert.Equal("linked", Settings("linked-by-file", "default_mode"));
            Assert.Contains("New Terminals Start Linked [x]", Settings("linked-by-file", "dock_menu"));
            Assert.Equal("\"linked\"", Key(run.ReadConfig(), "terminal", "defaultMode"));
        }
        finally
        {
            run.Stop();
        }
    }

    // ----- Gap 6: the update settings -----

    // The submenu's text in the log (FileContextMenu.OpenSubmenu) marks only the checked row with [x]; the top-level rows and the
    // dock menu say [ ] too.
    private const string UpdateRowsDefault = "Check Automatically [x]|Install Automatically [x]|Stable Channel [x]|Preview Channel [ ]";

    // The pill that holds the flyout is shown only while an update runs, which these windows do not have: the flyout's row is
    // pressed through its automation peer (settings-do:update-flyout) and its text is read from the log.
    private static async Task<List<string>> RunUpdateToggleAsync(WindowRun run, string name, string command, string rowTitle, string query, string setting,
        string keyName)
    {
        var data = MakeData(run);
        var process = run.Start(name, string.Join(';',
            "size:1400x900",
            "pane:1",
            $"path:{data}",
            "pane:0",
            $"path:{data}",
            "wait:500",
            "settings-state:start",
            // The command.
            $"cmd:{command}",
            $"until:setting:{setting}=off",
            "wait:400",
            "settings-state:off-by-command",
            "cmd:palette.show",
            "wait:300",
            $"type:{query}",
            "wait:600",
            "settings-state:palette-off",
            "cmd:overlay.close",
            "wait:300",
            // The top row's menu: Update Settings, then the row.
            "cmd:menu.show",
            "wait:500",
            "click:Update Settings",
            "wait:400",
            "settings-state:submenu-off",
            $"click:{rowTitle}",
            $"until:setting:{setting}=on",
            "wait:400",
            "settings-state:on-by-menu",
            // The pill's flyout.
            $"settings-do:update-flyout|{rowTitle}",
            $"until:setting:{setting}=off",
            "wait:400",
            "settings-state:off-by-flyout",
            // The file.
            "settings-state:edit-now",
            $"until:setting:{setting}=on",
            "wait:400",
            "settings-state:on-by-file",
            "shot:done"));
        await run.WaitForStateAsync(name, "settings state", "edit-now");
        Edit(run, config => Section(config, "update")[keyName] = true);
        return await run.FinishAsync(name, process);
    }

    /// <summary>
    /// update.check: the command, the "Check Automatically" row of the top row's "Update Settings" menu, the same row in the update pill's
    /// flyout and the file give the same state; the palette's row says on or off.
    /// </summary>
    [Fact]
    public async Task The_automatic_update_check_switches_from_the_command_the_menu_the_pill_s_flyout_and_the_file()
    {
        var run = Prepare("three-ways-update-check", _ => Config());
        try
        {
            var logs = await RunUpdateToggleAsync(run, "updatecheck", "update.toggleCheck", "Check Automatically", "Automatic Check", "update-check", "check");

            string Settings(string label, string field) => Text(State(logs, "settings state", label), field);
            Assert.Equal(("true", UpdateRowsDefault), (Settings("start", "update_check"), Settings("start", "update_flyout")));
            Assert.Equal(("false", "Check Automatically [ ]|Install Automatically [x]|Stable Channel [x]|Preview Channel [ ]"),
                (Settings("off-by-command", "update_check"), Settings("off-by-command", "update_flyout")));
            Assert.Contains("update.toggleCheck=off", Settings("palette-off", "palette_states").Split('|'));
            Assert.Equal("Update Settings: Check Automatically|Install Automatically [x]|Stable Channel [x]|Preview Channel", Settings("submenu-off", "menu"));
            Assert.Equal(("true", UpdateRowsDefault), (Settings("on-by-menu", "update_check"), Settings("on-by-menu", "update_flyout")));
            Assert.Equal("false", Settings("off-by-flyout", "update_check"));
            Assert.Equal(("true", UpdateRowsDefault), (Settings("on-by-file", "update_check"), Settings("on-by-file", "update_flyout")));
            Assert.Equal("true", Key(run.ReadConfig(), "update", "check"));
        }
        finally
        {
            run.Stop();
        }
    }

    /// <summary>
    /// update.autoInstall: the command, the "Install Automatically" row of the "Update Settings" menu, the same row in the update pill's
    /// flyout and the file give the same state; the palette's row says on or off.
    /// </summary>
    [Fact]
    public async Task The_automatic_update_install_switches_from_the_command_the_menu_the_pill_s_flyout_and_the_file()
    {
        var run = Prepare("three-ways-update-install", _ => Config());
        try
        {
            var logs = await RunUpdateToggleAsync(run, "updateinstall", "update.toggleAutoInstall", "Install Automatically", "Automatic Install", "auto-install", "autoInstall");

            string Settings(string label, string field) => Text(State(logs, "settings state", label), field);
            Assert.Equal(("true", UpdateRowsDefault), (Settings("start", "update_auto_install"), Settings("start", "update_flyout")));
            Assert.Equal(("false", "Check Automatically [x]|Install Automatically [ ]|Stable Channel [x]|Preview Channel [ ]"),
                (Settings("off-by-command", "update_auto_install"), Settings("off-by-command", "update_flyout")));
            Assert.Contains("update.toggleAutoInstall=off", Settings("palette-off", "palette_states").Split('|'));
            Assert.Equal("Update Settings: Check Automatically [x]|Install Automatically|Stable Channel [x]|Preview Channel", Settings("submenu-off", "menu"));
            Assert.Equal(("true", UpdateRowsDefault), (Settings("on-by-menu", "update_auto_install"), Settings("on-by-menu", "update_flyout")));
            Assert.Equal("false", Settings("off-by-flyout", "update_auto_install"));
            Assert.Equal(("true", UpdateRowsDefault), (Settings("on-by-file", "update_auto_install"), Settings("on-by-file", "update_flyout")));
            Assert.Equal("true", Key(run.ReadConfig(), "update", "autoInstall"));
        }
        finally
        {
            run.Stop();
        }
    }

    /// <summary>
    /// update.channel: the pick list "Update: Channel" (the channel in effect starts highlighted), the channel rows of the "Update Settings"
    /// menu, the pill's flyout and the file give the same channel; the palette's row names it.
    /// </summary>
    [Fact]
    public async Task The_update_channel_changes_from_the_palette_s_picker_the_menu_the_pill_s_flyout_and_the_file()
    {
        var run = Prepare("three-ways-update-channel", _ => Config());
        try
        {
            var data = MakeData(run);
            var process = run.Start("updatechannel", string.Join(';',
                "size:1400x900",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "settings-state:start",
                // The command's pick list.
                "cmd-nowait:update.chooseChannel",
                "until:prompt",
                "settings-state:list",
                "type:preview",
                "accept",
                "until:setting:channel=preview",
                "wait:400",
                "settings-state:preview-by-command",
                "cmd:palette.show",
                "wait:300",
                "type:Update: Channel",
                "wait:600",
                "settings-state:palette-preview",
                "cmd:overlay.close",
                "wait:300",
                // The top row's menu: Update Settings, then Stable Channel.
                "cmd:menu.show",
                "wait:500",
                "click:Update Settings",
                "wait:400",
                "settings-state:submenu-preview",
                "click:Stable Channel",
                "until:setting:channel=stable",
                "wait:400",
                "settings-state:stable-by-menu",
                // The pill's flyout.
                "settings-do:update-flyout|Preview Channel",
                "until:setting:channel=preview",
                "wait:400",
                "settings-state:preview-by-flyout",
                // The file.
                "settings-state:edit-now",
                "until:setting:channel=stable",
                "wait:400",
                "settings-state:stable-by-file",
                "shot:done"));
            await run.WaitForStateAsync("updatechannel", "settings state", "edit-now");
            Edit(run, config => Section(config, "update")["channel"] = "stable");
            var logs = await run.FinishAsync("updatechannel", process);

            string Settings(string label, string field) => Text(State(logs, "settings state", label), field);
            Assert.Equal(("stable", UpdateRowsDefault), (Settings("start", "update_channel"), Settings("start", "update_flyout")));
            Assert.Equal(("true", "*stable|preview"), (Settings("list", "prompt_open"), Settings("list", "prompt_rows")));
            Assert.Equal(("preview", "Check Automatically [x]|Install Automatically [x]|Stable Channel [ ]|Preview Channel [x]"),
                (Settings("preview-by-command", "update_channel"), Settings("preview-by-command", "update_flyout")));
            Assert.Contains("update.chooseChannel=preview", Settings("palette-preview", "palette_states").Split('|'));
            Assert.Equal("Update Settings: Check Automatically [x]|Install Automatically [x]|Stable Channel|Preview Channel [x]", Settings("submenu-preview", "menu"));
            Assert.Equal(("stable", UpdateRowsDefault), (Settings("stable-by-menu", "update_channel"), Settings("stable-by-menu", "update_flyout")));
            Assert.Equal("preview", Settings("preview-by-flyout", "update_channel"));
            Assert.Equal(("stable", UpdateRowsDefault), (Settings("stable-by-file", "update_channel"), Settings("stable-by-file", "update_flyout")));
            Assert.Equal("\"stable\"", Key(run.ReadConfig(), "update", "channel"));
        }
        finally
        {
            run.Stop();
        }
    }

    // ----- Gap 7: the editor and the log level -----

    /// <summary>
    /// files.editor: the pick list "Preferences: Choose Editor" (Windows' default, the programs of the file, Choose…), the "Editor"
    /// submenu of the top row's menu and the file give the same editor; the palette's row names it. The file dialog of "Choose…" is
    /// answered by the snapshot step settings-do:editor-file, which a test uses in its place.
    /// </summary>
    [Fact]
    public async Task The_editor_changes_from_the_palette_s_picker_the_menu_s_Editor_submenu_and_the_file()
    {
        var run = Prepare("three-ways-editor", _ => Config(config => config["programs"] = new JsonArray(new JsonObject
        {
            ["name"] = "code",
            ["title"] = "Open in Code",
            ["command"] = "notepad.exe",
            ["args"] = new JsonArray("--wait", "{selection}"),
        })));
        try
        {
            var data = MakeData(run);
            var fromList = Path.Combine(run.Root, "tools", "listed.exe");
            var fromMenu = Path.Combine(run.Root, "tools", "menued.exe");
            var process = run.Start("editor", string.Join(';',
                "size:1400x900",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "settings-state:start",
                // The command's pick list: a program of the file.
                "cmd-nowait:preferences.chooseEditor",
                "until:prompt",
                "settings-state:list",
                "type:Open in Code",
                "accept",
                "until:setting:editor=notepad.exe",
                "wait:400",
                "settings-state:program-by-command",
                "cmd:palette.show",
                "wait:300",
                "type:Choose Editor",
                "wait:600",
                "settings-state:palette",
                "cmd:overlay.close",
                "wait:300",
                // The top row's menu: Editor, then Windows' default.
                "cmd:menu.show",
                "wait:500",
                "click:Editor",
                "wait:400",
                "settings-state:submenu",
                "click:Windows' default",
                "until:setting:editor=default",
                "wait:400",
                "settings-state:default-by-menu",
                // "Choose…" in the pick list: the file dialog is answered by the step.
                $"settings-do:editor-file|{fromList}",
                "cmd-nowait:preferences.chooseEditor",
                "until:prompt",
                "type:Choose",
                "accept",
                $"until:setting:editor={fromList}",
                "wait:400",
                "settings-state:file-by-command",
                // "Choose…" in the menu's submenu.
                $"settings-do:editor-file|{fromMenu}",
                "cmd:menu.show",
                "wait:500",
                "click:Editor",
                "wait:400",
                "settings-state:submenu-after-file",
                "click:Choose…",
                $"until:setting:editor={fromMenu}",
                "wait:400",
                "settings-state:file-by-menu",
                // The file.
                "settings-state:edit-now",
                "until:setting:editor=vim.exe",
                "wait:400",
                "settings-state:by-file",
                "shot:done"));
            await run.WaitForStateAsync("editor", "settings state", "edit-now");
            Edit(run, config => Section(config, "files")["editor"] = new JsonObject { ["command"] = "vim.exe", ["args"] = new JsonArray("-g") });
            var logs = await run.FinishAsync("editor", process);

            string Settings(string label, string field) => Text(State(logs, "settings state", label), field);
            Assert.Equal(("Windows' default", "", "Windows' default [x]|Open in Code [ ]|Choose… [ ]"),
                (Settings("start", "editor_label"), Settings("start", "editor_command"), Settings("start", "editor_choices")));
            Assert.Equal(("true", "*Windows' default|Open in Code|Choose…"), (Settings("list", "prompt_open"), Settings("list", "prompt_rows")));
            // The program's arguments without {selection}: files.editor adds the file's path itself.
            Assert.Equal(("Open in Code", "notepad.exe", "--wait", "Windows' default [ ]|Open in Code [x]|Choose… [ ]"),
                (Settings("program-by-command", "editor_label"), Settings("program-by-command", "editor_command"), Settings("program-by-command", "editor_args"),
                    Settings("program-by-command", "editor_choices")));
            Assert.Contains("preferences.chooseEditor=Open in Code", Settings("palette", "palette_states").Split('|'));
            Assert.Equal("Editor: Windows' default|Open in Code [x]|Choose…", Settings("submenu", "menu"));
            Assert.Equal(("Windows' default", ""), (Settings("default-by-menu", "editor_label"), Settings("default-by-menu", "editor_command")));
            Assert.Equal(("listed", fromList), (Settings("file-by-command", "editor_label"), Settings("file-by-command", "editor_command")));
            Assert.Equal("Editor: Windows' default|listed [x]|Open in Code|Choose…", Settings("submenu-after-file", "menu"));
            Assert.Equal(("menued", fromMenu), (Settings("file-by-menu", "editor_label"), Settings("file-by-menu", "editor_command")));
            Assert.Equal(("vim", "vim.exe", "-g", "Windows' default [ ]|vim [x]|Open in Code [ ]|Choose… [ ]"),
                (Settings("by-file", "editor_label"), Settings("by-file", "editor_command"), Settings("by-file", "editor_args"), Settings("by-file", "editor_choices")));
            Assert.Equal("\"vim.exe\"", ((JsonObject)run.ReadConfig()["files"]!["editor"]!)["command"]!.ToJsonString());
        }
        finally
        {
            run.Stop();
        }
    }

    /// <summary>
    /// logging.level: the pick list "Diagnostics: Log Level" (the level in effect starts highlighted), the "Log Level" submenu of the top
    /// row's menu and the file give the same level; the palette's row names it.
    /// </summary>
    [Fact]
    public async Task The_log_level_changes_from_the_palette_s_picker_the_menu_s_Log_Level_submenu_and_the_file()
    {
        var run = Prepare("three-ways-loglevel", _ => Config());
        try
        {
            var data = MakeData(run);
            var process = run.Start("loglevel", string.Join(';',
                "size:1400x900",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "settings-state:start",
                "cmd-nowait:diagnostics.chooseLogLevel",
                "until:prompt",
                "settings-state:list",
                "type:debug",
                "accept",
                "until:setting:log-level=debug",
                "wait:400",
                "settings-state:debug-by-command",
                "cmd:palette.show",
                "wait:300",
                "type:Log Level",
                "wait:600",
                "settings-state:palette",
                "cmd:overlay.close",
                "wait:300",
                // The top row's menu: Log Level, then Warn.
                "cmd:menu.show",
                "wait:500",
                "click:Log Level",
                "wait:400",
                "settings-state:submenu",
                "click:Warn",
                "until:setting:log-level=warn",
                "wait:400",
                "settings-state:warn-by-menu",
                // The file.
                "settings-state:edit-now",
                "until:setting:log-level=trace",
                "wait:400",
                "settings-state:trace-by-file",
                "shot:done"));
            await run.WaitForStateAsync("loglevel", "settings state", "edit-now");
            Edit(run, config => Section(config, "logging")["level"] = "trace");
            var logs = await run.FinishAsync("loglevel", process);

            string Settings(string label, string field) => Text(State(logs, "settings state", label), field);
            Assert.Equal("info", Settings("start", "log_level"));
            Assert.Equal(("true", "trace|debug|*info|warn|error"), (Settings("list", "prompt_open"), Settings("list", "prompt_rows")));
            Assert.Equal("debug", Settings("debug-by-command", "log_level"));
            Assert.Contains("diagnostics.chooseLogLevel=debug", Settings("palette", "palette_states").Split('|'));
            Assert.Equal("Log Level: Trace|Debug [x]|Info|Warn|Error", Settings("submenu", "menu"));
            Assert.Equal("warn", Settings("warn-by-menu", "log_level"));
            Assert.Equal("trace", Settings("trace-by-file", "log_level"));
            Assert.Equal("\"trace\"", Key(run.ReadConfig(), "logging", "level"));
        }
        finally
        {
            run.Stop();
        }
    }
}
