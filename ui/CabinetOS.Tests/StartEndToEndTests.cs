using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CabinetOS.Core.Ipc;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// The window's start, as the speed review of 2026-10-01 left it (docs/log/2026-10-01/speed-review.md):
/// the core is started and connected while WinUI builds the window, and the protocol's JSON tables are
/// built beside it, so the core's first answer is not held up. Opens a window on the desktop, so it runs
/// only with <c>CABINETOS_UI_E2E=1</c> and a built window (Debug) and core.
/// </summary>
public class StartEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    [Fact]
    public async Task The_core_is_started_while_the_window_is_built_and_answers_hello_at_once()
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
        var root = Repo.NewTempFolder("start");
        Process? process = null;
        try
        {
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "alpha.txt"), "x");
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["version"] = 1,
                ["ui"] = new Dictionary<string, object> { ["dualPane"] = true, ["lastPaths"] = new[] { data, data } },
            }));
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            start.Environment["CABINETOS_CORE_EXE"] = core;
            start.Environment["CABINETOS_CONFIG"] = Path.Combine(root, "config", "cabinetos.json");
            start.Environment["CABINETOS_LOG_DIR"] = Path.Combine(root, "logs");
            foreach (var (name, folder) in new[] { ("THEMES", "themes"), ("UNDO", "undo"), ("PLUGINS", "plugins"), ("PLUGINS_DATA", "plugins-data"),
                ("MARKETPLACE", "marketplace"), ("WEBVIEW2", "webview2"), ("TOOLS", "tools"), ("UPDATE", "update") })
            {
                start.Environment[$"CABINETOS_{name}_DIR"] = Path.Combine(root, folder);
            }
            start.Environment["CABINETOS_UI_SNAPSHOT"] = Path.Combine(root, "shots");
            start.Environment["CABINETOS_UI_SNAPSHOT_STEPS"] = "wait:300;shot:done";
            process = Process.Start(start)!;
            var shot = Path.Combine(root, "shots", "done.png");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (!File.Exists(shot))
            {
                Assert.True(DateTime.UtcNow < deadline, "the window did not take its snapshot within 60 s");
                await Task.Delay(200);
            }
            process.CloseMainWindow();
            Assert.True(process.WaitForExit(20_000), "the window did not close");

            var logs = LogFiles.Ui(Path.Combine(root, "logs"))
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToList();
            JsonElement First(string message, Func<JsonElement, bool>? also = null) =>
                logs.First(l => l.GetProperty("message").GetString() == message && (also?.Invoke(l) ?? true));
            DateTime At(JsonElement line) => DateTime.Parse(line.GetProperty("ts").GetString()!, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

            var coreStarted = First("core started");
            var connected = First("connected to the core");
            var starting = First("notice shown", l => l.GetProperty("fields").GetProperty("text").GetString() == "Starting the core…");
            // Before the window was built and its start asked for the core: the launch ran beside WinUI's own start.
            Assert.True(At(coreStarted) < At(starting), $"the core started at {At(coreStarted):O}, after the window's start at {At(starting):O}");
            Assert.True(At(connected) <= At(starting), $"the core was connected at {At(connected):O}, after the window's start at {At(starting):O}");
            // The first reply found the protocol's tables built: about 10 ms, where building them on the reader took 140 ms.
            var hello = First("reply received", l => l.GetProperty("fields").GetProperty("request").GetString() == "hello");
            var helloMs = hello.GetProperty("fields").GetProperty("elapsed_us").GetInt64() / 1000.0;
            Assert.True(helloMs < 60, $"hello took {helloMs} ms");
            Assert.Equal(2, logs.Count(l => l.GetProperty("message").GetString() == "listing shown"));
            Assert.DoesNotContain(logs, l => l.GetProperty("level").GetString() == "ERROR");
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
            Repo.RemoveTempFolder(root);
        }
    }
}
