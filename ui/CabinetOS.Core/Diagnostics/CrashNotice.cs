using System.Globalization;

namespace CabinetOS.Core.Diagnostics;

/// <summary>
/// The window's "Open crash folder" offer at the next start (docs/ui.md, "Heavy logging"): a
/// <c>crash-*.zip</c> in the log folder that is newer than the window's last start means the last run
/// crashed while heavy mode was on, and the zip holds the logs around the crash. The time of each start
/// is kept in one small file next to the logs, <c>ui.last-start</c>.
/// </summary>
public static class CrashNotice
{
    /// <summary>The file with the time of the last start, in the log folder.</summary>
    public const string MarkerName = "ui.last-start";

    /// <summary>How far back a crash bundle counts when there is no record of a last start: a day, as a bundle holds crash traces.</summary>
    public static readonly TimeSpan NoMarkerLookBack = TimeSpan.FromHours(24);

    /// <summary>
    /// Reads the time of the last start, writes <paramref name="nowUtc"/> as this start's, and returns the
    /// newest crash bundle made since the last start, or null. Reads and writes files: call it off the UI thread.
    /// </summary>
    public static string? CheckAtStart(string directory, DateTime nowUtc)
    {
        try
        {
            var previous = ReadMarker(directory);
            WriteMarker(directory, nowUtc);
            return NewestCrashBundle(directory, previous ?? nowUtc - NoMarkerLookBack);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read has no bundle to offer; the window starts all the same.
            return null;
        }
    }

    /// <summary>The newest <c>crash-*.zip</c> in <paramref name="directory"/> written after <paramref name="sinceUtc"/>, or null.</summary>
    public static string? NewestCrashBundle(string directory, DateTime sinceUtc) =>
        new DirectoryInfo(directory).EnumerateFiles("crash-*.zip")
            .Where(file => file.LastWriteTimeUtc > sinceUtc)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName)
            .FirstOrDefault();

    /// <summary>The time of the last start, or null when the marker is missing or unreadable.</summary>
    public static DateTime? ReadMarker(string directory)
    {
        var path = Path.Combine(directory, MarkerName);
        if (!File.Exists(path))
        {
            return null;
        }
        // "O" writes the kind (Z), which RoundtripKind reads back as UTC.
        return DateTime.TryParse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            ? time.ToUniversalTime()
            : null;
    }

    /// <summary>Records <paramref name="nowUtc"/> as the time of the last start.</summary>
    public static void WriteMarker(string directory, DateTime nowUtc) =>
        File.WriteAllText(Path.Combine(directory, MarkerName), nowUtc.ToString("O", CultureInfo.InvariantCulture));
}
