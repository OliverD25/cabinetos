using System.Diagnostics;
using System.Text.Json;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Terminal;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Units 1 and 2 of the terminal sprint on a real window and core (docs/ui.md, "The terminal"): sessions bound to a
/// pane, Ctrl+` summoning the pane's own session, the Zero-Hijack rule, the mode toggle with the core's event, the
/// terminal's tab keys, and the prompt hook: a linked shell follows its pane at its next prompt, a locked one stays,
/// and the caption shows the folder each shell reports. The snapshot step <c>key:</c> sends a key as key messages to the window's input window, or
/// through DevTools into the terminal's page when the page has the keyboard, so the page's own key path runs.
/// <c>terminal-state:</c> logs the header's tabs ("*1 cmd [Left] Locked", the shown one marked) and who has the
/// keyboard. The shells are cmd (always on Windows), a cmd profile that says it is linkable, and Windows PowerShell
/// (always on Windows too), which gets the prompt hook. Unit 5 adds the tabs across a restart: one window saves them, the
/// next starts with the same file and its first Ctrl+` brings them back; with <c>terminal.restore</c> off nothing comes
/// back; what cannot come back as it was falls back. A window that closes ends its core, so the second window's core is a
/// new one, as after any restart. Opt-in with <c>CABINETOS_UI_E2E=1</c>, like the other end-to-end tests.
/// </summary>
public class TerminalEndToEndTests
{
    private const string OptIn = "CABINETOS_UI_E2E";

