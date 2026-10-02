using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Settings;
using CabinetOS.Core.Terminal;

namespace CabinetOS.Tests;

/// <summary>The terminal pane's parts that run without a window: the byte pump, the page protocol, summoning, the tabs and the header.</summary>
public class TerminalTests
{
    [Fact]
    public void Output_is_coalesced_into_one_message_per_flush_and_only_the_first_add_asks_for_one()
    {
        long now = 1000;
        var output = new OutputCoalescer(() => now);

        Assert.True(output.Add("hel"u8));
        Assert.False(output.Add("lo "u8));
        Assert.False(output.Add("wörld"u8));

        var chunks = output.Flush();
        Assert.Equal("hello wörld", Encoding.UTF8.GetString(Convert.FromBase64String(Assert.Single(chunks))));
        Assert.False(output.HasPending);
        Assert.True(output.Add("again"u8));
    }

    [Fact]
    public void Flushes_are_at_least_16_ms_apart_so_the_page_gets_at_most_60_a_second()
    {
        long now = 1000;
        var output = new OutputCoalescer(() => now);
        Assert.Equal(0, output.MillisecondsUntilDue());
        output.Flush();

        now += 5;
        Assert.Equal(11, output.MillisecondsUntilDue());
        now += 11;
        Assert.Equal(0, output.MillisecondsUntilDue());
    }

    [Fact]
    public void A_big_burst_is_split_into_chunks_that_join_back_to_the_same_bytes()
    {
        var output = new OutputCoalescer(() => 0);
        var bytes = new byte[(OutputCoalescer.MaxChunkBytes * 2) + 1000];
        new Random(7).NextBytes(bytes);
        output.Add(bytes.AsSpan(0, 1000));
        output.Add(bytes.AsSpan(1000));

        var chunks = output.Flush();

        Assert.Equal(3, chunks.Count);
        Assert.Equal(bytes, chunks.SelectMany(Convert.FromBase64String).ToArray());
    }

