using System.Diagnostics;
using System.Security.Cryptography;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Ipc;

/// <summary>The core could not be found or started; the message is for the user.</summary>
public sealed class CoreLaunchException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Finds <c>cabinetos-core.exe</c>, starts it with a random pipe token and
/// this process's ID, and connects (docs/ipc.md, "The pipe"). The core exits
/// by itself when this process ends.
/// </summary>
public sealed class CoreLauncher
{
    /// <summary>Environment variable with the full path of the core to start.</summary>
    public const string CoreExeEnv = "CABINETOS_CORE_EXE";

    /// <summary>The core's file name.</summary>
    public const string CoreExeName = "cabinetos-core.exe";

    private const string Target = "cabinetos_ui::launcher";

    /// <summary>
    /// The places the core is looked for, in order: <c>CABINETOS_CORE_EXE</c>;
    /// next to the UI; then, when running from the repository, the core's
    /// debug and release builds.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string uiDirectory, Func<string, string?> environment, Func<string, bool> fileExists)
    {
        var candidates = new List<string>();
        var fromEnv = environment(CoreExeEnv);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            candidates.Add(fromEnv.Trim().Trim('"'));
        }
        candidates.Add(Path.Combine(uiDirectory, CoreExeName));
        for (var dir = new DirectoryInfo(uiDirectory); dir is not null; dir = dir.Parent)
        {
            if (fileExists(Path.Combine(dir.FullName, "core", "Cargo.toml")))
            {
                candidates.Add(Path.Combine(dir.FullName, "core", "target", "debug", CoreExeName));
                candidates.Add(Path.Combine(dir.FullName, "core", "target", "release", CoreExeName));
                break;
            }
        }
        return candidates;
    }

    /// <summary>The first candidate that exists, or null.</summary>
    public static string? Find(string uiDirectory, Func<string, string?> environment, Func<string, bool> fileExists) =>
        Candidates(uiDirectory, environment, fileExists).FirstOrDefault(fileExists);

    /// <summary>A random pipe token: 16 hexadecimal digits.</summary>
    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    /// <summary>
    /// Starts the core and connects to it. Waits at most
    /// <paramref name="pipeDeadline"/> for its pipe, and fails early when the
    /// core exits first. <paramref name="environment"/> adds variables for the
    /// core only, such as <c>CABINETOS_CONFIG</c> in a test.
    /// </summary>
    public static async Task<CoreConnection> StartAsync(
        string coreExe,
        TimeSpan pipeDeadline,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var token = NewToken();
        var start = new ProcessStartInfo(coreExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(coreExe) ?? Environment.CurrentDirectory,
        };
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(token);
        start.ArgumentList.Add("--parent-pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var stderr = new StderrTail();
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new CoreLaunchException($"Windows did not start {coreExe}.");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
        {
            throw new CoreLaunchException($"Cannot start {coreExe}: {error.Message}", error);
        }
        process.ErrorDataReceived += (_, e) => stderr.Add(e.Data);
        process.BeginErrorReadLine();
        Diag.Info(Target, "core started", new LogField("exe", coreExe), new LogField("pid", process.Id), new LogField("pipe_token", token));

        var watch = Stopwatch.StartNew();
        var pipeName = CoreClient.PipeNameForToken(token);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new CoreLaunchException(
                    $"The core exited with code {process.ExitCode} before it opened its pipe.{stderr.Describe()}");
            }
            try
            {
                var client = await CoreClient.ConnectAsync(pipeName, TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                Diag.Info(Target, "connected to the core", new LogField("pipe", pipeName), new LogField("waited_ms", watch.ElapsedMilliseconds));
                return new CoreConnection(process, client, token, stderr);
            }
            catch (TimeoutException) when (watch.Elapsed < pipeDeadline)
            {
            }
            catch (TimeoutException)
            {
                TryKill(process);
                throw new CoreLaunchException(
                    $"The core did not open its pipe within {pipeDeadline.TotalSeconds:0} s.{stderr.Describe()}");
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}

/// <summary>A running core and the connection to it.</summary>
public sealed class CoreConnection : IAsyncDisposable
{
    private const string Target = "cabinetos_ui::launcher";
    private readonly StderrTail _stderr;

    internal CoreConnection(Process process, CoreClient client, string token, StderrTail stderr)
    {
        Process = process;
        Client = client;
        Token = token;
        _stderr = stderr;
    }

    /// <summary>The core's process.</summary>
    public Process Process { get; }

    /// <summary>The connection to it.</summary>
    public CoreClient Client { get; }

    /// <summary>The random pipe token it was started with.</summary>
    public string Token { get; }

    /// <summary>The last lines the core printed to stderr.</summary>
    public string StderrTail => _stderr.Describe();

    /// <summary>
    /// Sends <c>shutdown</c> and waits up to <paramref name="wait"/> for the
    /// core to exit. A core that stays is left to its parent-process watch,
    /// which ends it when this process ends.
    /// </summary>
    public async Task ShutdownAsync(TimeSpan wait)
    {
        var watch = Stopwatch.StartNew();
        await Client.ShutdownCoreAsync(wait).ConfigureAwait(false);
        var remaining = wait - watch.Elapsed;
        var exited = Process.HasExited;
        if (!exited && remaining > TimeSpan.Zero)
        {
            using var timeout = new CancellationTokenSource(remaining);
            try
            {
                await Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                exited = true;
            }
            catch (OperationCanceledException)
            {
            }
        }
        if (exited)
        {
            Diag.Info(Target, "core exited", new LogField("exit_code", Process.ExitCode), new LogField("waited_ms", watch.ElapsedMilliseconds));
        }
        else
        {
            Diag.Warn(Target, "core still running after shutdown; its parent-process watch will end it", new LogField("waited_ms", watch.ElapsedMilliseconds));
        }
    }

    /// <summary>Closes the connection (not the core).</summary>
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        Process.Dispose();
    }
}

/// <summary>Keeps the last lines the core wrote to stderr, for error messages and the log.</summary>
internal sealed class StderrTail
{
    private const int Kept = 20;
    private readonly Queue<string> _lines = new();

    public void Add(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        Diag.Warn("cabinetos_ui::launcher", "core stderr", new LogField("line", line));
        lock (_lines)
        {
            _lines.Enqueue(line);
            while (_lines.Count > Kept)
            {
                _lines.Dequeue();
            }
        }
    }

    public string Describe()
    {
        lock (_lines)
        {
            return _lines.Count == 0 ? "" : Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, _lines);
        }
    }
}
