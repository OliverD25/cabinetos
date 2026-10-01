using System.Diagnostics;
using System.Text.Json;
using CabinetOS.Core.Ipc;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// The theme picker's live preview on a real window and core (docs/ui.md, "Themes"). Opens a window
/// twice on one configuration, so it runs only with <c>CABINETOS_UI_E2E=1</c> and a built window
/// (Debug) and core. The snapshot steps drive the picker (<c>pick:</c> moves its highlight, as the
/// pointer or a key does) and the window's log lines of target <c>cabinetos_ui::theme</c> say what it painted.
/// </summary>
public class ThemePickerEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    // The core writes its shipped themes into an empty themes folder; the picker lists them in its order:
    // catppuccin-mocha, commander-compact, default, nord, rose-pine-moon. Row 3 is Nord.
    private const int NordRow = 3;

    /// <summary>
    /// A theme highlighted and left with Esc is previewed and then painted back, and nothing is
    /// written; the same theme highlighted and applied is chosen and applied with no restore between.
    /// </summary>
    [Fact]
    public async Task A_highlighted_theme_is_previewed_Esc_restores_the_theme_in_effect_and_apply_keeps_it()
    {
        if (Environment.GetEnvironmentVariable(OptIn) != "1")
        {
            Assert.Skip($"Opens a window on the desktop twice: set {OptIn}=1 to run it.");
        }
        var exe = Path.Combine(Repo.Root, "ui", "CabinetOS", "bin", "x64", "Debug", "net10.0-windows10.0.22621.0", "win-x64", "CabinetOS.exe");
        var coreExe = CoreLauncher.Find(Path.Combine(Repo.Root, "ui"), Environment.GetEnvironmentVariable, File.Exists);
        if (!File.Exists(exe) || coreExe is null)
        {
            Assert.Skip("Build ui/CabinetOS.sln (Debug) and the core first.");
        }

        var root = Repo.NewTempFolder("theme-preview");
        var started = new List<Process>();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            var configPath = Path.Combine(root, "config", "cabinetos.json");
            File.WriteAllText(configPath, """{ "version": 1, "ui": { "dualPane": true } }""");

            async Task<List<string>> RunAsync(string run, string steps)
            {
                var start = new ProcessStartInfo(exe) { UseShellExecute = false };
                start.Environment["CABINETOS_CORE_EXE"] = coreExe;
                start.Environment["CABINETOS_CONFIG"] = configPath;
                start.Environment["CABINETOS_LOG_DIR"] = Path.Combine(root, "logs-" + run);
                start.Environment["CABINETOS_THEMES_DIR"] = Path.Combine(root, "themes");
                start.Environment["CABINETOS_UNDO_DIR"] = Path.Combine(root, "undo");
                start.Environment["CABINETOS_PLUGINS_DIR"] = Path.Combine(root, "plugins");
                start.Environment["CABINETOS_PLUGINS_DATA_DIR"] = Path.Combine(root, "plugins-data");
                start.Environment["CABINETOS_MARKETPLACE_DIR"] = Path.Combine(root, "marketplace");
                start.Environment["CABINETOS_WEBVIEW2_DIR"] = Path.Combine(root, "webview2");
                start.Environment["CABINETOS_TOOLS_DIR"] = Path.Combine(root, "tools");
                start.Environment["CABINETOS_UPDATE_DIR"] = Path.Combine(root, "update");
                start.Environment["CABINETOS_UI_SNAPSHOT"] = Path.Combine(root, "shots-" + run);
                start.Environment["CABINETOS_UI_SNAPSHOT_STEPS"] = steps;
                var process = Process.Start(start)!;
                started.Add(process);
                await WaitForAsync(() => File.Exists(Path.Combine(root, "shots-" + run, "done.png")), $"the {run} window's last snapshot", TimeSpan.FromSeconds(120));
                process.CloseMainWindow();
                Assert.True(process.WaitForExit(20_000), $"the {run} window did not close");
                var folder = Path.Combine(root, "logs-" + run);
                Assert.Empty(Directory.GetFiles(folder, "crash-*.json"));
                var logs = LogFiles.Ui(folder);
                Assert.DoesNotContain(logs, l => Level(l) == "ERROR");
                var themes = logs.Where(l => Target(l) == "cabinetos_ui::theme").ToList();
                foreach (var line in themes)
                {
                    TestContext.Current.TestOutputHelper?.WriteLine($"{run}: {Message(line)} {Theme(line)}");
                }
                return themes;
            }

            // Esc: the preview comes and goes, and the theme in effect stays what it was.
            var escaped = await RunAsync("escape", string.Join(';',
                "cmd:preferences.selectColorTheme",
                "wait:500",
                $"pick:{NordRow}",
                "wait:1000",
                "shot:previewed",
                "cmd:overlay.close",
                "wait:1000",
                "shot:done"));
            var previewed = Index(escaped, "theme previewed", "nord");
            Assert.True(previewed >= 0, "no \"theme previewed\" for nord");
            Assert.True(Index(escaped, "theme restored", "default", from: previewed) > previewed, "no \"theme restored\" for default after the preview");
            Assert.DoesNotContain(escaped, l => Message(l) == "theme chosen");
            using (var config = JsonDocument.Parse(File.ReadAllText(configPath)))
            {
                var named = config.RootElement.GetProperty("ui").TryGetProperty("theme", out var theme) ? theme.GetString() : "default";
                Assert.Equal("default", named);
            }

            // Enter: the preview becomes the theme in effect through theme_changed, with no restore in between.
            var applied = await RunAsync("apply", string.Join(';',
                "cmd:preferences.selectColorTheme",
                "wait:500",
                $"pick:{NordRow}",
                "wait:1000",
                "cmd:theme.apply",
                "wait:1500",
                "shot:done"));
            var shown = Index(applied, "theme previewed", "nord");
            Assert.True(shown >= 0, "no \"theme previewed\" for nord");
            var chosen = Index(applied, "theme chosen", "nord", from: shown);
            Assert.True(chosen > shown, "no \"theme chosen\" for nord after its preview");
            // The core's theme_changed may come before its reply to set_value, which "theme chosen" waits for.
            Assert.True(Index(applied, "theme applied", "nord", from: shown) > shown, "no \"theme applied\" for nord after its preview");
            Assert.DoesNotContain(applied, l => Message(l) == "theme restored");
            using (var config = JsonDocument.Parse(File.ReadAllText(configPath)))
            {
                Assert.Equal("nord", config.RootElement.GetProperty("ui").GetProperty("theme").GetString());
            }
        }
        finally
        {
            foreach (var process in started.Where(p => !p.HasExited))
            {
                process.Kill(entireProcessTree: true);
            }
            Repo.RemoveTempFolder(root);
        }
    }

    private static int Index(List<string> lines, string message, string theme, int from = 0) =>
        lines.FindIndex(from, l => Message(l) == message && Theme(l) == theme);

    private static string? Message(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.GetProperty("message").GetString();
    }

    private static string? Level(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.GetProperty("level").GetString();
    }

    private static string? Target(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.TryGetProperty("target", out var target) ? target.GetString() : null;
    }

    private static string? Theme(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.TryGetProperty("fields", out var fields) && fields.TryGetProperty("theme", out var theme) ? theme.GetString() : null;
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
