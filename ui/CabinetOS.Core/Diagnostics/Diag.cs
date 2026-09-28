namespace CabinetOS.Core.Diagnostics;

/// <summary>
/// The UI's diagnostics, set up once at startup: the JSON Lines log and the
/// crash trace (docs/diagnostics.md). Until <see cref="Init"/> runs, events
/// are discarded.
/// </summary>
public static class Diag
{
    /// <summary>Environment variable that overrides the log directory, as for the core.</summary>
    public const string LogDirEnv = "CABINETOS_LOG_DIR";

    /// <summary>Environment variable with the level filter, as for the core.</summary>
    public const string LogFilterEnv = "CABINETOS_LOG";

    private static LogWriter? _writer;
    private static int _crashed;

    /// <summary>The writer, once initialized.</summary>
    public static LogWriter? Writer => Volatile.Read(ref _writer);

    /// <summary>
    /// The log directory: <c>CABINETOS_LOG_DIR</c>, else
    /// <c>%LOCALAPPDATA%\CabinetOS\logs</c>.
    /// </summary>
    public static string DefaultDirectory(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var fromEnv = environment(LogDirEnv);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CabinetOS",
            "logs");
    }

    /// <summary>Starts the log writer. Call once, first thing at startup.</summary>
    public static LogWriter Init(string version, string? directory = null)
    {
        var writer = new LogWriter(
            directory ?? DefaultDirectory(),
            LogFilter.Parse(Environment.GetEnvironmentVariable(LogFilterEnv)),
            "ui",
            version);
        var previous = Interlocked.Exchange(ref _writer, writer);
        previous?.Dispose();
        return writer;
    }

    /// <summary>Whether an event would be written.</summary>
    public static bool IsEnabled(LogLevel level, string target) => Writer?.IsEnabled(level, target) ?? false;

    /// <summary>Logs one event.</summary>
    public static void Log(
        LogLevel level,
        string target,
        string message,
        string? requestId = null,
        string? span = null,
        IReadOnlyList<LogField>? fields = null) =>
        Writer?.Write(level, target, message, requestId, span, fields);

    /// <summary>Logs a TRACE event.</summary>
    public static void Trace(string target, string message, params LogField[] fields) =>
        Log(LogLevel.Trace, target, message, fields: fields);

    /// <summary>Logs a DEBUG event.</summary>
    public static void Debug(string target, string message, params LogField[] fields) =>
        Log(LogLevel.Debug, target, message, fields: fields);

    /// <summary>Logs an INFO event.</summary>
    public static void Info(string target, string message, params LogField[] fields) =>
        Log(LogLevel.Info, target, message, fields: fields);

    /// <summary>Logs a WARN event.</summary>
    public static void Warn(string target, string message, params LogField[] fields) =>
        Log(LogLevel.Warn, target, message, fields: fields);

    /// <summary>Logs an ERROR event.</summary>
    public static void Error(string target, string message, params LogField[] fields) =>
        Log(LogLevel.Error, target, message, fields: fields);

    /// <summary>
    /// Logs an event that belongs to a request, in the span <c>request</c>, so
    /// its <c>request_id</c> leads to the core's lines for the same request.
    /// </summary>
    public static void Request(LogLevel level, string requestId, string target, string message, params LogField[] fields) =>
        Log(level, target, message, requestId, "request", fields);

    /// <summary>
    /// Writes the crash trace and flushes the log. Called from the unhandled
    /// exception hooks; returns the trace's path, if it was written.
    /// </summary>
    public static string? Crash(Exception? exception, string message)
    {
        var writer = Writer;
        if (writer is null)
        {
            return null;
        }
        writer.Write(LogLevel.Error, "cabinetos_ui::app", message, fields: [new LogField("error", exception?.ToString())]);
        if (Interlocked.Exchange(ref _crashed, 1) == 1)
        {
            // Two hooks can see the same failure; one trace per process.
            writer.Flush(TimeSpan.FromSeconds(2));
            return null;
        }
        return writer.WriteCrashReport(exception, message);
    }

    /// <summary>Flushes and stops the writer at exit.</summary>
    public static void Shutdown()
    {
        var writer = Interlocked.Exchange(ref _writer, null);
        if (writer is null)
        {
            return;
        }
        writer.Flush(TimeSpan.FromSeconds(2));
        writer.Dispose();
    }
}
