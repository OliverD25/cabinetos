using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CabinetOS.Core.Ipc;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Phase 16: the shell of the creator's SHELL_REDESIGN.md on a real window and
/// core (docs/ui.md, "The top row", "The breadcrumb row", "Find in pane",
/// "Quick Open"). Each test opens a window on the desktop, so they run only
/// with <c>CABINETOS_UI_E2E=1</c> and a built window (Debug) and core. The
/// window's snapshot steps drive it and its log line "shell state" says what
/// the top row and each pane show.
/// </summary>
public class ShellEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    [Fact]
    public async Task The_top_row_fits_924_px_find_filters_one_pane_and_a_tab_keeps_its_place()
    {
        var (run, root, data) = Prepare("shell-find");
        try
        {
            var deep = Directory.CreateDirectory(Path.Combine(data, "Users", "dev", "Projects", "fileforge")).FullName;
            var many = Directory.CreateDirectory(Path.Combine(data, "many")).FullName;
            for (var i = 0; i < 300; i++)
            {
                File.WriteAllText(Path.Combine(many, $"file-{i:000}.txt"), "x");
            }
            // §7's paths: a folder of five parts like the handout's C:\Users\dev\Projects\fileforge, which every user has
            // (C:\Users\<name>\AppData\Local), and one of seven parts above the test's own folder.
            var fiveParts = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var sevenParts = root;
            while (sevenParts.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length > 7)
            {
                sevenParts = Path.GetDirectoryName(sevenParts)!;
            }
            var process = run.Start("find", string.Join(';',
                "size:924x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "shell:start",
                // §7: Ctrl+F filters only the active pane; Enter puts the cursor on the first match; Esc shows every row again.
                "find:alpha",
                "shell:found",
                "find-key:enter",
                "shell:entered",
                "find:zzz",
                "shell:none",
                "find-key:esc",
                "shell:closed",
                // §7: in a 440 px pane the five-part path renders whole; a seven-part one collapses to C: › … › parent › current.
                "size:1136x700",
                "wait:500",
                $"path:{fiveParts}",
                "shell:five-parts",
                $"path:{sevenParts}",
                "shell:seven-parts",
                "size:924x700",
                "wait:500",
                // §7: the first tab's folder, cursor and scroll come back after a second tab went elsewhere.
                $"path:{many}",
                "scroll:4",
                "shell:scrolled",
                "tab:new",
                $"path:{deep}",
                "shell:second",
                "tab:select 0",
                "wait:600",
                "shell:first-again",
                // The first close takes "many" away; closing the last tab of a pane does nothing; Ctrl+9 with one tab does nothing either.
                "tab:close",
                "tab:close",
                "cmd:tab.select {\"tab\":8}",
                "shell:one-tab",
                // Near the window's least width the Quick Open chip still shows and the title gives way to it.
                "size:620x700",
                "wait:500",
                "shell:narrow",
                "shot:done"));
            var logs = await run.FinishAsync("find", process, "done");

            State(logs, "start", state =>
            {
                // §7: at 924 px in dual mode the top row shows every control and nothing lies over anything else.
                Assert.Equal(40, state.GetProperty("top_row").GetDouble());
                Assert.Equal($"CabinetOS · {Path.GetFileName(data)}", state.GetProperty("title").GetString());
                TopRowFits(state);
                Assert.Equal(5, state.GetProperty("pane0_count").GetInt32());
                // §7: each pane's toolbar shows nav, the drive chip, the free space and Find, none of them cut off.
                foreach (var pane in new[] { "pane0", "pane1" })
                {
                    Assert.Equal((pane, "back forward up drive free find"), (pane, state.GetProperty($"{pane}_toolbar_items").GetString()));
                    Assert.Equal((pane, 28.0, 24.0), (pane, state.GetProperty($"{pane}_toolbar_row").GetDouble(), state.GetProperty($"{pane}_path_row").GetDouble()));
                    Assert.Equal((pane, "C:"), (pane, state.GetProperty($"{pane}_drive").GetString()));
                    Assert.EndsWith(" free", state.GetProperty($"{pane}_free").GetString());
                    Assert.Equal((pane, "*.*"), (pane, state.GetProperty($"{pane}_filter").GetString()));
                }
                // The active pane's toolbar has the 9 % fill, the other's 5 %.
                Assert.StartsWith("CbFrontTabFillBrush ", state.GetProperty("pane0_toolbar_fill").GetString());
                Assert.StartsWith("CbFrontTabInactiveFillBrush ", state.GetProperty("pane1_toolbar_fill").GetString());
            });
            State(logs, "found", state =>
            {
                Assert.Equal("alpha", state.GetProperty("pane0_find").GetString());
                Assert.True(state.GetProperty("pane0_find_open").GetBoolean());
                Assert.Equal(2, state.GetProperty("pane0_shown").GetInt32());
                // §7: the path row's filter label reads *query*, and the toolbar's Find shows the find open.
                Assert.Equal("*alpha*", state.GetProperty("pane0_filter").GetString());
                Assert.True(state.GetProperty("pane0_find_button_open").GetBoolean());
                Assert.Equal("*.*", state.GetProperty("pane1_filter").GetString());
                // The widget drops from the toolbar row's right end and lies over the path row.
                Assert.Equal(state.GetProperty("pane0_toolbar_bottom").GetDouble(), state.GetProperty("pane0_find_top").GetDouble(), 0.6);
                Assert.InRange(state.GetProperty("pane0_find_right_gap").GetDouble(), 0, 8);
                // The other pane shows every row: the find is the active pane's only.
                Assert.Equal(state.GetProperty("pane1_count").GetInt32(), state.GetProperty("pane1_shown").GetInt32());
                Assert.False(state.GetProperty("pane1_find_open").GetBoolean());
            });
            State(logs, "entered", state =>
            {
                Assert.Equal("1 of 2", state.GetProperty("pane0_find_count").GetString());
                Assert.StartsWith("alpha", state.GetProperty("pane0_cursor").GetString(), StringComparison.OrdinalIgnoreCase);
                Assert.True(state.GetProperty("pane0_find_open").GetBoolean(), "Enter keeps the widget");
            });
            State(logs, "none", state =>
            {
                Assert.Equal(0, state.GetProperty("pane0_shown").GetInt32());
                Assert.Equal("0 of 0", state.GetProperty("pane0_find_count").GetString());
            });
            State(logs, "closed", state =>
            {
                Assert.False(state.GetProperty("pane0_find_open").GetBoolean());
                Assert.Equal("", state.GetProperty("pane0_find").GetString());
                Assert.Equal(5, state.GetProperty("pane0_shown").GetInt32());
                // §7: Esc restores the full list and *.*.
                Assert.Equal("*.*", state.GetProperty("pane0_filter").GetString());
                Assert.False(state.GetProperty("pane0_find_button_open").GetBoolean());
                // The selection survives the filter: the cursor stays on the match Enter chose.
                Assert.StartsWith("alpha", state.GetProperty("pane0_cursor").GetString(), StringComparison.OrdinalIgnoreCase);
            });
            State(logs, "five-parts", state =>
            {
                Assert.InRange(state.GetProperty("pane0_width").GetDouble(), 438, 442);
                var parts = fiveParts.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(5, parts.Length);
                Assert.Equal(string.Join(" › ", parts), state.GetProperty("pane0_crumbs").GetString());
                Assert.True(state.GetProperty("pane0_crumbs_fit").GetBoolean(), "the five-part path does not fit its 440 px pane");
            });
            State(logs, "seven-parts", state =>
            {
                var parts = sevenParts.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(7, parts.Length);
                Assert.Equal($"{parts[0]} › … › {parts[^2]} › {parts[^1]}", state.GetProperty("pane0_crumbs").GetString());
                Assert.True(state.GetProperty("pane0_crumbs_fit").GetBoolean());
            });
            var scrolled = 0.0;
            string? cursor = null;
            State(logs, "scrolled", state =>
            {
                scrolled = state.GetProperty("pane0_scroll").GetDouble();
                cursor = state.GetProperty("pane0_cursor").GetString();
                Assert.True(scrolled > 100, $"the list scrolled {scrolled} px");
            });
            State(logs, "second", state =>
            {
                Assert.Equal(deep, state.GetProperty("pane0_path").GetString(), ignoreCase: true);
                Assert.Equal("C: › … › Projects › fileforge", state.GetProperty("pane0_crumbs").GetString());
                Assert.Equal("many | *fileforge", state.GetProperty("pane0_tabs").GetString());
                // §7: the tab in front and the toolbar row under it share one fill, so they read as one surface.
                Assert.Equal(state.GetProperty("pane0_toolbar_fill").GetString(), state.GetProperty("pane0_tab_fill").GetString());
                Assert.StartsWith("CbFrontTabFillBrush ", state.GetProperty("pane0_tab_fill").GetString());
                Assert.Equal(state.GetProperty("pane1_toolbar_fill").GetString(), state.GetProperty("pane1_tab_fill").GetString());
                Assert.Equal("folder | *folder,close,32", state.GetProperty("pane0_tab_look").GetString());
            });
            State(logs, "first-again", state =>
            {
                Assert.Equal(many, state.GetProperty("pane0_path").GetString(), ignoreCase: true);
                Assert.Equal(cursor, state.GetProperty("pane0_cursor").GetString());
                Assert.InRange(state.GetProperty("pane0_scroll").GetDouble(), scrolled - 2, scrolled + 2);
                Assert.Equal("*many | fileforge", state.GetProperty("pane0_tabs").GetString());
            });
            State(logs, "one-tab", state =>
            {
                Assert.Equal("*fileforge", state.GetProperty("pane0_tabs").GetString());
                Assert.Equal(deep, state.GetProperty("pane0_path").GetString(), ignoreCase: true);
                Assert.True(state.GetProperty("pane0_tab_row").GetDouble() > 0, "one tab still has its strip");
            });
            State(logs, "narrow", state =>
            {
                TopRowFits(state);
                Assert.Equal(40, state.GetProperty("top_row").GetDouble());
            });
            Assert.Contains(logs, l => Message(l) == "find opened");
            Assert.Contains(logs, l => Message(l) == "find closed");
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    [Fact]
    public async Task Quick_Open_finds_in_the_repository_opens_here_or_in_the_other_pane_and_switches_to_commands()
    {
        var (run, root, data) = Prepare("shell-quick-open");
        try
        {
            // A repository on a branch: the sidebar's workspace row shows it, and Quick Open searches the whole repository from a folder in it.
            var project = Directory.CreateDirectory(Path.Combine(data, "proj")).FullName;
            Directory.CreateDirectory(Path.Combine(project, ".git"));
            File.WriteAllText(Path.Combine(project, ".git", "HEAD"), "ref: refs/heads/phase-16\n");
            var docs = Directory.CreateDirectory(Path.Combine(project, "docs")).FullName;
            File.WriteAllText(Path.Combine(docs, "notes.md"), "x");
            var app = Directory.CreateDirectory(Path.Combine(project, "src", "app")).FullName;
            File.WriteAllText(Path.Combine(app, "main.rs"), "x");
            var process = run.Start("quick", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{Path.Combine(project, "src")}",
                // The core's answer about the workspace (the branch) comes after the folder is listed; a busy machine is late with it.
                "until:workspace",
                "shell:header",
                // §7: the sidebar header opens the workspace dropdown at its full width; picking the workspace goes to its
                // root in the left pane's tab in front. With the sidebar hidden, Ctrl+K Ctrl+W opens it under the top row.
                "click:Workspace Default, branch phase-16",
                "wait:300",
                "shell:ws-menu",
                "click:Default",
                "wait:800",
                "shell:ws-picked",
                "cmd:view.toggleSidebar",
                "wait:300",
                "cmd:workspace.switch",
                "wait:300",
                "shell:ws-collapsed",
                "cmd:overlay.close",
                "cmd:view.toggleSidebar",
                "wait:300",
                "quick-open:notes",
                "shell:typed",
                "quick-open-key:enter",
                "shell:opened",
                "quick-open:main.rs",
                "quick-open-key:ctrl+enter",
                "shell:other",
                "quick-open:>tab",
                "wait:300",
                "shell:switched",
                "cmd:overlay.close",
                "pane:1",
                $"path:{data}",
                "until:workspace",
                "shell:no-repository",
                "shot:done"));
            var logs = await run.FinishAsync("quick", process, "done");

            State(logs, "header", state =>
            {
                Assert.Equal("phase-16", state.GetProperty("branch").GetString());
                Assert.Equal(project, state.GetProperty("workspace_root").GetString(), ignoreCase: true);
                // The sidebar's first row shows the workspace and its branch, at the default look's 28 px.
                Assert.True(state.GetProperty("workspace_header").GetBoolean());
                Assert.Equal(28, state.GetProperty("workspace_header_height").GetDouble());
                Assert.Equal("phase-16", state.GetProperty("workspace_header_branch").GetString());
            });
            double headerLeft = 0, headerWidth = 0, headerBottom = 0;
            State(logs, "ws-menu", state =>
            {
                Assert.Equal("Default|Open folder as workspace…", state.GetProperty("menu").GetString());
                headerLeft = state.GetProperty("workspace_header_left").GetDouble();
                headerWidth = state.GetProperty("workspace_header_width").GetDouble();
                headerBottom = state.GetProperty("workspace_header_bottom").GetDouble();
            });
            var menus = logs.Where(l => Message(l) == "workspace menu shown").ToList();
            Assert.Equal(2, menus.Count);
            // Anchored to the row's full width (at least the design's 220 px), right under it.
            Assert.Equal(headerLeft, Field(menus[0], "left").GetDouble(), 0.6);
            Assert.Equal(Math.Max(220, headerWidth), Field(menus[0], "width").GetDouble(), 0.6);
            Assert.Equal(headerBottom, Field(menus[0], "top").GetDouble(), 0.6);
            State(logs, "ws-picked", state =>
            {
                Assert.Equal(project, state.GetProperty("pane0_path").GetString(), ignoreCase: true);
                Assert.Equal("", state.GetProperty("menu").GetString());
            });
            State(logs, "ws-collapsed", state =>
            {
                Assert.False(state.GetProperty("sidebar_shown").GetBoolean());
                Assert.Equal("Default|Open folder as workspace…", state.GetProperty("menu").GetString());
            });
            // With the sidebar hidden: under the top row, at the menu's own width.
            Assert.Equal(40, Field(menus[1], "top").GetDouble(), 0.6);
            Assert.Equal(JsonValueKind.Null, Field(menus[1], "width").ValueKind);
            State(logs, "typed", state =>
            {
                Assert.True(state.GetProperty("quick_open").GetBoolean());
                Assert.StartsWith(@"notes.md (proj\docs)", state.GetProperty("quick_open_rows").GetString());
                Assert.Equal(0, state.GetProperty("quick_open_highlight").GetInt32());
            });
            State(logs, "opened", state =>
            {
                // Enter: the file's folder in the active pane, the file under the cursor, Quick Open gone.
                Assert.False(state.GetProperty("quick_open").GetBoolean());
                Assert.Equal(0, state.GetProperty("active_pane").GetInt32());
                Assert.Equal(docs, state.GetProperty("pane0_path").GetString(), ignoreCase: true);
                Assert.Equal("notes.md", state.GetProperty("pane0_cursor").GetString());
            });
            State(logs, "other", state =>
            {
                // Ctrl+Enter: the other pane, which becomes the active one.
                Assert.Equal(1, state.GetProperty("active_pane").GetInt32());
                Assert.Equal(app, state.GetProperty("pane1_path").GetString(), ignoreCase: true);
                Assert.Equal("main.rs", state.GetProperty("pane1_cursor").GetString());
                Assert.Equal(docs, state.GetProperty("pane0_path").GetString(), ignoreCase: true);
            });
            State(logs, "switched", state =>
            {
                // ">" typed first: the command palette with the rest of the text.
                Assert.False(state.GetProperty("quick_open").GetBoolean());
                Assert.True(state.GetProperty("palette_open").GetBoolean());
            });
            State(logs, "no-repository", state =>
            {
                Assert.Equal("", state.GetProperty("branch").GetString());
                Assert.Equal(data, state.GetProperty("workspace_root").GetString(), ignoreCase: true);
            });
            Assert.Contains(logs, l => Message(l) == "quick open shown");
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    [Fact]
    public async Task The_menus_come_from_the_registry_the_crumbs_navigate_and_settings_open_with_the_editor()
    {
        // An editor found nowhere: Settings must say so and open nothing else, so no Notepad opens here.
        var (run, root, data) = Prepare("shell-menus",
            """{ "version": 1, "ui": { "dualPane": true }, "files": { "editor": { "command": "cabinetos-no-such-editor.exe", "args": [] } } }""");
        try
        {
            var deep = Directory.CreateDirectory(Path.Combine(data, "Users", "dev", "Projects", "fileforge")).FullName;
            var projects = Path.GetDirectoryName(deep)!;
            var process = run.Start("menus", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{deep}",
                // The hamburger, found by its accessible name as a screen reader finds it; Esc closes it.
                "click:Menu",
                "wait:300",
                "shell:menu",
                "cmd:overlay.close",
                "shell:menu-closed",
                "click:Workspace Default",
                "wait:300",
                "shell:workspace",
                "cmd:overlay.close",
                // Ctrl+K W opens the same dropdown from the keyboard.
                "cmd:workspace.switch",
                "shell:workspace-key",
                "cmd:overlay.close",
                // A click on a crumb goes there; Back comes back; Up goes to the parent; Ctrl+L makes the row a text box, Esc
                // ends it. The toolbar's drive chip opens the pane's drive list, which Esc closes.
                $"click:{projects}",
                "wait:400",
                "shell:crumb",
                "click:Back",
                "wait:400",
                "shell:back",
                "click:Up",
                "wait:400",
                "shell:up",
                "click:Drive C:",
                "wait:400",
                "shell:drives",
                "cmd:overlay.close",
                "wait:300",
                "shell:drives-closed",
                "cmd:go.toPath",
                "shell:editing",
                "cmd:overlay.close",
                "shell:edited",
                // The toolbar's Find opens the pane's find and closes it again; a click on the filter label opens it too.
                "click:Find in pane",
                "wait:300",
                "shell:find-button",
                "click:Find in pane",
                "wait:300",
                "shell:find-button-off",
                "click:Filter",
                "wait:300",
                "shell:filter-click",
                "cmd:overlay.close",
                "cmd:settings.open",
                "wait:1000",
                "shot:done"));
            var logs = await run.FinishAsync("menus", process, "done");

            // The registry's titles, as the palette shows them: a rename there shows here too. After Toggle Sidebar come the preferences
            // that were in the file only (Phase 23): Layout with its three, and the two toggles with their state.
            State(logs, "menu", state => Assert.Equal(
                "New Tab|New Folder|Find in Pane|Go to Path…|Toggle Sidebar|Layout (Classic Layout [x], Terminal on the Right, Activity Rail)|Show Hidden Files [ ]"
                + "|Follow the Active Pane [x]|Browse Extensions|Open Keyboard Shortcuts|Check for Updates",
                state.GetProperty("menu").GetString()));
            State(logs, "menu-closed", state => Assert.Equal("", state.GetProperty("menu").GetString()));
            State(logs, "workspace", state => Assert.Equal("Default|Open folder as workspace…", state.GetProperty("menu").GetString()));
            State(logs, "workspace-key", state => Assert.Equal("Default|Open folder as workspace…", state.GetProperty("menu").GetString()));
            State(logs, "crumb", state =>
            {
                Assert.Equal(projects, state.GetProperty("pane0_path").GetString(), ignoreCase: true);
                Assert.Equal("back - up", state.GetProperty("pane0_nav").GetString());
            });
            State(logs, "back", state =>
            {
                Assert.Equal(deep, state.GetProperty("pane0_path").GetString(), ignoreCase: true);
                Assert.Equal("back forward up", state.GetProperty("pane0_nav").GetString());
            });
            State(logs, "up", state => Assert.Equal(projects, state.GetProperty("pane0_path").GetString(), ignoreCase: true));
            State(logs, "drives", state => Assert.True(state.GetProperty("prompt_open").GetBoolean(), "the drive chip opened no drive list"));
            State(logs, "drives-closed", state => Assert.False(state.GetProperty("prompt_open").GetBoolean()));
            Assert.Contains(logs, l => Message(l) == "prompt shown" && Field(l, "label").GetString() == "Drives");
            Assert.Contains(logs, l => Message(l) == "command executed" && Field(l, "command").GetString() == "go.chooseDriveLeft");
            State(logs, "editing", state => Assert.True(state.GetProperty("pane0_editing").GetBoolean()));
            State(logs, "edited", state => Assert.False(state.GetProperty("pane0_editing").GetBoolean()));
            State(logs, "find-button", state => Assert.True(state.GetProperty("pane0_find_open").GetBoolean() && state.GetProperty("pane0_find_button_open").GetBoolean()));
            State(logs, "find-button-off", state => Assert.False(state.GetProperty("pane0_find_open").GetBoolean() || state.GetProperty("pane0_find_button_open").GetBoolean()));
            State(logs, "filter-click", state => Assert.True(state.GetProperty("pane0_find_open").GetBoolean()));
            Assert.Contains(logs, l => Message(l) == "notice shown" && Field(l, "text").GetString()!.StartsWith("Cannot edit cabinetos.json", StringComparison.Ordinal));
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    // An install the core may update, and a feed with a newer version (docs/ui.md, "Updates"): a copy of the core with
    // release.json 0.1.0 beside it; the stable channel's latest.json names 0.2.0, its notes and its zip (the two programs
    // in it are stand-ins). No daily check: only the command checks, so nothing races it. Returns the window's run on
    // that core, and the install folder.
    private static (Run Run, string Install) UpdateSetup(string root, bool autoInstall)
    {
        var core = CoreLauncher.Find(Path.Combine(Repo.Root, "ui"), Environment.GetEnvironmentVariable, File.Exists)!;
        var install = Directory.CreateDirectory(Path.Combine(root, "install")).FullName;
        File.Copy(core, Path.Combine(install, "cabinetos-core.exe"));
        File.WriteAllText(Path.Combine(install, "release.json"), """{"product":"CabinetOS","version":"0.1.0"}""");
        var feed = Path.Combine(root, "feed");
        var channel = Directory.CreateDirectory(Path.Combine(feed, "stable")).FullName;
        Directory.CreateDirectory(Path.Combine(channel, "files"));
        var zip = Path.Combine(channel, "files", "CabinetOS-0.2.0-win-x64.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var (name, text) in new[] { ("release.json", """{"product":"CabinetOS","version":"0.2.0"}"""), ("CabinetOS.exe", "window"), ("cabinetos-core.exe", "core") })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        File.WriteAllText(Path.Combine(channel, "notes-0.2.0.md"), "### Added\n\n- Updates from **inside** the app, see [the guide](docs/release.md).\n- `cabinetos-cli update`\n");
        var latest = new Dictionary<string, object>
        {
            ["schemaVersion"] = 1,
            ["channel"] = "stable",
            ["version"] = "0.2.0",
            ["published"] = "2026-10-01",
            ["zip"] = new Dictionary<string, object>
            {
                ["url"] = "files/CabinetOS-0.2.0-win-x64.zip",
                ["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zip))),
                ["size"] = new FileInfo(zip).Length,
            },
            ["notes"] = new Dictionary<string, object> { ["url"] = "notes-0.2.0.md" },
            ["requires"] = new Dictionary<string, object> { ["windowsAppRuntime"] = "2.5", ["dotnet"] = "10.0" },
        };
        File.WriteAllText(Path.Combine(channel, "latest.json"), JsonSerializer.Serialize(latest));
        File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["version"] = 1,
            ["ui"] = new Dictionary<string, object> { ["dualPane"] = true },
            ["update"] = new Dictionary<string, object> { ["check"] = false, ["source"] = feed, ["autoInstall"] = autoInstall },
        }));
        var exe = Path.Combine(Repo.Root, "ui", "CabinetOS", "bin", "x64", "Debug", "net10.0-windows10.0.22621.0", "win-x64", "CabinetOS.exe");
        return (new Run(root, exe, Path.Combine(install, "cabinetos-core.exe")), install);
    }

    [Fact]
    public async Task With_auto_install_off_a_download_opens_its_dialog_once_and_Later_keeps_the_pill_and_the_dot()
    {
        var (_, root, _) = Prepare("shell-update");
        try
        {
            var (run, install) = UpdateSetup(root, autoInstall: false);
            var updateDir = Path.Combine(root, "update");

            var process = run.Start("update", string.Join(';',
                "size:1200x700",
                "shell:start",
                "cmd-nowait:update.check",
                "wait:5000",
                "shell:dialog",
                "shot:dialog",
                // Esc closes the dialog as Later does: a day's snooze; the pill and the dot stay.
                "dismiss",
                "wait:1500",
                "shell:later",
                "shot:done"));
            var logs = await run.FinishAsync("update", process, "done");

            State(logs, "start", state => Assert.Equal(("", false), (state.GetProperty("update_pill").GetString(), state.GetProperty("update_dot").GetBoolean())));
            var shown = Assert.Single(logs, l => Message(l) == "update dialog shown");
            Assert.Equal(("0.2.0", true, 3), (Field(shown, "version").GetString(), Field(shown, "automatic").GetBoolean(), Field(shown, "notes_blocks").GetInt32()));
            Assert.Contains(logs, l => Message(l) == "dialog shown" && Field(l, "title").GetString() == "CabinetOS 0.2.0 is ready");
            Assert.Contains(logs, l => Message(l) == "update snoozed");
            foreach (var label in new[] { "dialog", "later" })
            {
                State(logs, label, state => Assert.Equal(("Update ready \u00b7 Restart", true),
                    (state.GetProperty("update_pill").GetString(), state.GetProperty("update_dot").GetBoolean())));
            }
            Assert.Contains("\"snoozedUntilMs\"", File.ReadAllText(Path.Combine(updateDir, "state.json")), StringComparison.Ordinal);
            // Nothing was swapped: Later leaves the install as it was, and the status bar's notice never showed.
            Assert.Equal(["cabinetos-core.exe", "release.json"], Directory.GetFileSystemEntries(install).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(logs, l => Message(l) == "update notice shown");
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }
    }

    [Fact]
    public async Task With_auto_install_the_download_is_swapped_in_and_the_status_bar_only_asks_for_a_restart()
    {
        var (_, root, _) = Prepare("shell-auto-update");
        try
        {
            var (run, install) = UpdateSetup(root, autoInstall: true);
            var process = run.Start("auto", string.Join(';',
                "size:1200x700",
                "shell:start",
                "cmd-nowait:update.check",
                "wait:5000",
                "shell:installed",
                "shot:notice",
                // Later: the notice goes, the session runs on; the pill and the dot still offer the restart.
                "click:Later",
                "wait:500",
                "shell:later",
                "shot:done"));
            var logs = await run.FinishAsync("auto", process, "done");

            State(logs, "start", state => Assert.Equal("", state.GetProperty("update_notice").GetString()));
            var notice = Assert.Single(logs, l => Message(l) == "update notice shown");
            Assert.Equal(("CabinetOS 0.2.0 is installed; restart to use it", "0.2.0", false),
                (Field(notice, "text").GetString(), Field(notice, "version").GetString(), Field(notice, "failed").GetBoolean()));
            Assert.DoesNotContain(logs, l => Message(l) == "update dialog shown");
            Assert.DoesNotContain(logs, l => Message(l) == "update state" && Field(l, "state").GetString() == "downloaded");
            State(logs, "installed", state => Assert.Equal(("CabinetOS 0.2.0 is installed; restart to use it", "", true),
                (state.GetProperty("update_notice").GetString(), state.GetProperty("update_pill").GetString(), state.GetProperty("update_dot").GetBoolean())));
            Assert.Contains(logs, l => Message(l) == "update notice closed");
            State(logs, "later", state => Assert.Equal(("", "Update ready · Restart", true),
                (state.GetProperty("update_notice").GetString(), state.GetProperty("update_pill").GetString(), state.GetProperty("update_dot").GetBoolean())));
            // The swap ran while the core ran from the folder: 0.2.0 in place, 0.1.0 and the running core in previous\.
            Assert.Equal("window", File.ReadAllText(Path.Combine(install, "CabinetOS.exe")));
            Assert.Contains("0.2.0", File.ReadAllText(Path.Combine(install, "release.json")), StringComparison.Ordinal);
            Assert.Contains("0.1.0", File.ReadAllText(Path.Combine(install, "previous", "release.json")), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(install, "previous", "cabinetos-core.exe")));
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }
    }

    [Fact]
    public async Task A_swap_that_fails_says_so_in_the_status_bar_and_the_old_version_stays()
    {
        var (_, root, _) = Prepare("shell-failed-update");
        try
        {
            var (run, install) = UpdateSetup(root, autoInstall: true);
            // A file of the install held open without delete sharing: the swap cannot move it aside, and puts back
            // what it moved.
            using var held = new FileStream(Path.Combine(install, "release.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            var process = run.Start("failed", string.Join(';',
                "size:1200x700",
                "cmd-nowait:update.check",
                "wait:5000",
                "shell:failed",
                "shot:failed",
                "click:Close",
                "wait:500",
                "shell:closed",
                "shot:done"));
            var logs = await run.FinishAsync("failed", process, "done");

            var notice = Assert.Single(logs, l => Message(l) == "update notice shown");
            Assert.Equal(("CabinetOS 0.2.0 could not be installed; 0.1.0 keeps running", true),
                (Field(notice, "text").GetString(), Field(notice, "failed").GetBoolean()));
            Assert.Contains("nothing was changed", Field(notice, "reason").GetString(), StringComparison.Ordinal);
            State(logs, "failed", state => Assert.Equal(("CabinetOS 0.2.0 could not be installed; 0.1.0 keeps running", ""),
                (state.GetProperty("update_notice").GetString(), state.GetProperty("update_pill").GetString())));
            State(logs, "closed", state => Assert.Equal("", state.GetProperty("update_notice").GetString()));
            Assert.Equal(["cabinetos-core.exe", "release.json"], Directory.GetFileSystemEntries(install).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }
    }

    // The top row as the shell state logs it: the chip shows at its height, the title ends before it, and the chip ends
    // before the view buttons, which end before Windows' caption buttons.
    private static void TopRowFits(JsonElement state)
    {
        Assert.True(state.GetProperty("chip_visible").GetBoolean(), "the Quick Open chip is not shown");
        Assert.Equal(24, state.GetProperty("chip_height").GetDouble());
        Assert.Equal("Ctrl+P", state.GetProperty("chip_keys").GetString());
        var chipLeft = state.GetProperty("chip_left").GetDouble();
        Assert.True(state.GetProperty("title_right").GetDouble() <= chipLeft, $"the title lies over the chip at {chipLeft}");
        Assert.True(state.GetProperty("chip_right").GetDouble() <= state.GetProperty("right_cluster_end").GetDouble(), "the chip lies over the view buttons");
        Assert.True(state.GetProperty("right_cluster_end").GetDouble() <= state.GetProperty("caption_start").GetDouble() + 0.5, "the view buttons lie over the caption buttons");
    }

    private sealed class Run(string root, string exe, string core)
    {
        private readonly List<Process> _started = [];

        // Starts the window on the run's configuration; the snapshot steps are the test's script.
        public Process Start(string name, string steps)
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            start.Environment["CABINETOS_CORE_EXE"] = core;
            start.Environment["CABINETOS_CONFIG"] = Path.Combine(root, "config", "cabinetos.json");
            start.Environment["CABINETOS_LOG_DIR"] = Path.Combine(root, "logs-" + name);
            start.Environment["CABINETOS_THEMES_DIR"] = Path.Combine(root, "themes");
            start.Environment["CABINETOS_UNDO_DIR"] = Path.Combine(root, "undo");
            start.Environment["CABINETOS_PLUGINS_DIR"] = Path.Combine(root, "plugins");
            start.Environment["CABINETOS_PLUGINS_DATA_DIR"] = Path.Combine(root, "plugins-data");
            start.Environment["CABINETOS_MARKETPLACE_DIR"] = Path.Combine(root, "marketplace");
            start.Environment["CABINETOS_WEBVIEW2_DIR"] = Path.Combine(root, "webview2");
            start.Environment["CABINETOS_TOOLS_DIR"] = Path.Combine(root, "tools");
            start.Environment["CABINETOS_UI_SNAPSHOT"] = Path.Combine(root, "shots-" + name);
            start.Environment["CABINETOS_UI_SNAPSHOT_STEPS"] = steps;
            // The updater's folder; the core takes it from the window's environment (docs/release.md, "Updates").
            start.Environment["CABINETOS_UPDATE_DIR"] = Path.Combine(root, "update");
            var process = Process.Start(start)!;
            _started.Add(process);
            return process;
        }

        // Waits for the last snapshot, closes the window the way a user does and returns the UI's log lines.
        public async Task<List<string>> FinishAsync(string name, Process process, string lastShot)
        {
            var shot = Path.Combine(root, "shots-" + name, lastShot + ".png");
            await WaitForAsync(() => File.Exists(shot), $"the {name} window's last snapshot", TimeSpan.FromSeconds(90));
            process.CloseMainWindow();
            Assert.True(process.WaitForExit(20_000), $"the {name} window did not close");
            var logs = LogFiles.Ui(Path.Combine(root, "logs-" + name));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "logs-" + name), "crash-*.json"));
            // The error lines themselves, not the whole log as the collection assert prints it.
            var errors = logs.Where(l => Level(l) == "ERROR").ToList();
            Assert.True(errors.Count == 0, $"the {name} window logged {errors.Count} error line(s):\n{string.Join('\n', errors.Select(ErrorText))}");
            return logs;
        }

        public void Stop()
        {
            foreach (var process in _started.Where(p => !p.HasExited))
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    // A folder with a window's setting up: dual panes, and a data folder of three files to find in.
    private static (Run Run, string Root, string Data) Prepare(string purpose, string configJson = """{ "ui": { "dualPane": true } }""")
    {
        if (Environment.GetEnvironmentVariable(OptIn) != "1")
        {
            Assert.Skip($"Opens a window on the desktop: set {OptIn}=1 to run it.");
        }
        var exe = Path.Combine(Repo.Root, "ui", "CabinetOS", "bin", "x64", "Debug", "net10.0-windows10.0.22621.0", "win-x64", "CabinetOS.exe");
        var core = CoreLauncher.Find(Path.Combine(Repo.Root, "ui"), Environment.GetEnvironmentVariable, File.Exists);
        if (!File.Exists(exe) || core is null)
        {
            Assert.Skip("Build ui/CabinetOS.sln (Debug) and the core first.");
        }
        var root = Repo.NewTempFolder(purpose);
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "alpha.txt"), "x");
        File.WriteAllText(Path.Combine(data, "Alphabet.md"), "x");
        File.WriteAllText(Path.Combine(data, "beta.txt"), "x");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), configJson);
        return (new Run(root, exe, core), root, data);
    }

    private static void State(List<string> logs, string label, Action<JsonElement> check)
    {
        var line = Assert.Single(logs, l => Message(l) == "shell state" && Field(l, "label").GetString() == label);
        using var parsed = JsonDocument.Parse(line);
        try
        {
            check(parsed.RootElement.GetProperty("fields"));
        }
        catch (Xunit.Sdk.XunitException error)
        {
            // A state that came before the answer did shows in the times: the find's, Quick Open's and the workspace's lines against the state's.
            throw new Xunit.Sdk.XunitException($"state \"{label}\": {error.Message}\nthe find, Quick Open, workspace and state lines of the window's log:\n{FindTimeline(logs)}");
        }
    }

    private static string FindTimeline(List<string> logs) =>
        string.Join('\n', logs.Where(l => Message(l) is "find opened" or "find filtered" or "find closed" or "shell state"
            or "quick open shown" or "quick open went to a row" or "quick-open step ended" or "quick-open step ended before Quick Open answered"
            or "workspace shows a branch").Select(l =>
        {
            using var parsed = JsonDocument.Parse(l);
            var fields = parsed.RootElement.GetProperty("fields");
            var detail = Message(l) == "shell state"
                ? $"{fields.GetProperty("label")} active_pane={fields.GetProperty("active_pane")} pane0_find={fields.GetProperty("pane0_find")} pane0_shown={fields.GetProperty("pane0_shown")} pane0_find_count={fields.GetProperty("pane0_find_count")}"
                    + $" quick_open={fields.GetProperty("quick_open")} rows=[{fields.GetProperty("quick_open_rows")}] branch={fields.GetProperty("branch")} root={fields.GetProperty("workspace_root")}"
                    + $" pane0={fields.GetProperty("pane0_path")}>{fields.GetProperty("pane0_cursor")} pane1={fields.GetProperty("pane1_path")}>{fields.GetProperty("pane1_cursor")}"
                : fields.ToString();
            return $"  {parsed.RootElement.GetProperty("ts").GetString()} {Message(l)} {detail}";
        }));

    private static string? Message(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.GetProperty("message").GetString();
    }

    // An error line as text: its message, then the error field's first 40 lines (an exception with its stack), one to a line.
    private static string ErrorText(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        var fields = parsed.RootElement.GetProperty("fields");
        var text = fields.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString()! : fields.ToString();
        return $"{parsed.RootElement.GetProperty("message").GetString()}\n{string.Join('\n', text.Replace("\r", "", StringComparison.Ordinal).Split('\n').Take(40).Select(t => "    " + t))}";
    }

    private static string? Level(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.GetProperty("level").GetString();
    }

    private static JsonElement Field(string line, string name)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.GetProperty("fields").GetProperty(name).Clone();
    }

    private static async Task WaitForAsync(Func<bool> condition, string what, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"waited {timeout.TotalSeconds:N0} s for {what}");
            await Task.Delay(200);
        }
    }
}
