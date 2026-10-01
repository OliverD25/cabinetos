using System.Text.Json;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Settings;
using CabinetOS.Core.Terminal;
using CabinetOS.Services;
using CabinetOS.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace CabinetOS;

// The terminal and the Tool Dock around it (docs/ui.md, "The terminal"). Each session belongs to a
// pane; Ctrl+` reaches the session of the pane it is pressed in (Active Summoning), and nothing a
// pane does changes the shown session or sends anything to a shell (the Zero-Hijack rule).
public sealed partial class MainWindow
{
    private const string TerminalTarget = "cabinetos_ui::terminal";

    private TerminalController _terminal = null!;
    private DockPlacement _dockPlacement = DockPlacement.Bottom;
    private bool _dockVisible;
    private double? _dockUserSize;
    private double _dockDragStart;
    private bool _dockDragging;
    // What cabinetos.json holds for each placement, as far as the window knows: its own last
    // write, or what the file said. A config_changed that brings the same value changes nothing.
    private readonly Dictionary<DockPlacement, uint?> _dockKnown = [];
    private bool _paletteFromTerminal;
    // The pane Ctrl+` gave the keyboard back to from the terminal, until the terminal gets it again or the dock
    // hides: Ctrl+` there is the "second" one, which hides the dock. A pane switch in between (closing a tool in
    // the other pane, the drive list) keeps it, or the second Ctrl+` would give the keyboard back instead.
    private int? _terminalHandedBackTo;

    private void SetUpTerminal()
    {
        _terminal = new TerminalController(_session, DispatcherQueue, Dock.TerminalPage) { EstimateSize = Dock.EstimateCells };
        _terminal.Changed += UpdateDockHeader;
        _terminal.Notice += (text, isError) => ShowNotice(text, isError);
        _terminal.KeyCommand += command => _ = _router.ExecuteAsync(command, trigger: "key");
        _terminal.PasteRequested += session => _ = PasteIntoTerminalAsync(session);
        _terminal.LastClosed += () =>
        {
            if (_dockVisible)
            {
                HideDock();
            }
        };
        Dock.RunCommand = (id, args, trigger) => _router.ExecuteAsync(id, args, trigger);
        Dock.TerminalFocused += () => _terminalHandedBackTo = null;
        Dock.TerminalPage.Failed += reason =>
        {
            var hadFocus = Dock.HasTerminalFocus;
            Dock.ShowStopped("The terminal stopped", $"Its page failed: {reason}. The shells still run.");
            if (hadFocus)
            {
                FocusActivePane();
            }
        };
        BottomSplitter.DragStarted += StartDockDrag;
        BottomSplitter.Dragged += delta => ResizeDock(_dockDragStart - delta);
        BottomSplitter.DragCompleted += EndDockDrag;
        RightSplitter.DragStarted += StartDockDrag;
        RightSplitter.Dragged += delta => ResizeDock(_dockDragStart - delta);
        RightSplitter.DragCompleted += EndDockDrag;
        MainColumn.SizeChanged += (_, _) => ApplyDockSize();
        TerminalButton.Click += (_, _) => _ = _router.ExecuteAsync("view.toggleTerminal", trigger: "button");
        RootGrid.ActualThemeChanged += (_, _) => SendTerminalTheme();
        SendTerminalTheme();
        Dock.SetProfiles(_terminal.Profiles);
    }

    private void RegisterTerminalCommands()
    {
        _router.RegisterUiHandler("view.toggleTerminal", ToggleTerminalAsync);
        // The dock's own buttons and the menu's "Open in Terminal" pass a session, a folder or a pane;
        // from the palette or a key they come without, and act on the shown shell or the active pane.
        _router.RegisterUiHandler("terminal.new", invocation =>
            NewTerminalAsync(CommandArgs.Text(invocation.Args, "profile"), CommandArgs.Text(invocation.Args, "cwd"),
                CommandArgs.Number(invocation.Args, "pane") is { } pane and <= 1 ? (int)pane : null, invocation.RequestId));
        _router.RegisterUiHandler("terminal.show", async invocation =>
        {
            if (CommandArgs.Number(invocation.Args, "session") is not { } session)
            {
                await ShowDockAsync(invocation.RequestId);
                return;
            }
            _terminal.Show(session);
            FocusTerminal();
        });
        _router.RegisterUiHandler("terminal.close", CloseTerminalAsync);
        _router.RegisterUiHandler("terminal.reload", _ => ReloadTerminalAsync());
        _router.RegisterUiHandler("terminal.previousTab", _ => CycleTerminalTabs(-1));
        _router.RegisterUiHandler("terminal.nextTab", _ => CycleTerminalTabs(1));
        _router.RegisterUiHandler("terminal.setMode", SetTerminalModeAsync);
    }

