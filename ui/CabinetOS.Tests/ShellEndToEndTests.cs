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
                // §7: below 640 px the command center hides and the rest stays.
                "size:620x700",
                "wait:500",
                "shell:narrow",
                "shot:done"));
            var logs = await run.FinishAsync("find", process, "done");

            State(logs, "start", state =>
            {
                Assert.Equal(40, state.GetProperty("top_row").GetDouble());
                var (left, width) = Center(state);
                Assert.True(width >= 200, $"the command center is {width} px at 924 px");
                Assert.True(state.GetProperty("left_cluster_end").GetDouble() <= left, "the command center lies over the left cluster");
                Assert.True(left + width <= state.GetProperty("right_cluster_start").GetDouble(), "the command center lies over the right cluster");
                Assert.Equal(5, state.GetProperty("pane0_count").GetInt32());
            });
            State(logs, "found", state =>
            {
                Assert.Equal("alpha", state.GetProperty("pane0_find").GetString());
                Assert.True(state.GetProperty("pane0_find_open").GetBoolean());
                Assert.Equal(2, state.GetProperty("pane0_shown").GetInt32());
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
                // The selection survives the filter: the cursor stays on the match Enter chose.
                Assert.StartsWith("alpha", state.GetProperty("pane0_cursor").GetString(), StringComparison.OrdinalIgnoreCase);
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
                Assert.Equal("hidden", state.GetProperty("command_center").GetString());
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
            // A repository on a branch: the pill shows it, and Quick Open searches the whole repository from a folder in it.
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
                "shell:pill",
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

            State(logs, "pill", state =>
            {
                Assert.Equal("phase-16", state.GetProperty("branch").GetString());
                Assert.Equal(project, state.GetProperty("workspace_root").GetString(), ignoreCase: true);
            });
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
                // A click on a crumb goes there; Back comes back; Ctrl+L makes the row a text box, Esc ends it.
                $"click:{projects}",
                "wait:400",
                "shell:crumb",
                "click:Back",
                "wait:400",
                "shell:back",
                "cmd:go.toPath",
                "shell:editing",
                "cmd:overlay.close",
                "shell:edited",
                "cmd:settings.open",
                "wait:1000",
                "shot:done"));
            var logs = await run.FinishAsync("menus", process, "done");

            // The registry's titles, as the palette shows them: a rename there shows here too.
            State(logs, "menu", state => Assert.Equal(
                "New Tab|New Folder|Find in Pane|Go to Path…|Toggle Sidebar|Browse Plugins and Themes|Open Keyboard Shortcuts|Check for Updates",
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
            State(logs, "editing", state => Assert.True(state.GetProperty("pane0_editing").GetBoolean()));
            State(logs, "edited", state => Assert.False(state.GetProperty("pane0_editing").GetBoolean()));
            Assert.Contains(logs, l => Message(l) == "notice shown" && Field(l, "text").GetString()!.StartsWith("Cannot edit cabinetos.json", StringComparison.Ordinal));
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    [Fact]
    public async Task A_downloaded_update_opens_its_dialog_once_and_Later_keeps_the_pill_and_the_dot()
    {
        var (_, root, _) = Prepare("shell-update");
        try
        {
            // An install the core may update: a copy of the core with release.json 0.1.0 beside it (docs/ui.md, "Updates").
            var core = CoreLauncher.Find(Path.Combine(Repo.Root, "ui"), Environment.GetEnvironmentVariable, File.Exists)!;
            var install = Directory.CreateDirectory(Path.Combine(root, "install")).FullName;
            File.Copy(core, Path.Combine(install, "cabinetos-core.exe"));
            File.WriteAllText(Path.Combine(install, "release.json"), """{"product":"CabinetOS","version":"0.1.0"}""");
            // The stable channel's feed: latest.json names 0.2.0, its notes and its zip (the two programs are stand-ins).
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
            // No daily check: only the command checks, so nothing races it.
            File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["version"] = 1,
                ["ui"] = new Dictionary<string, object> { ["dualPane"] = true },
                ["update"] = new Dictionary<string, object> { ["check"] = false, ["source"] = feed },
            }));
            var updateDir = Path.Combine(root, "update");
            var exe = Path.Combine(Repo.Root, "ui", "CabinetOS", "bin", "x64", "Debug", "net10.0-windows10.0.22621.0", "win-x64", "CabinetOS.exe");
            var run = new Run(root, exe, Path.Combine(install, "cabinetos-core.exe"));

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
            // Nothing was swapped: Later leaves the install as it was.
            Assert.Equal(["cabinetos-core.exe", "release.json"], Directory.GetFileSystemEntries(install).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }
    }

    // "left+width" of the command center, as the shell state logs it.
    private static (double Left, double Width) Center(JsonElement state)
    {
        var text = state.GetProperty("command_center").GetString()!;
        var parts = text.Split('+');
        Assert.Equal(2, parts.Length);
        return (double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
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
            or "workspace pill shows a branch").Select(l =>
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
