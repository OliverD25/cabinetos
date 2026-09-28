using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Terminal;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace CabinetOS.Services;

/// <summary>One shell session as the window sees it: a tab of the terminal pane.</summary>
internal sealed class TerminalTab(ulong sessionId, string profile, string? folder, ushort cols, ushort rows)
{
    /// <summary>The core's session ID.</summary>
    public ulong SessionId { get; } = sessionId;

    /// <summary>The profile's name, the tab's title.</summary>
    public string Profile { get; } = profile;

    /// <summary>Whether the shell still runs.</summary>
    public bool Running { get; set; } = true;

    /// <summary>The shell's exit code, once it ended.</summary>
    public uint? ExitCode { get; set; }

    /// <summary>The folder the session started in or was last synced to.</summary>
    public string? LastSynced { get; set; } = folder;

    /// <summary>What the last folder sync decided.</summary>
    public CwdSyncDecision LastDecision { get; set; } = CwdSyncDecision.Sync;

    /// <summary>The size the core knows.</summary>
    public (ushort Cols, ushort Rows) Size { get; set; } = (cols, rows);

    /// <summary>Whether a line is being typed, or a full-screen program runs.</summary>
    public TypingTracker Typing { get; } = new();

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
}

/// <summary>
/// The terminal pane's sessions (docs/ui.md, "The terminal"). The core runs
/// the shells; this class opens and closes sessions, pumps each byte pipe
/// into the xterm.js page at most 60 times a second, sends keys and sizes
/// back, and types the active pane's folder into the shown shell when the
/// rule allows (<see cref="CwdSyncRule"/>). Everything runs on the UI thread;
/// the pipes' reader threads only schedule flushes.
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
    private readonly Debouncer<string> _sync = new(() => Environment.TickCount64, 300);
    private readonly DispatcherQueueTimer _syncTimer;
    private IReadOnlyDictionary<string, string> _passKeys = new Dictionary<string, string>();
    private string? _theme;
    private TaskCompletionSource<bool>? _ready;
    private bool _pageReady;
    private bool _starting;
    private string _activeFolder = "";
    private bool _visible;

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
        _syncTimer = dispatcher.CreateTimer();
        _syncTimer.IsRepeating = false;
        _syncTimer.Tick += (_, _) => _ = SyncDueAsync();
    }

    /// <summary>Tabs, the shown tab or the caption changed.</summary>
    public event Action? Changed;

    /// <summary>Something for the status bar: (text, is an error).</summary>
    public event Action<string, bool>? Notice;

    /// <summary>A window command for a key pressed in the terminal (<see cref="TerminalKeys"/>).</summary>
    public event Action<string>? KeyCommand;

    /// <summary>The last tab closed.</summary>
    public event Action? LastClosed;

    /// <summary>The sessions, oldest first.</summary>
    public IReadOnlyList<TerminalTab> Tabs => _tabs;

    /// <summary>The tab on screen.</summary>
    public TerminalTab? Shown { get; private set; }

    /// <summary>The profiles from <c>terminal.profiles</c>.</summary>
    public TerminalProfiles Profiles { get; set; } = TerminalProfiles.Defaults;

    /// <summary>How many cells fit the pane now: the size a new session starts with.</summary>
    public Func<(ushort Cols, ushort Rows)> EstimateSize { get; set; } = () => (100, 24);

    /// <summary>
    /// Whether the pane is on screen. A hidden terminal does not follow the
    /// active pane; when it shows again, it catches up after the usual wait.
    /// </summary>
    public bool IsVisible
    {
        get => _visible;
        set
        {
            _visible = value;
            if (value)
            {
                QueueSync();
            }
        }
    }

    /// <summary>The header's caption: the folder sync, or how the shell ended.</summary>
    public string Caption(DockPlacement placement)
    {
        var folder = DisplayFormat.FolderName(_activeFolder);
        return Shown switch
        {
            null => "",
            { Running: false } tab => $"{tab.Profile} exited with code {tab.ExitCode}",
            { LastDecision: CwdSyncDecision.SkipTyping } => placement == DockPlacement.Bottom ? "cwd not synced: a command is being typed" : "not synced: typing",
            { LastDecision: CwdSyncDecision.SkipFullScreen } => placement == DockPlacement.Bottom ? "cwd not synced: a full-screen program runs" : "not synced: a program runs",
            _ => placement == DockPlacement.Bottom ? $"cwd synced to active pane · {folder}" : $"synced to {folder}",
        };
    }

    /// <summary>The keymap changed: the page passes on the new ways out.</summary>
    public void SetKeymap(Keymap keymap)
    {
        _passKeys = TerminalKeys.PassKeys(keymap);
        if (_pageReady)
        {
            _page.Post(TerminalPageMessages.PassKeys(_passKeys.Keys));
        }
    }

    /// <summary>Colors and font for the page (the theme changed).</summary>
    public void SetTheme(string theme)
    {
        _theme = theme;
        if (_pageReady)
        {
            _page.Post(theme);
        }
    }

    /// <summary>The active pane's folder, which the shown shell follows.</summary>
    public void SetActiveFolder(string folder)
    {
        if (string.Equals(folder, _activeFolder, StringComparison.Ordinal))
        {
            return;
        }
        _activeFolder = folder;
        QueueSync();
        Changed?.Invoke();
    }

    /// <summary>Gives the shown terminal the keyboard (the view focused the WebView2 first).</summary>
    public void FocusPage() => _page.Post(TerminalPageMessages.Focus());

    /// <summary>
    /// Starts a shell: <paramref name="profile"/> (null for the default) in
    /// <paramref name="folder"/> (null for the profile folder), shown at once.
    /// </summary>
    public async Task<TerminalTab?> OpenAsync(string? profile, string? folder, string? requestId = null)
    {
        if (!await EnsurePageAsync())
        {
            Notice?.Invoke("The terminal could not start: WebView2 did not load its page.", true);
            return null;
        }
        var name = profile ?? Profiles.DefaultProfile;
        var (cols, rows) = EstimateSize();
        CoreReply reply;
        try
        {
            reply = await _core.RequestAsync(new TerminalOpenRequest(cols, rows) { Profile = profile, Cwd = folder, Id = requestId ?? "" });
        }
        catch (IOException error)
        {
            Notice?.Invoke($"Cannot start {name}: {error.Message}", true);
            return null;
        }
        switch (reply)
        {
            case TerminalOpenedReply opened:
                return await AttachAsync(opened, name, folder, cols, rows);
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
            if (_tabs.Count > 0)
            {
                ShowTab(_tabs[^1]);
            }
        }
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

    /// <summary>Types <paramref name="text"/> into the shown shell as if typed (the snapshot aid).</summary>
    public Task TypeAsync(string text) =>
        Shown is { Running: true, Pipe: { } pipe } tab ? TypeInto(tab, pipe, text) : Task.CompletedTask;

    private static Task TypeInto(TerminalTab tab, TerminalPipe pipe, string text)
    {
        tab.Typing.OnInput(text);
        return pipe.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text));
    }

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

    private async Task<TerminalTab?> AttachAsync(TerminalOpenedReply opened, string profile, string? folder, ushort cols, ushort rows)
    {
        var tab = new TerminalTab(opened.SessionId, profile, folder, cols, rows);
        _tabs.Add(tab);
        _page.Post(TerminalPageMessages.Create(tab.SessionId));
        ShowTab(tab);
        Diag.Info(Target, "terminal session opened", new LogField("session_id", tab.SessionId), new LogField("profile", profile),
            new LogField("cwd", folder), new LogField("pid", opened.Pid));
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
        Shown = tab;
        _page.Post(TerminalPageMessages.Show(tab.SessionId));
        QueueSync();
        Changed?.Invoke();
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
                if (Find(message.Session) is { Running: true, Pipe: { } pipe } typed)
                {
                    if (message.Type == "input")
                    {
                        typed.Typing.OnInput(message.Data ?? "");
                    }
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
            case "buffer":
                if (Find(message.Session) is { } buffered)
                {
                    buffered.Typing.FullScreen = message.Alternate;
                }
                break;
            case "key":
                if (message.Keys is { } keys && _passKeys.TryGetValue(keys, out var command))
                {
                    KeyCommand?.Invoke(command);
                }
                break;
        }
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
        if (Shown is { } shown)
        {
            _page.Post(TerminalPageMessages.Show(shown.SessionId));
            // A resize to another size makes the pseudo-console paint the whole screen (docs/terminal.md).
            if (shown.Running && shown.Size.Cols > 1)
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

    private void QueueSync()
    {
        if (!_visible || Shown is not { Running: true } || _activeFolder.Length == 0)
        {
            return;
        }
        _sync.Set(_activeFolder);
        _syncTimer.Stop();
        _syncTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _sync.MillisecondsUntilDue()));
        _syncTimer.Start();
    }

    private async Task SyncDueAsync()
    {
        if (!_sync.TryTake(out var folder))
        {
            var wait = _sync.MillisecondsUntilDue();
            if (wait >= 0)
            {
                _syncTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, wait));
                _syncTimer.Start();
            }
            return;
        }
        if (!_visible || Shown is not { } tab)
        {
            return;
        }
        var decision = CwdSyncRule.Decide(folder, tab.LastSynced, tab.Running, tab.Typing);
        Diag.Debug(Target, "cwd sync", new LogField("session_id", tab.SessionId), new LogField("path", folder),
            new LogField("decision", decision.ToString()));
        if (decision != CwdSyncDecision.SkipSameFolder)
        {
            tab.LastDecision = decision;
        }
        if (decision == CwdSyncDecision.Sync)
        {
            try
            {
                switch (await _core.RequestAsync(new TerminalSyncCwdRequest(tab.SessionId, folder)))
                {
                    case OkReply:
                        tab.LastSynced = folder;
                        tab.Typing.OnSynced();
                        break;
                    case ErrorReply error:
                        Diag.Info(Target, "cwd sync refused", new LogField("session_id", tab.SessionId), new LogField("code", error.Code),
                            new LogField("error", error.Message));
                        break;
                }
            }
            catch (IOException error)
            {
                Diag.Debug(Target, "cannot sync the cwd", new LogField("error", error.Message));
            }
        }
        Changed?.Invoke();
    }
}
