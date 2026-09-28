using System.Buffers;
using System.Text.Json;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Market;

/// <summary>The marketplace's four tabs (design view C), in the nav's order.</summary>
public static class MarketTabs
{
    public const string Discover = "discover";
    public const string Plugins = "plugins";
    public const string Themes = "themes";
    public const string Installed = "installed";

    /// <summary>Every tab with its title.</summary>
    public static IReadOnlyList<(string Id, string Title)> All { get; } =
        [(Discover, "Discover"), (Plugins, "Plugins"), (Themes, "Themes"), (Installed, "Installed")];
}

/// <summary>Where the view is with the index.</summary>
public enum MarketStatus
{
    /// <summary>Not read yet.</summary>
    Idle,

    /// <summary>A <c>marketplace_refresh</c> is on its way.</summary>
    Loading,

    /// <summary>The index was read.</summary>
    Ready,

    /// <summary>The index could not be read; <see cref="MarketplaceModel.Notice"/> says why.</summary>
    Failed,

    /// <summary>The core has no marketplace (before protocol 10).</summary>
    Unavailable,
}

/// <summary>What the view shows instead of cards: a line saying what happened, and one saying what to do.</summary>
public sealed record MarketNotice(string Title, string Detail);

/// <summary>An install on its way: the bytes downloaded of the download's size.</summary>
public sealed record InstallProgress(ulong Bytes, ulong Total)
{
    /// <summary>From 0 to 1.</summary>
    public double Fraction => Total == 0 ? 0 : Math.Clamp((double)Bytes / Total, 0, 1);
}

/// <summary>The primary button of the detail column.</summary>
public enum MarketAction
{
    Install,
    InstallAndApply,
    Installing,
    Installed,
    Applied,
}

/// <summary>How an install, uninstall or apply ended: null <see cref="Error"/> when it worked.</summary>
public sealed record MarketOutcome(string? Error)
{
    public static MarketOutcome Done { get; } = new((string?)null);

    public bool Ok => Error is null;
}

/// <summary>
/// The marketplace view (design view C, docs/ui.md, "The marketplace"): the
/// index the core read, the tab and the search, the selected card, what is
/// installed, and the installs on their way. The core does every download,
/// check and file operation; this model only asks and follows its events.
/// </summary>
public sealed class MarketplaceModel(ICoreChannel core, Func<CancellationToken, Task>? debounce = null)
{
    /// <summary>How long typing must pause before the search goes to the core.</summary>
    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(150);

    private const string Target = "cabinetos_ui::market";

    private readonly Func<CancellationToken, Task> _debounce = debounce ?? (token => Task.Delay(SearchDelay, token));
    private readonly Dictionary<string, HashSet<string>> _installed = new(StringComparer.Ordinal)
    {
        [ExtensionKinds.Plugin] = new(StringComparer.Ordinal),
        [ExtensionKinds.Theme] = new(StringComparer.Ordinal),
        [ExtensionKinds.Tool] = new(StringComparer.Ordinal),
    };
    private readonly Dictionary<string, InstallProgress> _installs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private IReadOnlyList<MarketItem>? _hits;
    private CancellationTokenSource? _pendingSearch;
    private MarketNotice? _failure;
    private int _refresh;
    private int _search;

    /// <summary>Anything the view shows changed.</summary>
    public event Action? Changed;

    /// <summary>Where the view is with the index.</summary>
    public MarketStatus Status { get; private set; }

    /// <summary>The index's file or URL, once read.</summary>
    public string? Source { get; private set; }

    /// <summary>Every extension of the index, one per ID (its newest version), in the index's order.</summary>
    public IReadOnlyList<MarketItem> All { get; private set; } = [];

    /// <summary>The tab shown (<see cref="MarketTabs"/>).</summary>
    public string Tab { get; private set; } = MarketTabs.Discover;

    /// <summary>The search text, as typed.</summary>
    public string Query { get; private set; } = "";

    /// <summary>The ID of the card whose detail column is open, or null.</summary>
    public string? SelectedId { get; private set; }

    /// <summary>The card whose detail column is open, or null.</summary>
    public MarketItem? Selected => SelectedId is { } id ? Find(id) : null;

    /// <summary>The theme in effect, which the window keeps up to date: its card says "Applied".</summary>
    public string? CurrentThemeId { get; private set; }

    /// <summary>The cards of the tab: the search's hits (best first) or the whole index, then the tab's kind.</summary>
    public IReadOnlyList<MarketItem> Items =>
        (Query.Trim().Length > 0 && _hits is { } hits ? hits : All).Where(item => InTab(item, Tab)).ToList();

