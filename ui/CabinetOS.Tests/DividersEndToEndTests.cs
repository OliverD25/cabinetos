using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CabinetOS.Core.Ipc;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Sorting by the column headings and the dividers of the window on a real window and core (docs/ui.md, "A pane's
/// order", "The divider between the panes" and "The activity rail and the sidebar"). It opens windows on the desktop, so
/// it runs only with <c>CABINETOS_UI_E2E=1</c> and a built window (Debug) and a core that knows <c>ui.paneSplit</c>.
/// The window's snapshot steps drive it (<c>header-click:</c>, <c>header-doubleclick:</c>, <c>sort-state:</c>,
/// <c>pane-divider:</c>, <c>pane-divider-reset</c>, <c>pane-split:</c>, <c>divider:</c>, <c>sidebar-divider-reset</c>,
/// <c>dock:</c>) and its log lines say what it did. It presses no keys. The steps run the click's and the drag's own
/// code; the real mouse is the live check's (section "24").
/// </summary>
public class DividersEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    /// <summary>
    /// A click on a heading sorts the pane clicked by that column through the key's own command, the same heading again
    /// reverses it, the chevron follows; another pane's heading sorts that pane and leaves the first; a double-click
    /// fits the column and leaves the order as it was; the key of a column agrees with the heading.
    /// </summary>
    [Fact]
    public async Task A_click_on_a_heading_sorts_that_pane_a_double_click_only_fits_and_the_key_agrees()
    {
        var (run, root, data) = Prepare("headers", """{ "version": 1, "ui": { "dualPane": true } }""");
        try
        {
            // 900 ms between two clicks on one heading: a second click within the double-click time is half of a double-click.
            var process = run.Start("first", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:800",
                "sort-state:start",
                "header-click:size",
                "until:sorted",
                "sort-state:size-down",
                "wait:900",
                "header-click:size",
                "until:sorted",
                "sort-state:size-up",
                "wait:900",
                "header-click:name",
                "until:sorted",
                "sort-state:name",
                "wait:900",
                "header-click:type|1",
                "until:sorted",
                "sort-state:type-in-the-right-pane",
                "wait:900",
                "header-doubleclick:modified",
                "wait:1500",
                "sort-state:after-the-double-click",
                // The command waits for its own sort to finish.
                "cmd:view.sortByName",
                "sort-state:key",
                "shot:done"));
            var logs = await run.FinishAsync("first", process, "done");

            var start = State(logs, "start");
            Assert.Equal(("none", "name:asc", "none"), start.Pane(0));
            Assert.Equal(("none", "name:asc", "none"), start.Pane(1));

            // Size starts with the largest first, as Ctrl+F6 does: the chevron points down.
            var sizeDown = State(logs, "size-down");
            Assert.Equal(("size:desc", "size:desc", "size:down"), sizeDown.Pane(0));
            Assert.Equal(("none", "name:asc", "none"), sizeDown.Pane(1));
            Assert.Equal(("size:asc", "size:asc", "size:up"), State(logs, "size-up").Pane(0));
            // The pane's own order is Name from A to Z: the chevron shows, since the user chose it.
            Assert.Equal(("name:asc", "name:asc", "name:up"), State(logs, "name").Pane(0));

            // The right pane's heading sorts the right pane, and makes it the active one; the left keeps its order.
            var right = State(logs, "type-in-the-right-pane");
            Assert.Equal(("extension:asc", "extension:asc", "type:up"), right.Pane(1));
            Assert.Equal(("name:asc", "name:asc", "name:up"), right.Pane(0));
            Assert.Equal(1, right.Active);

            // The double-click on Modified in the right pane: the column was fitted, the order is the one before its first click.
            Assert.Equal(("extension:asc", "extension:asc", "type:up"), State(logs, "after-the-double-click").Pane(1));
            Assert.Contains(logs, l => Message(l) == "columns fitted" && Field(l, "column").GetString() == "modified");
            Assert.Contains(logs, l => Message(l) == "the sort of a double-click's first click is taken back");

            // The key's command and the heading are one path: Ctrl+F3's command ran on the right pane after the heading's.
            Assert.Equal(("name:asc", "name:asc", "name:up"), State(logs, "key").Pane(1));
            foreach (var (command, trigger, minimum) in new[]
            {
                ("view.sortBySize", "heading", 2), ("view.sortByName", "heading", 1), ("view.sortByExtension", "heading", 1),
                ("view.sortByModified", "heading", 1), ("view.sortByName", "snapshot", 1),
            })
            {
                var ran = logs.Count(l => Message(l) == "command executed" && Field(l, "command").GetString() == command
                    && Field(l, "trigger").GetString() == trigger);
                Assert.True(ran >= minimum, $"{command} from {trigger} ran {ran} times, expected at least {minimum}");
            }

            // Nothing sorted that the steps above did not ask for: five sorts, the double-click's first one (which a newer
            // listing may have replaced before it finished) and the one that took it back.
            Assert.InRange(logs.Count(l => Message(l) == "pane sorted"), 6, 7);
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// The divider between the panes: a drag sets the left pane's share and writes <c>ui.paneSplit</c> once; Equal Panes
    /// and a double-click on the divider write null; an edit of the file moves the panes; the share is kept when the window
    /// is resized and held to the panes' least width; one pane has no divider; a second window starts on the saved share.
    /// </summary>
    [Fact]
    public async Task The_divider_between_the_panes_is_dragged_reset_followed_and_kept_as_a_share()
    {
        var (run, root, data) = Prepare("panesplit", """{ "version": 1, "ui": { "dualPane": true } }""");
        try
        {
            var configPath = Path.Combine(root, "config", "cabinetos.json");
            var process = run.Start("first", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:800",
                "pane-split:start",
                "pane-divider:300",
                "until:split-saved",
                "wait:300",
                "pane-split:dragged",
                // Time for the test to read the file before the next step writes it again.
                "wait:2500",
                "cmd:view.equalPanes",
                "until:split-saved",
                "wait:300",
                "pane-split:equal",
                "wait:2500",
                "pane-divider:300",
                "until:split-saved",
                "wait:300",
                "pane-split:again",
                "wait:2500",
                "pane-divider-reset",
                "until:split-saved",
                "wait:300",
                "pane-split:reset",
                // The test edits the file now: the divider follows.
                "until:pane-split:0.4",
                "wait:300",
                "pane-split:edited",
                "size:1000x700",
                "wait:800",
                "pane-split:narrower",
                "size:700x700",
                "wait:800",
                "pane-split:narrowest",
                "cmd:view.toggleDualPane",
                "wait:800",
                "pane-split:single",
                "shot:done"));
            await run.WaitForLabelAsync("first", "pane split shown", "dragged");
            var afterDrag = PaneSplitInFile(configPath);
            await run.WaitForLabelAsync("first", "pane split shown", "equal");
            var afterEqual = PaneSplitInFile(configPath);
            await run.WaitForLabelAsync("first", "pane split shown", "again");
            var afterAgain = PaneSplitInFile(configPath);
            await run.WaitForLabelAsync("first", "pane split shown", "reset");
            var afterReset = PaneSplitInFile(configPath);
            EditFile(configPath, ui => ui["paneSplit"] = 0.4);
            var logs = await run.FinishAsync("first", process, "done");

            // The start: equal panes, the divider over the gap between them.
            var start = Split(logs, "start");
            Assert.Null(start.Setting);
            Assert.Equal(0.5, start.Share);
            Assert.True(start.Dual && start.SplitterVisible);
            AssertNear(8, start.SplitterWidth, 0.6, "the divider's width");
            AssertNear(start.Room / 2, start.LeftColumn, 1.5, "the left column at the start");
            AssertNear(start.LeftWidth, start.RightWidth, 1.5, "the panes' widths at the start");

            // A drag of the divider to 300 px: the share is 300 of the room, written once as a number with three decimals.
            var dragged = Split(logs, "dragged");
            AssertNear(300, dragged.LeftColumn, 1.5, "the left column after the drag");
            AssertNear(300.0 / dragged.Room, dragged.Share, 0.002, "the share after the drag");
            Assert.NotNull(afterDrag);
            AssertNear(dragged.Share, afterDrag!.Value, 0.0015, "ui.paneSplit in the file after the drag");
            var dragLines = logs.Count(l => Message(l) == "pane split" && Field(l, "how").GetString() == "drag" && Field(l, "setting").ValueKind == JsonValueKind.Number
                && Math.Abs(Field(l, "setting").GetDouble() - afterDrag.Value) < 1e-9);
            Assert.True(dragLines == 2, $"each of the two drags logs once: {dragLines} lines for the first one's value");

            // Equal Panes (the command) and the divider's double-click write null.
            var equal = Split(logs, "equal");
            Assert.Null(equal.Setting);
            AssertNear(equal.Room / 2, equal.LeftColumn, 1.5, "the left column after Equal Panes");
            Assert.Null(afterEqual);
            Assert.NotNull(afterAgain);
            var reset = Split(logs, "reset");
            Assert.Null(reset.Setting);
            Assert.Null(afterReset);
            Assert.Contains(logs, l => Message(l) == "pane split" && Field(l, "how").GetString() == "command");
            Assert.Contains(logs, l => Message(l) == "pane split" && Field(l, "how").GetString() == "divider");

            // The file's edit: the panes follow, with no write back of the window's own.
            var edited = Split(logs, "edited");
            Assert.Equal(0.4, edited.Setting);
            AssertNear(0.4 * edited.Room, edited.LeftColumn, 1.5, "the left column after the file's edit");
            Assert.Contains(logs, l => Message(l) == "pane split" && Field(l, "how").GetString() == "config");

            // A smaller window keeps the share, not the pixels.
            var narrower = Split(logs, "narrower");
            Assert.True(narrower.Room < edited.Room - 100, $"the room was {edited.Room}, then {narrower.Room}");
            AssertNear(0.4 * narrower.Room, narrower.LeftColumn, 1.5, "the left column in the smaller window");

            // A window with no room for two panes of their least width keeps them equal; the setting stays as it is.
            var narrowest = Split(logs, "narrowest");
            Assert.Equal(0.4, narrowest.Setting);
            Assert.Equal(0.5, narrowest.Share);
            AssertNear(narrowest.Room / 2, narrowest.LeftColumn, 1.5, "the left column with no room to move");
            Assert.Equal(0.4, PaneSplitInFile(configPath));

            // One pane: the left takes the room and the divider is gone.
            var single = Split(logs, "single");
            Assert.False(single.Dual);
            Assert.False(single.SplitterVisible);
            AssertNear(single.Room, single.LeftColumn, 1.5, "the left column with one pane");
            Assert.DoesNotContain(logs, l => Message(l) == "the pane split was not saved");

            // A second window on a file with a share shows it from the start.
            EditFile(configPath, ui =>
            {
                ui["paneSplit"] = 0.35;
                ui["dualPane"] = true;
            });
            var second = run.Start("second", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "wait:800",
                "pane-split:restart",
                "shot:done"));
            var secondLogs = await run.FinishAsync("second", second, "done");
            var restart = Split(secondLogs, "restart");
            Assert.Equal(0.35, restart.Setting);
            AssertNear(0.35 * restart.Room, restart.LeftColumn, 1.5, "the left column in a window that started on the share");
            Assert.Contains(secondLogs, l => Message(l) == "pane split" && Field(l, "how").GetString() == "start");
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// The sidebar's divider is in the classic layout too: a drag writes <c>ui.sidebarWidth</c>, a double-click on it writes
    /// null and gives the design's width back, and the file's edit moves it; the Tool Dock's drags still save its size
    /// (<c>ui.dockSize</c>), under the panes and beside them.
    /// </summary>
    [Fact]
    public async Task The_sidebar_divider_works_in_the_classic_layout_and_the_docks_sizes_still_save()
    {
        var (run, root, data) = Prepare("sidebar", """{ "version": 1, "ui": { "dualPane": true, "layout": "classic" } }""");
        try
        {
            var configPath = Path.Combine(root, "config", "cabinetos.json");
            var process = run.Start("first", string.Join(';',
                "size:1400x800",
                $"path:{data}",
                "wait:800",
                "rail-state:start",
                "divider:300",
                "until:rail-saved",
                "wait:300",
                "rail-state:dragged",
                "wait:2500",
                "sidebar-divider-reset",
                "until:rail-saved",
                "wait:300",
                "rail-state:reset",
                "wait:2500",
                // The dock under the panes: its splitter's drag and save, with the dock hidden or shown.
                "dock:330",
                "wait:300",
                "pane-split:dock",
                "wait:2500",
                "shot:done"));
            await run.WaitForLabelAsync("first", "rail state", "dragged");
            var afterDrag = SidebarWidthInFile(configPath);
            await run.WaitForLabelAsync("first", "rail state", "reset");
            var afterReset = SidebarWidthInFile(configPath);
            await run.WaitForLabelAsync("first", "pane split shown", "dock");
            var dock = DockSizeInFile(configPath, "bottom");
            var logs = await run.FinishAsync("first", process, "done");

            var start = Assert.Single(logs, l => Message(l) == "rail state" && Field(l, "label").GetString() == "start");
            Assert.Equal("classic", Field(start, "layout").GetString());
            Assert.False(Field(start, "rail_visible").GetBoolean());
            Assert.True(Field(start, "splitter_visible").GetBoolean(), "the sidebar's divider shows in the classic layout");
            Assert.Equal(224, Field(start, "sidebar_width").GetInt32());

            // 300 px: the column and the file.
            var dragged = Assert.Single(logs, l => Message(l) == "rail state" && Field(l, "label").GetString() == "dragged");
            AssertNear(300, Field(dragged, "sidebar_width").GetDouble(), 1.5, "the sidebar's width after the drag");
            Assert.Equal(300, afterDrag);

            // A double-click gives the design's width back and writes null.
            var reset = Assert.Single(logs, l => Message(l) == "rail state" && Field(l, "label").GetString() == "reset");
            AssertNear(224, Field(reset, "sidebar_width").GetDouble(), 1.5, "the sidebar's width after the reset");
            Assert.Null(afterReset);

            // The dock's size is the dock's own key, and the dividers' work leaves it alone.
            Assert.Equal(330, dock);
            Assert.DoesNotContain(logs, l => Message(l) == "the pane split was not saved");
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }

        // The same drags with the terminal beside the panes (ui.layout "right"), where the right splitter is the dock's.
        var (rightRun, rightRoot, rightData) = Prepare("sidebar-right", """{ "version": 1, "ui": { "dualPane": true, "layout": "right" } }""");
        try
        {
            var configPath = Path.Combine(rightRoot, "config", "cabinetos.json");
            var process = rightRun.Start("right", string.Join(';',
                "size:1400x800",
                $"path:{rightData}",
                "wait:800",
                "rail-state:start",
                "divider:260",
                "until:rail-saved",
                "wait:300",
                "rail-state:dragged",
                "dock:340",
                "wait:300",
                "pane-split:dock",
                "wait:2500",
                "shot:done"));
            await rightRun.WaitForLabelAsync("right", "pane split shown", "dock");
            var width = SidebarWidthInFile(configPath);
            var dock = DockSizeInFile(configPath, "right");
            var logs = await rightRun.FinishAsync("right", process, "done");

            var start = Assert.Single(logs, l => Message(l) == "rail state" && Field(l, "label").GetString() == "start");
            Assert.Equal("right", Field(start, "layout").GetString());
            Assert.True(Field(start, "splitter_visible").GetBoolean(), "the sidebar's divider shows in the right layout");
            Assert.Equal(260, width);
            Assert.Equal(340, dock);
        }
        finally
        {
            rightRun.Stop();
            Repo.RemoveTempFolder(rightRoot);
        }
    }

    private static void AssertNear(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"{what}: expected {expected} within {tolerance}, was {actual}");

    // ui.paneSplit in the file: the number, or null when it is null or absent.
    private static double? PaneSplitInFile(string configPath) => Ui(configPath)?["paneSplit"]?.GetValue<double>();

    private static int? SidebarWidthInFile(string configPath) => Ui(configPath)?["sidebarWidth"]?.GetValue<int>();

    private static int? DockSizeInFile(string configPath, string placement) => (Ui(configPath)?["dockSize"] as JsonObject)?[placement]?.GetValue<int>();

    private static JsonObject? Ui(string configPath)
    {
        using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonNode.Parse(stream)?["ui"] as JsonObject;
    }

    // The hand edit of the file: the window follows it as it follows any change made outside.
    private static void EditFile(string configPath, Action<JsonObject> change)
    {
        JsonNode root;
        using (var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            root = JsonNode.Parse(stream)!;
        }
        var ui = root["ui"] as JsonObject ?? new JsonObject();
        root["ui"] = ui;
        change(ui);
        File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record SortLine(JsonElement Fields)
    {
        public int Active => Fields.GetProperty("active").GetInt32();

        // The pane's own order, the order its listing is in, and the chevron its header shows.
        public (string Own, string Effective, string Arrow) Pane(int pane) =>
            (Fields.GetProperty($"pane{pane}_own").GetString()!, Fields.GetProperty($"pane{pane}_effective").GetString()!,
                Fields.GetProperty($"pane{pane}_arrow").GetString()!);
    }

    private static SortLine State(List<string> logs, string label)
    {
        var line = Assert.Single(logs, l => Message(l) == "sort state" && Field(l, "label").GetString() == label);
        using var parsed = JsonDocument.Parse(line);
        return new SortLine(parsed.RootElement.GetProperty("fields").Clone());
    }

    private sealed record SplitLine(JsonElement Fields)
    {
        public double? Setting => Fields.GetProperty("setting").ValueKind == JsonValueKind.Number ? Fields.GetProperty("setting").GetDouble() : null;

        public double Share => Fields.GetProperty("share").GetDouble();

        public bool Dual => Fields.GetProperty("dual").GetBoolean();

        public bool SplitterVisible => Fields.GetProperty("splitter_visible").GetBoolean();

        public double SplitterWidth => Fields.GetProperty("splitter_width").GetDouble();

        public double Room => Fields.GetProperty("room").GetDouble();

        public double LeftColumn => Fields.GetProperty("left_column").GetDouble();

        public double LeftWidth => Fields.GetProperty("left_width").GetDouble();

        public double RightWidth => Fields.GetProperty("right_width").GetDouble();
    }

    private static SplitLine Split(List<string> logs, string label)
    {
        var line = Assert.Single(logs, l => Message(l) == "pane split shown" && Field(l, "label").GetString() == label);
        using var parsed = JsonDocument.Parse(line);
        return new SplitLine(parsed.RootElement.GetProperty("fields").Clone());
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
            start.Environment["CABINETOS_UPDATE_DIR"] = Path.Combine(root, "update");
            start.Environment["CABINETOS_UI_SNAPSHOT"] = Path.Combine(root, "shots-" + name);
            start.Environment["CABINETOS_UI_SNAPSHOT_STEPS"] = steps;
            var process = Process.Start(start)!;
            _started.Add(process);
            return process;
        }

        // Waits until the window logged the line with this message and label.
        public async Task WaitForLabelAsync(string name, string message, string label)
        {
            var folder = Path.Combine(root, "logs-" + name);
            await WaitForAsync(() => LogFiles.Ui(folder).Any(l => Message(l) == message && Field(l, "label").GetString() == label),
                $"the {name} window's \"{message}\" {label}", TimeSpan.FromSeconds(90));
        }

        // Waits for the last snapshot, closes the window the way a user does and returns the UI's log lines.
        public async Task<List<string>> FinishAsync(string name, Process process, string lastShot)
        {
            var shot = Path.Combine(root, "shots-" + name, lastShot + ".png");
            try
            {
                await WaitForAsync(() => File.Exists(shot), $"the {name} window's last snapshot", TimeSpan.FromSeconds(120));
            }
            catch (Xunit.Sdk.XunitException error)
            {
                throw new Xunit.Sdk.XunitException($"{error.Message}\nthe window's last log lines (times in UTC):\n{WindowLog.Last(LogFiles.Ui(Path.Combine(root, "logs-" + name)), 60)}");
            }
            process.CloseMainWindow();
            Assert.True(process.WaitForExit(60_000), $"the {name} window did not close");
            var logs = LogFiles.Ui(Path.Combine(root, "logs-" + name));
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

    // A folder with a window's setting up: the configuration, and a data folder of files and a folder.
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
        Directory.CreateDirectory(Path.Combine(data, "a folder"));
        File.WriteAllText(Path.Combine(data, "alpha.txt"), "x");
        File.WriteAllText(Path.Combine(data, "Alphabet.md"), "xx");
        File.WriteAllText(Path.Combine(data, "beta.json"), "{}");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), configJson);
        return (new Run(root, exe, core), root, data);
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
