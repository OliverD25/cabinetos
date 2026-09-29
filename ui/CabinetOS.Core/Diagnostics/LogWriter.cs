using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CabinetOS.Core.Diagnostics;

/// <summary>
/// Writes the UI's JSON Lines log, <c>ui.&lt;UTC date&gt;.jsonl</c>, on a
/// background thread (docs/diagnostics.md, Article 12).
/// </summary>
/// <remarks>
/// A caller formats its line and queues it; it never waits for the disk and
/// takes no lock (the queue and the ring of recent lines are lock-free). When
/// the writer falls more than <see cref="QueueCapacity"/> lines behind, new
/// lines are dropped and counted instead of stalling the caller.
/// </remarks>
public sealed class LogWriter : IDisposable
{
    /// <summary>Lines that may wait for the writer before new ones are dropped.</summary>
    public const int QueueCapacity = 16_384;

    /// <summary>Recent lines kept for a crash trace, as in the core.</summary>
    public const int RingCapacity = 256;

    /// <summary>Daily files kept, today's included, as in the core.</summary>
    public const int KeptLogFiles = 14;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ConcurrentQueue<string> _queue = new();
    private readonly string?[] _ring = new string?[RingCapacity];
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private readonly Func<DateTime> _clock;
    private readonly StringBuilder _batch = new();
    private long _ringNext;
    private int _queued;
    private long _dropped;
    private long _droppedReported;
    private int _sleeping;
    private int _busy;
    private volatile bool _stopping;
    private FileStream? _file;
    private DateOnly _fileDate;

    /// <summary>Starts the writer thread for <paramref name="directory"/>.</summary>
    public LogWriter(string directory, LogFilter filter, string process = "ui", string version = "0.1.0", Func<DateTime>? clock = null)
    {
        Directory = directory;
        Filter = filter;
        Process = process;
        Version = version;
        _clock = clock ?? (() => DateTime.UtcNow);
        System.IO.Directory.CreateDirectory(directory);
        _thread = new Thread(Run) { IsBackground = true, Name = "diag-writer" };
        _thread.Start();
    }

    /// <summary>The directory of the log files and crash traces.</summary>
    public string Directory { get; }

    /// <summary>The level filter in effect.</summary>
    public LogFilter Filter { get; }

    /// <summary>The process name in file names and crash traces: <c>ui</c>.</summary>
    public string Process { get; }

    /// <summary>The program version written into crash traces.</summary>
    public string Version { get; }

    /// <summary>Lines dropped because the writer fell behind.</summary>
    public long DroppedLines => Interlocked.Read(ref _dropped);

    /// <summary>The file today's lines go to.</summary>
    public string CurrentFilePath => FilePathFor(DateOnly.FromDateTime(_clock()));

    /// <summary>Whether an event would be written.</summary>
    public bool IsEnabled(LogLevel level, string target) => Filter.IsEnabled(level, target);

    /// <summary>Formats and queues one event, if the filter lets it through.</summary>
    public void Write(
        LogLevel level,
        string target,
        string message,
        string? requestId = null,
        string? span = null,
        IReadOnlyList<LogField>? fields = null,
        string? traceId = null)
    {
        if (!Filter.IsEnabled(level, target))
        {
            return;
        }
        var line = LogLine.Format(_clock(), level, target, message, traceId, requestId, span, fields, LogLine.CurrentThreadLabel());
        Enqueue(line);
    }

