using System.Text.Json;

namespace CabinetOS.Core.Tools;

/// <summary>
/// The plugin events the window itself understands, whichever plugin sends
/// them (Article 10: the window knows these names as general events, not as
/// any one extension's). A plugin sends an event with <c>emit</c>; the core
/// forwards it as <c>plugin_event</c> with the plugin's ID
/// (docs/plugins.md, "Host functions").
/// </summary>
public static class PluginEvents
{
    /// <summary><c>{ "text": … }</c>: a notice for the status bar, like the window's own.</summary>
    public const string Notice = "agent.notice";

    /// <summary><c>{ "view": …, "kind": "dot"|"spinner"|null }</c>: a badge on a view's button in the activity rail.</summary>
    public const string Badge = "badge";

    /// <summary>The longest notice the status bar shows; more is cut.</summary>
    public const int MaxNoticeLength = 300;

    /// <summary>
    /// The payload as JSON, or null when it is not (a plugin's payload is
    /// text, "usually JSON"; docs/ipc.md, "Plugins"). A number, a string or a
    /// list is JSON too, so the caller looks at its kind.
    /// </summary>
    public static JsonElement? Parse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The text of a notice event, or null when the event is not one or has no text.</summary>
    public static string? NoticeText(string name, string payload)
    {
        if (name != Notice || Parse(payload) is not { } json || Text(json, "text") is not { Length: > 0 } text)
        {
            return null;
        }
        return text.Length <= MaxNoticeLength ? text : text[..(MaxNoticeLength - 1)] + "…";
    }

    /// <summary>
    /// The preview a plugin's event names (rule 2 of the window's two generic
    /// rules): any event, whatever its name, whose payload has a string field
    /// <c>preview</c>. The window opens it in the other pane (<c>open_preview</c>).
    /// </summary>
    public static string? ProposedPreview(string payload) =>
        Parse(payload) is { } json && Text(json, "preview") is { Length: > 0 } id ? id : null;

    /// <summary>The badge a <see cref="Badge"/> event asks for: the view and the kind, or null when the event is not a badge.</summary>
    public static (string View, string? Kind)? BadgeOf(string name, string payload)
    {
        if (name != Badge || Parse(payload) is not { } json || Text(json, "view") is not { Length: > 0 } view)
        {
            return null;
        }
        var kind = Text(json, "kind");
        return (view, kind is "dot" or "spinner" ? kind : null);
    }

    /// <summary>The preview a plugin command's result names (rule 1: a string field <c>preview</c>), or null.</summary>
    public static string? PreviewOfResult(JsonElement result) => Text(result, "preview") is { Length: > 0 } id ? id : null;

    private static string? Text(JsonElement payload, string name) =>
        payload is { ValueKind: JsonValueKind.Object } && payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// The plugins a tool page asked to hear (<c>subscribe</c>). The window
/// forwards an event to a page only when the page subscribed to the sending
/// plugin, so a page hears nothing it did not ask for.
/// </summary>
public sealed class ToolSubscriptions
{
    /// <summary>The most plugins one page may follow; a page that asks for more is refused.</summary>
    public const int MaxPlugins = 16;

    private readonly HashSet<string> _plugins = new(StringComparer.Ordinal);

    /// <summary>The plugins the page follows.</summary>
    public IReadOnlyCollection<string> Plugins => _plugins;

    /// <summary>Adds <paramref name="plugin"/>; false when the page follows too many already.</summary>
    public bool Subscribe(string plugin) => _plugins.Contains(plugin) || (_plugins.Count < MaxPlugins && _plugins.Add(plugin));

    /// <summary>Stops forwarding <paramref name="plugin"/>'s events.</summary>
    public bool Unsubscribe(string plugin) => _plugins.Remove(plugin);

    /// <summary>Whether the page follows <paramref name="plugin"/>.</summary>
    public bool Wants(string plugin) => _plugins.Contains(plugin);

    /// <summary>The page loaded again: it starts with no subscriptions and asks for them once more.</summary>
    public void Clear() => _plugins.Clear();
}
