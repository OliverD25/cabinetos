using System.Text.Json.Nodes;
using CabinetOS.Tests.Support;
using static CabinetOS.Tests.Support.WindowRun;

namespace CabinetOS.Tests;

/// <summary>
/// The theme gallery on a real window and core, with a local catalogue of the shipped themes and the theme collection
/// (Phase 23; docs/ui.md, "The theme gallery"). Each test opens a window, so they run only with <c>CABINETOS_UI_E2E=1</c>
/// and a built window (Debug) and core. The snapshot steps drive the window (<c>gallery:</c> logs the gallery's state and
/// clicks its tiles, <c>key:</c> posts real keys) and the window's log lines say what it did.
/// </summary>
public class ThemeGalleryEndToEndTests
{
    // The configuration: the local catalogue is both the extensions index and the themes catalogue. With brokenThemes the themes
    // address is a themes.json that is no JSON, which is a catalogue that cannot be read (a file that is not there at all is the
    // transition rule: the themes of index.json, of which there are none now).
    private static Func<string, JsonObject> Config(bool brokenThemes = false) => root =>
    {
        var catalogue = Path.Combine(root, "catalogue");
        EndToEndTests.BuildLocalIndex(catalogue, collection: true);
        var themes = catalogue;
        if (brokenThemes)
        {
            themes = Path.Combine(root, "broken");
            Directory.CreateDirectory(themes);
            File.WriteAllText(Path.Combine(themes, "themes.json"), "{ \"schemaVersion\": ");
        }
        return new JsonObject
        {
            ["version"] = 1,
            ["ui"] = new JsonObject { ["dualPane"] = true },
            ["marketplace"] = new JsonObject { ["index"] = catalogue, ["themes"] = themes },
        };
    };

    // ui.theme in the file: absent or "default" until a theme is chosen (the core writes the default when it starts).
    private static string ThemeInFile(WindowRun run) => run.ReadConfig()["ui"]?["theme"]?.GetValue<string>() ?? "default";

