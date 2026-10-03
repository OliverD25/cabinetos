using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CabinetOS.Tests.Support;
using static CabinetOS.Tests.Support.WindowRun;

namespace CabinetOS.Tests;

/// <summary>
/// Quick View on a real window and core (ADR 0023, items W3 to W8), with the fixture viewer of
/// <c>sdk/fixtures/tools/quickview-fixture</c>: a <c>.qvtest</c> file's first line says what its page does. The steps
/// <c>quickview:&lt;label&gt;</c> log what the panel shows; <c>until:quickview-shown</c>, <c>-thumbnail</c>, <c>-idle</c>,
/// <c>-closed</c>, <c>-state:&lt;state&gt;</c> and <c>-viewer:&lt;id&gt;</c> wait for it; <c>crash:quickview:&lt;id&gt;</c> ends
/// a viewer's browser process. They run only with <c>CABINETOS_UI_E2E=1</c> and a built window (Debug) and core.
/// </summary>
public class QuickViewEndToEndTests
{
    private const string Fixture = "quickview-fixture";

    private static string FixtureSource => Path.Combine(Repo.Root, "sdk", "fixtures", "tools", Fixture);

    // A run whose panes start in <root>\data, with a local catalogue (no web read), and the fixture viewer in the tools
    // folder in development unless the test installs it itself.
    private static WindowRun Prepare(string purpose, Action<string>? makeData, bool fixtureInDevelopment = true, Func<string, JsonArray>? catalogue = null)
    {
        return WindowRun.Prepare(purpose, root =>
        {
            var data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
            makeData?.Invoke(data);
            if (fixtureInDevelopment)
            {
                CopyFixture(Path.Combine(root, "tools"), Fixture, null);
            }
            var index = Directory.CreateDirectory(Path.Combine(root, "index")).FullName;
            File.WriteAllText(Path.Combine(index, "index.json"), new JsonObject
            {
                ["schemaVersion"] = 1,
                ["generatedAt"] = "2026-10-03T00:00:00Z",
                ["items"] = catalogue?.Invoke(index) ?? [],
            }.ToJsonString());
            return new JsonObject
            {
                ["version"] = 1,
                ["ui"] = new JsonObject { ["dualPane"] = true, ["lastPaths"] = new JsonArray(data, data) },
                ["marketplace"] = new JsonObject { ["index"] = index, ["themes"] = index },
            };
        });
    }

