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
/// A message from a viewer page in the Quick View panel (ADR 0023, decisions 2 and 3; the schema
/// <c>sdk/tools/quickview-messages.schema.json</c>): <c>ready</c>, <c>quickview-shown</c>, <c>quickview-failed</c>,
/// <c>quickview-keys</c>, <c>quickview-render</c> and <c>quickview-system-preview</c>, each but <c>ready</c> with the
/// token of the <c>quickview-show</c> it answers. <c>Refused</c> stands for <c>command</c>, <c>subscribe</c> and
/// <c>unsubscribe</c>, which a page in the panel may not send: the window logs them and does nothing.
/// </summary>
public sealed record QuickViewPageMessage(string Type, long Token = -1)
{
    /// <summary>The type of a message the panel refuses (<see cref="RefusedType"/> holds what it was).</summary>
    public const string Refused = "refused";

    /// <summary>The refused message's own type: <c>command</c>, <c>subscribe</c> or <c>unsubscribe</c>.</summary>
    public string? RefusedType { get; init; }

    /// <summary><c>quickview-failed</c>'s reason: <c>unsupported</c>, <c>damaged</c>, <c>too-large</c> or <c>other</c>.</summary>
    public string? Reason { get; init; }

    /// <summary><c>quickview-failed</c>'s text, at most 200 characters.</summary>
    public string? Message { get; init; }

    /// <summary><c>quickview-shown</c>'s text for the bottom line, at most 80 characters.</summary>
    public string? Details { get; init; }

    /// <summary>The keys of <c>quickview-shown</c> or <c>quickview-keys</c>, as the page named them; null when it sent none.</summary>
    public IReadOnlyList<string>? Keys { get; init; }

    /// <summary><c>quickview-render</c>'s size in device pixels, 1 to 2560.</summary>
    public int Width { get; init; }

    /// <summary><c>quickview-render</c>'s height.</summary>
    public int Height { get; init; }
}

/// <summary>The window's look as a viewer page gets it: dark or light, four colours of the theme in effect, and the font.</summary>
public sealed record QuickViewLook(string Appearance, string Background, string Text, string TextSecondary, string Accent, string Font);

/// <summary>The thumbnail on screen when a page gets its file: a PNG as a data URL, and its size.</summary>
public sealed record QuickViewThumbnail(string DataUrl, int Width, int Height);

/// <summary>
/// The web messages between the window and a viewer page in the Quick View panel (ADR 0023): parsing what the page
/// posts, and writing what the window sends. The schema <c>sdk/tools/quickview-messages.schema.json</c> is the one
/// description of both sides; the window's tests check every message written here against it.
/// </summary>
public static class QuickViewMessages
{
    /// <summary>The longest <c>details</c> of <c>quickview-shown</c>.</summary>
    public const int MaxDetails = 80;

    /// <summary>The longest <c>message</c> of <c>quickview-failed</c>.</summary>
    public const int MaxMessage = 200;

    /// <summary>The longest side a page may ask a drawing for (<c>render_image</c>'s limit).</summary>
    public const int MaxRenderSize = 2560;

    private static readonly FrozenSet<string> Reasons = FrozenSet.ToFrozenSet(["unsupported", "damaged", "too-large", "other"], StringComparer.Ordinal);