    // Ctrl+` and the top row's terminal button. A key (or the palette) summons the active pane's session
    // (TerminalSummoning); the buttons, which no pane's keyboard is behind, toggle: shown -> hidden,
    // hidden -> the active pane's session. {"visible": false} hides, {"visible": true} only shows.
    private async Task ToggleTerminalAsync(CommandInvocation invocation)
    {
        var wanted = VisibleArgument(invocation.Args);
        if (wanted == false || (wanted is null && _dockVisible && invocation.Trigger is "button" or "rail"))
        {
            HideDock();
            return;
        }
        var state = SummonStateNow();
        var summon = wanted is null && invocation.Trigger is not ("button" or "rail")
            ? TerminalSummoning.Decide(state)
            : TerminalSummoning.DecideShow(state);
        await SummonAsync(summon, invocation.RequestId);
    }

    private static bool? VisibleArgument(JsonElement? args) =>
        args is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("visible", out var visible)
            && visible.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? visible.GetBoolean()
            : null;

    private SummonState SummonStateNow() => new(
        _dockVisible,
        _dockVisible && Dock.HasTerminalFocus,
        _active,
        _terminal.Shown?.Pane,
        _terminal.MostRecentFor(_active)?.SessionId,
        _terminalHandedBackTo == _active);

    // The dock's "show" without a session, and Ctrl+Alt+P: the active pane's session, shown, with the keyboard.
    private Task ShowDockAsync(string requestId) => SummonAsync(TerminalSummoning.DecideShow(SummonStateNow()), requestId);

    private async Task SummonAsync(Summon summon, string requestId)
    {
        Diag.Info(TerminalTarget, "terminal summoned", new LogField("action", summon.Action.ToString()),
            new LogField("pane", TerminalBinding.PaneName(_active)), new LogField("session_id", summon.Session));
        switch (summon.Action)
        {
            case SummonAction.HandBackToPane:
                FocusActivePane();
                // After the focus moved: the page's GotFocus must not clear it.
                _terminalHandedBackTo = _active;
                LogKeyboardSoon("terminal handed the keyboard back, 300 ms later");
                return;
            case SummonAction.Hide:
                HideDock();
                return;
        }
        if (RefuseInCompact("terminal"))
        {
            return;
        }
        switch (summon.Action)
        {
            case SummonAction.FocusShown:
                SetDockVisible(true);
                FocusTerminal();
                break;
            case SummonAction.ShowSession when summon.Session is { } session:
                SetDockVisible(true);
                _terminal.Show(session);
                FocusTerminal();
                break;
            default:
                await OpenInDockAsync(null, PaneFolder(_active), _active, requestId);
                break;
        }
    }

    private async Task NewTerminalAsync(string? profile, string? folder, int? pane, string requestId)
    {
        if (RefuseInCompact("terminal"))
        {
            return;
        }
        var bound = pane ?? _active;
        await OpenInDockAsync(profile, folder ?? PaneFolder(bound), bound, requestId);
    }

    // A new session bound to the pane, shown with the keyboard; when none could start, an empty dock goes away.
    private async Task OpenInDockAsync(string? profile, string? folder, int pane, string requestId)
    {
        SetDockVisible(true);
        if (_terminal.Tabs.Count == 0)
        {
            Dock.ShowStarting();
        }
        if (await _terminal.OpenAsync(profile, folder, pane, requestId) is null)
        {
            if (_terminal.Tabs.Count == 0)
            {
                // The status bar says why; an empty dock would only be in the way.
                SetDockVisible(false);
            }
            return;
        }
        FocusTerminal();
    }

    // Alt+[ and Alt+] in the terminal: the shown tab changes, the active pane does not, and a terminal
    // that had the keyboard keeps it in the tab that comes to the front.
    private Task CycleTerminalTabs(int step)
    {
        if (_terminal.ShowNext(step) && _dockVisible && Dock.HasTerminalFocus)
        {
            _terminal.FocusPage();
        }
        return Task.CompletedTask;
    }

    // terminal.close: {"session": n}, else the shown tab. Closed from the terminal (Ctrl+Shift+W), the tab that comes
    // to the front gets the keyboard: the closed one's xterm had it inside the page.
    private async Task CloseTerminalAsync(CommandInvocation invocation)
    {
        if ((CommandArgs.Number(invocation.Args, "session") ?? _terminal.Shown?.SessionId) is not { } session)
        {
            return;
        }
        var hadKeyboard = _dockVisible && Dock.HasTerminalFocus;
        await _terminal.CloseAsync(session);
        if (hadKeyboard && _dockVisible && _terminal.Shown is not null)
        {
            _terminal.FocusPage();
        }
    }

