using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CabinetOS.Core.Ipc;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Phase 19c: the compact overlay on a real window and core (docs/ui.md, "Compact overlay"). It opens windows on
/// the desktop, so it runs only with <c>CABINETOS_UI_E2E=1</c> and a built window (Debug) and a core that knows
/// <c>ui.compactOverlay</c>. The snapshot steps toggle the mode (<c>cmd:view.toggleCompactOverlay</c>); the window's
/// log lines "compact overlay entered", "left" and "size saved" say what it did, and the test reads the window's
/// own style and rectangle from Windows while the script waits. It presses no keys, and every run ends with the
/// drawer left or the window closed: an always-on-top window must not stay on the desktop.
/// </summary>
public class CompactOverlayEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    /// <summary>
    /// Without a saved size the drawer is 480 by 640, topmost, and the file does not get the dual pane, the sidebar
    /// or the dock's size written while it is on; leaving puts the size, the place and the layout back and takes the
    /// topmost style off.
    /// </summary>
    [Fact]
    public async Task The_drawer_is_480_by_640_and_topmost_and_leaving_brings_the_window_back_with_nothing_written()
    {
        var (run, root, data) = Prepare("compact", """{ "version": 1, "ui": { "dualPane": true, "sidebar": true, "dockSize": { "bottom": 200, "right": null } } }""");
        try
        {
            var configPath = Path.Combine(root, "config", "cabinetos.json");
            var process = run.Start("first", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "cmd:view.toggleCompactOverlay",
                // Time for the test to read the window's style and the file while the drawer is on.
                "wait:4000",
                "shot:compact",
                "cmd:view.toggleCompactOverlay",
                "wait:3000",
                "shot:back"));
            var entered = await run.WaitForLineAsync("first", "compact overlay entered");
            var window = NativeWindow.Find(process.Id);
            Assert.NotEqual(0, window);
            Assert.True(NativeWindow.IsTopmost(window), "the drawer is topmost");
            var drawer = NativeWindow.Bounds(window);
            AssertNear(480, drawer.WidthDips, 1, "the drawer's width on screen");
            AssertNear(640, drawer.HeightDips, 1, "the drawer's height on screen");
            Assert.Equal(480, Field(entered, "width").GetInt32());
            Assert.Equal(640, Field(entered, "height").GetInt32());
            Assert.False(Field(entered, "saved").GetBoolean(), "nothing was saved: the default size");
            // The full window it came from, and the layout it keeps for the way back.
            var (previousWidth, previousHeight) = (Field(entered, "previous_width").GetInt32(), Field(entered, "previous_height").GetInt32());
            Assert.True(previousWidth >= 1200 && previousHeight >= 700, $"the window was {previousWidth} by {previousHeight} DIPs");
            Assert.True(Field(entered, "dual").GetBoolean());
            Assert.True(Field(entered, "sidebar").GetBoolean());
            Assert.False(Field(entered, "dock").GetBoolean());

            // The drawer writes none of the three: the file, read a second into the mode, is what it was.
            await Task.Delay(1000);
            AssertLayoutUntouched(configPath, "while the drawer is on");

            var left = await run.WaitForLineAsync("first", "compact overlay left");
            Assert.False(NativeWindow.IsTopmost(window), "the window is not topmost after the drawer");
            var back = NativeWindow.Bounds(window);
            AssertNear(previousWidth, back.WidthDips, 1, "the window's width after the drawer");
            AssertNear(previousHeight, back.HeightDips, 1, "the window's height after the drawer");
            Assert.Equal(previousWidth, Field(left, "width").GetInt32());
            Assert.Equal(previousHeight, Field(left, "height").GetInt32());
            Assert.True(Field(left, "dual").GetBoolean());
            Assert.True(Field(left, "sidebar").GetBoolean());
            Assert.False(Field(left, "dock").GetBoolean());
            // The place: the top-left corner is where it was, on screen and in the log.
            Assert.Equal((Field(entered, "previous_left").GetInt32(), Field(entered, "previous_top").GetInt32()), (back.X, back.Y));
            Assert.Equal((back.X, back.Y), (Field(left, "left").GetInt32(), Field(left, "top").GetInt32()));

            var logs = await run.FinishAsync("first", process, "back");
            AssertLayoutUntouched(configPath, "after the drawer");
            Assert.Single(logs, l => Message(l) == "compact overlay entered");
            Assert.Single(logs, l => Message(l) == "compact overlay left");
            // Nothing in the window resized the drawer, so nothing was saved.
            Assert.DoesNotContain(logs, l => Message(l) == "compact overlay size saved");
            Assert.Null(CompactOverlayInFile(configPath));
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// A size in <c>ui.compactOverlay</c> is the drawer's size at entry (saved: true), and a layout that was single
    /// pane without a sidebar comes back as it was, not as the defaults. A command that needs the second pane
    /// (Open in Other Pane) is refused with a notice and does not write <c>ui.dualPane</c>.
    /// </summary>
    [Fact]
    public async Task A_saved_size_opens_the_drawer_and_a_layout_without_dual_pane_or_sidebar_comes_back_as_it_was()
    {
        var (run, root, data) = Prepare("compact-saved", """{ "version": 1, "ui": { "dualPane": false, "sidebar": false, "compactOverlay": { "width": 400, "height": 500 } } }""");
        try
        {
            var configPath = Path.Combine(root, "config", "cabinetos.json");
            var process = run.Start("saved", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "select:a folder",
                "wait:500",
                "cmd:view.toggleCompactOverlay",
                "wait:1500",
                "cmd:file.openInOtherPane",
                "wait:1500",
                "shot:compact",
                "cmd:view.toggleCompactOverlay",
                "wait:2000",
                "shot:back"));
            var entered = await run.WaitForLineAsync("saved", "compact overlay entered");
            var window = NativeWindow.Find(process.Id);
            Assert.NotEqual(0, window);
            Assert.True(NativeWindow.IsTopmost(window));
            var drawer = NativeWindow.Bounds(window);
            AssertNear(400, drawer.WidthDips, 1, "the saved width on screen");
            AssertNear(500, drawer.HeightDips, 1, "the saved height on screen");
            Assert.Equal(400, Field(entered, "width").GetInt32());
            Assert.Equal(500, Field(entered, "height").GetInt32());
            Assert.True(Field(entered, "saved").GetBoolean());
            Assert.False(Field(entered, "dual").GetBoolean());
            Assert.False(Field(entered, "sidebar").GetBoolean());

            var left = await run.WaitForLineAsync("saved", "compact overlay left");
            Assert.False(NativeWindow.IsTopmost(window));
            Assert.False(Field(left, "dual").GetBoolean(), "a single pane stays single");
            Assert.False(Field(left, "sidebar").GetBoolean(), "a hidden sidebar stays hidden");
            Assert.Equal(Field(entered, "previous_width").GetInt32(), Field(left, "width").GetInt32());
            Assert.Equal(Field(entered, "previous_height").GetInt32(), Field(left, "height").GetInt32());

            var logs = await run.FinishAsync("saved", process, "back");
            // Open in Other Pane would have shown the second pane and saved that; in the drawer it says why not.
            Assert.Contains(logs, l => Message(l) == "notice shown" && Field(l, "text").GetString()!.StartsWith("The compact overlay has no second pane", StringComparison.Ordinal));
            var ui = JsonNode.Parse(ReadFile(configPath))!["ui"]!;
            Assert.False(ui["dualPane"]!.GetValue<bool>());
            Assert.False(ui["sidebar"]!.GetValue<bool>());
            Assert.Equal(400, ui["compactOverlay"]!["width"]!.GetValue<int>());
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// A terminal's dock is hidden by the drawer and back as it was, with the same shell; the commands that would bring
    /// the dock, the second pane or the sidebar back are refused with a notice each, and write nothing.
    /// </summary>
    [Fact]
    public async Task The_dock_comes_back_with_its_terminal_and_the_commands_that_would_undo_the_drawer_are_refused()
    {
        var (run, root, data) = Prepare("compact-dock", """{ "version": 1, "ui": { "dualPane": true, "sidebar": true, "dockSize": { "bottom": 200, "right": null } } }""");
        try
        {
            var configPath = Path.Combine(root, "config", "cabinetos.json");
            var process = run.Start("dock", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "cmd:view.toggleTerminal",
                "wait:3000",
                "cmd:view.toggleCompactOverlay",
                "wait:1000",
                "cmd:view.toggleTerminal",
                "cmd:view.toggleDualPane",
                "cmd:view.toggleSidebar",
                "wait:2500",
                "shot:compact",
                "cmd:view.toggleCompactOverlay",
                "wait:2500",
                "shot:back"));
            var entered = await run.WaitForLineAsync("dock", "compact overlay entered");
            Assert.True(Field(entered, "dock").GetBoolean(), "the terminal's dock was shown when the drawer began");
            var window = NativeWindow.Find(process.Id);
            Assert.NotEqual(0, window);
            Assert.True(NativeWindow.IsTopmost(window));
            await Task.Delay(1500);
            AssertLayoutUntouched(configPath, "while the drawer is on");
            var left = await run.WaitForLineAsync("dock", "compact overlay left");
            Assert.False(NativeWindow.IsTopmost(window));
            Assert.True(Field(left, "dock").GetBoolean(), "the dock is shown again");
            Assert.True(Field(left, "dual").GetBoolean());
            Assert.True(Field(left, "sidebar").GetBoolean());

            var logs = await run.FinishAsync("dock", process, "back");
            AssertLayoutUntouched(configPath, "after the drawer");
            foreach (var what in new[] { "terminal", "dual pane", "sidebar" })
            {
                Assert.Contains(logs, l => Message(l) == "notice shown" && Field(l, "text").GetString()!.StartsWith($"The compact overlay has no {what}: ", StringComparison.Ordinal));
            }
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// Resizing the drawer saves the size in <c>ui.compactOverlay</c> half a second after the last resize (the test
    /// resizes the window as a user's drag would: through Windows), and the next entry opens at it. An edit of the
    /// file while the drawer is on resizes it at once, and the window does not save that size again.
    /// </summary>
    [Fact]
    public async Task Resizing_the_drawer_saves_its_size_the_next_entry_opens_at_it_and_an_edit_of_the_file_resizes_it()
    {
        var (run, root, data) = Prepare("compact-resize", """{ "version": 1, "ui": { "dualPane": true } }""");
        try
        {
            var configPath = Path.Combine(root, "config", "cabinetos.json");
            var process = run.Start("resize", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "cmd:view.toggleCompactOverlay",
                // The test resizes the drawer in this time, and reads the file after the save.
                "wait:7000",
                "cmd:view.toggleCompactOverlay",
                "wait:1500",
                "cmd:view.toggleCompactOverlay",
                // The test edits the file in this time, as an editor would.
                "wait:7000",
                "cmd:view.toggleCompactOverlay",
                "wait:1000",
                "shot:back"));
            await run.WaitForLineAsync("resize", "compact overlay entered");
            var window = NativeWindow.Find(process.Id);
            Assert.NotEqual(0, window);
            var first = NativeWindow.Bounds(window);
            AssertNear(480, first.WidthDips, 1, "the first entry's width");
            // A drag in steps, as the pointer makes it: only the last size is saved.
            foreach (var step in new[] { 500, 510, 520 })
            {
                NativeWindow.Resize(window, (int)Math.Round(step * first.Scale), (int)Math.Round((step + 180) * first.Scale));
                await Task.Delay(120);
            }
            var saved = await run.WaitForLineAsync("resize", "compact overlay size saved");
            Assert.Equal(520, Field(saved, "width").GetInt32());
            Assert.Equal(700, Field(saved, "height").GetInt32());
            Assert.Equal(1, run.Count("resize", "compact overlay size saved"));
            var inFile = CompactOverlayInFile(configPath);
            Assert.NotNull(inFile);
            Assert.Equal((520, 700), (inFile!["width"]!.GetValue<int>(), inFile["height"]!.GetValue<int>()));

            // Leaving, then the second entry: the saved size, from the file.
            var second = await run.WaitForLineAsync("resize", "compact overlay entered", occurrence: 2);
            Assert.Equal(520, Field(second, "width").GetInt32());
            Assert.Equal(700, Field(second, "height").GetInt32());
            Assert.True(Field(second, "saved").GetBoolean());
            AssertNear(520, NativeWindow.Bounds(window).WidthDips, 1, "the second entry's width on screen");

            // A hand edit while the drawer is on: the window takes the size from the file at once.
            var file = JsonNode.Parse(ReadFile(configPath))!;
            file["ui"]!["compactOverlay"] = new JsonObject { ["width"] = 440, ["height"] = 560 };
            File.WriteAllText(configPath, file.ToJsonString());
            var followed = await run.WaitForLineAsync("resize", "compact overlay follows the configuration");
            Assert.Equal((440, 560), (Field(followed, "width").GetInt32(), Field(followed, "height").GetInt32()));
            await Task.Delay(300);
            var edited = NativeWindow.Bounds(window);
            AssertNear(440, edited.WidthDips, 1, "the width after the edit of the file");
            AssertNear(560, edited.HeightDips, 1, "the height after the edit of the file");

            var logs = await run.FinishAsync("resize", process, "back");
            Assert.Equal(2, logs.Count(l => Message(l) == "compact overlay left"));
            // Entering again and following the file are not resizes by the user: nothing more was saved.
            Assert.Single(logs, l => Message(l) == "compact overlay size saved");
            Assert.False(NativeWindow.IsTopmost(window));
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    private static void AssertNear(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"{what}: expected {expected} within {tolerance}, was {actual}");

    // The three layout settings as the test wrote them, and as the file says now.
    private static void AssertLayoutUntouched(string configPath, string when)
    {
        var ui = JsonNode.Parse(ReadFile(configPath))!["ui"]!;
        Assert.True(ui["dualPane"]!.GetValue<bool>(), $"ui.dualPane {when}");
        Assert.True(ui["sidebar"]!.GetValue<bool>(), $"ui.sidebar {when}");
        Assert.Equal(200, ui["dockSize"]!["bottom"]!.GetValue<int>());
        Assert.Null(ui["dockSize"]!["right"]);
    }

    private static JsonObject? CompactOverlayInFile(string configPath) => JsonNode.Parse(ReadFile(configPath))?["ui"]?["compactOverlay"] as JsonObject;

    // The core replaces the file in one step, so a read either sees the old file or the new one; a read that meets the
    // swap is tried again.
    private static string ReadFile(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(100);
            }
        }
    }

    // The window from Windows' side: its topmost style and its rectangle. The test thread is per-monitor DPI aware while
    // it asks, so the numbers are the window's pixels whatever the test host is.
    private static class NativeWindow
    {
        private const int ExStyle = -20;
        private const long Topmost = 0x00000008;
        private const uint NoMove = 0x0002;
        private const uint NoZOrder = 0x0004;
        private const uint NoActivate = 0x0010;
        private static readonly nint PerMonitorAwareV2 = -4;

        public readonly record struct Place(int X, int Y, int Width, int Height, double Scale)
        {
            public double WidthDips => Width / Scale;

            public double HeightDips => Height / Scale;
        }

        // The visible top-level window of the process that is titled as the window is ("CabinetOS").
        public static nint Find(int processId)
        {
            nint found = 0;
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var owner);
                if (owner == processId && IsWindowVisible(hwnd) && Title(hwnd) == "CabinetOS")
                {
                    found = hwnd;
                    return false;
                }
                return true;
            }, 0);
            return found;
        }

        public static bool IsTopmost(nint hwnd) => (GetWindowLongPtr(hwnd, ExStyle) & Topmost) != 0;

        public static Place Bounds(nint hwnd)
        {
            var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
            try
            {
                Assert.True(GetWindowRect(hwnd, out var rect), "GetWindowRect");
                return new Place(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, GetDpiForWindow(hwnd) / 96.0);
            }
            finally
            {
                SetThreadDpiAwarenessContext(previous);
            }
        }

        // The window's outer size in pixels, its place unchanged: what the drag of a frame does.
        public static void Resize(nint hwnd, int width, int height)
        {
            var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
            try
            {
                Assert.True(SetWindowPos(hwnd, 0, 0, 0, width, height, NoMove | NoZOrder | NoActivate), "SetWindowPos");
            }
            finally
            {
                SetThreadDpiAwarenessContext(previous);
            }
        }

        private static string Title(nint hwnd)
        {
            var text = new StringBuilder(256);
            GetWindowText(hwnd, text, text.Capacity);
            return text.ToString();
        }

        private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(nint hwnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern nint GetWindowLongPtr(nint hwnd, int index);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(nint hwnd, out Rect rect);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(nint hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern nint SetThreadDpiAwarenessContext(nint context);
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

        // The window's log so far; empty before the window has written one.
        private List<string> Log(string name)
        {
            var folder = Path.Combine(root, "logs-" + name);
            return Directory.Exists(folder) && Directory.GetFiles(folder, "ui.*.jsonl") is [var file, ..] ? Lines(file) : [];
        }

        public int Count(string name, string message) => Log(name).Count(l => Message(l) == message);

        // Waits for the window to log the message (the nth time) and returns that line.
        public async Task<string> WaitForLineAsync(string name, string message, int occurrence = 1)
        {
            string? line = null;
            await WaitForAsync(() => (line = Log(name).Where(l => Message(l) == message).Skip(occurrence - 1).FirstOrDefault()) is not null,
                $"the {name} window to log \"{message}\" ({occurrence})", TimeSpan.FromSeconds(90));
            return line!;
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

    // A folder with a window's setting up: the configuration the test names, and a data folder of three files.
    private static (Run Run, string Root, string Data) Prepare(string purpose, string config)
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
        File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), config);
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
            await Task.Delay(100);
        }
    }
}
