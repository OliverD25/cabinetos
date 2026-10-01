using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// The window's note of a run that ended without closing (docs/diagnostics.md, "A run that ended without
/// closing"): a window that closes writes its end into <c>ui.last-start</c>, and the next start says
/// nothing; a window that is killed leaves no end, and the next start logs one WARN line. Opens three
/// windows on the desktop in turn, so it runs only with <c>CABINETOS_UI_E2E=1</c> and a built window
/// (Debug) and core.
/// </summary>
public class CrashNoticeEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";
    private const string Said = "previous run ended without closing";

    [Fact]
    public async Task A_window_that_closes_leaves_its_end_and_one_that_is_killed_is_noted_at_the_next_start()
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
        var root = Repo.NewTempFolder("unclean");
        try
        {
            var logs = Path.Combine(root, "logs");

            // A first start, closed as the close button does: it writes its end, and the log has no note.
            await RunAsync(root, exe, core, run: 1, kill: false);
            var closed = CrashNotice.ReadRun(logs);
            Assert.NotNull(closed);
            Assert.NotNull(closed.ProcessId);
            Assert.NotNull(closed.ClosedUtc);
            Assert.Empty(Notes(logs));

            // A second start finds that end, says nothing, and is killed: its run has no end.
            await RunAsync(root, exe, core, run: 2, kill: true);
            var killed = CrashNotice.ReadRun(logs);
            Assert.NotNull(killed);
            Assert.NotEqual(closed.StartedUtc, killed.StartedUtc);
            Assert.Null(killed.ClosedUtc);
            Assert.Empty(Notes(logs));

            // The third start says so once, with the time the killed run started.
            await RunAsync(root, exe, core, run: 3, kill: false);
            var note = Assert.Single(Notes(logs));
            Assert.Equal("WARN", note.GetProperty("level").GetString());
            var started = DateTime.Parse(note.GetProperty("fields").GetProperty("started_utc").GetString()!, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            Assert.Equal(killed.StartedUtc, started);
            // And that run closed, so the fourth would say nothing.
            Assert.NotNull(CrashNotice.ReadRun(logs)!.ClosedUtc);
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }
    }

    private static List<JsonElement> Notes(string logs) =>
        LogFiles.Ui(logs).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(line => line.GetProperty("message").GetString() == Said).ToList();

    // One window on the scratch folders: it runs until its first folders are shown and the snapshot is taken, then it
    // is closed (CloseMainWindow, which saves and exits as the close button does) or killed. All runs share the log folder.
    private static async Task RunAsync(string root, string exe, string core, int run, bool kill)
    {
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
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
        var shots = Path.Combine(root, $"shots{run}");
        start.Environment["CABINETOS_UI_SNAPSHOT"] = shots;
        start.Environment["CABINETOS_UI_SNAPSHOT_STEPS"] = "wait:300;shot:done";
        using var process = Process.Start(start)!;
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (!File.Exists(Path.Combine(shots, "done.png")))
            {
                Assert.True(DateTime.UtcNow < deadline, $"window {run} did not take its snapshot within 60 s");
                await Task.Delay(200);
            }
            if (kill)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(20_000);
            }
            else
            {
                process.CloseMainWindow();
                Assert.True(process.WaitForExit(20_000), $"window {run} did not close");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