    // terminal.setMode: {"session": n, "mode": "locked"|"linked"}; without them, the shown tab's mode flips.
    private Task SetTerminalModeAsync(CommandInvocation invocation)
    {
        var session = CommandArgs.Number(invocation.Args, "session") ?? _terminal.Shown?.SessionId;
        if (session is not { } id || _terminal.Tabs.FirstOrDefault(t => t.SessionId == id) is not { } tab)
        {
            ShowNotice("No terminal is open.");
            return Task.CompletedTask;
        }
        var mode = TerminalBinding.ParseMode(CommandArgs.Text(invocation.Args, "mode"))
            ?? (tab.Mode == TerminalMode.Linked ? TerminalMode.Locked : TerminalMode.Linked);
        return _terminal.SetModeAsync(id, mode, invocation.RequestId);
    }

    private async Task ReloadTerminalAsync()
    {
        Dock.HideStopped();
        if (!await _terminal.ReloadAsync())
        {
            Dock.ShowStopped("The terminal could not start again", "WebView2 did not load its page.");
            return;
        }
        FocusTerminal();
    }

    // Ctrl+Shift+V in the terminal: the window reads the clipboard, since the page may not.
    private async Task PasteIntoTerminalAsync(ulong session)
    {
        switch (await ReadClipboardTextAsync())
        {
            case null:
                ShowNotice("The clipboard could not be read.", isError: true);
                break;
            case { Length: > TerminalPageMessages.MaxIncomingLength }:
                ShowNotice("The clipboard's text is too long to paste into the terminal (over 1 MiB).", isError: true);
                break;
            case { Length: > 0 } text:
                _terminal.Paste(session, text);
                break;
            default:
                Diag.Info(TerminalTarget, "terminal paste: the clipboard holds no text", new LogField("session_id", session));
                break;
        }
    }

    private string? PaneFolder(int pane) => _panes[pane].Path.Length > 0 ? _panes[pane].Path : null;

    private void FocusTerminal()
    {
        if (!_dockVisible)
        {
            return;
        }
        // A dock that was collapsed a moment ago has no size yet, and a WebView2 without one takes no focus.
        MainColumn.UpdateLayout();
        // A dock shown a moment ago is visible to WebView2 only from the next frame, and the keys
        // may stay in the window: GiveKeysToPage checks and hands them over again (PageKeyboard).
        GiveKeysToPage(Dock.TerminalPage.View, "terminal", () => _dockVisible, _terminal.FocusPage);
    }

    private void HideDock()
    {
        _terminalHandedBackTo = null;
        SetDockVisible(false);
        // The collapsed page cannot keep the keyboard; the pane takes it.
        FocusActivePane();
        LogKeyboardSoon("terminal hidden, 300 ms later");
    }

    private void SetDockVisible(bool visible)
    {
        _dockVisible = visible;
        Dock.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BottomSplitter.Visibility = visible && _dockPlacement == DockPlacement.Bottom ? Visibility.Visible : Visibility.Collapsed;
        RightSplitter.Visibility = visible && _dockPlacement == DockPlacement.Right ? Visibility.Visible : Visibility.Collapsed;
        ApplyDockSize();
        TerminalIcon.Foreground = ThemeResources.Brush(visible ? "CbAccentBrush" : "CbTextSecondaryBrush");
        UpdateRail();
    }

    private void ApplyDockPlacement(DockPlacement placement)
    {
        if (placement != _dockPlacement)
        {
            // A height the user dragged to means nothing as a width: each placement keeps its own.
            _dockUserSize = _settings.DockSize(placement);
        }
        _dockPlacement = placement;
        var bottom = placement == DockPlacement.Bottom;
        Grid.SetRow(Dock, bottom ? 2 : 0);
        Grid.SetColumn(Dock, bottom ? 0 : 2);
        // Under hairlines the line is on the edge that meets the panes.
        Dock.ApplyMetrics(bottom);
        SetDockVisible(_dockVisible);
        UpdateDockHeader();
    }

    private void ApplyDockSize()
    {
        var bottom = _dockPlacement == DockPlacement.Bottom;
        var size = _dockVisible ? CurrentDockSize() : 0;
        var gap = DockLayout.GapWith(WindowMetrics.Current);
        BottomGapRow.Height = new GridLength(_dockVisible && bottom ? gap : 0);
        BottomDockRow.Height = new GridLength(_dockVisible && bottom ? size : 0);
        RightGapColumn.Width = new GridLength(_dockVisible && !bottom ? gap : 0);
        RightDockColumn.Width = new GridLength(_dockVisible && !bottom ? size : 0);
        // A gap too narrow to grab (a theme's gap 0) keeps a 6 px handle, laid over the edges it joins.
        var reach = Math.Max(0, (6 - gap) / 2);
        BottomSplitter.Margin = new Thickness(0, -reach, 0, -reach);
        RightSplitter.Margin = new Thickness(-reach, 0, -reach, 0);
    }

