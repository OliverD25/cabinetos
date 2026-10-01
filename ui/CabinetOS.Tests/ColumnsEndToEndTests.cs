using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Themes;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// The file panes' column widths on a real window and core (docs/ui.md, "Column widths"). It opens
/// windows on the desktop, so it runs only with <c>CABINETOS_UI_E2E=1</c> and a built window (Debug)
/// and a core that knows <c>ui.columns</c>. The window's snapshot steps drive it (<c>columns:</c>,
/// <c>column-drag:</c>, <c>column-fit:</c>) and its log lines "columns shown", "columns changed",
/// "columns fitted" and "columns saved" say what it did. It presses no keys.
/// </summary>
public class ColumnsEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    /// <summary>
    /// The start is the theme's split; a drag of the Modified|Type grip by 40 px makes Modified 40 px
    /// wider in both panes and is saved in ui.columns; a fit of Type gives it its widest text on screen
    /// with its cell's room; the reset gives the theme's split back and writes null; Commander Compact
    /// keeps the user's widths; a second window started on the file shows them from the start.
    /// </summary>
    [Fact]
    public async Task A_drag_a_fit_and_a_reset_change_both_panes_are_saved_and_outlive_the_window_and_the_theme()
    {
        var (run, root, data) = Prepare("columns");
        try
        {
            var configPath = Path.Combine(root, "config", "cabinetos.json");
            var process = run.Start("first", string.Join(';',
                "size:1200x700",
                // Room for the widest Type text: in two panes of a 1200 px window beside the sidebar, a fit of it would
                // leave Name under its 120 px, and a fit never does.
                "cmd:view.toggleSidebar",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:800",
                "columns:start",
                "column-drag:2|40",
                "until:config",
                "wait:300",
                "columns:dragged",
                // Time for the test to read the file before the next step writes it again.
                "wait:3000",
                "column-fit:type",
                "until:config",
                "wait:300",
                "columns:fitted",
                "cmd:view.resetColumns",
                "until:config",
                "wait:300",
                "columns:reset",
                "wait:3000",
                "column-drag:2|40",
                "until:config",
                "wait:300",
                "columns:saved",
                "theme:commander-compact",
                "wait:500",
                "columns:compact",
                "theme:default",
                "wait:500",
                "shot:done"));
            // The file between the steps: the test reads it once the window logged the step after the save.
            await run.WaitForColumnsAsync("first", "dragged");
            var afterDrag = Columns(configPath);
            await run.WaitForColumnsAsync("first", "reset");
            var afterReset = Columns(configPath);
            var logs = await run.FinishAsync("first", process, "done");

            var start = Shown(logs, "start");
            var defaults = new ColumnLayout(MetricsMapper.Default, null);
            foreach (var pane in new[] { 0, 1 })
            {
                var split = defaults.Resolve(start.List(pane));
                AssertNear(split.Name, start.Name(pane), 1, $"pane {pane}: Name at the start");
                AssertNear(split.Modified, start.Modified(pane), 1, $"pane {pane}: Modified at the start");
                AssertNear(split.Type, start.Type(pane), 1, $"pane {pane}: Type at the start");
                Assert.Equal(64, start.Size(pane));
                Assert.True(start.GripOffset(pane) < 1, $"pane {pane}: the grips are {start.GripOffset(pane)} px from the dividers");
            }
            Assert.False(start.User);

            // The Modified|Type grip by 40 px: Modified 40 px wider in both panes, Type as much narrower, Name and Size as they were.
            var dragged = Shown(logs, "dragged");
            Assert.True(dragged.User);
            foreach (var pane in new[] { 0, 1 })
            {
                AssertNear(start.Modified(pane) + 40, dragged.Modified(pane), 1.5, $"pane {pane}: Modified after the drag");
                AssertNear(start.Type(pane) - 40, dragged.Type(pane), 1.5, $"pane {pane}: Type after the drag");
                AssertNear(start.Name(pane), dragged.Name(pane), 1.5, $"pane {pane}: Name after the drag");
                Assert.Equal(64, dragged.Size(pane));
                // Every row on screen follows the header.
                Assert.Equal(dragged.HeaderText(pane), dragged.Row(pane));
                Assert.True(dragged.GripOffset(pane) < 1, $"pane {pane}: the grips are {dragged.GripOffset(pane)} px from the dividers");
            }
            var drag = logs.First(l => Message(l) == "columns changed" && Field(l, "how").GetString() == "drag");
            AssertNear(dragged.Modified(0), Field(drag, "modified").GetDouble(), 1, "the drag's logged Modified");
            Assert.Contains(logs, l => Message(l) == "columns saved");
            Assert.DoesNotContain(logs, l => Message(l) == "columns not saved");
            Assert.NotNull(afterDrag);
            Assert.Equal(dragged.Modified(0), afterDrag!["modified"]!.GetValue<double>(), 1.0);
            Assert.Equal(dragged.Type(0), afterDrag["type"]!.GetValue<double>(), 1.0);
            Assert.Equal(64, afterDrag["size"]!.GetValue<double>());

            // The fit of Type: its widest text on screen with its cell's room, or its heading with the chevron's.
            var fitted = Shown(logs, "fitted");
            var fit = Assert.Single(logs, l => Message(l) == "columns fitted" && Field(l, "column").GetString() == "type");
            var text = Field(fit, "type_text").GetString();
            Assert.False(string.IsNullOrEmpty(text), "the fit found no Type text on screen");
            var widest = Field(fit, "type_text_width").GetDouble();
            var wanted = Math.Max(40, Math.Max(widest + Field(fit, "type_extra").GetDouble(), Field(fit, "type_heading").GetDouble()));
            AssertNear(Math.Ceiling(wanted), Field(fit, "type_fit").GetDouble(), 1, "the fit's width");
            Assert.Equal(Field(fit, "type_fit").GetDouble(), Field(fit, "type").GetDouble());
            foreach (var pane in new[] { 0, 1 })
            {
                AssertNear(wanted, fitted.Type(pane), 2, $"pane {pane}: Type after the fit ({text}, {widest} px)");
                AssertNear(dragged.Modified(pane), fitted.Modified(pane), 1, $"pane {pane}: Modified kept by the fit of Type");
            }
            Assert.Contains(logs, l => Message(l) == "columns changed" && Field(l, "how").GetString() == "fit");

            // The reset: the theme's split again, and null in the file.
            var reset = Shown(logs, "reset");
            Assert.False(reset.User);
            foreach (var pane in new[] { 0, 1 })
            {
                AssertNear(start.Modified(pane), reset.Modified(pane), 1, $"pane {pane}: Modified after the reset");
                AssertNear(start.Type(pane), reset.Type(pane), 1, $"pane {pane}: Type after the reset");
                AssertNear(start.Name(pane), reset.Name(pane), 1, $"pane {pane}: Name after the reset");
            }
            Assert.Null(afterReset);
            Assert.Contains(logs, l => Message(l) == "columns changed" && Field(l, "how").GetString() == "reset");

            // Commander Compact keeps the widths the user set; its gap of 8 px comes off Name, three times.
            var saved = Shown(logs, "saved");
            var compact = Shown(logs, "compact");
            Assert.True(compact.User);
            foreach (var pane in new[] { 0, 1 })
            {
                AssertNear(saved.Modified(pane), compact.Modified(pane), 1, $"pane {pane}: Modified in Commander Compact");
                AssertNear(saved.Type(pane), compact.Type(pane), 1, $"pane {pane}: Type in Commander Compact");
                AssertNear(saved.Size(pane), compact.Size(pane), 1, $"pane {pane}: Size in Commander Compact");
                AssertNear(compact.List(pane) - 24 - compact.Modified(pane) - compact.Type(pane) - compact.Size(pane), compact.Name(pane), 1.5,
                    $"pane {pane}: Name in Commander Compact");
            }
            Assert.Contains(logs, l => Message(l) == "columns changed" && Field(l, "how").GetString() == "theme");
            var savedFile = Columns(configPath);
            Assert.NotNull(savedFile);

            // A second window on the same file shows the saved widths from the start.
            var second = run.Start("second", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "wait:800",
                "columns:restart",
                "shot:done"));
            var secondLogs = await run.FinishAsync("second", second, "done");
            var restart = Shown(secondLogs, "restart");
            Assert.True(restart.User);
            foreach (var pane in new[] { 0, 1 })
            {
                Assert.Equal(savedFile!["modified"]!.GetValue<double>(), restart.Modified(pane), 1.0);
                Assert.Equal(savedFile["type"]!.GetValue<double>(), restart.Type(pane), 1.0);
                Assert.Equal(savedFile["size"]!.GetValue<double>(), restart.Size(pane), 1.0);
            }
            Assert.Contains(secondLogs, l => Message(l) == "columns changed" && Field(l, "how").GetString() == "config");
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    private static void AssertNear(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"{what}: expected {expected} within {tolerance} px, was {actual}");

    // ui.columns in the file: the object, or null when it is null or absent.
    private static JsonObject? Columns(string configPath)
    {
        using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonNode.Parse(stream)?["ui"]?["columns"] as JsonObject;
    }

    private sealed record ColumnsLine(JsonElement Fields)
    {
        public bool User => Fields.GetProperty("user").GetBoolean();

        public double Name(int pane) => Get(pane, "name");

        public double Modified(int pane) => Get(pane, "modified");

        public double Type(int pane) => Get(pane, "type");

        public double Size(int pane) => Get(pane, "size");

        public double List(int pane) => Get(pane, "list");

        public double GripOffset(int pane) => Get(pane, "grip_offset");

        public string Row(int pane) => Fields.GetProperty($"pane{pane}_row").GetString()!;

        // The header's widths as the row's are written, to compare the two.
        public string HeaderText(int pane) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{Name(pane):0.#}/{Modified(pane):0.#}/{Type(pane):0.#}/{Size(pane):0.#}");

        private double Get(int pane, string name) => Fields.GetProperty($"pane{pane}_{name}").GetDouble();
    }

    private static ColumnsLine Shown(List<string> logs, string label)
    {
        var line = Assert.Single(logs, l => Message(l) == "columns shown" && Field(l, "label").GetString() == label);
        using var parsed = JsonDocument.Parse(line);
        return new ColumnsLine(parsed.RootElement.GetProperty("fields").Clone());
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

        // Waits until the window logged the "columns shown" line with this label.
        public async Task WaitForColumnsAsync(string name, string label)
        {
            var folder = Path.Combine(root, "logs-" + name);
            await WaitForAsync(() => LogFiles.Ui(folder).Any(l => Message(l) == "columns shown" && Field(l, "label").GetString() == label),
                $"the {name} window's columns {label}", TimeSpan.FromSeconds(90));
        }

        // Waits for the last snapshot, closes the window the way a user does and returns the UI's log lines.
        public async Task<List<string>> FinishAsync(string name, Process process, string lastShot)
        {
            var shot = Path.Combine(root, "shots-" + name, lastShot + ".png");
            await WaitForAsync(() => File.Exists(shot), $"the {name} window's last snapshot", TimeSpan.FromSeconds(120));
            process.CloseMainWindow();
            Assert.True(process.WaitForExit(20_000), $"the {name} window did not close");
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

    // A folder with a window's setting up: dual panes, and a data folder of three files and a folder.
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
        Directory.CreateDirectory(Path.Combine(data, "a folder"));
        File.WriteAllText(Path.Combine(data, "alpha.txt"), "x");
        File.WriteAllText(Path.Combine(data, "Alphabet.md"), "x");
        File.WriteAllText(Path.Combine(data, "beta.json"), "{}");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), """{ "version": 1, "ui": { "dualPane": true } }""");
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
