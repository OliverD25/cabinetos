using System.Diagnostics;
using System.Globalization;

namespace CabinetOS.Core.Diagnostics;

/// <summary>What a start found out about the run before it: its crash bundle, if it made one, and when it started if it ended without closing.</summary>
/// <param name="CrashBundle">The newest <c>crash-*.zip</c> made since the last start, or null.</param>
/// <param name="UncleanStartUtc">The start time of a previous run that never wrote its clean end and is not running now, or null.</param>
public sealed record StartCheck(string? CrashBundle, DateTime? UncleanStartUtc);

/// <summary>What the marker file says about the last run: when it started, which process it was, and when it closed (null: it did not, yet).</summary>
/// <param name="StartedUtc">When the run started.</param>
/// <param name="ProcessId">The run's process ID; null in a marker an older window wrote, which has no clean end either.</param>
/// <param name="ClosedUtc">When the run ended cleanly, or null.</param>
public sealed record PreviousRun(DateTime StartedUtc, int? ProcessId, DateTime? ClosedUtc);

/// <summary>
/// What the window learns about the run before it (docs/ui.md, "Heavy logging"; docs/diagnostics.md, "Last start"):
/// a <c>crash-*.zip</c> in the log folder that is newer than the last start means the last run crashed while heavy mode
/// was on, and the zip holds the logs around the crash; and a marker that says the last run started but never closed
/// means it ended without a word, as a native failure of WinUI does (no crash trace, the log just stops). The marker is
/// one small file next to the logs, <c>ui.last-start</c>: the start time on its first line, then <c>pid &lt;id&gt;</c>,
/// then <c>closed &lt;time&gt;</c> once the run ended cleanly.
/// </summary>
public static class CrashNotice
{
    /// <summary>The file with the time of the last start, in the log folder.</summary>
    public const string MarkerName = "ui.last-start";

    /// <summary>How far back a crash bundle counts when there is no record of a last start: a day, as a bundle holds crash traces.</summary>
    public static readonly TimeSpan NoMarkerLookBack = TimeSpan.FromHours(24);

    // A run's recorded start comes after its process started, by its own start-up; this much is allowed for the two clocks.
    private static readonly TimeSpan ProcessClockSlack = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reads the last run's marker, writes <paramref name="nowUtc"/> and <paramref name="processId"/> as this start's, and returns
    /// the newest crash bundle made since the last start and whether the last run ended without closing. Reads and writes files:
    /// call it off the UI thread.
    /// </summary>
    public static StartCheck CheckAtStart(string directory, DateTime nowUtc, int? processId = null)
    {
        try
        {
            var previous = ReadRun(directory);
            WriteMarker(directory, nowUtc, processId ?? Environment.ProcessId);
            return new StartCheck(
                NewestCrashBundle(directory, previous?.StartedUtc ?? nowUtc - NoMarkerLookBack),
                previous is { ProcessId: not null, ClosedUtc: null } && !IsRunning(previous) ? previous.StartedUtc : null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read has no bundle to offer; the window starts all the same.
            return new StartCheck(null, null);
        }
    }

    /// <summary>
    /// Writes the clean end of this run into the marker, if the marker is still this run's: another window that started
    /// meanwhile has written its own, and is left alone. Called when the window has closed; never throws.
    /// </summary>
    public static void MarkClosed(string directory, DateTime nowUtc, int? processId = null)
    {
        try
        {
            if (ReadRun(directory) is { ProcessId: { } id, ClosedUtc: null } run && id == (processId ?? Environment.ProcessId))
            {
                WriteMarker(directory, run.StartedUtc, id, nowUtc);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The next start then thinks this run ended without closing: a line in the log, nothing more.
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
    public static DateTime? ReadMarker(string directory) => ReadRun(directory)?.StartedUtc;

    /// <summary>The last run as the marker says it, or null when the marker is missing or its first line is not a time.</summary>
    public static PreviousRun? ReadRun(string directory)
    {
        var path = Path.Combine(directory, MarkerName);
        if (!File.Exists(path))
        {
            return null;
        }
        var lines = File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (lines.Length == 0 || ParseTime(lines[0]) is not { } started)
        {
            return null;
        }
        int? processId = null;
        DateTime? closed = null;
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("pid ", StringComparison.Ordinal) && int.TryParse(line[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                processId = id;
            }
            else if (line.StartsWith("closed ", StringComparison.Ordinal))
            {
                closed = ParseTime(line[7..]);
            }
        }
        return new PreviousRun(started, processId, closed);
    }

    /// <summary>Records <paramref name="nowUtc"/> as the time of the last start, with the process and, once there is one, the clean end.</summary>
    public static void WriteMarker(string directory, DateTime nowUtc, int? processId = null, DateTime? closedUtc = null)
    {
        var text = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        if (processId is { } id)
        {
            text += $"{Environment.NewLine}pid {id.ToString(CultureInfo.InvariantCulture)}";
            if (closedUtc is { } closed)
            {
                text += $"{Environment.NewLine}closed {closed.ToString("O", CultureInfo.InvariantCulture)}";
            }
        }
        File.WriteAllText(Path.Combine(directory, MarkerName), text);
    }

    // "O" writes the kind (Z), which RoundtripKind reads back as UTC.
    private static DateTime? ParseTime(string text) =>
        DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time.ToUniversalTime() : null;

    // The process of that ID, if it is the one that wrote the marker: a process that started after the marker's time is
    // another one that was given the same ID.
    private static bool IsRunning(PreviousRun run)
    {
        try
        {
            using var process = Process.GetProcessById(run.ProcessId!.Value);
            return process.StartTime.ToUniversalTime() <= run.StartedUtc + ProcessClockSlack;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // No such process, or one the window may not look at: not the window that wrote the marker.
            return false;
        }
    }
}
