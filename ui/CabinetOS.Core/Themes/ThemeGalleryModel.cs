using System.Globalization;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Market;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Themes;

/// <summary>The gallery's filter chips (docs/ui.md, "The theme gallery").</summary>
public enum GalleryFilter
{
    /// <summary>Every theme.</summary>
    All,

    /// <summary>Themes for a dark window.</summary>
    Dark,

    /// <summary>Themes for a light window.</summary>
    Light,

    /// <summary>Themes that follow Windows' light or dark mode.</summary>
    System,

    /// <summary>Density presets: themes that set the window's sizes, like Commander Compact.</summary>
    Density,

    /// <summary>The themes in the themes folder, whoever put them there.</summary>
    Installed,
}

/// <summary>What a tile does when it is chosen (Enter, double-click, its button).</summary>
public enum TileAction
{
    /// <summary>Download the theme and make it the theme in effect.</summary>
    Install,

    /// <summary>Make an installed theme the theme in effect.</summary>
    Apply,

    /// <summary>Install the newer version the catalogue offers (the core applies it again when it is in effect).</summary>
    Update,

    /// <summary>The download is on its way.</summary>
    Installing,

    /// <summary>It is the theme in effect: nothing to do.</summary>
    Applied,
}

/// <summary>
/// The three colours a tile is painted with: the colour behind a file list, the text of its rows, and the accent
/// (docs/themes.md, "The gallery's tile"). All opaque.
/// </summary>
public sealed record TileColors(Argb Background, Argb Text, Argb Accent)
{
    private static readonly Argb DarkMica = new(0xFF, 0x20, 0x20, 0x20);
    private static readonly Argb LightMica = new(0xFF, 0xF3, 0xF3, 0xF3);
    private static readonly Argb LightText = new(0xFF, 0x1B, 0x1B, 0x1B);

    /// <summary>The catalogue's colours for the theme, or null when the item has none or one cannot be read.</summary>
    public static TileColors? From(MarketTile? tile) =>
        tile is not null && Argb.TryParse(tile.Background, out var background) && Argb.TryParse(tile.Text, out var text) && Argb.TryParse(tile.Accent, out var accent)
            ? new TileColors(Opaque(background), Opaque(text), Opaque(accent))
            : null;

    /// <summary>
    /// A theme of kind <c>system</c> in the mode Windows is in now: its dark colours are the design's, its light
    /// ones the window's own (<see cref="SystemLight"/>), and its accent is Windows' (docs/ui.md, "Light mode of a
    /// <c>system</c> theme").
    /// </summary>
    public static TileColors ForSystem(bool light, Argb accent)
    {
        if (!light)
        {
            // The default theme's layer (white at 5 %) over dark Mica.
            return new TileColors(DarkMica.Mix(Argb.White, 0x0D / 255.0), Argb.White, Opaque(accent));
        }
        Argb.TryParse(SystemLight.Palette.LayerFill, out var layer);
        return new TileColors(LightMica.Mix(Opaque(layer), layer.Opacity), LightText, Opaque(accent));
    }

    /// <summary>
    /// A tile for an installed theme the catalogue does not list (or while the catalogue cannot be read), from
    /// what <c>list_themes</c> says: the Mica tint of the theme, light or dark text by its kind, its accent.
    /// </summary>
    public static TileColors FromInfo(ThemeInfo info, bool systemIsLight, Argb systemAccent)
    {
        var accent = info.Accent is { } own && Argb.TryParse(own, out var parsed) ? Opaque(parsed) : Opaque(systemAccent);
        if (info.Kind == ColorTheme.System)
        {
            return ForSystem(systemIsLight, accent);
        }
        var light = info.Kind == ColorTheme.Light;
        var mica = light ? LightMica : DarkMica;
        if (info.Mica is { } tint && Argb.TryParse(tint.Tint, out var color))
        {
            mica = mica.Mix(Opaque(color), Math.Clamp(tint.Opacity, 0, 1));
        }
        return new TileColors(mica, light ? LightText : Argb.White, accent);
    }