    [Fact]
    public async Task The_pipe_pumps_output_in_and_keys_out_and_says_when_it_ends()
    {
        var name = "cabinetos-uitest-term-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accept = server.WaitForConnectionAsync();
        var output = new OutputCoalescer(() => Environment.TickCount64);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipe = await TerminalPipe.ConnectAsync($@"\\.\pipe\{name}", output, TimeSpan.FromSeconds(5));
        pipe.OutputWaiting += () => waiting.TrySetResult();
        pipe.Ended += () => ended.TrySetResult();
        await accept.WaitAsync(TimeSpan.FromSeconds(5));

        await server.WriteAsync("PS C:\\> "u8.ToArray()).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("PS C:\\> ", Encoding.UTF8.GetString(output.Flush().SelectMany(Convert.FromBase64String).ToArray()));

        // The pipe has no buffer: a write completes when the other end reads, so the read starts first.
        var typed = new byte[4];
        var read = server.ReadExactlyAsync(typed).AsTask();
        await pipe.WriteAsync("dir\r"u8.ToArray()).WaitAsync(TimeSpan.FromSeconds(5));
        await read.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("dir\r", Encoding.UTF8.GetString(typed));

        server.Disconnect();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pipe.IsOpen);
    }

    private static SummonState State(bool dock = true, bool keyboard = false, int pane = 0, int? shown = 0, ulong? paneSession = 3, bool cameBack = false) =>
        new(dock, keyboard, pane, shown, paneSession, cameBack);

    [Fact]
    public void Ctrl_backquote_in_a_pane_with_the_dock_hidden_shows_the_pane_s_session_or_a_new_one()
    {
        // The dock hidden: the pane's most recent session comes back with the keyboard.
        Assert.Equal(new Summon(SummonAction.ShowSession, 3), TerminalSummoning.Decide(State(dock: false, shown: 1, paneSession: 3)));
        // A pane with no session gets a new one of the default profile, in its own folder.
        Assert.Equal(new Summon(SummonAction.OpenNew), TerminalSummoning.Decide(State(dock: false, shown: null, paneSession: null)));
        Assert.Equal(new Summon(SummonAction.OpenNew), TerminalSummoning.Decide(State(dock: false, pane: 1, shown: 0, paneSession: null)));
    }

    [Fact]
    public void Ctrl_backquote_in_the_terminal_hands_the_keyboard_back_to_the_pane()
    {
        Assert.Equal(new Summon(SummonAction.HandBackToPane), TerminalSummoning.Decide(State(keyboard: true)));
        // Whose tab is shown does not matter: the keyboard was in the terminal.
        Assert.Equal(new Summon(SummonAction.HandBackToPane), TerminalSummoning.Decide(State(keyboard: true, pane: 1, shown: 0, paneSession: null)));
    }

    [Fact]
    public void Ctrl_backquote_in_a_pane_whose_session_is_shown_gives_it_the_keyboard_and_the_second_one_hides()
    {
        // The terminal lost the keyboard some other way (a click in the pane): Ctrl+` gives it back to the terminal.
        Assert.Equal(new Summon(SummonAction.FocusShown), TerminalSummoning.Decide(State(shown: 0, cameBack: false)));
        // Ctrl+` just brought the keyboard from the terminal to this pane: the next one hides the dock, as before.
        Assert.Equal(new Summon(SummonAction.Hide), TerminalSummoning.Decide(State(shown: 0, cameBack: true)));
    }

    [Fact]
    public void Ctrl_backquote_in_the_other_pane_switches_the_shown_session_and_never_hides()
    {
        // The left pane's session is shown; Ctrl+` in the right pane shows the right pane's own.
        Assert.Equal(new Summon(SummonAction.ShowSession, 7), TerminalSummoning.Decide(State(pane: 1, shown: 0, paneSession: 7)));
        Assert.Equal(new Summon(SummonAction.OpenNew), TerminalSummoning.Decide(State(pane: 1, shown: 0, paneSession: null)));
        // Even right after a hand-back to that pane: the shown tab is not its own, so nothing hides.
        Assert.Equal(new Summon(SummonAction.ShowSession, 7), TerminalSummoning.Decide(State(pane: 1, shown: 0, paneSession: 7, cameBack: true)));
    }

    [Fact]
    public void The_show_commands_never_hide_and_never_hand_the_keyboard_back()
    {
        Assert.Equal(new Summon(SummonAction.FocusShown), TerminalSummoning.DecideShow(State(keyboard: true)));
        Assert.Equal(new Summon(SummonAction.FocusShown), TerminalSummoning.DecideShow(State(cameBack: true)));
        Assert.Equal(new Summon(SummonAction.ShowSession, 3), TerminalSummoning.DecideShow(State(dock: false)));
        Assert.Equal(new Summon(SummonAction.ShowSession, 9), TerminalSummoning.DecideShow(State(pane: 1, shown: 0, paneSession: 9)));
    }

    [Fact]
    public void A_pane_s_session_is_its_running_tab_shown_last()
    {
        TabFacts[] tabs =
        [
            new(1, 0, true, 5),
            new(2, 1, true, 6),
            new(3, 0, true, 2),
            new(4, 0, false, 9),
            new(5, 1, true, 0),
        ];
        Assert.Equal(1UL, TerminalTabs.MostRecent(tabs, 0));
        Assert.Equal(2UL, TerminalTabs.MostRecent(tabs, 1));
        // An ended shell does not count, and a pane without tabs has none.
        Assert.Null(TerminalTabs.MostRecent([new TabFacts(4, 0, false, 9)], 0));
        Assert.Null(TerminalTabs.MostRecent(tabs, 1 + 1));
        // Never shown: the newest wins.
        Assert.Equal(6UL, TerminalTabs.MostRecent([new TabFacts(5, 1, true, 0), new TabFacts(6, 1, true, 0)], 1));
    }

    [Fact]
    public void Alt_brackets_cycle_the_tabs_round_the_ends()
    {
        ulong[] sessions = [3, 5, 8];
        Assert.Equal(8UL, TerminalTabs.Cycle(sessions, 5, 1));
        Assert.Equal(3UL, TerminalTabs.Cycle(sessions, 8, 1));
        Assert.Equal(8UL, TerminalTabs.Cycle(sessions, 3, -1));
        Assert.Equal(3UL, TerminalTabs.Cycle(sessions, 5, -1));
        // No shown tab: the first for next, the last for previous.
        Assert.Equal(3UL, TerminalTabs.Cycle(sessions, null, 1));
        Assert.Equal(8UL, TerminalTabs.Cycle(sessions, null, -1));
        Assert.Equal(5UL, TerminalTabs.Cycle([5], 5, 1));
        Assert.Null(TerminalTabs.Cycle([], null, 1));
    }

    [Fact]
    public void A_tab_shows_its_pane_and_its_mode_as_a_toggle()
    {
        var left = TerminalHeader.Tab("pwsh", 0, TerminalMode.Locked, linkable: true);
        Assert.Equal(("pwsh", "[Left]", "Locked", true, TerminalMode.Linked), (left.Title, left.Badge, left.ModeText, left.ModeToggles, left.NextMode));
        Assert.Equal("Locked, pwsh on the left pane", left.ModeName);
        Assert.Contains("link it to the left pane", left.ModeTip, StringComparison.Ordinal);

        var right = TerminalHeader.Tab("wsl", 1, TerminalMode.Linked, linkable: true);
        Assert.Equal(("[Right]", "Linked", true, TerminalMode.Locked), (right.Badge, right.ModeText, right.ModeToggles, right.NextMode));
        Assert.Equal("Linked to the right pane. Click to lock it.", right.ModeTip);
        Assert.Equal("Linked, wsl on the right pane", right.ModeName);
    }

    [Fact]
    public void A_profile_that_cannot_be_linked_shows_locked_without_a_toggle_and_says_why()
    {
        var claude = TerminalHeader.Tab("claude", 1, TerminalMode.Locked, linkable: false);
        Assert.Equal(("Locked", false, "[Right]"), (claude.ModeText, claude.ModeToggles, claude.Badge));
        Assert.Equal("Locked: claude cannot follow a pane, because no prompt hook can be added to it.", claude.ModeTip);
    }

    [Fact]
    public void The_caption_says_where_the_shell_started_until_its_hook_reports_a_folder()
    {
        static CaptionLook Caption(string? profile, bool running, uint? code, string? start, string? folder) =>
            TerminalCaption.Decide(new CaptionFacts(profile, running, code, start, folder));

        Assert.Equal(new CaptionLook("started in work", @"E:\work\"), Caption("pwsh", true, null, @"E:\work\", null));
        // The first report replaces it: a linked shell that followed its pane, or a cd of the user's own.
        Assert.Equal(new CaptionLook("in Звіт 'проєкт'", @"D:\Звіт 'проєкт'"), Caption("pwsh", true, null, @"E:\work", @"D:\Звіт 'проєкт'"));
        Assert.Equal(new CaptionLook("in C:", @"C:\"), Caption("pwsh", true, null, @"E:\work", @"C:\"));
        Assert.Equal(new CaptionLook("in me", @"\\wsl.localhost\Ubuntu\home\me"),
            Caption("wsl", true, null, @"E:\work", @"\\wsl.localhost\Ubuntu\home\me"));
        // How the shell ended wins over any folder.
        Assert.Equal(new CaptionLook("pwsh exited with code 3", null), Caption("pwsh", false, 3, @"E:\work", @"D:\x"));
        Assert.Equal(new CaptionLook("", null), Caption(null, false, null, null, null));
        Assert.Equal(new CaptionLook("", null), Caption("cmd", true, null, null, null));
        Assert.Equal(new CaptionLook("started in x", @"C:\x"), Caption("cmd", true, null, @"C:\x", ""));
        // The old folder sync's texts are gone.
        Assert.DoesNotContain("synced", Caption("pwsh", true, null, @"C:\x", null).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reported_folder_matches_without_case_and_trailing_backslash()
    {
        Assert.True(TerminalCaption.IsFolder(@"E:\Work\Docs", @"e:\work\docs\"));
        Assert.True(TerminalCaption.IsFolder(@"C:\", @"C:\"));
        Assert.False(TerminalCaption.IsFolder(@"E:\work", @"E:\work\docs"));
        Assert.False(TerminalCaption.IsFolder(null, @"E:\work"));
    }

    [Fact]
    public void The_tabs_read_as_one_line_for_the_logs()
    {
        Assert.Equal("3 pwsh [Left] Locked | *4 cmd [Right] Linked",
            TerminalHeader.Describe([(3UL, "pwsh", 0, TerminalMode.Locked, false), (4UL, "cmd", 1, TerminalMode.Linked, true)]));
        Assert.Equal("", TerminalHeader.Describe([]));
    }

    [Theory]
    [InlineData(0, "left")]
    [InlineData(1, "right")]
    public void Panes_have_their_wire_names(int pane, string name)
    {
        Assert.Equal(name, TerminalBinding.PaneName(pane));
        Assert.Equal(pane, TerminalBinding.PaneIndex(name));
    }

    [Fact]
    public void Modes_have_their_wire_names_and_anything_else_is_no_mode()
    {
        Assert.Equal("locked", TerminalBinding.ModeName(TerminalMode.Locked));
        Assert.Equal("linked", TerminalBinding.ModeName(TerminalMode.Linked));
        Assert.Equal(TerminalMode.Linked, TerminalBinding.ParseMode("linked"));
        Assert.Null(TerminalBinding.ParseMode("Linked"));
        Assert.Null(TerminalBinding.ParseMode(null));
    }


    [Fact]
    public void The_debouncer_hands_out_the_last_value_once_the_user_stopped()
    {
        long now = 0;
        var debouncer = new Debouncer<string>(() => now, 300);
        Assert.Equal(-1, debouncer.MillisecondsUntilDue());

        debouncer.Set(@"C:\a");
        now = 200;
        debouncer.Set(@"C:\b");
        now = 450;
        Assert.False(debouncer.TryTake(out _));
        Assert.Equal(50, debouncer.MillisecondsUntilDue());
        now = 500;
        Assert.True(debouncer.TryTake(out var folder));
        Assert.Equal(@"C:\b", folder);
        Assert.False(debouncer.HasPending);
    }

    [Theory]
    [InlineData("""{"type":"ready"}""", "ready")]
    [InlineData("""{"type":"input","session":3,"data":"ls\r"}""", "input")]
    [InlineData("""{"type":"binary","session":3,"data":"G1tN"}""", "binary")]
    [InlineData("""{"type":"resize","session":3,"cols":120,"rows":30}""", "resize")]
    [InlineData("""{"type":"buffer","session":3,"alternate":true}""", "buffer")]
    [InlineData("""{"type":"key","keys":"ctrl+backquote"}""", "key")]
    [InlineData("""{"type":"paste","session":3}""", "paste")]
    public void The_page_messages_the_window_takes(string json, string type) =>
        Assert.Equal(type, TerminalPageMessages.Parse(json)?.Type);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""["input"]""")]
    [InlineData("""{"type":"input","data":"no session"}""")]
    [InlineData("""{"type":"resize","session":3,"cols":0,"rows":30}""")]
    [InlineData("""{"type":"resize","session":3,"cols":40000,"rows":30}""")]
    [InlineData("""{"type":"launch","session":3}""")]
    [InlineData("""{"type":"paste"}""")]
    public void Malformed_or_unknown_page_messages_are_dropped(string json) =>
        Assert.Null(TerminalPageMessages.Parse(json));

    [Fact]
    public void Keys_reach_the_shell_as_bytes_and_output_reaches_the_page_as_base64()
    {
        Assert.Equal("ls\r"u8.ToArray(), TerminalPageMessages.Bytes(TerminalPageMessages.Parse("""{"type":"input","session":3,"data":"ls\r"}""")!));
        Assert.Equal(new byte[] { 0x1B, 0x5B, 0x4D }, TerminalPageMessages.Bytes(TerminalPageMessages.Parse("""{"type":"binary","session":3,"data":"G1tN"}""")!));

        using var output = JsonDocument.Parse(TerminalPageMessages.Output(3, "aGk="));
        Assert.Equal(("output", 3UL, "aGk="), (output.RootElement.GetProperty("type").GetString(), output.RootElement.GetProperty("session").GetUInt64(), output.RootElement.GetProperty("data").GetString()));
        using var keys = JsonDocument.Parse(TerminalPageMessages.PassKeys(["ctrl+shift+p", "ctrl+backquote"]));
        Assert.Equal(2, keys.RootElement.GetProperty("keys").GetArrayLength());
    }

    [Fact]
    public void Pasted_text_reaches_the_page_whole_for_one_session()
    {
        // The text goes as it is: quotes, a line break and a tab must arrive unchanged for xterm.js to paste.
        const string text = "echo \"a b\"\r\n\tcd 'D:\\x y' — ґ";
        using var paste = JsonDocument.Parse(TerminalPageMessages.Paste(3, text));
        Assert.Equal(("paste", 3UL, text), (paste.RootElement.GetProperty("type").GetString(), paste.RootElement.GetProperty("session").GetUInt64(), paste.RootElement.GetProperty("text").GetString()));
    }

    [Fact]
    public void The_page_takes_the_theme_s_line_height_and_padding_and_the_default_look_keeps_its_own()
    {
        // The page's own look (terminal.js, terminal.css) is the default theme's metrics.
        Assert.Equal(new TerminalSpacing(1.25, 8, 4, 4, 12), TerminalSpacing.From(Core.Themes.MetricsMapper.Default));

        var compact = Core.Themes.MetricsMapper.Map(new Dictionary<string, double>
        {
            ["terminalLineHeight"] = 1.45,
            ["terminalPaddingY"] = 6,
            ["terminalPaddingX"] = 8,
        });
        var spacing = TerminalSpacing.From(compact);
        Assert.Equal(new TerminalSpacing(1.133, 4.8, 2.7, 2.4, 8), spacing);

        using var theme = JsonDocument.Parse(TerminalPageMessages.Theme("#00000000", "#CCCCCC", "#FFFFFF", "#60CDFF4D", "Cascadia Code", 12, null, spacing));
        Assert.Equal(1.133, theme.RootElement.GetProperty("lineHeight").GetDouble());
        Assert.Equal([4.8, 2.7, 2.4, 8], theme.RootElement.GetProperty("padding").EnumerateArray().Select(e => e.GetDouble()));
        // Without metrics the message is as before: the page keeps its own.
        using var plain = JsonDocument.Parse(TerminalPageMessages.Theme("#00000000", "#CCCCCC", "#FFFFFF", "#60CDFF4D", "Cascadia Code", 12));
        Assert.False(plain.RootElement.TryGetProperty("lineHeight", out _));
    }

    [Fact]
    public void The_profiles_come_from_the_config_with_the_documented_defaults()
    {
        using var config = JsonDocument.Parse("""{"terminal":{"defaultProfile":"cmd","profiles":[{"name":"pwsh"},{"name":"cmd"},{"command":"x"}]}}""");
        var profiles = TerminalProfiles.FromConfig(config.RootElement);
        Assert.Equal("cmd", profiles.DefaultProfile);
        Assert.Equal(["pwsh", "cmd"], profiles.Names);
        using var empty = JsonDocument.Parse("{}");
        Assert.Same(TerminalProfiles.Defaults, TerminalProfiles.FromConfig(empty.RootElement));
        // The defaults mirror the core's: the fourth profile is Claude Code.
        Assert.Equal(["pwsh", "cmd", "wsl", "claude"], TerminalProfiles.Defaults.Names);
    }

    [Fact]
    public void A_config_written_before_unit_1_still_gives_the_profile_names()
    {
        // followsPane and linkable are the core's business; the window reads only the names.
        using var config = JsonDocument.Parse("""
            {"terminal":{"defaultProfile":"pwsh","profiles":[
              {"name":"pwsh","followsPane":true},
              {"name":"claude","followsPane":false,"linkable":false}
            ]}}
            """);
        var profiles = TerminalProfiles.FromConfig(config.RootElement);
        Assert.Equal(["pwsh", "claude"], profiles.Names);
        Assert.Equal("pwsh", profiles.DefaultProfile);
    }

    [Fact]
    public void In_the_terminal_only_the_ways_out_and_terminal_bindings_leave_the_shell()
    {
        var keymap = Keymap.From(new KeymapData(1000,
        [
            new KeymapBinding("ctrl+shift+p", "palette.show", null),
            new KeymapBinding("escape", "overlay.close", null),
            new KeymapBinding("ctrl+k ctrl+s", "keys.open", null),
            new KeymapBinding("ctrl+backquote", "view.toggleTerminal", null),
            new KeymapBinding("ctrl+b", "view.toggleSidebar", null),
            new KeymapBinding("f5", "file.copyToOtherPane", KeyContexts.FilesView),
            new KeymapBinding("ctrl+shift+t", "terminal.new", KeyContexts.TerminalFocus),
            new KeymapBinding("ctrl+k ctrl+t", "terminal.clear", KeyContexts.TerminalFocus),
            // A terminal binding on a way-out key wins there: it is the more specific one.
            new KeymapBinding("ctrl+backquote", "test.terminalOnly", KeyContexts.TerminalFocus),
        ],
        ["palette.show", "overlay.close", "keys.open"]));

        var keys = TerminalKeys.PassKeys(keymap);

        Assert.Equal(new Dictionary<string, string>
        {
            ["ctrl+shift+p"] = "palette.show",
            ["ctrl+backquote"] = "test.terminalOnly",
            ["ctrl+shift+t"] = "terminal.new",
        }, keys);
    }

    [Fact]
    public void A_tool_page_in_the_sidebar_also_hands_back_the_keys_that_change_the_sidebar()
    {
        var keymap = Keymap.From(new KeymapData(1000,
        [
            new KeymapBinding("ctrl+shift+p", "palette.show", null),
            new KeymapBinding("ctrl+shift+e", "view.showExplorer", null),
            new KeymapBinding("ctrl+shift+f", "view.showSearch", null),
            new KeymapBinding("ctrl+alt+b", "view.toggleSidebar", null),
            new KeymapBinding("f5", "file.copyToOtherPane", KeyContexts.FilesView),
            new KeymapBinding("ctrl+k ctrl+s", "view.showSearch", null),
        ],
        ["palette.show"]));

        // A tool page in a pane keeps to the ways out, as before.
        Assert.Equal(new Dictionary<string, string> { ["ctrl+shift+p"] = "palette.show" }, TerminalKeys.PassKeys(keymap, context: null));

        // In the sidebar the view keys come too, under whatever keys the user gave them; a chord and a pane-only key stay with the page.
        Assert.Equal(new Dictionary<string, string>
        {
            ["ctrl+shift+p"] = "palette.show",
            ["ctrl+shift+e"] = "view.showExplorer",
            ["ctrl+shift+f"] = "view.showSearch",
            ["ctrl+alt+b"] = "view.toggleSidebar",
        }, TerminalKeys.PassKeys(keymap, context: null, TerminalKeys.SidebarPageWays));
    }

    private static Keymap TabKeymap(params KeymapBinding[] extra) => Keymap.From(new KeymapData(1000,
    [
        new KeymapBinding("ctrl+shift+p", "palette.show", null),
        new KeymapBinding("ctrl+tab", "tab.next", KeyContexts.FilesView),
        new KeymapBinding("ctrl+shift+tab", "tab.previous", KeyContexts.FilesView),
        new KeymapBinding("ctrl+w", "tab.close", KeyContexts.FilesView),
        new KeymapBinding("ctrl+t", "tab.new", KeyContexts.FilesView),
        new KeymapBinding("ctrl+1", "tab.select", KeyContexts.FilesView),
        new KeymapBinding("ctrl+2", "tab.select", KeyContexts.FilesView),
        // Not tab keys of a page: Tab and Esc stay the page's, a chord starts with a key the page may use, and the rest are pane keys.
        new KeymapBinding("tab", "view.focusOtherPane", KeyContexts.FilesView),
        new KeymapBinding("ctrl+k ctrl+right", "tab.moveToOtherPane", KeyContexts.FilesView),
        new KeymapBinding("ctrl+up", "tab.openFolderInNewTab", KeyContexts.FilesView),
        new KeymapBinding("f5", "file.copyToOtherPane", KeyContexts.FilesView),
        .. extra,
    ],
    ["palette.show"]));

    [Fact]
    public void A_tool_page_hands_back_the_tab_keys_that_are_bound_in_the_pane()
    {
        var keys = TerminalKeys.PassKeys(TabKeymap(), context: null, paneWays: TerminalKeys.TabWays);

        Assert.Equal(new Dictionary<string, string>
        {
            ["ctrl+shift+p"] = "palette.show",
            ["ctrl+tab"] = "tab.next",
            ["ctrl+shift+tab"] = "tab.previous",
            ["ctrl+w"] = "tab.close",
            ["ctrl+t"] = "tab.new",
            ["ctrl+1"] = "tab.select",
            ["ctrl+2"] = "tab.select",
        }, keys);
    }

    [Fact]
    public void A_tab_key_the_user_rebound_is_the_one_the_page_hands_back()
    {
        // Rebinding replaces the default keys (docs/keybindings.md, "Changing bindings"); the keymap the core sends has only the new ones.
        var keymap = Keymap.From(new KeymapData(1000,
        [
            new KeymapBinding("ctrl+shift+p", "palette.show", null),
            new KeymapBinding("ctrl+alt+n", "tab.next", KeyContexts.FilesView),
            new KeymapBinding("ctrl+alt+x", "tab.close", null),
        ],
        ["palette.show"]));

        Assert.Equal(new Dictionary<string, string>
        {
            ["ctrl+shift+p"] = "palette.show",
            ["ctrl+alt+n"] = "tab.next",
            ["ctrl+alt+x"] = "tab.close",
        }, TerminalKeys.PassKeys(keymap, context: null, paneWays: TerminalKeys.TabWays));
    }

    [Fact]
    public void A_way_out_on_the_same_keys_wins_over_a_tab_key_whatever_their_order()
    {
        var wayOut = new KeymapBinding("ctrl+w", "view.toggleTerminal", null);
        foreach (var keymap in new[] { TabKeymap(wayOut), Keymap.From(new KeymapData(1000, [wayOut, new KeymapBinding("ctrl+w", "tab.close", KeyContexts.FilesView)], ["palette.show"])) })
        {
            Assert.Equal("view.toggleTerminal", TerminalKeys.PassKeys(keymap, context: null, paneWays: TerminalKeys.TabWays)["ctrl+w"]);
        }
    }

    [Fact]
    public void The_terminal_hands_back_only_the_two_tab_keys_that_no_shell_uses()
    {
        var keys = TerminalKeys.PassKeys(TabKeymap(), paneWays: TerminalKeys.TerminalTabWays);

        // Ctrl+W and Ctrl+T stay with the shell (delete word, transpose, fzf's file picker), and so does Ctrl+1.
        Assert.Equal(new Dictionary<string, string>
        {
            ["ctrl+shift+p"] = "palette.show",
            ["ctrl+tab"] = "tab.next",
            ["ctrl+shift+tab"] = "tab.previous",
        }, keys);
    }

    [Fact]
    public void Without_the_tab_ways_a_page_hands_back_only_what_it_did_before()
    {
        Assert.Equal(new Dictionary<string, string> { ["ctrl+shift+p"] = "palette.show" }, TerminalKeys.PassKeys(TabKeymap(), context: null));
    }

    [Theory]
    [InlineData(DockPlacement.Bottom, 300, 120)]
    [InlineData(DockPlacement.Bottom, 600, 180)]
    [InlineData(DockPlacement.Bottom, 1000, 240)]
    [InlineData(DockPlacement.Right, 500, 220)]
    [InlineData(DockPlacement.Right, 1000, 320)]
    [InlineData(DockPlacement.Right, 2000, 380)]
    public void The_dock_starts_at_the_design_s_clamp(DockPlacement placement, double available, double expected) =>
        Assert.Equal(expected, DockLayout.DefaultSize(placement, available), 3);

    [Fact]
    public void A_dragged_dock_keeps_its_minimum_and_leaves_the_panes_room()
    {
        Assert.Equal(DockPlacement.Right, DockLayout.PlacementFor("right"));
        Assert.Equal(DockPlacement.Bottom, DockLayout.PlacementFor("rail"));
        Assert.Equal(DockPlacement.Bottom, DockLayout.PlacementFor(null));

        Assert.Equal(120, DockLayout.Clamp(DockPlacement.Bottom, 40, 800));
        Assert.Equal(632, DockLayout.Clamp(DockPlacement.Bottom, 700, 800));
        Assert.Equal(400, DockLayout.Clamp(DockPlacement.Bottom, 400, 800));
        // A window too small for both keeps the minimum.
        Assert.Equal(120, DockLayout.Clamp(DockPlacement.Bottom, 300, 250));
        Assert.Equal(872, DockLayout.Clamp(DockPlacement.Right, 2000, 1200));
    }

    [Fact]
    public void A_dragged_size_is_kept_in_whole_pixels_under_its_placement_s_key()
    {
        Assert.Equal("ui.dockSize.bottom", DockLayout.ConfigKey(DockPlacement.Bottom));
        Assert.Equal("ui.dockSize.right", DockLayout.ConfigKey(DockPlacement.Right));
        Assert.Equal(213u, DockLayout.ToSetting(212.6));
        Assert.Equal(0u, DockLayout.ToSetting(-3));
    }

    [Fact]
    public void The_stored_dock_size_is_read_per_placement_and_an_odd_value_means_the_design_s_size()
    {
        using var stored = JsonDocument.Parse("""{"ui":{"layout":"right","dockSize":{"bottom":212,"right":null}}}""");
        var settings = UiSettings.FromConfig(stored.RootElement);
        Assert.Equal(212, settings.DockSize(DockPlacement.Bottom));
        Assert.Null(settings.DockSize(DockPlacement.Right));
        Assert.Null(settings.DockSize(DockLayout.PlacementFor(settings.Layout)));

        using var odd = JsonDocument.Parse("""{"ui":{"dockSize":{"bottom":"tall","right":-40}}}""");
        var fallback = UiSettings.FromConfig(odd.RootElement);
        Assert.Null(fallback.DockBottom);
        Assert.Null(fallback.DockRight);

        // A size the window cannot fit is kept within the design's limits when it is used.
        Assert.Equal(632, DockLayout.Clamp(DockPlacement.Bottom, 5000, 800));
    }

    [Fact]
    public void The_WebView2_data_folder_moves_with_CABINETOS_WEBVIEW2_DIR()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CabinetOS", "WebView2");
        Assert.Equal(real, CabinetOS.Core.Presentation.WebViewData.Root(_ => null));
        Assert.Equal(real, CabinetOS.Core.Presentation.WebViewData.Root(_ => "  "));
        Assert.Equal(@"D:\scratch\webview2", CabinetOS.Core.Presentation.WebViewData.Root(name => name == CabinetOS.Core.Presentation.WebViewData.DirEnv ? @" D:\scratch\webview2 " : null));
        var relative = CabinetOS.Core.Presentation.WebViewData.Root(_ => "webview2");
        Assert.True(Path.IsPathRooted(relative), relative);
        Assert.EndsWith(Path.DirectorySeparatorChar + "webview2", relative);
    }
}
