using System.Text.Json;

namespace CabinetOS.Core.Settings;

/// <summary>
/// The shell's own state in <c>cabinetos.json</c> (docs/config.md): the
/// folders the panes showed last (<c>ui.lastPaths</c>, left pane first) and
/// the folders the user pinned to the sidebar (<c>ui.pinned</c>). Like every
/// setting it lives in the file, so the user can read and edit it (Article 6).
/// </summary>
public sealed record ShellState(IReadOnlyList<string> LastPaths, IReadOnlyList<string> Pinned)
{
    /// <summary>The setting paths, as <c>set_value</c> and <c>config_changed</c> name them.</summary>
    public const string LastPathsKey = "ui.lastPaths";

    /// <summary>The user's pinned folders.</summary>
    public const string PinnedKey = "ui.pinned";

    /// <summary>Two panes or one.</summary>
    public const string DualPaneKey = "ui.dualPane";

    /// <summary>Whether the sidebar shows.</summary>
    public const string SidebarKey = "ui.sidebar";

    /// <summary>Nothing remembered: the first start.</summary>
    public static readonly ShellState Empty = new([], []);

    /// <summary>Reads both lists from a <c>config</c> reply; anything that is not text is left out.</summary>
    public static ShellState FromConfig(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("ui", out var ui)
            || ui.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }
        return new ShellState(Strings(ui, "lastPaths"), Strings(ui, "pinned"));
    }

    private static IReadOnlyList<string> Strings(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
            {
                list.Add(text);
            }
        }
        return list;
    }
}