    private static Argb Opaque(Argb color) => color with { A = 0xFF };
}

/// <summary>
/// One tile of the gallery: a theme painted in its own colours, with what it can do now. <see cref="Item"/> is the
/// catalogue's item, null for an installed theme the catalogue does not list.
/// </summary>
public sealed record ThemeTile(
    string Id,
    string Name,
    string Author,
    string Appearance,
    bool Compact,
    TileColors Colors,
    bool IsPresent,
    bool IsApplied,
    TileAction Action,
    bool CanRemove,
    string StateText,
    MarketItem? Item)
{
    /// <summary>The small mark under the name: "Dark", "Light" or "System".</summary>
    public string Mark => Appearance switch
    {
        ColorTheme.Light => "Light",
        ColorTheme.System => "System",
        _ => "Dark",
    };

    /// <summary>What a screen reader says for the tile.</summary>
    public string AutomationName =>
        $"{Name}, by {Author}, {Mark.ToLowerInvariant()}{(Compact ? ", compact" : "")}{(IsApplied ? ", applied" : IsPresent ? ", installed" : "")}";
}

/// <summary>
/// The theme gallery (<c>themes.browse</c>, docs/ui.md, "The theme gallery"): the themes catalogue as colour
/// tiles, with the filter chips, the search, the selection the keys move, and the live preview of the selected
/// tile on the whole window. It wraps a <see cref="MarketplaceModel"/> of the themes catalogue, which does the
/// reading, the installs and the events, and adds what the themes folder has (<c>list_themes</c>), so the gallery
/// still shows the installed themes while the catalogue cannot be read. A theme that is not installed is
/// previewed with <c>preview_theme</c> (the core downloads it into memory, installs nothing); an installed one
/// with <c>get_theme</c>. Nothing is written until a tile is applied: <c>set_value ui.theme</c>.
/// </summary>
public sealed class ThemeGalleryModel
{
    private const string Target = "cabinetos_ui::theme";

    private readonly ICoreChannel _core;
    private readonly Func<bool> _systemIsLight;
    private readonly Func<Argb> _systemAccent;
    private IReadOnlyList<ThemeTile> _tiles = [];
    private string? _selectedId;
    private int _load;
    private int _preview;
    private bool _previews;

    /// <summary>Makes the gallery for a core; <paramref name="systemIsLight"/> and <paramref name="systemAccent"/> are the window's.</summary>
    public ThemeGalleryModel(ICoreChannel core, Func<bool>? systemIsLight = null, Func<Argb>? systemAccent = null, Func<CancellationToken, Task>? debounce = null)
    {
        _core = core;
        _systemIsLight = systemIsLight ?? (() => false);
        _systemAccent = systemAccent ?? (() => new Argb(0xFF, 0x60, 0xCD, 0xFF));
        Market = new MarketplaceModel(core, debounce, Catalogues.Themes);
        Market.Changed += () =>
        {
            Rebuild();
            Changed?.Invoke();
        };
    }

    /// <summary>The tiles, the filter, the selection or a notice changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// The window is to paint this theme as a preview, or, with null, the theme in effect again. Raised on the
    /// thread that moved the selection.
    /// </summary>
    public event Action<ColorTheme?>? Preview;

    /// <summary>The themes catalogue, read and followed by the core's events; the window sends it the events.</summary>
    public MarketplaceModel Market { get; }

    /// <summary>How long the selection must rest on a tile before its theme is fetched.</summary>
    public TimeSpan PreviewDelay { get; set; } = TimeSpan.FromMilliseconds(80);

    /// <summary>The filter chip in effect.</summary>
    public GalleryFilter Filter { get; private set; }

    /// <summary>The search text, as typed.</summary>
    public string Query => Market.Query;

