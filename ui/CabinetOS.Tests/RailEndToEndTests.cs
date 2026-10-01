using System.Diagnostics;
using System.Text.Json;
using CabinetOS.Core.Ipc;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Phase 13: the three layouts and the rail's views, on a real window and core
/// (docs/ui.md, "The activity rail and the sidebar"). Each test opens a window on
/// the desktop, so they run only with <c>CABINETOS_UI_E2E=1</c> and a built
/// window (Debug) and core. The window's snapshot steps drive it and its log
/// line "rail state" says what it shows.
/// </summary>
public class RailEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    private sealed class Run(string root, string exe, string core)
    {
        public List<Process> Started { get; } = [];

        // Starts the window on the run's configuration and tools; the snapshot steps are the test's script.
        public Process Start(string name, string steps) => Start(name, steps, snapshot: true);

        // With snapshot false the window starts as a user starts it: no aid, no steps, nothing waits for the window to be ready.
        public Process Start(string name, string steps, bool snapshot)
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
            if (snapshot)
            {
                start.Environment["CABINETOS_UI_SNAPSHOT"] = Path.Combine(root, "shots-" + name);
                start.Environment["CABINETOS_UI_SNAPSHOT_STEPS"] = steps;
            }
            var process = Process.Start(start)!;
            Started.Add(process);
            return process;
        }

        public string Shot(string name, string shot) => Path.Combine(root, "shots-" + name, shot + ".png");

        // Waits for the last snapshot, closes the window the way a user does and returns the UI's log lines.
        public async Task<List<string>> FinishAsync(string name, Process process, string lastShot)
        {
            await WaitForAsync(() => File.Exists(Shot(name, lastShot)), $"the {name} window's last snapshot", TimeSpan.FromSeconds(90));
            process.CloseMainWindow();
            Assert.True(process.WaitForExit(20_000), $"the {name} window did not close");
            var logs = LogFiles.Ui(Path.Combine(root, "logs-" + name));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "logs-" + name), "crash-*.json"));
            Assert.DoesNotContain(logs, l => Level(l) == "ERROR");
            return logs;
        }
    }

    // A folder with a window's whole setting up: the configuration, the tools (the shipped one, and the test tool with a
    // sidebar page twice, so a page hidden earlier is suspended), and some folders to show in the tree.
    private static (Run Run, string Root, string Data) Prepare(string purpose, string configJson)
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
        Directory.CreateDirectory(Path.Combine(data, "a", "sub"));
        File.WriteAllText(Path.Combine(data, "a", "one.txt"), "x");
        File.WriteAllText(Path.Combine(data, "a", "two.md"), "x");
        Directory.CreateDirectory(Path.Combine(data, "b"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), configJson);
        var tools = Path.Combine(root, "tools");
        CopyFolder(Path.Combine(Repo.Root, "sdk", "tools", "markdown-preview"), Path.Combine(tools, "markdown-preview"));
        CopyFolder(Path.Combine(Repo.Root, "ui", "livecheck", "fixtures", "quick-notes"), Path.Combine(tools, "quick-notes"));
        CopyFolder(Path.Combine(Repo.Root, "ui", "livecheck", "fixtures", "quick-notes"), Path.Combine(tools, "pin-notes"));
        var pin = Path.Combine(tools, "pin-notes", "tool.json");
        File.WriteAllText(pin, File.ReadAllText(pin).Replace("\"quick-notes\"", "\"pin-notes\"", StringComparison.Ordinal).Replace("Quick Notes", "Pin Notes", StringComparison.Ordinal));
        return (new Run(root, exe, core), root, data);
    }

    [Theory]
    [InlineData("classic", false)]
    [InlineData("right", false)]
    [InlineData("rail", true)]
    public async Task Each_layout_shows_the_rail_and_the_tree_or_leaves_them_out(string layout, bool rail)
    {
        var (run, root, data) = Prepare($"rail-{layout}", $$"""{ "ui": { "layout": "{{layout}}" } }""");
        try
        {
            // The two commands of the rail layout, run in a layout without it: the sidebar commands say so, Ctrl+Shift+F searches.
            var process = run.Start("run", string.Join(';',
                "size:1400x800",
                $"path:{Path.Combine(data, "a")}",
                "rail-state:start",
                "cmd:view.showExplorer",
                "cmd:sidebar.locate",
                "cmd:sidebar.lock",
                "wait:500",
                "rail-state:after",
                "shot:layout"));
            var logs = await run.FinishAsync("run", process, "layout");

            var start = Assert.Single(logs, l => Message(l) == "rail state" && Field(l, "label").GetString() == "start");
            Assert.Equal(layout, Field(start, "layout").GetString());
            Assert.Equal(rail, Field(start, "rail_visible").GetBoolean());
            Assert.Equal(rail, Field(start, "tree_visible").GetBoolean());
            Assert.Equal(rail, Field(start, "splitter_visible").GetBoolean());
            Assert.True(Field(start, "sidebar_open").GetBoolean());
            // Today's sidebar: the design's clamp(180 px, 20 %, 224 px) in a window 1400 px wide, in every layout.
            Assert.Equal(224, Field(start, "sidebar_width").GetInt32());
            Assert.Equal("explorer", Field(start, "view").GetString());
            if (rail)
            {
                Assert.Equal("explorer", Field(start, "active").GetString());
            }

            var after = Assert.Single(logs, l => Message(l) == "rail state" && Field(l, "label").GetString() == "after");
            Assert.True(Field(after, "sidebar_open").GetBoolean());
            Assert.Equal("explorer", Field(after, "view").GetString());
            if (rail)
            {
                // Locate opened the path down to the folder; lock was toggled on.
                Assert.Equal(Path.Combine(data, "a"), Field(after, "tree_current").GetString(), ignoreCase: true);
                Assert.True(Field(after, "tree_locked").GetBoolean());
            }
            else
            {
                Assert.Equal("", Field(after, "tree_current").GetString());
                Assert.False(Field(after, "tree_locked").GetBoolean());
                Assert.Contains(logs, l => Message(l) == "notice shown" && Field(l, "text").GetString()!.Contains("rail layout", StringComparison.Ordinal));
                Assert.DoesNotContain(logs, l => Message(l) == "a rail button was pressed");
            }
        }
        finally
        {
            foreach (var process in run.Started.Where(p => !p.HasExited))
            {
                process.Kill(entireProcessTree: true);
            }
            Repo.RemoveTempFolder(root);
        }
    }

    // Found by a real-key check of the window on 2026-09-30: started in the rail layout, the tree under FOLDERS was empty, though
    // the log said "the tree shows a folder ... rows 4488"; the rows came after Ctrl+Shift+E. The reveal scrolled the list to the
    // folder before the list was laid out, so it drew its rows around the folder, far from the sidebar's window. The snapshot aid
    // waits until the window is ready and does not show it, so this test starts the window the way a user does.
    [Fact]
    public async Task The_tree_has_rows_on_the_screen_when_the_window_starts_in_the_rail_layout_before_any_key()
    {
        var (run, root, data) = Prepare("rail-start", "{}");
        try
        {
            // Far more rows above the folder than the sidebar shows, so the folder lies well below the window when the tree opens.
            for (var i = 0; i < 80; i++)
            {
                Directory.CreateDirectory(Path.Combine(data, $"0-{i:00}"));
            }
            var folder = Path.Combine(data, "a", "sub");
            File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"),
                JsonSerializer.Serialize(new { ui = new { layout = "rail", sidebar = true, lastPaths = new[] { folder, folder } } }));

            var process = run.Start("plain", "", snapshot: false);
            List<string> log() => LogFiles.Ui(Path.Combine(root, "logs-plain"));
            // The window logs its look at the tree once a row is inside the sidebar's window, or after 10 s without one: a loaded
            // machine lays the list out late, and a look at a fixed time called a slow tree an empty one.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!log().Any(l => Message(l) == "the tree drew rows"))
            {
                Assert.True(DateTime.UtcNow < deadline, "waited 60 s for the tree's look at its rows; the window's log so far: " + string.Join(" | ", log().TakeLast(12).Select(Message)));
                await Task.Delay(200);
            }
            var logs = log();
            process.CloseMainWindow();
            Assert.True(process.WaitForExit(20_000), "the window did not close");

            var shown = Assert.Single(logs, l => Message(l) == "the tree shows a folder");
            Assert.Equal(folder, Field(shown, "path").GetString(), ignoreCase: true);
            var drew = Assert.Single(logs, l => Message(l) == "the tree drew rows");
            // All the rows are in the model, and some of them are inside the sidebar's window, with no key pressed.
            Assert.True(Field(drew, "rows").GetInt32() > 80);
            Assert.True(Field(drew, "visible").GetInt32() > 0,
                $"the tree has its rows in the model and none on the screen, {Field(drew, "looked_after_ms").GetDouble():N0} ms after the scroll");
        }
        finally
        {
            foreach (var process in run.Started.Where(p => !p.HasExited))
            {
                process.Kill(entireProcessTree: true);
            }
            Repo.RemoveTempFolder(root);
        }
    }

    [Fact]
    public async Task The_rail_switches_views_keeps_the_last_web_page_warm_and_saves_its_settings_for_the_next_start()
    {
        var (run, root, data) = Prepare("rail-views", """{ "ui": { "layout": "rail" } }""");
        try
        {
            var folder = Path.Combine(data, "a");
            var sub = Path.Combine(folder, "sub");
            // The tree opens down to sub and lists it; the Search view shows what the snapshot aid typed into it; two tool
            // pages start; the explorer's return suspends the page hidden first; the divider takes 320 px, then snaps shut.
            var first = run.Start("first", string.Join(';',
                "size:1400x800",
                $"path:{folder}",
                $"tree:{sub}",
                "rail-state:tree",
                "search:one",
                "wait:1500",
                "rail:search",
                "wait:500",
                "rail-state:search",
                "rail:quick-notes",
                "until:tool",
                "plugin-event:badge|{\"view\":\"quick-notes\",\"kind\":\"dot\"}",
                "rail-state:notes",
                "rail:pin-notes",
                "wait:1500",
                "rail:explorer",
                "wait:1500",
                "rail:quick-notes",
                "wait:1000",
                "rail-move:quick-notes|-1",
                "divider:320",
                "rail-state:wide",
                "divider:90",
                "wait:500",
                "rail-state:closed",
                "shot:done"));
            var logs = await run.FinishAsync("first", first, "done");

            State(logs, "tree", state =>
            {
                Assert.Equal(sub, state.GetProperty("tree_current").GetString(), ignoreCase: true);
                // Lazy: one request for each folder opened on the way and none for the folders beside them.
                // (The start folder of the panes may be opened on the way too: three more.)
                Assert.InRange(state.GetProperty("tree_requests").GetInt32(), 1, sub.Split('\\').Length + 4);
            });
            State(logs, "search", state =>
            {
                Assert.Equal("search", state.GetProperty("view").GetString());
                Assert.Equal("one", state.GetProperty("search_text").GetString());
                Assert.True(state.GetProperty("search_hits").GetInt32() >= 1, "the Search view lists the hits of the search typed into it");
                Assert.Equal("search", state.GetProperty("active").GetString());
            });
            State(logs, "notes", state =>
            {
                Assert.Equal("quick-notes", state.GetProperty("view").GetString());
                Assert.Equal("quick-notes", state.GetProperty("active").GetString());
                Assert.Equal("quick-notes=dot", state.GetProperty("badges").GetString());
                Assert.Equal("explorer,search,marketplace,terminal,pin-notes,quick-notes", state.GetProperty("buttons").GetString());
            });
            State(logs, "wide", state =>
            {
                Assert.Equal(320, state.GetProperty("sidebar_width").GetInt32());
                Assert.True(state.GetProperty("sidebar_open").GetBoolean());
                Assert.Equal("explorer,search,marketplace,terminal,quick-notes,pin-notes", state.GetProperty("buttons").GetString());
            });
            State(logs, "closed", state => Assert.False(state.GetProperty("sidebar_open").GetBoolean()));
            Assert.Contains(logs, l => Message(l) == "a sidebar page started" && Field(l, "tool").GetString() == "quick-notes");
            Assert.Contains(logs, l => Message(l) == "a sidebar page started" && Field(l, "tool").GetString() == "pin-notes");
            // The explorer came on show with pin-notes hidden last: quick-notes, hidden before it, was suspended, and woke when its button was pressed.
            Assert.Contains(logs, l => Message(l) == "a page was suspended" && Field(l, "host").GetString() == "sidebar-quick-notes");
            Assert.Contains(logs, l => Message(l) == "a page was resumed" && Field(l, "host").GetString() == "sidebar-quick-notes");
            // Closing the sidebar hides quick-notes, which becomes the warm one; pin-notes, hidden before it, is suspended.
            Assert.Single(logs, l => Message(l) == "a page was suspended" && Field(l, "host").GetString() == "sidebar-pin-notes");
            Assert.Single(logs, l => Message(l) == "a page was suspended" && Field(l, "host").GetString() == "sidebar-quick-notes");

            // The core has what the window saved: ui.sidebar false (snapped shut), the width from before, the view, the order.
            using (var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config", "cabinetos.json"))))
            {
                var ui = config.RootElement.GetProperty("ui");
                Assert.False(ui.GetProperty("sidebar").GetBoolean());
                Assert.Equal(320, ui.GetProperty("sidebarWidth").GetInt32());
                Assert.Equal("quick-notes", ui.GetProperty("sidebarView").GetString());
                Assert.Equal(["explorer", "search", "marketplace", "terminal", "quick-notes", "pin-notes"], ui.GetProperty("rail").EnumerateArray().Select(i => i.GetString()).ToArray());
            }

            // The next start: the sidebar is closed as it was left; opened again it is 320 px wide and shows the same view.
            var second = run.Start("second", "size:1400x800;cmd:view.toggleSidebar;wait:1500;rail-state:restored;shot:restored");
            logs = await run.FinishAsync("second", second, "restored");
            State(logs, "restored", state =>
            {
                Assert.True(state.GetProperty("sidebar_open").GetBoolean());
                Assert.Equal(320, state.GetProperty("sidebar_width").GetInt32());
                Assert.Equal("quick-notes", state.GetProperty("view").GetString());
                Assert.Equal("explorer,search,marketplace,terminal,quick-notes,pin-notes", state.GetProperty("buttons").GetString());
            });
        }
        finally
        {
            foreach (var process in run.Started.Where(p => !p.HasExited))
            {
                process.Kill(entireProcessTree: true);
            }
            Repo.RemoveTempFolder(root);
        }
    }

    private static void State(List<string> logs, string label, Action<JsonElement> check)
    {
        var line = Assert.Single(logs, l => Message(l) == "rail state" && Field(l, "label").GetString() == label);
        using var parsed = JsonDocument.Parse(line);
        check(parsed.RootElement.GetProperty("fields"));
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
        foreach (var folder in Directory.GetDirectories(from))
        {
            CopyFolder(folder, Path.Combine(to, Path.GetFileName(folder)));
        }
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
