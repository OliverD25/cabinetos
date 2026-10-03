using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CabinetOS.Core.Ipc;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// The marketplace makes its cards in parts (docs/ui.md, "The marketplace";
/// the speed review's proposal C): the cards that fill the view at once, the
/// rest in slices, in the index's order, which is the keyboard's; a tab chosen
/// while slices are still to come leaves only its own cards. Opens a real
/// window on a real core with a local index of 120 items, so it runs only
/// with <c>CABINETOS_UI_E2E=1</c> and a built window (Debug) and core.
/// </summary>
public class MarketplaceCardsEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    // Every third item a plugin, the others tools: 40 and 80 (the Extensions page has no themes since Phase 23).
    private static readonly string[] Ids = [.. Enumerable.Range(0, 120).Select(i => $"item-{i:000}")];

    private static bool IsPlugin(int i) => i % 3 == 0;

    // The card's accessible name, as the view gives it: "Item 11, Tool, by CabinetOS".
    private static string CardName(int i) => $"Item {i}, {(IsPlugin(i) ? "WASM plugin" : "Tool")}, by CabinetOS";

    [Fact]
    public async Task A_120_item_index_shows_a_screenful_at_once_and_all_120_in_index_order_after_the_slices()
    {
        var logs = await RunAsync("all", string.Join(';',
            "size:1400x900",
            "cmd:marketplace.browse",
            // The slices come one low-priority dispatcher turn each, which a busy machine runs late: wait for the last one's line.
            "until:market-complete",
            "market:complete",
            // Tab from a card goes to the next item's card, across the slices' borders too.
            $"click:{CardName(11)}",
            "key:tab",
            "focus:after-11",
            $"click:{CardName(47)}",
            "key:tab",
            "focus:after-47",
            $"click:{CardName(118)}",
            "key:tab",
            "focus:after-118",
            "shot:done"));

        // At once: the cards that fill the view, a part of the 120.
        var shown = Assert.Single(logs, l => Message(l) == "marketplace cards shown");
        Assert.InRange(Field(shown, "cards").GetInt32(), 4, 119);
        Assert.Equal(120, Field(shown, "total").GetInt32());

        // After the slices: every card, in the index's order, which is the order Tab follows.
        var complete = Cards(logs, "complete");
        Assert.Equal((120, true), (Field(complete, "cards").GetInt32(), Field(complete, "complete").GetBoolean()));
        Assert.Equal(Ids, Text(complete, "ids").Split(','));
        var done = Assert.Single(logs, l => Message(l) == "marketplace cards complete");
        Assert.Equal(120, Field(done, "cards").GetInt32());
        Assert.True(Field(done, "slices").GetInt32() > 1, "the cards came in more than one slice");

        foreach (var from in new[] { 11, 47, 118 })
        {
            var focus = Assert.Single(logs, l => Message(l) == "keyboard focus" && Text(l, "label") == $"after-{from}");
            Assert.Equal(CardName(from + 1), Text(focus, "name"));
        }
    }

    [Fact]
    public async Task A_tab_chosen_while_the_slices_are_made_leaves_only_its_cards()
    {
        var logs = await RunAsync("plugins", string.Join(';',
            "size:1400x900",
            "cmd:marketplace.browse",
            "wait:2000",
            "click:Plugins",
            "wait:2000",
            // Discover again, and Plugins in the same dispatcher turn: steps without a wait run one after the other,
            // and a slice waits for a turn of its own, so Discover's slices are still to come when Plugins is clicked.
            "click:Discover",
            "market:discover",
            "click:Plugins",
            "market:plugins",
            "until:market-complete",
            "market:plugins-done",
            "shot:done"));

        var discover = Cards(logs, "discover");
        Assert.Equal((120, false), (Field(discover, "total").GetInt32(), Field(discover, "complete").GetBoolean()));
        Assert.InRange(Field(discover, "cards").GetInt32(), 4, 119);
        Assert.Equal(40, Field(Cards(logs, "plugins"), "total").GetInt32());

        var done = Cards(logs, "plugins-done");
        Assert.Equal((40, 40, true), (Field(done, "cards").GetInt32(), Field(done, "total").GetInt32(), Field(done, "complete").GetBoolean()));
        Assert.Equal(Ids.Where((_, i) => IsPlugin(i)), Text(done, "ids").Split(','));
        // Discover's second set stopped when the tab changed: after it began, no set of 120 cards was completed.
        var began = logs.FindIndex(l => Message(l) == "marketplace cards" && Text(l, "label") == "discover");
        Assert.DoesNotContain(logs.Skip(began), l => Message(l) == "marketplace cards complete" && Field(l, "cards").GetInt32() == 120);
        Assert.Contains(logs.Skip(began), l => Message(l) == "marketplace cards complete" && Field(l, "cards").GetInt32() == 40);
    }

    [Fact]
    public async Task The_view_is_prepared_after_start_before_any_marketplace_command_and_the_first_opening_logs_its_cards()
    {
        // The window prepares the view while it is idle after start, after the menu shapes; nothing here is input.
        var logs = await RunAsync("prepared", string.Join(';',
            "size:1400x900",
            // The idle slot after the menu shapes (low priority, and only once no key or pointer came for a moment) is late on a busy machine.
            "until:market-prepared",
            "cmd:marketplace.browse",
            "until:market-complete",
            "market:complete",
            "shot:done"));

        var prepared = Assert.Single(logs, l => Message(l) == "marketplace view prepared");
        Assert.True(Field(prepared, "ms").GetDouble() > 0);
        var at = logs.IndexOf(prepared);
        // After the first folders were on screen and after the menu shapes, before the marketplace's command.
        Assert.InRange(logs.FindIndex(l => Message(l) == "listing shown"), 0, at - 1);
        Assert.InRange(logs.FindLastIndex(l => Message(l) == "context menu prepared"), 0, at - 1);
        var command = logs.FindIndex(l => Message(l) == "command executed" && Text(l, "command") == "marketplace.browse");
        Assert.True(at < command, "the view was prepared before the marketplace's command");
        // Preparing reads no index: the core is asked only at the opening.
        Assert.DoesNotContain(logs.Take(command), l => Message(l) == "request sent" && Text(l, "request") == "marketplace_refresh");

        // The first opening afterwards makes its cards as before: a screenful at once, then all 120 in the index's order.
        var shown = Assert.Single(logs, l => Message(l) == "marketplace cards shown");
        Assert.True(logs.IndexOf(shown) > command);
        Assert.InRange(Field(shown, "cards").GetInt32(), 4, 119);
        Assert.Equal(120, Field(shown, "total").GetInt32());
        Assert.Equal(120, Field(Assert.Single(logs, l => Message(l) == "marketplace cards complete"), "cards").GetInt32());
        var complete = Cards(logs, "complete");
        Assert.Equal(Ids, Text(complete, "ids").Split(','));
    }

    // A window on a scratch configuration whose marketplace.index is a local index of the 120 items; returns its log lines.
    private static async Task<List<string>> RunAsync(string purpose, string steps)
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
        var root = Repo.NewTempFolder("market-cards-" + purpose);
        Process? process = null;
        try
        {
            var data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
            var index = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "index")).FullName, "index.json");
            File.WriteAllText(index, Index().ToJsonString());
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), new JsonObject
            {
                ["ui"] = new JsonObject { ["dualPane"] = true, ["lastPaths"] = new JsonArray(data, data) },
                ["marketplace"] = new JsonObject { ["index"] = index },
            }.ToJsonString());

            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            start.Environment["CABINETOS_CORE_EXE"] = coreExe;
            start.Environment["CABINETOS_CONFIG"] = Path.Combine(root, "config", "cabinetos.json");
            start.Environment["CABINETOS_LOG_DIR"] = Path.Combine(root, "logs");
            foreach (var (name, folder) in new[] { ("THEMES", "themes"), ("UNDO", "undo"), ("PLUGINS", "plugins"), ("PLUGINS_DATA", "plugins-data"),
                ("MARKETPLACE", "marketplace"), ("WEBVIEW2", "webview2"), ("TOOLS", "tools"), ("UPDATE", "update") })
            {
                start.Environment[$"CABINETOS_{name}_DIR"] = Path.Combine(root, folder);
            }
            start.Environment["CABINETOS_UI_SNAPSHOT"] = Path.Combine(root, "shots");
            start.Environment["CABINETOS_UI_SNAPSHOT_STEPS"] = steps;
            process = Process.Start(start)!;
            await WaitForAsync(() => File.Exists(Path.Combine(root, "shots", "done.png")), "the window's last snapshot", TimeSpan.FromSeconds(90));
            process.CloseMainWindow();
            Assert.True(process.WaitForExit(20_000), "the window did not close");

            var logs = Lines(Path.Combine(root, "logs"), "ui.*.jsonl");
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "logs"), "crash-*.json"));
            Assert.DoesNotContain(logs, l => Text(l, "level", top: true) == "ERROR");
            var read = Assert.Single(logs, l => Message(l) == "marketplace index read");
            Assert.Equal("120", Text(read, "items"));
            return logs;
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

    // The index format of sdk/marketplace/index.schema.json; nothing is installed, so the downloads need not exist.
    private static JsonObject Index() => new()
    {
        ["schemaVersion"] = 1,
        ["generatedAt"] = "2026-10-01T00:00:00Z",
        ["items"] = new JsonArray([.. Enumerable.Range(0, 120).Select(i => (JsonNode)new JsonObject
        {
            ["id"] = Ids[i],
            ["kind"] = IsPlugin(i) ? "plugin" : "tool",
            ["name"] = $"Item {i}",
            ["author"] = new JsonObject { ["name"] = "CabinetOS" },
            ["version"] = "1.0.0",
            ["description"] = $"Item {i} of the index, for the test of the cards made in parts.",
            ["size"] = 1000,
            ["download"] = new JsonObject { ["url"] = $"files/{Ids[i]}-1.0.0.zip", ["sha256"] = new string('a', 64) },
            ["manifest"] = new JsonObject { ["id"] = Ids[i] },
            ["minCoreVersion"] = "0.1.0",
            ["license"] = "MIT",
        })]),
    };

    private static string Cards(List<string> logs, string label) =>
        Assert.Single(logs, l => Message(l) == "marketplace cards" && Text(l, "label") == label);

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

    private static string Text(string line, string name, bool top = false)
    {
        using var parsed = JsonDocument.Parse(line);
        var at = top ? parsed.RootElement : parsed.RootElement.TryGetProperty("fields", out var fields) ? fields : default;
        return at.ValueKind == JsonValueKind.Object && at.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()
            : "";
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
