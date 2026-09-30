using System.Text.Json;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Presentation;

namespace CabinetOS.Core.Settings;

/// <summary>
/// The settings the window uses, read from the core's <c>config</c> reply
/// (docs/config.md). The core owns the file; the UI only reads what it sends.
/// <see cref="DockBottom"/> and <see cref="DockRight"/> are
/// <c>ui.dockSize</c>: the Tool Dock's dragged size, or null for the design's.
/// <see cref="Selection"/> is <c>panes.selection</c>. <see cref="HeavyLogging"/> is
/// <c>logging.heavy</c>, which the window's own log follows as the core's does.
/// The rail layout's four settings are read when the core sends them:
/// <see cref="Rail"/> (<c>ui.rail</c>: the rail's button IDs in order; null for the default order),
/// <see cref="SidebarWidth"/> (<c>ui.sidebarWidth</c>: whole pixels, or null for the design's width),
/// <see cref="SidebarView"/> (<c>ui.sidebarView</c>: <c>explorer</c>, <c>search</c> or a tool's ID) and
/// <see cref="SidebarAutoReveal"/> (<c>ui.sidebarAutoReveal</c>: whether the folder tree follows the active pane).
/// A core that does not know them leaves them out, and the window keeps them in memory.
/// <see cref="Columns"/> is <c>ui.columns</c>: the file panes' Modified, Type and Size widths, or null for the theme's.
/// <see cref="FolderSizes"/> is <c>panes.folderSizes</c>: whether every folder of a listing is measured when it opens.
/// <see cref="CompactOverlay"/> is <c>ui.compactOverlay</c>: the drawer's size the user last resized it to, or null for 480 by 640.
/// </summary>
public sealed record UiSettings(
    string Layout,
    bool DualPane,
    bool Sidebar,
    string Theme,
    bool ShowHidden,
    string SortKey,
    bool SortDescending,
    double? DockBottom = null,
    double? DockRight = null,
    SelectionStyle Selection = SelectionStyle.Windows,
    bool HeavyLogging = false,
    IReadOnlyList<string>? Rail = null,
    double? SidebarWidth = null,
    string? SidebarView = null,
    bool SidebarAutoReveal = true,
    ColumnWidths? Columns = null,
    bool FolderSizes = false,
    CompactSize? CompactOverlay = null)
{
    /// <summary>The defaults of docs/config.md, used until the core answers.</summary>
    public static readonly UiSettings Defaults = new("classic", true, true, "default", false, "name", false);

    /// <summary>The dock's stored size where <paramref name="placement"/> puts it; null for the design's.</summary>
    public double? DockSize(DockPlacement placement) => placement == DockPlacement.Bottom ? DockBottom : DockRight;

    /// <summary>Reads the settings; anything missing or of the wrong kind keeps its default.</summary>
    public static UiSettings FromConfig(JsonElement config)
    {
        var ui = Section(config, "ui");
        var panes = Section(config, "panes");
        var sort = panes is { } p ? Section(p, "sort") : null;
        var dock = ui is { } u ? Section(u, "dockSize") : null;
        return new UiSettings(
            String(ui, "layout") ?? Defaults.Layout,
            Bool(ui, "dualPane") ?? Defaults.DualPane,
            Bool(ui, "sidebar") ?? Defaults.Sidebar,
            String(ui, "theme") ?? Defaults.Theme,
            Bool(panes, "showHidden") ?? Defaults.ShowHidden,
            String(sort, "key") ?? Defaults.SortKey,
            Bool(sort, "descending") ?? Defaults.SortDescending,
            Pixels(dock, "bottom"),
            Pixels(dock, "right"),
            String(panes, "selection") == "commander" ? SelectionStyle.Commander : SelectionStyle.Windows,
            Bool(Section(config, "logging"), "heavy") ?? Defaults.HeavyLogging,
            Strings(ui, "rail"),
            Pixels(ui, "sidebarWidth"),
            String(ui, "sidebarView"),
            Bool(ui, "sidebarAutoReveal") ?? Defaults.SidebarAutoReveal,
            ColumnLayout.FromConfig(config),
            Bool(panes, "folderSizes") ?? Defaults.FolderSizes,
            CompactOverlayLayout.FromConfig(config));
    }

    // Null for a list that is absent or empty, so two readings of the same file are equal (a record compares lists by reference).
    private static IReadOnlyList<string>? Strings(JsonElement? parent, string name)
    {
        if (parent is not { } p || !p.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var list = value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList();
        return list.Count > 0 ? list : null;
    }

    private static double? Pixels(JsonElement? parent, string name) =>
        parent is { } p && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var pixels) && pixels >= 0
            ? pixels
            : null;

    private static JsonElement? Section(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? String(JsonElement? parent, string name) =>
        parent is { } p && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? Bool(JsonElement? parent, string name) =>
        parent is { } p && p.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
