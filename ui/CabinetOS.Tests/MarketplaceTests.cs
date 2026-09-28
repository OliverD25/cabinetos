using System.Text.Json;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Market;
using CabinetOS.Core.Plugins;
using CabinetOS.Core.Protocol;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>The marketplace view's model: tabs, search, the detail column, install states from the core's events, and the card texts.</summary>
public class MarketplaceTests
{
    private const string Source = @"C:\market\index.json";

    private static readonly MarketItem Hello = Item("hello", ExtensionKinds.Plugin, "Hello",
        capabilities: [new("cmd:register", "Adds the Say Hello command.", "low"), new("events:emit", "Tells the window.", "low")]);

    private static readonly MarketItem Nord = Item("nord", ExtensionKinds.Theme, "Nord", accent: "#88C0D0",
        author: "Arctic Ice Studio", verified: true, url: "https://www.nordtheme.com", rating: new MarketRating(4.8, 120), installs: 5400);

    private static readonly MarketItem Paper = Item("paper", ExtensionKinds.Theme, "Paper");

    private static readonly MarketItem Preview = Item("md-preview", ExtensionKinds.Tool, "Markdown Preview");

    private static readonly MarketItem[] Index = [Hello, Nord, Paper, Preview];

    // The index as the core offers it once the marketplace installed some of it (installedVersion).
    private static MarketItem[] Offered(params (string Id, string Version)[] installed) =>
        Index.Select(item => installed.Any(i => i.Id == item.Id) ? item with { InstalledVersion = installed.First(i => i.Id == item.Id).Version } : item).ToArray();

    private static MarketItem Item(
        string id,
        string kind,
        string name,
        string version = "1.0.0",
        IReadOnlyList<MarketCapability>? capabilities = null,
        string? accent = null,
        string author = "CabinetOS",
        bool verified = false,
        string? url = null,
        MarketRating? rating = null,
        ulong? installs = null)
    {
        using var manifest = JsonDocument.Parse(accent is null ? "{}" : $$"""{"accent":"{{accent}}"}""");
        return new MarketItem(id, kind, name, new MarketAuthor(author, verified, url), version, $"{name}, for the tests.", 1000,
            new MarketDownload($"files/{id}-{version}.zip", new string('a', 64)), manifest.RootElement.Clone(), "0.1.0", "MIT",
            Rating: rating, Installs: installs, Capabilities: capabilities);
    }

    // A core with an index and what is installed; `more` answers first when it has an answer.
    private static FakeChannel CoreWith(
        IReadOnlyList<MarketItem> index,
        Func<CoreRequest, CoreReply?>? more = null,
        string[]? plugins = null,
        string[]? themes = null,
        string[]? tools = null) => new(request => more?.Invoke(request) ?? request switch
        {
            MarketplaceRefreshRequest => new MarketplaceIndexReply(index, Source, 1790000000000),
            MarketplaceSearchRequest search => new MarketplaceIndexReply(
                index.Where(i => i.Name.Contains(search.Query, StringComparison.OrdinalIgnoreCase) || i.Id.Contains(search.Query, StringComparison.Ordinal)).ToList(),
                Source, 1790000000000),
            ListPluginsRequest => new PluginsReply((plugins ?? []).Select(id =>
                new PluginInfo(id, id, "1.0.0", "CabinetOS", "", new PluginState(PluginState.Active), [], [])).ToList()),
            ListThemesRequest => new ThemesReply((themes ?? []).Select(id => new ThemeInfo(id, id, "CabinetOS", "1.0.0", ColorTheme.Dark)).ToList()),
            ListToolsRequest => new ToolsReply((tools ?? []).Select(id => new ToolInfo(id, id, "1.0.0", "CabinetOS", "", $@"C:\tools\{id}")).ToList()),
            _ => new OkReply(),
        });

    private static MarketplaceModel Model(ICoreChannel core) => new(core, _ => Task.CompletedTask);

