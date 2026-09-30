using System.Buffers;
using System.Collections.Frozen;
using System.Text.Json;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Tools;

/// <summary>
/// A message from a tool's page to the window: <c>ready</c> (its listener is
/// set: send <c>context</c> and <c>open</c>), <c>command</c> (run a command
/// through the router), or <c>key</c> (a shortcut of the window, from the
/// script the host puts into every tool page), <c>subscribe</c> and
/// <c>unsubscribe</c> (hear one plugin's events).
/// </summary>
public sealed record ToolMessage(string Type, string? CommandId, JsonElement? Args, string? Keys, string? Plugin = null);

/// <summary>
/// The web-message protocol between the window and a Tool Extension
/// (docs/tool-extensions.md, "Messages"). Everything is a JSON string
/// (<c>PostWebMessageAsString</c> and <c>chrome.webview.postMessage</c>).
/// </summary>
public static class ToolMessages
{
    /// <summary>The largest message the window takes from a tool.</summary>
    public const int MaxIncomingLength = 64 * 1024;

    /// <summary>The most selected paths a <c>context</c> carries; a bigger selection is cut and says so.</summary>
    public const int MaxSelection = 1000;

    // A tool is a web page: it may move around and open things, never change
    // files or grant rights. Everything else it asks for is refused and logged.
    private static readonly FrozenSet<string> AllowedCommands = FrozenSet.ToFrozenSet(
    [
        "go.toPath", "go.back", "go.forward", "go.up",
        "view.toggleDualPane", "view.toggleSidebar", "view.toggleTerminal", "view.focusOtherPane",
        "palette.show", "search.focus", "help.about",
        "terminal.new", "editor.openMarkdownPreview",
    ], StringComparer.Ordinal);

    /// <summary>Whether a tool may run <paramref name="commandId"/> of the fixed list.</summary>
    public static bool MayRun(string commandId) => AllowedCommands.Contains(commandId);

    /// <summary>
    /// Whether a tool page may run <paramref name="commandId"/>: a command of the fixed list, or a command of a plugin the
    /// page follows (<paramref name="follows"/>). A page that follows a plugin is that plugin's own page, so it may run what
    /// the plugin registered, whatever the names (Constitution Article 10: no plugin's name is in the window). The command
    /// must be listed with that plugin as its source and start with <c>&lt;plugin id&gt;.</c>; a page that follows a name
    /// like <c>file</c> gets none of the core's commands by it.
    /// </summary>
    public static bool MayRun(string commandId, CommandSource? source, Func<string, bool> follows) =>
        MayRun(commandId)
        || (source is { Kind: "plugin", Id: { Length: > 0 } plugin }
            && commandId.Length > plugin.Length + 1
            && commandId.StartsWith(plugin, StringComparison.Ordinal)
            && commandId[plugin.Length] == '.'
            && follows(plugin));

    /// <summary>The commands a tool may run, for the documentation and the log.</summary>
    public static IReadOnlyCollection<string> Allowed => AllowedCommands;

    /// <summary>Reads a tool's message; null for anything malformed, too long or unknown.</summary>
    public static ToolMessage? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaxIncomingLength)
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "type") is not { } type)
            {
                return null;
            }
            switch (type)
            {
                case "ready":
                    return new ToolMessage(type, null, null, null);
                case "command" when Text(root, "id") is { Length: > 0 and <= 128 } id:
                    JsonElement? args = null;
                    if (root.TryGetProperty("args", out var given))
                    {
                        if (given.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                        {
                            return null;
                        }
                        args = given.ValueKind == JsonValueKind.Object ? given.Clone() : null;
                    }
                    return new ToolMessage(type, id, args, null);
                case "key" when Text(root, "keys") is { Length: > 0 and < 64 } keys:
                    return new ToolMessage(type, null, null, keys);
                case "subscribe" or "unsubscribe" when Text(root, "plugin") is { Length: > 0 and <= 128 } plugin:
                    return new ToolMessage(type, null, null, null, plugin);
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the user has in front of them: the active pane's selected paths
    /// (at most <see cref="MaxSelection"/>) and its folder.
    /// </summary>
    public static string Context(IReadOnlyList<string> selection, string? activeFolder) => Write(w =>
    {
        w.WriteString("type", "context");
        w.WriteStartArray("selection");
        foreach (var path in selection.Take(MaxSelection))
        {
            w.WriteStringValue(path);
        }
        w.WriteEndArray();
        if (selection.Count > MaxSelection)
        {
            w.WriteBoolean("truncated", true);
        }
        if (activeFolder is null)
        {
            w.WriteNull("activeFolder");
        }
        else
        {
            w.WriteString("activeFolder", activeFolder);
        }
    });

    /// <summary>A file to show: its path, and the URL the tool reads it from.</summary>
    public static string Open(string path, string url) => Write(w =>
    {
        w.WriteString("type", "open");
        w.WriteString("path", path);
        w.WriteString("url", url);
    });

    /// <summary>
    /// An event of a plugin the page subscribed to. The plugin's payload is
    /// text; when it is JSON the page gets it as JSON (an object, a list, a
    /// number), and any other text as a string.
    /// </summary>
    public static string PluginEvent(string plugin, string name, string payload) => Write(w =>
    {
        w.WriteString("type", "plugin-event");
        w.WriteString("plugin", plugin);
        w.WriteString("name", name);
        w.WritePropertyName("payload");
        if (PluginEvents.Parse(payload) is { } json)
        {
            json.WriteTo(w);
        }
        else
        {
            w.WriteStringValue(payload);
        }
    });

    /// <summary>The most paths one <c>paths-dropped</c> message carries; a longer drop is cut and says so.</summary>
    public const int MaxDropped = 1000;

    /// <summary>Rows of a pane were dropped on the page: their paths.</summary>
    public static string PathsDropped(IReadOnlyList<string> paths) => Write(w =>
    {
        w.WriteString("type", "paths-dropped");
        w.WriteStartArray("paths");
        foreach (var path in paths.Take(MaxDropped))
        {
            w.WriteStringValue(path);
        }
        w.WriteEndArray();
        if (paths.Count > MaxDropped)
        {
            w.WriteBoolean("truncated", true);
        }
    });

    /// <summary>The window's shortcuts the host's key script passes on.</summary>
    public static string PassKeys(IEnumerable<string> keys) => Write(w =>
    {
        w.WriteString("type", "passKeys");
        w.WriteStartArray("keys");
        foreach (var key in keys)
        {
            w.WriteStringValue(key);
        }
        w.WriteEndArray();
    });

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>
/// Where a tool reads the file it shows: the file's own folder, mapped
/// read-only to a virtual host of its own. Each open gets a new host name, so
/// the tool cannot keep reading an earlier folder and nothing comes from the
/// browser's cache of another one.
/// </summary>
public static class ToolFileUrls
{
    /// <summary>The host for open number <paramref name="serial"/> of tool <paramref name="toolId"/>.</summary>
    public static string Host(string toolId, int serial, string domain) => $"f{serial}.{toolId}.{domain}";

    /// <summary>The URL of <paramref name="path"/> on <paramref name="host"/>, which serves the file's folder.</summary>
    public static string Url(string host, string path) => $"https://{host}/{Uri.EscapeDataString(Path.GetFileName(path))}";
}