    /// <summary>The caption beside the search field.</summary>
    public string Caption => $"{Items.Count} results · WebAssembly, sandboxed";

    /// <summary>What to show instead of cards, or null when there are cards.</summary>
    public MarketNotice? Notice => Status switch
    {
        MarketStatus.Idle or MarketStatus.Loading => new MarketNotice("Reading the marketplace index…", Source ?? ""),
        MarketStatus.Failed or MarketStatus.Unavailable => _failure,
        _ when Items.Count > 0 => null,
        _ when All.Count == 0 => new MarketNotice("The index lists no extensions.", Source ?? ""),
        _ when Query.Trim().Length > 0 => new MarketNotice($"No results for “{Query.Trim()}”.", "The search looks at names, IDs and publishers."),
        _ when Tab == MarketTabs.Installed => new MarketNotice("Nothing from this index is installed.", "Install from Discover, Plugins or Themes."),
        _ => new MarketNotice($"The index has no {TitleOf(Tab).ToLowerInvariant()}.", Source ?? ""),
    };

    /// <summary>The extension with that ID, or null.</summary>
    public MarketItem? Find(string id) => All.FirstOrDefault(item => item.Id == id)
        ?? _hits?.FirstOrDefault(item => item.Id == id);

    /// <summary>How many cards <paramref name="tab"/> has, without the search.</summary>
    public int CountOf(string tab) => All.Count(item => InTab(item, tab));

    /// <summary>Whether an extension with the item's kind and ID is installed (whoever installed it).</summary>
    public bool IsInstalled(MarketItem item) => _installed.TryGetValue(item.Kind, out var ids) && ids.Contains(item.Id);

    /// <summary>The download of an install on its way, or null.</summary>
    public InstallProgress? InstallOf(string id) => _installs.GetValueOrDefault(id);

    /// <summary>Why the last install, uninstall or apply of <paramref name="id"/> failed, or null.</summary>
    public string? ErrorOf(string id) => _errors.GetValueOrDefault(id);

    /// <summary>What the detail column's primary button says and does.</summary>
    public MarketAction ActionFor(MarketItem item) => item switch
    {
        _ when _installs.ContainsKey(item.Id) => MarketAction.Installing,
        { Kind: ExtensionKinds.Theme } when item.Id == CurrentThemeId => MarketAction.Applied,
        _ when IsInstalled(item) => MarketAction.Installed,
        { Kind: ExtensionKinds.Theme } => MarketAction.InstallAndApply,
        _ => MarketAction.Install,
    };

    /// <summary>
    /// Reads the index (<c>marketplace_refresh</c>) and what is installed.
    /// False when the index could not be read; <see cref="Notice"/> says why.
    /// </summary>
    public async Task<bool> RefreshAsync(string? requestId = null)
    {
        var refresh = ++_refresh;
        Status = MarketStatus.Loading;
        _failure = null;
        Changed?.Invoke();
        CoreReply reply;
        try
        {
            var installed = ReadInstalledAsync();
            reply = await core.RequestAsync(new MarketplaceRefreshRequest { Id = requestId ?? "" });
            await installed;
        }
        catch (IOException error)
        {
            return refresh == _refresh && Fail(MarketStatus.Failed, new MarketNotice("Cannot read the marketplace index.", error.Message));
        }
        if (refresh != _refresh)
        {
            return false;
        }
        switch (reply)
        {
            case MarketplaceIndexReply index:
                All = Newest(index.Items);
                Source = index.Source;
                Status = MarketStatus.Ready;
                Diag.Info(Target, "marketplace index read", new LogField("source", index.Source), new LogField("items", All.Count));
                Changed?.Invoke();
                if (Query.Trim().Length > 0)
                {
                    await SearchNowAsync(Query, ++_search);
                }
                return true;
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                return Fail(MarketStatus.Unavailable, new MarketNotice(
                    "This core has no marketplace yet.",
                    "It does not know marketplace_refresh: it is older than protocol version 10."));
            case ErrorReply error:
                return Fail(MarketStatus.Failed, await DescribeFailureAsync(error));
            default:
                return false;
        }
    }