    /// <summary>Whether the window shows a previewed theme rather than the theme in effect.</summary>
    public bool IsPreviewShown { get; private set; }

    /// <summary>The name of the theme the window previews now, or null; the status bar says "Previewing &lt;name&gt;".</summary>
    public string? PreviewingName { get; private set; }

    /// <summary>Why the last preview, install or apply failed, for the line under the toolbar; null when it did not.</summary>
    public string? Error { get; private set; }

    /// <summary>The theme in effect, which the window keeps up to date.</summary>
    public string? CurrentThemeId => Market.CurrentThemeId;

    /// <summary>The tiles in the order shown: the catalogue's (or the search's), then the installed themes it does not list.</summary>
    public IReadOnlyList<ThemeTile> Tiles => _tiles;

    /// <summary>The selected tile's row in <see cref="Tiles"/>, or -1.</summary>
    public int Selected
    {
        get
        {
            for (var row = 0; row < _tiles.Count; row++)
            {
                if (_tiles[row].Id == _selectedId)
                {
                    return row;
                }
            }
            return -1;
        }
    }

    /// <summary>The selected tile, or null.</summary>
    public ThemeTile? SelectedTile
    {
        get
        {
            var row = Selected;
            return row >= 0 ? _tiles[row] : null;
        }
    }

    /// <summary>
    /// The one line shown when the catalogue could not be read ("Cannot read the themes catalogue."), with what
    /// the gallery shows instead: the installed themes. Null when it was read, or not read yet.
    /// </summary>
    public string? CatalogueNotice => Market.Status is MarketStatus.Failed or MarketStatus.Unavailable
        ? $"{Market.Notice?.Title ?? "Cannot read the themes catalogue."} Showing the installed themes."
        : null;

    /// <summary>
    /// What the chips are, in order: All first, then Dark and Light with the one for the mode Windows is in now
    /// before the other, then System, Density presets and Installed.
    /// </summary>
    public IReadOnlyList<GalleryFilter> FilterOrder => _systemIsLight()
        ? [GalleryFilter.All, GalleryFilter.Light, GalleryFilter.Dark, GalleryFilter.System, GalleryFilter.Density, GalleryFilter.Installed]
        : [GalleryFilter.All, GalleryFilter.Dark, GalleryFilter.Light, GalleryFilter.System, GalleryFilter.Density, GalleryFilter.Installed];

    /// <summary>The chip's words.</summary>
    public static string TitleOf(GalleryFilter filter) => filter switch
    {
        GalleryFilter.Dark => "Dark",
        GalleryFilter.Light => "Light",
        GalleryFilter.System => "System",
        GalleryFilter.Density => "Density presets",
        GalleryFilter.Installed => "Installed",
        _ => "All",
    };

    /// <summary>The line the status bar shows while a theme is previewed, or null.</summary>
    public string? PreviewText => PreviewingName is { } name ? $"Previewing {name} · Esc restores" : null;

    /// <summary>
    /// The gallery opened: reads the catalogue (<c>marketplace_refresh</c> of the themes) and the themes folder
    /// (<c>list_themes</c>), shows All, and selects the theme in effect. False when the catalogue could not be read
    /// (<see cref="CatalogueNotice"/> says so; the installed themes still show).
    /// </summary>
    public async Task<bool> LoadAsync(string? currentThemeId, string? requestId = null)
    {
        var load = ++_load;
        Error = null;
        Filter = GalleryFilter.All;
        _selectedId = currentThemeId;
        Market.SetCurrentTheme(currentThemeId);
        var ok = await Market.RefreshAsync(requestId);
        if (load == _load)
        {
            // The market read the themes folder with the catalogue; its own Changed events built the tiles.
            Rebuild();
            Changed?.Invoke();
        }
        return ok;
    }

