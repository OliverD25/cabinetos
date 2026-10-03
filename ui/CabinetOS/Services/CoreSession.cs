using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Services;

/// <summary>
/// The window's connection to its core: it starts <c>cabinetos-core.exe</c>,
/// says hello, and hands the core's events to the UI thread one by one. Every
/// request of the window goes through here, so a restarted core is picked up
/// by everyone at once.
/// </summary>
public sealed class CoreSession : ICoreChannel
{
    private const string Target = "cabinetos_ui::session";

    // The core started at process start (StartEarly), taken by the first StartAsync.
    private static Task<CoreConnection?>? s_early;

    // The window's tools folder in development (--tools-dir or CABINETOS_TOOLS_DIR), passed to every core it starts.
    private static string? s_devToolsDir;

    private CoreConnection? _connection;
    private bool _stopping;

    /// <summary>Raised on the UI thread for every event of the core, in order.</summary>
    public event Action<CoreEvent>? EventReceived;

    /// <summary>Raised on the UI thread when the core went away without being asked to.</summary>
    public event Action<string>? Lost;

    /// <summary>Whether a core is connected.</summary>
    public bool IsConnected => _connection?.Client.IsConnected == true;

    /// <summary>The core's protocol version, from <c>welcome</c>.</summary>
    public uint ProtocolVersion { get; private set; }

    /// <summary>The running core's process ID, or null.</summary>
    public int? CoreProcessId => _connection?.Process is { HasExited: false } process ? process.Id : null;

    /// <summary>
    /// Finds, starts and connects the core on a background thread at process start, so it is ready
    /// when the window has been built: the window takes about a second to build, the core about
    /// 100 ms to open its pipe (docs/log/2026-10-01/speed-review.md). The first <see cref="StartAsync"/>
    /// takes the connection, or the failure; a core that was not found is looked for again there,
    /// which reports where it looked.
    /// </summary>
    public static void StartEarly(string? devToolsDir)
    {
        s_devToolsDir = devToolsDir;
        s_early = Task.Run(async () =>
        {
            var exe = CoreLauncher.Find(AppContext.BaseDirectory, Environment.GetEnvironmentVariable, File.Exists);
            return exe is null ? null : await CoreLauncher.StartAsync(exe, TimeSpan.FromSeconds(10), devToolsDir: devToolsDir).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Finds and starts the core, connects and says hello. Call it on the UI
    /// thread: the event pump then continues there.
    /// </summary>
    /// <exception cref="CoreLaunchException">No core was found, or it did not start.</exception>
    public async Task StartAsync()
    {
        var early = Interlocked.Exchange(ref s_early, null);
        var connection = (early is null ? null : await early) ?? await LaunchAsync();
        try
        {
            var welcome = await connection.Client.HelloAsync();
            ProtocolVersion = welcome.ProtocolVersion;
            Diag.SetBundleProtocol(welcome.ProtocolVersion);
            Diag.Info(Target, "core ready", new LogField("protocol_version", welcome.ProtocolVersion),
                new LogField("core_version", welcome.CoreVersion), new LogField("pid", connection.Process.Id));
        }
        catch (Exception error) when (error is IOException or CoreRequestException)
        {
            await connection.DisposeAsync();
            throw new CoreLaunchException($"The core started but did not accept this window: {error.Message}{connection.StderrTail}", error);
        }
        _connection = connection;
        _ = PumpAsync(connection);
    }

    private static async Task<CoreConnection> LaunchAsync()
    {
        var directory = AppContext.BaseDirectory;
        // Probing the candidate paths is file I/O: off the UI thread (brief §1).
        var exe = await Task.Run(() => CoreLauncher.Find(directory, Environment.GetEnvironmentVariable, File.Exists));
        if (exe is null)
        {
            var looked = string.Join(Environment.NewLine, await Task.Run(() => CoreLauncher.Candidates(directory, Environment.GetEnvironmentVariable, File.Exists)));
            throw new CoreLaunchException(
                $"CabinetOS could not find {CoreLauncher.CoreExeName}. It looked in:{Environment.NewLine}{looked}{Environment.NewLine}{Environment.NewLine}" +
                $"Build the core (cargo build -p cabinetos-core in core\\), or set {CoreLauncher.CoreExeEnv} to its full path.");
        }

        return await CoreLauncher.StartAsync(exe, TimeSpan.FromSeconds(10), devToolsDir: s_devToolsDir);
    }

    /// <inheritdoc/>
    public Task<CoreReply> RequestAsync(CoreRequest request, CancellationToken cancellationToken = default) =>
        _connection?.Client is { IsConnected: true } client
            ? client.RequestAsync(request, cancellationToken)
            : Task.FromException<CoreReply>(new CoreDisconnectedException("the core is not running"));

    /// <summary>Asks the core to exit and waits up to 2 s (the window is closing).</summary>
    public async Task StopAsync()
    {
        _stopping = true;
        if (_connection is not { } connection)
        {
            return;
        }
        _connection = null;
        await connection.ShutdownAsync(TimeSpan.FromSeconds(2));
        await connection.DisposeAsync();
    }

    private async Task PumpAsync(CoreConnection connection)
    {
        // Each await resumes on the UI thread through its DispatcherQueue, so
        // handlers run there, in the order the core sent the events.
        await foreach (var coreEvent in connection.Client.Events.ReadAllAsync())
        {
            try
            {
                EventReceived?.Invoke(coreEvent);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Diag.Error(Target, "an event handler failed", new LogField("event", coreEvent.GetType().Name), new LogField("error", error.ToString()));
            }
            finally
            {
                // A section nobody took is closed now rather than at the next collection.
                (coreEvent as ICarriesSection)?.TakeSection()?.Dispose();
            }
        }
        if (_stopping || _connection != connection)
        {
            return;
        }
        var reason = connection.Client.EndReason ?? "the pipe closed";
        var exited = connection.Process.HasExited ? $" (exit code {connection.Process.ExitCode})" : "";
        Diag.Warn(Target, "the core went away", new LogField("reason", reason), new LogField("exited", exited));
        _connection = null;
        await connection.DisposeAsync();
        Lost?.Invoke(reason + exited);
    }
}
