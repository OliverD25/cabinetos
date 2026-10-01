using System.Diagnostics;
using System.Text.Json;
using CabinetOS.Core.Ipc;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Phase 12: the tabs of a pane survive a restart of the window (docs/ui.md,
/// "Tabs"). Opens a real window twice on one configuration, so it runs only
/// with <c>CABINETOS_UI_E2E=1</c> and a built window and core.
/// </summary>
public class TabsEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    [Fact]
    public async Task Tabs_and_locks_of_both_panes_are_found_again_when_the_window_starts_a_second_time()
    {
        if (Environment.GetEnvironmentVariable(OptIn) != "1")
        {
            Assert.Skip($"Opens a window on the desktop twice: set {OptIn}=1 to run it.");
        }
        var exe = Path.Combine(Repo.Root, "ui", "CabinetOS", "bin", "x64", "Debug", "net10.0-windows10.0.22621.0", "win-x64", "CabinetOS.exe");
        var coreExe = CoreLauncher.Find(Path.Combine(Repo.Root, "ui"), Environment.GetEnvironmentVariable, File.Exists);
        if (!File.Exists(exe) || coreExe is null)
        {
            Assert.Skip("Build ui/CabinetOS.sln (Debug) and the core first.");
        }

        var root = Repo.NewTempFolder("tabs-e2e");
        var started = new List<Process>();
        try
        {
            var folders = new[] { "one", "two", "three" }.Select(name => Directory.CreateDirectory(Path.Combine(root, "data", name)).FullName).ToArray();
            Directory.CreateDirectory(Path.Combine(root, "config"));
            var configPath = Path.Combine(root, "config", "cabinetos.json");

            Process Start(string run, string steps)
            {
                var start = new ProcessStartInfo(exe) { UseShellExecute = false };
                start.Environment["CABINETOS_CORE_EXE"] = coreExe;
                start.Environment["CABINETOS_CONFIG"] = configPath;
                start.Environment["CABINETOS_LOG_DIR"] = Path.Combine(root, "logs-" + run);
                start.Environment["CABINETOS_THEMES_DIR"] = Path.Combine(root, "themes");
                start.Environment["CABINETOS_UNDO_DIR"] = Path.Combine(root, "undo");
                start.Environment["CABINETOS_PLUGINS_DIR"] = Path.Combine(root, "plugins");
                start.Environment["CABINETOS_PLUGINS_DATA_DIR"] = Path.Combine(root, "plugins-data");
                start.Environment["CABINETOS_MARKETPLACE_DIR"] = Path.Combine(root, "marketplace");
                start.Environment["CABINETOS_WEBVIEW2_DIR"] = Path.Combine(root, "webview2");
                start.Environment["CABINETOS_UI_SNAPSHOT"] = Path.Combine(root, "shots-" + run);
                start.Environment["CABINETOS_UI_SNAPSHOT_STEPS"] = steps;
                var process = Process.Start(start)!;
                started.Add(process);
                return process;
            }
            string Shot(string run, string name) => Path.Combine(root, "shots-" + run, name + ".png");

            // First run: the left pane opens three folders in three tabs, the last one in front and locked; the right
            // pane opens two; then the window closes.
            var first = Start("first", string.Join(';',
                "pane:0",
                $"path:{folders[0]}",
                "tab:new",
                $"path:{folders[1]}",
                "tab:new",
                $"path:{folders[2]}",
                "tab:lock",
                "pane:1",
                $"path:{folders[0]}",
                "tab:new",
                $"path:{folders[1]}",
                "pane:0",
                "tabs:opened",
                "wait:1500",
                "shot:opened"));
            await WaitForAsync(() => File.Exists(Shot("first", "opened")), "the first window's last snapshot", TimeSpan.FromSeconds(60));
            first.CloseMainWindow();
            Assert.True(first.WaitForExit(15_000), "the first window did not close");

            using (var config = JsonDocument.Parse(File.ReadAllText(configPath)))
            {
                var tabs = config.RootElement.GetProperty("ui").GetProperty("tabs");
                var left = tabs.GetProperty("left");
                Assert.Equal(folders, left.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("path").GetString()).ToArray());
                Assert.Equal([false, false, true], left.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("locked").GetBoolean()).ToArray());
                Assert.Equal(2, left.GetProperty("active").GetInt32());
                var right = tabs.GetProperty("right");
                Assert.Equal(folders[..2], right.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("path").GetString()).ToArray());
                Assert.Equal(1, right.GetProperty("active").GetInt32());
            }

            // Second run: the same configuration; the row shows the three tabs again, the last in front, without a step to open them.
            var second = Start("second", "pane:0;wait:500;tabs:restored;wait:1000;shot:restored");
            await WaitForAsync(() => File.Exists(Shot("second", "restored")), "the second window's snapshot", TimeSpan.FromSeconds(60));
            second.CloseMainWindow();
            Assert.True(second.WaitForExit(15_000), "the second window did not close");

            var logs = LogFiles.Ui(Path.Combine(root, "logs-second"));
            var shown = Assert.Single(logs, l => Message(l) == "tabs shown" && Field(l, "label").GetString() == "restored");
            // The row as the window shows it: the tab in front marked with *, the lock in brackets.
            Assert.Equal("one | two | *three (locked)", Field(shown, "left").GetString());
            Assert.Equal("one | *two", Field(shown, "right").GetString());
            // How v2 draws them: a folder before each name (the lock in its place on a locked tab), a divider between the
            // tabs behind but none next to the tab in front, the × on the tab in front of a pane with more than one tab,
            // and that tab a 32 px card on the default look's 36 px strip.
            Assert.Equal("folder,divider | folder | *lock,close,32", Field(shown, "left_look").GetString());
            Assert.Equal("folder | *folder,close,32", Field(shown, "right_look").GetString());
            Assert.DoesNotContain(logs, l => Message(l) is "a tab could not show its folder");
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "logs-second"), "crash-*.json"));
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
