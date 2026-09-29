using System.Globalization;
using System.Security.AccessControl;

namespace CabinetOS.Core.Diagnostics;

/// <summary>
/// The sizes heavy mode keeps to. <see cref="Default"/> is what docs/diagnostics.md says; the
/// tests use smaller ones.
/// </summary>
/// <param name="QueueBytes">Bytes of lines that may wait for the writer before a thread that logs waits for it.</param>
/// <param name="NeverWaitExtraBytes">How far the lines of threads that never wait may go over <paramref name="QueueBytes"/> before they are dropped.</param>
/// <param name="FileBytes">The most one heavy file holds before the next part starts.</param>
/// <param name="DiskBytes">The most the heavy files of every process in the folder may hold together.</param>
/// <param name="CheckEveryBytes">How often, in bytes written, the folder is measured against <paramref name="DiskBytes"/>.</param>
public sealed record HeavyLimits(long QueueBytes, long NeverWaitExtraBytes, long FileBytes, long DiskBytes, long CheckEveryBytes)
{
    private const long MiB = 1024 * 1024;

    /// <summary>
    /// A queue of 64 MiB (the core's is 256 MiB: the window has less to log), 8 MiB more for
    /// the UI thread and the pipe's reader, files of 256 MiB, 2 GiB for the folder, checked
    /// every 64 MiB: the core's numbers.
    /// </summary>
    public static HeavyLimits Default { get; } = new(64 * MiB, 8 * MiB, 256 * MiB, 2048 * MiB, 64 * MiB);
}

/// <summary>
/// The heavy files of one process: <c>heavy-&lt;process&gt;.&lt;date&gt;[.&lt;part&gt;].jsonl</c>, the one written
/// now, the next part, the next day, and the folder's cap (docs/diagnostics.md, "Heavy mode").
/// The process name comes after <c>heavy-</c> because the normal log's writer deletes old files by
/// the process name at the start of the file name. Only the writer's thread uses it.
/// </summary>
internal sealed class HeavyFiles(string directory, string process, HeavyLimits limits, Func<DateTime> clock,
    Action<string, LogField[]> note)
{
    /// <summary>Every heavy file's name starts with this.</summary>
    public const string Prefix = "heavy-";

    /// <summary>The targets of lines only heavy mode writes start with this.</summary>
    public const string TargetPrefix = "heavy::";

    private FileStream? _file;
    private string _path = "";
    private string _date = "";
    private long _size;
    private long _sinceCheck;

    /// <summary>The file being written, or null while none is open.</summary>
    public string? OpenPath => _file is null ? null : _path;

    /// <summary><c>heavy-ui.2026-09-29.jsonl</c>, and <c>heavy-ui.2026-09-29.3.jsonl</c> for part 3.</summary>
    public static string FileName(string process, string date, int part) =>
        part == 0 ? $"{Prefix}{process}.{date}.jsonl" : $"{Prefix}{process}.{date}.{part.ToString(CultureInfo.InvariantCulture)}.jsonl";

    /// <summary>
    /// Appends <paramref name="bytes"/> in one write. A file that cannot be opened or written is
    /// left closed; the next batch tries again.
    /// </summary>
    public void Write(byte[] bytes, int count)
    {
        var today = clock().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_file is null || _date != today || _size >= limits.FileBytes)
        {
            Close();
            Open(today);
            EnforceCap();
        }
        try
        {
            _file!.Write(bytes, 0, count);
        }
        catch (IOException)
        {
            // A full disk must not end the writer; the next batch opens the file again.
            Close();
            throw;
        }
        _size += count;
        _sinceCheck += count;
        if (_sinceCheck >= limits.CheckEveryBytes)
        {
            EnforceCap();
        }
    }

    /// <summary>Closes the file; the next write opens it again.</summary>
    public void Close()
    {
        _file?.Dispose();
        _file = null;
    }

    /// <summary>
    /// Deletes the oldest heavy files in <paramref name="directory"/> (by date, then part, then
    /// name) until all of them hold at most <paramref name="cap"/> bytes. <paramref name="keep"/>
    /// (the file this process writes) and a file another process holds open are never deleted.
    /// Returns each deleted file's name and size.
    /// </summary>
    public static List<(string Name, long Size)> EnforceDiskCap(string directory, long cap, string? keep)
    {
        var deleted = new List<(string, long)>();
        List<(string Date, int Part, string Name, string Path, long Size)> files = [];
        try
        {
            foreach (var info in new DirectoryInfo(directory).EnumerateFiles(Prefix + "*.jsonl"))
            {
                if (Parse(info.Name) is { } order)
                {
                    files.Add((order.Date, order.Part, info.Name, info.FullName, info.Length));
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return deleted;
        }
        var total = files.Sum(f => f.Size);
        if (total <= cap)
        {
            return deleted;
        }
        files.Sort((a, b) =>
        {
            var byDate = string.CompareOrdinal(a.Date, b.Date);
            if (byDate != 0)
            {
                return byDate;
            }
            return a.Part != b.Part ? a.Part.CompareTo(b.Part) : string.CompareOrdinal(a.Name, b.Name);
        });
        foreach (var file in files)
        {
            if (total <= cap)
            {
                break;
            }
            if (keep is not null && string.Equals(file.Path, keep, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                File.Delete(file.Path);
                total -= file.Size;
                deleted.Add((file.Name, file.Size));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Another process writes it (heavy files are opened without delete sharing): its turn
                // comes when that process moves on to its next file.
            }
        }
        return deleted;
    }

    // "heavy-ui.2026-09-29.jsonl" is (2026-09-29, 0); "heavy-ui.2026-09-29.3.jsonl" is (2026-09-29, 3).
    private static (string Date, int Part)? Parse(string name)
    {
        if (!name.EndsWith(".jsonl", StringComparison.Ordinal))
        {
            return null;
        }
        var stem = name[Prefix.Length..^".jsonl".Length];
        var firstDot = stem.IndexOf('.');
        if (firstDot < 0)
        {
            return null;
        }
        var stamp = stem[(firstDot + 1)..];
        var partDot = stamp.IndexOf('.');
        if (partDot < 0)
        {
            return (stamp, 0);
        }
        return (stamp[..partDot], int.TryParse(stamp[(partDot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var part) ? part : 0);
    }

    // Opens the first part of the date that is not full yet.
    private void Open(string date)
    {
        System.IO.Directory.CreateDirectory(directory);
        for (var part = 0; ; part++)
        {
            var path = Path.Combine(directory, FileName(process, date, part));
            var size = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (size >= limits.FileBytes)
            {
                continue;
            }
            // Append-only (FILE_APPEND_DATA), so another window's lines never overwrite these; no
            // delete sharing, so no process's cap deletes the file while it is written.
            _file = new FileInfo(path).Create(FileMode.Append, FileSystemRights.AppendData | FileSystemRights.Synchronize,
                FileShare.ReadWrite, bufferSize: 1, FileOptions.None, fileSecurity: null);
            _path = path;
            _date = date;
            _size = size;
            return;
        }
    }

    private void EnforceCap()
    {
        _sinceCheck = 0;
        var deleted = EnforceDiskCap(directory, limits.DiskBytes, _path);
        if (deleted.Count > 0)
        {
            note("heavy log files deleted to keep the folder under its cap",
            [
                new LogField("deleted", string.Join(", ", deleted.Select(d => d.Name))),
                new LogField("freed_bytes", deleted.Sum(d => d.Size)),
                new LogField("cap_bytes", limits.DiskBytes),
            ]);
        }
    }
}
