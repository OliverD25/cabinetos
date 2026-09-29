using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CabinetOS.Core.Diagnostics;

/// <summary>What a bundle says about the program and the machine (<c>bundle.json</c>).</summary>
/// <param name="Process">The process that made the bundle: <c>ui</c>.</param>
/// <param name="Version">The program's version.</param>
/// <param name="Protocol">The protocol version the core spoke, when the window knows it.</param>
/// <param name="WindowsBuild">The Windows version and build.</param>
/// <param name="Config">The configuration the core last sent, secrets already masked; null when the window never read it.</param>
public sealed record BundleFacts(string Process, string Version, uint? Protocol, string? WindowsBuild, JsonNode? Config);

/// <summary>
/// A log bundle: one zip with what someone needs to find a problem, the last minutes of every
/// process's logs (docs/diagnostics.md, "Bundles"). The core makes the same zip on request
/// (<c>save_log_bundle</c>); the window makes it, as <c>crash-&lt;time&gt;.zip</c>, when it crashes while heavy
/// mode is on. A bundle holds, from the log folder:
/// <list type="bullet">
/// <item>every <c>*.jsonl</c> file (every process, normal and heavy files), cut to the lines of the last
/// minutes, under its own name; a file with no such line is left out;</item>
/// <item>every <c>crash-*.json</c> of the last 24 hours;</item>
/// <item><c>bundle.json</c>: when and why the bundle was made, the versions, the Windows build, the
/// <c>CABINETOS_*</c> environment variables and the configuration, secrets masked, and the files.</item>
/// </list>
/// It reads only the files on disk, so a crash hook needs nothing from the threads that log.
/// </summary>
public static class LogBundle
{
    /// <summary>The minutes a bundle holds when nobody says otherwise.</summary>
    public const int DefaultMinutes = 10;

    /// <summary>The most minutes a bundle may hold: a day.</summary>
    public const int MaxMinutes = 24 * 60;

    /// <summary>A log file is read backwards in pieces this large, until a piece holds only older lines.</summary>
    private const int Chunk = 1024 * 1024;

    private static readonly TimeSpan CrashesKeptFor = TimeSpan.FromHours(24);

    /// <summary><c>&lt;prefix&gt;-&lt;YYYYMMDDTHHMMSSmmmZ&gt;.zip</c>, as the core names them.</summary>
    public static string NameFor(string prefix, DateTime utc) =>
        $"{prefix}-{utc.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)}.zip";

