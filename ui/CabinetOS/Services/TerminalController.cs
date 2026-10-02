using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Terminal;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace CabinetOS.Services;

/// <summary>One shell session as the window sees it: a tab of the terminal pane.</summary>
internal sealed class TerminalTab(ulong sessionId, string profile, int pane, string? folder, ushort cols, ushort rows)
{
    /// <summary>The core's session ID.</summary>
    public ulong SessionId { get; } = sessionId;

    /// <summary>The profile's name, the tab's title.</summary>
    public string Profile { get; } = profile;

    /// <summary>The file pane the session belongs to: 0 left, 1 right.</summary>
    public int Pane { get; } = pane;

    /// <summary>Locked or linked; the core tells changes (<c>terminal_mode_changed</c>).</summary>
    public TerminalMode Mode { get; set; } = TerminalMode.Locked;

    /// <summary>Whether the session may be linked (its profile's <c>linkable</c>, as the core says).</summary>
    public bool Linkable { get; set; }

    /// <summary>The folder the session started in.</summary>
    public string? StartFolder { get; } = folder;

    /// <summary>The shell's folder as its prompt hook last reported it (<c>terminal_folder_changed</c>); null before the first report.</summary>
    public string? Folder { get; set; }

    /// <summary>When the tab was last shown, as a growing count; the pane's most recent session is the one shown last.</summary>
    public long LastShown { get; set; }

    /// <summary>Whether the shell still runs.</summary>
    public bool Running { get; set; } = true;

    /// <summary>The shell's exit code, once it ended.</summary>
    public uint? ExitCode { get; set; }

    /// <summary>The size the core knows.</summary>
    public (ushort Cols, ushort Rows) Size { get; set; } = (cols, rows);

    /// <summary>The output waiting for the page.</summary>
    public OutputCoalescer Output { get; } = new(() => Environment.TickCount64);

    /// <summary>The byte pipe, once connected.</summary>
    public TerminalPipe? Pipe { get; set; }

    /// <summary>Completed when the pipe ended: the shell's last output has been read.</summary>
    public TaskCompletionSource PipeEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The one-shot timer that sends the next batch of output.</summary>
    public DispatcherQueueTimer? FlushTimer { get; set; }

    /// <summary>Whether a flush is scheduled.</summary>
    public bool FlushQueued { get; set; }

    /// <summary>The timer that closes the tab 3 s after the shell ended.</summary>
    public DispatcherQueueTimer? CloseTimer { get; set; }

    /// <summary>What the order of the tabs needs of it.</summary>
    public TabFacts Facts => new(SessionId, Pane, Running, LastShown);
}

/// <summary>
/// The terminal pane's sessions (docs/ui.md, "The terminal"). The core runs
/// the shells; this class opens and closes sessions, each bound to a file
/// pane, pumps each byte pipe into the xterm.js page at most 60 times a
/// second, and sends keys and sizes back. Nothing the panes do reaches a
/// session (the Zero-Hijack rule): a shell gets only the user's keys and the
/// paths the user asked to type. Everything runs on the UI thread; the
/// pipes' reader threads only schedule flushes.
/// </summary>
internal sealed class TerminalController
{
    /// <summary>The terminal page's virtual host.</summary>
    public const string PageHost = "terminal." + WebViewHost.Domain;

    private const string Target = "cabinetos_ui::terminal";

    private readonly ICoreChannel _core;
    private readonly DispatcherQueue _dispatcher;
    private readonly WebViewHost _page;
    private readonly List<TerminalTab> _tabs = [];
    private IReadOnlyDictionary<string, string> _passKeys = new Dictionary<string, string>();
    private string? _theme;
    private TaskCompletionSource<bool>? _ready;
    private bool _pageReady;
    private bool _starting;
    private long _shownCount;
    // The split mirror: whether the dock shows two halves, and where the panes lie across it (the window
    // measures them; docs/ui.md, "The terminal"). The view the page was last sent, so a layout that did not
    // change sends nothing.
    private bool _split;
    private bool _dual = true;
    private PaneSpan _leftSpan;
    private PaneSpan _rightSpan;
    private double _dockWidth;
    private string? _lastView;

