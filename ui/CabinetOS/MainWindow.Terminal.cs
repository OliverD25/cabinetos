using System.Globalization;
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
    // terminal.split as the window knows it: its own last write, or what the file said. The dock shows two halves while
    // it is on and the dock sits under the panes (TerminalSplitLayout.Active); the signature is what the dock's
    // columns were laid out for, so a layout that did not change redraws nothing.
    private bool _splitSetting;
    private string? _dockSplitSignature;
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
        // The halves lie under the panes and follow them: when a pane's place or width changes (the window, the sidebar,
        // the second pane coming or going), the halves are laid out again.
        PanesGrid.SizeChanged += (_, _) => ApplySplitLayout();
        LeftSide.SizeChanged += (_, _) => ApplySplitLayout();
        RightSide.SizeChanged += (_, _) => ApplySplitLayout();
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
        _router.RegisterUiHandler("terminal.toggleSplit", ToggleSplitAsync);
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
        _terminal.ShownPaneFor(_active),
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
                // In the split the half under this pane gets the keyboard, whichever half had it.
                if (_terminal.Split && _terminal.ShownIn(_active) is { } half)
                {
                    _terminal.Show(half.SessionId);
                }
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
        // Ctrl+Shift+T acts for the active pane, also while the other half has the keyboard; a half's own "+" names its pane.
        var bound = pane ?? TerminalSplitLayout.NewTabPane(_dual, _active);
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
        // The split holds only under the panes; beside them the dock shows one view (the setting stays).
        ApplySplitLayout();
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

    // The header: the one view's tabs and caption, or in the split each half's own, with only its pane's sessions.
    private void UpdateDockHeader()
    {
        var halves = _terminal.Split ? _terminal.Halves() : null;
        Dock.ApplySplit(halves);
        _dockSplitSignature = SplitSignature(halves);
        if (halves is null)
        {
            Dock.SetTabs(_terminal.Tabs, _terminal.Shown);
            var caption = _terminal.Caption();
            Dock.SetCaption(caption.Text, caption.Tip);
            return;
        }
        foreach (var half in halves)
        {
            var shown = _terminal.ShownIn(half.Pane);
            Dock.SetHalfTabs(half.Pane, [.. _terminal.Tabs.Where(t => t.Pane == half.Pane)], shown);
            var caption = TerminalController.CaptionFor(shown);
            Dock.SetHalfCaption(half.Pane, caption.Text, caption.Tip);
        }
    }

    private static string SplitSignature(IReadOnlyList<SplitHalf>? halves) =>
        halves is null ? "" : string.Join('|', halves.Select(h => string.Create(CultureInfo.InvariantCulture, $"{h.Pane}:{h.X:0.#}:{h.Width:0.#}")));

    // ----- The split mirror (docs/ui.md, "The terminal") -----

    // terminal.toggleSplit: Ctrl+\ in the terminal, or the palette. {"split": true|false} sets it instead of flipping it.
    // The dock splits only under the panes, so beside them it says so and the setting stays as it is.
    private Task ToggleSplitAsync(CommandInvocation invocation)
    {
        if (RefuseInCompact("terminal"))
        {
            return Task.CompletedTask;
        }
        if (_dockPlacement != DockPlacement.Bottom)
        {
            ShowNotice("The terminal splits only when the dock is under the panes (ui.layout classic or rail).");
            return Task.CompletedTask;
        }
        var wanted = SplitArgument(invocation.Args) ?? !_splitSetting;
        var hadKeyboard = _dockVisible && Dock.HasTerminalFocus;
        SetSplit(wanted);
        if (hadKeyboard)
        {
            // The terminal that had the keyboard moved on the page, not away: its half keeps it.
            _terminal.FocusPage();
        }
        return Task.CompletedTask;
    }

    private static bool? SplitArgument(JsonElement? args) =>
        args is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("split", out var split)
            && split.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? split.GetBoolean()
            : null;

    // The user's toggle: applied at once, then written (terminal.split); the configuration the core sends back meanwhile
    // does not flip it back (IsOwnWrite). Logged with the halves' widths.
    private void SetSplit(bool on)
    {
        if (on == _splitSetting)
        {
            return;
        }
        _splitSetting = on;
        ApplySplitLayout();
        LogSplit(on);
        _ = PersistAsync(SplitKey, on);
    }

    private const string SplitKey = "terminal.split";

    // terminal.split at start, and after a hand edit of cabinetos.json or another window's toggle.
    private void ApplyStoredSplit(UiSettings settings, UiSettings previous, bool firstStart)
    {
        if (!firstStart && settings.TerminalSplit == previous.TerminalSplit)
        {
            return;
        }
        // The window's own toggle is on its way to the file: the configuration that comes meanwhile may still hold the old value.
        if (IsOwnWrite(SplitKey, settings.TerminalSplit) || settings.TerminalSplit == _splitSetting)
        {
            return;
        }
        _splitSetting = settings.TerminalSplit;
        ApplySplitLayout();
        if (!firstStart)
        {
            Diag.Info(TerminalTarget, "the terminal split follows the configuration", new LogField("split", _splitSetting));
            LogSplit(_splitSetting);
        }
    }

    // Measures the panes and gives the terminal and the dock their layout: where each pane lies across the dock decides
    // where each half goes. Runs when a pane's place or width changes, the setting changes, or the dock changes place.
    private void ApplySplitLayout()
    {
        if (_terminal is null)
        {
            return;
        }
        var bottom = _dockPlacement == DockPlacement.Bottom;
        var inset = Dock.BodyInset;
        var left = new PaneSpan(LeftSide.ActualOffset.X - inset, LeftSide.ActualWidth);
        var right = new PaneSpan(RightSide.ActualOffset.X - inset, RightSide.ActualWidth);
        var width = Math.Max(0, PanesGrid.ActualWidth - Dock.BodyInsets);
        _terminal.SetSplit(TerminalSplitLayout.Active(_splitSetting, bottom), _dual, left, right, width);
        var halves = _terminal.Split ? _terminal.Halves() : null;
        if (SplitSignature(halves) != _dockSplitSignature)
        {
            UpdateDockHeader();
        }
    }

    // "terminal split": the split turned on or off, the halves (pane:x:width, from the page's left edge) and the panes
    // (x:width, in the same measure), in device-independent pixels, so a reader judges the halves against the panes.
    private void LogSplit(bool on)
    {
        var inset = Dock.BodyInset;
        string Pane(FrameworkElement side) => string.Create(CultureInfo.InvariantCulture, $"{side.ActualOffset.X - inset:0.#}:{side.ActualWidth:0.#}");
        Diag.Info(TerminalTarget, "terminal split", new LogField("split", on), new LogField("effective", _terminal.Split), new LogField("dual", _dual),
            new LogField("halves", _terminal.Split ? SplitSignature(_terminal.Halves()) : "none"),
            new LogField("left_session", _terminal.ShownIn(0)?.SessionId), new LogField("right_session", _terminal.ShownIn(1)?.SessionId),
            new LogField("left_pane", Pane(LeftSide)), new LogField("right_pane", _dual ? Pane(RightSide) : "none"));
    }

    // The snapshot aid's terminal-state:<label> step: the tabs as the header shows them, who has the keyboard, and the
    // split: each half's shown session and place on the window, and each pane's, so a test judges the halves against the panes.
    private void LogTerminalState(string label)
    {
        var fields = new List<LogField>
        {
            new("label", label), new("tabs", _terminal.Describe()), new("shown", _terminal.Shown?.SessionId), new("dock", _dockVisible),
            new("terminal_keyboard", _dockVisible && Dock.HasTerminalFocus), new("active_pane", TerminalBinding.PaneName(_active)),
            new("caption", _terminal.Caption().Text), new("folder", _terminal.Shown?.Folder),
            new("split", _terminal.Split), new("split_setting", _splitSetting), new("dual", _dual),
            new("keyboard_half", _terminal.Split ? TerminalBinding.PaneName(_terminal.KeyboardPane) : null),
        };
        if (_dockVisible)
        {
            IReadOnlyList<SplitHalf> halves = _terminal.Split ? _terminal.Halves() : [];
            foreach (var pane in new[] { 0, 1 })
            {
                var name = TerminalBinding.PaneName(pane);
                var side = pane == 1 ? RightSide : LeftSide;
                if (pane == 0 || _dual)
                {
                    var (x, w) = AcrossWindow(side);
                    fields.Add(new($"{name}_pane_x", x));
                    fields.Add(new($"{name}_pane_width", w));
                }
                if (halves.FirstOrDefault(h => h.Pane == pane) is not { } half)
                {
                    continue;
                }
                var header = Dock.HeaderOf(pane);
                var (hx, hw) = AcrossWindow(header);
                fields.Add(new($"{name}_half_x", hx));
                fields.Add(new($"{name}_half_width", hw));
                fields.Add(new($"{name}_half_session", half.Session));
                fields.Add(new($"{name}_half_tabs", string.Join(",", half.Tabs)));
                fields.Add(new($"{name}_half_hint", half.Empty));
                fields.Add(new($"{name}_badge", header.BadgeColors.Half));
            }
        }
        Diag.Info(TerminalTarget, "terminal state", [.. fields]);
    }

    // Where an element lies on the window, in device-independent pixels: its left edge and its width.
    private (double X, double Width) AcrossWindow(FrameworkElement element)
    {
        var box = element.TransformToVisual(RootGrid).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return (Math.Round(box.X, 1), Math.Round(box.Width, 1));
    }

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