    [Fact]
    public async Task Refreshing_reads_the_index_and_what_is_installed()
    {
        var core = CoreWith(Offered(("nord", "1.0.0")), themes: ["default", "nord"]);
        var market = Model(core);

        Assert.True(await market.RefreshAsync("01J0000000000000000000000A"));

        Assert.Equal(MarketStatus.Ready, market.Status);
        Assert.Equal(Source, market.Source);
        Assert.Equal(["hello", "nord", "paper", "md-preview"], market.Items.Select(i => i.Id));
        Assert.Equal("4 results · WebAssembly, sandboxed", market.Caption);
        Assert.Null(market.Notice);
        // Plugins and Tool Extensions share the Plugins tab; the chip tells them apart.
        Assert.Equal((4, 2, 2, 1), (market.CountOf(MarketTabs.Discover), market.CountOf(MarketTabs.Plugins), market.CountOf(MarketTabs.Themes), market.CountOf(MarketTabs.Installed)));
        Assert.Equal("01J0000000000000000000000A", core.Requests.OfType<MarketplaceRefreshRequest>().Single().Id);
        Assert.Single(core.Requests.OfType<ListPluginsRequest>());
        Assert.Single(core.Requests.OfType<ListThemesRequest>());
        Assert.Single(core.Requests.OfType<ListToolsRequest>());
    }

    [Fact]
    public async Task A_tab_shows_its_kind_and_closes_the_detail_column()
    {
        // Nord is in the themes folder but did not come from the marketplace: not on the Installed tab.
        var market = Model(CoreWith(Offered(("paper", "1.0.0")), themes: ["nord", "paper"]));
        await market.RefreshAsync();

        market.Select("nord");
        Assert.Same(Nord, market.Selected);
        market.SetTab(MarketTabs.Themes);
        Assert.Equal(["nord", "paper"], market.Items.Select(i => i.Id));
        Assert.Null(market.Selected);
        market.SetTab(MarketTabs.Plugins);
        Assert.Equal(["hello", "md-preview"], market.Items.Select(i => i.Id));
        market.SetTab(MarketTabs.Installed);
        Assert.Equal(["paper"], market.Items.Select(i => i.Id));
        market.SetTab("nothing");
        Assert.Equal(MarketTabs.Installed, market.Tab);
        market.Select("not-in-the-index");
        Assert.Null(market.SelectedId);
    }

    [Fact]
    public async Task An_older_installed_version_offers_an_update_and_a_newer_one_does_not()
    {
        var hello = Hello with { Version = "0.10.0", InstalledVersion = "0.2.0" };
        var nord = Nord with { Version = "1.1.0", InstalledVersion = "1.0.0" };
        var paper = Paper with { InstalledVersion = "1.1.0" };
        var preview = Preview with { InstalledVersion = "1.0.0" };
        var market = Model(CoreWith([hello, nord, paper, preview], themes: ["nord", "paper"], tools: ["md-preview"]));
        await market.RefreshAsync();
        market.SetCurrentTheme("nord");

        // Versions compare as numbers: 0.10.0 is newer than 0.2.0.
        Assert.Equal((MarketAction.Update, "0.2.0"), (market.ActionFor(hello), market.InstalledVersionOf(hello)));
        // The theme in effect can be updated (the core applies it again), but not removed.
        Assert.Equal(MarketAction.Update, market.ActionFor(nord));
        Assert.False(market.CanUninstall(nord));
        // An installed version newer than the offer is kept: no update goes back.
        Assert.Equal((MarketAction.Installed, false), (market.ActionFor(paper), market.HasUpdate(paper)));
        Assert.Equal(MarketAction.Installed, market.ActionFor(preview));
        Assert.True(market.CanUninstall(preview));
        market.SetTab(MarketTabs.Installed);
        Assert.Equal(["hello", "nord", "paper", "md-preview"], market.Items.Select(i => i.Id));

        Assert.Equal("Version 0.2.0 is installed. You review the permissions again before the update is installed.", MarketText.Note(market, hello));
        Assert.Equal("Version 1.0.0 is installed. The update replaces its files.", MarketText.Note(market, nord));
        Assert.Equal("Version 1.1.0 is installed; the index offers 1.0.0.", MarketText.Note(market, paper));
        Assert.Equal("", MarketText.Note(market, preview));

        Assert.True((await market.InstallAsync("hello")).Ok);
        Assert.Equal((MarketAction.Installed, "0.10.0"), (market.ActionFor(hello), market.InstalledVersionOf(hello)));
    }

    [Fact]
    public async Task A_search_reply_does_not_undo_what_an_install_just_did()
    {
        // The core's items say nothing is installed: its search reply was made before the install ended.
        var market = Model(CoreWith(Index));
        await market.RefreshAsync();
        Assert.True((await market.InstallAsync("hello")).Ok);

        await market.QueryChangedAsync("hel");

        Assert.Equal(["hello"], market.Items.Select(i => i.Id));
        Assert.Null(market.Items[0].InstalledVersion);
        Assert.Equal("1.0.0", market.InstalledVersionOf(Hello));
    }