    /// <summary>
    /// Reads a viewer page's message; null for anything malformed, too long, over a limit or unknown. A key name a page
    /// may not ask for does not spoil the message: it is simply never granted (<see cref="QuickView.QuickViewKeys"/>).
    /// </summary>
    public static QuickViewPageMessage? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > ToolMessages.MaxIncomingLength)
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            var type = typeElement.GetString()!;
            switch (type)
            {
                case "ready":
                    return new QuickViewPageMessage(type);
                case "command" or "subscribe" or "unsubscribe":
                    return new QuickViewPageMessage(QuickViewPageMessage.Refused) { RefusedType = type };
            }
            if (!type.StartsWith("quickview-", StringComparison.Ordinal) || Token(root) is not { } token)
            {
                return null;
            }
            switch (type)
            {
                case "quickview-shown":
                    var details = Text(root, "details", MaxDetails, out var detailsOk);
                    var shownKeys = KeyList(root, out var shownKeysOk);
                    return detailsOk && shownKeysOk ? new QuickViewPageMessage(type, token) { Details = details, Keys = shownKeys } : null;
                case "quickview-failed":
                    var reason = Text(root, "reason", 32, out var reasonOk);
                    var message = Text(root, "message", MaxMessage, out var messageOk);
                    return reasonOk && messageOk && reason is not null && Reasons.Contains(reason) && message is not null
                        ? new QuickViewPageMessage(type, token) { Reason = reason, Message = message }
                        : null;
                case "quickview-keys":
                    var keys = KeyList(root, out var keysOk);
                    return keysOk && keys is not null ? new QuickViewPageMessage(type, token) { Keys = keys } : null;
                case "quickview-render":
                    return Size(root, "width") is { } width && Size(root, "height") is { } height
                        ? new QuickViewPageMessage(type, token) { Width = width, Height = height }
                        : null;
                case "quickview-system-preview":
                    return new QuickViewPageMessage(type, token);
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The file to show, once after the page's <c>ready</c> (<c>quickview-show</c>).</summary>
    public static string Show(long token, string path, string url, string name, string extension, string claim, ulong size, DateTime modified,
        QuickViewThumbnail? thumbnail, QuickViewLook theme, double width, double height, double scale) => Write(w =>
    {
        w.WriteString("type", "quickview-show");
        w.WriteNumber("token", token);
        w.WriteString("path", path);
        w.WriteString("url", url);
        w.WriteString("name", name);
        w.WriteString("extension", extension);
        w.WriteString("claim", claim);
        w.WriteNumber("size", size);
        w.WriteString("modified", modified.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
        if (thumbnail is null)
        {
            w.WriteNull("thumbnail");
        }
        else
        {
            w.WriteStartObject("thumbnail");
            w.WriteString("dataUrl", thumbnail.DataUrl);
            w.WriteNumber("width", thumbnail.Width);
            w.WriteNumber("height", thumbnail.Height);
            w.WriteEndObject();
        }
        WriteLook(w, theme);
        w.WriteStartObject("panel");
        w.WriteNumber("width", Math.Round(Math.Max(1, width), 2));
        w.WriteNumber("height", Math.Round(Math.Max(1, height), 2));
        w.WriteNumber("scale", Math.Round(Math.Max(0.1, scale), 3));
        w.WriteEndObject();
    });

    /// <summary>The theme changed while the panel shows the page (<c>quickview-theme</c>).</summary>
    public static string Theme(long token, QuickViewLook theme) => Write(w =>
    {
        w.WriteString("type", "quickview-theme");
        w.WriteNumber("token", token);
        WriteLook(w, theme);
    });

    /// <summary>The keys of the page's request that the window grants (<c>quickview-keys-granted</c>).</summary>
    public static string KeysGranted(long token, IReadOnlyList<string> keys) => Write(w =>
    {
        w.WriteString("type", "quickview-keys-granted");
        w.WriteNumber("token", token);
        w.WriteStartArray("keys");
        foreach (var key in keys)
        {
            w.WriteStringValue(key);
        }
        w.WriteEndArray();
    });

    /// <summary>A press of a granted key (<c>quickview-key</c>), by the name the page asked for.</summary>
    public static string Key(long token, string key, bool repeat) => Write(w =>
    {
        w.WriteString("type", "quickview-key");
        w.WriteNumber("token", token);
        w.WriteString("key", key);
        w.WriteBoolean("repeat", repeat);
    });

    /// <summary>The shell's drawing of the file, on a host of its own (<c>quickview-rendered</c>).</summary>
    public static string Rendered(long token, string url, int width, int height) => Write(w =>
    {
        w.WriteString("type", "quickview-rendered");
        w.WriteNumber("token", token);
        w.WriteString("url", url);
        w.WriteNumber("width", width);
        w.WriteNumber("height", height);
    });

    /// <summary>The shell could not draw the file (<c>quickview-render-failed</c>).</summary>
    public static string RenderFailed(long token, string message) => Write(w =>
    {
        w.WriteString("type", "quickview-render-failed");
        w.WriteNumber("token", token);
        w.WriteString("message", Cut(message, MaxMessage));
    });

    /// <summary>No out-of-process preview handler shows the file (<c>quickview-system-preview-failed</c>).</summary>
    public static string SystemPreviewFailed(long token, string message) => Write(w =>
    {
        w.WriteString("type", "quickview-system-preview-failed");
        w.WriteNumber("token", token);
        w.WriteString("message", Cut(message, MaxMessage));
    });

    private static void WriteLook(Utf8JsonWriter w, QuickViewLook theme)
    {
        w.WriteStartObject("theme");
        w.WriteString("appearance", theme.Appearance);
        w.WriteString("background", theme.Background);
        w.WriteString("text", theme.Text);
        w.WriteString("textSecondary", theme.TextSecondary);
        w.WriteString("accent", theme.Accent);
        w.WriteString("font", theme.Font);
        w.WriteEndObject();
    }

    private static string Cut(string text, int most) => text.Length <= most ? text : text[..(most - 1)] + "…";

    private static long? Token(JsonElement root) =>
        root.TryGetProperty("token", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var token) && token >= 0 ? token : null;

    // An optional text: absent is fine (null, ok); present it must be a string of at most `most` characters.
    private static string? Text(JsonElement root, string name, int most, out bool ok)
    {
        ok = true;
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > most)
        {
            ok = false;
            return null;
        }
        return text;
    }

    // An optional list of key names: at most 64 texts, each once and short.
    private static IReadOnlyList<string>? KeyList(JsonElement root, out bool ok)
    {
        ok = true;
        if (!root.TryGetProperty("keys", out var value))
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > QuickView.QuickViewKeys.MaxKeys)
        {
            ok = false;
            return null;
        }
        var keys = new List<string>();
        foreach (var key in value.EnumerateArray())
        {
            if (key.ValueKind != JsonValueKind.String || key.GetString() is not { Length: > 0 and <= 32 } name || keys.Contains(name))
            {
                ok = false;
                return null;
            }
            keys.Add(name);
        }
        return keys;
    }

    private static int? Size(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var size) && size is >= 1 and <= MaxRenderSize
            ? size
            : null;

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

    /// <summary>
    /// The host for drawing number <paramref name="serial"/> a Quick View page of tool <paramref name="toolId"/> asked for
    /// (<c>quickview-render</c>): the core's folder of that drawing, read-only (ADR 0023, decision 3).
    /// </summary>
    public static string RenderHost(string toolId, int serial, string domain) => $"r{serial}.{toolId}.{domain}";

    /// <summary>The URL of <paramref name="path"/> on <paramref name="host"/>, which serves the file's folder.</summary>
    public static string Url(string host, string path) => $"https://{host}/{Uri.EscapeDataString(Path.GetFileName(path))}";
}
