using System.Buffers;
using System.Text.Json;

namespace CabinetOS.Services;

/// <summary>Arguments of commands, as the JSON object <c>execute_command</c> carries.</summary>
internal static class CommandArgs
{
    /// <summary>An object with one text field, for example <c>{"path":"C:\\"}</c>.</summary>
    public static JsonElement With(string name, string value) => Object((name, value));

    /// <summary>
    /// An object from name and value pairs; a value is text, a number, true or
    /// false, or a list of text.
    /// </summary>
    public static JsonElement Object(params (string Name, object? Value)[] fields)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in fields)
            {
                switch (value)
                {
                    case null:
                        writer.WriteNull(name);
                        break;
                    case string text:
                        writer.WriteString(name, text);
                        break;
                    case bool flag:
                        writer.WriteBoolean(name, flag);
                        break;
                    case ulong number:
                        writer.WriteNumber(name, number);
                        break;
                    case int number:
                        writer.WriteNumber(name, number);
                        break;
                    case IEnumerable<string> texts:
                        writer.WriteStartArray(name);
                        foreach (var item in texts)
                        {
                            writer.WriteStringValue(item);
                        }
                        writer.WriteEndArray();
                        break;
                    default:
                        throw new ArgumentException($"a command argument cannot be a {value.GetType().Name}", nameof(fields));
                }
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>The text field <paramref name="name"/> of the arguments, if present.</summary>
    public static string? Text(JsonElement? args, string name) =>
        Field(args, name) is { ValueKind: JsonValueKind.String } field ? field.GetString() : null;

    /// <summary>The true/false field <paramref name="name"/>; false when absent.</summary>
    public static bool Bool(JsonElement? args, string name) =>
        Field(args, name) is { ValueKind: JsonValueKind.True };

    /// <summary>The list-of-text field <paramref name="name"/>, if present; items that are not text are left out.</summary>
    public static IReadOnlyList<string>? Texts(JsonElement? args, string name) =>
        Field(args, name) is { ValueKind: JsonValueKind.Array } field
            ? [.. field.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
            : null;

    /// <summary>The whole-number field <paramref name="name"/>, if present.</summary>
    public static ulong? Number(JsonElement? args, string name) =>
        Field(args, name) is { ValueKind: JsonValueKind.Number } field && field.TryGetUInt64(out var value) ? value : null;

    private static JsonElement? Field(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var field) ? field : null;
}