    [Fact]
    public async Task The_search_waits_for_typing_to_pause_and_shows_only_the_newest_text()
    {
        var gates = new List<TaskCompletionSource>();
        var core = CoreWith(Index);
        var market = new MarketplaceModel(core, token =>
        {
            var gate = new TaskCompletionSource();
            token.Register(() => gate.TrySetCanceled(token));
            gates.Add(gate);
            return gate.Task;
        });
        await market.RefreshAsync();

        var typed = new[] { market.QueryChangedAsync("n"), market.QueryChangedAsync("no"), market.QueryChangedAsync("nor") };
        await typed[0];
        await typed[1];
        Assert.Empty(core.Requests.OfType<MarketplaceSearchRequest>());
        // Until the core answers, the cards stay as they were.
        Assert.Equal(4, market.Items.Count);

        gates[2].SetResult();
        await typed[2];
        Assert.Equal(["nor"], core.Requests.OfType<MarketplaceSearchRequest>().Select(r => r.Query));
        Assert.Equal(["nord"], market.Items.Select(i => i.Id));
        Assert.Equal("1 results · WebAssembly, sandboxed", market.Caption);

        // A tab narrows the hits; an empty field shows the whole index again, without asking.
        market.SetTab(MarketTabs.Plugins);
        Assert.Empty(market.Items);
        Assert.Equal("No results for “nor”.", market.Notice!.Title);
        await market.QueryChangedAsync("  ");
        Assert.Equal(["hello", "md-preview"], market.Items.Select(i => i.Id));
        Assert.Single(core.Requests.OfType<MarketplaceSearchRequest>());
    }

    [Theory]
    [InlineData("https://marketplace.cabinetos.invalid/index.json", "The marketplace index is not published yet.", "build-index.ps1")]
    [InlineData(null, "No marketplace index is set.", "marketplace.index")]
    [InlineData("", "No marketplace index is set.", "marketplace.index")]
    [InlineData(@"D:\market", "Cannot read the marketplace index.", "cannot read the index")]
    public async Task An_index_that_cannot_be_read_says_what_to_do(string? index, string title, string detail)
    {
        var market = Model(CoreWith(Index, request => request switch
        {
            MarketplaceRefreshRequest => new ErrorReply(ErrorCodes.MarketplaceError, "cannot read the index D:\\market: not found"),
            GetValueRequest { Path: "marketplace.index" } => new ValueReply(JsonDocument.Parse(index is null ? "null" : $"\"{JsonEncodedText.Encode(index)}\"").RootElement.Clone()),
            _ => null,
        }));

        Assert.False(await market.RefreshAsync());

        Assert.Equal(MarketStatus.Failed, market.Status);
        Assert.Equal(title, market.Notice!.Title);
        Assert.Contains(detail, market.Notice.Detail);
        Assert.Empty(market.Items);
    }

    [Fact]
    public async Task A_core_before_protocol_10_has_no_marketplace()
    {
        var market = Model(new FakeChannel(request => new ErrorReply(ErrorCodes.UnknownRequest, request.Type)));

        Assert.False(await market.RefreshAsync());

        Assert.Equal(MarketStatus.Unavailable, market.Status);
        Assert.Equal("This core has no marketplace yet.", market.Notice!.Title);
    }

    [Fact]
    public async Task Empty_tabs_and_an_empty_index_say_so()
    {
        var empty = Model(CoreWith([]));
        await empty.RefreshAsync();
        Assert.Equal("The index lists no extensions.", empty.Notice!.Title);

        var market = Model(CoreWith(Index));
        await market.RefreshAsync();
        market.SetTab(MarketTabs.Installed);
        Assert.Equal("Nothing was installed from the marketplace yet.", market.Notice!.Title);
        Assert.Contains("ships with", market.Notice.Detail);
    }

