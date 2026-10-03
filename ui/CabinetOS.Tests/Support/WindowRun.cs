using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CabinetOS.Core.Ipc;

namespace CabinetOS.Tests.Support;

/// <summary>
/// A real window on a scratch configuration, for the window tests that run only with <c>CABINETOS_UI_E2E=1</c> and a built window
/// (Debug) and core: a folder of its own with the configuration, every data folder the window and the core use, and the
/// snapshot steps as the test's script. A test reads the window's log lines for what it did.
/// </summary>
internal sealed class WindowRun
{
    private const string OptIn = "CABINETOS_UI_E2E";

    private readonly string _exe;
    private readonly string _core;
    private readonly List<Process> _started = [];

    private WindowRun(string root, string exe, string core)
    {
        Root = root;
        _exe = exe;
        _core = core;
    }

    /// <summary>The run's folder: <c>config</c>, <c>themes</c>, <c>plugins</c>, the logs of each window and so on.</summary>
    public string Root { get; }

    /// <summary>The run's <c>cabinetos.json</c>.</summary>
    public string ConfigPath => Path.Combine(Root, "config", "cabinetos.json");

    /// <summary>The themes folder the core uses.</summary>
    public string ThemesFolder => Path.Combine(Root, "themes");

    /// <summary>
    /// A run for <paramref name="purpose"/> whose configuration is <paramref name="config"/> (dual pane when it is null), or the test is
    /// skipped when windows are not opted into or the window or the core is not built.
    /// </summary>
    public static WindowRun Prepare(string purpose, Func<string, JsonObject>? config = null)
    {
        if (Environment.GetEnvironmentVariable(OptIn) != "1")
        {
            Assert.Skip($"Opens a window on the desktop: set {OptIn}=1 to run it.");
        }
        var exe = Path.Combine(Repo.Root, "ui", "CabinetOS", "bin", "x64", "Debug", "net10.0-windows10.0.22621.0", "win-x64", "CabinetOS.exe");
        var core = CoreLauncher.Find(Path.Combine(Repo.Root, "ui"), Environment.GetEnvironmentVariable, File.Exists);
        if (!File.Exists(exe) || core is null)
        {
            Assert.Skip("Build ui/CabinetOS.sln (Debug) and the core first.");
        }
        var root = Repo.NewTempFolder(purpose);
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var run = new WindowRun(root, exe, core);
        run.WriteConfig(config?.Invoke(root) ?? new JsonObject { ["version"] = 1, ["ui"] = new JsonObject { ["dualPane"] = true } });
        return run;
    }

    /// <summary>Writes the configuration file, as an editor would.</summary>
    public void WriteConfig(JsonObject config) => File.WriteAllText(ConfigPath, config.ToJsonString());

    /// <summary>The configuration file as a tree.</summary>
    public JsonObject ReadConfig() => JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();