    /// <summary>
    /// Reads which plugins, themes and tools are installed (<c>list_plugins</c>,
    /// <c>list_themes</c>, <c>list_tools</c>); a kind the core cannot list
    /// counts as none installed.
    /// </summary>
    public async Task ReadInstalledAsync()
    {
        var plugins = ListAsync(new ListPluginsRequest(), reply => (reply as PluginsReply)?.Plugins.Select(p => p.Id));
        var themes = ListAsync(new ListThemesRequest(), reply => (reply as ThemesReply)?.Themes.Select(t => t.Id));
        var tools = ListAsync(new ListToolsRequest(), reply => (reply as ToolsReply)?.Tools.Select(t => t.Id));
        await Task.WhenAll(plugins, tools, themes);
        Replace(ExtensionKinds.Plugin, await plugins);
        Replace(ExtensionKinds.Theme, await themes);
        Replace(ExtensionKinds.Tool, await tools);
        Changed?.Invoke();
    }

    /// <summary>Shows <paramref name="tab"/>; the detail column closes, as in the design.</summary>
    public void SetTab(string tab)
    {
        if (Tab == tab || MarketTabs.All.All(t => t.Id != tab))
        {
            return;
        }
        Tab = tab;
        SelectedId = null;
        Changed?.Invoke();
    }

    /// <summary>Opens the detail column of <paramref name="id"/>, or closes it (null).</summary>
    public void Select(string? id)
    {
        if (SelectedId != id && (id is null || Find(id) is not null))
        {
            SelectedId = id;
            Changed?.Invoke();
        }
    }