    /// <summary>A controller whose page lives in <paramref name="page"/>.</summary>
    public TerminalController(ICoreChannel core, DispatcherQueue dispatcher, WebViewHost page)
    {
        _core = core;
        _dispatcher = dispatcher;
        _page = page;
        _page.MapFolder(PageHost, Path.Combine(AppContext.BaseDirectory, "Assets", "xterm"), CoreWebView2HostResourceAccessKind.Deny);
        _page.MessageReceived += OnPageMessage;
        _page.Failed += _ =>
        {
            _pageReady = false;
            _ready?.TrySetResult(false);
            _ready = null;
        };
    }

    /// <summary>Tabs, the shown tab, a mode or the caption changed.</summary>
    public event Action? Changed;

    /// <summary>Something for the status bar: (text, is an error).</summary>
    public event Action<string, bool>? Notice;

    /// <summary>A window command for a key pressed in the terminal (<see cref="TerminalKeys"/>).</summary>
    public event Action<string>? KeyCommand;

    /// <summary>The page asked for the clipboard's text (Ctrl+Shift+V) for a running session.</summary>
    public event Action<ulong>? PasteRequested;

    /// <summary>The last tab closed.</summary>
    public event Action? LastClosed;

    /// <summary>The sessions, oldest first.</summary>
    public IReadOnlyList<TerminalTab> Tabs => _tabs;

    /// <summary>
    /// The tab the keys go to: the one view's shown tab, or in the split mirror the shown tab of the half that has
    /// the keyboard (null when that half shows the hint).
    /// </summary>
    public TerminalTab? Shown { get; private set; }

    /// <summary>Whether the dock shows two halves, one under each pane (<see cref="TerminalSplitLayout"/>).</summary>
    public bool Split => _split;

    /// <summary>The half that has the keyboard, in the split mirror: the pane of the tab shown last, or last clicked into.</summary>
    public int KeyboardPane { get; private set; }

    /// <summary>The profiles from <c>terminal.profiles</c>.</summary>
    public TerminalProfiles Profiles { get; set; } = TerminalProfiles.Defaults;

    /// <summary>How many cells fit a new session of a pane now (the pane's half in the split): the size it starts with.</summary>
    public Func<int, (ushort Cols, ushort Rows)> EstimateSize { get; set; } = _ => (100, 24);

    /// <summary>The header's caption: how the shown shell ended, else its folder (<see cref="TerminalCaption"/>).</summary>
    public CaptionLook Caption() => CaptionFor(Shown);

    /// <summary>The caption of a half's header: the same rule for the session that half shows.</summary>
    public static CaptionLook CaptionFor(TerminalTab? tab) =>
        TerminalCaption.Decide(new CaptionFacts(tab?.Profile, tab?.Running ?? false, tab?.ExitCode, tab?.StartFolder, tab?.Folder));

    /// <summary>The tabs in one line, the shown one marked (<see cref="TerminalHeader.Describe"/>).</summary>
    public string Describe() =>
        TerminalHeader.Describe(_tabs.Select(t => (t.SessionId, t.Profile, t.Pane, t.Mode, t == Shown)));

    /// <summary>The pane's most recent running session, or null (<see cref="TerminalTabs.MostRecent"/>).</summary>
    public TerminalTab? MostRecentFor(int pane) =>
        TerminalTabs.MostRecent(_tabs.Select(t => t.Facts), pane) is { } session ? Find(session) : null;

    /// <summary>
    /// The halves of the split dock now, left first (<see cref="TerminalSplitLayout.Halves"/>): one under each pane, or
    /// one for the left pane alone while one pane is shown. The one view has none of its own, but this answers for it
    /// too, from the panes the window last measured.
    /// </summary>
    public IReadOnlyList<SplitHalf> Halves() =>
        TerminalSplitLayout.Halves([.. _tabs.Select(t => t.Facts)], _dual, _leftSpan, _rightSpan, _dockWidth);

    /// <summary>
    /// The pane whose session Ctrl+` pressed in <paramref name="activePane"/> counts as shown (<see cref="TerminalSplitLayout.SummonShownPane"/>):
    /// the shown tab's pane, or in the split the active pane's own half when it shows a session.
    /// </summary>
    public int? ShownPaneFor(int activePane) =>
        TerminalSplitLayout.SummonShownPane(_split, [.. _tabs.Select(t => t.Facts)], activePane, Shown?.Pane);

