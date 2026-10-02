using System.Buffers;
using System.Text.Json;

namespace CabinetOS.Core.Terminal;

/// <summary>
/// One saved terminal session (<c>terminal.tabs.items</c>, docs/config.md): what is needed to start the same kind of
/// shell again. <paramref name="Folder"/> is the one the shell's prompt hook reported last, else the one it started in
/// (null: the user's profile folder); <paramref name="Pane"/> is 0 for the left pane, 1 for the right.
/// </summary>
public sealed record SavedSession(string Profile, string? Folder, int Pane, TerminalMode Mode);

/// <summary>
/// The terminal's sessions as the window last saved them (<c>terminal.tabs</c>, docs/config.md): in the order of the
/// tabs, with the index of the tab in front and of each pane's own front tab, which the split dock shows in that pane's
/// half. The window owns the setting and writes it whole with one <c>set_value</c>, so an index never points past the
/// sessions. Only running sessions are saved; what a shell printed and its history are not.
/// </summary>
public sealed record TerminalLayout(IReadOnlyList<SavedSession> Items, int? Front, int? ShownLeft, int? ShownRight)
{
    /// <summary>The setting, as <c>set_value</c> and <c>config_changed</c> name it.</summary>
    public const string Key = "terminal.tabs";

    /// <summary>Nothing saved.</summary>
    public static readonly TerminalLayout Empty = new([], null, null, null);

    /// <summary>Whether no session was saved.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>The saved front tab of pane <paramref name="pane"/> (0 left, 1 right), as an index in <see cref="Items"/>.</summary>
    public int? ShownIn(int pane) => pane == 1 ? ShownRight : ShownLeft;

    /// <summary>Reads <c>terminal.tabs</c> from a <c>config</c> reply's configuration; what is missing or malformed is left out.</summary>
    public static TerminalLayout FromConfig(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty("terminal", out var terminal) || terminal.ValueKind != JsonValueKind.Object
            || !terminal.TryGetProperty("tabs", out var tabs) || tabs.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }
        // A session without a profile cannot be started again: it is dropped, and the indexes of the others move with it.
        var items = new List<SavedSession>();
        var moved = new Dictionary<int, int>();
        if (tabs.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var original = 0;
            foreach (var item in list.EnumerateArray())
            {
                if (Session(item) is { } session)
                {
                    moved[original] = items.Count;
                    items.Add(session);
                }
                original++;
            }
        }
        int? Index(JsonElement parent, string name) =>
            parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var index) && moved.TryGetValue(index, out var now)
                ? now
                : null;
        tabs.TryGetProperty("shown", out var shown);
        return new TerminalLayout(items, Index(tabs, "front"), Index(shown, "left"), Index(shown, "right"));
    }

    private static SavedSession? Session(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.String
            || profile.GetString() is not { Length: > 0 } name)
        {
            return null;
        }
        var folder = item.TryGetProperty("folder", out var path) && path.ValueKind == JsonValueKind.String && path.GetString() is { Length: > 0 } text ? text : null;
        var pane = item.TryGetProperty("pane", out var side) && side.ValueKind == JsonValueKind.String ? TerminalBinding.PaneIndex(side.GetString()) : 0;
        var mode = item.TryGetProperty("mode", out var how) && how.ValueKind == JsonValueKind.String ? TerminalBinding.ParseMode(how.GetString()) : null;
        return new SavedSession(name, folder, pane, mode ?? TerminalMode.Locked);
    }

    /// <summary>The value <c>set_value terminal.tabs</c> takes; an absent folder or index is left out, as the core writes it.</summary>
    public JsonElement ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("items");
            foreach (var item in Items)
            {
                writer.WriteStartObject();
                writer.WriteString("profile", item.Profile);
                if (item.Folder is { Length: > 0 } folder)
                {
                    writer.WriteString("folder", folder);
                }
                writer.WriteString("pane", TerminalBinding.PaneName(item.Pane));
                writer.WriteString("mode", TerminalBinding.ModeName(item.Mode));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (Within(Front) is { } front)
            {
                writer.WriteNumber("front", front);
            }
            writer.WriteStartObject("shown");
            if (Within(ShownLeft) is { } left)
            {
                writer.WriteNumber("left", left);
            }
            if (Within(ShownRight) is { } right)
            {
                writer.WriteNumber("right", right);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    // An index that names no session is left out: the core refuses a file whose index points past its sessions.
    private int? Within(int? index) => index is { } i && i >= 0 && i < Items.Count ? i : null;
}
