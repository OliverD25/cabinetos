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
    public async Task The_palette_opened_from_a_tool_page_gives_the_keyboard_back_to_the_page()
    {
        var (run, root, data) = Prepare("keys-palette-page");
        try
        {
            // The preview opens in the other pane, which stays the inactive one: Esc used to give the keyboard to the
            // active pane's list. Then a command that takes no keyboard, chosen with Enter, leaves it with the page too.
            var process = run.Start("palette", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "open:readme.md",
                "until:tool",
                "wait:800",
                "key:tab",
                "wait:800",
                "focus:page",
                "cmd:palette.show",
                "wait:500",
                "focus:palette",
                "key:escape",
                "wait:1000",
                "focus:after-esc",
                "cmd:palette.show",
                "wait:500",
                "type:toggle sidebar",
                "wait:600",
                "key:enter",
                "wait:1000",
                "focus:after-command",
                "shot:done"));
            var logs = await run.FinishAsync("palette", process, "done");

            var focus = Focus(logs);
            Assert.Equal("WebView2", Field(focus["page"], "element").GetString());
            Assert.Equal("TextBox", Field(focus["palette"], "element").GetString());
            Assert.Equal("WebView2", Field(focus["after-esc"], "element").GetString());
            Assert.Contains(logs, l => Message(l) == "command executed" && Field(l, "command").GetString() == "view.toggleSidebar"
                && Field(l, "trigger").GetString() == "palette");
            Assert.Equal("WebView2", Field(focus["after-command"], "element").GetString());
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

    [Fact]
    public async Task The_pane_s_keys_that_type_nothing_act_on_the_pane_from_its_find_box()
    {
        var (run, root, data) = Prepare("keys-find-box");
        var other = Path.Combine(root, "other");
        Directory.CreateDirectory(other);
        try
        {
            // The keyboard stays in the find box throughout: a letter filters, Enter finds, F5 copies the cursor row,
            // Ctrl+A selects the box's text (the next letter replaces it), Ctrl+T opens a tab, Ctrl+W closes it.
            var process = run.Start("find", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{other}",
                "wait:500",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "cmd:search.focus",
                "wait:400",
                "focus:find",
                "key:e",
                "wait:600",
                "shell:typed",
                "key:enter",
                "wait:300",
                "key:f5",
                "wait:2000",
                "focus:after-copy",
                "shell:before-select-all",
                "key:ctrl+a",
                "wait:300",
                "shell:after-ctrl-a",
                "key:l",
                "wait:600",
                "shell:after-select-all",
                "key:ctrl+t",
                "wait:1000",
                "shell:new-tab",
                "cmd:search.focus",
                "wait:400",
                "focus:new-tab-find",
                "key:ctrl+w",
                "wait:1000",
                "shell:closed-tab",
                "shot:done"));
            var logs = await run.FinishAsync("find", process, "done");

            var focus = Focus(logs);
            var shell = logs.Where(l => Message(l) == "shell state").ToDictionary(l => Field(l, "label").GetString()!);
            string Pane(string label, string field) => Field(shell[label], "pane0_" + field).ToString();
            bool RanByKey(string command) => logs.Any(l => Message(l) == "command executed"
                && Field(l, "command").GetString() == command && Field(l, "trigger").GetString() == "key");

            Assert.Equal(("TextBox", "Input"), (Field(focus["find"], "element").GetString(), Field(focus["find"], "x_name").GetString()));
            // "e" is in beta.txt and readme.md.
            Assert.Equal(("e", "2"), (Pane("typed", "find"), Pane("typed", "shown")));
            Assert.True(RanByKey("file.copyToOtherPane"), "F5 in the find box ran the copy");
            Assert.True(File.Exists(Path.Combine(other, "beta.txt")), "F5 copied the cursor row, beta.txt, to the other pane");
            Assert.Equal(("TextBox", "Input"), (Field(focus["after-copy"], "element").GetString(), Field(focus["after-copy"], "x_name").GetString()));
            // Ctrl+A left the pane's selection (the cursor row) as it was: in the pane it would select both rows shown.
            Assert.Equal("beta.txt", Pane("before-select-all", "all_selected"));
            Assert.Equal("beta.txt", Pane("after-ctrl-a", "all_selected"));
            // It selected the box's "e", which "l" replaced: "l" finds alpha.txt, "el" would find nothing.
            Assert.Equal(("l", "1"), (Pane("after-select-all", "find"), Pane("after-select-all", "shown")));
            Assert.True(RanByKey("tab.new"), "Ctrl+T in the find box opened a tab");
            Assert.Equal(2, Pane("new-tab", "tabs").Split(" | ").Length);
            Assert.Equal("TextBox", Field(focus["new-tab-find"], "element").GetString());
            Assert.True(RanByKey("tab.close"), "Ctrl+W in the find box closed the tab");
            Assert.Single(Pane("closed-tab", "tabs").Split(" | "));
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    [Fact]
    public async Task A_tool_page_hands_back_the_tab_keys_and_the_keyboard_follows_the_front_tab()
    {
        var (run, root, data) = Prepare("keys-page-tabs");
        try
        {
            // The preview opens in the other pane, which stays the inactive one: the tab keys are about the pane whose page
            // has the keyboard. Each key in the page goes to the window as a message and runs the command; the keyboard
            // follows the tab that comes to the front: its list for a folder tab, its page for the preview.
            var process = run.Start("page-tabs", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "open:readme.md",
                "until:tool",
                "wait:800",
                "key:tab",
                "wait:800",
                "focus:page",
                "tabs:page",
                "key:ctrl+tab",
                "wait:1200",
                "focus:next",
                "tabs:next",
                "key:ctrl+shift+tab",
                "wait:1200",
                "focus:previous",
                "tabs:previous",
                "key:ctrl+1",
                "wait:1200",
                "focus:first",
                "tabs:first",
                "key:ctrl+2",
                "wait:1200",
                "focus:second",
                "tabs:second",
                "key:ctrl+t",
                "wait:1200",
                "focus:new",
                "tabs:new",
                "key:ctrl+2",
                "wait:1200",
                "focus:back",
                "tabs:back",
                "key:ctrl+w",
                "wait:1200",
                "focus:closed",
                "tabs:closed",
                "shot:done"));
            var logs = await run.FinishAsync("page-tabs", process, "done");

            var focus = Focus(logs);
            var tabs = logs.Where(l => Message(l) == "tabs shown").ToDictionary(l => Field(l, "label").GetString()!);
            // The right pane's row as the window shows it ("Documents | *readme.md (tool)"): how many tabs, which is in front, whether it is a tool.
            (int Count, int Front, bool Tool) Row(string label)
            {
                var parts = Field(tabs[label], "right").GetString()!.Split(" | ");
                var front = Array.FindIndex(parts, part => part.StartsWith('*'));
                return (parts.Length, front, front >= 0 && parts[front].EndsWith("(tool)"));
            }
            string Element(string label) => Field(focus[label], "element").GetString()!;
            string Context() => Evidence(logs);

            Assert.Equal((2, 1, true), Row("page"));
            Assert.Equal("WebView2", Element("page"));
            // Ctrl+Tab in the page: the folder tab of that pane comes to the front, and its list has the keyboard.
            Assert.True(Row("next") == (2, 0, false), "Ctrl+Tab in the page ran Next Tab" + Context());
            Assert.Equal("FilePane", Element("next"));
            // Ctrl+Shift+Tab from the list goes back; the preview is in front and its page has the keyboard.
            Assert.Equal((2, 1, true), Row("previous"));
            Assert.Equal("WebView2", Element("previous"));
            // Ctrl+1 in the page goes to the first tab, Ctrl+2 from the list to the second, the preview.
            Assert.True(Row("first") == (2, 0, false), "Ctrl+1 in the page ran Go to Tab" + Context());
            Assert.Equal("FilePane", Element("first"));
            Assert.Equal((2, 1, true), Row("second"));
            Assert.Equal("WebView2", Element("second"));
            // Ctrl+T in the page: a new folder tab beside the preview, in front, with the keyboard in its list.
            Assert.True(Row("new") is (3, _, false), "Ctrl+T in the page ran New Tab" + Context());
            Assert.Equal("FilePane", Element("new"));
            Assert.Equal((3, 1, true), Row("back"));
            Assert.Equal("WebView2", Element("back"));
            // Ctrl+W in the page closes the preview's tab; the keyboard goes to the list of the tab that is left.
            Assert.True(Row("closed") is (2, _, false), "Ctrl+W in the page ran Close Tab" + Context());
            Assert.Equal("FilePane", Element("closed"));
            var byKey = logs.Where(l => Message(l) == "command executed" && Field(l, "trigger").GetString() == "key")
                .Select(l => Field(l, "command").GetString()!).Where(command => command.StartsWith("tab.")).ToList();
            Assert.Equal(["tab.next", "tab.previous", "tab.select", "tab.select", "tab.new", "tab.select", "tab.close"], byKey);
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    [Fact]
    public async Task Opening_an_overlay_closes_the_others_and_Esc_closes_the_one_on_screen()
    {
        var (run, root, data) = Prepare("keys-one-overlay");
        try
        {
            // Ctrl+K Ctrl+T works in every box and over every overlay: from the palette, Quick Open and the drive list the
            // picker opens, and the overlay under it goes first, so the first Esc closes the picker and nothing is left open.
            // The plugin list opened over the palette does the same; the palette opened over the list too.
            var process = run.Start("one-overlay", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "cmd:palette.show",
                "wait:500",
                "focus:palette",
                "key:ctrl+k ctrl+t",
                "wait:800",
                "focus:palette-picker",
                "key:escape",
                "wait:600",
                "focus:palette-esc",
                "cmd:quickOpen.show",
                "wait:500",
                "focus:quick",
                "key:ctrl+k ctrl+t",
                "wait:800",
                "focus:quick-picker",
                "key:escape",
                "wait:600",
                "focus:quick-esc",
                "cmd-nowait:go.chooseDriveLeft",
                "wait:700",
                "focus:drives",
                "key:ctrl+k ctrl+t",
                "wait:800",
                "focus:drives-picker",
                "key:escape",
                "wait:600",
                "focus:drives-esc",
                "cmd:palette.show",
                "wait:500",
                "cmd:plugins.list",
                "wait:800",
                "focus:palette-plugins",
                "cmd:palette.show",
                "wait:500",
                "focus:plugins-palette",
                "key:escape",
                "wait:600",
                "focus:plugins-esc",
                "shot:done"));
            var logs = await run.FinishAsync("one-overlay", process, "done");

            var focus = Focus(logs);
            string Open(string label) => Field(focus[label], "overlays").GetString()!;
            string Context() => Evidence(logs);

            Assert.Equal("Palette", Open("palette"));
            Assert.True(Open("palette-picker") == "ThemePicker", "the picker opened over the palette and closed it" + Context());
            Assert.Equal("", Open("palette-esc"));
            Assert.Equal("FilePane", Field(focus["palette-esc"], "element").GetString());

            Assert.Equal("QuickOpen", Open("quick"));
            Assert.True(Open("quick-picker") == "ThemePicker", "the picker opened over Quick Open and closed it" + Context());
            Assert.Equal("", Open("quick-esc"));

            Assert.Equal("Prompt", Open("drives"));
            Assert.True(Open("drives-picker") == "ThemePicker", "the picker opened over the drive list and closed it" + Context());
            Assert.Equal("", Open("drives-esc"));

            Assert.True(Open("palette-plugins") == "PluginList", "the plugin list opened over the palette and closed it" + Context());
            Assert.True(Open("plugins-palette") == "Palette", "the palette opened over the plugin list and closed it" + Context());
            Assert.Equal("", Open("plugins-esc"));
            Assert.Equal("FilePane", Field(focus["plugins-esc"], "element").GetString());

            var closed = logs.Where(l => Message(l) == "overlays closed for another")
                .Select(l => (Field(l, "opening").GetString(), Field(l, "closed").GetString())).ToList();
            Assert.Contains(("ThemePicker", "Palette"), closed);
            Assert.Contains(("ThemePicker", "QuickOpen"), closed);
            Assert.Contains(("ThemePicker", "Prompt"), closed);
            Assert.Contains(("PluginList", "Palette"), closed);
            Assert.Contains(("Palette", "PluginList"), closed);
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    // The lines that say what the keys did, for the message of a failed assertion.
    private static string Evidence(List<string> logs) => "\n" + string.Join('\n', logs
        .Where(l => Message(l) is "key sent" or "key sent to a page" or "command executed" or "a key the page did not get" or "keyboard focus"
            or "tabs shown" or "overlays closed for another" or "command refused: a dialog is open")
        .Select(l => l.Length > 360 ? l[..360] : l));

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