    /// <summary>
    /// Starts a window on the run's configuration; <paramref name="steps"/> are the snapshot steps, the test's script.
    /// <paramref name="environment"/> adds variables of the test's own.
    /// </summary>
    public Process Start(string name, string steps, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(_exe) { UseShellExecute = false };
        start.Environment["CABINETOS_CORE_EXE"] = _core;
        start.Environment["CABINETOS_CONFIG"] = ConfigPath;
        start.Environment["CABINETOS_LOG_DIR"] = LogFolder(name);
        start.Environment["CABINETOS_THEMES_DIR"] = ThemesFolder;
        start.Environment["CABINETOS_UNDO_DIR"] = Path.Combine(Root, "undo");
        start.Environment["CABINETOS_PLUGINS_DIR"] = Path.Combine(Root, "plugins");
        start.Environment["CABINETOS_PLUGINS_DATA_DIR"] = Path.Combine(Root, "plugins-data");
        start.Environment["CABINETOS_MARKETPLACE_DIR"] = Path.Combine(Root, "marketplace");
        start.Environment["CABINETOS_WEBVIEW2_DIR"] = Path.Combine(Root, "webview2");
        start.Environment["CABINETOS_TOOLS_DIR"] = Path.Combine(Root, "tools");
        start.Environment["CABINETOS_UPDATE_DIR"] = Path.Combine(Root, "update");
        start.Environment["CABINETOS_UI_SNAPSHOT"] = Path.Combine(Root, "shots-" + name);
        start.Environment["CABINETOS_UI_SNAPSHOT_STEPS"] = steps;
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }
        var process = Process.Start(start)!;
        _started.Add(process);
        return process;
    }

    /// <summary>The log folder of the window started under <paramref name="name"/>.</summary>
    public string LogFolder(string name) => Path.Combine(Root, "logs-" + name);

    /// <summary>The window's log lines so far.</summary>
    public List<string> Logs(string name) => LogFiles.Ui(LogFolder(name));

    /// <summary>Waits until the window logged a line that <paramref name="match"/> accepts; a failure shows the window's last lines.</summary>
    public async Task WaitForLogAsync(string name, Func<string, bool> match, string what)
    {
        try
        {
            await WaitForAsync(() => Logs(name).Any(match), $"the {name} window's {what}", TimeSpan.FromSeconds(180));
        }
        catch (Xunit.Sdk.XunitException error)
        {
            throw new Xunit.Sdk.XunitException($"{error.Message}\nthe window's last log lines (times in UTC):\n{WindowLog.Last(Logs(name), 60)}");
        }
    }

    /// <summary>Waits until the window logged the state <paramref name="message"/> with the label <paramref name="label"/>.</summary>
    public Task WaitForStateAsync(string name, string message, string label) =>
        WaitForLogAsync(name, l => Message(l) == message && Text(l, "label") == label, $"\"{message}\" {label}");

    /// <summary>Waits for the last snapshot, closes the window the way a user does and returns the window's log lines.</summary>
    public async Task<List<string>> FinishAsync(string name, Process process, string lastShot = "done")
    {
        var shot = Path.Combine(Root, "shots-" + name, lastShot + ".png");
        try
        {
            await WaitForAsync(() => File.Exists(shot), $"the {name} window's last snapshot", TimeSpan.FromSeconds(240));
        }
        catch (Xunit.Sdk.XunitException error)
        {
            throw new Xunit.Sdk.XunitException($"{error.Message}\nthe window's last log lines (times in UTC):\n{WindowLog.Last(Logs(name), 60)}");
        }
        process.CloseMainWindow();
        Assert.True(process.WaitForExit(60_000), $"the {name} window did not close");
        var logs = Logs(name);
        Assert.Empty(Directory.GetFiles(LogFolder(name), "crash-*.json"));
        var errors = logs.Where(l => Level(l) == "ERROR").ToList();
        Assert.True(errors.Count == 0, $"the {name} window logged {errors.Count} error line(s):\n{string.Join('\n', errors.Select(l => l.Length > 500 ? l[..500] : l))}");
        return logs;
    }

    /// <summary>Ends every window the run started and removes its folder.</summary>
    public void Stop()
    {
        foreach (var process in _started.Where(p => !p.HasExited))
        {
            process.Kill(entireProcessTree: true);
        }
        Repo.RemoveTempFolder(Root);
    }

    /// <summary>A line's <c>message</c>.</summary>
    public static string? Message(string line) => Top(line, "message");

    /// <summary>A line's <c>level</c>.</summary>
    public static string? Level(string line) => Top(line, "level");

    /// <summary>A line's <c>target</c>.</summary>
    public static string? Target(string line) => Top(line, "target");

    /// <summary>A text field of a line, or an empty text; any other kind of field as its JSON.</summary>
    public static string Text(string line, string name)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.TryGetProperty("fields", out var fields) && fields.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()
            : "";
    }

    /// <summary>A field of a line.</summary>
    public static JsonElement Field(string line, string name)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.GetProperty("fields").GetProperty(name).Clone();
    }

    /// <summary>The one line with this message and this label; a failure names them and shows the lines around.</summary>
    public static string State(IReadOnlyList<string> logs, string message, string label)
    {
        var found = logs.Where(l => Message(l) == message && Text(l, "label") == label).ToList();
        Assert.True(found.Count == 1, $"the window logged \"{message}\" {label} {found.Count} times, once was expected\n{WindowLog.Last(logs, 40)}");
        return found[0];
    }

    private static string? Top(string line, string name)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private static async Task WaitForAsync(Func<bool> condition, string what, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"waited {timeout.TotalSeconds:N0} s for {what}");
            await Task.Delay(200);
        }
    }
}
