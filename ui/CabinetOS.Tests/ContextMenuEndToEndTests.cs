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
