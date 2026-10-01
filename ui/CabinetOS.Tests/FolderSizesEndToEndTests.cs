using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CabinetOS.Core.Ipc;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Folder sizes for every folder of a listing, on a real window and core (panes.folderSizes; docs/ui.md,
/// "Folder sizes"). It opens windows on the desktop, so it runs only with <c>CABINETOS_UI_E2E=1</c> and a
/// built window (Debug) and a core that knows the setting. The window's snapshot steps drive it (<c>path:</c>,
/// <c>cmd:</c>, <c>focus:</c> as a marker) and press no key. The window's log lines "folder sizes asked",
/// "folder sizes counted" and "folder sizes cancelled" and the core's "measure finished" say what happened.
/// The walk that has to be stopped is the Windows folder's, which takes seconds to count.
/// </summary>
public class FolderSizesEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    // The bytes under the three folders of the data folder, and under the two of the second one.
    private const ulong DataBytes = 1000 + 500 + 3000 + 700 + 300;
    private const ulong Data2Bytes = 100 + 200;

    /// <summary>
    /// With the setting on in the file, a folder opened by a path step has its three folders counted at once,
    /// with no key, and the window logs their total; a pane that leaves the Windows folder while its count runs
    /// asks the core to cancel it, and the core ends that count as cancelled.
    /// </summary>
    [Fact]
    public async Task A_listing_counts_its_folders_with_no_key_and_leaving_the_folder_stops_the_count()
    {
        var setup = Prepare("folder-sizes-on", folderSizes: true);
        try
        {
            var process = setup.Run.Start("on", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{setup.Data}",
                "wait:2500",
                // The Windows folder takes seconds to count: the pane is on another folder before it ends.
                $"path:{setup.Windows}",
                $"path:{setup.Quiet}",
                "wait:1500",
                "shot:done"));
            var logs = await setup.Run.FinishAsync("on", process, "done");
            var core = setup.Run.CoreLines("on");
            Say(logs, core);

            // The first folders (ui.lastPaths) have no folders: nothing is asked for them.
            var asked = logs.Where(l => Message(l) == "folder sizes asked").ToList();
            Assert.True(asked.Count == 2, Story(logs));
            Assert.Equal((0, 3, "listed"), (Int(asked[0], "pane"), Int(asked[0], "folders"), Text(asked[0], "why")));
            Assert.True(Int(asked[1], "folders") > 3, "the Windows folder has more than three folders");

            // The three folders' total, as the window's Size column shows it.
            var counted = Assert.Single(logs, l => Message(l) == "folder sizes counted" && Int(l, "folders") == 3);
            Assert.Equal(DataBytes, Field(counted, "bytes").GetUInt64());
            Assert.False(Field(counted, "cancelled").GetBoolean());
            Assert.True(logs.IndexOf(asked[0]) < logs.IndexOf(counted), "counted after asked");
            var finished = Assert.Single(core, l => Message(l) == "measure finished" && !Field(l, "cancelled").GetBoolean());
            Assert.Equal(3, Int(finished, "counted"));

            // Leaving the Windows folder: one count cancelled, and the core ended it as cancelled.
            var cancelled = Assert.Single(logs, l => Message(l) == "folder sizes cancelled");
            Assert.Equal((0, 1, "left the folder"), (Int(cancelled, "pane"), Int(cancelled, "measures"), Text(cancelled, "why")));
            Assert.Contains(logs, l => Message(l) == "request sent" && Text(l, "request") == "cancel_measure");
            Assert.Contains(core, l => Message(l) == "measure cancel asked");
            Assert.Contains(core, l => Message(l) == "measure finished" && Field(l, "cancelled").GetBoolean());
            Assert.DoesNotContain(logs, l => Message(l) == "folder sizes refused");
        }
        finally
        {
            setup.Run.Stop();
            Repo.RemoveTempFolder(setup.Root);
        }
    }

    /// <summary>
    /// With the setting off: the toggle writes the file and counts the current listing at once; a second one writes
    /// it back; a folder opened while it is off asks for nothing; a hand edit of the file turns it on again and the
    /// listing is counted; turning it off while a count of the Windows folder runs cancels the count.
    /// </summary>
    [Fact]
    public async Task The_toggle_and_a_hand_edit_turn_it_on_and_off_and_off_stops_a_running_count()
    {
        var setup = Prepare("folder-sizes-toggle", folderSizes: false);
        try
        {
            var process = setup.Run.Start("toggle", string.Join(';',
                "size:1200x700",
                "pane:0",
                // Off: the data folder's three folders are not asked for.
                $"path:{setup.Data}",
                "wait:1000",
                "focus:before",
                "cmd:view.toggleFolderSizes",
                "until:config",
                "wait:1500",
                "focus:on",
                // Time for the test to read the file before the next step writes it again.
                "wait:3500",
                "cmd:view.toggleFolderSizes",
                "until:config",
                "wait:500",
                "focus:off",
                // Off: this folder's two folders are not asked for.
                $"path:{setup.Data2}",
                "wait:500",
                "focus:moved",
                // The test edits cabinetos.json by hand: the window follows, with no command.
                "until:config",
                "wait:1500",
                "focus:edited",
                $"path:{setup.Windows}",
                "cmd:view.toggleFolderSizes",
                "until:config",
                "wait:1000",
                "shot:done"));
            await setup.Run.WaitForFocusAsync("toggle", "on");
            var afterOn = FolderSizesInFile(setup.ConfigPath);
            await setup.Run.WaitForFocusAsync("toggle", "moved");
            var afterOff = FolderSizesInFile(setup.ConfigPath);
            WriteWhole(setup.ConfigPath, ConfigText(true, setup.Data2, setup.Quiet));
            var logs = await setup.Run.FinishAsync("toggle", process, "done");
            var core = setup.Run.CoreLines("toggle");
            Say(logs, core);

            // The toggle wrote the file, both ways.
            Assert.True(afterOn);
            Assert.False(afterOff);
            Assert.Equal([true, false, true, false], logs
                .Where(l => Message(l) == "folder sizes follow the configuration").Select(l => Field(l, "on").GetBoolean()));

            // Nothing was asked while the setting was off; then the current listing, each time it came on, and the Windows folder.
            var asked = logs.Where(l => Message(l) == "folder sizes asked").ToList();
            Assert.True(asked.Count == 3, Story(logs));
            var firstFollow = logs.FindIndex(l => Message(l) == "folder sizes follow the configuration");
            Assert.True(logs.IndexOf(asked[0]) > firstFollow, "nothing is asked before the setting is on");
            Assert.Equal((0, 3, "setting on"), (Int(asked[0], "pane"), Int(asked[0], "folders"), Text(asked[0], "why")));
            Assert.Equal((0, 2, "setting on"), (Int(asked[1], "pane"), Int(asked[1], "folders"), Text(asked[1], "why")));
            Assert.Equal("listed", Text(asked[2], "why"));
            Assert.True(Int(asked[2], "folders") > 3, "the Windows folder has more than three folders");

            var done = logs.Where(l => Message(l) == "folder sizes counted" && !Field(l, "cancelled").GetBoolean()).ToList();
            Assert.Equal([(3, DataBytes), (2, Data2Bytes)], done.Select(l => (Int(l, "folders"), Field(l, "bytes").GetUInt64())));
            Assert.Equal(2, core.Count(l => Message(l) == "measure finished" && !Field(l, "cancelled").GetBoolean()));

            // Off while the Windows folder counts: the count is cancelled, and the core ends it as cancelled.
            var cancelled = Assert.Single(logs, l => Message(l) == "folder sizes cancelled");
            Assert.Equal((0, 1, "setting off"), (Int(cancelled, "pane"), Int(cancelled, "measures"), Text(cancelled, "why")));
            Assert.Contains(logs, l => Message(l) == "request sent" && Text(l, "request") == "cancel_measure");
            Assert.Contains(core, l => Message(l) == "measure finished" && Field(l, "cancelled").GetBoolean());
        }
        finally
        {
            setup.Run.Stop();
            Repo.RemoveTempFolder(setup.Root);
        }
    }

    // The evidence of a run, in the test's own output: the window's lines about folder sizes and the core's about measures.
    private static void Say(List<string> logs, List<string> core) =>
        TestContext.Current.TestOutputHelper?.WriteLine(Story(logs) + Environment.NewLine
            + string.Join(Environment.NewLine, core.Where(l => Message(l)!.StartsWith("measure ", StringComparison.Ordinal))));

    // What the window said about folder sizes, for a failure's message.
    private static string Story(List<string> logs) =>
        string.Join(Environment.NewLine, logs.Where(l => Message(l)!.StartsWith("folder sizes", StringComparison.Ordinal)));

    // A hand edit as an editor saves it: the whole file at once, so the core never reads half of it.
    private static void WriteWhole(string path, string text)
    {
        var temporary = path + ".edit";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }

    // panes.folderSizes in the file, as the core wrote it.
    private static bool FolderSizesInFile(string configPath)
    {
        using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonNode.Parse(stream)?["panes"]?["folderSizes"]?.GetValue<bool>() ?? false;
    }

    private static string ConfigText(bool? folderSizes, params string[] lastPaths)
    {
        var config = new JsonObject
        {
            ["version"] = 1,
            ["ui"] = new JsonObject
            {
                ["dualPane"] = true,
                ["lastPaths"] = new JsonArray(lastPaths.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray()),
            },
        };
        if (folderSizes is { } on)
        {
            config["panes"] = new JsonObject { ["folderSizes"] = on };
        }
        return config.ToJsonString();
    }

    private sealed record Setup(Run Run, string Root, string Data, string Data2, string Quiet, string Windows, string ConfigPath);

    // A window's setting up: dual panes that start in a folder with no folders (so the first listing asks for nothing),
    // a data folder with three folders (1,000 + 500, 3,000 and 700 + 300 bytes under them), a second one with two
    // (100 and 200 bytes), and the Windows folder, which takes seconds to count.
    private static Setup Prepare(string purpose, bool folderSizes)
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
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!Directory.Exists(windows))
        {
            Assert.Skip("The Windows folder is the big tree this test counts.");
        }
        var root = Repo.NewTempFolder(purpose);
        var quiet = Path.Combine(root, "quiet");
        Directory.CreateDirectory(quiet);
        var data = Path.Combine(root, "data");
        Fill(Path.Combine(data, "alpha"), ("a1.bin", 1000), ("a2.bin", 500));
        Fill(Path.Combine(data, "beta"), ("b1.bin", 3000));
        Fill(Path.Combine(data, "gamma", "deep"), ("g1.bin", 700), ("g2.bin", 300));
        File.WriteAllText(Path.Combine(data, "readme.txt"), "a file is not a folder");
        var data2 = Path.Combine(root, "data2");
        Fill(Path.Combine(data2, "one"), ("x.bin", 100));
        Fill(Path.Combine(data2, "two"), ("y.bin", 200));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var configPath = Path.Combine(root, "config", "cabinetos.json");
        File.WriteAllText(configPath, ConfigText(folderSizes ? true : null, quiet, quiet));
        return new Setup(new Run(root, exe, core), root, data, data2, quiet, windows, configPath);
    }

    private static void Fill(string folder, params (string Name, int Bytes)[] files)
    {
        Directory.CreateDirectory(folder);
        foreach (var (name, bytes) in files)
        {
            File.WriteAllBytes(Path.Combine(folder, name), new byte[bytes]);
        }
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

        // Waits until the window logged the "keyboard focus" line of a focus step with this label.
        public async Task WaitForFocusAsync(string name, string label)
        {
            var folder = Path.Combine(root, "logs-" + name);
            await WaitForAsync(() => LogFiles.Ui(folder).Any(l => Message(l) == "keyboard focus" && Text(l, "label") == label),
                $"the {name} window's marker {label}", TimeSpan.FromSeconds(90));
        }

        // The core's log lines, once the window has closed and the core with it.
        public List<string> CoreLines(string name) =>
            LogFiles.Core(Path.Combine(root, "logs-" + name));

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

    private static int Int(string line, string name) => Field(line, name).GetInt32();

    private static string Text(string line, string name) => Field(line, name).GetString()!;

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
