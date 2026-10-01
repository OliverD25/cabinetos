namespace CabinetOS.Tests.Support;

/// <summary>
/// Reads the log files a test's window and core wrote. Each process writes one file per UTC day
/// (<c>ui.2026-10-01.jsonl</c>, <c>core.2026-10-01.jsonl</c>), so a run that crosses midnight UTC
/// leaves two: a test reads them all, oldest first, and finds its evidence whichever file holds it.
/// </summary>
internal static class LogFiles
{
    /// <summary>The window's log lines in the folder, every file of the run oldest first; empty while there is none.</summary>
    public static List<string> Ui(string folder) => Lines(folder, "ui.*.jsonl");

    /// <summary>The core's log lines in the folder, every file of the run oldest first; empty while there is none.</summary>
    public static List<string> Core(string folder) => Lines(folder, "core.*.jsonl");

    /// <summary>
    /// The lines of every file in the folder that matches the pattern, one after another in file-name
    /// order: the date in the name puts the oldest first. Each file is opened as its writer shares it,
    /// so a window still running does not block the read.
    /// </summary>
    public static List<string> Lines(string folder, string pattern)
    {
        var lines = new List<string>();
        if (!Directory.Exists(folder))
        {
            return lines;
        }
        foreach (var path in Directory.GetFiles(folder, pattern).Order(StringComparer.Ordinal))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }
        }
        return lines;
    }
}
