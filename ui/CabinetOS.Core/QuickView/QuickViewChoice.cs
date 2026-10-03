using System.Text.Json;
using System.Text.Json.Nodes;

namespace CabinetOS.Core.QuickView;

/// <summary>
/// The user's choice of viewer for a kind, the setting <c>quickView.viewers</c> (ADR 0023, decision 1.2): an object
/// from a pattern to a viewer's ID, or to <c>none</c> for the thumbnail only. The panel's chooser and the palette's
/// prompt both write it with <c>set_value</c>; a key such as <c>*.pdf</c> has a dot, so the whole object is written.
/// </summary>
public static class QuickViewChoice
{
    /// <summary>The setting's dotted path.</summary>
    public const string Setting = "quickView.viewers";

    /// <summary>The value that means "no viewer, the thumbnail only".</summary>
    public const string NoViewer = "none";

    /// <summary>
    /// The setting's new value: <paramref name="current"/> (an object, else nothing) with <paramref name="pattern"/>
    /// set to <paramref name="value"/>, every other choice kept.
    /// </summary>
    public static JsonElement With(JsonElement? current, string pattern, string value)
    {
        var choices = new JsonObject();
        if (current is { ValueKind: JsonValueKind.Object } given)
        {
            foreach (var property in given.EnumerateObject())
            {
                if (!string.Equals(property.Name, pattern, StringComparison.OrdinalIgnoreCase))
                {
                    choices[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                }
            }
        }
        choices[pattern] = value;
        return JsonDocument.Parse(choices.ToJsonString()).RootElement.Clone();
    }

    /// <summary>The value the user chose for <paramref name="pattern"/>, or null when they chose none.</summary>
    public static string? Of(JsonElement? current, string pattern)
    {
        if (current is not { ValueKind: JsonValueKind.Object } given)
        {
            return null;
        }
        foreach (var property in given.EnumerateObject())
        {
            if (string.Equals(property.Name, pattern, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }
        return null;
    }
}
