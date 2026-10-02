using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Platform;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Phase 18: the right-click menu from cabinetos.json on a real window and core (docs/ui.md, "The
/// context menu"). Each test opens a window on the desktop, so they run only with
/// <c>CABINETOS_UI_E2E=1</c> and a built window (Debug) and core. The window's snapshot steps drive
/// it (<c>menu:</c>, <c>menu-click:</c>, <c>shellmenu:</c>) and its log line "shell state" says what
/// the open menus show.
/// </summary>
/// <summary>
/// Tests that measure the window's frames run alone, after the tests that run in parallel: a frame
/// goal says nothing about a window that shares the machine with other test windows.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FrameTests
{
    public const string Name = "frame measurements";
}

public class ContextMenuEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    /// <summary>
    /// A fresh config shows today's menus; a program added to the file while the window runs shows
    /// at the next right-click for a matching file only, runs with the focused path from the menu
    /// and from the palette; a program not in the list is refused; with <c>shellMenu</c> on,
    /// Shift+right-click shows Windows' menu of the file.
    /// </summary>
    [Fact]
    public async Task The_menu_follows_the_file_runs_a_program_and_shows_windows_own_menu()
    {
        var (run, root, data) = Prepare("menu-config");
        try
        {
            var stub = Path.Combine(root, "record paths.js");
            var recorded = Path.Combine(root, "record paths.js.log");
            // A stand-in program with no window, as the live check's editor: each start adds its argument as a line.
            File.WriteAllText(stub, """
                var fso = new ActiveXObject("Scripting.FileSystemObject");
                var log = fso.OpenTextFile(WScript.ScriptFullName + ".log", 8, true, -1);
                log.WriteLine(WScript.Arguments.length > 0 ? WScript.Arguments(0) : "(nothing)");
                log.Close();
                """);
            var process = run.Start("config", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "menu:alpha.txt",
                "shell:fresh",
                "cmd:overlay.close",
                "menu:*",
                "shell:background",
                "cmd:overlay.close",
                "shellmenu:alpha.txt",
                "shell:shell-off",
                "cmd:overlay.close",
                // The test writes the file now, as an editor would; the window reads it again.
                "until:config",
                "wait:500",
                "menu:Alphabet.md",
                "shell:md",
                "menu-click:Record Paths",
                "wait:2000",
                "menu:alpha.txt",
                "shell:txt",
                "cmd:overlay.close",
                "cmd:program.record",
                "wait:2000",
                "cmd:program.nosuch",
                "shellmenu:alpha.txt",
                "shell:windows",
                "cmd:overlay.close",
                "shot:done"));
            await run.WaitForStateAsync("config", "background");
            File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["version"] = 1,
                ["ui"] = new Dictionary<string, object> { ["dualPane"] = true },
                ["contextMenu"] = new Dictionary<string, object>
                {
                    ["shellMenu"] = true,
                    ["file"] = new Dictionary<string, object>
                    {
                        ["items"] = new object[]
                        {
                            new Dictionary<string, object> { ["command"] = "pane.openSelected" },
                            new Dictionary<string, object> { ["separator"] = true },
                            new Dictionary<string, object> { ["command"] = "program.record", ["extensions"] = new[] { ".md" } },
                            new Dictionary<string, object> { ["command"] = "hex.view" },
                        },
                    },
                },
                ["programs"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["name"] = "record",
                        ["title"] = "Record Paths",
                        ["command"] = "wscript.exe",
                        ["args"] = new[] { "//B", "//Nologo", stub, "{path}" },
                    },
                },
            }));
            var logs = await run.FinishAsync("config", process, "done");

            State(logs, "fresh", state =>
            {
                Assert.Equal("Cut|Copy|Paste|Rename|Delete", state.GetProperty("context_quick").GetString());
                Assert.Equal("Open|Open in other pane|Copy to other pane|Open in Terminal|Properties|Edit Menu…", state.GetProperty("context_menu").GetString());
            });
            State(logs, "background", state =>
            {
                Assert.Equal("", state.GetProperty("context_quick").GetString());
                Assert.Equal("Paste|New folder|Pin this folder to the sidebar|Properties|Edit Menu…", state.GetProperty("context_menu").GetString());
            });
            // Off by default: Shift+right-click opens the same menu as a right-click.
            State(logs, "shell-off", state =>
            {
                Assert.Equal("", state.GetProperty("windows_menu").GetString());
                Assert.StartsWith("Open|", state.GetProperty("context_menu").GetString());
            });
            State(logs, "md", state => Assert.Equal("Open|Record Paths|Properties|Edit Menu…", state.GetProperty("context_menu").GetString()));
            State(logs, "txt", state => Assert.Equal("Open|Properties|Edit Menu…", state.GetProperty("context_menu").GetString()));
            State(logs, "windows", state =>
            {
                Assert.Equal("", state.GetProperty("context_menu").GetString());
                Assert.True(state.GetProperty("windows_menu").GetString()!.Split('|').Length > 3, state.GetProperty("windows_menu").GetString());
            });
            // From the menu the right-clicked file, from the palette the cursor's.
            Assert.Equal([Path.Combine(data, "Alphabet.md"), Path.Combine(data, "alpha.txt")], await ReadLinesAsync(recorded, 2));
            Assert.Contains(logs, l => Message(l) == "command failed" && Field(l, "command").GetString() == "program.nosuch"
                && Field(l, "code").GetString() == "unknown_program");
            // The ID no command has is logged once, though the file menu opened twice.
            Assert.Single(logs, l => Message(l) == "context menu entry left out: no command has this ID" && Field(l, "command").GetString() == "hex.view");
            var windowsShown = Assert.Single(logs, l => Message(l) == "windows menu shown");
            // Windows' menu hangs from the focused row as well: its corner is where it was asked for. WinUI moves a menu this
            // long up only when the screen is too low for it, so the top may also lie above the point, never below it.
            var windowsPlaced = Assert.Single(logs, l => Message(l) == "windows menu placed");
            var (askedX, askedY) = (Field(windowsShown, "x").GetDouble(), Field(windowsShown, "y").GetDouble());
            Assert.InRange(Field(windowsPlaced, "left").GetDouble(), askedX - 2, askedX + 2);
            Assert.InRange(Field(windowsPlaced, "top").GetDouble(), double.MinValue, askedY + 2);
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// The menu opens with its top-left corner at the point, as Explorer's does (docs/ui.md, "The context
    /// menu"): a pointer menu at (300, 200) lies there, and a keyboard menu hangs under the focused row, at the
    /// name column's left edge. In a small window the menu that does not fit below the point ends at it, and
    /// the one that does not fit on the right ends at it on the left. "context menu placed" says where WinUI
    /// drew the menu (the icon row's popup and the list's, joined), in the window's coordinates like the
    /// points asked for.
    /// </summary>
    [Fact]
    public async Task The_menu_opens_with_its_corner_at_the_point_and_flips_before_the_windows_edge()
    {
        var (run, root, data) = Prepare("menu-place");
        List<string> seen = [];
        try
        {
            var process = run.Start("place", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "menu-at:alpha.txt|300,200",
                "wait:900",
                "cmd:overlay.close",
                "wait:500",
                "menu:beta.txt",
                "wait:900",
                "cmd:overlay.close",
                "wait:500",
                // The first showing of this shape near an edge is placed from an estimate of its size, the ones after from the measured one.
                "size:900x420",
                "wait:800",
                "menu-at:beta.txt|300,400",
                "wait:900",
                "cmd:overlay.close",
                "wait:500",
                "menu-at:beta.txt|850,150",
                "wait:900",
                "cmd:overlay.close",
                "wait:500",
                "menu-at:beta.txt|850,400",
                "wait:900",
                "cmd:overlay.close",
                "wait:500",
                "menu-at:beta.txt|300,400",
                "wait:900",
                "cmd:overlay.close",
                "wait:500",
                "shot:done"));
            var logs = await run.FinishAsync("place", process, "done");
            seen = logs;

            var shown = logs.Where(l => Message(l) == "context menu shown").ToList();
            var placed = logs.Where(l => Message(l) == "context menu placed").ToList();
            Assert.Equal(6, shown.Count);
            Assert.Equal(6, placed.Count);
            var window = logs.Last(l => Message(l) == "window sized");
            var (windowWidth, windowHeight) = (Field(window, "width").GetDouble(), Field(window, "height").GetDouble());
            double Number(string line, string name) => Field(line, name).GetDouble();
            double Right(string line) => Number(line, "left") + Number(line, "width");
            double Bottom(string line) => Number(line, "top") + Number(line, "height");

            // A pointer menu in the upper part of the list: the corner is the point.
            Assert.False(Field(shown[0], "keyboard").GetBoolean());
            Assert.Equal((300.0, 200.0), (Number(shown[0], "x"), Number(shown[0], "y")));
            Assert.InRange(Number(placed[0], "left"), 298, 302);
            Assert.InRange(Number(placed[0], "top"), 198, 202);
            // A keyboard menu: the corner is the row's bottom-left, at the name column's left edge.
            Assert.True(Field(shown[1], "keyboard").GetBoolean());
            Assert.InRange(Number(placed[1], "left"), Number(shown[1], "row_left") - 2, Number(shown[1], "row_left") + 2);
            Assert.InRange(Number(placed[1], "top"), Number(shown[1], "row_bottom") - 2, Number(shown[1], "row_bottom") + 2);
            Assert.True(Number(shown[1], "row_top") < Number(shown[1], "row_bottom"));
            // Near the bottom of the small window: above the point, its bottom at it, the whole menu in the window.
            Assert.InRange(Bottom(placed[2]), 398, 402);
            Assert.True(Number(placed[2], "top") >= 0 && Number(placed[2], "top") < 400, placed[2]);
            Assert.InRange(Number(placed[2], "left"), 298, 302);
            // Near the right edge: to the left of the point, its right edge at it.
            Assert.InRange(Right(placed[3]), 848, 852);
            Assert.InRange(Number(placed[3], "top"), 148, 152);
            Assert.True(Right(placed[3]) <= windowWidth, placed[3]);
            // Near the corner: both.
            Assert.InRange(Right(placed[4]), 848, 852);
            Assert.InRange(Bottom(placed[4]), 398, 402);
            // The same menu again, now from the size WinUI measured.
            Assert.InRange(Bottom(placed[5]), 398, 402);
            Assert.All(placed.Skip(2), line =>
            {
                Assert.True(Number(line, "left") >= 0 && Right(line) <= windowWidth && Number(line, "top") >= 0 && Bottom(line) <= windowHeight, line);
            });
        }
        catch (Xunit.Sdk.XunitException error) when (seen.Count > 0)
        {
            throw new Xunit.Sdk.XunitException($"{error.Message}\nthe window's log lines (times in UTC):\n{WindowLog.Last(seen, 70)}");
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// A menu asked for while the flyout of the same shape is still closing comes on screen (a fault
    /// of step 1: the cached flyout was shown again inside its own <c>Closed</c>, which WinUI drops, and
    /// every later menu of that shape then waited for a <c>Closed</c> that never came). The file menu
    /// on alpha.txt, closed, and at once the same menu on the same row, twice: the place matters,
    /// since a ShowAt at another place (another row) came through even inside <c>Closed</c>. "On
    /// screen" is WinUI's <c>Opened</c>, not the window's own bookkeeping.
    /// </summary>
    [Fact]
    public async Task A_menu_asked_for_while_the_same_menu_closes_comes_on_screen()
    {
        var (run, root, data) = Prepare("menu-reopen");
        try
        {
            var process = run.Start("reopen", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "menu:alpha.txt",
                // Each menu is waited for until WinUI has it on screen: a loaded machine closes and opens a flyout seconds late
                // (a Closed that came 3 s after its Hide in a run beside two full suites), and a fixed time was a guess.
                "until:menu",
                "shell:first",
                // No step waits between these two: the second menu is asked for while the first one closes.
                "cmd:overlay.close",
                "menu:alpha.txt",
                "until:menu",
                "shell:second",
                "cmd:overlay.close",
                "menu:alpha.txt",
                "until:menu",
                "shell:third",
                "cmd:overlay.close",
                "until:menu-closed",
                "shell:closed",
                "shot:done"));
            var logs = await run.FinishAsync("reopen", process, "done");

            foreach (var label in new[] { "first", "second", "third" })
            {
                State(logs, label, state =>
                {
                    Assert.True(state.GetProperty("context_menu_on_screen").GetBoolean(), $"the {label} menu is not on screen");
                    Assert.Equal("alpha.txt", state.GetProperty("pane0_cursor").GetString());
                    Assert.StartsWith("Open|", state.GetProperty("context_menu").GetString());
                });
            }
            State(logs, "closed", state => Assert.False(state.GetProperty("context_menu_on_screen").GetBoolean()));
            // The second and the third are asked for while the flyout before them still closes: the window logs "context menu closed"
            // when WinUI's Closed comes and no newer menu waits for it, so that line must not lie between the close and the next
            // request. The time between the two lines says nothing about it: it is the UI thread's work for the menu, 15 ms on a
            // quiet machine and 52 and 53 ms in two runs beside two full suites.
            var closes = Positions(logs, l => Message(l) == "command executed" && Field(l, "command").GetString() == "overlay.close");
            var shown = Positions(logs, l => Message(l) == "context menu shown");
            Assert.Equal(3, shown.Count);
            foreach (var i in new[] { 0, 1 })
            {
                Assert.True(closes[i] < shown[i + 1], $"the {(i == 0 ? "second" : "third")} menu was asked for before the close of the one before it");
                Assert.DoesNotContain(logs.Skip(closes[i]).Take(shown[i + 1] - closes[i]), l => Message(l) == "context menu closed");
            }
            Assert.Equal(3, logs.Count(l => Message(l) == "context menu opened"));
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// Phase 18, step 2: "Edit Menu…" turns the file menu into its edit mode. A row goes with its X,
    /// a command comes through "Add Command…" (Insert) and moves with Alt+Up, Delete takes it out,
    /// and "Done" saves the list through the core: the file holds it and the next menu shows it. A
    /// save the core refuses (the file has an error) keeps the edit mode open with a notice; Esc and
    /// the palette's way out leave without saving; from the palette, <c>menu.edit</c> edits the
    /// focused row's menu.
    /// </summary>
    [Fact]
    public async Task The_menu_is_edited_inside_the_menu_and_saved_through_the_core()
    {
        var (run, root, data) = Prepare("menu-edit");
        try
        {
            var configPath = Path.Combine(root, "config", "cabinetos.json");
            var process = run.Start("edit", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "menu:alpha.txt",
                "wait:600",
                "menu-click:Edit Menu…",
                "wait:500",
                "shell:editing",
                // The row's X, pressed through its automation peer as a screen reader would.
                "click:Remove Open in Terminal",
                "shell:removed",
                "menu-edit-key:insert",
                "wait:300",
                "type:New folder",
                "wait:300",
                "accept",
                "wait:300",
                "shell:added",
                "menu-edit-key:alt+up",
                "shell:moved",
                "menu-edit-key:delete",
                "shell:deleted",
                // The drag's own steps, as the pointer takes them: down to the first row and back.
                "menu-edit-drag:Copy to other pane|Open",
                "shell:dragged",
                "menu-edit-drag:Copy to other pane|Open in other pane",
                "shell:dragged-back",
                "click:Done",
                "until:config",
                "wait:300",
                "shell:saved",
                "menu:alpha.txt",
                "shell:after",
                // The same menu at once, while this one closes (A_menu_asked_for_while_the_same_menu_closes_comes_on_screen).
                "cmd:overlay.close",
                // The file gets an error while the list is edited: the core refuses the save.
                "menu:alpha.txt",
                "wait:600",
                "menu-click:Edit Menu…",
                "wait:500",
                "click:Remove Copy to other pane",
                "shell:break-the-file",
                "until:config-error",
                "click:Done",
                "wait:1000",
                "shell:refused",
                // The test puts the file back; the core says it is valid again.
                "until:config",
                "click:Done",
                "wait:1000",
                "shell:saved-again",
                // Esc leaves without saving.
                "menu:alpha.txt",
                "wait:600",
                "menu-click:Edit Menu…",
                "wait:500",
                "menu-edit-key:delete",
                "shell:before-esc",
                "menu-edit-key:escape",
                "shell:after-esc",
                "menu:alpha.txt",
                "wait:600",
                "shell:unchanged",
                "cmd:overlay.close",
                // From the palette there is no open menu: the focused row's is edited; overlay.close leaves it.
                "cmd:menu.edit",
                "wait:300",
                "shell:from-palette",
                "cmd:overlay.close",
                "shell:closed",
                "shot:done"));
            await run.WaitForStateAsync("edit", "saved");
            var afterFirstSave = File.ReadAllText(configPath);
            await run.WaitForStateAsync("edit", "break-the-file");
            File.WriteAllText(configPath, "{ \"version\": 1, \"ui\": ");
            await run.WaitForStateAsync("edit", "refused");
            File.WriteAllText(configPath, afterFirstSave);
            var logs = await run.FinishAsync("edit", process, "done");

            State(logs, "editing", state =>
            {
                Assert.Equal("File menu", state.GetProperty("menu_edit_target").GetString());
                Assert.Equal("Open|Open in other pane|Copy to other pane|Open in Terminal", state.GetProperty("menu_edit").GetString());
                // The menu turned into the edit mode: it is not open as well.
                Assert.Equal("", state.GetProperty("context_menu").GetString());
            });
            State(logs, "removed", state => Assert.Equal("Open|Open in other pane|Copy to other pane", state.GetProperty("menu_edit").GetString()));
            State(logs, "added", state => Assert.Equal("Open|Open in other pane|Copy to other pane|New folder", state.GetProperty("menu_edit").GetString()));
            State(logs, "moved", state => Assert.Equal("Open|Open in other pane|New folder|Copy to other pane", state.GetProperty("menu_edit").GetString()));
            State(logs, "deleted", state => Assert.Equal("Open|Open in other pane|Copy to other pane", state.GetProperty("menu_edit").GetString()));
            State(logs, "dragged", state => Assert.Equal("Copy to other pane|Open|Open in other pane", state.GetProperty("menu_edit").GetString()));
            State(logs, "dragged-back", state => Assert.Equal("Open|Open in other pane|Copy to other pane", state.GetProperty("menu_edit").GetString()));
            Assert.Equal(2, logs.Count(l => Message(l) == "menu edit step" && Field(l, "step").GetString() == "drag"));
            State(logs, "saved", state => Assert.Equal("", state.GetProperty("menu_edit").GetString()));
            State(logs, "after", state => Assert.Equal("Open|Open in other pane|Copy to other pane|Properties|Edit Menu…", state.GetProperty("context_menu").GetString()));

            // Refused: the edit mode stays, with its rows, and the status bar says why.
            State(logs, "refused", state => Assert.Equal("Open|Open in other pane", state.GetProperty("menu_edit").GetString()));
            var refused = Assert.Single(logs, l => Message(l) == "menu edit refused");
            Assert.Equal("config_error", Field(refused, "code").GetString());
            Assert.Contains(logs, l => Message(l) == "notice shown" && Field(l, "text").GetString()!.StartsWith("The menu was not saved:", StringComparison.Ordinal)
                && Field(l, "error").GetBoolean());
            State(logs, "saved-again", state => Assert.Equal("", state.GetProperty("menu_edit").GetString()));

            State(logs, "before-esc", state => Assert.Equal("Open in other pane", state.GetProperty("menu_edit").GetString()));
            State(logs, "after-esc", state => Assert.Equal("", state.GetProperty("menu_edit").GetString()));
            State(logs, "unchanged", state => Assert.Equal("Open|Open in other pane|Properties|Edit Menu…", state.GetProperty("context_menu").GetString()));
            State(logs, "from-palette", state =>
            {
                Assert.Equal("File menu", state.GetProperty("menu_edit_target").GetString());
                Assert.Equal("Open|Open in other pane", state.GetProperty("menu_edit").GetString());
            });
            State(logs, "closed", state => Assert.Equal("", state.GetProperty("menu_edit").GetString()));

            Assert.Equal(["contextMenu.file.items", "contextMenu.file.items"],
                logs.Where(l => Message(l) == "menu edit saved").Select(l => Field(l, "path").GetString()));
            Assert.Equal([true, true, false, false], logs.Where(l => Message(l) == "menu edit closed").Select(l => Field(l, "saved").GetBoolean()));

            // The core wrote the list in the defaults' shape; the other targets kept theirs.
            using var config = JsonDocument.Parse(File.ReadAllText(configPath));
            var menu = config.RootElement.GetProperty("contextMenu");
            Assert.Equal("""[{"command":"pane.openSelected"},{"command":"file.openInOtherPane"}]""", JsonSerializer.Serialize(menu.GetProperty("file").GetProperty("items")));
            Assert.Equal(4, menu.GetProperty("folder").GetProperty("items").GetArrayLength());
            var core = LogFiles.Core(Path.Combine(root, "logs-edit"));
            Assert.Equal(2, core.Count(l => Message(l) == "configuration changed" && Field(l, "changed").GetString()!.Contains("contextMenu.file.items", StringComparison.Ordinal)));
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// The speed review of 2026-10-01 found the window ended by WinUI (no crash trace, a fault in CoreMessagingXP)
    /// when the keyboard's menu was asked for a row the list had made ahead of the view but scrolled out of it: the
    /// menu was shown below the window. The row's menu now opens near the top of the pane, inside the window, and
    /// the window goes on.
    /// </summary>
    [Fact]
    public async Task The_keyboards_menu_for_a_row_out_of_view_opens_inside_the_window()
    {
        var (run, root, data) = Prepare("menu-out-of-view");
        try
        {
            for (var i = 0; i < 120; i++)
            {
                File.WriteAllText(Path.Combine(data, $"row-{i:D3}.txt"), "x");
            }
            var process = run.Start("out-of-view", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "wait:800",
                "menu:row-040.txt",
                // "context menu placed" is logged some low-priority dispatcher turns after WinUI opens the menu, which a busy
                // machine does late; closing the menu before that logs no place (all of 5 runs beside two full test runs).
                "until:menu-placed",
                "shell:open",
                "cmd:overlay.close",
                "wait:300",
                "shot:done"));
            var logs = await run.FinishAsync("out-of-view", process, "done");

            // A failure shows the window's lines with their times: which step ran when, and what the menu did.
            try
            {
                var shown = OnlyLine(logs, "context menu shown");
                Assert.True(Field(shown, "keyboard").GetBoolean());
                Assert.InRange(Field(shown, "y").GetDouble(), 0, 700);
                State(logs, "open", state => Assert.True(state.GetProperty("context_menu_on_screen").GetBoolean()));
                var placed = OnlyLine(logs, "context menu placed");
                Assert.InRange(Field(placed, "top").GetDouble() + Field(placed, "height").GetDouble(), 0, 700);
            }
            catch (Xunit.Sdk.XunitException error)
            {
                throw new Xunit.Sdk.XunitException($"{error.Message}\nthe window's log lines (times in UTC):\n{Timeline(logs)}");
            }
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// The speed review of 2026-10-01: the window builds the menu's common shapes (a file, a folder, the
    /// pane's space, several rows) while it is idle after start, so the first right-click on each finds
    /// its flyout built and builds nothing ("built": false).
    /// </summary>
    [Fact]
    public async Task The_first_menu_of_a_file_a_folder_and_the_space_finds_its_flyout_built_at_start()
    {
        var (run, root, data) = Prepare("menu-prepared");
        try
        {
            Directory.CreateDirectory(Path.Combine(data, "gamma"));
            // Both panes start in the data folder, where the shapes are prepared.
            File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["version"] = 1,
                ["ui"] = new Dictionary<string, object> { ["dualPane"] = true, ["lastPaths"] = new[] { data, data } },
            }));
            var process = run.Start("prepared", string.Join(';',
                "size:1200x700",
                "pane:0",
                "wait:500",
                "menu:alpha.txt",
                "wait:500",
                "cmd:overlay.close",
                "wait:300",
                "menu:gamma",
                "wait:500",
                "cmd:overlay.close",
                "wait:300",
                "menu:*",
                "wait:500",
                "cmd:overlay.close",
                "wait:300",
                "shot:done"));
            var logs = await run.FinishAsync("prepared", process, "done");

            var prepared = logs.Where(l => Message(l) == "context menu prepared").ToList();
            Assert.Equal(["File", "Folder", "Background", "MultiSelect"], prepared.Select(l => Field(l, "target").GetString()));
            // The default menus give the rows one shape: the first row's preparing builds it, the others find it.
            Assert.True(Field(prepared[0], "built").GetBoolean());
            var shown = logs.Where(l => Message(l) == "context menu shown").ToList();
            Assert.Equal(["File", "Folder", "Background"], shown.Select(l => Field(l, "target").GetString()));
            Assert.True(Timestamp(prepared[^1]) < Timestamp(shown[0]), "the shapes were prepared before the first right-click");
            Assert.All(shown, l => Assert.False(Field(l, "built").GetBoolean(), $"a first right-click built its menu: {l}"));
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// The frame measurement, alone: another test's window on the same desktop takes the processor and
    /// the GPU, and its load shows in this window's frames (one 35 ms frame in the full run of
    /// 2026-09-30, none when the test ran by itself).
    /// </summary>
    [Collection(FrameTests.Name)]
    public class Alone
    {
        /// <summary>
        /// The menu over every row of the 100,000-entry bench folder, all selected: no frame with more
        /// than 33 ms of UI-thread work while it opens (docs/ui.md, "Scrolling", measures the same way).
        /// The first opening of the process is timed too, and logged, since WinUI loads the flyout's
        /// template then.
        /// </summary>
        [Fact]
        public async Task Opening_the_menu_over_100000_selected_rows_adds_no_slow_frame()
        {
            var bench = Path.Combine(Path.GetTempPath(), "cabinetos-bench", "100000");
            if (!File.Exists(bench + ".complete"))
            {
                Assert.Skip($"Needs the bench folder {bench}: cargo bench -p cabinetos-fs --bench list_directory makes it.");
            }
            var (run, root, data) = Prepare("menu-bench");
            try
            {
                var process = run.Start("bench", string.Join(';',
                    "size:1200x700",
                    "pane:0",
                    $"path:{data}",
                    "wait:500",
                    "menu:alpha.txt",
                    "wait:800",
                    "shell:warm",
                    "cmd:overlay.close",
                    $"path:{bench}",
                    "wait:3000",
                    "selectall",
                    "wait:1500",
                    "shell:before",
                    "menu:",
                    "wait:1500",
                    "shell:open",
                    "cmd:overlay.close",
                    "wait:500",
                    "shot:done"), frameStats: true);
                var logs = await run.FinishAsync("bench", process, "done");

                DateTime At(string label) => Timestamp(logs.Single(l => Message(l) == "shell state" && Field(l, "label").GetString() == label));
                var (before, open) = (At("before"), At("open"));
                State(logs, "open", state => Assert.StartsWith("Open|", state.GetProperty("context_menu").GetString()));
                var shown = logs.Where(l => Message(l) == "context menu shown").ToList();
                Assert.Equal(2, shown.Count);
                Assert.Equal("MultiSelect", Field(shown[1], "target").GetString());
                var slow = logs.Where(l => Message(l) == "slow frame" && Timestamp(l) > before && Timestamp(l) <= open
                    && Field(l, "busy_ms").GetDouble() > 33).ToList();
                Assert.True(slow.Count == 0, $"frames with over 33 ms of UI-thread work while the menu opened:\n{string.Join('\n', slow)}\n"
                    + string.Join('\n', logs.Where(l => Timestamp(l) > before && Timestamp(l) <= open)));
            }
            finally
            {
                run.Stop();
                Repo.RemoveTempFolder(root);
            }
        }

        /// <summary>
        /// The edit mode over the 100,000 selected rows of the bench folder: entering it adds no frame
        /// with more than 33 ms of UI-thread work. The first entering of the process builds the
        /// surface's templates, as the menu's first opening does, so it is done once in the small
        /// folder first and its time logged. A slow frame can come from the machine: the same collection of the garbage
        /// collector took 21 to 33 ms in eight runs beside a full suite and 40 busy processes, and stretched one frame over
        /// 33 ms in two of them. So each start waits until the machine is calm (up to 30 s), and the window gets up to
        /// <see cref="Attempts"/> fresh starts: the test fails only when every one has a slow frame. Work of the window's
        /// own on 100,000 rows is in every start; a pause of the machine's is not.
        /// </summary>
        [Fact]
        public async Task Entering_the_edit_mode_over_100000_selected_rows_adds_no_slow_frame()
        {
            var bench = Path.Combine(Path.GetTempPath(), "cabinetos-bench", "100000");
            if (!File.Exists(bench + ".complete"))
            {
                Assert.Skip($"Needs the bench folder {bench}: cargo bench -p cabinetos-fs --bench list_directory makes it.");
            }
            var seen = new List<string>();
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                var calm = await WaitForCalmAsync();
                var before = CpuLoad.Read(null);
                if (await EditModeAttemptAsync(bench) is not { } slow)
                {
                    return;
                }
                seen.Add($"start {attempt}: the machine was {calm:N0} % busy before the start and {CpuLoad.Between(before, CpuLoad.Read(null)).TotalPercent:N0} % during it\n{slow}");
            }
            Assert.Fail($"every one of {Attempts} fresh windows had a frame with over 33 ms of UI-thread work while the edit mode opened:\n{string.Join("\n\n", seen)}");
        }

        private const int Attempts = 3;

        // Whether the machine is calm: under 35 % busy (Task Manager's CPU, all processors) over one second. The main PC idles at
        // 13 to 19 % with its other sessions, and a full suite beside this test is at 40 to 55 % in its first minute. Gives up after
        // 30 s and returns the last reading: a machine that is never calm is measured anyway, and the failure says how busy it was.
        private static async Task<double> WaitForCalmAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                var before = CpuLoad.Read(null);
                await Task.Delay(1000);
                var busy = CpuLoad.Between(before, CpuLoad.Read(null)).TotalPercent;
                if (busy < 35 || DateTime.UtcNow >= deadline)
                {
                    return busy;
                }
            }
        }

        // One fresh window: null when no frame of the opening had over 33 ms of UI-thread work, else the frames and the log of the opening.
        private static async Task<string?> EditModeAttemptAsync(string bench)
        {
            var (run, root, data) = Prepare("menu-edit-bench");
            try
            {
                var process = run.Start("edit-bench", string.Join(';',
                    "size:1200x700",
                    "pane:0",
                    $"path:{data}",
                    "wait:500",
                    "menu:alpha.txt",
                    "wait:800",
                    "menu-click:Edit Menu…",
                    "wait:800",
                    "cmd:overlay.close",
                    $"path:{bench}",
                    "wait:3000",
                    "selectall",
                    "wait:1500",
                    "menu:",
                    "wait:1500",
                    "shell:before",
                    "menu-click:Edit Menu…",
                    "wait:1500",
                    "shell:open",
                    "cmd:overlay.close",
                    "wait:500",
                    "shot:done"), frameStats: true);
                var logs = await run.FinishAsync("edit-bench", process, "done");

                DateTime At(string label) => Timestamp(logs.Single(l => Message(l) == "shell state" && Field(l, "label").GetString() == label));
                var (before, open) = (At("before"), At("open"));
                State(logs, "open", state =>
                {
                    Assert.Equal("Selection menu", state.GetProperty("menu_edit_target").GetString());
                    Assert.StartsWith("Open|", state.GetProperty("menu_edit").GetString());
                });
                var shown = logs.Where(l => Message(l) == "menu edit shown").ToList();
                Assert.Equal(2, shown.Count);
                Assert.Equal("MultiSelect", Field(shown[1], "target").GetString());
                var slow = logs.Where(l => Message(l) == "slow frame" && Timestamp(l) > before && Timestamp(l) <= open
                    && Field(l, "busy_ms").GetDouble() > 33).ToList();
                return slow.Count == 0 ? null : $"{string.Join('\n', slow)}\n{string.Join('\n', logs.Where(l => Timestamp(l) > before && Timestamp(l) <= open))}";
            }
            finally
            {
                run.Stop();
                Repo.RemoveTempFolder(root);
            }
        }
    }

    private static async Task<List<string>> ReadLinesAsync(string path, int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            if (File.Exists(path))
            {
                // wscript writes UTF-16 (the last argument of OpenTextFile).
                var lines = File.ReadAllLines(path, System.Text.Encoding.Unicode).Where(l => l.Length > 0).ToList();
                if (lines.Count >= count)
                {
                    return lines;
                }
            }
            Assert.True(DateTime.UtcNow < deadline, $"{path} did not get {count} lines");
            await Task.Delay(200);
        }
    }

    private sealed class Run(string root, string exe, string core)
    {
        private readonly List<Process> _started = [];

        // Starts the window on the run's configuration; the snapshot steps are the test's script.
        public Process Start(string name, string steps, bool frameStats = false)
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            start.Environment["CABINETOS_CORE_EXE"] = core;
            start.Environment["CABINETOS_CONFIG"] = Path.Combine(root, "config", "cabinetos.json");
            start.Environment["CABINETOS_LOG_DIR"] = Path.Combine(root, "logs-" + name);
            start.Environment["CABINETOS_THEMES_DIR"] = Path.Combine(root, "themes");
            start.Environment["CABINETOS_UNDO_DIR"] = Path.Combine(root, "undo");
            start.Environment["CABINETOS_PLUGINS_DIR"] = Path.Combine(root, "plugins");
            start.Environment["CABINETOS_PLUGINS_DATA_DIR"] = Path.Combine(root, "plugins-data");
            start.Environment["CABINETOS_MARKETPLACE_DIR"] = Path.Combine(root, "marketplace");
            start.Environment["CABINETOS_WEBVIEW2_DIR"] = Path.Combine(root, "webview2");
            start.Environment["CABINETOS_TOOLS_DIR"] = Path.Combine(root, "tools");
            start.Environment["CABINETOS_UPDATE_DIR"] = Path.Combine(root, "update");
            start.Environment["CABINETOS_UI_SNAPSHOT"] = Path.Combine(root, "shots-" + name);
            start.Environment["CABINETOS_UI_SNAPSHOT_STEPS"] = steps;
            if (frameStats)
            {
                start.Environment["CABINETOS_UI_FRAMESTATS"] = "1";
            }
            var process = Process.Start(start)!;
            _started.Add(process);
            return process;
        }

        // Waits until the window logged the "shell state" line with this label.
        public async Task WaitForStateAsync(string name, string label)
        {
            var folder = Path.Combine(root, "logs-" + name);
            await WaitForAsync(() => LogFiles.Ui(folder).Any(l => Message(l) == "shell state" && Field(l, "label").GetString() == label),
                $"the {name} window's state {label}", TimeSpan.FromSeconds(60));
        }

        // Waits for the last snapshot, closes the window the way a user does and returns the UI's log lines.
        public async Task<List<string>> FinishAsync(string name, Process process, string lastShot)
        {
            var shot = Path.Combine(root, "shots-" + name, lastShot + ".png");
            await WaitForAsync(() => File.Exists(shot), $"the {name} window's last snapshot", TimeSpan.FromSeconds(120));
            process.CloseMainWindow();
            Assert.True(process.WaitForExit(20_000), $"the {name} window did not close");
            var logs = LogFiles.Ui(Path.Combine(root, "logs-" + name));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "logs-" + name), "crash-*.json"));
            // The error lines themselves, not the whole log as the collection assert prints it.
            var errors = logs.Where(l => Level(l) == "ERROR").ToList();
            Assert.True(errors.Count == 0, $"the {name} window logged {errors.Count} error line(s):\n{string.Join('\n', errors.Select(l => l.Length > 500 ? l[..500] : l))}");
            return logs;
        }

        public void Stop()
        {
            foreach (var process in _started.Where(p => !p.HasExited))
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    // A folder with a window's setting up: dual panes, and a data folder of three files.
    private static (Run Run, string Root, string Data) Prepare(string purpose)
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
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "alpha.txt"), "x");
        File.WriteAllText(Path.Combine(data, "Alphabet.md"), "x");
        File.WriteAllText(Path.Combine(data, "beta.txt"), "x");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), """{ "version": 1, "ui": { "dualPane": true } }""");
        return (new Run(root, exe, core), root, data);
    }

    // Where in the log the lines that match are, oldest first: a line's place says what came before what, whatever the clock says.
    private static List<int> Positions(List<string> logs, Func<string, bool> match) =>
        [.. logs.Select((line, index) => (line, index)).Where(p => match(p.line)).Select(p => p.index)];

    // The one line with this message; a failure names the message, which Assert.Single does not.
    private static string OnlyLine(List<string> logs, string message)
    {
        var found = logs.Where(l => Message(l) == message).ToList();
        Assert.True(found.Count == 1, $"the window logged \"{message}\" {found.Count} times, once was expected");
        return found[0];
    }

    // The window's last log lines, each with its time, message and fields (cut short): what a failed check shows.
    private static string Timeline(List<string> logs, int last = 45) =>
        string.Join('\n', logs.Where(l => Message(l) is not ("slow frame" or "frame stats")).TakeLast(last).Select(l =>
        {
            using var parsed = JsonDocument.Parse(l);
            var fields = parsed.RootElement.TryGetProperty("fields", out var f) ? f.ToString() : "";
            return $"  {parsed.RootElement.GetProperty("ts").GetString()} {Message(l)} {(fields.Length > 200 ? fields[..200] : fields)}";
        }));

    private static void State(List<string> logs, string label, Action<JsonElement> check)
    {
        var line = Assert.Single(logs, l => Message(l) == "shell state" && Field(l, "label").GetString() == label);
        using var parsed = JsonDocument.Parse(line);
        try
        {
            check(parsed.RootElement.GetProperty("fields"));
        }
        catch (Xunit.Sdk.XunitException error)
        {
            // What the window did before the state was read, and after: a late menu shows as a line that comes after the state.
            throw new Xunit.Sdk.XunitException($"state \"{label}\": {error.Message}\nthe window's log lines around the state (times in UTC):\n{WindowLog.Around(logs, logs.IndexOf(line))}");
        }
    }

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

    private static DateTime Timestamp(string line)
    {
        using var parsed = JsonDocument.Parse(line);
        return DateTime.Parse(parsed.RootElement.GetProperty("ts").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    private static JsonElement Field(string line, string name)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.GetProperty("fields").GetProperty(name).Clone();
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