    /// <summary>
    /// The Extensions page lists no theme. The theme picker's last row opens the gallery, so does the palette's "Themes: Browse".
    /// The keys move the selection across the tiles (Home, Right, Down, End), each tile is previewed, and Esc closes the gallery
    /// and paints the theme in effect again. Windows' mode puts Dark or Light first among the chips.
    /// </summary>
    [Fact]
    public async Task The_Extensions_page_has_no_theme_and_the_gallery_opens_from_the_picker_and_the_palette_and_the_keys_move_across_its_tiles()
    {
        var run = Prepare("gallery-keys", Config());
        try
        {
            var process = run.Start("keys", string.Join(';',
                "size:1400x900",
                "mode:dark",
                // The Extensions page: plugins and tools of the index, no theme.
                "click:Extensions",
                "until:market-complete",
                "market:extensions",
                "cmd:overlay.close",
                "wait:300",
                // The picker's last row (End) opens the gallery with Enter.
                "cmd:preferences.selectColorTheme",
                "wait:600",
                "key:end",
                "wait:300",
                "key:enter",
                "until:gallery-ready",
                "gallery:state:from-picker",
                "key:home",
                "wait:500",
                "gallery:state:home",
                "key:right",
                "wait:500",
                "gallery:state:right",
                "key:down",
                "wait:500",
                "gallery:state:down",
                "key:end",
                "wait:500",
                "gallery:state:end",
                // Esc closes the gallery and paints the theme in effect again.
                "key:escape",
                "wait:800",
                "gallery:state:closed",
                // The palette's own row.
                "cmd:palette.show",
                "wait:400",
                "type:Themes: Browse",
                "wait:400",
                "key:enter",
                "until:gallery-ready",
                "gallery:state:from-palette",
                // Windows' mode: the chip of the mode Windows is in comes first among Dark and Light.
                "mode:light",
                "wait:500",
                "gallery:state:light-mode",
                "mode:dark",
                "cmd:overlay.close",
                "wait:500",
                "shot:done"));
            var logs = await run.FinishAsync("keys", process);

            // The Extensions page holds the index's plugins and tools and not one theme.
            var cards = Assert.Single(logs, l => Message(l) == "marketplace cards" && Text(l, "label") == "extensions");
            var ids = Text(cards, "ids").Split(',', StringSplitOptions.RemoveEmptyEntries);
            Assert.NotEmpty(ids);
            Assert.DoesNotContain(ids, id => id is "default" or "nord" or "dracula" or "commander-compact");

            // Both ways to the gallery ran "themes.browse", by the key and by the palette.
            var browse = logs.Where(l => Message(l) == "command executed" && Text(l, "command") == "themes.browse").Select(l => Text(l, "trigger")).ToList();
            Assert.Equal(new[] { "key", "palette" }, browse);

            var opened = State(logs, "gallery state", "from-picker");
            Assert.True(Field(opened, "open").GetBoolean());
            Assert.Equal(("All", "default", "default", "tiles"), (Text(opened, "filter"), Text(opened, "selected"), Text(opened, "applied"), Text(opened, "keyboard")));
            Assert.Equal("All,Dark,Light,System,Density,Installed", Text(opened, "chips"));
            var tiles = Text(opened, "ids").Split(',');
            Assert.Equal(Field(opened, "tiles").GetInt32(), tiles.Length);
            Assert.True(tiles.Length >= 40, $"{tiles.Length} tiles");
            Assert.Equal(tiles.Length, Field(opened, "made").GetInt32());
            Assert.True(Field(opened, "columns").GetInt32() >= 2);

            // The keys across the grid: the first tile, the next, one row down, the last.
            Assert.Equal(tiles[0], Text(State(logs, "gallery state", "home"), "selected"));
            var right = State(logs, "gallery state", "right");
            Assert.Equal(tiles[1], Text(right, "selected"));
            var columns = Field(right, "columns").GetInt32();
            Assert.Equal(tiles[1 + columns], Text(State(logs, "gallery state", "down"), "selected"));
            Assert.Equal(tiles[^1], Text(State(logs, "gallery state", "end"), "selected"));

            // The tile selected is previewed on the window; Esc closed the gallery and painted the theme in effect again.
            Assert.Contains(logs, l => Message(l) == "theme previewed");
            var closed = State(logs, "gallery state", "closed");
            Assert.False(Field(closed, "open").GetBoolean());
            Assert.Equal("", Text(closed, "previewing"));
            var themeLines = logs.Where(l => Target(l) == "cabinetos_ui::theme").ToList();
            var lastPreview = themeLines.FindLastIndex(l => Message(l) == "theme previewed");
            Assert.True(lastPreview >= 0, "no theme was previewed");
            Assert.True(themeLines.FindIndex(lastPreview, l => Message(l) == "theme restored" && Text(l, "theme") == "default") > lastPreview,
                "the theme in effect was not painted back");
            Assert.DoesNotContain(logs, l => Message(l) == "theme chosen");
            Assert.Equal("default", ThemeInFile(run));

            // The status bar said which theme was previewed, and was empty again after Esc.
            var status = logs.Where(l => Message(l) == "preview status shown").Select(l => Text(l, "text")).ToList();
            Assert.Contains(status, text => text.StartsWith("Previewing ", StringComparison.Ordinal) && text.EndsWith("· Esc restores", StringComparison.Ordinal));
            Assert.Equal("", status[^1]);

            Assert.Equal("palette", Text(logs.Last(l => Message(l) == "command executed" && Text(l, "command") == "themes.browse"), "trigger"));
            Assert.True(Field(State(logs, "gallery state", "from-palette"), "open").GetBoolean());
            Assert.Equal("All,Light,Dark,System,Density,Installed", Text(State(logs, "gallery state", "light-mode"), "chips"));
        }
        finally
        {
            run.Stop();
        }
    }

    /// <summary>
    /// A tile clicked is selected and its theme is previewed without being installed; Esc paints the theme in effect again, and
    /// nothing was installed or written.
    /// </summary>
    [Fact]
    public async Task A_theme_that_is_not_installed_is_previewed_without_installing_it_and_Esc_restores_the_theme_in_effect()
    {
        var run = Prepare("gallery-preview", Config());
        try
        {
            var process = run.Start("preview", string.Join(';',
                "size:1400x900",
                "mode:dark",
                "cmd:themes.browse",
                "until:gallery-ready",
                "gallery:click:dracula",
                "until:gallery-preview",
                "wait:400",
                "gallery:state:previewing",
                "shot:previewing",
                "cmd:overlay.close",
                "wait:800",
                "gallery:state:closed",
                "shot:done"));
            var logs = await run.FinishAsync("preview", process);

            var previewing = State(logs, "gallery state", "previewing");
            Assert.Equal(("dracula", "Dracula", "default"), (Text(previewing, "selected"), Text(previewing, "previewing"), Text(previewing, "applied")));
            var themeLines = logs.Where(l => Target(l) == "cabinetos_ui::theme").ToList();
            var previewed = themeLines.FindIndex(l => Message(l) == "theme previewed" && Text(l, "theme") == "dracula");
            Assert.True(previewed >= 0, "no \"theme previewed\" for dracula");
            Assert.True(themeLines.FindIndex(previewed, l => Message(l) == "theme restored" && Text(l, "theme") == "default") > previewed,
                "no \"theme restored\" for default after the preview");
            var status = logs.Where(l => Message(l) == "preview status shown").Select(l => Text(l, "text")).ToList();
            Assert.Equal(new[] { "Previewing Dracula · Esc restores", "" }, status);
            Assert.False(Field(State(logs, "gallery state", "closed"), "open").GetBoolean());

            // Nothing was installed or written.
            Assert.False(File.Exists(Path.Combine(run.ThemesFolder, "dracula.json")));
            Assert.Equal("default", ThemeInFile(run));
        }
        finally
        {
            run.Stop();
        }
    }

