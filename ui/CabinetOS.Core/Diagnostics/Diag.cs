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

    /// <summary>
    /// Environment variable that turns heavy mode on (<c>1</c>) or off (<c>0</c>) for this process,
    /// whatever <c>logging.heavy</c> says; the core reads the same variable.
    /// </summary>
    public const string LogHeavyEnv = "CABINETOS_LOG_HEAVY";

    private static readonly AsyncLocal<string?> TraceSlot = new();
    private static LogWriter? _writer;
    private static int _crashed;
    private static bool _heavyFromEnvironment;
    private static System.Text.Json.Nodes.JsonNode? _bundleConfig;
    private static uint _bundleProtocol;

    /// <summary>Whether heavy mode is on: every event is written, and the heavy-only lines too.</summary>
    public static bool HeavyEnabled => Writer?.HeavyEnabled ?? false;

    /// <summary>Whether <c>CABINETOS_LOG_HEAVY</c> decided heavy mode: the configuration cannot change it.</summary>
    public static bool HeavyFromEnvironment => Volatile.Read(ref _heavyFromEnvironment);

    /// <summary>Heavy lines the window dropped since heavy mode came on, for the status bar's pill.</summary>
    public static long HeavyLostLines => Writer?.HeavyLostLines ?? 0;

    /// <summary>
    /// The trace of the user action the calling code works for (docs/diagnostics.md,
    /// "Trace ids"): set by <see cref="BeginTrace"/> while a command runs, and carried
    /// across its awaits. Every log line and every request sent meanwhile carries it.
    /// </summary>
    public static string? CurrentTrace => TraceSlot.Value;

    /// <summary>
    /// Makes <paramref name="traceId"/> the current trace until the returned scope is
    /// disposed; the previous one comes back then.
    /// </summary>
    public static IDisposable BeginTrace(string traceId)
    {
        var previous = TraceSlot.Value;
        TraceSlot.Value = traceId;
        return new TraceScope(previous);
    }

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
        var heavy = ParseHeavy(Environment.GetEnvironmentVariable(LogHeavyEnv), out var badHeavy);
        Volatile.Write(ref _heavyFromEnvironment, heavy is not null);
        if (heavy is { } on)
        {
            writer.SetHeavy(on);
        }
        if (badHeavy is not null)
        {
            writer.Write(LogLevel.Warn, "cabinetos_ui::diag", $"ignoring {LogHeavyEnv}: expected 1 or 0; the configuration decides",
                fields: [new LogField("value", badHeavy)]);
        }
        return writer;
    }

    /// <summary>
    /// <c>CABINETOS_LOG_HEAVY</c>: <c>1</c> or <c>true</c> on, <c>0</c> or <c>false</c> off, unset or
    /// empty for the configuration to decide (null). Anything else is null too, and
    /// <paramref name="invalid"/> holds it, to be warned about.
    /// </summary>
    public static bool? ParseHeavy(string? text, out string? invalid)
    {
        invalid = null;
        var trimmed = text?.Trim();
        switch (trimmed)
        {
            case null or "":
                return null;
            case "1" or "true":
                return true;
            case "0" or "false":
                return false;
            default:
                invalid = trimmed;
                return null;
        }
    }

    /// <summary>
    /// Logs a line only heavy mode writes, with the target <c>heavy::&lt;area&gt;</c> (the normal file
    /// leaves such targets out at every level). Call it under <c>if (Diag.HeavyEnabled)</c>, so
    /// nothing is built while heavy mode is off.
    /// </summary>
    public static void Heavy(string area, string message, params LogField[] fields) =>
        Log(LogLevel.Debug, HeavyTarget(area), message, fields: fields);

    /// <summary>
    /// Logs the JSON of a request or a reply as a heavy line: secrets masked, at most 64 KB, the
    /// rest cut and marked <c>truncated</c> (<see cref="LogMask"/>). The line belongs to the request
    /// <paramref name="requestId"/> and the action <paramref name="traceId"/> (null: the current one).
    /// </summary>
    public static void HeavyPayload(string message, string requestId, string? traceId, ReadOnlySpan<byte> json)
    {
        if (HeavyEnabled)
        {
            Log(LogLevel.Debug, HeavyTarget("pipe"), message, requestId, "request", PayloadFields(json), traceId);
        }
    }

    /// <summary>The fields of a payload line: <c>payload</c>, masked and cut at 64 KB, and <c>truncated</c> when it was cut.</summary>
    public static LogField[] PayloadFields(ReadOnlySpan<byte> json)
    {
        var (payload, truncated) = LogMask.MaskedJson(json);
        return truncated ? [new("payload", payload), new("truncated", true)] : [new("payload", payload)];
    }

    /// <summary>
    /// Logs a message between the window and a web page (the terminal, a tool) as a heavy line: the
    /// message's <c>type</c> and its size, never its content, which can hold what the user typed
    /// or what a file says. <paramref name="direction"/> is <c>to_page</c> or <c>from_page</c>.
    /// </summary>
    public static void HeavyPageMessage(string host, string direction, string json)
    {
        if (HeavyEnabled)
        {
            Heavy("pages", "page message", new LogField("host", host), new LogField("direction", direction),
                new LogField("name", PageMessageName(json)), new LogField("bytes", System.Text.Encoding.UTF8.GetByteCount(json)));
        }
    }

    /// <summary>The <c>type</c> of a page's message, or <c>?</c> when it has none.</summary>
    public static string PageMessageName(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root
                && root.TryGetProperty("type", out var type) && type.ValueKind == System.Text.Json.JsonValueKind.String
                ? type.GetString() ?? "?"
                : "?";
        }
        catch (System.Text.Json.JsonException)
        {
            return "?";
        }
    }

    private static string HeavyTarget(string area) => $"{HeavyFiles.TargetPrefix}{area}";

    /// <summary>Whether an event would be written.</summary>
    public static bool IsEnabled(LogLevel level, string target) => Writer?.IsEnabled(level, target) ?? false;

    /// <summary>Logs one event, with <paramref name="traceId"/> or else the current trace.</summary>
    public static void Log(
        LogLevel level,
        string target,
        string message,
        string? requestId = null,
        string? span = null,
        IReadOnlyList<LogField>? fields = null,
        string? traceId = null) =>
        Writer?.Write(level, target, message, requestId, span, fields, traceId ?? CurrentTrace);

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
    /// Like <see cref="Request"/>, for a thread outside the action's scope (the pipe's
    /// reader): the line carries <paramref name="traceId"/>.
    /// </summary>
    public static void Traced(LogLevel level, string? traceId, string requestId, string target, string message, params LogField[] fields) =>
        Log(level, target, message, requestId, "request", fields, traceId);

    /// <summary>
    /// Writes the crash trace and flushes the log; while heavy mode is on it then writes the crash bundle
    /// as well (<see cref="LogWriter.WriteCrashBundle"/>). Called from the unhandled exception hooks;
    /// returns the trace's path, if it was written. The bundle comes second: if it fails, the trace is there.
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
        var trace = writer.WriteCrashReport(exception, message);
        if (writer.HeavyEnabled)
        {
            writer.WriteCrashBundle(trace, BundleFactsNow(writer));
        }
        return trace;
    }

    /// <summary>
    /// Tells a crash bundle the configuration the core last sent (<c>config</c> reply): its secrets are
    /// masked here, so a bundle never holds them.
    /// </summary>
    public static void SetBundleConfig(System.Text.Json.JsonElement config)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(config.GetRawText());
            LogMask.MaskSecrets(node);
            Volatile.Write(ref _bundleConfig, node);
        }
        catch (System.Text.Json.JsonException)
        {
            // A configuration that does not parse is left out of the bundle.
        }
    }

    /// <summary>Lets <see cref="Crash"/> write a trace again: the tests crash the process's log more than once.</summary>
    internal static void ForgetCrashForTests() => Volatile.Write(ref _crashed, 0);

    /// <summary>Tells a crash bundle which protocol version the core spoke.</summary>
    public static void SetBundleProtocol(uint protocol) => Volatile.Write(ref _bundleProtocol, protocol);

    private static BundleFacts BundleFactsNow(LogWriter writer)
    {
        var protocol = Volatile.Read(ref _bundleProtocol);
        return new BundleFacts(writer.Process, writer.Version, protocol == 0 ? null : protocol, WindowsBuild(), Volatile.Read(ref _bundleConfig));
    }

    // 10.0.26200.6899 (25H2), as the core writes it; the version the runtime reports when the registry says nothing.
    private static string WindowsBuild()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key?.GetValue("CurrentBuildNumber") is string build)
            {
                var revision = key.GetValue("UBR");
                var name = key.GetValue("DisplayVersion") as string;
                return $"10.0.{build}" + (revision is null ? "" : $".{revision}") + (string.IsNullOrEmpty(name) ? "" : $" ({name})");
            }
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return Environment.OSVersion.Version.ToString();
    }

    private sealed class TraceScope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                TraceSlot.Value = previous;
            }
        }
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