    [Fact]
    public async Task The_primary_button_follows_what_is_there_and_applied()
    {
        // A shipped theme and a tool copied by hand: there, but not the marketplace's (trust rule 7).
        var market = Model(CoreWith(Index, themes: ["nord"], tools: ["md-preview"]));
        await market.RefreshAsync();
        market.SetCurrentTheme("nord");

        Assert.Equal(MarketAction.Install, market.ActionFor(Hello));
        Assert.Equal(MarketAction.Applied, market.ActionFor(Nord));
        Assert.Equal(MarketAction.InstallAndApply, market.ActionFor(Paper));
        Assert.Equal(MarketAction.Installed, market.ActionFor(Preview));
        Assert.Equal((true, false, false), (market.IsPresent(Preview), market.IsInstalled(Preview), market.CanUninstall(Preview)));
        Assert.StartsWith("This one is here already, but not from the marketplace", MarketText.Note(market, Preview));
        Assert.Equal("", MarketText.Note(market, Hello));
        market.SetCurrentTheme("paper");
        Assert.Equal((MarketAction.Installed, MarketAction.Applied), (market.ActionFor(Nord), market.ActionFor(Paper)));
    }

    [Fact]
    public async Task An_install_shows_its_progress_from_events_and_ends_with_the_reply()
    {
        var core = new SlowInstall(CoreWith(Index));
        var market = Model(core);
        await market.RefreshAsync();
        var redraws = 0;
        market.Changed += () => redraws++;

        var install = market.InstallAsync("hello", "01J0000000000000000000000B");
        Assert.Equal(MarketAction.Installing, market.ActionFor(Hello));
        Assert.Equal(new InstallProgress(0, 1000), market.InstallOf("hello"));
        Assert.Equal("01J0000000000000000000000B", core.Install!.Id);

        market.OnEvent(new InstallProgressEvent("hello", 500, 1000));
        Assert.Equal(0.5, market.InstallOf("hello")!.Fraction);
        market.OnEvent(new InstallFinishedEvent("hello", true, "installed hello 1.0.0 (plugin)"));
        Assert.Null(market.InstallOf("hello"));
        Assert.Equal(MarketAction.Installed, market.ActionFor(Hello));

        core.Reply.SetResult(new OkReply());
        Assert.True((await install).Ok);
        Assert.Equal(MarketAction.Installed, market.ActionFor(Hello));
        Assert.Equal("1.0.0", market.InstalledVersionOf(Hello));
        Assert.True(redraws >= 3);
        market.SetTab(MarketTabs.Installed);
        Assert.Equal(["hello"], market.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task A_failed_install_keeps_the_core_message_for_the_detail_column()
    {
        var market = Model(CoreWith(Index, request => request is InstallExtensionRequest
            ? new ErrorReply(ErrorCodes.HashMismatch, "the download of hello does not have the SHA-256 the index gives")
            : null));
        await market.RefreshAsync();

        var outcome = await market.InstallAsync("hello");

        Assert.Equal("the download of hello does not have the SHA-256 the index gives", outcome.Error);
        Assert.Equal(outcome.Error, market.ErrorOf("hello"));
        Assert.Equal(MarketAction.Install, market.ActionFor(Hello));

        market.OnEvent(new InstallFinishedEvent("paper", false, "paper needs a newer CabinetOS"));
        Assert.Equal("paper needs a newer CabinetOS", market.ErrorOf("paper"));
    }

    [Fact]
    public async Task Install_and_apply_writes_ui_theme_once_the_theme_is_in_place()
    {
        var core = CoreWith(Index);
        var market = Model(core);
        await market.RefreshAsync();

        Assert.True((await market.InstallAndApplyAsync("paper", "01J0000000000000000000000C")).Ok);

        Assert.Equal(["install_extension", "set_value"], core.Requests.Where(r => r is InstallExtensionRequest or SetValueRequest).Select(r => r.Type));
        var set = core.Requests.OfType<SetValueRequest>().Single();
        Assert.Equal(("ui.theme", "paper", "01J0000000000000000000000C"), (set.Path, set.Value.GetString(), set.Id));
        Assert.Equal(MarketAction.Installed, market.ActionFor(Paper));
        market.SetCurrentTheme("paper");
        Assert.Equal(MarketAction.Applied, market.ActionFor(Paper));
    }

    [Fact]
    public async Task Install_and_grant_grants_what_the_review_allowed_and_names_a_grant_that_failed()
    {
        var core = CoreWith(Index);
        var market = Model(core);
        await market.RefreshAsync();

        Assert.True((await market.InstallAndGrantAsync("hello", ["cmd:register", "events:emit"])).Ok);
        var grant = core.Requests.OfType<GrantCapabilitiesRequest>().Single();
        Assert.Equal("hello", grant.PluginId);
        Assert.Equal(["cmd:register", "events:emit"], grant.Capabilities);

        var refusing = Model(CoreWith(Index, request => request is GrantCapabilitiesRequest
            ? new ErrorReply(ErrorCodes.ConfigError, "cabinetos.json line 3: expected `,`")
            : null));
        await refusing.RefreshAsync();
        var outcome = await refusing.InstallAndGrantAsync("hello", ["cmd:register"]);
        Assert.Equal("Installed, but the grant failed: cabinetos.json line 3: expected `,`", outcome.Error);
        Assert.True(refusing.IsInstalled(Hello));

        // Nothing to grant: the install is the whole job.
        var plain = CoreWith(Index);
        var quiet = Model(plain);
        await quiet.RefreshAsync();
        Assert.True((await quiet.InstallAndGrantAsync("hello", [])).Ok);
        Assert.Empty(plain.Requests.OfType<GrantCapabilitiesRequest>());
    }

    [Fact]
    public async Task Uninstall_removes_it_from_the_installed_tab_and_leaves_what_the_marketplace_did_not_install()
    {
        var market = Model(CoreWith(Offered(("hello", "1.0.0")), request => request is UninstallExtensionRequest { ExtensionId: "nord" }
            ? new ErrorReply(ErrorCodes.NoSuchExtension, "the marketplace did not install nord")
            : null, plugins: ["hello"], themes: ["nord"]));
        await market.RefreshAsync();
        Assert.Equal(1, market.CountOf(MarketTabs.Installed));

        Assert.True((await market.UninstallAsync("hello")).Ok);
        Assert.False(market.IsInstalled(Hello));
        Assert.Equal(MarketAction.Install, market.ActionFor(Hello));
        Assert.Equal(0, market.CountOf(MarketTabs.Installed));

        var refused = await market.UninstallAsync("nord");
        Assert.Equal("Nord was not installed from the marketplace, so the marketplace leaves it alone.", refused.Error);
        Assert.True(market.IsPresent(Nord));
    }

    [Fact]
    public async Task Events_of_tools_and_plugins_keep_what_is_there_up_to_date()
    {
        var market = Model(CoreWith(Offered(("md-preview", "1.0.0"))));
        await market.RefreshAsync();
        Assert.True(market.IsInstalled(Preview));

        market.OnEvent(new ToolsChangedEvent([new ToolInfo("md-preview", "Markdown Preview", "1.0.0", "CabinetOS", "", @"C:\tools\md-preview")]));
        market.OnEvent(new PluginStateChangedEvent("hello", new PluginState(PluginState.NeedsReview, ["cmd:register"])));

        Assert.True(market.IsInstalled(Preview));
        // A plugin that appears is there; only the marketplace's record makes it the marketplace's.
        Assert.Equal((true, false, MarketAction.Installed), (market.IsPresent(Hello), market.IsInstalled(Hello), market.ActionFor(Hello)));

        // A tool removed elsewhere (the command line) leaves the Installed tab too.
        market.OnEvent(new ToolsChangedEvent([]));
        Assert.False(market.IsPresent(Preview));
        Assert.Equal(0, market.CountOf(MarketTabs.Installed));
    }

    [Fact]
    public async Task A_core_that_started_again_ends_the_installs_on_their_way()
    {
        var core = new SlowInstall(CoreWith(Index));
        var market = Model(core);
        await market.RefreshAsync();
        _ = market.InstallAsync("hello");

        market.Reset();

        Assert.Null(market.InstallOf("hello"));
        Assert.Equal(MarketStatus.Idle, market.Status);
    }

    [Theory]
    [InlineData("go.toPath", true)]
    [InlineData("view.toggleTerminal", true)]
    [InlineData("pane.openSelected", true)]
    [InlineData("file.copyToOtherPane", true)]
    [InlineData("edit.paste", true)]
    [InlineData("editor.openMarkdownPreview", true)]
    [InlineData("search.focus", true)]
    [InlineData("palette.show", false)]
    [InlineData("marketplace.browse", false)]
    [InlineData("overlay.close", false)]
    [InlineData("view.toggleSidebar", false)]
    [InlineData("preferences.selectColorTheme", false)]
    [InlineData("plugins.grant", false)]
    [InlineData("market.install", false)]
    public void A_command_on_the_panes_closes_the_marketplace_first(string command, bool closes) =>
        Assert.Equal(closes, MarketplaceModel.NeedsThePanes(command));

    [Fact]
    public void A_card_puts_the_item_into_words()
    {
        Assert.Equal(("WASM plugin", "Theme", "Tool"), (MarketText.KindLabel(Hello), MarketText.KindLabel(Nord), MarketText.KindLabel(Preview)));
        Assert.Equal("Arctic Ice Studio · v1.0.0", MarketText.Byline(Nord));
        Assert.Equal(("4.8", "(120)", "120 ratings", "5.4k", "· 5.4k installs"),
            (MarketText.Rating(Nord), MarketText.RatingCount(Nord), MarketText.RatingsLabel(Nord), MarketText.Installs(Nord), MarketText.InstallsLine(Nord)));
        Assert.Equal(("—", "no ratings", "no ratings", "—", ""),
            (MarketText.Rating(Hello), MarketText.RatingCount(Hello), MarketText.RatingsLabel(Hello), MarketText.Installs(Hello), MarketText.InstallsLine(Hello)));
        Assert.Equal("1 rating", MarketText.RatingsLabel(Hello with { Rating = new MarketRating(5, 1) }));
        Assert.Equal(("950", "12k", "1.2M"), (MarketText.Count(950), MarketText.Count(12_000), MarketText.Count(1_240_000)));
        Assert.Equal("1000 B", MarketText.Size(Hello));
        Assert.Equal(("Install", "Install and apply", "Update", "Installing…", "Installed", "Applied"),
            (MarketText.ActionText(MarketAction.Install), MarketText.ActionText(MarketAction.InstallAndApply), MarketText.ActionText(MarketAction.Update),
             MarketText.ActionText(MarketAction.Installing), MarketText.ActionText(MarketAction.Installed), MarketText.ActionText(MarketAction.Applied)));
    }

    [Fact]
    public void A_theme_tile_wears_its_accent_and_anything_else_a_colour_from_its_id()
    {
        Assert.Equal(new PluginTile("No", "#88C0D0"), MarketText.Tile(Nord));
        Assert.Equal(PluginTile.For("md-preview", "Markdown Preview"), MarketText.Tile(Preview));
        Assert.Equal("MP", MarketText.Tile(Preview).Text);
        // A theme without an accent follows Windows: the tile takes the ID's colour.
        Assert.Equal(PluginTile.For("paper", "Paper"), MarketText.Tile(Paper));
    }

    [Theory]
    [InlineData("https://www.nordtheme.com", true)]
    [InlineData("http://example.org/plugin", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("www.example.org", false)]
    [InlineData(null, false)]
    public void Source_opens_only_a_web_page(string? url, bool opens) =>
        Assert.Equal(opens, MarketText.SourceUri(Nord with { Author = Nord.Author with { Url = url } }) is not null);

    [Fact]
    public void The_review_before_an_install_shows_what_the_index_lists_and_allows_all_of_it()
    {
        var review = PermissionReview.ForInstall(Hello with
        {
            Capabilities = [new("cmd:register", "Adds the Say Hello command.", "low"), new("fs:read", "Reads its folder.", Roots: [@"%TEMP%\hello"])],
        });

        Assert.Equal("hello", review.Install!.Id);
        Assert.Equal("Hello by CabinetOS", review.Subtitle);
        Assert.Equal(["cmd:register", "fs:read"], review.ToGrant);
        Assert.Equal(PluginState.NeedsReview, review.Plugin.State.Type);
        Assert.Equal(("LOW", "UNKNOWN"), (review.Rows[0].LevelText, review.Rows[1].LevelText));
        Assert.Equal(@"Reads its folder. (%TEMP%\hello)", review.Rows[1].Detail);
        Assert.Null(new PermissionReview(review.Plugin).Install);
    }

    // A core whose install_extension waits until the test answers it.
    private sealed class SlowInstall(FakeChannel inner) : ICoreChannel
    {
        public TaskCompletionSource<CoreReply> Reply { get; } = new();

        public InstallExtensionRequest? Install { get; private set; }

        public Task<CoreReply> RequestAsync(CoreRequest request, CancellationToken cancellationToken = default)
        {
            if (request is not InstallExtensionRequest install)
            {
                return inner.RequestAsync(request, cancellationToken);
            }
            Install = install;
            return Reply.Task;
        }
    }
}
