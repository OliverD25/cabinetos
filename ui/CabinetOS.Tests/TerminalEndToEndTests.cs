using System.Diagnostics;
using System.Text.Json;
using CabinetOS.Core.Ipc;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Unit 1 of the terminal sprint on a real window and core (docs/ui.md, "The terminal"): sessions bound to a pane,
/// Ctrl+` summoning the pane's own session, the Zero-Hijack rule, the mode toggle with the core's event, and the
/// terminal's tab keys. The snapshot step <c>key:</c> sends a key as key messages to the window's input window, or
/// through DevTools into the terminal's page when the page has the keyboard, so the page's own key path runs.
/// <c>terminal-state:</c> logs the header's tabs ("*1 cmd [Left] Locked", the shown one marked) and who has the
/// keyboard. The shells are cmd (always on Windows) and a cmd profile that says it is linkable. Opt-in with
/// <c>CABINETOS_UI_E2E=1</c>, like the other end-to-end tests.
/// </summary>
public class TerminalEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    /// <summary>
    /// Ctrl+` in the left pane starts the left pane's session; a click in the right pane and Ctrl+` there starts the
    /// right pane's own, and the dock stays. Ctrl+` in the terminal gives the keyboard back to the pane, and only that
    /// second Ctrl+` from the pane hides the dock; the next one brings the same session back, without a new shell.
    /// </summary>
    [Fact]
    public async Task Ctrl_backquote_in_each_pane_shows_that_pane_s_session_and_never_hides_when_the_pane_changes()
    {
        var (run, root, data) = Prepare("terminal-summon");
        try
        {
            var process = run.Start("summon", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "key:ctrl+backquote",
                "until:terminals:1",
                "wait:1200",
                "terminal-state:left",
                // The click's own effect: the right pane gets the keyboard and becomes the active pane.
                "pane:1",
                "wait:400",
                "key:ctrl+backquote",
                "until:terminals:2",
                "wait:1200",
                "terminal-state:right",
                "focus:right",
                // In the terminal: back to the pane. From the pane, the second one hides.
                "key:ctrl+backquote",
                "wait:800",
                "terminal-state:back",
                // The active pane changes and comes back in between, as closing a tool in the other pane does
                // (the live check of 2026-10-02): the second Ctrl+` still hides.
                "pane:0",
                "wait:300",
                "pane:1",
                "wait:300",
                "key:ctrl+backquote",
                "wait:800",
                "terminal-state:hidden",
                "key:ctrl+backquote",
                "wait:1500",
                "terminal-state:again",
                "shot:done"));
            var logs = await run.FinishAsync("summon", process, "done");
            var state = States(logs);
            string Tabs(string label) => Field(state[label], "tabs").GetString()!;
            bool Dock(string label) => Field(state[label], "dock").GetBoolean();
            bool Keyboard(string label) => Field(state[label], "terminal_keyboard").GetBoolean();

            var opened = logs.Where(l => Message(l) == "terminal session opened").ToList();
            Assert.True(opened.Count == 2, "two sessions, one per pane" + Evidence(logs));
            Assert.Equal(["left", "right"], opened.Select(l => Field(l, "pane").GetString()));
            var (first, second) = (Field(opened[0], "session_id").GetUInt64(), Field(opened[1], "session_id").GetUInt64());

            Assert.Equal($"*{first} cmd [Left] Locked", Tabs("left"));
            Assert.True(Dock("left") && Keyboard("left"), "the left pane's session has the keyboard" + Evidence(logs));
            Assert.Equal($"{first} cmd [Left] Locked | *{second} cmd [Right] Locked", Tabs("right"));
            Assert.True(Dock("right") && Keyboard("right"), "the right pane's session has the keyboard" + Evidence(logs));
            Assert.Equal("right", Field(state["right"], "active_pane").GetString());
            Assert.True(PageHasKeys(logs, "right"), "Windows sends the keys to the terminal's page" + Evidence(logs));

            Assert.True(Dock("back") && !Keyboard("back"), "Ctrl+` in the terminal gave the keyboard back" + Evidence(logs));
            Assert.False(Dock("hidden"), "the second Ctrl+` from the pane hid the dock" + Evidence(logs));
            Assert.True(Dock("again") && Keyboard("again"), "the dock came back with the keyboard" + Evidence(logs));
            Assert.Equal($"{first} cmd [Left] Locked | *{second} cmd [Right] Locked", Tabs("again"));

            var actions = logs.Where(l => Message(l) == "terminal summoned").Select(l => Field(l, "action").GetString()).ToList();
            Assert.Equal(["OpenNew", "OpenNew", "HandBackToPane", "Hide", "ShowSession"], actions);
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// The Zero-Hijack rule: with the left pane's session shown and holding the keyboard, a click in the right pane and
    /// navigation there change neither the shown session nor anything in the shell: no tab is shown anew, no path is
    /// typed. The click takes the keyboard to the pane, as any click does.
    /// </summary>
    [Fact]
    public async Task A_pane_click_and_navigation_leave_the_shown_session_and_the_shell_alone()
    {
        var (run, root, data) = Prepare("terminal-hijack");
        try
        {
            var other = Path.Combine(data, "sub");
            Directory.CreateDirectory(other);
            var process = run.Start("hijack", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "key:ctrl+backquote",
                "until:terminals:1",
                "wait:1200",
                "focus:in-terminal",
                "terminal-state:before",
                "pane:1",
                "wait:500",
                "focus:after-click",
                "terminal-state:after-click",
                $"path:{other}",
                "wait:800",
                "pane:0",
                $"path:{other}",
                "wait:800",
                "terminal-state:after-navigation",
                "shot:done"));
            var logs = await run.FinishAsync("hijack", process, "done");
            var state = States(logs);

            Assert.True(PageHasKeys(logs, "in-terminal"), "Windows sends the keys to the terminal's page" + Evidence(logs));
            var shown = Field(state["before"], "shown").GetUInt64();
            foreach (var label in new[] { "after-click", "after-navigation" })
            {
                Assert.Equal(shown, Field(state[label], "shown").GetUInt64());
                Assert.Equal(Field(state["before"], "tabs").GetString(), Field(state[label], "tabs").GetString());
                Assert.True(Field(state[label], "dock").GetBoolean(), label);
            }
            Assert.Equal("right", Field(state["after-click"], "active_pane").GetString());
            Assert.Equal(("FilePane", "window"), (Field(Owner(logs, "after-click"), "element").GetString(), Field(Owner(logs, "after-click"), "keys_to").GetString()));
            // Nothing went to the shell and no tab came to the front after the first.
            Assert.Single(logs, l => Message(l) == "terminal tab shown");
            Assert.DoesNotContain(logs, l => Message(l) is "paths typed at the prompt" or "cwd sync");
            Assert.Single(logs, l => Message(l) == "terminal session opened");
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// The tab's mode toggle runs <c>terminal.setMode</c>; the tab changes when the core's
    /// <c>terminal_mode_changed</c> comes back. A profile that cannot be linked shows no toggle, and the command says why.
    /// </summary>
    [Fact]
    public async Task The_mode_toggle_runs_set_mode_and_the_core_s_event_changes_the_tab()
    {
        var (run, root, data) = Prepare("terminal-mode", defaultProfile: "hooked");
        try
        {
            var process = run.Start("mode", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "key:ctrl+backquote",
                "until:terminals:1",
                "wait:800",
                "terminal-state:locked",
                "click:Locked, hooked on the left pane",
                "wait:1200",
                "terminal-state:linked",
                "cmd:terminal.setMode",
                "wait:1200",
                "terminal-state:relocked",
                "cmd:terminal.new {\"profile\":\"cmd\"}",
                "until:terminals:2",
                "wait:800",
                "cmd:terminal.setMode {\"mode\":\"linked\"}",
                "wait:800",
                "terminal-state:cmd",
                "shot:done"));
            var logs = await run.FinishAsync("mode", process, "done");
            var state = States(logs);
            string Tabs(string label) => Field(state[label], "tabs").GetString()!;
            var hooked = Field(Assert.Single(logs, l => Message(l) == "terminal session opened" && Field(l, "profile").GetString() == "hooked"), "session_id").GetUInt64();

            Assert.Equal($"*{hooked} hooked [Left] Locked", Tabs("locked"));
            Assert.True(Tabs("linked") == $"*{hooked} hooked [Left] Linked", "the toggle linked it" + Evidence(logs));
            Assert.Equal($"*{hooked} hooked [Left] Locked", Tabs("relocked"));
            var set = logs.Where(l => Message(l) == "command executed" && Field(l, "command").GetString() == "terminal.setMode").ToList();
            Assert.Equal("button", Field(set[0], "trigger").GetString());
            // The tab follows the core's event, once per change.
            var changed = logs.Where(l => Message(l) == "terminal mode changed").Select(l => Field(l, "mode").GetString()).ToList();
            Assert.Equal(["linked", "locked"], changed);
            Assert.True(Field(Assert.Single(logs, l => Message(l) == "terminal session opened" && Field(l, "profile").GetString() == "cmd"), "linkable").GetBoolean() is false);
            Assert.EndsWith(" cmd [Left] Locked", Tabs("cmd"));
            Assert.Contains(logs, l => Message(l) == "notice shown" && Field(l, "text").GetString()!.StartsWith("cmd cannot be linked to a pane", StringComparison.Ordinal));
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// In the terminal, Ctrl+Shift+T opens a session bound to the active pane, Alt+[ and Alt+] go round the tabs and
    /// the keyboard stays in the terminal, and Ctrl+Shift+W closes the shown tab. Alt+[ and Alt+] are the keys at
    /// that place, whatever the layout puts on them (the key's virtual-key code, as a Ukrainian layout sends it).
    /// </summary>
    [Fact]
    public async Task The_terminal_s_tab_keys_open_cycle_and_close_its_tabs()
    {
        var (run, root, data) = Prepare("terminal-keys");
        try
        {
            var process = run.Start("keys", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "key:ctrl+backquote",
                "until:terminals:1",
                "wait:1200",
                "key:ctrl+shift+t",
                "until:terminals:2",
                "wait:1200",
                "terminal-state:two",
                "key:alt+bracketleft",
                "wait:700",
                "terminal-state:previous",
                "key:alt+bracketright",
                "wait:700",
                "terminal-state:next",
                "key:ctrl+shift+w",
                "wait:2000",
                "terminal-state:closed",
                "focus:end",
                "shot:done"));
            var logs = await run.FinishAsync("keys", process, "done");
            var state = States(logs);
            string Tabs(string label) => Field(state[label], "tabs").GetString()!;
            var opened = logs.Where(l => Message(l) == "terminal session opened").ToList();
            Assert.True(opened.Count == 2, "Ctrl+Shift+T opened a second session" + Evidence(logs));
            Assert.All(opened, l => Assert.Equal("left", Field(l, "pane").GetString()));
            var (first, second) = (Field(opened[0], "session_id").GetUInt64(), Field(opened[1], "session_id").GetUInt64());

            Assert.Equal($"{first} cmd [Left] Locked | *{second} cmd [Left] Locked", Tabs("two"));
            Assert.True(Tabs("previous") == $"*{first} cmd [Left] Locked | {second} cmd [Left] Locked", "Alt+[ showed the previous tab" + Evidence(logs));
            Assert.Equal($"{first} cmd [Left] Locked | *{second} cmd [Left] Locked", Tabs("next"));
            Assert.True(Tabs("closed") == $"*{first} cmd [Left] Locked", "Ctrl+Shift+W closed the shown tab" + Evidence(logs));
            foreach (var label in new[] { "two", "previous", "next", "closed" })
            {
                Assert.True(Field(state[label], "terminal_keyboard").GetBoolean(), $"the terminal kept the keyboard ({label})" + Evidence(logs));
                Assert.Equal("left", Field(state[label], "active_pane").GetString());
            }
            Assert.True(PageHasKeys(logs, "end"), "Windows sends the keys to the terminal's page" + Evidence(logs));
            var ran = logs.Where(l => Message(l) == "command executed" && Field(l, "trigger").GetString() == "key")
                .Select(l => Field(l, "command").GetString()).ToList();
            foreach (var command in new[] { "terminal.new", "terminal.previousTab", "terminal.nextTab", "terminal.close" })
            {
                Assert.Contains(command, ran);
            }
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    // The lines that say what the keys and the terminal did, for the message of a failed assertion.
    private static string Evidence(List<string> logs) => "\n" + string.Join('\n', logs
        .Where(l => Message(l) is "key sent" or "key sent to a page" or "command executed" or "a key the page did not get" or "keyboard owner"
            or "terminal summoned" or "terminal state" or "terminal tab shown" or "terminal session opened" or "a page has the keyboard"
            or "terminal mode changed" or "notice shown")
        .Select(l => l.Length > 360 ? l[..360] : l));

    private static Dictionary<string, string> States(List<string> logs) =>
        logs.Where(l => Message(l) == "terminal state").ToDictionary(l => Field(l, "label").GetString()!);

    // The "keyboard owner" line the focus:<label> step wrote: XAML's focused element and the window that gets the keys.
    private static string Owner(List<string> logs, string label) =>
        logs.Last(l => Message(l) == "keyboard owner" && Field(l, "moment").GetString() == label);

    // Whether a browser's input window has the keys. The window tells the terminal's page by where that input window
    // sits on screen, which an end-to-end run's window (never in front) does not always give: then it says "a page".
    // These tests open no other page, so either answer is the terminal.
    private static bool PageHasKeys(List<string> logs, string label) =>
        Field(Owner(logs, label), "keys_to").GetString() is "terminal" or "a page";

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
            try
            {
                await WaitForAsync(() => File.Exists(shot), $"the {name} window's last snapshot", TimeSpan.FromSeconds(120));
            }
            catch (Xunit.Sdk.XunitException error)
            {
                // A step that never ends: the last lines of the window's log say which.
                var tail = LogFiles.Ui(Path.Combine(root, "logs-" + name)).TakeLast(20).Select(l => l.Length > 360 ? l[..360] : l);
                throw new Xunit.Sdk.XunitException(error.Message + "\nthe window's last log lines:\n" + string.Join('\n', tail));
            }
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

    // A window's setting up: dual panes on a data folder, and two terminal profiles: cmd, which is not linkable, and a
    // cmd that says it is (the hook of unit 2 does not exist yet, so the mode changes nothing in the shell).
    private static (Run Run, string Root, string Data) Prepare(string purpose, string defaultProfile = "cmd")
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
        foreach (var name in new[] { "alpha.txt", "beta.txt" })
        {
            File.WriteAllText(Path.Combine(data, name), "x");
        }
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), $$"""
            { "version": 1, "ui": { "dualPane": true },
              "terminal": { "defaultProfile": "{{defaultProfile}}", "profiles": [
                { "name": "cmd", "command": "cmd.exe" },
                { "name": "hooked", "command": "cmd.exe", "linkable": true } ] } }
            """);
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