    /// <summary>Queues an already formatted line.</summary>
    public void Enqueue(string line)
    {
        var slot = Interlocked.Increment(ref _ringNext) - 1;
        Volatile.Write(ref _ring[slot % RingCapacity], line);

        if (Interlocked.Increment(ref _queued) > QueueCapacity)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return;
        }
        _queue.Enqueue(line);
        if (Volatile.Read(ref _sleeping) == 1 && Interlocked.CompareExchange(ref _sleeping, 0, 1) == 1)
        {
            _signal.Set();
        }
    }

    /// <summary>The last lines logged (at most <see cref="RingCapacity"/>), oldest first.</summary>
    public IReadOnlyList<string> RecentLines()
    {
        var end = Interlocked.Read(ref _ringNext);
        var start = Math.Max(0, end - RingCapacity);
        var lines = new List<string>((int)(end - start));
        for (var slot = start; slot < end; slot++)
        {
            var line = Volatile.Read(ref _ring[slot % RingCapacity]);
            if (line is not null)
            {
                lines.Add(line);
            }
        }
        return lines;
    }

    /// <summary>
    /// Waits until every queued line is in the file, at most
    /// <paramref name="timeout"/>. Returns whether it got there.
    /// </summary>
    public bool Flush(TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Volatile.Read(ref _queued) > 0 || Volatile.Read(ref _busy) == 1)
        {
            if (!_thread.IsAlive || Stopwatch.GetTimestamp() > deadline)
            {
                return false;
            }
            _signal.Set();
            Thread.Sleep(2);
        }
        return true;
    }

    /// <summary>
    /// Writes <c>crash-&lt;timestamp&gt;.json</c> in the core's crash format with
    /// boundary <c>frontend</c> and the recent lines, then flushes the log.
    /// Returns the file's path, or null when it could not be written.
    /// </summary>
    public string? WriteCrashReport(Exception? exception, string message)
    {
        string? path = null;
        try
        {
            var json = CrashReportJson(exception, message, LogLine.CurrentThreadLabel(), RecentLines());
            path = WriteCrashFile(json);
        }
        catch (Exception)
        {
            // The process is going down; a failed trace must not hide the crash.
        }
        Flush(TimeSpan.FromSeconds(2));
        return path;
    }

    /// <summary>The crash trace's JSON text (docs/diagnostics.md, "Crash traces").</summary>
    public string CrashReportJson(Exception? exception, string message, string thread, IReadOnlyList<string> recent)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("boundary", LogLine.Boundary);
            writer.WriteString("process", Process);
            writer.WriteString("version", Version);
            writer.WriteString("message", exception is null ? message : $"{message}: {exception.GetType().FullName}: {exception.Message}");
            var frame = exception is null
                ? null
                : new StackTrace(exception, fNeedFileInfo: true).GetFrames().FirstOrDefault(f => f.GetFileName() is not null);
            if (frame is null)
            {
                writer.WriteNull("location");
            }
            else
            {
                writer.WriteStartObject("location");
                writer.WriteString("file", frame.GetFileName());
                writer.WriteNumber("line", frame.GetFileLineNumber());
                writer.WriteNumber("column", frame.GetFileColumnNumber());
                writer.WriteEndObject();
            }
            writer.WriteString("thread", thread);
            writer.WriteString("backtrace", exception?.ToString() ?? new StackTrace(fNeedFileInfo: true).ToString());
            writer.WriteStartArray("recent_events");
            foreach (var line in recent)
            {
                try
                {
                    using var parsed = JsonDocument.Parse(line);
                    parsed.RootElement.WriteTo(writer);
                }
                catch (JsonException)
                {
                    writer.WriteStringValue(line);
                }
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Stops the writer after it has written everything queued.</summary>
    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }
        _stopping = true;
        _signal.Set();
        _thread.Join(TimeSpan.FromSeconds(3));
        _signal.Dispose();
    }

    private string FilePathFor(DateOnly date) =>
        Path.Combine(Directory, $"{Process}.{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.jsonl");

    private string WriteCrashFile(string json)
    {
        var stamp = _clock().ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var path = Path.Combine(Directory, $"crash-{stamp}.json");
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        catch (IOException) when (File.Exists(path))
        {
            path = Path.Combine(Directory, $"crash-{stamp}-{Environment.ProcessId}.json");
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        using (stream)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            stream.Write(bytes);
        }
        return path;
    }

    private void Run()
    {
        while (true)
        {
            Drain();
            if (_stopping && _queue.IsEmpty)
            {
                break;
            }
            Interlocked.Exchange(ref _sleeping, 1);
            if (!_queue.IsEmpty || _stopping)
            {
                Interlocked.Exchange(ref _sleeping, 0);
                continue;
            }
            _signal.WaitOne(TimeSpan.FromSeconds(1));
            Interlocked.Exchange(ref _sleeping, 0);
        }
        _file?.Dispose();
        _file = null;
    }

    private void Drain()
    {
        Volatile.Write(ref _busy, 1);
        try
        {
            _batch.Clear();
            while (_queue.TryDequeue(out var line))
            {
                Interlocked.Decrement(ref _queued);
                _batch.Append(line).Append('\n');
            }
            var dropped = Interlocked.Read(ref _dropped);
            if (dropped != _droppedReported)
            {
                var fields = new[] { new LogField("dropped", dropped - _droppedReported) };
                _droppedReported = dropped;
                _batch.Append(LogLine.Format(_clock(), LogLevel.Warn, "cabinetos_ui::diag", "the log writer fell behind and dropped lines", null, null, fields, LogLine.CurrentThreadLabel()))
                    .Append('\n');
            }
            if (_batch.Length > 0)
            {
                WriteToFile(_batch);
            }
        }
        catch (IOException)
        {
            // A full or vanished disk must not end the writer; the next line tries again.
            _file?.Dispose();
            _file = null;
        }
        catch (UnauthorizedAccessException)
        {
            _file?.Dispose();
            _file = null;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    // Two windows are two processes writing one daily file. A handle that may only append
    // (FILE_APPEND_DATA without FILE_WRITE_DATA) has Windows put every write at the file's
    // current end, and each batch is one write, so neither writes over the other's lines.
    // A plain FileMode.Append handle writes where it thinks the end is: in a test, two
    // writers kept 3,000 of 6,000 lines (edge cases, class D).
    private void WriteToFile(StringBuilder batch)
    {
        var today = DateOnly.FromDateTime(_clock());
        if (_file is null || today != _fileDate)
        {
            _file?.Dispose();
            _file = new FileInfo(FilePathFor(today)).Create(FileMode.Append, FileSystemRights.AppendData | FileSystemRights.Synchronize,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.None, fileSecurity: null);
            _fileDate = today;
            // Today's file exists now, so it counts among the files kept.
            DeleteOldFiles();
        }
        _file.Write(Utf8.GetBytes(batch.ToString()));
    }

    private void DeleteOldFiles()
    {
        try
        {
            var prefix = Process + ".";
            var old = new DirectoryInfo(Directory)
                .EnumerateFiles(Process + ".*.jsonl")
                .Where(f => f.Name.Length == prefix.Length + "yyyy-MM-dd.jsonl".Length)
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(KeptLogFiles);
            foreach (var file in old)
            {
                file.Delete();
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