    /// <summary>Follows the core's events: the install and the theme in effect (<c>theme_changed</c> ends a preview).</summary>
    public void OnEvent(CoreEvent coreEvent)
    {
        if (coreEvent is ThemeChangedEvent)
        {
            // The window shows that theme now: no preview is on screen any more, and one on its way is dropped.
            _preview++;
            IsPreviewShown = false;
            PreviewingName = null;
        }
        Market.OnEvent(coreEvent);
    }

    /// <summary>The core started again: what was on its way ended with it.</summary>
    public void Reset()
    {
        _load++;
        _preview++;
        Market.Reset();
    }

    /// <summary>Shows the tiles of <paramref name="filter"/>; the selection stays on its tile when that is still shown.</summary>
    public void SetFilter(GalleryFilter filter)
    {
        if (Filter == filter)
        {
            return;
        }
        Filter = filter;
        Rebuild();
        Changed?.Invoke();
        SchedulePreview();
    }

    /// <summary>The search field changed; the core searches the catalogue once typing pauses.</summary>
    public Task QueryChangedAsync(string text) => Market.QueryChangedAsync(text);

    /// <summary>Puts the selection on row <paramref name="row"/> of <see cref="Tiles"/>.</summary>
    public void Select(int row)
    {
        if ((uint)row >= (uint)_tiles.Count || _tiles[row].Id == _selectedId)
        {
            return;
        }
        _selectedId = _tiles[row].Id;
        Error = null;
        Changed?.Invoke();
        SchedulePreview();
    }

    /// <summary>
    /// Moves the selection across a grid of <paramref name="columns"/>: <paramref name="dx"/> tiles along the row
    /// and <paramref name="dy"/> rows down. It stays on the grid, and one row down from a short last row lands on
    /// its last tile. With nothing selected, the first tile is.
    /// </summary>
    public void Move(int dx, int dy, int columns)
    {
        if (_tiles.Count == 0)
        {
            return;
        }
        var from = Selected;
        if (from < 0)
        {
            Select(0);
            return;
        }
        columns = Math.Max(1, columns);
        var target = from + dx + (dy * columns);
        if (dy != 0 && dx == 0 && (uint)target >= (uint)_tiles.Count)
        {
            // Down from the row above a short last row, or up from the first row: the last tile, or none.
            target = dy > 0 && from / columns < (_tiles.Count - 1) / columns ? _tiles.Count - 1 : from;
        }
        Select(Math.Clamp(target, 0, _tiles.Count - 1));
    }

    /// <summary>The first tile.</summary>
    public void Home() => Select(0);

    /// <summary>The last tile.</summary>
    public void End() => Select(_tiles.Count - 1);

    /// <summary>The gallery opened: from now on the selected tile is previewed.</summary>
    public void BeginPreviews() => _previews = true;

    /// <summary>
    /// The gallery closed. With <paramref name="restore"/> the theme in effect is painted again if a preview is
    /// shown (Esc, another view taking the place); without it the preview stays on screen for the
    /// <c>theme_changed</c> that makes it the theme in effect. A preview on its way is dropped either way.
    /// </summary>
    public void EndPreviews(bool restore)
    {
        _previews = false;
        _preview++;
        var shown = IsPreviewShown;
        IsPreviewShown = false;
        PreviewingName = null;
        if (restore && shown)
        {
            Preview?.Invoke(null);
        }
    }

    /// <summary>
    /// Enter or a double-click: installs and applies the selected theme, or applies it when it is installed, or
    /// installs its update. Null when there is nothing to do (no tile, or it is the theme in effect).
    /// </summary>
    public Task<MarketOutcome?> ActivateSelectedAsync(string? requestId = null) =>
        SelectedTile is { } tile ? ActivateAsync(tile.Id, requestId) : Task.FromResult<MarketOutcome?>(null);

