using System.Diagnostics;
using System.Text.Json;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Platform;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Edge cases, class D: two windows (docs/ui.md, "Two windows"). Each window
/// is a process with a core of its own; both share the configuration, the
/// log folder and WebView2's data folders.
/// </summary>
public class TwoWindowsTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    [Fact]
    public void A_new_window_starts_at_the_active_folder_with_this_windows_tools()
    {
        var mine = WindowArgs.Parse(["--tools-dir", @"E:\repo\sdk\tools"]);

        var args = mine.ForNewWindow(@"C:\Users\me\Звіт folder");

        Assert.Equal(["--path", @"C:\Users\me\Звіт folder", "--tools-dir", @"E:\repo\sdk\tools"], args);
        Assert.Equal(new WindowArgs(@"C:\Users\me\Звіт folder", @"E:\repo\sdk\tools", false), WindowArgs.Parse(args));
        // The crash self-test is not passed on, and no folder when the pane has none.
        Assert.Equal(new WindowArgs(null, null, true), WindowArgs.Parse(["--self-test-crash"]));
        Assert.Empty(WindowArgs.Parse(["--self-test-crash"]).ForNewWindow(null));
        Assert.Empty(WindowArgs.Parse([]).ForNewWindow(""));
        // A flag without its value is left out.
        Assert.Equal(new WindowArgs(null, null, false), WindowArgs.Parse(["--path"]));
    }

    /// <summary>
    /// Two real windows, B first, then A, on one configuration and one log
    /// folder, each opening the terminal (one WebView2 data folder). A turns
    /// dual pane off and closes; B follows the setting, keeps its core and its
    /// terminal, and still runs a command after A is gone. Opens two windows on
    /// the desktop for about half a minute, so it runs only with
    /// <c>CABINETOS_UI_E2E=1</c> and a built window and core.
    /// </summary>
    [Fact]
    public async Task Two_windows_keep_their_own_cores_follow_each_others_settings_and_outlive_each_other()
    {
        if (Environment.GetEnvironmentVariable(OptIn) != "1")
        {
            Assert.Skip($"Opens two windows on the desktop: set {OptIn}=1 to run it.");
        }
        var exe = Path.Combine(Repo.Root, "ui", "CabinetOS", "bin", "x64", "Debug", "net10.0-windows10.0.22621.0", "win-x64", "CabinetOS.exe");
        var coreExe = CoreLauncher.Find(Path.Combine(Repo.Root, "ui"), Environment.GetEnvironmentVariable, File.Exists);
        if (!File.Exists(exe) || coreExe is null)
        {
            Assert.Skip("Build ui/CabinetOS.sln (Debug) and the core first.");
        }

        var root = Repo.NewTempFolder("two-windows");
        var started = new List<Process>();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            Process Start(string name, string steps)
            {
                var start = new ProcessStartInfo(exe) { UseShellExecute = false };
                start.Environment["CABINETOS_CORE_EXE"] = coreExe;
                start.Environment["CABINETOS_CONFIG"] = Path.Combine(root, "config", "cabinetos.json");
                start.Environment["CABINETOS_LOG_DIR"] = Path.Combine(root, "logs");
                start.Environment["CABINETOS_THEMES_DIR"] = Path.Combine(root, "themes");
                start.Environment["CABINETOS_UNDO_DIR"] = Path.Combine(root, "undo");
                start.Environment["CABINETOS_PLUGINS_DIR"] = Path.Combine(root, "plugins");
                start.Environment["CABINETOS_PLUGINS_DATA_DIR"] = Path.Combine(root, "plugins-data");
                start.Environment["CABINETOS_MARKETPLACE_DIR"] = Path.Combine(root, "marketplace");
                start.Environment["CABINETOS_WEBVIEW2_DIR"] = Path.Combine(root, "webview2");
                start.Environment["CABINETOS_UI_SNAPSHOT"] = Path.Combine(root, name);
                start.Environment["CABINETOS_UI_SNAPSHOT_STEPS"] = steps;
                var process = Process.Start(start)!;
                started.Add(process);
                return process;
            }
            string Shot(string window, string name) => Path.Combine(root, window, name + ".png");

            // B waits long enough for A to start, change the setting and close.
            var b = Start("b", "cmd:view.toggleTerminal;until:terminal;wait:1000;shot:b-terminal;wait:30000;shot:b-single;cmd:go.up;wait:1500;shot:b-after");
            await WaitForAsync(() => File.Exists(Shot("b", "b-terminal")), "B's terminal", TimeSpan.FromSeconds(60));
            var a = Start("a", "cmd:view.toggleTerminal;until:terminal;wait:1000;shot:a-terminal;cmd:view.toggleDualPane;wait:4000;shot:a-done");
            await WaitForAsync(() => File.Exists(Shot("a", "a-done")), "A's last snapshot", TimeSpan.FromSeconds(60));

            var logPath = Directory.GetFiles(Path.Combine(root, "logs"), "ui.*.jsonl").Single();
            var cores = Lines(logPath).Where(l => Message(l) == "core started").Select(l => Field(l, "pid").GetInt32()).ToList();
            Assert.Equal(2, cores.Distinct().Count());
            var (coreOfB, coreOfA) = (cores[0], cores[1]);
            Assert.True(IsRunning(coreOfA) && IsRunning(coreOfB));

            a.CloseMainWindow();
            Assert.True(a.WaitForExit(15_000), "A did not close");
            await WaitForAsync(() => !IsRunning(coreOfA), "A's core to end with A", TimeSpan.FromSeconds(10));
            Assert.False(b.HasExited);
            Assert.True(IsRunning(coreOfB));

            await WaitForAsync(() => File.Exists(Shot("b", "b-after")), "B's snapshots after A closed", TimeSpan.FromSeconds(60));
            Assert.True(IsRunning(coreOfB));
            b.CloseMainWindow();
            Assert.True(b.WaitForExit(15_000), "B did not close");

            // One log file for both windows, every line whole.
            var lines = Lines(logPath);
            Assert.All(lines, line => JsonDocument.Parse(line).Dispose());
            // A wrote the setting; B followed it and wrote nothing back.
            var followed = lines.Where(l => Message(l) == "dual pane follows the configuration").ToList();
            Assert.False(Field(Assert.Single(followed), "dual").GetBoolean());
            using (var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config", "cabinetos.json"))))
            {
                Assert.False(config.RootElement.GetProperty("ui").GetProperty("dualPane").GetBoolean());
            }
            // Both terminals started from the one WebView2 data folder; none stopped, A's closing included.
            Assert.Equal(2, lines.Count(l => Message(l) == "WebView2 started" && Field(l, "host").GetString() == "terminal"));
            Assert.DoesNotContain(lines, l => Message(l) is "a WebView2 process failed" or "WebView2 could not start");
            // B ran a command after A had gone.
            var closed = lines.FindIndex(l => Message(l) == "window closing");
            var up = lines.FindIndex(l => Message(l) == "command executed" && Field(l, "command").GetString() == "go.up");
            Assert.InRange(closed, 0, up - 1);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "logs"), "crash-*.json"));
        }
        finally
        {
            foreach (var process in started.Where(p => !p.HasExited))
            {
                process.Kill(entireProcessTree: true);
            }
            Repo.RemoveTempFolder(root);
        }
    }

    private static List<string> Lines(string path)
    {
        // The windows may still hold the file: read it as they share it.
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

    private static JsonElement Field(string line, string name)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.GetProperty("fields").GetProperty(name).Clone();
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
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
