using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CabinetOS.Core.Diagnostics;

/// <summary>One named value in a log line's <c>fields</c> object.</summary>
public readonly record struct LogField(string Name, object? Value);

/// <summary>
/// The JSON Lines format every CabinetOS process writes (docs/diagnostics.md):
/// one object per line with the keys <c>ts</c>, <c>level</c>, <c>boundary</c>,
/// <c>target</c>, <c>message</c>, <c>request_id</c>, <c>span</c>,
/// <c>fields</c>, <c>thread</c>, always in that order.
/// </summary>
public static class LogLine
{
    /// <summary>The boundary of every line the UI writes.</summary>
    public const string Boundary = "frontend";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // Log files are not embedded in HTML, so names in any script stay readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>RFC 3339 in UTC with milliseconds: <c>2026-09-28T01:02:03.004Z</c>.</summary>
    public static string Timestamp(DateTime utc) =>
        utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The level as the file writes it: <c>TRACE</c> … <c>ERROR</c>.</summary>
    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warn => "WARN",
        _ => "ERROR",
    };

    /// <summary>The name of the calling thread, or its ID as the core writes it.</summary>
    public static string CurrentThreadLabel()
    {
        var thread = Thread.CurrentThread;
        return string.IsNullOrEmpty(thread.Name)
            ? string.Create(CultureInfo.InvariantCulture, $"ThreadId({thread.ManagedThreadId})")
            : thread.Name;
    }

    /// <summary>Formats one line, without the trailing newline.</summary>
    public static string Format(
        DateTime utc,
        LogLevel level,
        string target,
        string message,
        string? requestId,
        string? span,
        IReadOnlyList<LogField>? fields,
        string thread)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("ts", Timestamp(utc));
            writer.WriteString("level", LevelName(level));
            writer.WriteString("boundary", Boundary);
            writer.WriteString("target", target);
            writer.WriteString("message", message);
            if (requestId is not null)
            {
                writer.WriteString("request_id", requestId);
            }
            if (span is not null)
            {
                writer.WriteString("span", span);
            }
            if (fields is { Count: > 0 })
            {
                writer.WriteStartObject("fields");
                foreach (var field in fields)
                {
                    writer.WritePropertyName(field.Name);
                    WriteValue(writer, field.Value);
                }
                writer.WriteEndObject();
            }
            writer.WriteString("thread", thread);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string text: writer.WriteStringValue(text); break;
            case bool flag: writer.WriteBooleanValue(flag); break;
            case int number: writer.WriteNumberValue(number); break;
            case uint number: writer.WriteNumberValue(number); break;
            case long number: writer.WriteNumberValue(number); break;
            case ulong number: writer.WriteNumberValue(number); break;
            case double number when double.IsFinite(number): writer.WriteNumberValue(number); break;
            case float number when float.IsFinite(number): writer.WriteNumberValue(number); break;
            case JsonElement element: element.WriteTo(writer); break;
            default: writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }
}
