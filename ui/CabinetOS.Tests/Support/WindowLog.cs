using System.Text.Json;

namespace CabinetOS.Tests.Support;

/// <summary>
/// What a failed check of a window test shows: the window's log lines with their times (UTC). A check that says only
/// "expected 6, was 5" does not say what the window did late; the lines before and after it do.
/// </summary>
internal static class WindowLog
{
    // Frame lines say nothing about what a check read.
    private static readonly string[] Noise = ["slow frame", "frame stats"];

    /// <summary>The last <paramref name="last"/> lines <paramref name="keep"/> accepts (all lines when it is null), one per line of text.</summary>
    public static string Last(IReadOnlyList<string> logs, int last = 60, Func<string, bool>? keep = null, int fieldChars = 240) =>
        string.Join('\n', logs.Where(l => keep?.Invoke(l) ?? true).Select(l => Describe(l, fieldChars)).Where(d => d is not null).TakeLast(last));

    /// <summary>The lines from <paramref name="before"/> before the line at <paramref name="index"/> to <paramref name="after"/> after it.</summary>
    public static string Around(IReadOnlyList<string> logs, int index, int before = 40, int after = 25, int fieldChars = 240)
    {
        if (index < 0)
        {
            return Last(logs, before + after, null, fieldChars);
        }
        var from = Math.Max(0, index - before);
        var to = Math.Min(logs.Count, index + after + 1);
        return string.Join('\n', Enumerable.Range(from, to - from).Select(i => Describe(logs[i], fieldChars)).Where(d => d is not null));
    }

    private static string? Describe(string line, int fieldChars)
    {
        try
        {
            using var parsed = JsonDocument.Parse(line);
            var message = parsed.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            if (message is null || Noise.Contains(message))
            {
                return null;
            }
            var time = parsed.RootElement.TryGetProperty("ts", out var ts) ? ts.GetString() : "";
            var fields = parsed.RootElement.TryGetProperty("fields", out var f) ? f.ToString() : "";
            return $"  {time} {message} {(fields.Length > fieldChars ? fields[..fieldChars] : fields)}";
        }
        catch (JsonException)
        {
            return $"  (not JSON) {(line.Length > fieldChars ? line[..fieldChars] : line)}";
        }
    }
}
