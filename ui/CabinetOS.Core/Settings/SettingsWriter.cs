using System.Buffers;
using System.Text.Json;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Settings;

/// <summary>
/// Writes one setting through the core (<c>set_value</c>, protocol version 8).
/// The core owns <c>cabinetos.json</c>; the UI never writes the file itself.
/// A core without <c>set_value</c> answers <c>unknown_request</c>: the writer
/// then stops asking, and the window keeps its state in memory only.
/// </summary>
public sealed class SettingsWriter(ICoreChannel core)
{
    private const string Target = "cabinetos_ui::settings";

    /// <summary>Whether the core can write settings (false after <c>unknown_request</c>).</summary>
    public bool IsAvailable { get; private set; } = true;

    /// <summary>A core was started again: ask it anew.</summary>
    public void Reset() => IsAvailable = true;

    /// <summary>Sets <paramref name="path"/> to <paramref name="value"/>; returns whether the core wrote it.</summary>
    public async Task<bool> SetAsync(string path, JsonElement value, CancellationToken cancellationToken = default) =>
        await SetOrRefusalAsync(path, value, cancellationToken) is null;

    /// <summary>
    /// Sets <paramref name="path"/> to <paramref name="value"/>; returns null
    /// when the core wrote it, else why not: the core's own message, or that
    /// the core could not be asked.
    /// </summary>
    public async Task<string?> SetOrRefusalAsync(string path, JsonElement value, CancellationToken cancellationToken = default)
    {
        const string CannotWrite = "the core cannot write settings (set_value)";
        if (!IsAvailable)
        {
            return CannotWrite;
        }
        var request = new SetValueRequest(path, value);
        CoreReply reply;
        try
        {
            reply = await core.RequestAsync(request, cancellationToken);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            Diag.Info(Target, "cannot write a setting", new LogField("path", path), new LogField("error", error.Message));
            return error.Message;
        }
        switch (reply)
        {
            case OkReply:
                return null;
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                IsAvailable = false;
                Diag.Info(Target, "the core cannot write settings yet (set_value); the window's state stays in memory");
                return CannotWrite;
            case ErrorReply error:
                Diag.Request(LogLevel.Info, request.Id, Target, "setting refused",
                    new LogField("path", path), new LogField("code", error.Code), new LogField("error", error.Message));
                return error.Message;
            default:
                return $"the core answered {reply.GetType().Name}";
        }
    }

    /// <summary>Sets a true/false setting.</summary>
    public Task<bool> SetAsync(string path, bool value) => SetAsync(path, Json(writer => writer.WriteBooleanValue(value)));

    /// <summary>Sets a text, such as <c>ui.sidebarView</c>.</summary>
    public Task<bool> SetAsync(string path, string value) => SetAsync(path, Json(writer => writer.WriteStringValue(value)));

    /// <summary>Sets a whole number, such as <c>ui.dockSize.bottom</c>.</summary>
    public Task<bool> SetAsync(string path, uint value) => SetAsync(path, Json(writer => writer.WriteNumberValue(value)));

    /// <summary>Sets a number that may have decimals, such as <c>ui.paneSplit</c>.</summary>
    public Task<bool> SetNumberAsync(string path, double value) => SetAsync(path, Json(writer => writer.WriteNumberValue(value)));

    /// <summary>Sets a setting to <c>null</c>, which gives its default back (<c>ui.paneSplit</c>, <c>ui.sidebarWidth</c>).</summary>
    public Task<bool> SetNullAsync(string path) => SetAsync(path, Json(writer => writer.WriteNullValue()));

    /// <summary>Sets a list of text, such as <c>ui.lastPaths</c>.</summary>
    public Task<bool> SetAsync(string path, IReadOnlyList<string> values, CancellationToken cancellationToken = default) =>
        SetAsync(path, Json(writer =>
        {
            writer.WriteStartArray();
            foreach (var value in values)
            {
                writer.WriteStringValue(value);
            }
            writer.WriteEndArray();
        }), cancellationToken);

    private static JsonElement Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
