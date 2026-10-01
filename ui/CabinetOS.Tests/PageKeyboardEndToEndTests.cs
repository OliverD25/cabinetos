using System.Diagnostics;
using System.Text.Json;
using CabinetOS.Core.Ipc;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// The keyboard handed to the terminal's web page (docs/ui.md, "The terminal"; <c>PageKeyboard</c>). The live check of
/// 2026-10-01 on the Omen laptop lost one hand-over: no "a page has the keyboard" line came after Ctrl+Alt+P. A hand-over
/// is a chain of checks (each a timer later than the one before), and a chain that ends without a line cannot be told from
/// a page that never got the keys. This test shows the terminal again and again the way sub-phase 11a does and reads the
/// window's log. Opt-in with <c>CABINETOS_UI_E2E=1</c>, like the other end-to-end tests.
/// </summary>
public class PageKeyboardEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";
    private const int HandOvers = 20;

    [Fact]
    public async Task The_terminal_gets_the_keyboard_each_of_20_times_it_is_shown_with_the_folder_typed_at_its_prompt()
    {
        var (run, root, data) = Prepare("page-keyboard");
        try
        {
            File.WriteAllText(Path.Combine(data, "readme.md"), "# readme");
            // Sub-phase 11a of the live check: a web page (the preview) is open in the other pane, the pane has the keyboard
            // and the terminal is hidden; Ctrl+Alt+P (terminal.insertPath) shows the terminal and types the folder at its
            // prompt. until:keyboard waits for the end of the hand-over's checks, so the next round does not hide the
            // terminal while a check is still to come (a hidden terminal is wanted no more, and that check ends quietly).
            var steps = new List<string> { "size:1200x700", "pane:0", $"path:{data}", "wait:500", "open:readme.md", "until:tool", "until:keyboard" };
            for (var round = 1; round <= HandOvers; round++)
            {
                steps.Add("cmd:view.toggleTerminal {\"visible\":false}");
                steps.Add("wait:300");
                steps.Add("cmd:terminal.insertPath");
                steps.Add("until:keyboard");
                steps.Add($"focus:round-{round}");
            }
            steps.Add("shot:done");
            var process = run.Start("page-keyboard", string.Join(';', steps));
            var logs = await run.FinishAsync("page-keyboard", process, "done", TimeSpan.FromMinutes(4));

            var problems = new List<string>();
            var from = 0;
            for (var round = 1; round <= HandOvers; round++)
            {
                var label = $"round-{round}";
                var marker = logs.FindIndex(from, l => Message(l) == "keyboard focus" && Field(l, "label").GetString() == label);
                if (marker < 0)
                {
                    problems.Add($"round {round}: the window never reached it");
                    break;
                }
                var block = logs.GetRange(from, marker - from + 1);
                from = marker + 1;
                var has = block.Count(l => Message(l) == "a page has the keyboard" && Field(l, "page").GetString() == "terminal");
                var owner = logs.Skip(marker).FirstOrDefault(l => Message(l) == "keyboard owner" && Field(l, "moment").GetString() == label);
                var keysTo = owner is null ? "(not logged)" : Field(owner, "keys_to").GetString();
                var wrong = has == 0 || keysTo != "terminal" || block.Any(l => Message(l) == "a page did not get the keyboard");
                if (wrong)
                {
                    problems.Add($"round {round}: {has} lines \"a page has the keyboard\", the keys go to {keysTo}\n{Shell(block)}");
                }
            }
            Assert.True(problems.Count == 0,
                $"the terminal did not get the keyboard in {problems.Count} of {HandOvers} rounds:\n{string.Join('\n', problems)}");
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    // The lines about the keyboard of one round: the time, the message and what says where the keys went.
    private static string Shell(List<string> block) =>
        string.Join('\n', block.Where(l => Target(l) == "cabinetos_ui::shell").Select(l =>
        {
            using var parsed = JsonDocument.Parse(l);
            var fields = parsed.RootElement.TryGetProperty("fields", out var found) ? found.ToString() : "";
            return $"  {parsed.RootElement.GetProperty("ts").GetString()} {parsed.RootElement.GetProperty("level").GetString()} {Message(l)} {fields}";
        }));

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
        public async Task<List<string>> FinishAsync(string name, Process process, string lastShot, TimeSpan timeout)
        {
            var shot = Path.Combine(root, "shots-" + name, lastShot + ".png");
            await WaitForAsync(() => File.Exists(shot), $"the {name} window's last snapshot", timeout);
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

    private static string? Message(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.GetProperty("message").GetString();
    }

    private static string? Target(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.GetProperty("target").GetString();
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