    // The fixture viewer's folder under <tools>\<id>; another ID gets its own name, so two viewers claim the same kinds.
    private static void CopyFixture(string tools, string id, string? name)
    {
        var folder = Directory.CreateDirectory(Path.Combine(tools, id)).FullName;
        foreach (var file in Directory.GetFiles(FixtureSource))
        {
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
        }
        if (id != Fixture)
        {
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "tool.json")))!.AsObject();
            manifest["id"] = id;
            manifest["name"] = name ?? id;
            File.WriteAllText(Path.Combine(folder, "tool.json"), manifest.ToJsonString());
        }
    }

    private static void Test(string folder, string name, string firstLine) => File.WriteAllText(Path.Combine(folder, name), firstLine + "\n");

    private static string Steps(params string[] steps) => string.Join(';', steps);

    private static List<string> Shown(IReadOnlyList<string> logs) => logs.Where(l => Message(l) == "quick view shown").ToList();

    private static string Q(IReadOnlyList<string> logs, string label, string field) => Text(State(logs, "quick view state", label), field);

    private static double N(IReadOnlyList<string> logs, string label, string field) =>
        double.Parse(Q(logs, label, field), System.Globalization.CultureInfo.InvariantCulture);

    // The window's lines about Quick View, its pages and the snapshot steps: what a failed check shows.
    private static string QuickViewLines(IReadOnlyList<string> logs) => WindowLog.Last(logs, 80, l =>
        Target(l) is "cabinetos_ui::quickview" or "cabinetos_ui::snapshot" or "cabinetos_ui::webview" || Message(l) == "command executed");

    /// <summary>
    /// Space opens the panel (the card, then the fixture's page), the keyboard stays in the list, Space and Esc close it;
    /// the panel is drawn in dark and in light; its size is the ADR's share of the window; the line has the times.
    /// </summary>
    [Fact]
    public async Task Space_opens_the_panel_on_the_cursor_file_and_Space_and_Esc_close_it()
    {
        var run = Prepare("quickview-open", data =>
        {
            Test(data, "a.qvtest", "shown");
            Test(data, "b.qvtest", "shown-keys left right k plus space");
        });
        try
        {
            var process = run.Start("open", Steps(
                "size:1400x900",
                "pane:0",
                "until:listing-drawn",
                "select:a.qvtest",
                "key:space",
                "until:quickview-shown",
                "quickview:open-a",
                "shot:quickview-dark",
                "mode:light",
                "wait:600",
                "quickview:light",
                "shot:quickview-light",
                "mode:dark",
                "wait:300",
                "key:space",
                "until:quickview-closed",
                "quickview:closed-by-space",
                "select:b.qvtest",
                "key:space",
                "until:quickview-shown",
                "wait:300",
                "quickview:keys",
                "key:right",
                "key:k",
                "wait:300",
                "key:escape",
                "until:quickview-closed",
                "quickview:closed-by-escape",
                "shot:done"));
            var logs = await run.FinishAsync("open", process);

            Assert.Equal(("shown", "a.qvtest", Fixture, "page"), (Q(logs, "open-a", "state"), Q(logs, "open-a", "file"), Q(logs, "open-a", "viewer"), Q(logs, "open-a", "picture")));
            Assert.Equal(("true", "QuickView"), (Q(logs, "open-a", "focus_in_list"), Q(logs, "open-a", "overlays")));
            // 72 % of the area's width and 80 % of its height (the area is the window's content, 1400 wide).
            Assert.InRange(N(logs, "open-a", "area_width"), 1390, 1410);
            Assert.InRange(N(logs, "open-a", "width") / N(logs, "open-a", "area_width"), 0.715, 0.725);
            Assert.InRange(N(logs, "open-a", "height") / N(logs, "open-a", "area_height"), 0.795, 0.805);
            Assert.Equal("shown", Q(logs, "light", "state"));
            Assert.True(File.Exists(Path.Combine(run.Root, "shots-open", "quickview-dark.png")));
            Assert.True(File.Exists(Path.Combine(run.Root, "shots-open", "quickview-light.png")));
            Assert.Equal("closed", Q(logs, "closed-by-space", "state"));
            Assert.Equal("closed", Q(logs, "closed-by-escape", "state"));

            // The page asked for left, right, k, plus and space: space is never granted, the others are free in filesView.
            Assert.Equal("left,right,k,plus", Q(logs, "keys", "keys"));
            var pressed = logs.Where(l => Message(l) == "a key went to the viewer").Select(l => Text(l, "key")).ToList();
            Assert.Equal(["right", "k"], pressed);

            var first = Shown(logs)[0];
            Assert.Equal((".qvtest", Fixture, "true"), (Text(first, "kind"), Text(first, "viewer"), Text(first, "cold")));
            Assert.True(long.Parse(Text(first, "card_ms")) <= long.Parse(Text(first, "full_ms")), WindowLog.Last(logs));
            Assert.Equal("false", Text(Shown(logs)[1], "cold"));
        }
        finally
        {
            run.Stop();
        }
    }

    /// <summary>
    /// Down and Up walk the folder under the panel and it follows the cursor, one token per file; Shift+Space marks in
    /// place under it; Tab closes it; the folder card shows the folder's size; Enter closes the panel and opens the row.
    /// </summary>
    [Fact]
    public async Task The_arrows_walk_the_folder_under_the_panel_and_Tab_and_Enter_close_it()
    {
        var run = Prepare("quickview-walk", data =>
        {
            foreach (var name in new[] { "a", "b", "c", "d", "e" })
            {
                Test(data, $"{name}.qvtest", "shown");
            }
            var sub = Directory.CreateDirectory(Path.Combine(data, "sub")).FullName;
            File.WriteAllBytes(Path.Combine(sub, "one.bin"), new byte[3000]);
            File.WriteAllBytes(Path.Combine(sub, "two.bin"), new byte[5000]);
        });
        try
        {
            var data = Path.Combine(run.Root, "data");
            var process = run.Start("walk", Steps(
                "size:1400x900",
                "pane:0",
                "until:listing-drawn",
                "select:a.qvtest",
                "key:space",
                "until:quickview-shown",
                "key:down",
                "until:quickview-shown",
                "key:down",
                "until:quickview-shown",
                "key:down",
                "until:quickview-shown",
                "key:down",
                "until:quickview-shown",
                "quickview:at-e",
                "key:up",
                "until:quickview-shown",
                "quickview:back-at-d",
                "key:shift+space",
                "wait:400",
                "quickview:marked",
                "key:tab",
                "until:quickview-closed",
                "quickview:after-tab",
                "pane:0",
                "select:sub",
                "key:space",
                "until:quickview-idle",
                "wait:1500",
                "quickview:folder",
                "key:enter",
                "until:quickview-closed",
                $"until:pane-at:{Path.Combine(data, "sub")}",
                "quickview:after-enter",
                "shot:done"));
            var logs = await run.FinishAsync("walk", process);

            var shown = Shown(logs).Where(l => Text(l, "full_ms").Length > 0).Select(l => long.Parse(Text(l, "token"))).ToList();
            Assert.True(shown.Count >= 6, WindowLog.Last(logs));
            Assert.Equal(shown.Count, shown.Distinct().Count());
            // The folder comes first in the listing: e.qvtest is the sixth row of six.
            Assert.Equal(("e.qvtest", "6 of 6"), (Q(logs, "at-e", "file"), Q(logs, "at-e", "position")));
            Assert.Equal(("d.qvtest", "5 of 6"), (Q(logs, "back-at-d", "file"), Q(logs, "back-at-d", "position")));
            // Shift+Space ran marking in place under the panel, which stayed open on the same file.
            Assert.Equal(("shown", "d.qvtest"), (Q(logs, "marked", "state"), Q(logs, "marked", "file")));
            Assert.Contains(logs, l => Message(l) == "command executed" && Text(l, "command") == "edit.toggleSelectionInPlace" && Text(l, "trigger") == "key");
            Assert.Equal("closed", Q(logs, "after-tab", "state"));
            // The folder card: the shell's folder thumbnail when it has one, else the icon card; no viewer; the size measured.
            Assert.Contains(Q(logs, "folder", "state"), new[] { "card", "thumbnail" });
            Assert.Equal("", Q(logs, "folder", "viewer"));
            Assert.Contains("2 files", Q(logs, "folder", "facts"));
            Assert.Equal("closed", Q(logs, "after-enter", "state"));
            Assert.Equal(Path.Combine(data, "sub"), Q(logs, "after-enter", "pane_path"));
        }
        finally
        {
            run.Stop();
        }
    }

    /// <summary>A PNG shows the shell's thumbnail before the viewer's page; a .xyz file no viewer claims shows the card, offers nothing from an empty catalogue, and leaves no request open.</summary>
    [Fact]
    public async Task A_PNG_shows_its_thumbnail_before_the_viewer_and_an_unclaimed_file_shows_the_card()
    {
        var run = Prepare("quickview-thumbnail", data =>
        {
            TestPng.Write(Path.Combine(data, "photo.png"), 1600, 1200);
            File.WriteAllText(Path.Combine(data, "notes.xyz"), "no viewer claims this");
        });
        try
        {
            var process = run.Start("thumbnail", Steps(
                "size:1400x900",
                "pane:0",
                "until:listing-drawn",
                "select:photo.png",
                "key:space",
                "until:quickview-thumbnail",
                "until:quickview-shown",
                "quickview:png",
                "key:space",
                "until:quickview-closed",
                "select:notes.xyz",
                "key:space",
                "until:quickview-idle",
                "wait:300",
                "quickview:xyz",
                "shot:done"));
            var logs = await run.FinishAsync("thumbnail", process);

            var png = Shown(logs).First(l => Text(l, "kind") == ".png");
            Assert.True(long.Parse(Text(png, "thumbnail_ms")) <= long.Parse(Text(png, "full_ms")), WindowLog.Last(logs));
            Assert.Equal(("shown", "1600 × 1200"), (Q(logs, "png", "state"), Q(logs, "png", "facts").Split(" · ").Last()));

            Assert.Equal(("offer", "card", "", "0"), (Q(logs, "xyz", "state"), Q(logs, "xyz", "picture"), Q(logs, "xyz", "viewer"), Q(logs, "xyz", "asking")));
            Assert.Equal("No viewer for .xyz files.", Q(logs, "xyz", "offer"));
            Assert.Equal("", Q(logs, "xyz", "button"));
            Assert.StartsWith("notes.xyz|", Q(logs, "xyz", "card"));
            var xyz = Shown(logs).First(l => Text(l, "kind") == ".xyz");
            Assert.Equal(("none", "none"), (Text(xyz, "viewer"), Text(xyz, "full")));
        }
        finally
        {
            run.Stop();
        }
    }

    /// <summary>
    /// The failure paths, with the waits shortened (CABINETOS_QUICKVIEW_LIMITS): a page that fails keeps the card and says
    /// why; one that hangs says "Still loading…" and then "did not finish"; Esc closes the panel while a page hangs; a
    /// crashed page says it stopped, and the next file works.
    /// </summary>
    [Fact]
    public async Task A_failing_a_hanging_and_a_crashed_page_never_hold_up_the_panel()
    {
        var run = Prepare("quickview-failures", data =>
        {
            Test(data, "a-ok.qvtest", "shown");
            Test(data, "b-fail.qvtest", "failed");
            Test(data, "c-hang.qvtest", "hang");
            Test(data, "d-ok.qvtest", "shown");
            Test(data, "e-ok.qvtest", "shown");
        });
        try
        {
            var process = run.Start("failures", Steps(
                "size:1400x900",
                "pane:0",
                "until:listing-drawn",
                "select:b-fail.qvtest",
                "key:space",
                "until:quickview-state:failed",
                "quickview:failed",
                "key:down",
                "wait:2600",
                "quickview:still-loading",
                "key:escape",
                "until:quickview-closed",
                "quickview:escape-while-hanging",
                "key:space",
                "until:quickview-state:failed",
                "quickview:hang-failed",
                "key:down",
                "until:quickview-shown",
                "crash:quickview:quickview-fixture",
                "until:quickview-state:stopped",
                "quickview:crashed",
                "focus:after-crash",
                "key:down",
                "until:quickview-shown",
                "quickview:after-crash",
                "shot:done"), new Dictionary<string, string> { ["CABINETOS_QUICKVIEW_LIMITS"] = "120,3000,1500,4000" });
            var logs = await run.FinishAsync("failures", process);

            Assert.Equal(("failed", "card"), (Q(logs, "failed", "state"), Q(logs, "failed", "picture")));
            Assert.Equal("Quick View Fixture cannot show this file. The fixture was told to fail.", Q(logs, "failed", "line"));
            Assert.Equal(("loading", "Still loading…", "c-hang.qvtest"), (Q(logs, "still-loading", "state"), Q(logs, "still-loading", "line"), Q(logs, "still-loading", "file")));
            Assert.Equal("closed", Q(logs, "escape-while-hanging", "state"));
            Assert.Equal("Quick View Fixture did not finish.", Q(logs, "hang-failed", "line"));
            Assert.True(Q(logs, "crashed", "state") == "stopped" && Q(logs, "crashed", "line") == "Quick View Fixture stopped.", QuickViewLines(logs));
            Assert.Equal(("shown", "e-ok.qvtest"), (Q(logs, "after-crash", "state"), Q(logs, "after-crash", "file")));
            Assert.Contains(Shown(logs), l => Text(l, "full") == "failed:unsupported");
            Assert.Contains(Shown(logs), l => Text(l, "full") == "failed:not-finished");
        }
        finally
        {
            run.Stop();
        }
    }

    /// <summary>
    /// A local catalogue holds the fixture viewer: Space on its kind shows the offer, its one button installs it through
    /// the marketplace's own install, and the viewer shows the file without a restart (ADR 0023, W7).
    /// </summary>
    [Fact]
    public async Task The_offer_installs_the_viewer_and_the_file_shows_in_it_without_a_restart()
    {
        var run = Prepare("quickview-offer", data => Test(data, "a.qvtest", "shown"), fixtureInDevelopment: false, catalogue: index => [FixtureItem(index)]);
        try
        {
            var process = run.Start("offer", Steps(
                "size:1400x900",
                "pane:0",
                "until:listing-drawn",
                "select:a.qvtest",
                "key:space",
                "until:quickview-idle",
                "quickview:offer",
                "click:Install Quick View Fixture",
                "until:quickview-viewer:quickview-fixture",
                "until:quickview-shown",
                "quickview:installed",
                "shot:done"), new Dictionary<string, string> { ["CABINETOS_CORE_TOOLS_DIR"] = Path.Combine(run.Root, "installed-tools") });
            var logs = await run.FinishAsync("offer", process);

            Assert.Equal(("offer", "No viewer for .qvtest files is installed.", "Install Quick View Fixture"),
                (Q(logs, "offer", "state"), Q(logs, "offer", "offer"), Q(logs, "offer", "button")));
            Assert.Equal(("shown", Fixture, ""), (Q(logs, "installed", "state"), Q(logs, "installed", "viewer"), Q(logs, "installed", "offer")));
            Assert.True(File.Exists(Path.Combine(run.Root, "installed-tools", Fixture, "tool.json")));
        }
        finally
        {
            run.Stop();
        }
    }

    /// <summary>
    /// Two viewers claim PNG files: the panel's viewer button chooses the second (quickView.viewers is written and the next
    /// Space uses it), "No viewer" leaves the thumbnail only, and the palette's prompt chooses again (ADR 0023, W8).
    /// </summary>
    [Fact]
    public async Task The_viewer_button_and_the_palette_choose_the_viewer_and_write_the_setting()
    {
        var run = Prepare("quickview-choice", data => TestPng.Write(Path.Combine(data, "photo.png"), 800, 600));
        CopyFixture(Path.Combine(run.Root, "tools"), "quickview-fixture-two", "Quick View Fixture Two");
        try
        {
            var process = run.Start("choice", Steps(
                "size:1400x900",
                "pane:0",
                "until:listing-drawn",
                "select:photo.png",
                "key:space",
                "until:quickview-shown",
                "quickview:first",
                "click:Viewer",
                "click:Quick View Fixture Two",
                "until:config",
                "until:quickview-shown",
                "quickview:second",
                "key:space",
                "until:quickview-closed",
                "key:space",
                "until:quickview-shown",
                "quickview:reopened",
                "click:Viewer",
                "click:No viewer (thumbnail only)",
                "until:config",
                "until:quickview-thumbnail",
                "until:quickview-idle",
                "quickview:none",
                "cmd-nowait:quickView.chooseViewer",
                "wait:600",
                "type:Two",
                "accept",
                "until:config",
                "pane:0",
                "key:space",
                "until:quickview-shown",
                "quickview:by-palette",
                "shot:done"));
            var logs = await run.FinishAsync("choice", process);

            Assert.Equal((Fixture, "Viewer: Quick View Fixture"), (Q(logs, "first", "viewer"), Q(logs, "first", "chooser")));
            Assert.Equal(("quickview-fixture-two", "Viewer: Quick View Fixture Two"), (Q(logs, "second", "viewer"), Q(logs, "second", "chooser")));
            Assert.Equal("quickview-fixture-two", Q(logs, "reopened", "viewer"));
            Assert.Equal(("thumbnail", "", "Viewer: No viewer", ""), (Q(logs, "none", "state"), Q(logs, "none", "viewer"), Q(logs, "none", "chooser"), Q(logs, "none", "offer")));
            Assert.Equal("quickview-fixture-two", Q(logs, "by-palette", "viewer"));
            var viewers = run.ReadConfig()["quickView"]?["viewers"]?.AsObject();
            Assert.Equal("quickview-fixture-two", viewers?["*.png"]?.GetValue<string>());
        }
        finally
        {
            run.Stop();
        }
    }

    // The fixture viewer as the marketplace ships a tool: a zip of its folder in <index>\files, with its tool.json as manifest.
    private static JsonObject FixtureItem(string index)
    {
        var files = Directory.CreateDirectory(Path.Combine(index, "files")).FullName;
        var zip = Path.Combine(files, "quickview-fixture.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.GetFiles(FixtureSource).Where(f => !f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
            {
                archive.CreateEntryFromFile(file, Path.GetFileName(file));
            }
        }
        var bytes = File.ReadAllBytes(zip);
        return new JsonObject
        {
            ["id"] = Fixture,
            ["kind"] = "tool",
            ["name"] = "Quick View Fixture",
            ["author"] = new JsonObject { ["name"] = "CabinetOS", ["verified"] = false },
            ["version"] = "1.0.0",
            ["description"] = "The test viewer.",
            ["size"] = bytes.Length,
            ["download"] = new JsonObject { ["url"] = "files/quickview-fixture.zip", ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)) },
            ["manifest"] = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureSource, "tool.json"))),
            ["minCoreVersion"] = "0.1.0",
            ["license"] = "MIT",
        };
    }
}
