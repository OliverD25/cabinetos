using System.Collections.Concurrent;
using System.Text.Json;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Market;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Themes;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// The theme gallery's model (<c>themes.browse</c>, docs/ui.md, "The theme gallery"): the themes catalogue as tiles,
/// the filter chips with the system's mode first, the search, the selection the keys move, and the live preview of
/// the selected tile (<c>preview_theme</c> for a theme that is not installed, <c>get_theme</c> for one that is).
/// </summary>
public class ThemeGalleryTests
{
    private const string Source = @"C:\market\themes.json";

    private static MarketItem Theme(
        string id,
        string name,
        string appearance,
        bool density = false,
        string? background = "#2E3440",
        string? accent = null,
        string version = "1.0.0",
        string author = "CabinetOS")
    {
        using var manifest = JsonDocument.Parse(accent is null ? "{}" : $$"""{"accent":"{{accent}}"}""");
        return new MarketItem(id, ExtensionKinds.Theme, name, new MarketAuthor(author), version, $"{name}, for the tests.", 1000,
            new MarketDownload($"files/{id}-{version}.json", new string('a', 64)), manifest.RootElement.Clone(), "0.1.0", "MIT",
            Appearance: appearance, Density: density,
            Tile: background is null ? null : new MarketTile(background, "#ECEFF4", accent ?? "#88C0D0"));
    }

    // The catalogue: two dark, one light, two that follow Windows (one a density preset).
    private static readonly MarketItem Nord = Theme("nord", "Nord", ColorTheme.Dark, accent: "#88C0D0");
    private static readonly MarketItem Dracula = Theme("dracula", "Dracula", ColorTheme.Dark, background: "#282A36", accent: "#BD93F9");
    private static readonly MarketItem Paper = Theme("paper", "Paper", ColorTheme.Light, background: "#F9F2E0", accent: "#268BD2");
    private static readonly MarketItem Default = Theme("default", "Default", ColorTheme.System, background: "#2B2B2B");
    private static readonly MarketItem Compact = Theme("commander-compact", "Commander Compact", ColorTheme.System, density: true, background: "#2B2B2B");

    private static readonly MarketItem[] Catalogue = [Default, Compact, Nord, Dracula, Paper];

    private static ThemeInfo Info(string id, string kind = ColorTheme.Dark, bool metrics = false) =>
        new(id, id == "mine" ? "Mine" : id, "Someone", "1.0.0", kind, HasMetrics: metrics);

    // The core: the catalogue, what is in the themes folder, and a whole theme for each preview.
    private static FakeChannel CoreWith(
        IReadOnlyList<MarketItem> catalogue,
        string[] installed,
        Func<CoreRequest, CoreReply?>? more = null,
        IReadOnlyList<ThemeInfo>? folder = null) => new(request => more?.Invoke(request) ?? request switch
        {
            MarketplaceRefreshRequest { Catalogue: Catalogues.Themes } => new MarketplaceIndexReply(catalogue, Source, 1790000000000),
            MarketplaceSearchRequest { Catalogue: Catalogues.Themes } search => new MarketplaceIndexReply(
                [.. catalogue.Where(i => i.Name.Contains(search.Query, StringComparison.OrdinalIgnoreCase))], Source, 1790000000000),
            ListThemesRequest => new ThemesReply(folder ?? [.. installed.Select(id => Info(id))]),
            GetThemeRequest get => new ThemeReply(Whole(get.ThemeId ?? "default")),
            PreviewThemeRequest preview => new ThemeReply(Whole(preview.ExtensionId)),
            _ => new OkReply(),
        });

    private static ColorTheme Whole(string id) => ThemeTests.Shipped("nord") with { Id = id };