    /// <summary>
    /// Ctrl+` in the left pane starts the left pane's session; a click in the right pane and Ctrl+` there starts the
    /// right pane's own, and the dock stays. Ctrl+` in the terminal gives the keyboard back to the pane, and only that
    /// second Ctrl+` from the pane hides the dock; the next one brings the same session back, without a new shell.
    /// </summary>
    [Fact]
    public async Task Ctrl_backquote_in_each_pane_shows_that_pane_s_session_and_never_hides_when_the_pane_changes()
    {
        var (run, root, data) = Prepare("terminal-summon");
        try
        {
            var process = run.Start("summon", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "key:ctrl+backquote",
                "until:terminals:1",
                "wait:1200",
                "terminal-state:left",
                // The click's own effect: the right pane gets the keyboard and becomes the active pane.
                "pane:1",
                "wait:400",
                "key:ctrl+backquote",
                "until:terminals:2",
                "wait:1200",
                "terminal-state:right",
                "focus:right",
                // In the terminal: back to the pane. From the pane, the second one hides.
                "key:ctrl+backquote",
                "wait:800",
                "terminal-state:back",
                // The active pane changes and comes back in between, as closing a tool in the other pane does
                // (the live check of 2026-10-02): the second Ctrl+` still hides.
                "pane:0",
                "wait:300",
                "pane:1",
                "wait:300",
                "key:ctrl+backquote",
                "wait:800",
                "terminal-state:hidden",
                "key:ctrl+backquote",
                "wait:1500",
                "terminal-state:again",
                "shot:done"));
            var logs = await run.FinishAsync("summon", process, "done");
            var state = States(logs);
            string Tabs(string label) => Field(state[label], "tabs").GetString()!;
            bool Dock(string label) => Field(state[label], "dock").GetBoolean();
            bool Keyboard(string label) => Field(state[label], "terminal_keyboard").GetBoolean();

            var opened = logs.Where(l => Message(l) == "terminal session opened").ToList();
            Assert.True(opened.Count == 2, "two sessions, one per pane" + Evidence(logs));
            Assert.Equal(["left", "right"], opened.Select(l => Field(l, "pane").GetString()));
            var (first, second) = (Field(opened[0], "session_id").GetUInt64(), Field(opened[1], "session_id").GetUInt64());

            Assert.Equal($"*{first} cmd [Left] Locked", Tabs("left"));
            Assert.True(Dock("left") && Keyboard("left"), "the left pane's session has the keyboard" + Evidence(logs));
            Assert.Equal($"{first} cmd [Left] Locked | *{second} cmd [Right] Locked", Tabs("right"));
            Assert.True(Dock("right") && Keyboard("right"), "the right pane's session has the keyboard" + Evidence(logs));
            Assert.Equal("right", Field(state["right"], "active_pane").GetString());
            Assert.True(PageHasKeys(logs, "right"), "Windows sends the keys to the terminal's page" + Evidence(logs));

            Assert.True(Dock("back") && !Keyboard("back"), "Ctrl+` in the terminal gave the keyboard back" + Evidence(logs));
            Assert.False(Dock("hidden"), "the second Ctrl+` from the pane hid the dock" + Evidence(logs));
            Assert.True(Dock("again") && Keyboard("again"), "the dock came back with the keyboard" + Evidence(logs));
            Assert.Equal($"{first} cmd [Left] Locked | *{second} cmd [Right] Locked", Tabs("again"));

            var actions = logs.Where(l => Message(l) == "terminal summoned").Select(l => Field(l, "action").GetString()).ToList();
            Assert.Equal(["OpenNew", "OpenNew", "HandBackToPane", "Hide", "ShowSession"], actions);
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// The Zero-Hijack rule: with the left pane's session shown and holding the keyboard, a click in the right pane and
    /// navigation there change neither the shown session nor anything in the shell: no tab is shown anew, no path is
    /// typed. The click takes the keyboard to the pane, as any click does.
    /// </summary>
    [Fact]
    public async Task A_pane_click_and_navigation_leave_the_shown_session_and_the_shell_alone()
    {
        var (run, root, data) = Prepare("terminal-hijack");
        try
        {
            var other = Path.Combine(data, "sub");
            Directory.CreateDirectory(other);
            var process = run.Start("hijack", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "key:ctrl+backquote",
                "until:terminals:1",
                "wait:1200",
                "focus:in-terminal",
                "terminal-state:before",
                "pane:1",
                "wait:500",
                "focus:after-click",
                "terminal-state:after-click",
                $"path:{other}",
                "wait:800",
                "pane:0",
                $"path:{other}",
                "wait:800",
                "terminal-state:after-navigation",
                "shot:done"));
            var logs = await run.FinishAsync("hijack", process, "done");
            var state = States(logs);

            Assert.True(PageHasKeys(logs, "in-terminal"), "Windows sends the keys to the terminal's page" + Evidence(logs));
            var shown = Field(state["before"], "shown").GetUInt64();
            foreach (var label in new[] { "after-click", "after-navigation" })
            {
                Assert.Equal(shown, Field(state[label], "shown").GetUInt64());
                Assert.Equal(Field(state["before"], "tabs").GetString(), Field(state[label], "tabs").GetString());
                Assert.True(Field(state[label], "dock").GetBoolean(), label);
            }
            Assert.Equal("right", Field(state["after-click"], "active_pane").GetString());
            Assert.Equal(("FilePane", "window"), (Field(Owner(logs, "after-click"), "element").GetString(), Field(Owner(logs, "after-click"), "keys_to").GetString()));
            // Nothing went to the shell and no tab came to the front after the first.
            Assert.Single(logs, l => Message(l) == "terminal tab shown");
            Assert.DoesNotContain(logs, l => Message(l) is "paths typed at the prompt" or "cwd sync");
            Assert.Single(logs, l => Message(l) == "terminal session opened");
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// The tab's mode toggle runs <c>terminal.setMode</c>; the tab changes when the core's
    /// <c>terminal_mode_changed</c> comes back. A profile that cannot be linked shows no toggle, and the command says why.
    /// </summary>
    [Fact]
    public async Task The_mode_toggle_runs_set_mode_and_the_core_s_event_changes_the_tab()
    {
        var (run, root, data) = Prepare("terminal-mode", defaultProfile: "hooked");
        try
        {
            var process = run.Start("mode", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "key:ctrl+backquote",
                "until:terminals:1",
                "wait:800",
                "terminal-state:locked",
                "click:Locked, hooked on the left pane",
                "wait:1200",
                "terminal-state:linked",
                "cmd:terminal.setMode",
                "wait:1200",
                "terminal-state:relocked",
                "cmd:terminal.new {\"profile\":\"cmd\"}",
                "until:terminals:2",
                "wait:800",
                "cmd:terminal.setMode {\"mode\":\"linked\"}",
                "wait:800",
                "terminal-state:cmd",
                "shot:done"));
            var logs = await run.FinishAsync("mode", process, "done");
            var state = States(logs);
            string Tabs(string label) => Field(state[label], "tabs").GetString()!;
            var hooked = Field(Assert.Single(logs, l => Message(l) == "terminal session opened" && Field(l, "profile").GetString() == "hooked"), "session_id").GetUInt64();

            Assert.Equal($"*{hooked} hooked [Left] Locked", Tabs("locked"));
            Assert.True(Tabs("linked") == $"*{hooked} hooked [Left] Linked", "the toggle linked it" + Evidence(logs));
            Assert.Equal($"*{hooked} hooked [Left] Locked", Tabs("relocked"));
            var set = logs.Where(l => Message(l) == "command executed" && Field(l, "command").GetString() == "terminal.setMode").ToList();
            Assert.Equal("button", Field(set[0], "trigger").GetString());
            // The tab follows the core's event, once per change.
            var changed = logs.Where(l => Message(l) == "terminal mode changed").Select(l => Field(l, "mode").GetString()).ToList();
            Assert.Equal(["linked", "locked"], changed);
            Assert.True(Field(Assert.Single(logs, l => Message(l) == "terminal session opened" && Field(l, "profile").GetString() == "cmd"), "linkable").GetBoolean() is false);
            Assert.EndsWith(" cmd [Left] Locked", Tabs("cmd"));
            Assert.Contains(logs, l => Message(l) == "notice shown" && Field(l, "text").GetString()!.StartsWith("cmd cannot be linked to a pane", StringComparison.Ordinal));
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// In the terminal, Ctrl+Shift+T opens a session bound to the active pane, Alt+[ and Alt+] go round the tabs and
    /// the keyboard stays in the terminal, and Ctrl+Shift+W closes the shown tab. Alt+[ and Alt+] are the keys at
    /// that place, whatever the layout puts on them (the key's virtual-key code, as a Ukrainian layout sends it).
    /// </summary>
    [Fact]
    public async Task The_terminal_s_tab_keys_open_cycle_and_close_its_tabs()
    {
        var (run, root, data) = Prepare("terminal-keys");
        try
        {
            var process = run.Start("keys", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "key:ctrl+backquote",
                "until:terminals:1",
                "wait:1200",
                "key:ctrl+shift+t",
                "until:terminals:2",
                "wait:1200",
                "terminal-state:two",
                "key:alt+bracketleft",
                "wait:700",
                "terminal-state:previous",
                "key:alt+bracketright",
                "wait:700",
                "terminal-state:next",
                "key:ctrl+shift+w",
                "wait:2000",
                "terminal-state:closed",
                "focus:end",
                "shot:done"));
            var logs = await run.FinishAsync("keys", process, "done");
            var state = States(logs);
            string Tabs(string label) => Field(state[label], "tabs").GetString()!;
            var opened = logs.Where(l => Message(l) == "terminal session opened").ToList();
            Assert.True(opened.Count == 2, "Ctrl+Shift+T opened a second session" + Evidence(logs));
            Assert.All(opened, l => Assert.Equal("left", Field(l, "pane").GetString()));
            var (first, second) = (Field(opened[0], "session_id").GetUInt64(), Field(opened[1], "session_id").GetUInt64());

            Assert.Equal($"{first} cmd [Left] Locked | *{second} cmd [Left] Locked", Tabs("two"));
            Assert.True(Tabs("previous") == $"*{first} cmd [Left] Locked | {second} cmd [Left] Locked", "Alt+[ showed the previous tab" + Evidence(logs));
            Assert.Equal($"{first} cmd [Left] Locked | *{second} cmd [Left] Locked", Tabs("next"));
            Assert.True(Tabs("closed") == $"*{first} cmd [Left] Locked", "Ctrl+Shift+W closed the shown tab" + Evidence(logs));
            foreach (var label in new[] { "two", "previous", "next", "closed" })
            {
                Assert.True(Field(state[label], "terminal_keyboard").GetBoolean(), $"the terminal kept the keyboard ({label})" + Evidence(logs));
                Assert.Equal("left", Field(state[label], "active_pane").GetString());
            }
            Assert.True(PageHasKeys(logs, "end"), "Windows sends the keys to the terminal's page" + Evidence(logs));
            var ran = logs.Where(l => Message(l) == "command executed" && Field(l, "trigger").GetString() == "key")
                .Select(l => Field(l, "command").GetString()).ToList();
            foreach (var command in new[] { "terminal.new", "terminal.previousTab", "terminal.nextTab", "terminal.close" })
            {
                Assert.Contains(command, ran);
            }
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// The prompt hook in the window: a PowerShell session linked by its tab's toggle follows its pane's folder when
    /// Enter draws its next prompt, and the caption names the folder the shell reports ("in sub"). Nothing reaches
    /// the shell until it draws a prompt.
    /// </summary>
    [Fact]
    public async Task A_linked_session_follows_its_pane_at_the_next_prompt_and_the_caption_shows_it()
    {
        var (run, root, data) = Prepare("terminal-follow");
        try
        {
            var sub = Path.Combine(data, "sub folder");
            Directory.CreateDirectory(sub);
            var process = run.Start("follow", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "cmd:terminal.new {\"profile\":\"ps\"}",
                "until:terminals:1",
                $"until:terminal-folder:{data}",
                "terminal-state:start",
                "click:Locked, ps on the left pane",
                "wait:1200",
                "terminal-state:linked",
                $"path:{sub}",
                "wait:1500",
                "terminal-state:moved",
                "terminal:{enter}",
                $"until:terminal-folder:{sub}",
                "terminal-state:followed",
                "shot:done"));
            var logs = await run.FinishAsync("follow", process, "done");
            var state = States(logs);
            string Caption(string label) => Field(state[label], "caption").GetString()!;
            string? Folder(string label) => Field(state[label], "folder").GetString();

            Assert.True(Folder("start") == data, "the shell reported its first folder" + Evidence(logs));
            Assert.Equal("in data", Caption("start"));
            Assert.EndsWith(" ps [Left] Linked", Field(state["linked"], "tabs").GetString(), StringComparison.Ordinal);
            // The pane moved, but the shell has not drawn a prompt since: nothing reached it.
            Assert.Equal((data, "in data"), (Folder("moved"), Caption("moved")));
            Assert.True(Folder("followed") == sub, "Enter's prompt followed the pane" + Evidence(logs));
            Assert.Equal("in sub folder", Caption("followed"));
            var reported = logs.Where(l => Message(l) == "terminal folder changed").Select(l => Field(l, "folder").GetString()).ToList();
            Assert.Equal([data, sub], reported);
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// A locked PowerShell session stays where it is when its pane moves, also after Enter; a <c>cd</c> of the
    /// user's own moves it, and the caption follows the shell's report.
    /// </summary>
    [Fact]
    public async Task A_locked_session_stays_and_the_caption_follows_the_user_s_own_cd()
    {
        var (run, root, data) = Prepare("terminal-locked");
        try
        {
            var sub = Path.Combine(data, "sub");
            var own = Path.Combine(data, "own");
            Directory.CreateDirectory(sub);
            Directory.CreateDirectory(own);
            var process = run.Start("locked", string.Join(';',
                "size:1200x700",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "cmd:terminal.new {\"profile\":\"ps\"}",
                "until:terminals:1",
                $"until:terminal-folder:{data}",
                $"path:{sub}",
                "wait:800",
                "terminal:{enter}",
                "wait:2500",
                "terminal-state:stayed",
                $"terminal:Set-Location -LiteralPath '{own}'{{enter}}",
                $"until:terminal-folder:{own}",
                "terminal-state:own",
                "shot:done"));
            var logs = await run.FinishAsync("locked", process, "done");
            var state = States(logs);

            Assert.EndsWith(" ps [Left] Locked", Field(state["stayed"], "tabs").GetString(), StringComparison.Ordinal);
            Assert.True(Field(state["stayed"], "folder").GetString() == data, "the locked shell stayed" + Evidence(logs));
            Assert.Equal("in data", Field(state["stayed"], "caption").GetString());
            Assert.True(Field(state["own"], "folder").GetString() == own, "the user's own cd was reported" + Evidence(logs));
            Assert.Equal("in own", Field(state["own"], "caption").GetString());
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// The GUI context for <c>cab</c> (unit 4), with the real window, a real shell and the real command line: the
    /// shell asks the window's core what the panes show. With every row of the left pane marked, <c>cab selection</c>
    /// prints all three paths and <c>cab copy --selection --dest opposite_pane</c> puts them in the right pane's
    /// folder; after one row is selected, <c>cab selection</c> prints that row and <c>cab move --selection --dest
    /// &lt;path&gt;</c> takes it to a folder of its own. Each command's exit code is written next to its output. A shell
    /// of a CabinetOS terminal has the core's pipe in <c>CABINETOS_PIPE</c> and <c>cabinetos-cli</c> on its PATH, so
    /// nothing names either. The shell waits for the window's state to catch up with each selection (a loop on
    /// <c>cab selection</c> itself), and says it is done by changing folder, which its prompt hook reports.
    /// </summary>
    [Fact]
    public async Task Cab_in_a_shell_sees_the_window_s_selection_and_copies_and_moves_it()
    {
        var (run, root, left) = Prepare("terminal-cab");
        try
        {
            var gamma = Path.Combine(left, "gamma.txt");
            File.WriteAllText(gamma, "x");
            var (right, third, marker1, marker2) = (Path.Combine(root, "right"), Path.Combine(root, "third"), Path.Combine(root, "marker1"), Path.Combine(root, "marker2"));
            foreach (var folder in new[] { right, third, marker1, marker2 })
            {
                Directory.CreateDirectory(folder);
            }
            var (selection1, copyOutput, selection2, moveOutput, codes) = (Path.Combine(root, "selection1.txt"), Path.Combine(root, "copy.txt"), Path.Combine(root, "selection2.txt"), Path.Combine(root, "move.txt"), Path.Combine(root, "codes.txt"));
            var process = run.Start("cab", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{right}",
                "pane:0",
                $"path:{left}",
                "wait:500",
                "cmd:terminal.new {\"profile\":\"ps\"}",
                "until:terminals:1",
                $"until:terminal-folder:{left}",
                // All three rows of the left pane are marked; the shell goes on when cab says so.
                "selectall",
                "terminal:while ((cabinetos-cli selection | Measure-Object).Count -ne 3) { Start-Sleep -Milliseconds 50 }{enter}",
                $"terminal:cabinetos-cli selection | Out-File -Encoding utf8 -LiteralPath '{selection1}'{{enter}}",
                $"terminal:Add-Content -LiteralPath '{codes}' \"selection=$LASTEXITCODE\"{{enter}}",
                $"terminal:cabinetos-cli copy --selection --dest opposite_pane | Out-File -Encoding utf8 -LiteralPath '{copyOutput}'{{enter}}",
                $"terminal:Add-Content -LiteralPath '{codes}' \"copy=$LASTEXITCODE\"{{enter}}",
                $"terminal:Set-Location -LiteralPath '{marker1}'{{enter}}",
                $"until:terminal-folder:{marker1}",
                // One row selected: the selection is the row the cursor is on.
                "select:gamma.txt",
                $"terminal:while ((cabinetos-cli selection | Out-String).Trim() -ne '{gamma}') {{ Start-Sleep -Milliseconds 50 }}{{enter}}",
                $"terminal:cabinetos-cli selection | Out-File -Encoding utf8 -LiteralPath '{selection2}'{{enter}}",
                $"terminal:cabinetos-cli move --selection --dest '{third}' | Out-File -Encoding utf8 -LiteralPath '{moveOutput}'{{enter}}",
                $"terminal:Add-Content -LiteralPath '{codes}' \"move=$LASTEXITCODE\"{{enter}}",
                $"terminal:Set-Location -LiteralPath '{marker2}'{{enter}}",
                $"until:terminal-folder:{marker2}",
                "shot:done"));
            var logs = await run.FinishAsync("cab", process, "done");

            string[] Lines(string file) => File.Exists(file) ? [.. File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0)] : [$"(no file {Path.GetFileName(file)})"];
            string[] names = ["alpha.txt", "beta.txt", "gamma.txt"];
            Assert.True(Lines(codes).SequenceEqual(["selection=0", "copy=0", "move=0"]), $"the exit codes were {string.Join(", ", Lines(codes))}" + Evidence(logs));
            Assert.Equal(names.Select(n => Path.Combine(left, n)), Lines(selection1));
            var copied = string.Join('\n', Lines(copyOutput));
            Assert.Contains($"copying 3 selected items of the left pane to {right}", copied);
            Assert.Contains("completed", copied);
            Assert.All(names, n => Assert.True(File.Exists(Path.Combine(right, n)), $"{n} is in the right pane's folder"));

            Assert.Equal([gamma], Lines(selection2));
            Assert.Contains($"moving 1 selected item of the left pane to {third}", string.Join('\n', Lines(moveOutput)));
            Assert.True(File.Exists(Path.Combine(third, "gamma.txt")) && !File.Exists(gamma), "the move took gamma.txt to the folder the path names");
            Assert.True(File.Exists(Path.Combine(right, "gamma.txt")), "the copy of the first round stays");

            // The core started the two jobs and logged them: a copy of the three rows, a move of the one.
            var jobs = LogFiles.Core(Path.Combine(root, "logs-cab")).Where(l => Message(l) == "job queued").ToList();
            Assert.Equal(["Copy", "Move"], jobs.Select(l => Field(l, "kind").GetString()));
            Assert.Equal([3, 1], jobs.Select(l => Field(l, "sources").GetInt32()));
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// The window tells the core about the first 1,000 marked rows only, and how many there are (<c>marked_total</c>).
    /// With 1,100 rows marked, <c>cab selection</c> and <c>cab copy --selection</c> in the shell refuse, with exit
    /// code 1 and the numbers, and copy nothing: a copy that reported success on 1,000 of 1,100 files would be worse
    /// than none.
    /// </summary>
    [Fact]
    public async Task Cab_refuses_a_selection_the_window_cut_at_a_thousand_rows()
    {
        var (run, root, _) = Prepare("terminal-cab-cut");
        try
        {
            var (big, right, marker) = (Path.Combine(root, "big"), Path.Combine(root, "right"), Path.Combine(root, "marker"));
            foreach (var folder in new[] { big, right, marker })
            {
                Directory.CreateDirectory(folder);
            }
            for (var i = 0; i < 1100; i++)
            {
                File.WriteAllText(Path.Combine(big, $"file{i:D4}.txt"), "x");
            }
            var (selectionOutput, copyOutput, codes) = (Path.Combine(root, "selection.txt"), Path.Combine(root, "copy.txt"), Path.Combine(root, "codes.txt"));
            var process = run.Start("cab-cut", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{right}",
                "pane:0",
                $"path:{big}",
                "wait:500",
                "cmd:terminal.new {\"profile\":\"ps\"}",
                "until:terminals:1",
                $"until:terminal-folder:{big}",
                "selectall",
                // The shell goes on when cab has the count: the refusal names all 1,100 rows.
                "terminal:while ((cabinetos-cli selection 2>&1 | Out-String) -notmatch '1100 rows') { Start-Sleep -Milliseconds 50 }{enter}",
                $"terminal:cabinetos-cli selection 2>&1 | Out-File -Encoding utf8 -LiteralPath '{selectionOutput}'{{enter}}",
                $"terminal:Add-Content -LiteralPath '{codes}' \"selection=$LASTEXITCODE\"{{enter}}",
                $"terminal:cabinetos-cli copy --selection --dest opposite_pane 2>&1 | Out-File -Encoding utf8 -LiteralPath '{copyOutput}'{{enter}}",
                $"terminal:Add-Content -LiteralPath '{codes}' \"copy=$LASTEXITCODE\"{{enter}}",
                $"terminal:Set-Location -LiteralPath '{marker}'{{enter}}",
                $"until:terminal-folder:{marker}",
                "shot:done"));
            var logs = await run.FinishAsync("cab-cut", process, "done");

            string Text(string file) => File.Exists(file) ? string.Join(' ', File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0)) : $"(no file {Path.GetFileName(file)})";
            Assert.True(Text(codes) == "selection=1 copy=1", $"the exit codes were '{Text(codes)}'" + Evidence(logs));
            Assert.Contains("1100 rows are selected", Text(selectionOutput));
            Assert.Contains("only 1000", Text(selectionOutput));
            Assert.Contains("1100 rows are selected", Text(copyOutput));
            Assert.Empty(Directory.GetFileSystemEntries(right));
            Assert.Equal(1100, Directory.GetFiles(big).Length);
            Assert.DoesNotContain(LogFiles.Core(Path.Combine(root, "logs-cab-cut")), l => Message(l) == "job queued");
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// The split mirror (unit 3): Ctrl+\ in the terminal splits the dock under the two panes, the left session under
    /// the left pane and the right under the right, with the badges in the panes' colours; in a pane the same keys
    /// still go to Up to Root. Ctrl+Shift+W in a half with one tab leaves the hint there and the other half alone.
    /// The setting is saved, and a second window starts split. With one pane shown there is one half, as wide as the
    /// dock; the right half returns with the second pane.
    /// </summary>
    [Fact]
    public async Task Ctrl_backslash_splits_the_dock_under_the_panes_and_the_setting_survives_a_restart()
    {
        var (run, root, data) = Prepare("terminal-split");
        try
        {
            var first = run.Start("split", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                // In a pane the same keys are Up to Root: nothing splits.
                "key:ctrl+backslash",
                "wait:600",
                "terminal-state:pane-key",
                "key:ctrl+backquote",
                "until:terminals:1",
                "wait:1200",
                "pane:1",
                "wait:400",
                "key:ctrl+backquote",
                "until:terminals:2",
                "wait:1200",
                "terminal-state:one",
                // In the terminal (the right session has the keyboard) it splits, and writes the setting.
                "key:ctrl+backslash",
                "until:config",
                "wait:1000",
                "terminal-state:split",
                // The right half has one tab: it closes and shows the hint; the left half is left alone.
                "key:ctrl+shift+w",
                "wait:1500",
                "terminal-state:hint",
                "key:ctrl+backslash",
                "until:config",
                "wait:800",
                "terminal-state:unsplit",
                "key:ctrl+backslash",
                "until:config",
                "wait:800",
                "terminal-state:again",
                "shot:done"));
            var logs = await run.FinishAsync("split", first, "done");
            var state = States(logs);
            var opened = logs.Where(l => Message(l) == "terminal session opened").Select(l => Field(l, "session_id").GetUInt64()).ToList();
            Assert.True(opened.Count == 2, "two sessions, one per pane" + Evidence(logs));
            var (left, right) = (opened[0], opened[1]);

            Assert.True(!Flag(state["pane-key"], "split"), "Ctrl+\\ in a pane did not split" + Evidence(logs));
            Assert.Contains(logs, l => Message(l) == "command executed" && Field(l, "command").GetString() == "go.root" && Field(l, "trigger").GetString() == "key");
            Assert.False(Flag(state["one"], "split"));

            var split = state["split"];
            Assert.True(Flag(split, "split") && Flag(split, "split_setting"), "Ctrl+\\ in the terminal split the dock" + Evidence(logs));
            Assert.Equal("right", Field(split, "keyboard_half").GetString());
            Assert.Equal((left, right), (Field(split, "left_half_session").GetUInt64(), Field(split, "right_half_session").GetUInt64()));
            AssertHalvesUnderPanes(split, logs);
            // The badges: the left in the accent, the right the accent's hue turned by 150 degrees.
            var (leftBadge, rightBadge) = (Field(split, "left_badge").GetString()!, Field(split, "right_badge").GetString()!);
            Assert.NotEqual(leftBadge, rightBadge);
            Assert.Equal(TurnedHue(leftBadge), rightBadge);

            var hint = state["hint"];
            Assert.True(Flag(hint, "split") && Flag(hint, "right_half_hint") && !Flag(hint, "left_half_hint"), "the right half shows the hint, the left its session" + Evidence(logs));
            Assert.Equal(JsonValueKind.Null, Field(hint, "right_half_session").ValueKind);
            Assert.Equal(left, Field(hint, "left_half_session").GetUInt64());
            Assert.Equal(left.ToString(System.Globalization.CultureInfo.InvariantCulture), Field(hint, "left_half_tabs").GetString());
            Assert.Equal(1, logs.Count(l => Message(l) == "terminal tab closed"));

            Assert.False(Flag(state["unsplit"], "split"));
            Assert.False(Flag(state["unsplit"], "split_setting"));
            Assert.True(Flag(state["again"], "split"));
            var toggles = logs.Where(l => Message(l) == "command executed" && Field(l, "command").GetString() == "terminal.toggleSplit").ToList();
            Assert.Equal(3, toggles.Count);
            Assert.All(toggles, l => Assert.Equal("key", Field(l, "trigger").GetString()));
            var lines = logs.Where(l => Message(l) == "terminal split").ToList();
            Assert.Equal([true, false, true], lines.Select(l => Field(l, "split").GetBoolean()));

            // The setting was written: the file says so, and a second window starts split with the panes' halves.
            Assert.True(ReadSplitSetting(root), "terminal.split is in cabinetos.json");
            var second = run.Start("second", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "key:ctrl+backquote",
                "until:terminals:1",
                "wait:1200",
                "terminal-state:restart",
                // One pane shown: one half, the left's, as wide as the dock.
                "cmd:view.toggleDualPane",
                "wait:800",
                "terminal-state:single",
                // The second pane comes back: so does the right half, empty, with its hint.
                "cmd:view.toggleDualPane",
                "wait:800",
                "terminal-state:two",
                "shot:done"));
            var again = await run.FinishAsync("second", second, "done");
            var restarted = States(again);
            var restart = restarted["restart"];
            Assert.True(Flag(restart, "split"), "the second window started split" + Evidence(again));
            var session = Field(restart, "left_half_session").GetUInt64();
            Assert.Equal(session, Field(Assert.Single(again, l => Message(l) == "terminal session opened"), "session_id").GetUInt64());
            Assert.True(Flag(restart, "right_half_hint"), "the right pane has no session: the hint" + Evidence(again));
            AssertHalvesUnderPanes(restart, again);

            var single = restarted["single"];
            Assert.True(Flag(single, "split") && !Flag(single, "dual"), "one pane shown, still split" + Evidence(again));
            Assert.False(Has(single, "right_half_x"), "one half only");
            // The left pane is the whole row now, and so is its half.
            AssertHalvesUnderPanes(single, again, "left");
            Assert.True(Number(single, "left_half_width")!.Value > 900, "the one half is the whole dock" + Evidence(again));
            Assert.Equal(session, Field(single, "left_half_session").GetUInt64());

            var two = restarted["two"];
            Assert.True(Flag(two, "dual") && Flag(two, "right_half_hint"), "the right half came back with the second pane" + Evidence(again));
            AssertHalvesUnderPanes(two, again);
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// The keys in the split: Ctrl+Shift+T opens a tab in the half of the active pane, Alt+[ and Alt+] go round the
    /// tabs of the half that has the keyboard, Ctrl+` in the left pane gives the left half the keyboard, Ctrl+Shift+W
    /// closes the shown tab of that half, and the last tab of a half leaves its hint, with the other half untouched.
    /// </summary>
    [Fact]
    public async Task In_the_split_the_tab_keys_act_on_the_half_that_has_the_keyboard_and_the_active_pane_opens_its_own()
    {
        var (run, root, data) = Prepare("terminal-split-keys", split: true);
        try
        {
            var process = run.Start("splitkeys", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "key:ctrl+backquote",
                "until:terminals:1",
                "wait:1200",
                "pane:1",
                "wait:400",
                "key:ctrl+backquote",
                "until:terminals:2",
                "wait:1200",
                "terminal-state:two",
                // Ctrl+Shift+T from the right half: the active pane is the right one, so its half gets the tab.
                "key:ctrl+shift+t",
                "until:terminals:3",
                "wait:1200",
                "terminal-state:new-right",
                "key:alt+bracketleft",
                "wait:700",
                "terminal-state:previous",
                "key:alt+bracketright",
                "wait:700",
                "terminal-state:next",
                // Ctrl+` in the terminal hands the keyboard to the active (right) pane; in the left pane it goes to the left half.
                "key:ctrl+backquote",
                "wait:800",
                "pane:0",
                "wait:400",
                "key:ctrl+backquote",
                "wait:1200",
                "terminal-state:left-half",
                "key:ctrl+shift+t",
                "until:terminals:4",
                "wait:1200",
                "terminal-state:new-left",
                "key:ctrl+shift+w",
                "wait:1500",
                "terminal-state:closed-left",
                "key:ctrl+shift+w",
                "wait:1500",
                "terminal-state:hint-left",
                "shot:done"));
            var logs = await run.FinishAsync("splitkeys", process, "done");
            var state = States(logs);
            var opened = logs.Where(l => Message(l) == "terminal session opened").ToList();
            Assert.True(opened.Count == 4, "four sessions were opened" + Evidence(logs));
            var ids = opened.Select(l => Field(l, "session_id").GetUInt64()).ToList();
            Assert.Equal(["left", "right", "right", "left"], opened.Select(l => Field(l, "pane").GetString()));
            var (s1, s2, s3, s4) = (ids[0], ids[1], ids[2], ids[3]);
            string Id(ulong id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ulong? Session(string label, string half) => Field(state[label], $"{half}_half_session") is { ValueKind: JsonValueKind.Number } n ? n.GetUInt64() : null;
            string Tabs(string label, string half) => Field(state[label], $"{half}_half_tabs").GetString()!;

            Assert.True(Flag(state["two"], "split"), "the setting in the file split the dock from the start" + Evidence(logs));
            Assert.Equal((s1, s2), (Session("two", "left"), Session("two", "right")));
            Assert.Equal($"{Id(s2)},{Id(s3)}", Tabs("new-right", "right"));
            Assert.True(Session("new-right", "right") == s3 && Session("new-right", "left") == s1, "Ctrl+Shift+T opened in the active pane's half" + Evidence(logs));
            Assert.True(Session("previous", "right") == s2 && Session("previous", "left") == s1, "Alt+[ went round the right half's tabs only" + Evidence(logs));
            Assert.Equal(s3, Session("next", "right"));
            Assert.Equal("right", Field(state["next"], "keyboard_half").GetString());

            Assert.True(Field(state["left-half"], "keyboard_half").GetString() == "left" && Field(state["left-half"], "terminal_keyboard").GetBoolean(), "Ctrl+` in the left pane gave the left half the keyboard" + Evidence(logs));
            Assert.Equal(s1, Field(state["left-half"], "shown").GetUInt64());
            Assert.True(Session("new-left", "left") == s4 && Session("new-left", "right") == s3, "the new tab is the left half's" + Evidence(logs));
            Assert.True(Session("closed-left", "left") == s1 && Session("closed-left", "right") == s3, "Ctrl+Shift+W closed the left half's shown tab only" + Evidence(logs));
            var hint = state["hint-left"];
            Assert.True(Flag(hint, "left_half_hint") && !Flag(hint, "right_half_hint"), "the last tab of the left half left its hint" + Evidence(logs));
            Assert.True(Flag(hint, "split") && Flag(hint, "dock"), "the split and the dock stay" + Evidence(logs));
            Assert.Equal(s3, Session("hint-left", "right"));
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// The tabs across a restart (unit 5), whole: a first window opens a Linked PowerShell in the left pane, a locked one in
    /// the right pane that goes to a folder of its own, and a cmd in the right pane, and shows the right pane's PowerShell.
    /// The layout is written to <c>terminal.tabs</c>. The window and its core close. A second window starts with the same
    /// file, and its first Ctrl+` starts the three again as fresh shells, with their profile, folder, pane and mode, in the
    /// same order, with the same tab in front; the log says "terminal session restored" for each and "terminal restored" once.
    /// </summary>
    [Fact]
    public async Task After_a_restart_the_first_Ctrl_backquote_brings_the_tabs_back_as_they_were()
    {
        var (run, root, left) = Prepare("terminal-restore");
        try
        {
            var (right, own) = (Path.Combine(root, "right"), Path.Combine(root, "own"));
            Directory.CreateDirectory(right);
            Directory.CreateDirectory(own);
            var first = run.Start("first", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{right}",
                "pane:0",
                $"path:{left}",
                "wait:500",
                "cmd:terminal.new {\"profile\":\"ps\",\"pane\":0}",
                "until:terminals:1",
                $"until:terminal-folder:{left}",
                "cmd:terminal.setMode {\"mode\":\"linked\"}",
                "wait:1200",
                "cmd:terminal.new {\"profile\":\"ps\",\"pane\":1}",
                "until:terminals:2",
                $"until:terminal-folder:{right}",
                // The right session goes to a folder of its own: the saved folder is where its shell is now, not where it started.
                $"terminal:Set-Location -LiteralPath '{own}'{{enter}}",
                $"until:terminal-folder:{own}",
                "cmd:terminal.new {\"profile\":\"cmd\",\"pane\":1}",
                "until:terminals:3",
                "wait:800",
                // The right pane's own front tab is the PowerShell, the one the tab row shows.
                "cmd:terminal.previousTab",
                "wait:1500",
                "terminal-state:saved",
                "shot:done"));
            var firstLogs = await run.FinishAsync("first", first, "done");
            var before = States(firstLogs)["saved"];
            var ids = firstLogs.Where(l => Message(l) == "terminal session opened").Select(l => Field(l, "session_id").GetUInt64()).ToList();
            Assert.True(ids.Count == 3, "three sessions" + Evidence(firstLogs));
            Assert.Equal($"{ids[0]} ps [Left] Linked | *{ids[1]} ps [Right] Locked | {ids[2]} cmd [Right] Locked", Field(before, "tabs").GetString());
            Assert.Contains(firstLogs, l => Message(l) == "terminal layout saved");

            // The file has the layout, in the order of the tabs.
            var saved = ReadSavedTabs(root);
            Assert.Equal(
                [("ps", left, "left", "linked"), ("ps", own, "right", "locked"), ("cmd", right, "right", "locked")],
                saved.Items.Select(i => (i.Profile, i.Folder, TerminalBinding.PaneName(i.Pane), TerminalBinding.ModeName(i.Mode))));
            Assert.Equal((1, 0, 1), (saved.Front, saved.ShownLeft, saved.ShownRight));

            var second = run.Start("second", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{right}",
                "pane:0",
                $"path:{left}",
                "wait:500",
                "terminal-state:before",
                "key:ctrl+backquote",
                "until:terminal-restored",
                "until:terminals:3",
                $"until:terminal-folder:{own}",
                "wait:1500",
                "terminal-state:restored",
                "shot:done"));
            var logs = await run.FinishAsync("second", second, "done");
            var state = States(logs);

            Assert.True(Flag(state["before"], "restore_pending") && Field(state["before"], "tabs").GetString() == "", "nothing runs before the dock is shown");
            Assert.False(Flag(state["restored"], "restore_pending"));
            var decision = Assert.Single(logs, l => Message(l) == "terminal restore");
            Assert.Equal(("Restore", 3), (Field(decision, "action").GetString(), Field(decision, "saved").GetInt32()));

            var restored = logs.Where(l => Message(l) == "terminal session restored").ToList();
            Assert.Equal(
                [("ps", left, "left", "linked"), ("ps", own, "right", "locked"), ("cmd", right, "right", "locked")],
                restored.Select(l => (Field(l, "profile").GetString(), Field(l, "folder").GetString(), Field(l, "pane").GetString(), Field(l, "mode").GetString())));
            Assert.All(restored, l => Assert.Equal(0, Field(l, "fallbacks").GetInt32()));
            var summary = Assert.Single(logs, l => Message(l) == "terminal restored");
            Assert.Equal((3, 3, 0), (Field(summary, "count").GetInt32(), Field(summary, "saved").GetInt32(), Field(summary, "skipped").GetInt32()));
            // The sessions are fresh shells of the new core, started in the saved order; the same tab is in front.
            var now = restored.Select(l => Field(l, "session_id").GetUInt64()).ToList();
            Assert.Equal($"{now[0]} ps [Left] Linked | *{now[1]} ps [Right] Locked | {now[2]} cmd [Right] Locked", Field(state["restored"], "tabs").GetString());
            Assert.True(Field(state["restored"], "folder").GetString() == own, "the shell of the front tab started in the folder it was left in" + Evidence(logs));
            Assert.Equal(3, logs.Count(l => Message(l) == "terminal session opened"));
            Assert.True(Flag(state["restored"], "dock") && Flag(state["restored"], "terminal_keyboard"), "the first Ctrl+` showed the dock with the keyboard in the terminal" + Evidence(logs));
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// <c>terminal.restore</c> false: the saved sessions stay where they are, and the first Ctrl+` starts one session as it
    /// always did, in the mode <c>terminal.defaultMode</c> says (here the default profile is a linkable one, and the mode linked).
    /// </summary>
    [Fact]
    public async Task With_terminal_restore_off_nothing_comes_back_and_a_new_session_starts_in_the_default_mode()
    {
        const string tabs = """, "tabs": { "items": [ { "profile": "cmd", "pane": "left" }, { "profile": "ps", "pane": "right", "mode": "linked" } ], "front": 0, "shown": { "left": 0, "right": 1 } }""";
        var (run, root, data) = Prepare("terminal-no-restore", defaultProfile: "hooked", terminal: """, "restore": false, "defaultMode": "linked" """ + tabs);
        try
        {
            var process = run.Start("off", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "key:ctrl+backquote",
                "until:terminal-restored",
                "until:terminals:1",
                "wait:1200",
                "terminal-state:after",
                "shot:done"));
            var logs = await run.FinishAsync("off", process, "done");
            var state = States(logs);

            var decision = Assert.Single(logs, l => Message(l) == "terminal restore");
            Assert.Equal("Nothing", Field(decision, "action").GetString());
            Assert.Contains("terminal.restore is false", Field(decision, "reason").GetString());
            Assert.DoesNotContain(logs, l => Message(l) is "terminal session restored" or "terminal restored");
            var opened = Assert.Single(logs, l => Message(l) == "terminal session opened");
            var id = Field(opened, "session_id").GetUInt64();
            // The one session is the summoned one: the default profile, the left pane's, in the default mode.
            Assert.Equal($"*{id} hooked [Left] Linked", Field(state["after"], "tabs").GetString());
            Assert.Equal("linked", Field(opened, "mode").GetString());
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    /// <summary>
    /// What cannot come back as it was falls back, with a log line each: a profile that no longer exists becomes the default
    /// one, a folder that no longer exists the user's home folder, a link the profile can no longer take a locked session; a
    /// session whose program is not there is skipped, and the rest still come back.
    /// </summary>
    [Fact]
    public async Task A_profile_a_folder_or_a_link_that_is_gone_falls_back_and_a_session_that_cannot_start_is_skipped()
    {
        var gone = Path.Combine(Path.GetTempPath(), "cabinetos-restore-vanished-" + Guid.NewGuid().ToString("N"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var (run, root, data) = Prepare("terminal-fallbacks");
        try
        {
            // Five saved sessions: a profile that is gone; a folder that is gone; a cmd saved linked (cmd cannot be); one that is
            // as it was; one whose program is not there. The file is written over the one Prepare made.
            var config = $$"""
                { "version": 1, "ui": { "dualPane": true },
                  "terminal": { "defaultProfile": "cmd", "profiles": [
                    { "name": "cmd", "command": "cmd.exe" },
                    { "name": "hooked", "command": "cmd.exe", "linkable": true },
                    { "name": "missing", "command": "cabinetos-no-such-program.exe" } ],
                    "tabs": { "items": [
                      { "profile": "fish", "folder": {{Json(data)}}, "pane": "left" },
                      { "profile": "cmd", "folder": {{Json(gone)}}, "pane": "right" },
                      { "profile": "cmd", "folder": {{Json(data)}}, "pane": "right", "mode": "linked" },
                      { "profile": "hooked", "folder": {{Json(data)}}, "pane": "left", "mode": "linked" },
                      { "profile": "missing", "folder": {{Json(data)}}, "pane": "left" } ],
                      "front": 1, "shown": { "left": 0, "right": 1 } } } }
                """;
            File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), config);
            var process = run.Start("fallbacks", string.Join(';',
                "size:1200x700",
                "pane:1",
                $"path:{data}",
                "pane:0",
                $"path:{data}",
                "wait:500",
                "key:ctrl+backquote",
                "until:terminal-restored",
                "until:terminals:4",
                "wait:1200",
                "terminal-state:restored",
                "shot:done"));
            var logs = await run.FinishAsync("fallbacks", process, "done");
            var state = States(logs);

            var restored = logs.Where(l => Message(l) == "terminal session restored").ToList();
            Assert.Equal(
                [("cmd", data, "left", "locked"), ("cmd", home, "right", "locked"), ("cmd", data, "right", "locked"), ("hooked", data, "left", "linked")],
                restored.Select(l => (Field(l, "profile").GetString(), Field(l, "folder").GetString(), Field(l, "pane").GetString(), Field(l, "mode").GetString())));
            var fallbacks = logs.Where(l => Message(l) == "terminal restore fell back")
                .Select(l => (Field(l, "index").GetInt32(), Field(l, "what").GetString(), Field(l, "saved").GetString(), Field(l, "used").GetString())).ToList();
            Assert.Equal(
                [(0, "profile", "fish", "cmd"), (1, "folder", gone, home), (2, "mode", "linked", "locked")],
                fallbacks.OrderBy(f => f.Item1));
            // The one whose program is not there was asked for twice (its folder, then home), and skipped with a warning.
            var skipped = Assert.Single(logs, l => Message(l) == "terminal session not restored");
            Assert.Equal(("missing", 4, "spawn_failed"), (Field(skipped, "profile").GetString(), Field(skipped, "index").GetInt32(), Field(skipped, "code").GetString()));
            var summary = Assert.Single(logs, l => Message(l) == "terminal restored");
            Assert.Equal((4, 5, 1), (Field(summary, "count").GetInt32(), Field(summary, "saved").GetInt32(), Field(summary, "skipped").GetInt32()));
            // The tab in front is the saved one (the second), though the fifth session never started.
            var ids = restored.Select(l => Field(l, "session_id").GetUInt64()).ToList();
            Assert.Equal($"{ids[0]} cmd [Left] Locked | *{ids[1]} cmd [Right] Locked | {ids[2]} cmd [Right] Locked | {ids[3]} hooked [Left] Linked", Field(state["restored"], "tabs").GetString());
        }
        finally
        {
            run.Stop();
            Repo.RemoveTempFolder(root);
        }
    }

    // A path as the JSON of a configuration file writes it.
    private static string Json(string text) => JsonSerializer.Serialize(text);

    // terminal.tabs as the window wrote it into the file.
    private static TerminalLayout ReadSavedTabs(string root)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config", "cabinetos.json")));
        return TerminalLayout.FromConfig(config.RootElement);
    }

    // The halves lie under the panes: each half's edges are its pane's, within 2 px.
    private static void AssertHalvesUnderPanes(string state, List<string> logs, params string[] sides)
    {
        foreach (var side in sides.Length > 0 ? sides : ["left", "right"])
        {
            var (paneX, paneWidth) = (Number(state, $"{side}_pane_x")!.Value, Number(state, $"{side}_pane_width")!.Value);
            var (halfX, halfWidth) = (Number(state, $"{side}_half_x")!.Value, Number(state, $"{side}_half_width")!.Value);
            Assert.True(Math.Abs(halfX - paneX) <= 2, $"the {side} half starts at {halfX}, its pane at {paneX}" + Evidence(logs));
            Assert.True(Math.Abs((halfX + halfWidth) - (paneX + paneWidth)) <= 2, $"the {side} half ends at {halfX + halfWidth}, its pane at {paneX + paneWidth}" + Evidence(logs));
        }
    }

    // The accent's hue turned by 150 degrees, as the window draws the right badge (a WinUI colour, #AARRGGBB).
    private static string TurnedHue(string accent)
    {
        var value = Convert.ToUInt32(accent.TrimStart('#'), 16);
        var color = new CabinetOS.Core.Themes.Argb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return color.RotateHue(CabinetOS.Core.Themes.ThemeMapper.RightBadgeHueShift).ToString();
    }

    private static bool ReadSplitSetting(string root)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config", "cabinetos.json")));
        return config.RootElement.GetProperty("terminal").GetProperty("split").GetBoolean();
    }

    private static bool Flag(string line, string name) => Field(line, name).ValueKind == JsonValueKind.True;

    private static bool Has(string line, string name)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.GetProperty("fields").TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;
    }

    private static double? Number(string line, string name)
    {
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.GetProperty("fields").TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    }

    // The lines that say what the keys and the terminal did, for the message of a failed assertion.
    private static string Evidence(List<string> logs) => "\n" + string.Join('\n', logs
        .Where(l => Message(l) is "key sent" or "key sent to a page" or "command executed" or "a key the page did not get" or "keyboard owner"
            or "terminal summoned" or "terminal state" or "terminal tab shown" or "terminal session opened" or "a page has the keyboard"
            or "terminal mode changed" or "terminal folder changed" or "notice shown" or "terminal split" or "terminal half focused"
            or "terminal tab closed" or "terminal restore" or "terminal session restored" or "terminal restored" or "terminal restore fell back"
            or "terminal restore tries again" or "terminal session not restored" or "terminal layout saved")
        .Select(l => l.Length > 360 ? l[..360] : l));

    private static Dictionary<string, string> States(List<string> logs) =>
        logs.Where(l => Message(l) == "terminal state").ToDictionary(l => Field(l, "label").GetString()!);

    // The "keyboard owner" line the focus:<label> step wrote: XAML's focused element and the window that gets the keys.
    private static string Owner(List<string> logs, string label) =>
        logs.Last(l => Message(l) == "keyboard owner" && Field(l, "moment").GetString() == label);

    // Whether a browser's input window has the keys. The window tells the terminal's page by where that input window
    // sits on screen, which an end-to-end run's window (never in front) does not always give: then it says "a page".
    // These tests open no other page, so either answer is the terminal.
    private static bool PageHasKeys(List<string> logs, string label) =>
        Field(Owner(logs, label), "keys_to").GetString() is "terminal" or "a page";

    private sealed class Run(string root, string exe, string core)
    {
        private readonly List<Process> _started = [];

        public Process Start(string name, string steps)
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
            var process = Process.Start(start)!;
            _started.Add(process);
            return process;
        }

        // Waits for the last snapshot, closes the window the way a user does and returns the UI's log lines.
        public async Task<List<string>> FinishAsync(string name, Process process, string lastShot)
        {
            var shot = Path.Combine(root, "shots-" + name, lastShot + ".png");
            try
            {
                await WaitForAsync(() => File.Exists(shot), $"the {name} window's last snapshot", TimeSpan.FromSeconds(120));
            }
            catch (Xunit.Sdk.XunitException error)
            {
                // A step that never ends: the last lines of the window's log say which.
                var tail = LogFiles.Ui(Path.Combine(root, "logs-" + name)).TakeLast(20).Select(l => l.Length > 360 ? l[..360] : l);
                throw new Xunit.Sdk.XunitException(error.Message + "\nthe window's last log lines:\n" + string.Join('\n', tail));
            }
            process.CloseMainWindow();
            Assert.True(process.WaitForExit(20_000), $"the {name} window did not close");
            var logs = LogFiles.Ui(Path.Combine(root, "logs-" + name));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "logs-" + name), "crash-*.json"));
            Assert.DoesNotContain(logs, l => Level(l) == "ERROR");
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

    // A window's setting up: dual panes on a data folder, and three terminal profiles: cmd, which is not linkable, a
    // cmd that says it is (cmd gets no prompt hook, so its mode changes nothing in the shell), and Windows PowerShell,
    // which gets the prompt hook. `terminal` is more members of the terminal section (JSON, each after a comma, such as
    // `"restore": false`).
    private static (Run Run, string Root, string Data) Prepare(string purpose, string defaultProfile = "cmd", bool split = false, string terminal = "")
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
        foreach (var name in new[] { "alpha.txt", "beta.txt" })
        {
            File.WriteAllText(Path.Combine(data, name), "x");
        }
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"), $$"""
            { "version": 1, "ui": { "dualPane": true },
              "terminal": { "defaultProfile": "{{defaultProfile}}", "split": {{(split ? "true" : "false")}}{{terminal}}, "profiles": [
                { "name": "cmd", "command": "cmd.exe" },
                { "name": "hooked", "command": "cmd.exe", "linkable": true },
                { "name": "ps", "command": "powershell.exe", "args": ["-NoLogo", "-NoProfile"] } ] } }
            """);
        return (new Run(root, exe, core), root, data);
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
