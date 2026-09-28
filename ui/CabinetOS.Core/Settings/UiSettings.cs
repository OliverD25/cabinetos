using System.Text.Json;

namespace CabinetOS.Core.Settings;

/// <summary>
/// The settings the window uses, read from the core's <c>config</c> reply
/// (docs/config.md). The core owns the file; the UI only reads what it sends.
/// </summary>
public sealed record UiSettings(
    string Layout,
    bool DualPane,
    bool Sidebar,
    string Theme,
    bool ShowHidden,
    string SortKey,
    bool SortDescending)
{
    /// <summary>The defaults of docs/config.md, used until the core answers.</summary>
    public static readonly UiSettings Defaults = new("classic", true, true, "default", false, "name", false);

    /// <summary>Reads the settings; anything missing or of the wrong kind keeps its default.</summary>
    public static UiSettings FromConfig(JsonElement config)
    {
        var ui = Section(config, "ui");
        var panes = Section(config, "panes");
        var sort = panes is { } p ? Section(p, "sort") : null;
        return new UiSettings(
            String(ui, "layout") ?? Defaults.Layout,
            Bool(ui, "dualPane") ?? Defaults.DualPane,
            Bool(ui, "sidebar") ?? Defaults.Sidebar,
            String(ui, "theme") ?? Defaults.Theme,
            Bool(panes, "showHidden") ?? Defaults.ShowHidden,
            String(sort, "key") ?? Defaults.SortKey,
            Bool(sort, "descending") ?? Defaults.SortDescending);
    }

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
