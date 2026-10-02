using System.Diagnostics;
using System.Text.Json;
using CabinetOS.Core.Ipc;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Phase 19f (ADR 0016; docs/ui.md, "The column view"): a pane's tab shows
/// its folder as columns on a real window and core. Three levels open with
/// Enter and with clicks, Left goes back up the columns, a row opened again
/// drops the columns right of it and releases their listings in the window
/// and in the core, the list comes back with the deepest folder, and the mode
/// is saved with the tab, so a second window starts in columns. The window's
/// snapshot steps drive it (<c>column-view:</c>, <c>column-open:</c>,
/// <c>column-key:</c>, <c>column-click:</c>), so it runs only with
/// <c>CABINETOS_UI_E2E=1</c> and a built window and core.
/// </summary>
public class ColumnViewEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    [Fact]
    public async Task Three_levels_open_close_and_come_back_as_columns_and_each_dropped_column_releases_its_listing()
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

        var root = Repo.NewTempFolder("column-view-e2e");
        var started = new List<Process>();
        // The first window's log, for the message of a failed check.
        List<string> seen = [];
        try
        {
            // data\a\b\c, a file in each folder; folders sort first, so a folder's first row is its subfolder.
            var data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
            var a = Directory.CreateDirectory(Path.Combine(data, "a")).FullName;
            var b = Directory.CreateDirectory(Path.Combine(a, "b")).FullName;
            var c = Directory.CreateDirectory(Path.Combine(b, "c")).FullName;
            foreach (var (folder, name) in new[] { (data, "top.txt"), (a, "a.txt"), (b, "b.txt"), (c, "c.txt") })
            {
                File.WriteAllText(Path.Combine(folder, name), name);
            }
            Directory.CreateDirectory(Path.Combine(root, "config"));
            var configPath = Path.Combine(root, "config", "cabinetos.json");

            Process Start(string run, string steps)
            {
                var start = new ProcessStartInfo(exe) { UseShellExecute = false };
                start.Environment["CABINETOS_CORE_EXE"] = coreExe;
                start.Environment["CABINETOS_CONFIG"] = configPath;
                start.Environment["CABINETOS_LOG_DIR"] = Path.Combine(root, "logs-" + run);
                // The core logs a closed listing at the debug level: the test reads the watch of each dropped column end there.
                start.Environment["CABINETOS_LOG"] = "debug";
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

            var first = Start("first", string.Join(';',
                "pane:0",
                $"path:{data}",
                "cmd:view.toggleColumns",
                "column-view:one",
                "column-open:a",
                "column-open:b",
                "column-view:three",
                "shell:three",
                "wait:1500",
                "shot:columns-three",
                "column-key:left",
                "column-key:left",
                "column-view:left",
                // Opened again from the first column: the two columns right of it go, and a comes back as a new one.
                "column-open:a",
                "column-view:two",
                "column-open:b",
                "column-view:reopened",
                "shell:reopened",
                // The mouse's way: a click on a row of the first column, then on a row of the keyboard's column.
                "column-click:1|a",
                "column-view:clicked-two",
                "column-click:2|b",
                "column-view:clicked-three",
                "cmd:view.toggleColumns",
                "column-view:back",
                "shell:back",
                "cmd:view.toggleColumns",
                "column-view:again",
                "until:tabs-saved",
                "tabs:saved",
                "shot:done"));
            await WaitForAsync(() => File.Exists(Shot("first", "done")), "the first window's last snapshot", TimeSpan.FromSeconds(240));
            first.CloseMainWindow();
            Assert.True(first.WaitForExit(60_000), "the first window did not close");

            var logs = LogFiles.Ui(Path.Combine(root, "logs-first"));
            seen = logs;
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "logs-first"), "crash-*.json"));
            Assert.DoesNotContain(logs, l => Level(l) == "ERROR");
            string Shown(string label, string field) => Text(Assert.Single(logs, l => Message(l) == "column view shown" && Text(l, "label") == label), field);

            Assert.Contains(logs, l => Message(l) == "column view entered" && Text(l, "path") == data);
            Assert.Equal("1", Shown("one", "depth"));
            Assert.Equal(data, Shown("one", "folders"));

            // Three columns: data, a and b; the keyboard in the third, which lists c and the file; three listings.
            Assert.Equal("3", Shown("three", "depth"));
            Assert.Equal(string.Join('|', data, a, b), Shown("three", "folders"));
            Assert.Equal("3", Shown("three", "keyboard"));
            Assert.Equal("c>|b.txt", Shown("three", "rows"));
            Assert.Equal("3", Shown("three", "listings"));
            Assert.Equal(b, Shown("three", "path"));
            Assert.Equal(b, Shown("three", "tab_path"));
            Assert.Equal("True", Shown("three", "keyboard_in_view"));
            // Each column shows its rows on screen, names only, folders with their chevron.
            Assert.Equal("a>|top.txt / b>|a.txt / c>|b.txt", Shown("three", "shown_rows"));
            var three = Assert.Single(logs, l => Message(l) == "shell state" && Text(l, "label") == "three");
            Assert.Equal(3, Field(three, "pane0_listings").GetInt32());
            Assert.Equal(3, Field(three, "pane0_columns").GetInt32());
            Assert.EndsWith("b", Field(three, "pane0_crumbs").GetString());

            // Two Lefts: the keyboard in the first column, the columns and the tab's path unchanged.
            Assert.Equal("1", Shown("left", "keyboard"));
            Assert.Equal("3", Shown("left", "depth"));
            Assert.Equal(b, Shown("left", "tab_path"));
            Assert.Equal("a|b|c", Shown("left", "cursors"));
            Assert.Equal("a>|top.txt", Shown("left", "rows"));

            // a opened again from the first column: b's column and the old a's go, deepest first, and their listings with them.
            var left = logs.FindIndex(l => Message(l) == "column view shown" && Text(l, "label") == "left");
            var two = logs.FindIndex(l => Message(l) == "column view shown" && Text(l, "label") == "two");
            var released = logs.Skip(left).Take(two - left).Where(l => Message(l) == "column released").ToList();
            Assert.Equal(["3:" + b, "2:" + a], released.Select(l => $"{Text(l, "depth")}:{Text(l, "path")}"));
            Assert.Equal("2", Shown("two", "depth"));
            Assert.Equal("2", Shown("two", "listings"));
            Assert.Equal(a, Shown("two", "tab_path"));
            Assert.Equal("3", Shown("reopened", "depth"));
            Assert.Equal("3", Shown("reopened", "listings"));
            Assert.Equal(3, Field(Assert.Single(logs, l => Message(l) == "shell state" && Text(l, "label") == "reopened"), "pane0_listings").GetInt32());

            // The clicks: two levels, then three again, the listings never more than the columns.
            Assert.Equal(("2", "2"), (Shown("clicked-two", "depth"), Shown("clicked-two", "listings")));
            Assert.Equal(("3", "3"), (Shown("clicked-three", "depth"), Shown("clicked-three", "listings")));
            Assert.Equal(6, logs.Count(l => Message(l) == "column opened"));

            // Back to the list: the deepest folder, one listing; the other two columns released.
            Assert.Contains(logs, l => Message(l) == "column view left" && Text(l, "path") == b);
            Assert.Equal("files", Shown("back", "mode"));
            Assert.Equal(b, Shown("back", "path"));
            Assert.Equal("c>|b.txt", Shown("back", "rows"));
            Assert.Equal("1", Shown("back", "listings"));
            Assert.Equal(1, Field(Assert.Single(logs, l => Message(l) == "shell state" && Text(l, "label") == "back"), "pane0_listings").GetInt32());
            // Every release in order: the reopened a (twice, by key and by click), then leaving the columns.
            Assert.Equal(["3:" + b, "2:" + a, "3:" + b, "2:" + a, "2:" + a, "1:" + data],
                logs.Where(l => Message(l) == "column released").Select(l => $"{Text(l, "depth")}:{Text(l, "path")}"));
            // Columns again, from the list's folder: one column.
            Assert.Equal(("columns", "1", b), (Shown("again", "mode"), Shown("again", "depth"), Shown("again", "folders")));
            Assert.Contains(logs, l => Message(l) == "tabs saved" && Field(l, "columns").GetInt32() == 1);

            // The core stopped watching every folder whose column went: each released listing is closed there.
            var core = LogFiles.Core(Path.Combine(root, "logs-first"));
            var closed = core.Where(l => Message(l) == "listing closed").Select(l => Field(l, "listing_id").GetUInt64()).ToHashSet();
            var releasedIds = logs.Where(l => Message(l) == "column released").Select(l => Field(l, "listing_id").GetUInt64()).ToList();
            Assert.NotEmpty(releasedIds);
            Assert.All(releasedIds, id => Assert.Contains(id, closed));

            using (var config = JsonDocument.Parse(File.ReadAllText(configPath)))
            {
                var item = Assert.Single(config.RootElement.GetProperty("ui").GetProperty("tabs").GetProperty("left").GetProperty("items").EnumerateArray());
                Assert.Equal(b, item.GetProperty("path").GetString());
                Assert.Equal("columns", item.GetProperty("mode").GetString());
            }

            // A second window on the same configuration: the tab starts in columns, its folder as the one column.
            var second = Start("second", "pane:0;column-view:start;shell:start;wait:500;shot:start");
            await WaitForAsync(() => File.Exists(Shot("second", "start")), "the second window's snapshot", TimeSpan.FromSeconds(180));
            second.CloseMainWindow();
            Assert.True(second.WaitForExit(60_000), "the second window did not close");
            var again = LogFiles.Ui(Path.Combine(root, "logs-second"));
            Assert.Contains(again, l => Message(l) == "column view entered" && Text(l, "path") == b);
            var start = Assert.Single(again, l => Message(l) == "column view shown" && Text(l, "label") == "start");
            Assert.Equal(("columns", "1", b, "c>|b.txt"), (Text(start, "mode"), Text(start, "depth"), Text(start, "folders"), Text(start, "rows")));
            Assert.Equal(1, Field(Assert.Single(again, l => Message(l) == "shell state" && Text(l, "label") == "start"), "pane0_listings").GetInt32());
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "logs-second"), "crash-*.json"));
        }
        catch (Xunit.Sdk.XunitException error)
        {
            // A window that never reached its last snapshot has no "seen" yet: its log is read now.
            var window = seen.Count > 0 ? seen : LogFiles.Ui(Path.Combine(root, "logs-first"));
            throw new Xunit.Sdk.XunitException($"{error.Message}\nthe first window's last log lines (times in UTC):\n{WindowLog.Last(window, 90, fieldChars: 420)}");
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
        return parsed.RootElement.TryGetProperty("message", out var message) ? message.GetString() : null;
    }

    private static string? Level(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.TryGetProperty("level", out var level) ? level.GetString() : null;
    }

    private static JsonElement Field(string line, string name)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.GetProperty("fields").GetProperty(name).Clone();
    }

    // A field as text, whatever its JSON type: numbers and booleans as .NET writes them.
    private static string Text(string line, string name)
    {
        using var parsed = JsonDocument.Parse(line);
        if (!parsed.RootElement.TryGetProperty("fields", out var fields) || !fields.TryGetProperty(name, out var value))
        {
            return "";
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => value.GetRawText(),
        };
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