    /// <summary>The tab a half shows: the pane's one shown last, or null for the hint.</summary>
    public TerminalTab? ShownIn(int pane) =>
        TerminalSplitLayout.ShownIn(_tabs.Select(t => t.Facts), pane) is { } session ? Find(session) : null;

    /// <summary>
    /// The window measured the dock's layout: whether it is split (<see cref="TerminalSplitLayout.Active"/>), whether two
    /// panes are shown, where the panes lie across the dock, and how wide the dock is. The page gets the new places when
    /// they differ from the ones it has. Leaving the split shows the session the half with the keyboard showed.
    /// </summary>
    public void SetSplit(bool split, bool dual, PaneSpan left, PaneSpan right, double dockWidth)
    {
        var (was, wasDual) = (_split, _dual);
        (_split, _dual, _leftSpan, _rightSpan, _dockWidth) = (split, dual, left, right, dockWidth);
        var before = Shown;
        if (!split)
        {
            if (was)
            {
                Shown = Find(TerminalSplitLayout.ShownWhenUnsplit([.. _tabs.Select(t => t.Facts)], KeyboardPane) ?? 0);
            }
        }
        else
        {
            // Turned on, the half of the shown tab has the keyboard; with one pane shown, the left half is all there is.
            KeyboardPane = !dual ? 0 : !was && Shown is { } current ? current.Pane : KeyboardPane;
            Shown = ShownIn(KeyboardPane);
        }
        PostView();
        if (was != split || wasDual != dual || before != Shown)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>The keymap changed: the page passes on the new ways out.</summary>
    public void SetKeymap(Keymap keymap)
    {
        _passKeys = TerminalKeys.PassKeys(keymap, paneWays: TerminalKeys.TerminalTabWays);
        if (_pageReady)
        {
            _page.Post(TerminalPageMessages.PassKeys(_passKeys.Keys));
        }
    }

    /// <summary>The command the page passes on for <paramref name="combo"/> (<c>ctrl+`</c>), or null: the key is the shell's.</summary>
    public string? PassKeyCommand(string combo) => _passKeys.TryGetValue(combo, out var command) ? command : null;

    /// <summary>Colors and font for the page (the theme changed).</summary>
    public void SetTheme(string theme)
    {
        _theme = theme;
        if (_pageReady)
        {
            _page.Post(theme);
        }
    }

    /// <summary>Gives the shown terminal the keyboard (the view focused the WebView2 first); in the split, the half that has it.</summary>
    public void FocusPage() => _page.Post(TerminalPageMessages.Focus(Shown?.SessionId));

    /// <summary>
    /// Starts a shell bound to <paramref name="pane"/> (0 left, 1 right): <paramref name="profile"/>
    /// (null for the default) in <paramref name="folder"/> (null for the profile folder), shown at once.
    /// </summary>
    public async Task<TerminalTab?> OpenAsync(string? profile, string? folder, int pane, string? requestId = null)
    {
        if (!await EnsurePageAsync())
        {
            Notice?.Invoke("The terminal could not start: WebView2 did not load its page.", true);
            return null;
        }
        var name = profile ?? Profiles.DefaultProfile;
        var (cols, rows) = EstimateSize(pane);
        CoreReply reply;
        try
        {
            reply = await _core.RequestAsync(new TerminalOpenRequest(cols, rows, TerminalBinding.PaneName(pane))
            {
                Profile = profile,
                Cwd = folder,
                Id = requestId ?? "",
            });
        }
        catch (IOException error)
        {
            Notice?.Invoke($"Cannot start {name}: {error.Message}", true);
            return null;
        }
        switch (reply)
        {
            case TerminalOpenedReply opened:
                return await AttachAsync(opened, name, pane, folder, cols, rows);
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                Notice?.Invoke("This core has no terminal yet (terminal_open).", true);
                return null;
            case ErrorReply error:
                Notice?.Invoke($"Cannot start {name}: {error.Message}", true);
                return null;
            default:
                return null;
        }
    }

    /// <summary>Shows another tab.</summary>
    public void Show(ulong sessionId)
    {
        if (Find(sessionId) is { } tab)
        {
            ShowTab(tab);
        }
    }

    /// <summary>
    /// Shows the tab <paramref name="step"/> places from the shown one (1 the next, -1 the
    /// previous), round the ends; false when there is no other tab to show.
    /// </summary>
    public bool ShowNext(int step)
    {
        // In the split mirror the round is the tabs of the half that has the keyboard.
        var next = TerminalSplitLayout.CycleTarget([.. _tabs.Select(t => t.Facts)], _split ? KeyboardPane : null, Shown?.SessionId, step);
        if (next is not { } session || session == Shown?.SessionId)
        {
            return false;
        }
        Show(session);
        return true;
    }

    /// <summary>
    /// Locks a session or links it to its pane (<c>terminal_set_mode</c>). The tab changes when the
    /// core's <c>terminal_mode_changed</c> arrives, so every window shows the same. A profile that
    /// cannot be linked gets a notice and stays locked.
    /// </summary>
    public async Task SetModeAsync(ulong sessionId, TerminalMode mode, string? requestId = null)
    {
        if (Find(sessionId) is not { } tab)
        {
            return;
        }
        if (mode == TerminalMode.Linked && !tab.Linkable)
        {
            Notice?.Invoke($"{tab.Profile} cannot be linked to a pane: no prompt hook can be added to it.", false);
            return;
        }
        CoreReply reply;
        try
        {
            reply = await _core.RequestAsync(new TerminalSetModeRequest(sessionId, TerminalBinding.ModeName(mode)) { Id = requestId ?? "" });
        }
        catch (IOException error)
        {
            Notice?.Invoke($"The mode could not change: {error.Message}", true);
            return;
        }
        switch (reply)
        {
            case OkReply:
                Diag.Info(Target, "terminal mode asked", new LogField("session_id", sessionId), new LogField("mode", TerminalBinding.ModeName(mode)));
                break;
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                Notice?.Invoke("This core cannot lock or link a terminal yet (terminal_set_mode).", true);
                break;
            case ErrorReply error:
                Notice?.Invoke($"The mode could not change: {error.Message}", true);
                break;
        }
    }

    /// <summary>A session's mode changed (<c>terminal_mode_changed</c>, from this window or another client).</summary>
    public void OnModeChanged(TerminalModeChangedEvent changed)
    {
        if (Find(changed.SessionId) is not { } tab || TerminalBinding.ParseMode(changed.Mode) is not { } mode || tab.Mode == mode)
        {
            return;
        }
        tab.Mode = mode;
        Diag.Info(Target, "terminal mode changed", new LogField("session_id", tab.SessionId), new LogField("mode", changed.Mode),
            new LogField("pane", TerminalBinding.PaneName(tab.Pane)));
        Changed?.Invoke();
    }

    /// <summary>
    /// A session's shell reported a new folder (<c>terminal_folder_changed</c>): its prompt hook prints it at
    /// each prompt, after a <c>cd</c> of the user's own and after a linked shell followed its pane. The caption
    /// shows it.
    /// </summary>
    public void OnFolderChanged(TerminalFolderChangedEvent changed)
    {
        if (Find(changed.SessionId) is not { } tab || tab.Folder == changed.Folder)
        {
            return;
        }
        tab.Folder = changed.Folder;
        Diag.Info(Target, "terminal folder changed", new LogField("session_id", tab.SessionId), new LogField("folder", changed.Folder),
            new LogField("pane", TerminalBinding.PaneName(tab.Pane)), new LogField("mode", TerminalBinding.ModeName(tab.Mode)),
            new LogField("shown", tab == Shown));
        Changed?.Invoke();
    }

    /// <summary>Closes a tab: the shell ends (<c>terminal_close</c>) if it still runs.</summary>
    public async Task CloseAsync(ulong sessionId)
    {
        if (Find(sessionId) is not { } tab || !_tabs.Remove(tab))
        {
            return;
        }
        tab.CloseTimer?.Stop();
        tab.FlushTimer?.Stop();
        _page.Post(TerminalPageMessages.Close(tab.SessionId));
        if (Shown == tab)
        {
            Shown = null;
            // The pane's other session comes to the front before another pane's. In the split mirror it only comes
            // to the front of its own half: a half with no other session shows the hint, and nothing moves to it.
            if (_split)
            {
                if (ShownIn(tab.Pane) is { } half)
                {
                    ShowTab(half);
                }
            }
            else if ((MostRecentFor(tab.Pane) ?? _tabs.LastOrDefault()) is { } next)
            {
                ShowTab(next);
            }
        }
        PostView();
        Changed?.Invoke();
        if (_tabs.Count == 0)
        {
            LastClosed?.Invoke();
        }
        if (tab.Pipe is { } pipe)
        {
            await pipe.DisposeAsync();
        }
        try
        {
            await _core.RequestAsync(new TerminalCloseRequest(tab.SessionId));
        }
        catch (IOException error)
        {
            Diag.Debug(Target, "cannot close a session", new LogField("session_id", tab.SessionId), new LogField("error", error.Message));
        }
        Diag.Info(Target, "terminal tab closed", new LogField("session_id", tab.SessionId));
    }

    /// <summary>
    /// The shell ended (<c>terminal_exited</c>). Its last output is shown,
    /// then a line with the exit code; the tab closes 3 s later.
    /// </summary>
    public async Task OnExitedAsync(TerminalExitedEvent exited)
    {
        if (Find(exited.SessionId) is not { Running: true } tab)
        {
            return;
        }
        tab.Running = false;
        tab.ExitCode = exited.ExitCode;
        Diag.Info(Target, "terminal shell exited", new LogField("session_id", tab.SessionId), new LogField("exit_code", exited.ExitCode));
        // The event and the pipe's last bytes travel apart; the bytes go first.
        await Task.WhenAny(tab.PipeEnded.Task, Task.Delay(1000));
        Flush(tab);
        _page.Post(TerminalPageMessages.Exited(tab.SessionId, exited.ExitCode));
        Changed?.Invoke();
        var timer = _dispatcher.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromSeconds(3);
        timer.Tick += (_, _) => _ = CloseAsync(tab.SessionId);
        tab.CloseTimer = timer;
        timer.Start();
    }

    /// <summary>The core stopped: its sessions ended with it.</summary>
    public void Reset()
    {
        var tabs = _tabs.ToList();
        _tabs.Clear();
        Shown = null;
        PostView();
        foreach (var tab in tabs)
        {
            tab.CloseTimer?.Stop();
            tab.FlushTimer?.Stop();
            _page.Post(TerminalPageMessages.Close(tab.SessionId));
            if (tab.Pipe is { } pipe)
            {
                _ = pipe.DisposeAsync().AsTask();
            }
        }
        if (tabs.Count > 0)
        {
            Changed?.Invoke();
            LastClosed?.Invoke();
        }
    }

    /// <summary>
    /// Types <paramref name="paths"/> at the shown shell's prompt, quoted for
    /// it by the core (<c>terminal_type_paths</c>), without Enter. Returns the
    /// core's reply, or null when no shell runs.
    /// </summary>
    public async Task<CoreReply?> TypePathsAsync(IReadOnlyList<string> paths, string? requestId = null)
    {
        if (Shown is not { Running: true } tab)
        {
            return null;
        }
        var reply = await _core.RequestAsync(new TerminalTypePathsRequest(tab.SessionId, paths) { Id = requestId ?? "" });
        Diag.Info(Target, "paths typed at the prompt", new LogField("session_id", tab.SessionId), new LogField("paths", paths.Count),
            new LogField("reply", reply.GetType().Name));
        return reply;
    }

    /// <summary>Types <paramref name="text"/> into the shown shell as if typed (the snapshot aid).</summary>
    public Task TypeAsync(string text) =>
        Shown is { Running: true, Pipe: { } pipe } ? pipe.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text)) : Task.CompletedTask;

    private async Task<bool> EnsurePageAsync()
    {
        if (_pageReady)
        {
            return true;
        }
        _ready ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = _ready.Task;
        if (!_page.IsRunning && !_starting)
        {
            _starting = true;
            try
            {
                var started = _page.View is null
                    ? await _page.StartAsync(PageUri())
                    : await _page.RestartAsync();
                if (!started)
                {
                    _ready?.TrySetResult(false);
                    _ready = null;
                    return false;
                }
            }
            finally
            {
                _starting = false;
            }
        }
        return await Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(15))) == ready && ready.Result;
    }

    /// <summary>Loads the page again after it stopped; the sessions go on and are drawn anew.</summary>
    public async Task<bool> ReloadAsync()
    {
        _pageReady = false;
        _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = _ready.Task;
        if (!await _page.RestartAsync())
        {
            _ready = null;
            return false;
        }
        return await Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(15))) == ready && ready.Result;
    }

    private static Uri PageUri() =>
        new($"https://{PageHost}/terminal.html?build={Environment.OSVersion.Version.Build}");

    private async Task<TerminalTab?> AttachAsync(TerminalOpenedReply opened, string profile, int pane, string? folder, ushort cols, ushort rows)
    {
        var tab = new TerminalTab(opened.SessionId, profile, pane, folder, cols, rows)
        {
            Mode = TerminalBinding.ParseMode(opened.Mode) ?? TerminalMode.Locked,
            Linkable = opened.Linkable,
        };
        _tabs.Add(tab);
        _page.Post(TerminalPageMessages.Create(tab.SessionId));
        Diag.Info(Target, "terminal session opened", new LogField("session_id", tab.SessionId), new LogField("profile", profile),
            new LogField("pane", TerminalBinding.PaneName(pane)), new LogField("mode", opened.Mode), new LogField("linkable", opened.Linkable),
            new LogField("cwd", folder), new LogField("pid", opened.Pid));
        ShowTab(tab);
        try
        {
            tab.Pipe = await TerminalPipe.ConnectAsync(opened.Pipe, tab.Output, TimeSpan.FromSeconds(5));
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Notice?.Invoke($"Cannot connect to {profile}: {error.Message}", true);
            await CloseAsync(tab.SessionId);
            return null;
        }
        tab.Pipe.OutputWaiting += () => _dispatcher.TryEnqueue(() => ScheduleFlush(tab));
        tab.Pipe.Ended += () =>
        {
            tab.PipeEnded.TrySetResult();
            _dispatcher.TryEnqueue(() => ScheduleFlush(tab));
        };
        // Output that arrived before the handlers did.
        if (tab.Output.HasPending)
        {
            ScheduleFlush(tab);
        }
        Changed?.Invoke();
        return tab;
    }

    private void ShowTab(TerminalTab tab)
    {
        tab.LastShown = ++_shownCount;
        // Showing a tab gives its half the keyboard (a tab button, Ctrl+`, a new session): the half is the tab's pane's.
        KeyboardPane = tab.Pane;
        if (Shown != tab)
        {
            Shown = tab;
            Diag.Info(Target, "terminal tab shown", new LogField("session_id", tab.SessionId), new LogField("profile", tab.Profile),
                new LogField("pane", TerminalBinding.PaneName(tab.Pane)), new LogField("tabs", Describe()));
        }
        PostView();
        Changed?.Invoke();
    }

    // What the page shows now: the one view's shown tab, or in the split the halves, each with its session or its hint.
    // A view the page already has is not sent again.
    private void PostView()
    {
        var message = _split
            ? TerminalPageMessages.View([.. Halves().Select(h => new ViewPlace(h.Session, h.X, h.Width, h.Empty ? TerminalSplitLayout.Hint : null))])
            : Shown is { } shown ? TerminalPageMessages.Show(shown.SessionId) : TerminalPageMessages.View([]);
        if (message == _lastView)
        {
            return;
        }
        _lastView = message;
        if (_pageReady)
        {
            _page.Post(message);
        }
    }

    private TerminalTab? Find(ulong sessionId) => _tabs.Find(t => t.SessionId == sessionId);

    // At most one message per session every 16 ms: a burst becomes one write in the page.
    private void ScheduleFlush(TerminalTab tab)
    {
        if (tab.FlushQueued || !_tabs.Contains(tab))
        {
            return;
        }
        var wait = tab.Output.MillisecondsUntilDue();
        if (wait <= 0)
        {
            Flush(tab);
            return;
        }
        if (tab.FlushTimer is null)
        {
            var timer = _dispatcher.CreateTimer();
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                tab.FlushQueued = false;
                Flush(tab);
            };
            tab.FlushTimer = timer;
        }
        tab.FlushQueued = true;
        tab.FlushTimer.Interval = TimeSpan.FromMilliseconds(wait);
        tab.FlushTimer.Start();
    }

    // While the page is down (a crash), output is dropped; the redraw after the reload repaints the screen.
    private void Flush(TerminalTab tab)
    {
        foreach (var chunk in tab.Output.Flush())
        {
            if (_pageReady)
            {
                _page.Post(TerminalPageMessages.Output(tab.SessionId, chunk));
            }
        }
    }

    private void OnPageMessage(string json)
    {
        if (TerminalPageMessages.Parse(json) is not { } message)
        {
            return;
        }
        switch (message.Type)
        {
            case "ready":
                OnPageReady();
                break;
            case "input" or "binary":
                if (Find(message.Session) is { Running: true, Pipe: { } pipe })
                {
                    _ = pipe.WriteAsync(TerminalPageMessages.Bytes(message));
                }
                break;
            case "resize":
                if (Find(message.Session) is { } sized && sized.Size != ((ushort)message.Cols, (ushort)message.Rows))
                {
                    sized.Size = ((ushort)message.Cols, (ushort)message.Rows);
                    if (sized.Running)
                    {
                        _ = ResizeAsync(sized);
                    }
                }
                break;
            case "key":
                if (message.Keys is { } keys && _passKeys.TryGetValue(keys, out var command))
                {
                    KeyCommand?.Invoke(command);
                }
                break;
            case "focused":
                OnTerminalFocused(message.Session);
                break;
            case "paste":
                var running = Find(message.Session) is { Running: true };
                Diag.Info(Target, "terminal paste asked", new LogField("session_id", message.Session), new LogField("running", running));
                if (running)
                {
                    PasteRequested?.Invoke(message.Session);
                }
                break;
        }
    }

    // A session's terminal got the keyboard, by a click into its text or by the window's own focus message. In the split
    // that says which half the keys go to; nothing else moves it (the Zero-Hijack rule): the window never moves the
    // keyboard between halves by itself. The one view has one terminal on screen, so there is nothing to learn.
    private void OnTerminalFocused(ulong sessionId)
    {
        if (!_split || Find(sessionId) is not { } tab || tab == Shown || ShownIn(tab.Pane) != tab)
        {
            return;
        }
        KeyboardPane = tab.Pane;
        Shown = tab;
        Diag.Info(Target, "terminal half focused", new LogField("session_id", tab.SessionId), new LogField("pane", TerminalBinding.PaneName(tab.Pane)));
        Changed?.Invoke();
    }

    /// <summary>Pastes text into a session's terminal, as if the user had pasted it there.</summary>
    public void Paste(ulong sessionId, string text)
    {
        if (Find(sessionId) is not { Running: true } tab)
        {
            return;
        }
        _page.Post(TerminalPageMessages.Paste(tab.SessionId, text));
        Diag.Info(Target, "terminal pasted", new LogField("session_id", tab.SessionId), new LogField("chars", text.Length));
    }

    private void OnPageReady()
    {
        _pageReady = true;
        _page.Post(TerminalPageMessages.PassKeys(_passKeys.Keys));
        if (_theme is { } theme)
        {
            _page.Post(theme);
        }
        // After a reload: the sessions went on in the core; draw them again.
        foreach (var tab in _tabs)
        {
            _page.Post(TerminalPageMessages.Create(tab.SessionId));
            if (!tab.Running && tab.ExitCode is { } code)
            {
                _page.Post(TerminalPageMessages.Exited(tab.SessionId, code));
            }
        }
        _lastView = null;
        PostView();
        // A resize to another size makes the pseudo-console paint the whole screen (docs/terminal.md): every
        // session on screen, which is the shown one, or each half's in the split.
        IEnumerable<TerminalTab?> onScreen = _split ? Halves().Select(h => h.Session is { } s ? Find(s) : null) : [Shown];
        foreach (var shown in onScreen)
        {
            if (shown is { Running: true, Size.Cols: > 1 })
            {
                var (cols, rows) = shown.Size;
                _ = RepaintAsync(shown, cols, rows);
            }
        }
        _ready?.TrySetResult(true);
        _ready = null;
        Diag.Info(Target, "terminal page ready", new LogField("sessions", _tabs.Count));
    }

    private async Task RepaintAsync(TerminalTab tab, ushort cols, ushort rows)
    {
        await SendSizeAsync(tab, (ushort)(cols - 1), rows);
        await SendSizeAsync(tab, cols, rows);
    }

    private Task ResizeAsync(TerminalTab tab) => SendSizeAsync(tab, tab.Size.Cols, tab.Size.Rows);

    private async Task SendSizeAsync(TerminalTab tab, ushort cols, ushort rows)
    {
        try
        {
            await _core.RequestAsync(new TerminalResizeRequest(tab.SessionId, cols, rows));
        }
        catch (IOException error)
        {
            Diag.Debug(Target, "cannot resize a session", new LogField("session_id", tab.SessionId), new LogField("error", error.Message));
        }
    }
}
