using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Tabs;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// A pane keeps the listing of the tab that went behind last (docs/ui.md,
/// "Tabs"; the speed review's proposal E): switching between two tabs lists
/// each folder once, and the kept listing is closed in the core when its time
/// is up. Opens a real window on a real core, so it runs only with
/// <c>CABINETOS_UI_E2E=1</c> and a built window (Debug) and core.
/// </summary>
public class KeptListingEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    [Fact]
    public async Task Switching_between_two_tabs_lists_each_folder_once_and_the_kept_listing_closes_when_its_time_is_up()
    {
        if (Environment.GetEnvironmentVariable(OptIn) != "1")
        {
            Assert.Skip($"Opens a window on the desktop: set {OptIn}=1 to run it.");
        }
        var exe = Path.Combine(Repo.Root, "ui", "CabinetOS", "bin", "x64", "Debug", "net10.0-windows10.0.22621.0", "win-x64", "CabinetOS.exe");
        var coreExe = CoreLauncher.Find(Path.Combine(Repo.Root, "ui"), Environment.GetEnvironmentVariable, File.Exists);
        if (!File.Exists(exe) || coreExe is null)
        {
            Assert.Skip("Build ui/CabinetOS.sln (Debug) and the core first.");
        }

        var root = Repo.NewTempFolder("kept-listing-e2e");
        Process? process = null;
        List<string> seen = [];
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(root, "data", "home")).FullName;
            var one = Directory.CreateDirectory(Path.Combine(root, "data", "one")).FullName;
            var two = Directory.CreateDirectory(Path.Combine(root, "data", "two")).FullName;
            for (var i = 0; i < 40; i++)
            {
                File.WriteAllText(Path.Combine(one, $"file-{i:00}.txt"), "1");
                File.WriteAllText(Path.Combine(two, $"other-{i:00}.txt"), "2");
            }
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"),
                new JsonObject { ["ui"] = new JsonObject { ["dualPane"] = true, ["lastPaths"] = new JsonArray(home, home) } }.ToJsonString());

            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            start.Environment["CABINETOS_CORE_EXE"] = coreExe;
            start.Environment["CABINETOS_CONFIG"] = Path.Combine(root, "config", "cabinetos.json");
            start.Environment["CABINETOS_LOG_DIR"] = Path.Combine(root, "logs");
            // The core's "listing closed" is a debug line.
            start.Environment["CABINETOS_LOG"] = "debug";
            foreach (var (name, folder) in new[] { ("THEMES", "themes"), ("UNDO", "undo"), ("PLUGINS", "plugins"), ("PLUGINS_DATA", "plugins-data"),
                ("MARKETPLACE", "marketplace"), ("WEBVIEW2", "webview2"), ("TOOLS", "tools"), ("UPDATE", "update") })
            {
                start.Environment[$"CABINETOS_{name}_DIR"] = Path.Combine(root, folder);
            }
            // Ten seconds instead of thirty, so the test sees the kept listing go. The time must be longer than the longest step between
            // two switches: with 3 s a step took 3.5 s beside three full test runs, the listing was let go before the tab came back, and
            // the tab listed its folder again (2 of 4 switches kept, 3 of 5 runs).
            start.Environment[ParkedListing.LifetimeEnv] = "10000";
            start.Environment["CABINETOS_UI_SNAPSHOT"] = Path.Combine(root, "shots");
            start.Environment["CABINETOS_UI_SNAPSHOT_STEPS"] = string.Join(';',
                "pane:0",
                $"path:{one}",
                "select:file-07.txt",
                // A second tab on the same folder lists it once more and keeps the first tab's listing; it then goes to two.
                "tab:new",
                $"path:{two}",
                "tabs:switching",
                // "listing shown" is logged at the first frame after the listing is bound, and a switch that comes before that frame
                // drops the line (beside three test runs a frame came every few hundred milliseconds: 0 of 4 lines), so each switch
                // waits for its frame.
                "tab:next",
                "until:listing-drawn",
                "shell:back-in-one",
                "tab:next",
                "until:listing-drawn",
                "tab:next",
                "until:listing-drawn",
                "tab:next",
                "until:listing-drawn",
                "tabs:switched",
                // The listing of one, kept since the last switch, goes when its 10 s are up: waited for until it is let go (a timer that
                // a busy machine runs late made a fixed wait of 1.5 s more than the lifetime too short).
                "until:kept-released",
                "tabs:expired",
                "tab:next",
                "until:listing-drawn",
                "shell:listed-again",
                "wait:500",
                "shot:done");
            process = Process.Start(start)!;
            await WaitForAsync(() => File.Exists(Path.Combine(root, "shots", "done.png")), "the window's last snapshot", TimeSpan.FromSeconds(150));
            process.CloseMainWindow();
            Assert.True(process.WaitForExit(60_000), "the window did not close");

            var ui = Lines(Path.Combine(root, "logs"), "ui.*.jsonl");
            var core = Lines(Path.Combine(root, "logs"), "core.*.jsonl");
            seen = ui;
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "logs"), "crash-*.json"));
            Assert.DoesNotContain(ui, l => Text(l, "level", top: true) == "ERROR");
            int At(string label) => ui.FindIndex(l => Message(l) == "tabs shown" && Text(l, "label") == label);
            var (switching, switched, expired) = (At("switching"), At("switched"), At("expired"));
            Assert.True(switching >= 0 && switching < switched && switched < expired, "the steps' marks are in the log in order");

            // Four switches between the two tabs: no listing asked of the core, each tab's kept listing taken back.
            var during = ui.Skip(switching).Take(switched - switching).ToList();
            Assert.DoesNotContain(during, l => Message(l) == "request sent" && Text(l, "request") == "list_directory");
            Assert.Equal([one, two, one, two], during.Where(l => Message(l) == "tab listing taken back").Select(l => Text(l, "path")));
            Assert.Equal(4, during.Count(l => Message(l) == "listing shown" && Field(l, "kept").GetBoolean()));
            // The first tab came back with its cursor where it was.
            var back = Assert.Single(ui, l => Message(l) == "shell state" && Text(l, "label") == "back-in-one");
            Assert.Equal((one, "file-07.txt"), (Text(back, "pane0_path"), Text(back, "pane0_cursor")));

            // The core listed two once, for the whole run; it listed one three times: for its tab, for the second
            // tab before that went to two, and after the kept listing went (below). Listing again at each switch made
            // that five and three.
            int Opened(string path) => core.Count(l => Message(l) == "listing opened" && Text(l, "path") == path);
            var relisted = ui.FindIndex(l => Message(l) == "shell state" && Text(l, "label") == "listed-again");
            Assert.Equal(1, Opened(two));

            // The kept listing of one went when its time was up, and the core closed it.
            var released = Assert.Single(ui.Skip(switched).Take(expired - switched), l => Message(l) == "tab listing released");
            Assert.Equal((one, "its time was up"), (Text(released, "path"), Text(released, "why")));
            var closed = core.Where(l => Message(l) == "listing closed").Select(l => Field(l, "listing_id").GetUInt64()).ToHashSet();
            Assert.Contains(Field(released, "listing_id").GetUInt64(), closed);

            // Back to one after that: its folder is listed again, as before listings were kept.
            Assert.True(relisted > expired);
            Assert.Contains(ui.Skip(expired).Take(relisted - expired), l => Message(l) == "request sent" && Text(l, "request") == "list_directory");
            Assert.Equal(3, Opened(one));
            var again = ui.Last(l => Message(l) == "listing shown" && Text(l, "path") == one);
            Assert.False(Field(again, "kept").GetBoolean());
        }
        catch (Xunit.Sdk.XunitException error)
        {
            // A window that never reached its last snapshot has no "seen" yet: its log is read now.
            var window = seen.Count > 0 ? seen : Directory.Exists(Path.Combine(root, "logs")) ? Lines(Path.Combine(root, "logs"), "ui.*.jsonl") : new List<string>();
            throw new Xunit.Sdk.XunitException($"{error.Message}\nthe window's last log lines (times in UTC):\n{WindowLog.Last(window, 90)}");
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
            Repo.RemoveTempFolder(root);
        }
    }

    // Every log file of the run, in the order the writer made them (a run that crosses midnight UTC has two).
    private static List<string> Lines(string folder, string pattern)
    {
        var lines = new List<string>();
        foreach (var path in Directory.GetFiles(folder, pattern).Order(StringComparer.Ordinal))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }
        }
        return lines;
    }

    private static string? Message(string line) => Text(line, "message", top: true);

    private static string? Text(string line, string name, bool top = false)
    {
        using var parsed = JsonDocument.Parse(line);
        var at = top ? parsed.RootElement : parsed.RootElement.TryGetProperty("fields", out var fields) ? fields : default;
        return at.ValueKind == JsonValueKind.Object && at.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;
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