    /// <summary>What <see cref="ActivateSelectedAsync"/> does, for the tile <paramref name="id"/>.</summary>
    public async Task<MarketOutcome?> ActivateAsync(string id, string? requestId = null)
    {
        var tile = _tiles.FirstOrDefault(t => t.Id == id);
        if (tile is null)
        {
            return null;
        }
        return tile.Action switch
        {
            TileAction.Install => await FinishAsync(Market.InstallAndApplyAsync(id, requestId)),
            TileAction.Update => await FinishAsync(Market.InstallAsync(id, requestId)),
            TileAction.Apply => await FinishAsync(Market.ApplyThemeAsync(id, requestId)),
            _ => null,
        };
    }

    /// <summary>Installs a theme without applying it.</summary>
    public Task<MarketOutcome> InstallAsync(string id, string? requestId = null) => FinishAsync(Market.InstallAsync(id, requestId));

    /// <summary>Applies an installed theme: <c>set_value ui.theme</c>.</summary>
    public Task<MarketOutcome> ApplyAsync(string id, string? requestId = null) => FinishAsync(Market.ApplyThemeAsync(id, requestId));

    /// <summary>Removes a theme the marketplace installed (the core refuses the theme in effect).</summary>
    public Task<MarketOutcome> RemoveAsync(string id, string? requestId = null) => FinishAsync(Market.UninstallAsync(id, requestId));

    private async Task<MarketOutcome> FinishAsync(Task<MarketOutcome> action)
    {
        var outcome = await action;
        Error = outcome.Error;
        Rebuild();
        Changed?.Invoke();
        return outcome;
    }

    // The tiles: the catalogue's items, then the installed themes it does not list, through the filter.
    private void Rebuild()
    {
        var light = _systemIsLight();
        var accent = _systemAccent();
        var tiles = new List<ThemeTile>();
        var listed = Market.All.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (Market.Status == MarketStatus.Ready)
        {
            tiles.AddRange(Market.Items.Select(item => TileOf(item, light, accent)));
        }
        var query = Market.Query.Trim();
        foreach (var info in Market.InstalledThemes.Where(info => !listed.Contains(info.Id) && Matches(info, query)))
        {
            tiles.Add(TileOf(info, light, accent));
        }
        // A selection whose tile the filter hides stays remembered (Selected is -1 meanwhile) and shows again with it.
        _tiles = [.. tiles.Where(IsShown)];
    }

    private bool IsShown(ThemeTile tile) => Filter switch
    {
        GalleryFilter.Dark => tile.Appearance == ColorTheme.Dark,
        GalleryFilter.Light => tile.Appearance == ColorTheme.Light,
        GalleryFilter.System => tile.Appearance == ColorTheme.System,
        GalleryFilter.Density => tile.Compact,
        GalleryFilter.Installed => tile.IsPresent,
        _ => true,
    };

    private static bool Matches(ThemeInfo info, string query) =>
        query.Length == 0
        || info.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || info.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
        || info.Author.Contains(query, StringComparison.OrdinalIgnoreCase);

    private ThemeTile TileOf(MarketItem item, bool light, Argb accent)
    {
        var appearance = item.Appearance is ColorTheme.Light or ColorTheme.System ? item.Appearance : ColorTheme.Dark;
        // A system theme is painted as Windows is now, in the accent it names or else Windows'; any other theme in
        // the colours the catalogue gives, or, for an index without tiles, from what the item says of it.
        var colors = appearance == ColorTheme.System
            ? TileColors.ForSystem(light, ManifestAccent(item) ?? accent)
            : TileColors.From(item.Tile)
                ?? TileColors.FromInfo(new ThemeInfo(item.Id, item.Name, item.Author.Name, item.Version, appearance, ManifestAccent(item) is { } own ? $"#{own.R:X2}{own.G:X2}{own.B:X2}" : null), light, accent);
        var action = Market.ActionFor(item);
        var applied = item.Id == Market.CurrentThemeId;
        var tileAction = action switch
        {
            MarketAction.Installing => TileAction.Installing,
            MarketAction.Update => TileAction.Update,
            _ when applied => TileAction.Applied,
            MarketAction.Installed or MarketAction.Applied => TileAction.Apply,
            _ => TileAction.Install,
        };
        return new ThemeTile(item.Id, item.Name, item.Author.Name, appearance, item.Density == true, colors, Market.IsPresent(item), applied,
            tileAction, Market.CanUninstall(item), StateOf(item, tileAction), item);
    }

