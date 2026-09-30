using System.Buffers;
using System.Text.Json;

namespace CabinetOS.Core.Tabs;

/// <summary>One saved tab: the folder it shows, whether it is locked, and whether it shows columns (<c>ui.tabs</c>, docs/config.md).</summary>
public sealed record TabItemConfig(string Path, bool Locked, TabMode Mode = TabMode.Files);

/// <summary>One pane's saved tabs and the one in front.</summary>
public sealed record PaneTabsConfig(IReadOnlyList<TabItemConfig> Items, int Active)
{
    /// <summary>No tabs saved: the pane opens one as the window decides.</summary>
    public static readonly PaneTabsConfig Empty = new([], 0);

    /// <summary>
    /// The pane's tabs as a strip whose tabs show <paramref name="items"/>
    /// (null when none was saved). The tab in front is <see cref="Active"/>,
    /// which the file keeps within the tabs; a hand-made value past the end
    /// falls back to the last tab.
    /// </summary>
    public TabStrip? ToStrip() =>
        Items.Count == 0
            ? null
            : new TabStrip(Items.Select(item => new PaneTab(item.Path, item.Locked, item.Mode)), Math.Clamp(Active, 0, Items.Count - 1));
}

/// <summary>
/// <c>ui.tabs</c> (docs/config.md): what each pane's tabs were when the window
/// last saved them. The window owns the tab state and writes it whole with
/// one <c>set_value</c>, so the tab in front never points past the tabs.
/// </summary>
public sealed record TabsConfig(PaneTabsConfig Left, PaneTabsConfig Right)
{
    /// <summary>The setting, as <c>set_value</c> and <c>config_changed</c> name it.</summary>
    public const string Key = "ui.tabs";

    /// <summary>A saved tab's <c>mode</c> in the column view; the list has none.</summary>
    public const string ColumnsMode = "columns";

    /// <summary>Nothing saved.</summary>
    public static readonly TabsConfig Empty = new(PaneTabsConfig.Empty, PaneTabsConfig.Empty);

    /// <summary>The saved tabs of pane <paramref name="pane"/> (0 left, 1 right).</summary>
    public PaneTabsConfig ForPane(int pane) => pane == 0 ? Left : Right;

    /// <summary>Reads <c>ui.tabs</c> from a <c>config</c> reply's configuration.</summary>
    public static TabsConfig FromConfig(JsonElement config) =>
        config.ValueKind == JsonValueKind.Object && config.TryGetProperty("ui", out var ui) ? FromUi(ui) : Empty;

    /// <summary>Reads <c>ui.tabs</c> from the <c>ui</c> object of a <c>config</c> reply; what is missing or malformed is left out.</summary>
    public static TabsConfig FromUi(JsonElement ui)
    {
        if (ui.ValueKind != JsonValueKind.Object || !ui.TryGetProperty("tabs", out var tabs) || tabs.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }
        return new TabsConfig(Pane(tabs, "left"), Pane(tabs, "right"));
    }

    private static PaneTabsConfig Pane(JsonElement tabs, string name)
    {
        if (!tabs.TryGetProperty(name, out var pane) || pane.ValueKind != JsonValueKind.Object
            || !pane.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return PaneTabsConfig.Empty;
        }
        var list = new List<TabItemConfig>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String && path.GetString() is { Length: > 0 } text)
            {
                var locked = item.TryGetProperty("locked", out var flag) && flag.ValueKind == JsonValueKind.True;
                // Left out, or a mode this window does not know: the list.
                var columns = item.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() == ColumnsMode;
                list.Add(new TabItemConfig(text, locked, columns ? TabMode.Columns : TabMode.Files));
            }
        }
        var active = pane.TryGetProperty("active", out var front) && front.ValueKind == JsonValueKind.Number && front.TryGetInt32(out var index) ? index : 0;
        return new PaneTabsConfig(list, active);
    }

    /// <summary>The value <c>set_value ui.tabs</c> takes: both panes, each with its items and the tab in front.</summary>
    public JsonElement ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            Write(writer, "left", Left);
            Write(writer, "right", Right);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void Write(Utf8JsonWriter writer, string name, PaneTabsConfig pane)
    {
        writer.WriteStartObject(name);
        writer.WriteStartArray("items");
        foreach (var item in pane.Items)
        {
            writer.WriteStartObject();
            writer.WriteString("path", item.Path);
            writer.WriteBoolean("locked", item.Locked);
            // A list tab is written without a mode, as the core writes it back.
            if (item.Mode == TabMode.Columns)
            {
                writer.WriteString("mode", ColumnsMode);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteNumber("active", pane.Items.Count == 0 ? 0 : Math.Clamp(pane.Active, 0, pane.Items.Count - 1));
        writer.WriteEndObject();
    }
}