    // A gallery on a core, previews on and no delay, in Windows' dark mode; the IDs the window was asked to paint (null: the theme in effect).
    private static async Task<(ThemeGalleryModel Gallery, ConcurrentQueue<string?> Painted)> OpenAsync(
        FakeChannel core, string? current = "default", bool systemIsLight = false)
    {
        var gallery = new ThemeGalleryModel(core, () => systemIsLight, () => new Argb(0xFF, 0x00, 0x78, 0xD4), _ => Task.CompletedTask)
        {
            PreviewDelay = TimeSpan.Zero,
        };
        var painted = new ConcurrentQueue<string?>();
        gallery.Preview += theme => painted.Enqueue(theme?.Id);
        gallery.BeginPreviews();
        Assert.True(await gallery.LoadAsync(current));
        return (gallery, painted);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 10)
        {
            Assert.True(waited < 5000, "waited 5 s");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Opening_reads_the_themes_catalogue_and_the_themes_folder_and_selects_the_theme_in_effect()
    {
        var core = CoreWith(Catalogue, ["default", "nord"]);

        var (gallery, painted) = await OpenAsync(core, "nord");

        var refresh = Assert.Single(core.Requests.OfType<MarketplaceRefreshRequest>());
        Assert.Equal(Catalogues.Themes, refresh.Catalogue);
        Assert.Single(core.Requests.OfType<ListThemesRequest>());
        Assert.Equal(["default", "commander-compact", "nord", "dracula", "paper"], gallery.Tiles.Select(t => t.Id));
        Assert.Equal(GalleryFilter.All, gallery.Filter);
        Assert.Equal("nord", gallery.SelectedTile!.Id);
        Assert.Equal(("nord", true, TileAction.Applied), (gallery.CurrentThemeId, gallery.SelectedTile.IsApplied, gallery.SelectedTile.Action));
        Assert.Null(gallery.CatalogueNotice);
        // The theme in effect is on screen already: no preview is asked for.
        Assert.Empty(painted);
        Assert.Empty(core.Requests.OfType<GetThemeRequest>());
        Assert.Empty(core.Requests.OfType<PreviewThemeRequest>());
    }

    [Fact]
    public async Task A_tile_is_painted_in_the_catalogues_colours_with_a_mark_and_what_it_can_do()
    {
        var core = CoreWith(Catalogue, ["default", "commander-compact", "nord"]);
        var (gallery, _) = await OpenAsync(core, "nord");

        var nord = gallery.Tiles.Single(t => t.Id == "nord");
        Assert.Equal((new Argb(0xFF, 0x2E, 0x34, 0x40), new Argb(0xFF, 0xEC, 0xEF, 0xF4), new Argb(0xFF, 0x88, 0xC0, 0xD0)),
            (nord.Colors.Background, nord.Colors.Text, nord.Colors.Accent));
        Assert.Equal(("Dark", false, true, TileAction.Applied), (nord.Mark, nord.Compact, nord.IsPresent, nord.Action));
        Assert.Equal("Nord, by CabinetOS, dark, applied", nord.AutomationName);

        // Not installed: Install; installed (a shipped one, say): Apply, and not removable, since it did not come from the marketplace.
        var dracula = gallery.Tiles.Single(t => t.Id == "dracula");
        Assert.Equal((false, TileAction.Install, false, "Dracula, by CabinetOS, dark"), (dracula.IsPresent, dracula.Action, dracula.CanRemove, dracula.AutomationName));
        var shipped = gallery.Tiles.Single(t => t.Id == "commander-compact");
        Assert.Equal(("System", true, true, TileAction.Apply, false), (shipped.Mark, shipped.Compact, shipped.IsPresent, shipped.Action, shipped.CanRemove));
        Assert.Equal("Light", gallery.Tiles.Single(t => t.Id == "paper").Mark);
    }

    [Fact]
    public async Task A_system_theme_is_painted_as_windows_is_now_in_its_own_accent_or_windows()
    {
        // Windows in light mode: the window's own light colours, and Windows' accent (#0078D4 here) for a theme without one.
        var (light, _) = await OpenAsync(CoreWith(Catalogue, ["default"]), systemIsLight: true);
        var tile = light.Tiles.Single(t => t.Id == "default");
        Assert.Equal((new Argb(0xFF, 0xF9, 0xF9, 0xF9), new Argb(0xFF, 0x1B, 0x1B, 0x1B), new Argb(0xFF, 0x00, 0x78, 0xD4)),
            (tile.Colors.Background, tile.Colors.Text, tile.Colors.Accent));

        var (dark, _) = await OpenAsync(CoreWith(Catalogue, ["default"]), systemIsLight: false);
        tile = dark.Tiles.Single(t => t.Id == "default");
        Assert.Equal((new Argb(0xFF, 0x2B, 0x2B, 0x2B), Argb.White), (tile.Colors.Background, tile.Colors.Text));

        // A system theme that names an accent keeps it in both modes.
        var named = Theme("tinted", "Tinted", ColorTheme.System, accent: "#FF8800");
        var (gallery, _) = await OpenAsync(CoreWith([named], []), systemIsLight: true);
        Assert.Equal(new Argb(0xFF, 0xFF, 0x88, 0x00), gallery.Tiles.Single().Colors.Accent);
    }

    [Fact]
    public async Task An_item_without_tile_colours_gets_a_plain_tile_from_what_the_item_says()
    {
        // An index from before the tile existed: the kind and the accent are all there is.
        var plain = Theme("old", "Old", ColorTheme.Light, background: null, accent: "#0F6CBD");
        var (gallery, _) = await OpenAsync(CoreWith([plain], []));

        var tile = gallery.Tiles.Single();
        Assert.Equal((new Argb(0xFF, 0xF3, 0xF3, 0xF3), new Argb(0xFF, 0x1B, 0x1B, 0x1B), new Argb(0xFF, 0x0F, 0x6C, 0xBD)),
            (tile.Colors.Background, tile.Colors.Text, tile.Colors.Accent));
    }

    [Fact]
    public async Task The_chips_put_the_mode_windows_is_in_first_after_All()
    {
        var (dark, _) = await OpenAsync(CoreWith(Catalogue, []), systemIsLight: false);
        Assert.Equal(["All", "Dark", "Light", "System", "Density presets", "Installed"], dark.FilterOrder.Select(ThemeGalleryModel.TitleOf));

        var (light, _) = await OpenAsync(CoreWith(Catalogue, []), systemIsLight: true);
        Assert.Equal(["All", "Light", "Dark", "System", "Density presets", "Installed"], light.FilterOrder.Select(ThemeGalleryModel.TitleOf));
        Assert.Equal(GalleryFilter.All, light.Filter);
    }

    [Fact]
    public async Task A_filter_shows_the_themes_of_its_kind_and_keeps_the_selection_when_its_tile_stays()
    {
        var core = CoreWith(Catalogue, ["default", "commander-compact"]);
        var (gallery, _) = await OpenAsync(core, "default");
        var redraws = 0;
        gallery.Changed += () => redraws++;

        gallery.SetFilter(GalleryFilter.Dark);
        Assert.Equal(["nord", "dracula"], gallery.Tiles.Select(t => t.Id));
        // The theme in effect is a system theme: its tile is gone, so nothing is selected.
        Assert.Equal(-1, gallery.Selected);
        gallery.SetFilter(GalleryFilter.Light);
        Assert.Equal(["paper"], gallery.Tiles.Select(t => t.Id));
        gallery.SetFilter(GalleryFilter.System);
        Assert.Equal(["default", "commander-compact"], gallery.Tiles.Select(t => t.Id));
        gallery.SetFilter(GalleryFilter.Density);
        Assert.Equal(["commander-compact"], gallery.Tiles.Select(t => t.Id));
        gallery.SetFilter(GalleryFilter.Installed);
        Assert.Equal(["default", "commander-compact"], gallery.Tiles.Select(t => t.Id));
        gallery.SetFilter(GalleryFilter.All);
        Assert.Equal(5, gallery.Tiles.Count);
        Assert.True(redraws >= 6);

        // The same chip again changes nothing.
        redraws = 0;
        gallery.SetFilter(GalleryFilter.All);
        Assert.Equal(0, redraws);
    }

    [Fact]
    public async Task Themes_in_the_folder_that_the_catalogue_does_not_list_are_shown_after_it()
    {
        var core = CoreWith(Catalogue, [], folder: [Info("default", ColorTheme.System), Info("mine", ColorTheme.Light), Info("tight", metrics: true)]);

        var (gallery, _) = await OpenAsync(core, "default");

        Assert.Equal(["default", "commander-compact", "nord", "dracula", "paper", "mine", "tight"], gallery.Tiles.Select(t => t.Id));
        var mine = gallery.Tiles.Single(t => t.Id == "mine");
        Assert.Equal((null, true, TileAction.Apply, false, "Light"), (mine.Item, mine.IsPresent, mine.Action, mine.CanRemove, mine.Mark));
        Assert.True(gallery.Tiles.Single(t => t.Id == "tight").Compact);
        gallery.SetFilter(GalleryFilter.Installed);
        Assert.Equal(["default", "mine", "tight"], gallery.Tiles.Select(t => t.Id));
    }

    [Fact]
    public async Task A_catalogue_that_cannot_be_read_leaves_the_installed_themes_and_one_line_of_notice()
    {
        var core = CoreWith(Catalogue, [], request => request switch
        {
            MarketplaceRefreshRequest => new ErrorReply(ErrorCodes.MarketplaceError, "cannot fetch the themes catalogue https://example.org/themes.json: dns error"),
            GetValueRequest { Path: "marketplace.themes" } => new ValueReply(JsonDocument.Parse("\"https://example.org/themes.json\"").RootElement.Clone()),
            _ => null,
        }, folder: [Info("default", ColorTheme.System), Info("nord", metrics: false)]);
        var gallery = new ThemeGalleryModel(core, debounce: _ => Task.CompletedTask);

        Assert.False(await gallery.LoadAsync("default"));

        Assert.Equal(["default", "nord"], gallery.Tiles.Select(t => t.Id));
        Assert.All(gallery.Tiles, tile => Assert.True(tile.IsPresent));
        Assert.Equal("Cannot read the themes catalogue. Showing the installed themes.", gallery.CatalogueNotice);
        Assert.Equal(MarketStatus.Failed, gallery.Market.Status);
    }

    [Fact]
    public async Task The_search_asks_the_core_for_the_themes_catalogue_and_narrows_the_installed_themes_too()
    {
        var core = CoreWith(Catalogue, [], folder: [Info("default", ColorTheme.System), Info("mine", ColorTheme.Light)]);
        var (gallery, _) = await OpenAsync(core, "default");

        await gallery.QueryChangedAsync("dra");

        var search = Assert.Single(core.Requests.OfType<MarketplaceSearchRequest>());
        Assert.Equal(("dra", Catalogues.Themes), (search.Query, search.Catalogue));
        Assert.Equal(["dracula"], gallery.Tiles.Select(t => t.Id));
        Assert.Equal("dra", gallery.Query);
        await gallery.QueryChangedAsync("min");
        Assert.Equal(["mine"], gallery.Tiles.Select(t => t.Id));
        await gallery.QueryChangedAsync("");
        Assert.Equal(["default", "commander-compact", "nord", "dracula", "paper", "mine"], gallery.Tiles.Select(t => t.Id));
    }

    [Fact]
    public async Task The_arrows_move_across_the_grid_and_stay_on_it()
    {
        var (gallery, _) = await OpenAsync(CoreWith(Catalogue, ["default"]), "default");
        // Three columns: default, commander-compact, nord / dracula, paper.
        Assert.Equal(0, gallery.Selected);

        gallery.Move(1, 0, 3);
        Assert.Equal(1, gallery.Selected);
        gallery.Move(0, 1, 3);
        Assert.Equal("paper", gallery.SelectedTile!.Id);
        // Down from the last row stays; up goes back to the column's tile.
        gallery.Move(0, 1, 3);
        Assert.Equal(4, gallery.Selected);
        gallery.Move(0, -1, 3);
        Assert.Equal(1, gallery.Selected);
        gallery.Move(0, -1, 3);
        Assert.Equal(1, gallery.Selected);
        // One row down from the first row, in the third column: the short last row has no tile there, so its last tile.
        gallery.Select(2);
        gallery.Move(0, 1, 3);
        Assert.Equal(4, gallery.Selected);
        gallery.Move(-1, 0, 3);
        Assert.Equal(3, gallery.Selected);
        gallery.Move(10, 0, 3);
        Assert.Equal(4, gallery.Selected);
        gallery.Home();
        Assert.Equal(0, gallery.Selected);
        gallery.End();
        Assert.Equal(4, gallery.Selected);
    }

    [Fact]
    public async Task Selecting_a_tile_that_is_not_installed_previews_it_with_preview_theme_and_writes_nothing()
    {
        var core = CoreWith(Catalogue, ["default"]);
        var (gallery, painted) = await OpenAsync(core, "default");

        gallery.Select(gallery.Tiles.ToList().FindIndex(t => t.Id == "dracula"));
        await UntilAsync(() => painted.Count == 1);

        Assert.Equal("dracula", painted.Single());
        Assert.Equal("dracula", Assert.Single(core.Requests.OfType<PreviewThemeRequest>()).ExtensionId);
        Assert.Empty(core.Requests.OfType<GetThemeRequest>());
        Assert.Empty(core.Requests.OfType<SetValueRequest>());
        Assert.Empty(core.Requests.OfType<InstallExtensionRequest>());
        Assert.True(gallery.IsPreviewShown);
        Assert.Equal("Previewing Dracula · Esc restores", gallery.PreviewText);
    }

    [Fact]
    public async Task Selecting_an_installed_tile_previews_it_with_get_theme()
    {
        var core = CoreWith(Catalogue, ["default", "nord"]);
        var (gallery, painted) = await OpenAsync(core, "default");

        gallery.Select(gallery.Tiles.ToList().FindIndex(t => t.Id == "nord"));
        await UntilAsync(() => painted.Count == 1);

        Assert.Equal("nord", painted.Single());
        Assert.Equal("nord", Assert.Single(core.Requests.OfType<GetThemeRequest>()).ThemeId);
        Assert.Empty(core.Requests.OfType<PreviewThemeRequest>());
    }

    [Fact]
    public async Task Selecting_the_theme_in_effect_again_restores_it_without_a_request()
    {
        var core = CoreWith(Catalogue, ["default"]);
        var (gallery, painted) = await OpenAsync(core, "default");
        gallery.Select(2);
        await UntilAsync(() => painted.Count == 1);
        var asked = core.Requests.Count;

        gallery.Select(0);
        await UntilAsync(() => painted.Count == 2);

        Assert.Null(painted.Last());
        Assert.False(gallery.IsPreviewShown);
        Assert.Null(gallery.PreviewText);
        Assert.Equal(asked, core.Requests.Count);
    }

    [Fact]
    public async Task Esc_restores_the_theme_in_effect_once_and_only_after_a_preview()
    {
        var (gallery, painted) = await OpenAsync(CoreWith(Catalogue, ["default"]), "default");
        gallery.EndPreviews(restore: true);
        Assert.Empty(painted);

        gallery.BeginPreviews();
        gallery.Select(2);
        await UntilAsync(() => painted.Count == 1);
        gallery.EndPreviews(restore: true);

        Assert.Equal(new string?[] { gallery.Tiles[2].Id, null }, painted.ToArray());
        Assert.False(gallery.IsPreviewShown);
        Assert.Null(gallery.PreviewingName);
        // Closed: a later selection is not previewed.
        gallery.Select(3);
        await Task.Delay(50);
        Assert.Equal(2, painted.Count);
    }

    [Fact]
    public async Task A_reply_for_a_tile_the_selection_has_left_is_dropped()
    {
        var pending = new List<(string Id, TaskCompletionSource<CoreReply> Reply)>();
        var core = new HeldCore(request => request switch
        {
            PreviewThemeRequest preview => Hold(pending, preview.ExtensionId),
            MarketplaceRefreshRequest => Task.FromResult<CoreReply>(new MarketplaceIndexReply(Catalogue, Source, 1)),
            ListThemesRequest => Task.FromResult<CoreReply>(new ThemesReply([Info("default", ColorTheme.System)])),
            _ => Task.FromResult<CoreReply>(new OkReply()),
        });
        var gallery = new ThemeGalleryModel(core, debounce: _ => Task.CompletedTask) { PreviewDelay = TimeSpan.Zero };
        var painted = new ConcurrentQueue<string?>();
        gallery.Preview += theme => painted.Enqueue(theme?.Id);
        gallery.BeginPreviews();
        await gallery.LoadAsync("default");

        gallery.Select(3);
        await UntilAsync(() => pending.Count == 1);
        gallery.Select(4);
        await UntilAsync(() => pending.Count == 2);
        pending[1].Reply.SetResult(new ThemeReply(Whole("paper")));
        await UntilAsync(() => painted.Count == 1);
        pending[0].Reply.SetResult(new ThemeReply(Whole("dracula")));
        await Task.Delay(50);

        Assert.Equal(new string?[] { "paper" }, painted.ToArray());
        Assert.Equal("Previewing Paper · Esc restores", gallery.PreviewText);

        static Task<CoreReply> Hold(List<(string, TaskCompletionSource<CoreReply>)> list, string id)
        {
            var reply = new TaskCompletionSource<CoreReply>();
            list.Add((id, reply));
            return reply.Task;
        }
    }

    [Fact]
    public async Task A_preview_the_core_refuses_leaves_the_screen_and_says_why_in_one_line()
    {
        var core = CoreWith(Catalogue, ["default"], request => request is PreviewThemeRequest
            ? new ErrorReply(ErrorCodes.MarketplaceError, "cannot download dracula 1.0.0: the server answered 500")
            : null);
        var (gallery, painted) = await OpenAsync(core, "default");

        gallery.Select(gallery.Tiles.ToList().FindIndex(t => t.Id == "dracula"));
        await UntilAsync(() => gallery.Error is not null);

        Assert.Equal("Cannot preview Dracula: cannot download dracula 1.0.0: the server answered 500", gallery.Error);
        Assert.Empty(painted);
        Assert.False(gallery.IsPreviewShown);
        // Selecting another tile clears the line.
        gallery.Select(0);
        Assert.Null(gallery.Error);
    }

    [Fact]
    public async Task Enter_on_a_theme_that_is_not_installed_installs_it_and_applies_it()
    {
        var core = CoreWith(Catalogue, ["default"]);
        var (gallery, _) = await OpenAsync(core, "default");
        gallery.Select(gallery.Tiles.ToList().FindIndex(t => t.Id == "dracula"));

        var outcome = await gallery.ActivateSelectedAsync("01J0000000000000000000000A");

        Assert.True(outcome!.Ok);
        Assert.Equal(["install_extension", "set_value"], core.Requests.Where(r => r is InstallExtensionRequest or SetValueRequest).Select(r => r.Type));
        Assert.Equal("dracula", core.Requests.OfType<InstallExtensionRequest>().Single().ExtensionId);
        var set = core.Requests.OfType<SetValueRequest>().Single();
        Assert.Equal(("ui.theme", "dracula"), (set.Path, set.Value.GetString()));
        // The core's events then say it is the theme in effect, and the tile reads Applied.
        gallery.OnEvent(new InstallFinishedEvent("dracula", true, "installed dracula 1.0.0 (theme)", "1.0.0"));
        gallery.OnEvent(new ThemeChangedEvent(Whole("dracula")));
        Assert.Equal(("dracula", TileAction.Applied, false), (gallery.CurrentThemeId, gallery.Tiles.Single(t => t.Id == "dracula").Action, gallery.IsPreviewShown));
        // It came from the marketplace, so it can be removed once it is not the theme in effect.
        Assert.False(gallery.Tiles.Single(t => t.Id == "dracula").CanRemove);
        gallery.OnEvent(new ThemeChangedEvent(Whole("default")));
        Assert.True(gallery.Tiles.Single(t => t.Id == "dracula").CanRemove);
        Assert.Equal(TileAction.Apply, gallery.Tiles.Single(t => t.Id == "dracula").Action);
    }

    [Fact]
    public async Task Enter_on_an_installed_theme_applies_it_and_on_the_theme_in_effect_does_nothing()
    {
        var core = CoreWith(Catalogue, ["default", "nord"]);
        var (gallery, _) = await OpenAsync(core, "default");

        // The theme in effect: nothing to do.
        Assert.Null(await gallery.ActivateSelectedAsync());
        Assert.Empty(core.Requests.OfType<SetValueRequest>());

        gallery.Select(gallery.Tiles.ToList().FindIndex(t => t.Id == "nord"));
        var outcome = await gallery.ActivateSelectedAsync();

        Assert.True(outcome!.Ok);
        Assert.Empty(core.Requests.OfType<InstallExtensionRequest>());
        Assert.Equal("nord", core.Requests.OfType<SetValueRequest>().Single().Value.GetString());
    }

    [Fact]
    public async Task A_theme_with_a_newer_version_offers_an_update_that_installs_without_applying()
    {
        var newer = Nord with { Version = "1.1.0", InstalledVersion = "1.0.0" };
        var core = CoreWith([Default, newer], ["default", "nord"]);
        var (gallery, _) = await OpenAsync(core, "default");
        var tile = gallery.Tiles.Single(t => t.Id == "nord");
        Assert.Equal((TileAction.Update, "Update available"), (tile.Action, tile.StateText));

        gallery.Select(1);
        var outcome = await gallery.ActivateSelectedAsync();

        Assert.True(outcome!.Ok);
        Assert.Equal("nord", core.Requests.OfType<InstallExtensionRequest>().Single().ExtensionId);
        Assert.Empty(core.Requests.OfType<SetValueRequest>());
    }

    [Fact]
    public async Task A_failed_install_keeps_the_core_message_for_the_line_under_the_toolbar()
    {
        var core = CoreWith(Catalogue, ["default"], request => request is InstallExtensionRequest
            ? new ErrorReply(ErrorCodes.HashMismatch, "the download of dracula does not have the SHA-256 the index gives")
            : null);
        var (gallery, _) = await OpenAsync(core, "default");
        gallery.Select(gallery.Tiles.ToList().FindIndex(t => t.Id == "dracula"));

        var outcome = await gallery.ActivateSelectedAsync();

        Assert.Equal("the download of dracula does not have the SHA-256 the index gives", outcome!.Error);
        Assert.Equal(outcome.Error, gallery.Error);
        Assert.Empty(core.Requests.OfType<SetValueRequest>());
        Assert.Equal(TileAction.Install, gallery.Tiles.Single(t => t.Id == "dracula").Action);
    }

    [Fact]
    public async Task An_install_shows_its_progress_on_the_tile_and_a_removal_leaves_the_folder()
    {
        var core = CoreWith(Catalogue, ["default"]);
        var (gallery, _) = await OpenAsync(core, "default");

        gallery.Market.OnEvent(new InstallProgressEvent("dracula", 500, 1000));
        var tile = gallery.Tiles.Single(t => t.Id == "dracula");
        Assert.Equal((TileAction.Installing, "Installing 50%"), (tile.Action, tile.StateText));

        gallery.OnEvent(new InstallFinishedEvent("dracula", true, "installed dracula 1.0.0 (theme)", "1.0.0"));
        Assert.Equal(TileAction.Apply, gallery.Tiles.Single(t => t.Id == "dracula").Action);

        var removal = await gallery.RemoveAsync("dracula");
        Assert.True(removal.Ok);
        Assert.Equal("dracula", core.Requests.OfType<UninstallExtensionRequest>().Single().ExtensionId);
        Assert.Equal(TileAction.Install, gallery.Tiles.Single(t => t.Id == "dracula").Action);
    }

    // A core whose answers the test holds back, to answer them out of order.
    private sealed class HeldCore(Func<CoreRequest, Task<CoreReply>> answer) : ICoreChannel
    {
        public Task<CoreReply> RequestAsync(CoreRequest request, CancellationToken cancellationToken = default) => answer(request);
    }
}
