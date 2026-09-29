using System.Buffers;
using System.Text.Json;
using CabinetOS.Core.Themes;

namespace CabinetOS.Core.Terminal;

/// <summary>
/// The terminal page's line height (xterm.js's, a multiple of its cell) and
/// the space around its text, in pixels: top, right, bottom, left.
/// </summary>
public sealed record TerminalSpacing(double LineHeight, double Top, double Right, double Bottom, double Left)
{
    /// <summary>
    /// From a theme's metrics. The page's default look is xterm.js's 1.25
    /// and 8, 4, 4, 12 px around the text, which the design writes as a CSS
    /// line height of 1.6 and a padding of 10 by 12; a theme's
    /// <c>terminalLineHeight</c>, <c>terminalPaddingY</c> and
    /// <c>terminalPaddingX</c> scale them by their share of those.
    /// </summary>
    public static TerminalSpacing From(ThemeMetrics metrics)
    {
        var y = metrics.TerminalPaddingY / 10;
        var x = metrics.TerminalPaddingX / 12;
        return new TerminalSpacing(
            Math.Round(1.25 * metrics.TerminalLineHeight / 1.6, 3),
            Math.Round(8 * y, 1),
            Math.Round(4 * x, 1),
            Math.Round(4 * y, 1),
            Math.Round(12 * x, 1));
    }
}

/// <summary>
/// A message from the terminal page (xterm.js in WebView2) to the window:
/// <c>ready</c>, <c>input</c> (keys as text), <c>binary</c> (keys as base64),
/// <c>resize</c> (the fit addon's cells), <c>buffer</c> (the alternate screen
/// came or went), or <c>key</c> (a shortcut of the window pressed in the terminal).
/// </summary>
public sealed record TerminalPageMessage(string Type, ulong Session, string? Data, int Cols, int Rows, bool Alternate, string? Keys);

/// <summary>
/// The web-message protocol between the window and its terminal page. The
/// page gets JSON strings (<c>PostWebMessageAsString</c>); output travels as
/// base64 so any bytes pass, split at any point (xterm.js joins UTF-8
/// sequences across writes).
/// </summary>
public static class TerminalPageMessages
{
    /// <summary>The largest message the window accepts from the page.</summary>
    public const int MaxIncomingLength = 1024 * 1024;

    /// <summary>Reads a page message; null for anything malformed, too long or unknown.</summary>
    public static TerminalPageMessage? Parse(string? json)
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
            var session = root.TryGetProperty("session", out var s) && s.TryGetUInt64(out var id) ? id : 0;
            return type switch
            {
                "ready" => new TerminalPageMessage(type, 0, null, 0, 0, false, null),
                "input" or "binary" when session > 0 && Text(root, "data") is { } data =>
                    new TerminalPageMessage(type, session, data, 0, 0, false, null),
                "resize" when session > 0 && Int(root, "cols") is >= 1 and <= 32767 and var cols && Int(root, "rows") is >= 1 and <= 32767 and var rows =>
                    new TerminalPageMessage(type, session, null, cols, rows, false, null),
                "buffer" when session > 0 && root.TryGetProperty("alternate", out var alternate) && alternate.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                    new TerminalPageMessage(type, session, null, 0, 0, alternate.GetBoolean(), null),
                "key" when Text(root, "keys") is { Length: > 0 and < 64 } keys =>
                    new TerminalPageMessage(type, 0, null, 0, 0, false, keys),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The bytes of an <c>input</c> or <c>binary</c> message for the shell.</summary>
    public static byte[] Bytes(TerminalPageMessage message) =>
        message.Type == "binary" ? DecodeBase64(message.Data) : System.Text.Encoding.UTF8.GetBytes(message.Data ?? "");

    /// <summary>A terminal for a new session.</summary>
    public static string Create(ulong session) => Write(w => { w.WriteString("type", "create"); w.WriteNumber("session", session); });

    /// <summary>Output for a session, one base64 chunk.</summary>
    public static string Output(ulong session, string base64) =>
        Write(w => { w.WriteString("type", "output"); w.WriteNumber("session", session); w.WriteString("data", base64); });

    /// <summary>Shows one session's terminal (the tabs).</summary>
    public static string Show(ulong session) => Write(w => { w.WriteString("type", "show"); w.WriteNumber("session", session); });

    /// <summary>Drops a session's terminal.</summary>
    public static string Close(ulong session) => Write(w => { w.WriteString("type", "close"); w.WriteNumber("session", session); });

    /// <summary>The shell ended: the page writes a last line.</summary>
    public static string Exited(ulong session, uint code) =>
        Write(w => { w.WriteString("type", "exited"); w.WriteNumber("session", session); w.WriteNumber("code", code); });

    /// <summary>Gives the shown terminal the keyboard.</summary>
    public static string Focus() => Write(w => w.WriteString("type", "focus"));

    /// <summary>The window's shortcuts the page passes on instead of sending to the shell.</summary>
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

    /// <summary>
    /// Colours and font: the theme's terminal colours (docs/themes.md), and
    /// its 16 ANSI colours in order (black, red, green, yellow, blue, magenta,
    /// cyan, white, then the bright ones) when it has them; with
    /// <paramref name="spacing"/>, the line height and the space around the
    /// text (the theme's metrics).
    /// </summary>
    public static string Theme(string background, string foreground, string cursor, string selection, string fontFamily, int fontSize,
        IReadOnlyList<string>? ansi = null, TerminalSpacing? spacing = null) => Write(w =>
    {
        w.WriteString("type", "theme");
        w.WriteString("background", background);
        w.WriteString("foreground", foreground);
        w.WriteString("cursor", cursor);
        w.WriteString("selection", selection);
        w.WriteString("fontFamily", fontFamily);
        w.WriteNumber("fontSize", fontSize);
        if (ansi is { Count: 16 })
        {
            w.WriteStartArray("ansi");
            foreach (var color in ansi)
            {
                w.WriteStringValue(color);
            }
            w.WriteEndArray();
        }
        if (spacing is { } s)
        {
            w.WriteNumber("lineHeight", s.LineHeight);
            w.WriteStartArray("padding");
            foreach (var side in (double[])[s.Top, s.Right, s.Bottom, s.Left])
            {
                w.WriteNumberValue(side);
            }
            w.WriteEndArray();
        }
    });

    private static byte[] DecodeBase64(string? data)
    {
        try
        {
            return Convert.FromBase64String(data ?? "");
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

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