    /// <summary>
    /// Writes the bundle of <paramref name="directory"/> into the new file <paramref name="path"/>: the
    /// lines of the last <paramref name="minutes"/> (1 to 1,440) of every log file there, the crash
    /// traces of the last day, and <c>bundle.json</c>. <paramref name="reason"/> is <c>asked</c> or <c>crash</c>.
    /// </summary>
    public static void Write(string directory, string path, DateTime nowUtc, int minutes, string reason, BundleFacts facts)
    {
        minutes = Math.Clamp(minutes, 1, MaxMinutes);
        var since = LogLine.Timestamp(nowUtc - TimeSpan.FromMinutes(minutes));
        var crashSince = nowUtc - CrashesKeptFor;
        var names = new DirectoryInfo(directory).EnumerateFiles().OrderBy(f => f.Name, StringComparer.Ordinal).ToList();

        var included = new List<(string Name, int? Lines, long Bytes)>();
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var file in names)
            {
                if (file.Extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
                {
                    // No test of the file's time: Windows keeps it stale while a process holds the file open. The
                    // backwards read stops after one piece for a file with nothing recent.
                    var (lines, count) = RecentLines(file.FullName, since);
                    if (count > 0)
                    {
                        Add(zip, file.Name, lines);
                        included.Add((file.Name, count, lines.Length));
                    }
                }
                else if (file.Name.StartsWith("crash-", StringComparison.Ordinal)
                    && file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) && file.LastWriteTimeUtc >= crashSince)
                {
                    var bytes = ReadShared(file.FullName);
                    Add(zip, file.Name, bytes);
                    included.Add((file.Name, null, bytes.Length));
                }
            }
            Add(zip, "bundle.json", Manifest(nowUtc, reason, minutes, since, facts, included));
        }
    }

    /// <summary>
    /// The lines of <paramref name="path"/> stamped <paramref name="since"/> or later, as bytes with their
    /// line ends, and how many there are. Reads from the end, a piece at a time, and stops at a piece
    /// that holds only older lines: the files are written in time order, give or take the lines of two
    /// processes that share one. A line without a stamp cannot be placed, so it stays with its neighbours.
    /// </summary>
    public static (byte[] Lines, int Count) RecentLines(string path, string since)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var end = file.Length;
        // The front of the piece read last, which may be the end of a line cut at its start.
        var carry = Array.Empty<byte>();
        var pieces = new List<byte[]>();
        var count = 0;
        while (end > 0)
        {
            var start = Math.Max(0, end - Chunk);
            var buffer = new byte[(end - start) + carry.Length];
            file.Seek(start, SeekOrigin.Begin);
            file.ReadExactly(buffer, 0, (int)(end - start));
            Buffer.BlockCopy(carry, 0, buffer, (int)(end - start), carry.Length);
            var body = 0;
            if (start > 0)
            {
                var first = Array.IndexOf(buffer, (byte)'\n');
                if (first < 0)
                {
                    carry = buffer;
                    end = start;
                    continue;
                }
                carry = buffer[..(first + 1)];
                body = first + 1;
            }
            else
            {
                carry = [];
            }
            var (kept, lines, seen, old) = KeepRecent(buffer.AsSpan(body), since);
            count += lines;
            pieces.Add(kept);
            end = start;
            if (seen > 0 && old == seen)
            {
                break;
            }
        }
        pieces.Reverse();
        return (pieces.SelectMany(p => p).ToArray(), count);
    }

    // The lines of the body stamped since or later, with a line end each; how many they are, how many
    // lines had a stamp, and how many of those were older.
    private static (byte[] Kept, int Lines, int Seen, int Old) KeepRecent(ReadOnlySpan<byte> body, string since)
    {
        var sinceBytes = Encoding.ASCII.GetBytes(since);
        var kept = new List<byte>(body.Length);
        var lines = 0;
        var seen = 0;
        var old = 0;
        while (!body.IsEmpty)
        {
            var newline = body.IndexOf((byte)'\n');
            var line = newline < 0 ? body : body[..newline];
            body = newline < 0 ? default : body[(newline + 1)..];
            if (line.IsEmpty)
            {
                continue;
            }
            var keep = true;
            var stamp = StampOf(line);
            if (!stamp.IsEmpty)
            {
                seen++;
                keep = stamp.SequenceCompareTo(sinceBytes) >= 0;
                if (!keep)
                {
                    old++;
                }
            }
            if (keep)
            {
                kept.AddRange(line);
                kept.Add((byte)'\n');
                lines++;
            }
        }
        return (kept.ToArray(), lines, seen, old);
    }

    // Every writer puts the stamp first: {"ts":"2026-09-30T01:02:03.004Z",...
    private static ReadOnlySpan<byte> StampOf(ReadOnlySpan<byte> line)
    {
        ReadOnlySpan<byte> prefix = "{\"ts\":\""u8;
        return line.StartsWith(prefix) && line.Length >= prefix.Length + 24 ? line.Slice(prefix.Length, 24) : default;
    }

    private static byte[] ReadShared(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        file.CopyTo(memory);
        return memory.ToArray();
    }

    // Fast rather than small: a crash bundle is written while the process goes down.
    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var target = entry.Open();
        target.Write(bytes);
    }

    private static byte[] Manifest(DateTime nowUtc, string reason, int minutes, string since, BundleFacts facts,
        List<(string Name, int? Lines, long Bytes)> included)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("created", LogLine.Timestamp(nowUtc));
            writer.WriteString("reason", reason);
            writer.WriteString("process", facts.Process);
            writer.WriteNumber("minutes", minutes);
            writer.WriteString("since", since);
            writer.WriteStartObject("versions");
            writer.WriteString("cabinetos", facts.Version);
            if (facts.Protocol is { } protocol)
            {
                writer.WriteNumber("protocol", protocol);
            }
            else
            {
                writer.WriteNull("protocol");
            }
            writer.WriteEndObject();
            writer.WriteString("windows_build", facts.WindowsBuild);
            writer.WriteStartObject("environment");
            foreach (var (name, value) in CabinetosEnvironment())
            {
                writer.WriteString(name, value);
            }
            writer.WriteEndObject();
            writer.WritePropertyName("config");
            if (facts.Config is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                facts.Config.WriteTo(writer);
            }
            writer.WriteStartArray("files");
            foreach (var (name, lines, bytes) in included)
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                if (lines is { } count)
                {
                    writer.WriteNumber("lines", count);
                }
                writer.WriteNumber("bytes", bytes);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    // The CABINETOS_* variables, those that look like keys masked.
    private static IEnumerable<(string Name, string Value)> CabinetosEnvironment() =>
        System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(entry => (Name: (string)entry.Key, Value: entry.Value as string ?? ""))
            .Where(variable => variable.Name.StartsWith("CABINETOS_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(variable => variable.Name, StringComparer.Ordinal)
            .Select(variable => (variable.Name, LogMask.IsSecretEnvironmentVariable(variable.Name) ? LogMask.Mask : variable.Value));
}
