using System.Diagnostics;
using System.Text.Json;
using CabinetOS.Core.Ipc;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// The keys audit of Phase 19d (docs/log/2026-10-01/keys-audit-report.md): places where a key did nothing or the
/// wrong thing. The window runs on a real core; the snapshot step <c>key:</c> sends each key as key messages to the
/// window's input window, so WinUI routes it as it routes a real one (the window's PreviewKeyDown, the focused
/// control, Tab's move, a dialog's buttons) without the window being in front. Opt-in with
/// <c>CABINETOS_UI_E2E=1</c>, like the other end-to-end tests.
/// </summary>
public class KeysEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    [Fact]
    public async Task Tab_and_Enter_reach_the_buttons_of_a_dialog()
    {
        var (run, root, data) = Prepare("keys-dialog");
        try
        {
            // Shift+Delete's question: Cancel has the keyboard; Tab goes to "Delete permanently", Enter presses it.
            var process = run.Start("dialog", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "select:alpha.txt",
                "cmd-nowait:file.deletePermanently",
                "wait:1000",
                "focus:dialog",
                "key:tab",
                "wait:300",
                "focus:after-tab",
                "key:enter",
                "wait:1500",
                "focus:after-enter",
                "dismiss",
                "wait:300",
                "shot:done"));
            var logs = await run.FinishAsync("dialog", process, "done");

            var focus = Focus(logs);
            Assert.Equal(("dialog", "CloseButton"), (Field(focus["dialog"], "within").GetString(), Field(focus["dialog"], "x_name").GetString()));
            // The dialog's own buttons: Cancel is its CloseButton, "Delete permanently" its PrimaryButton.
            Assert.Equal(("dialog", "PrimaryButton"), (Field(focus["after-tab"], "within").GetString(), Field(focus["after-tab"], "x_name").GetString()));
            Assert.DoesNotContain(logs, l => Message(l) == "key held by a dialog");
            var closed = Assert.Single(logs, l => Message(l) == "dialog closed");
            Assert.Equal("Primary", Field(closed, "result").GetString());
            Assert.Equal("window", Field(focus["after-enter"], "within").GetString());
            Assert.False(File.Exists(Path.Combine(data, "alpha.txt")), "Enter on Delete permanently removed the file");
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    [Fact]
    public async Task Tab_gives_the_keyboard_to_a_tool_tab_in_the_other_pane()
    {
        var (run, root, data) = Prepare("keys-tool-tab");
        try
        {
            // Enter on a Markdown file opens it in the other pane's tool tab; the keyboard stays in the list until Tab.
            var process = run.Start("tool", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "open:readme.md",
                "until:tool",
                "wait:800",
                "focus:before",
                "key:tab",
                "wait:800",
                "focus:after",
                "shot:done"));
            var logs = await run.FinishAsync("tool", process, "done");

            var focus = Focus(logs);
            Assert.Equal("FilePane", Field(focus["before"], "element").GetString());
            Assert.Contains(logs, l => Message(l) == "command executed" && Field(l, "command").GetString() == "view.focusOtherPane"
                && Field(l, "trigger").GetString() == "key");
            Assert.Equal("WebView2", Field(focus["after"], "element").GetString());
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    [Fact]
    public async Task Tab_keeps_the_keyboard_in_the_theme_picker_and_in_the_plugin_list()
    {
        var (run, root, data) = Prepare("keys-overlays");
        try
        {
            var process = run.Start("overlays", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "cmd:preferences.selectColorTheme",
                "wait:600",
                "focus:picker",
                "key:tab",
                "wait:300",
                "focus:picker-tab",
                "key:escape",
                "wait:400",
                "cmd:plugins.list",
                "wait:800",
                "focus:plugins",
                "key:tab",
                "wait:300",
                "focus:plugins-tab",
                "key:escape",
                "wait:400",
                "focus:end",
                "shot:done"));
            var logs = await run.FinishAsync("overlays", process, "done");

            var focus = Focus(logs);
            (string?, string?) Where(string label) => (Field(focus[label], "element").GetString(), Field(focus[label], "name").GetString());
            Assert.NotEqual("FilePane", Where("picker").Item1);
            Assert.Equal(Where("picker"), Where("picker-tab"));
            Assert.Equal(("Button", "Close"), Where("plugins"));
            Assert.Equal(Where("plugins"), Where("plugins-tab"));
            Assert.Equal("FilePane", Where("end").Item1);
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    private static Dictionary<string, string> Focus(List<string> logs) =>
        logs.Where(l => Message(l) == "keyboard focus").ToDictionary(l => Field(l, "label").GetString()!);

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

    // A window's setting up: dual panes, a data folder of three files and a Markdown file, the Markdown Preview tool.
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
        foreach (var name in new[] { "alpha.txt", "beta.txt", "gamma.txt" })
        {
            File.WriteAllText(Path.Combine(data, name), "x");
        }
        File.WriteAllText(Path.Combine(data, "readme.md"), "# readme");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), """{ "version": 1, "ui": { "dualPane": true } }""");
        CopyFolder(Path.Combine(Repo.Root, "sdk", "tools", "markdown-preview"), Path.Combine(root, "tools", "markdown-preview"));
        return (new Run(root, exe, core), root, data);
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
