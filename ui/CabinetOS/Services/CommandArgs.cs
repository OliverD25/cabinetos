using System.Buffers;
using System.Text.Json;

namespace CabinetOS.Services;

/// <summary>Arguments of commands, as the JSON object <c>execute_command</c> carries.</summary>
internal static class CommandArgs
{
    /// <summary>An object with one text field, for example <c>{"path":"C:\\"}</c>.</summary>
    public static JsonElement With(string name, string value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(name, value);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>The text field <paramref name="name"/> of the arguments, if present.</summary>
    public static string? Text(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var field)
        && field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;
}
