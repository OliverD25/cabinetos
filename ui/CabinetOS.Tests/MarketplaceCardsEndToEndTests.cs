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

    // Every third item a theme, the others tools: 40 and 80.
    private static readonly string[] Ids = [.. Enumerable.Range(0, 120).Select(i => $"item-{i:000}")];

    private static bool IsTheme(int i) => i % 3 == 0;

    // The card's accessible name, as the view gives it: "Item 11, Tool, by CabinetOS".
    private static string CardName(int i) => $"Item {i}, {(IsTheme(i) ? "Theme" : "Tool")}, by CabinetOS";

    [Fact]
    public async Task A_120_item_index_shows_a_screenful_at_once_and_all_120_in_index_order_after_the_slices()
    {
        var logs = await RunAsync("all", string.Join(';',
            "size:1400x900",
            "cmd:marketplace.browse",
            "market:opened",
            "wait:2000",
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

        var shown = Assert.Single(logs, l => Message(l) == "marketplace cards shown");
        var first = Field(shown, "cards").GetInt32();
        Assert.InRange(first, 4, 119);
        Assert.Equal(120, Field(shown, "total").GetInt32());

        // Right after the command: the first screenful only, the rest still to come.
        var opened = Cards(logs, "opened");
        Assert.Equal((first, 120, false), (Field(opened, "cards").GetInt32(), Field(opened, "total").GetInt32(), Field(opened, "complete").GetBoolean()));
        Assert.Equal(Ids[..first], Text(opened, "ids").Split(','));

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
        var logs = await RunAsync("themes", string.Join(';',
            "size:1400x900",
            "cmd:marketplace.browse",
            "market:opened",
            // The same dispatcher turn as the opening's first cards: Discover's slices are still to come.
            "click:Themes",
            "market:themes",
            "wait:2000",
            "market:themes-done",
            "shot:done"));

        var opened = Cards(logs, "opened");
        Assert.False(Field(opened, "complete").GetBoolean());
        Assert.InRange(Field(opened, "cards").GetInt32(), 4, 119);

        var themes = Cards(logs, "themes");
        Assert.Equal(40, Field(themes, "total").GetInt32());
        var done = Cards(logs, "themes-done");
        Assert.Equal((40, 40, true), (Field(done, "cards").GetInt32(), Field(done, "total").GetInt32(), Field(done, "complete").GetBoolean()));
        Assert.Equal(Ids.Where((_, i) => IsTheme(i)), Text(done, "ids").Split(','));
        // Discover's slices stopped when the tab changed: its 120 cards were never all made.
        Assert.DoesNotContain(logs, l => Message(l) == "marketplace cards complete" && Field(l, "cards").GetInt32() == 120);
        Assert.Contains(logs, l => Message(l) == "marketplace cards complete" && Field(l, "cards").GetInt32() == 40);
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
            ["kind"] = IsTheme(i) ? "theme" : "tool",
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
