using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CabinetOS.Core.Ipc;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Phase 18: the right-click menu from cabinetos.json on a real window and core (docs/ui.md, "The
/// context menu"). Each test opens a window on the desktop, so they run only with
/// <c>CABINETOS_UI_E2E=1</c> and a built window (Debug) and core. The window's snapshot steps drive
/// it (<c>menu:</c>, <c>menu-click:</c>, <c>shellmenu:</c>) and its log line "shell state" says what
/// the open menus show.
/// </summary>
/// <summary>
/// Tests that measure the window's frames run alone, after the tests that run in parallel: a frame
/// goal says nothing about a window that shares the machine with other test windows.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FrameTests
{
    public const string Name = "frame measurements";
}

public class ContextMenuEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    /// <summary>
    /// A fresh config shows today's menus; a program added to the file while the window runs shows
    /// at the next right-click for a matching file only, runs with the focused path from the menu
    /// and from the palette; a program not in the list is refused; with <c>shellMenu</c> on,
    /// Shift+right-click shows Windows' menu of the file.
    /// </summary>
    [Fact]
    public async Task The_menu_follows_the_file_runs_a_program_and_shows_windows_own_menu()
    {
        var (run, root, data) = Prepare("menu-config");
        try
        {
            var stub = Path.Combine(root, "record paths.js");
            var recorded = Path.Combine(root, "record paths.js.log");
            // A stand-in program with no window, as the live check's editor: each start adds its argument as a line.
            File.WriteAllText(stub, """
                var fso = new ActiveXObject("Scripting.FileSystemObject");
                var log = fso.OpenTextFile(WScript.ScriptFullName + ".log", 8, true, -1);
                log.WriteLine(WScript.Arguments.length > 0 ? WScript.Arguments(0) : "(nothing)");
                log.Close();
                """);
            var process = run.Start("config", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "menu:alpha.txt",
                "shell:fresh",
                "cmd:overlay.close",
                "menu:*",
                "shell:background",
                "cmd:overlay.close",
                "shellmenu:alpha.txt",
                "shell:shell-off",
                "cmd:overlay.close",
                // The test writes the file now, as an editor would; the window reads it again.
                "until:config",
                "wait:500",
                "menu:Alphabet.md",
                "shell:md",
                "menu-click:Record Paths",
                "wait:2000",
                "menu:alpha.txt",
                "shell:txt",
                "cmd:overlay.close",
                "cmd:program.record",
                "wait:2000",
                "cmd:program.nosuch",
                "shellmenu:alpha.txt",
                "shell:windows",
                "cmd:overlay.close",
                "shot:done"));
            await run.WaitForStateAsync("config", "background");
            File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["version"] = 1,
                ["ui"] = new Dictionary<string, object> { ["dualPane"] = true },
                ["contextMenu"] = new Dictionary<string, object>
                {
                    ["shellMenu"] = true,
                    ["file"] = new Dictionary<string, object>
                    {
                        ["items"] = new object[]
                        {
                            new Dictionary<string, object> { ["command"] = "pane.openSelected" },
                            new Dictionary<string, object> { ["separator"] = true },
                            new Dictionary<string, object> { ["command"] = "program.record", ["extensions"] = new[] { ".md" } },
                            new Dictionary<string, object> { ["command"] = "hex.view" },
                        },
                    },
                },
                ["programs"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["name"] = "record",
                        ["title"] = "Record Paths",
                        ["command"] = "wscript.exe",
                        ["args"] = new[] { "//B", "//Nologo", stub, "{path}" },
                    },
                },
            }));
            var logs = await run.FinishAsync("config", process, "done");

            State(logs, "fresh", state =>
            {
                Assert.Equal("Cut|Copy|Paste|Rename|Delete", state.GetProperty("context_quick").GetString());
                Assert.Equal("Open|Open in other pane|Copy to other pane|Open in Terminal|Properties|Edit Menu…", state.GetProperty("context_menu").GetString());
            });
            State(logs, "background", state =>
            {
                Assert.Equal("", state.GetProperty("context_quick").GetString());
                Assert.Equal("Paste|New folder|Pin this folder to the sidebar|Properties|Edit Menu…", state.GetProperty("context_menu").GetString());
            });
            // Off by default: Shift+right-click opens the same menu as a right-click.
            State(logs, "shell-off", state =>
            {
                Assert.Equal("", state.GetProperty("windows_menu").GetString());
                Assert.StartsWith("Open|", state.GetProperty("context_menu").GetString());
            });
            State(logs, "md", state => Assert.Equal("Open|Record Paths|Properties|Edit Menu…", state.GetProperty("context_menu").GetString()));
            State(logs, "txt", state => Assert.Equal("Open|Properties|Edit Menu…", state.GetProperty("context_menu").GetString()));
            State(logs, "windows", state =>
            {
                Assert.Equal("", state.GetProperty("context_menu").GetString());
                Assert.True(state.GetProperty("windows_menu").GetString()!.Split('|').Length > 3, state.GetProperty("windows_menu").GetString());
            });
            // From the menu the right-clicked file, from the palette the cursor's.
            Assert.Equal([Path.Combine(data, "Alphabet.md"), Path.Combine(data, "alpha.txt")], await ReadLinesAsync(recorded, 2));
            Assert.Contains(logs, l => Message(l) == "command failed" && Field(l, "command").GetString() == "program.nosuch"
                && Field(l, "code").GetString() == "unknown_program");
            // The ID no command has is logged once, though the file menu opened twice.
            Assert.Single(logs, l => Message(l) == "context menu entry left out: no command has this ID" && Field(l, "command").GetString() == "hex.view");
            Assert.Contains(logs, l => Message(l) == "windows menu shown");
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// Phase 18, step 2: "Edit Menu…" turns the file menu into its edit mode. A row goes with its X,
    /// a command comes through "Add Command…" (Insert) and moves with Alt+Up, Delete takes it out,
    /// and "Done" saves the list through the core: the file holds it and the next menu shows it. A
    /// save the core refuses (the file has an error) keeps the edit mode open with a notice; Esc and
    /// the palette's way out leave without saving; from the palette, <c>menu.edit</c> edits the
    /// focused row's menu.
    /// </summary>
    [Fact]
    public async Task The_menu_is_edited_inside_the_menu_and_saved_through_the_core()
    {
        var (run, root, data) = Prepare("menu-edit");
        try
        {
            var configPath = Path.Combine(root, "config", "cabinetos.json");
            var process = run.Start("edit", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "menu:alpha.txt",
                "wait:600",
                "menu-click:Edit Menu…",
                "wait:500",
                "shell:editing",
                // The row's X, pressed through its automation peer as a screen reader would.
                "click:Remove Open in Terminal",
                "shell:removed",
                "menu-edit-key:insert",
                "wait:300",
                "type:New folder",
                "wait:300",
                "accept",
                "wait:300",
                "shell:added",
                "menu-edit-key:alt+up",
                "shell:moved",
                "menu-edit-key:delete",
                "shell:deleted",
                // The drag's own steps, as the pointer takes them: down to the first row and back.
                "menu-edit-drag:Copy to other pane|Open",
                "shell:dragged",
                "menu-edit-drag:Copy to other pane|Open in other pane",
                "shell:dragged-back",
                "click:Done",
                "until:config",
                "wait:300",
                "shell:saved",
                // Each menu gets its time on screen, and its time to close: the same menu asked for again while it is
                // still closing does not come back (step 1's flyout; see the Phase 18 step 2 report).
                "menu:alpha.txt",
                "wait:600",
                "shell:after",
                "cmd:overlay.close",
                "wait:600",
                // The file gets an error while the list is edited: the core refuses the save.
                "menu:alpha.txt",
                "wait:600",
                "menu-click:Edit Menu…",
                "wait:500",
                "click:Remove Copy to other pane",
                "shell:break-the-file",
                "until:config-error",
                "click:Done",
                "wait:1000",
                "shell:refused",
                // The test puts the file back; the core says it is valid again.
                "until:config",
                "click:Done",
                "wait:1000",
                "shell:saved-again",
                // Esc leaves without saving.
                "menu:alpha.txt",
                "wait:600",
                "menu-click:Edit Menu…",
                "wait:500",
                "menu-edit-key:delete",
                "shell:before-esc",
                "menu-edit-key:escape",
                "shell:after-esc",
                "menu:alpha.txt",
                "wait:600",
                "shell:unchanged",
                "cmd:overlay.close",
                // From the palette there is no open menu: the focused row's is edited; overlay.close leaves it.
                "cmd:menu.edit",
                "wait:300",
                "shell:from-palette",
                "cmd:overlay.close",
                "shell:closed",
                "shot:done"));
            await run.WaitForStateAsync("edit", "saved");
            var afterFirstSave = File.ReadAllText(configPath);
            await run.WaitForStateAsync("edit", "break-the-file");
            File.WriteAllText(configPath, "{ \"version\": 1, \"ui\": ");
            await run.WaitForStateAsync("edit", "refused");
            File.WriteAllText(configPath, afterFirstSave);
            var logs = await run.FinishAsync("edit", process, "done");

            State(logs, "editing", state =>
            {
                Assert.Equal("File menu", state.GetProperty("menu_edit_target").GetString());
                Assert.Equal("Open|Open in other pane|Copy to other pane|Open in Terminal", state.GetProperty("menu_edit").GetString());
                // The menu turned into the edit mode: it is not open as well.
                Assert.Equal("", state.GetProperty("context_menu").GetString());
            });
            State(logs, "removed", state => Assert.Equal("Open|Open in other pane|Copy to other pane", state.GetProperty("menu_edit").GetString()));
            State(logs, "added", state => Assert.Equal("Open|Open in other pane|Copy to other pane|New folder", state.GetProperty("menu_edit").GetString()));
            State(logs, "moved", state => Assert.Equal("Open|Open in other pane|New folder|Copy to other pane", state.GetProperty("menu_edit").GetString()));
            State(logs, "deleted", state => Assert.Equal("Open|Open in other pane|Copy to other pane", state.GetProperty("menu_edit").GetString()));
            State(logs, "dragged", state => Assert.Equal("Copy to other pane|Open|Open in other pane", state.GetProperty("menu_edit").GetString()));
            State(logs, "dragged-back", state => Assert.Equal("Open|Open in other pane|Copy to other pane", state.GetProperty("menu_edit").GetString()));
            Assert.Equal(2, logs.Count(l => Message(l) == "menu edit step" && Field(l, "step").GetString() == "drag"));
            State(logs, "saved", state => Assert.Equal("", state.GetProperty("menu_edit").GetString()));
            State(logs, "after", state => Assert.Equal("Open|Open in other pane|Copy to other pane|Properties|Edit Menu…", state.GetProperty("context_menu").GetString()));

            // Refused: the edit mode stays, with its rows, and the status bar says why.
            State(logs, "refused", state => Assert.Equal("Open|Open in other pane", state.GetProperty("menu_edit").GetString()));
            var refused = Assert.Single(logs, l => Message(l) == "menu edit refused");
            Assert.Equal("config_error", Field(refused, "code").GetString());
            Assert.Contains(logs, l => Message(l) == "notice shown" && Field(l, "text").GetString()!.StartsWith("The menu was not saved:", StringComparison.Ordinal)
                && Field(l, "error").GetBoolean());
            State(logs, "saved-again", state => Assert.Equal("", state.GetProperty("menu_edit").GetString()));

            State(logs, "before-esc", state => Assert.Equal("Open in other pane", state.GetProperty("menu_edit").GetString()));
            State(logs, "after-esc", state => Assert.Equal("", state.GetProperty("menu_edit").GetString()));
            State(logs, "unchanged", state => Assert.Equal("Open|Open in other pane|Properties|Edit Menu…", state.GetProperty("context_menu").GetString()));
            State(logs, "from-palette", state =>
            {
                Assert.Equal("File menu", state.GetProperty("menu_edit_target").GetString());
                Assert.Equal("Open|Open in other pane", state.GetProperty("menu_edit").GetString());
            });
            State(logs, "closed", state => Assert.Equal("", state.GetProperty("menu_edit").GetString()));

            Assert.Equal(["contextMenu.file.items", "contextMenu.file.items"],
                logs.Where(l => Message(l) == "menu edit saved").Select(l => Field(l, "path").GetString()));
            Assert.Equal([true, true, false, false], logs.Where(l => Message(l) == "menu edit closed").Select(l => Field(l, "saved").GetBoolean()));

            // The core wrote the list in the defaults' shape; the other targets kept theirs.
            using var config = JsonDocument.Parse(File.ReadAllText(configPath));
            var menu = config.RootElement.GetProperty("contextMenu");
            Assert.Equal("""[{"command":"pane.openSelected"},{"command":"file.openInOtherPane"}]""", JsonSerializer.Serialize(menu.GetProperty("file").GetProperty("items")));
            Assert.Equal(4, menu.GetProperty("folder").GetProperty("items").GetArrayLength());
            var core = Lines(Directory.GetFiles(Path.Combine(root, "logs-edit"), "core.*.jsonl").Single());
            Assert.Equal(2, core.Count(l => Message(l) == "configuration changed" && Field(l, "changed").GetString()!.Contains("contextMenu.file.items", StringComparison.Ordinal)));
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// The frame measurement, alone: another test's window on the same desktop takes the processor and
    /// the GPU, and its load shows in this window's frames (one 35 ms frame in the full run of
    /// 2026-09-30, none when the test ran by itself).
    /// </summary>
    [Collection(FrameTests.Name)]
    public class Alone
    {
        /// <summary>
        /// The menu over every row of the 100,000-entry bench folder, all selected: no frame with more
        /// than 33 ms of UI-thread work while it opens (docs/ui.md, "Scrolling", measures the same way).
        /// The first opening of the process is timed too, and logged, since WinUI loads the flyout's
        /// template then.
        /// </summary>
        [Fact]
        public async Task Opening_the_menu_over_100000_selected_rows_adds_no_slow_frame()
        {
            var bench = Path.Combine(Path.GetTempPath(), "cabinetos-bench", "100000");
            if (!File.Exists(bench + ".complete"))
            {
                Assert.Skip($"Needs the bench folder {bench}: cargo bench -p cabinetos-fs --bench list_directory makes it.");
            }
            var (run, root, data) = Prepare("menu-bench");
            try
            {
                var process = run.Start("bench", string.Join(';',
                    "size:1200x700",
                    "pane:0",
                    $"path:{data}",
                    "wait:500",
                    "menu:alpha.txt",
                    "wait:800",
                    "shell:warm",
                    "cmd:overlay.close",
                    $"path:{bench}",
                    "wait:3000",
                    "selectall",
                    "wait:1500",
                    "shell:before",
                    "menu:",
                    "wait:1500",
                    "shell:open",
                    "cmd:overlay.close",
                    "wait:500",
                    "shot:done"), frameStats: true);
                var logs = await run.FinishAsync("bench", process, "done");

                DateTime At(string label) => Timestamp(logs.Single(l => Message(l) == "shell state" && Field(l, "label").GetString() == label));
                var (before, open) = (At("before"), At("open"));
                State(logs, "open", state => Assert.StartsWith("Open|", state.GetProperty("context_menu").GetString()));
                var shown = logs.Where(l => Message(l) == "context menu shown").ToList();
                Assert.Equal(2, shown.Count);
                Assert.Equal("MultiSelect", Field(shown[1], "target").GetString());
                var slow = logs.Where(l => Message(l) == "slow frame" && Timestamp(l) > before && Timestamp(l) <= open
                    && Field(l, "busy_ms").GetDouble() > 33).ToList();
                Assert.True(slow.Count == 0, $"frames with over 33 ms of UI-thread work while the menu opened:\n{string.Join('\n', slow)}\n"
                    + string.Join('\n', logs.Where(l => Timestamp(l) > before && Timestamp(l) <= open)));
            }
            finally
            {
                run.Stop();
                Repo.RemoveTempFolder(root);
            }
        }

        /// <summary>
        /// The edit mode over the 100,000 selected rows of the bench folder: entering it adds no frame
        /// with more than 33 ms of UI-thread work. The first entering of the process builds the
        /// surface's templates, as the menu's first opening does, so it is done once in the small
        /// folder first and its time logged.
        /// </summary>
        [Fact]
        public async Task Entering_the_edit_mode_over_100000_selected_rows_adds_no_slow_frame()
        {
            var bench = Path.Combine(Path.GetTempPath(), "cabinetos-bench", "100000");
            if (!File.Exists(bench + ".complete"))
            {
                Assert.Skip($"Needs the bench folder {bench}: cargo bench -p cabinetos-fs --bench list_directory makes it.");
            }
            var (run, root, data) = Prepare("menu-edit-bench");
            try
            {
                var process = run.Start("edit-bench", string.Join(';',
                    "size:1200x700",
                    "pane:0",
                    $"path:{data}",
                    "wait:500",
                    "menu:alpha.txt",
                    "wait:800",
                    "menu-click:Edit Menu…",
                    "wait:800",
                    "cmd:overlay.close",
                    $"path:{bench}",
                    "wait:3000",
                    "selectall",
                    "wait:1500",
                    "menu:",
                    "wait:1500",
                    "shell:before",
                    "menu-click:Edit Menu…",
                    "wait:1500",
                    "shell:open",
                    "cmd:overlay.close",
                    "wait:500",
                    "shot:done"), frameStats: true);
                var logs = await run.FinishAsync("edit-bench", process, "done");

                DateTime At(string label) => Timestamp(logs.Single(l => Message(l) == "shell state" && Field(l, "label").GetString() == label));
                var (before, open) = (At("before"), At("open"));
                State(logs, "open", state =>
                {
                    Assert.Equal("Selection menu", state.GetProperty("menu_edit_target").GetString());
                    Assert.StartsWith("Open|", state.GetProperty("menu_edit").GetString());
                });
                var shown = logs.Where(l => Message(l) == "menu edit shown").ToList();
                Assert.Equal(2, shown.Count);
                Assert.Equal("MultiSelect", Field(shown[1], "target").GetString());
                var slow = logs.Where(l => Message(l) == "slow frame" && Timestamp(l) > before && Timestamp(l) <= open
                    && Field(l, "busy_ms").GetDouble() > 33).ToList();
                Assert.True(slow.Count == 0, $"frames with over 33 ms of UI-thread work while the edit mode opened:\n{string.Join('\n', slow)}\n"
                    + string.Join('\n', logs.Where(l => Timestamp(l) > before && Timestamp(l) <= open)));
            }
            finally
            {
                run.Stop();
                Repo.RemoveTempFolder(root);
            }
        }
    }

    private static async Task<List<string>> ReadLinesAsync(string path, int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            if (File.Exists(path))
            {
                // wscript writes UTF-16 (the last argument of OpenTextFile).
                var lines = File.ReadAllLines(path, System.Text.Encoding.Unicode).Where(l => l.Length > 0).ToList();
                if (lines.Count >= count)
                {
                    return lines;
                }
            }
            Assert.True(DateTime.UtcNow < deadline, $"{path} did not get {count} lines");
            await Task.Delay(200);
        }
    }

    private sealed class Run(string root, string exe, string core)
    {
        private readonly List<Process> _started = [];

        // Starts the window on the run's configuration; the snapshot steps are the test's script.
        public Process Start(string name, string steps, bool frameStats = false)
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
            start.Environment["CABINETOS_UPDATE_DIR"] = Path.Combine(root, "update");
            start.Environment["CABINETOS_UI_SNAPSHOT"] = Path.Combine(root, "shots-" + name);
            start.Environment["CABINETOS_UI_SNAPSHOT_STEPS"] = steps;
            if (frameStats)
            {
                start.Environment["CABINETOS_UI_FRAMESTATS"] = "1";
            }
            var process = Process.Start(start)!;
            _started.Add(process);
            return process;
        }

        // Waits until the window logged the "shell state" line with this label.
        public async Task WaitForStateAsync(string name, string label)
        {
            var folder = Path.Combine(root, "logs-" + name);
            await WaitForAsync(() => Directory.Exists(folder) && Directory.GetFiles(folder, "ui.*.jsonl")
                .Any(file => Lines(file).Any(l => Message(l) == "shell state" && Field(l, "label").GetString() == label)),
                $"the {name} window's state {label}", TimeSpan.FromSeconds(60));
        }

        // Waits for the last snapshot, closes the window the way a user does and returns the UI's log lines.
        public async Task<List<string>> FinishAsync(string name, Process process, string lastShot)
        {
            var shot = Path.Combine(root, "shots-" + name, lastShot + ".png");
            await WaitForAsync(() => File.Exists(shot), $"the {name} window's last snapshot", TimeSpan.FromSeconds(120));
            process.CloseMainWindow();
            Assert.True(process.WaitForExit(20_000), $"the {name} window did not close");
            var logs = Lines(Directory.GetFiles(Path.Combine(root, "logs-" + name), "ui.*.jsonl").Single());
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "logs-" + name), "crash-*.json"));
            Assert.DoesNotContain(logs, l => Level(l) == "ERROR");
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

    // A folder with a window's setting up: dual panes, and a data folder of three files.
    private static (Run Run, string Root, string Data) Prepare(string purpose)
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
        File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), """{ "version": 1, "ui": { "dualPane": true } }""");
        return (new Run(root, exe, core), root, data);
    }

    private static void State(List<string> logs, string label, Action<JsonElement> check)
    {
        var line = Assert.Single(logs, l => Message(l) == "shell state" && Field(l, "label").GetString() == label);
        using var parsed = JsonDocument.Parse(line);
        check(parsed.RootElement.GetProperty("fields"));
    }

    private static List<string> Lines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }
        return lines;
    }

    private static string? Message(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.GetProperty("message").GetString();
    }

    private static string? Level(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.GetProperty("level").GetString();
    }

    private static DateTime Timestamp(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        return DateTime.Parse(parsed.RootElement.GetProperty("ts").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
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