    /// <summary>The theme in effect changed (the window applied one).</summary>
    public void SetCurrentTheme(string? themeId)
    {
        if (CurrentThemeId != themeId)
        {
            CurrentThemeId = themeId;
            if (themeId is not null)
            {
                _installed[ExtensionKinds.Theme].Add(themeId);
            }
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The search field changed: once typing pauses for 150 ms, the core
    /// searches the index (<c>marketplace_search</c>). Only the newest text's
    /// hits are shown; an empty field shows the whole index again.
    /// </summary>
    public async Task QueryChangedAsync(string text)
    {
        Query = text;
        _pendingSearch?.Cancel();
        var search = ++_search;
        if (text.Trim().Length == 0)
        {
            _hits = null;
            Changed?.Invoke();
            return;
        }
        var pending = new CancellationTokenSource();
        _pendingSearch = pending;
        try
        {
            await _debounce(pending.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (search == _search)
        {
            await SearchNowAsync(text, search);
        }
    }

    /// <summary>
    /// Installs <paramref name="id"/> (<c>install_extension</c>). The progress
    /// comes as events (<see cref="OnEvent"/>); the reply ends it.
    /// </summary>
    public async Task<MarketOutcome> InstallAsync(string id, string? requestId = null)
    {
        if (_installs.ContainsKey(id))
        {
            return new MarketOutcome("It is being installed already.");
        }
        var item = Find(id);
        _installs[id] = new InstallProgress(0, item?.Size ?? 0);
        _errors.Remove(id);
        Changed?.Invoke();
        CoreReply reply;
        try
        {
            reply = await core.RequestAsync(new InstallExtensionRequest(id) { Id = requestId ?? "" });
        }
        catch (IOException error)
        {
            return Failed(id, error.Message);
        }
        switch (reply)
        {
            case OkReply:
                _installs.Remove(id);
                MarkInstalled(item?.Kind, id, installed: true);
                Changed?.Invoke();
                return MarketOutcome.Done;
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                return Failed(id, "This core cannot install extensions yet (install_extension).");
            case ErrorReply error:
                return Failed(id, error.Message);
            default:
                return Failed(id, $"Unexpected reply {reply.GetType().Name}.");
        }
    }

    /// <summary>Installs a theme, then makes it the theme in effect (<c>set_value ui.theme</c>).</summary>
    public async Task<MarketOutcome> InstallAndApplyAsync(string id, string? requestId = null)
    {
        var installed = await InstallAsync(id, requestId);
        return installed.Ok ? await ApplyThemeAsync(id, requestId) : installed;
    }

    /// <summary>
    /// Installs a plugin, then grants what the user allowed in the review
    /// (<c>grant_capabilities</c>), so the core starts it. The core installs
    /// every plugin waiting for review; the grant is what lets it run.
    /// </summary>
    public async Task<MarketOutcome> InstallAndGrantAsync(string id, IReadOnlyList<string> capabilities, string? requestId = null)
    {
        var installed = await InstallAsync(id, requestId);
        if (!installed.Ok || capabilities.Count == 0)
        {
            return installed;
        }
        try
        {
            switch (await core.RequestAsync(new GrantCapabilitiesRequest(id, capabilities) { Id = requestId ?? "" }))
            {
                case OkReply:
                    return MarketOutcome.Done;
                case ErrorReply error:
                    return Failed(id, $"Installed, but the grant failed: {error.Message}");
                default:
                    return Failed(id, "Installed, but the grant got an unexpected reply.");
            }
        }
        catch (IOException error)
        {
            return Failed(id, $"Installed, but the grant failed: {error.Message}");
        }
    }

    /// <summary>Makes the theme <paramref name="id"/> the theme in effect; <c>theme_changed</c> follows.</summary>
    public async Task<MarketOutcome> ApplyThemeAsync(string id, string? requestId = null)
    {
        try
        {
            return await core.RequestAsync(new SetValueRequest("ui.theme", Text(id)) { Id = requestId ?? "" }) switch
            {
                OkReply => MarketOutcome.Done,
                ErrorReply error => Failed(id, error.Message),
                var other => Failed(id, $"Unexpected reply {other.GetType().Name}."),
            };
        }
        catch (IOException error)
        {
            return Failed(id, error.Message);
        }
    }

    /// <summary>Removes what the marketplace installed for <paramref name="id"/> (<c>uninstall_extension</c>).</summary>
    public async Task<MarketOutcome> UninstallAsync(string id, string? requestId = null)
    {
        var item = Find(id);
        _errors.Remove(id);
        CoreReply reply;
        try
        {
            reply = await core.RequestAsync(new UninstallExtensionRequest(id) { Id = requestId ?? "" });
        }
        catch (IOException error)
        {
            return Failed(id, error.Message);
        }
        switch (reply)
        {
            case OkReply:
                MarkInstalled(item?.Kind, id, installed: false);
                Changed?.Invoke();
                return MarketOutcome.Done;
            case ErrorReply { Code: ErrorCodes.NoSuchExtension }:
                // Trust rule 7: what the user put there by hand, or what ships, is not the marketplace's to remove.
                return Failed(id, $"{item?.Name ?? id} was not installed from the marketplace, so the marketplace leaves it alone.");
            case ErrorReply error:
                return Failed(id, error.Message);
            default:
                return Failed(id, $"Unexpected reply {reply.GetType().Name}.");
        }
    }

    /// <summary>Follows the core's events: install progress and its end, tools, plugins and the theme.</summary>
    public void OnEvent(CoreEvent coreEvent)
    {
        switch (coreEvent)
        {
            case InstallProgressEvent progress:
                // Every client gets it, whichever asked: an install from the command line shows too.
                _installs[progress.ExtensionId] = new InstallProgress(progress.Bytes, progress.Total);
                Changed?.Invoke();
                break;
            case InstallFinishedEvent finished:
                // It and the reply travel apart; whichever comes first ends the install.
                _installs.Remove(finished.ExtensionId);
                if (finished.Ok)
                {
                    MarkInstalled(Find(finished.ExtensionId)?.Kind, finished.ExtensionId, installed: true);
                }
                else
                {
                    _errors[finished.ExtensionId] = finished.Message;
                }
                Changed?.Invoke();
                break;
            case ToolsChangedEvent tools:
                Replace(ExtensionKinds.Tool, tools.Tools.Select(t => t.Id));
                Changed?.Invoke();
                break;
            case PluginStateChangedEvent plugin:
                if (_installed[ExtensionKinds.Plugin].Add(plugin.PluginId))
                {
                    Changed?.Invoke();
                }
                break;
            case ThemeChangedEvent theme:
                SetCurrentTheme(theme.Theme.Id);
                break;
        }
    }

    /// <summary>The core started again: what was on its way ended with it.</summary>
    public void Reset()
    {
        _installs.Clear();
        _refresh++;
        _search++;
        Status = MarketStatus.Idle;
        Changed?.Invoke();
    }

    /// <summary>
    /// Whether running <paramref name="commandId"/> needs the file panes,
    /// which the marketplace covers: it closes first, so what the command
    /// does can be seen.
    /// </summary>
    public static bool NeedsThePanes(string commandId) =>
        commandId is "view.toggleTerminal" or "view.toggleDualPane" or "view.focusOtherPane"
        || commandId.StartsWith("pane.", StringComparison.Ordinal)
        || commandId.StartsWith("file.", StringComparison.Ordinal)
        || commandId.StartsWith("edit.", StringComparison.Ordinal)
        || commandId.StartsWith("go.", StringComparison.Ordinal)
        || commandId.StartsWith("search.", StringComparison.Ordinal)
        || commandId.StartsWith("terminal.", StringComparison.Ordinal)
        || commandId.StartsWith("editor.", StringComparison.Ordinal);

    // Plugins and Tool Extensions both add function, so the Plugins tab has both; the chip tells them apart.
    private bool InTab(MarketItem item, string tab) => tab switch
    {
        MarketTabs.Plugins => item.Kind is ExtensionKinds.Plugin or ExtensionKinds.Tool,
        MarketTabs.Themes => item.Kind == ExtensionKinds.Theme,
        MarketTabs.Installed => IsInstalled(item),
        _ => true,
    };

    private async Task SearchNowAsync(string text, int search)
    {
        CoreReply reply;
        try
        {
            reply = await core.RequestAsync(new MarketplaceSearchRequest(text.Trim()));
        }
        catch (IOException error)
        {
            Diag.Info(Target, "marketplace search failed", new LogField("error", error.Message));
            return;
        }
        if (search != _search)
        {
            return;
        }
        switch (reply)
        {
            case MarketplaceIndexReply hits:
                _hits = Newest(hits.Items);
                Changed?.Invoke();
                break;
            case ErrorReply error:
                Diag.Info(Target, "marketplace search failed", new LogField("code", error.Code), new LogField("error", error.Message));
                _hits = [];
                Changed?.Invoke();
                break;
        }
    }

    // The core's words for why the index could not be read, with the setting that decides it.
    private async Task<MarketNotice> DescribeFailureAsync(ErrorReply error)
    {
        string? index = null;
        try
        {
            if (await core.RequestAsync(new GetValueRequest("marketplace.index")) is ValueReply { Value.ValueKind: JsonValueKind.String } value)
            {
                index = value.Value.GetString();
            }
        }
        catch (IOException)
        {
            // Only the words get less precise.
        }
        if (string.IsNullOrWhiteSpace(index))
        {
            return new MarketNotice("No marketplace index is set.",
                "Set marketplace.index in cabinetos.json to the folder, file or https: URL of an index.json.");
        }
        if (Uri.TryCreate(index, UriKind.Absolute, out var uri) && uri.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase))
        {
            // docs/marketplace.md, "Not yet": the default index is a placeholder that never resolves.
            return new MarketNotice("The marketplace index is not published yet.",
                $"marketplace.index is the placeholder {index}. To try the marketplace, set it to a local index; sdk/marketplace/build-index.ps1 builds one.");
        }
        return new MarketNotice("Cannot read the marketplace index.", error.Message);
    }

    private bool Fail(MarketStatus status, MarketNotice notice)
    {
        Status = status;
        _failure = notice;
        Diag.Info(Target, "marketplace index not read", new LogField("status", status.ToString()), new LogField("reason", notice.Detail));
        Changed?.Invoke();
        return false;
    }

    private MarketOutcome Failed(string id, string message)
    {
        _installs.Remove(id);
        _errors[id] = message;
        Changed?.Invoke();
        return new MarketOutcome(message);
    }

    private async Task<IEnumerable<string>?> ListAsync(CoreRequest request, Func<CoreReply, IEnumerable<string>?> ids)
    {
        try
        {
            return ids(await core.RequestAsync(request));
        }
        catch (IOException error)
        {
            // Nothing of that kind shows as installed until the next look.
            Diag.Info(Target, "cannot list what is installed", new LogField("request", request.Type), new LogField("error", error.Message));
            return null;
        }
    }

    private void Replace(string kind, IEnumerable<string>? ids)
    {
        var set = _installed[kind];
        set.Clear();
        set.UnionWith(ids ?? []);
        if (kind == ExtensionKinds.Theme && CurrentThemeId is { } current)
        {
            set.Add(current);
        }
    }

    private void MarkInstalled(string? kind, string id, bool installed)
    {
        if (kind is null || !_installed.TryGetValue(kind, out var ids))
        {
            return;
        }
        if (installed)
        {
            ids.Add(id);
        }
        else
        {
            ids.Remove(id);
        }
    }

    private static string TitleOf(string tab) => MarketTabs.All.FirstOrDefault(t => t.Id == tab).Title ?? tab;

    // An index may list several versions of one ID; a card shows the newest, where the ID first appears.
    private static IReadOnlyList<MarketItem> Newest(IReadOnlyList<MarketItem> items)
    {
        var newest = new Dictionary<string, MarketItem>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var item in items)
        {
            if (!newest.TryGetValue(item.Id, out var seen))
            {
                order.Add(item.Id);
                newest[item.Id] = item;
            }
            else if (CompareVersions(item.Version, seen.Version) > 0)
            {
                newest[item.Id] = item;
            }
        }
        return order.Select(id => newest[id]).ToList();
    }

    private static int CompareVersions(string a, string b) =>
        Version.TryParse(a, out var left) && Version.TryParse(b, out var right)
            ? left.CompareTo(right)
            : string.CompareOrdinal(a, b);

    private static JsonElement Text(string value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(value);
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