    private double DockSpace() => _dockPlacement == DockPlacement.Bottom ? MainColumn.ActualHeight : MainColumn.ActualWidth;

    private double CurrentDockSize() => _dockUserSize is { } size
        ? DockLayout.Clamp(_dockPlacement, size, DockSpace(), WindowMetrics.Current)
        : DockLayout.DefaultSize(_dockPlacement, DockSpace(), WindowMetrics.Current);

    private void ResizeDock(double size)
    {
        _dockUserSize = DockLayout.Clamp(_dockPlacement, size, DockSpace(), WindowMetrics.Current);
        ApplyDockSize();
    }

    private void StartDockDrag()
    {
        _dockDragging = true;
        _dockDragStart = CurrentDockSize();
    }

    // One write per drag, when it ends, so the size survives a restart (ui.dockSize, docs/config.md).
    private void EndDockDrag()
    {
        _dockDragging = false;
        if (_dockUserSize is not { } size)
        {
            return;
        }
        var value = DockLayout.ToSetting(size);
        if (_dockKnown.TryGetValue(_dockPlacement, out var known) && known == value)
        {
            return;
        }
        _dockKnown[_dockPlacement] = value;
        _ = _settingsWriter.SetAsync(DockLayout.ConfigKey(_dockPlacement), value);
    }

    // ui.dockSize at start, and after a hand edit of cabinetos.json. null is the design's size;
    // CurrentDockSize keeps any value within the design's limits.
    private void ApplyStoredDockSize(UiSettings settings)
    {
        var stored = settings.DockSize(_dockPlacement);
        uint? value = stored is { } pixels ? DockLayout.ToSetting(pixels) : null;
        if (_dockKnown.TryGetValue(_dockPlacement, out var known) && known == value)
        {
            return;
        }
        _dockKnown[_dockPlacement] = value;
        if (_dockDragging)
        {
            return;
        }
        _dockUserSize = stored;
        ApplyDockSize();
    }

    private void UpdateDockHeader()
    {
        Dock.SetTabs(_terminal.Tabs, _terminal.Shown);
        Dock.SetCaption(_terminal.Caption());
    }

    // The snapshot aid's terminal-state:<label> step: the tabs as the header shows them, who has the keyboard.
    private void LogTerminalState(string label) =>
        Diag.Info(TerminalTarget, "terminal state", new LogField("label", label), new LogField("tabs", _terminal.Describe()),
            new LogField("shown", _terminal.Shown?.SessionId), new LogField("dock", _dockVisible),
            new LogField("terminal_keyboard", _dockVisible && Dock.HasTerminalFocus), new LogField("active_pane", TerminalBinding.PaneName(_active)),
            new LogField("caption", _terminal.Caption()));

    // The theme's terminal colours (docs/themes.md, "terminal"); before the core sent a theme,
    // the design's text colour on a clear background with the accent's cursor.
    private void SendTerminalTheme()
    {
        const string font = "'Cascadia Code', 'Cascadia Mono', Consolas, monospace";
        if (_themes.Current is { Terminal: var terminal })
        {
            _terminal.SetTheme(TerminalPageMessages.Theme(terminal.Background, terminal.Foreground, terminal.Cursor, terminal.Selection,
                font, 12, terminal.Ansi, TerminalSpacing.From(WindowMetrics.Current)));
            return;
        }
        var dark = RootGrid.ActualTheme != ElementTheme.Light;
        var accent = AccentColor(dark);
        _terminal.SetTheme(TerminalPageMessages.Theme(
            background: "#00000000",
            foreground: dark ? "#FFFFFFE6" : "#000000E4",
            cursor: Css(accent, 0xFF),
            selection: Css(accent, 0x4D),
            fontFamily: font,
            fontSize: 12));
    }

    // The design's accent (#60CDFF is the default blue's Light2): Light2 on dark, Dark1 on light.
    private static Color AccentColor(bool dark) =>
        Application.Current.Resources.TryGetValue(dark ? "SystemAccentColorLight2" : "SystemAccentColorDark1", out var value) && value is Color color
            ? color
            : Color.FromArgb(0xFF, 0x60, 0xCD, 0xFF);

    private static string Css(Color color, byte alpha) => $"#{color.R:X2}{color.G:X2}{color.B:X2}{alpha:X2}";
}