    /// <summary>
    /// A double-click installs a theme of the catalogue and applies it; the picker then lists it, and the gallery marks it Applied.
    /// </summary>
    [Fact]
    public async Task A_double_click_installs_and_applies_a_theme_of_the_local_catalogue_and_the_picker_lists_it()
    {
        var run = Prepare("gallery-install", Config());
        try
        {
            var process = run.Start("install", string.Join(';',
                "size:1400x900",
                "mode:dark",
                "cmd:themes.browse",
                "until:gallery-ready",
                "gallery:double:dracula",
                "until:gallery-applied:dracula",
                "wait:500",
                "gallery:state:installed",
                "shot:installed",
                "cmd:overlay.close",
                "wait:500",
                "cmd:preferences.selectColorTheme",
                "wait:1000",
                "cmd:overlay.close",
                "wait:300",
                "shot:done"));
            var logs = await run.FinishAsync("install", process);

            Assert.True(File.Exists(Path.Combine(run.ThemesFolder, "dracula.json")));
            Assert.Equal("dracula", run.ReadConfig()["ui"]!["theme"]!.GetValue<string>());
            var installed = State(logs, "gallery state", "installed");
            Assert.Equal(("dracula", "dracula", "Applied"), (Text(installed, "selected"), Text(installed, "applied"), Text(installed, "selected_action")));
            Assert.Equal("", Text(installed, "previewing"));
            Assert.Contains(logs, l => Message(l) == "notice shown" && Text(l, "text") == "Dracula is installed and applied.");
            // The theme in effect now: no preview is left on the status bar.
            Assert.Equal("", logs.Where(l => Message(l) == "preview status shown").Select(l => Text(l, "text")).LastOrDefault() ?? "");
            // The picker lists it (and the gallery's own row after the themes).
            var listed = Assert.Single(logs, l => Message(l) == "theme picker listed");
            Assert.Contains("dracula", Text(listed, "ids").Split(','));
        }
        finally
        {
            run.Stop();
        }
    }

    /// <summary>
    /// With the themes catalogue unreachable the gallery shows the installed themes with one line saying so, and no dialog.
    /// </summary>
    [Fact]
    public async Task A_catalogue_that_cannot_be_read_leaves_the_installed_themes_and_one_line_in_the_gallery()
    {
        var run = Prepare("gallery-offline", Config(brokenThemes: true));
        try
        {
            var process = run.Start("offline", string.Join(';',
                "size:1400x900",
                "mode:dark",
                "cmd:themes.browse",
                "until:gallery-ready",
                "gallery:state:offline",
                "shot:offline",
                "cmd:overlay.close",
                "wait:300",
                "shot:done"));
            var logs = await run.FinishAsync("offline", process);

            var offline = State(logs, "gallery state", "offline");
            Assert.True(Field(offline, "open").GetBoolean());
            Assert.Contains("Showing the installed themes.", Text(offline, "notice"));
            // The core's own themes, which are in the themes folder.
            var tiles = Text(offline, "ids").Split(',');
            Assert.Contains("default", tiles);
            Assert.Contains("nord", tiles);
            Assert.Equal(Directory.GetFiles(run.ThemesFolder, "*.json").Count(f => !Path.GetFileName(f).StartsWith('.') && !f.EndsWith("theme.schema.json", StringComparison.Ordinal)), tiles.Length);
            // Never a dialog: nothing in the log says one opened.
            Assert.DoesNotContain(logs, l => Message(l)?.Contains("dialog", StringComparison.OrdinalIgnoreCase) == true && Level(l) != "DEBUG");
        }
        finally
        {
            run.Stop();
        }
    }
}