    // The accent the theme file names (the item's manifest header), or null when it follows Windows' accent.
    private static Argb? ManifestAccent(MarketItem item) =>
        item.Manifest.ValueKind == System.Text.Json.JsonValueKind.Object
        && item.Manifest.TryGetProperty("accent", out var accent)
        && accent.ValueKind == System.Text.Json.JsonValueKind.String
        && Argb.TryParse(accent.GetString(), out var color)
            ? color
            : null;

    private ThemeTile TileOf(ThemeInfo info, bool light, Argb accent)
    {
        var applied = info.Id == Market.CurrentThemeId;
        var appearance = info.Kind is ColorTheme.Light or ColorTheme.System ? info.Kind : ColorTheme.Dark;
        return new ThemeTile(info.Id, info.Name, info.Author, appearance, info.HasMetrics, TileColors.FromInfo(info, light, accent), true, applied,
            applied ? TileAction.Applied : TileAction.Apply, false, "", null);
    }

    private string StateOf(MarketItem item, TileAction action) => action switch
    {
        TileAction.Installing => Market.InstallOf(item.Id) is { Total: > 0 } progress
            ? $"Installing {(int)(progress.Fraction * 100)}%"
            : "Installing…",
        TileAction.Update => "Update available",
        TileAction.Applied => "Applied",
        _ => "",
    };

    private void SchedulePreview()
    {
        if (_previews && SelectedTile is { } tile)
        {
            _ = PreviewAsync(++_preview, tile);
        }
        else if (_previews)
        {
            // Nothing selected (the filter took the tile away): the theme in effect again.
            _preview++;
            RestoreIfShown();
        }
    }

    private void RestoreIfShown()
    {
        if (IsPreviewShown)
        {
            IsPreviewShown = false;
            PreviewingName = null;
            Preview?.Invoke(null);
            Changed?.Invoke();
        }
    }

    // The awaits stay on the caller's context: Preview is raised on the UI thread, as Changed is.
    private async Task PreviewAsync(int sequence, ThemeTile tile)
    {
        if (PreviewDelay > TimeSpan.Zero)
        {
            await Task.Delay(PreviewDelay);
            if (sequence != _preview)
            {
                return;
            }
        }
        if (tile.IsApplied)
        {
            RestoreIfShown();
            return;
        }
        CoreRequest request = tile.IsPresent ? new GetThemeRequest { ThemeId = tile.Id } : new PreviewThemeRequest(tile.Id);
        CoreReply reply;
        try
        {
            reply = await _core.RequestAsync(request);
        }
        catch (IOException error)
        {
            Diag.Debug(Target, "theme preview failed", new LogField("theme", tile.Id), new LogField("error", error.Message));
            return;
        }
        if (sequence != _preview)
        {
            return;
        }
        switch (reply)
        {
            case ThemeReply { Theme: var theme }:
                IsPreviewShown = true;
                PreviewingName = tile.Name;
                Error = null;
                Preview?.Invoke(theme);
                Changed?.Invoke();
                Diag.Info(Target, "gallery theme previewed", new LogField("theme", tile.Id), new LogField("installed", tile.IsPresent));
                break;
            case ErrorReply error:
                Error = string.Create(CultureInfo.InvariantCulture, $"Cannot preview {tile.Name}: {error.Message}");
                Diag.Debug(Target, "gallery theme preview failed", new LogField("theme", tile.Id), new LogField("code", error.Code),
                    new LogField("error", error.Message));
                Changed?.Invoke();
                break;
        }
    }
}
