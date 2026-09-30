using System.Diagnostics;
using System.Text.Json;
using CabinetOS.Core.Ipc;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// A click on the window's chrome leaves the keyboard in the pane (docs/ui.md, "The top row"), so Tab, bound to
/// <c>view.focusOtherPane</c> in the context <c>filesView</c>, still switches panes. The window runs on a real core with
/// the snapshot steps; the <c>click:</c> step presses a button through its automation peer without a pointer, so the live
/// check's real mouse is the last proof. Opt-in with <c>CABINETOS_UI_E2E=1</c>, like the other end-to-end tests.
/// </summary>
public class ChromeKeyboardEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    [Fact]
    public async Task The_keyboard_stays_in_the_pane_after_the_top_rows_buttons_are_pressed()
    {
        var (run, root, data) = Prepare("chrome-keyboard");
        try
        {
            var process = run.Start("chrome", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "focus:start",
                // The dual toggle twice: one pane, then two again; the hamburger and the workspace pill open a dropdown that Esc closes.
                "click:Toggle dual pane",
                "wait:400",
                "focus:after-dual",
                "click:Toggle dual pane",
                "wait:400",
                "focus:after-dual-back",
                "click:Menu",
                "wait:300",
                "cmd:overlay.close",
                "wait:300",
                "focus:after-menu",
                "click:Workspace Default",
                "wait:300",
                "cmd:overlay.close",
                "wait:300",
                "focus:after-workspace",
                "shot:done"));
            var logs = await run.FinishAsync("chrome", process, "done");

            var focus = logs.Where(l => Message(l) == "keyboard focus").ToDictionary(l => Field(l, "label").GetString()!);
            var labels = new[] { "start", "after-dual", "after-dual-back", "after-menu", "after-workspace" };
            Assert.Equal(labels, labels.Where(focus.ContainsKey));
            // What the pane's own list is called is the baseline's business; it is never a button, and always inside the window.
            var baseline = Field(focus["start"], "element").GetString();
            Assert.NotEqual("Button", baseline);
            Assert.NotEqual("none", baseline);
            foreach (var label in labels)
            {
                Assert.Equal((label, baseline), (label, Field(focus[label], "element").GetString()));
                Assert.Equal((label, "window"), (label, Field(focus[label], "within").GetString()));
            }
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    private sealed class Run(string root, string exe, string core)
    {
        private readonly List<Process> _started = [];

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

    // A window's setting up: dual panes, and a data folder of three files.
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
        foreach (var name in new[] { "alpha.txt", "Alphabet.md", "beta.txt" })
        {
            File.WriteAllText(Path.Combine(data, name), "x");
        }
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), """{ "version": 1, "ui": { "dualPane": true } }""");
        return (new Run(root, exe, core), root, data);
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
