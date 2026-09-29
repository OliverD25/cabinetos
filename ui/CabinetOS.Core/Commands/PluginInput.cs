using System.Buffers;
using System.Text.Json;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Commands;

/// <summary>
/// The prompt a plugin command asks for (protocol 13: <c>input</c> in
/// <c>list_commands</c>). The window shows a prompt box with the command's
/// title and placeholder, and runs the command with the answer as <c>input</c>
/// next to <c>path</c> and <c>paths</c> (docs/plugins.md, "What the shell passes").
/// </summary>
public static class PluginInput
{
    /// <summary>The argument that carries the answer.</summary>
    public const string Field = "input";

    /// <summary>
    /// Whether running <paramref name="command"/> with <paramref name="args"/>
    /// must ask first: a plugin's command that has an <c>input</c>, run
    /// without one. A caller that gives <c>input</c> itself (a tool page, the
    /// command line) is never asked.
    /// </summary>
    public static bool Asks(CommandInfo command, JsonElement? args) =>
        command is { Source.Kind: "plugin", Input: not null }
        && !(args is { ValueKind: JsonValueKind.Object } given && given.TryGetProperty(Field, out _));

    /// <summary>The prompt's heading: the input's own title, else the command's title (docs/ipc.md, "Commands").</summary>
    public static string Label(CommandInfo command) =>
        command.Input?.Title is { Length: > 0 } title ? title : command.Title;

    /// <summary><paramref name="args"/> with <c>input</c> set to <paramref name="text"/>; the other arguments stay as they were.</summary>
    public static JsonElement With(JsonElement? args, string text)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (args is { ValueKind: JsonValueKind.Object } existing)
            {
                foreach (var property in existing.EnumerateObject().Where(p => p.Name != Field))
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteString(Field, text);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
