using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Files;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Jobs;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Platform;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Search;
using CabinetOS.Core.Settings;
using CabinetOS.Core.Tabs;
using CabinetOS.Core.Terminal;
using CabinetOS.Services;
using CabinetOS.ViewModels;
using CabinetOS.Views;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace CabinetOS;

/// <summary>
/// The window (design views A, B, D and E). It is the composition root: it
/// owns the connection to the core, the command router, the key state
/// machine, the panes, the palette, the transfers and the context menu, and
/// turns every button, key and menu choice into a command ID for the router
/// (brief §5).
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string Target = "cabinetos_ui::shell";

    private readonly CoreSession _session = new();
    private readonly CommandRouter _router;
    private readonly ChordStateMachine _keys = new(() => Environment.TickCount64);
    private readonly PaneModel[] _panes;
    private readonly FilePane[] _paneViews;
    private readonly IconCache _icons;
    private readonly TintedMicaBackdrop _backdrop = new();
    private readonly SidebarModel _sidebar = new();
    private readonly PaletteModel _palette;
    private readonly TransferCenter _transfers;
    private readonly FileClipboard _clipboard = new();
    private readonly SettingsWriter _settingsWriter;
    private readonly Dictionary<string, bool> _pendingSettings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unavailable = new(StringComparer.Ordinal);
    private readonly DispatcherQueueTimer _chordTimer;
    private readonly DispatcherQueueTimer _noticeTimer;
    private readonly DispatcherQueueTimer _speedTimer;
    private readonly WindowArgs _args;
    private readonly FrameMonitor? _frames;
    private readonly bool _selfTestCrash;
    private readonly string? _toolsDir;
    private UiSettings _settings = UiSettings.Defaults;

    // How often the configuration was read, and how often the core found an error in the file: the snapshot aid's
    // until:config and until:config-error wait for the next one.
    private int _configReads;
    private int _configErrors;
    private ShellState _shell = ShellState.Empty;
    private bool _dual = true;
    private bool _sidebarOpen = true;
    private int _active;
    private bool _closing;
    private bool _closed;
    private bool _started;
    private bool _volumesLogged;
    private ContentDialog? _openDialog;
    private bool _systemClipboardHolds;
    private readonly Queue<DateTime> _restarts = new();

    /// <summary>
    /// Creates the window; the core starts once the content is loaded.
    /// <paramref name="args"/>: <c>--path</c> is the left pane's first folder
    /// (a new window's, from <c>window.new</c>); <c>--tools-dir</c> is a
    /// folder of Tool Extensions read before the installed ones, for writing
    /// a tool.
    /// </summary>
    public MainWindow(WindowArgs args)
    {
        InitializeComponent();
        _args = args;
        _selfTestCrash = args.SelfTestCrash;
        _toolsDir = args.ToolsDir;
        _router = new CommandRouter(_session);
        // Both panes share what the core said per extension, and the icons.
        var known = new ExtensionDetails();
        _icons = new IconCache(_session);
        _panes = [new PaneModel(0, _session, known, _icons), new PaneModel(1, _session, known, _icons)];
        _paneViews = [LeftPane, RightPane];
        _icons.Loaded += key =>
        {
            foreach (var view in _paneViews)
            {
                view.RefreshIcon(key);
            }
        };
        _palette = new PaletteModel(_session, _router);
        _transfers = new TransferCenter(_session);
        _settingsWriter = new SettingsWriter(_session);

        SetUpWindow();
        _chordTimer = DispatcherQueue.CreateTimer();
        _chordTimer.IsRepeating = false;
        _chordTimer.Tick += (_, _) => _keys.ExpireIfDue();
        _noticeTimer = DispatcherQueue.CreateTimer();
        _noticeTimer.IsRepeating = false;
        _noticeTimer.Interval = TimeSpan.FromSeconds(5);
        _noticeTimer.Tick += (_, _) => NoticeText.Text = "";
        // The speed graph's samples: the design's 40 points, one every 500 ms.
        _speedTimer = DispatcherQueue.CreateTimer();
        _speedTimer.Interval = TimeSpan.FromMilliseconds(500);
        _speedTimer.Tick += (_, _) => _transfers.SampleSpeeds();

        for (var i = 0; i < _panes.Length; i++)
        {
            var pane = _panes[i];
            var view = _paneViews[i];
            view.Model = pane;
            view.RunCommand = (id, args, trigger) => _router.ExecuteAsync(id, args, trigger);
            view.Activated += OnPaneActivated;
            view.ContextMenuRequested += OnContextMenuRequested;
            pane.PropertyChanged += OnPaneChanged;
            pane.Notice += text => ShowNotice(text, isError: true);
            pane.Listed += OnPaneListed;
        }
        _panes[0].IsActive = true;

        SidebarView.Model = _sidebar;
        SidebarView.Navigate += path => _ = _router.ExecuteAsync("go.toPath", CommandArgs.With("path", path), "sidebar");
        SidebarView.UnpinRequested += path => _ = _router.ExecuteAsync("sidebar.unpin", CommandArgs.With("path", path), "sidebar");
        SetPinnedFolders();

        SetUpThemes();
        SetUpTerminal();
        SetUpQuickSearch();
        SetUpSearch();
        SetUpPlugins();
        SetUpTools();
        SetUpTabs();
        SetUpShell();
        SetUpContextMenu();
        SetUpFind();
        SetUpQuickOpen();
        SetUpPreview();
        SetUpMarket();
        SetUpRail();
        SetUpColumns();
        SetUpCompact();

        Palette.Model = _palette;
        Palette.RunCommand = (id, args, trigger) => _router.ExecuteAsync(id, args, trigger);
        Palette.ReturnFocus = ReturnFocusAfterPalette;
        PromptView.ReturnFocus = FocusActivePane;
        _palette.KeymapUpdated += keymap => ApplyKeymap(Keymap.From(keymap));

        TransferView.Center = _transfers;
        TransferView.RunCommand = (id, args, trigger) => _router.ExecuteAsync(id, args, trigger);
        _transfers.Changed += UpdateTransfers;
        TransferPill.Click += (_, _) => _ = _router.ExecuteAsync("transfer.restore", trigger: "button");
        PillFill.Scale = new Vector3(0, 1, 1);
        PillFill.CenterPoint = new Vector3(0, 2, 0);

        FileMenu.RunCommand = (id, args, trigger) => _router.ExecuteAsync(id, args, trigger);
        FileMenu.Closed += FocusActivePane;
        FkeyBar.RunCommand = (id, args, trigger) => _router.ExecuteAsync(id, args, trigger);

        DualButton.Click += (_, _) => _ = _router.ExecuteAsync("view.toggleDualPane", trigger: "button");
        PaletteButton.Click += (_, _) => _ = _router.ExecuteAsync("palette.show", trigger: "button");
        PaletteKeycap.Click += (_, _) => _ = _router.ExecuteAsync("palette.show", trigger: "button");

        RegisterCommands();
        SetUpDiagnostics();
        SetUpUpdates();
        _keys.PendingChanged += UpdateChordIndicator;
        RootGrid.PreviewKeyDown += OnPreviewKeyDown;
        RootGrid.SizeChanged += (_, e) => UpdateWidths(e.NewSize.Width);
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionColors();
        _session.EventReceived += OnCoreEvent;
        _session.Lost += reason => _ = OnCoreLostAsync(reason);
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                _keys.Reset();
            }
            else if (_panes[_active].Path.Length > 0)
            {
                // A branch switched in a terminal while the window was behind shows when it comes back.
                _ = UpdateWorkspaceAsync();
            }
        };
        RootGrid.Loaded += OnLoaded;
        AppWindow.Closing += OnClosing;

        if (FrameMonitor.Enabled)
        {
            _frames = new FrameMonitor(() => _session.CoreProcessId);
            _frames.Start();
        }
        ApplyDual(true);
        UpdateStatus();
        UpdateNavigationButtons();
        // The default look's sizes until the core's theme arrives (the base text among them).
        LayOutWithMetrics();
    }

    private PaneModel Active => _panes[_active];

    private PaneModel Other => _panes[1 - _active];

    // ----- Window -----

    private void SetUpWindow()
    {
        // Mica that a theme can tint (MicaBackdrop has no tint).
        SystemBackdrop = _backdrop;
        ExtendsContentIntoTitleBar = true;
        // The top row is the drag area; its controls are taken out of it (UpdateDragRegions).
        SetTitleBar(TopBar);
        AppWindow.Title = "CabinetOS";
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = (int)(area.Width * 0.8);
        var height = (int)(area.Height * 0.82);
        AppWindow.MoveAndResize(new RectInt32(area.X + ((area.Width - width) / 2), area.Y + ((area.Height - height) / 2), width, height));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateCaptionColors();
        if (AppWindow.Presenter is OverlappedPresenter presenter && RootGrid.XamlRoot is { } root)
        {
            // 600 px, so the window can be narrower than the 640 px under which the command center hides (Phase 16).
            presenter.PreferredMinimumWidth = (int)(600 * root.RasterizationScale);
            presenter.PreferredMinimumHeight = (int)(480 * root.RasterizationScale);
        }
        FitCaptionSpace();
        LayOutTopRow();
        if (!_started)
        {
            _started = true;
            if (RootGrid.XamlRoot is { } xamlRoot)
            {
                _icons.Size = IconSizes.For(xamlRoot.RasterizationScale);
                xamlRoot.Changed += OnXamlRootChanged;
            }
            _ = StartAsync();
        }
    }

    // A move to a screen with another scale: rows ask for the icon size that is sharp there.
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        var size = IconSizes.For(sender.RasterizationScale);
        if (size != _icons.Size)
        {
            _icons.Size = size;
            foreach (var view in _paneViews)
            {
                view.RefreshDetails();
            }
        }
    }

    private void UpdateCaptionColors()
    {
        var titleBar = AppWindow.TitleBar;
        var dark = RootGrid.ActualTheme != ElementTheme.Light;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = dark ? Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0xE4, 0, 0, 0);
        titleBar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x09, 0, 0, 0);
        titleBar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        titleBar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x06, 0, 0, 0);
    }

    private void UpdateWidths(double windowWidth)
    {
        _windowWidth = windowWidth;
        // The design's clamp(180px, 20%, 224px), or the theme's (in the rail layout the width the user dragged the divider to),
        // and clamp(120px, 22%, 240px). The divider sets the width itself while it is dragged.
        if (!_sidebarDragging)
        {
            SidebarColumn.Width = SidebarWidthFor(windowWidth);
        }
        FitCaptionSpace();
        LayOutTopRow();
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closed)
        {
            return;
        }
        // The window hides at once; the core gets its shutdown without the UI thread waiting.
        args.Cancel = true;
        await CloseWindowAsync();
    }

    // The close button's way out, also a restart's into a new version (MainWindow.Update.cs).
    private async Task CloseWindowAsync()
    {
        if (_closing)
        {
            return;
        }
        _closing = true;
        AppWindow.Hide();
        Diag.Info(Target, "window closing");
        // Closing must stay quick: what the core does not answer within a second is not waited for.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Task.WhenAll(SaveLastPathsAsync(), FlushTabsAsync(deadline.Token));
        await _session.StopAsync();
        StartRestart();
        foreach (var pane in _panes)
        {
            pane.Release();
        }
        _closed = true;
        Close();
    }

    // ----- Startup, events and restarts -----

    private async Task StartAsync()
    {
        ShowNotice("Starting the core…");
        try
        {
            await _session.StartAsync();
        }
        catch (CoreLaunchException error)
        {
            Diag.Error(Target, "the core could not start", new LogField("error", error.Message));
            await ShowStartFailureAsync(error.Message);
            return;
        }
        ShowNotice("");
        await LoadAsync(firstStart: true);
        if (_selfTestCrash)
        {
            CrashForSelfTest();
        }
        if (DevSnapshots.Folder is not null)
        {
            await TakeSnapshotsAsync();
        }
    }

    // Fails the way a real bug in an async handler would: after an await, on the UI thread.
    private static async void CrashForSelfTest()
    {
        await Task.Yield();
        Diag.Info(Target, "about to crash (self-test)");
        throw new InvalidOperationException("self-test crash (--self-test-crash)");
    }

    private async Task TakeSnapshotsAsync()
    {
        await Task.Delay(1500);
        foreach (var step in DevSnapshots.Steps())
        {
            switch (step.Kind)
            {
                case "scroll" when ScrollStep.TryParse(step.Argument, out var scroll):
                    await ScrollForSnapshotAsync(scroll);
                    break;
                case "cmd" or "cmd-nowait":
                    var space = step.Argument.IndexOf(' ');
                    JsonElement? args = space < 0 ? null : JsonDocument.Parse(step.Argument[(space + 1)..]).RootElement.Clone();
                    var run = _router.ExecuteAsync(space < 0 ? step.Argument : step.Argument[..space], args, "snapshot");
                    // A command that waits for the user (a dialog, a rename) must not hold the steps up.
                    if (step.Kind == "cmd")
                    {
                        await run;
                    }
                    break;
                case "selectall":
                    Active.Selection.SelectAll();
                    break;
                case "rename":
                    _paneViews[_active].CommitRename(step.Argument);
                    break;
                case "dismiss":
                    foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot))
                    {
                        if (popup.Child is ContentDialog dialog)
                        {
                            dialog.Hide();
                        }
                    }
                    break;
                case "path":
                    await _router.ExecuteAsync("go.toPath", CommandArgs.With("path", step.Argument), "snapshot");
                    break;
                case "pane" when int.TryParse(step.Argument, out var pane) && pane is 0 or 1:
                    // GotFocus comes after the fact: let earlier focus changes land first.
                    _paneViews[pane].Focus(FocusState.Programmatic);
                    await Task.Delay(150);
                    SetActive(pane);
                    break;
                case "select":
                    var index = Active.View?.IndexOfName(step.Argument) ?? -1;
                    if (index >= 0)
                    {
                        Active.Selection.MoveTo(index, SelectMode.Single);
                        _paneViews[_active].ScrollToFocus();
                    }
                    break;
                case "menu":
                    // "menu:*" is the pane's empty space; "menu:" the focused row; else a row by name. A row outside
                    // the selection is selected first, as a right-click does.
                    OnContextMenuRequested(_paneViews[_active], MenuRowForStep(step.Argument), null);
                    break;
                case "menu-at" when TryParseMenuAt(step.Argument, out var menuRow, out var menuPoint):
                    // A right-click's point: the window's content DIPs, "menu-at:alpha.txt|300,200".
                    OnContextMenuRequested(_paneViews[_active], MenuRowForStep(menuRow), menuPoint);
                    break;
                case "shellmenu":
                    // Shift+right-click on the same rows; Windows' menu comes when the core answers.
                    OnShellMenuRequested(_paneViews[_active], MenuRowForStep(step.Argument), null);
                    await Task.Delay(1500);
                    break;
                case "shellmenu-at" when TryParseMenuAt(step.Argument, out var shellRow, out var shellPoint):
                    OnShellMenuRequested(_paneViews[_active], MenuRowForStep(shellRow), shellPoint);
                    await Task.Delay(1500);
                    break;
                case "menu-edit-drag":
                    // A row dragged onto another in the menu's edit mode, by their titles: "menu-edit-drag:Open|Copy to other pane".
                    var dragRows = step.Argument.Split('|');
                    if (dragRows.Length != 2 || !MenuEditorView.DragForSnapshot(dragRows[0], dragRows[1]))
                    {
                        Diag.Info("cabinetos_ui::snapshot", "no such rows to drag in the menu's edit mode", new LogField("rows", step.Argument));
                    }
                    await Task.Delay(300);
                    break;
                case "menu-edit-key":
                    // A key in the menu's edit mode, as the window passes a real one: "alt+down", "delete", "insert".
                    if (KeyCombo.TryParse(step.Argument, out var editKey))
                    {
                        MenuEditorView.HandleKey(editKey.Value);
                    }
                    await Task.Delay(300);
                    break;
                case "menu-click":
                    if (!_contextMenu.Click(step.Argument))
                    {
                        Diag.Info("cabinetos_ui::snapshot", "no such entry in the open context menu", new LogField("title", step.Argument));
                    }
                    await Task.Delay(300);
                    break;
                case "shellmenu-click":
                    if (!_windowsMenu.Click(step.Argument))
                    {
                        Diag.Info("cabinetos_ui::snapshot", "no such item in the open Windows menu", new LogField("text", step.Argument));
                    }
                    await Task.Delay(300);
                    break;
                case "type":
                    // Into the prompt in the palette's frame when one is shown, else the palette.
                    if (PromptView.IsOpen)
                    {
                        PromptView.Type(step.Argument);
                    }
                    else
                    {
                        Palette.TypeQuery(step.Argument);
                    }
                    break;
                case "accept":
                    PromptView.Accept();
                    break;
                case "drive" when step.Argument.Length == 1:
                    // A drive's letter in the open drive list (Alt+F1, Alt+F2).
                    PromptView.PressKey(step.Argument[0]);
                    break;
                case "quick":
                    // Letters typed in the active pane: quick search.
                    await QuickSearchForSnapshotAsync(step.Argument);
                    break;
                case "terminal":
                    // Typed into the shown shell as keys; {enter} is Enter.
                    await _terminal.TypeAsync(step.Argument.Replace("{enter}", "\r", StringComparison.Ordinal));
                    break;
                case "search":
                    // Typed into the Search view's field (the command bar's search field is gone since Phase 16).
                    SetSearchText(step.Argument);
                    break;
                case "shell":
                    LogShellState(step.Argument);
                    break;
                case "find" or "find-key":
                    await RunFindStepAsync(step.Kind, step.Argument);
                    break;
                case "quick-open" or "quick-open-key":
                    await RunQuickOpenStepAsync(step.Kind, step.Argument);
                    break;
                case "crash":
                    CrashPageForSnapshot(step.Argument);
                    break;
                case "dock" when double.TryParse(step.Argument, System.Globalization.CultureInfo.InvariantCulture, out var dockSize):
                    // A drag of the dock's splitter to this size: the same resize and save as the pointer's.
                    StartDockDrag();
                    ResizeDock(dockSize);
                    EndDockDrag();
                    break;
                case "mode":
                    // Windows' light or dark mode as the window sees it, through the path a change there takes.
                    ForceSystemMode(step.Argument);
                    break;
                case "size":
                    await SizeForSnapshotAsync(step.Argument);
                    break;
                case "fit" when double.TryParse(step.Argument, System.Globalization.CultureInfo.InvariantCulture, out var listHeight):
                    await FitListForSnapshotAsync(listHeight);
                    break;
                case "theme":
                    await SwitchThemeForSnapshotAsync(step.Argument);
                    break;
                case "pick" when int.TryParse(step.Argument, out var pick):
                    // The open theme picker's highlight on that row, as the pointer or a key moves it.
                    _picker.SetHighlight(pick);
                    break;
                case "layout":
                    LogLayoutForSnapshot(step.Argument);
                    break;
                case "click":
                    ClickForSnapshot(step.Argument);
                    break;
                case "focus":
                    LogFocusForSnapshot(step.Argument);
                    break;
                case "key":
                    await PressKeysForSnapshotAsync(step.Argument);
                    break;
                case "tooltip":
                    await OpenToolTipForSnapshotAsync(step.Argument);
                    break;
                case "tab":
                    await RunTabStepAsync(step.Argument);
                    break;
                case "tabs":
                    LogTabsForSnapshot(step.Argument);
                    break;
                case "preview" or "preview-key" or "plugin-event" or "drop" or "fake-command":
                    await RunPreviewStepAsync(step.Kind, step.Argument);
                    break;
                case "rail" or "rail-move" or "rail-state" or "divider" or "tree":
                    await RunRailStepAsync(step.Kind, step.Argument);
                    break;
                case "columns" or "column-drag" or "column-fit":
                    RunColumnStep(step.Kind, step.Argument);
                    break;
                case "open":
                    // Enter on a row by name in the active pane, as the user would.
                    var shown = Active.View?.IndexOfName(step.Argument) ?? -1;
                    if (shown >= 0)
                    {
                        Active.Selection.MoveTo(shown, SelectMode.Single);
                        await _router.ExecuteAsync("pane.openSelected", trigger: "snapshot");
                    }
                    break;
                case "until":
                    await WaitUntilAsync(step.Argument);
                    break;
                case "wait" when int.TryParse(step.Argument, out var milliseconds):
                    await Task.Delay(milliseconds);
                    break;
                case "shot":
                    await Task.Delay(400);
                    var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Select(p => p.Child).ToList();
                    Diag.Debug("cabinetos_ui::snapshot", "open popups", new LogField("children", string.Join(",", popups.Select(c => c?.GetType().Name))));
                    await DevSnapshots.RenderAsync(RootGrid, step.Argument, WebPages(), SnapshotBackdrop(),
                        popups.Where(c => c is ContentDialog or MenuFlyoutPresenter or FlyoutPresenter).ToList());
                    break;
            }
        }
    }

    // The snapshot aid's scroll:<pages> step: PageDown in the active pane, 30 times a second as a
    // held key repeats. A press that falls due during a slow frame is made at the next frame,
    // as queued key messages are. scroll:<pages>/<n> presses once every n frames instead: with the
    // display asleep Windows draws 30 frames a second, and one press every other frame is then the
    // rhythm of 30 presses a second on a 60 Hz display. The frames of the presses are one run
    // ("scroll run"); the second after the last press is another ("scroll settle"). docs/ui.md, "Scrolling".
    private async Task ScrollForSnapshotAsync(ScrollStep scroll)
    {
        const int PerSecond = 30;
        var pages = scroll.Pages;
        var frames = 0;
        var view = _paneViews[_active];
        view.Focus(FocusState.Programmatic);
        var firstFocus = Active.CurrentSelection.Focus;
        var done = new TaskCompletionSource();
        var pressed = 0;
        long started = 0;
        void OnFrame(object? sender, object e)
        {
            if (started == 0)
            {
                started = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            var due = scroll.EveryFrames > 0
                ? Math.Min(pages, (frames++ / scroll.EveryFrames) + 1)
                : Math.Min(pages, 1 + (int)(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds * PerSecond));
            while (pressed < due)
            {
                view.PageDownForSnapshot();
                pressed++;
            }
            if (pressed >= pages)
            {
                CompositionTarget.Rendering -= OnFrame;
                done.TrySetResult();
            }
        }
        _frames?.BeginRun($"scroll:{scroll}");
        CompositionTarget.Rendering += OnFrame;
        await done.Task;
        var rows = new[] { new LogField("pages", pages), new LogField("rows_moved", Active.CurrentSelection.Focus - firstFocus), new LogField("rows_per_page", view.RowsPerPageNow) };
        _frames?.EndRun(rows);
        _frames?.BeginRun("scroll settle");
        await Task.Delay(1000);
        _frames?.EndRun();
    }

    // Every WebView2 the window hosts, for the snapshot aid.
    private IEnumerable<WebViewHost> WebPages() => [Dock.TerminalPage, .. AllToolHosts().Select(h => h.Page)];

    // The snapshot aid's click: step: the first shown button or menu item (open menus included) with that
    // accessible name, pressed as assistive technology may press it: the keyboard moves to it, then its
    // automation peer invokes it.
    private void ClickForSnapshot(string name)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(RootGrid);
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot))
        {
            pending.Push(popup.Child);
        }
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element is UIElement { Visibility: Visibility.Collapsed })
            {
                continue;
            }
            // The peer's name is what UI Automation reports: the accessible name, else the button's text.
            if (element is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or MenuFlyoutItem
                && element is Control button
                && Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(button) is { } peer
                && peer.GetName() == name
                && peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke) is Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider invoke)
            {
                // Best effort: the window's chrome buttons refuse the keyboard (IsTabStop off, as under a real click), and the press goes on.
                _ = button.Focus(FocusState.Keyboard);
                invoke.Invoke();
                return;
            }
            for (var i = VisualTreeHelper.GetChildrenCount(element) - 1; i >= 0; i--)
            {
                pending.Push(VisualTreeHelper.GetChild(element, i));
            }
        }
        Diag.Info(Target, "snapshot click: no shown button has that name", new LogField("name", name));
    }

    // The snapshot aid's tooltip: step: opens the tooltip of the first element with that accessible
    // name, shown or not, as the end of a hover delay would, and logs whether it stayed open.
    private async Task OpenToolTipForSnapshotAsync(string name)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(RootGrid);
        FrameworkElement? owner = null;
        while (owner is null && pending.Count > 0)
        {
            var element = pending.Pop();
            if (element is FrameworkElement candidate && Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(candidate) == name)
            {
                owner = candidate;
                break;
            }
            for (var i = VisualTreeHelper.GetChildrenCount(element) - 1; i >= 0; i--)
            {
                pending.Push(VisualTreeHelper.GetChild(element, i));
            }
        }
        if (owner is null || ToolTipService.GetToolTip(owner) is not ToolTip tip)
        {
            Diag.Info("cabinetos_ui::snapshot", "tooltip: no element with that name has a tooltip object", new LogField("name", name));
            return;
        }
        tip.IsOpen = true;
        await Task.Delay(300);
        Diag.Info("cabinetos_ui::snapshot", "tooltip", new LogField("name", name), new LogField("owner_shown", OpenToolTips.IsShown(owner)),
            new LogField("open", tip.IsOpen));
        tip.IsOpen = false;
    }

    // The snapshot aid's focus: step: where the keyboard is, in the log (the label names the moment).
    private void LogFocusForSnapshot(string label)
    {
        var focused = RootGrid.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
        var name = focused is UIElement element ? Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(element) : "";
        var within = focused is null ? "nothing"
            : _openDialog is { } dialog && IsWithin(focused, dialog) ? "dialog"
            : IsWithin(focused, RootGrid) ? "window"
            : "elsewhere";
        Diag.Info("cabinetos_ui::snapshot", "keyboard focus", new LogField("label", label), new LogField("element", focused?.GetType().Name ?? "none"),
            new LogField("name", name), new LogField("x_name", (focused as FrameworkElement)?.Name ?? ""), new LogField("within", within));
        LogKeyboard(label);

        static bool IsWithin(DependencyObject element, DependencyObject container)
        {
            for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current == container)
                {
                    return true;
                }
            }
            return false;
        }
    }

    // The crash-isolation check: ends a page's browser process, as a crash would.
    // "terminal", or "tool:<id>".
    private void CrashPageForSnapshot(string which)
    {
        var page = which == "terminal"
            ? Dock.TerminalPage
            : _toolHosts.FirstOrDefault(h => h is not null && which == $"tool:{h.Tool.Manifest.Id}")?.Page;
        if (page is not { BrowserProcessId: > 0 and var pid })
        {
            Diag.Info(Target, "nothing to crash", new LogField("page", which));
            return;
        }
        Diag.Info(Target, "ending a page's browser process (snapshot step)", new LogField("page", which), new LogField("pid", pid));
        try
        {
            System.Diagnostics.Process.GetProcessById(pid).Kill();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Diag.Info(Target, "the browser process could not be ended", new LogField("error", error.Message));
        }
    }

    // Snapshot steps that wait for the core: "until:running" (a job moves bytes) or "until:conflict".
    private async Task WaitUntilAsync(string condition)
    {
        var configReads = _configReads;
        var configErrors = _configErrors;
        for (var waited = 0; waited < 20_000; waited += 100)
        {
            var met = condition switch
            {
                // The configuration was read again: an edit of the file, from outside, arrived.
                "config" => _configReads > configReads,
                "config-error" => _configErrors > configErrors,
                "conflict" => _transfers.Conflicts.Current is not null,
                "running" => _transfers.Shown is { State.Type: JobState.Running, Progress.FilesDone: > 0 },
                "terminal" => _terminal.Shown is { Pipe: not null },
                "search" => _search.Phase is SearchPhase.Done or SearchPhase.Failed,
                "tool" => AllToolHosts().Any(h => h.IsReady),
                _ => true,
            };
            if (met)
            {
                return;
            }
            await Task.Delay(100);
        }
    }

    private async Task LoadAsync(bool firstStart)
    {
        try
        {
            await ReadConfigAsync(firstStart);
            var theme = ReadThemeAsync();
            var keymap = ReadKeymapAsync();
            var commands = _router.RefreshAsync();
            var volumes = ReadVolumesAsync();
            var jobs = _transfers.LoadAsync();
            if (firstStart)
            {
                await OpenFirstFoldersAsync();
            }
            else
            {
                _icons.Reset();
                foreach (var pane in _panes)
                {
                    pane.ForgetListing();
                    if (pane.Path.Length > 0)
                    {
                        await pane.ReloadAsync();
                    }
                }
            }
            await Task.WhenAll(theme, keymap, commands, volumes, jobs);
            await RefreshPluginsAsync();
            await ReadUpdateStatusAsync();
        }
        catch (IOException error)
        {
            Diag.Warn(Target, "loading the session failed", new LogField("error", error.Message));
        }
    }

    private async Task OpenFirstFoldersAsync()
    {
        // A new window's folder (--path, from window.new) on the left; else the
        // folders of the last session (ui.lastPaths); else dual pane on first
        // start (PLAN.md, consistency check B): the profile on the left, its
        // Documents on the right when the profile lists one, else C:\.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var last = _shell.LastPaths;
        // The tabs of the last session (ui.tabs) come first; a new window's folder (--path) is one tab of its own.
        if (_args.Path is null && _savedTabs.Left.ToStrip() is { } savedLeft)
        {
            await OpenSavedTabsAsync(0, savedLeft, profile);
        }
        else if ((_args.Path is not { } start || !await _panes[0].NavigateAsync(start))
            && (last.Count < 1 || !await _panes[0].NavigateAsync(last[0])))
        {
            await _panes[0].NavigateAsync(profile);
        }
        // Where the right pane starts when nothing is saved, or what is saved is gone.
        var right = () =>
        {
            var left = _panes[0].View;
            var documents = string.Equals(_panes[0].Path, profile, StringComparison.OrdinalIgnoreCase) ? left?.IndexOfName("Documents") ?? -1 : -1;
            return documents >= 0 && left!.IsFolder(documents) ? DisplayFormat.Join(profile, "Documents") : @"C:\";
        };
        if (_savedTabs.Right.ToStrip() is { } savedRight)
        {
            await OpenSavedTabsAsync(1, savedRight, right());
        }
        else if (last.Count < 2 || !await _panes[1].NavigateAsync(last[1]))
        {
            await _panes[1].NavigateAsync(right());
        }
        _tabsStarted = true;
        UpdateTabRows();
        ScheduleWindowState();
        SaveTabsSoon();
        FocusActivePane();
    }

    private async Task ReadConfigAsync(bool firstStart)
    {
        var reply = await _session.RequestAsync(new GetConfigRequest());
        if (reply is ConfigReply config)
        {
            _configReads++;
            _shell = ShellState.FromConfig(config.Config);
            Diag.SetBundleConfig(config.Config);
            if (firstStart)
            {
                _savedTabs = TabsConfig.FromConfig(config.Config);
                _tabsWritten = _savedTabs.ToJson().GetRawText();
            }
            SetPinnedFolders();
            ApplySettings(UiSettings.FromConfig(config.Config), firstStart);
            ApplyMenuConfig(config.Config);
            _terminal.Profiles = TerminalProfiles.FromConfig(config.Config);
            Dock.SetProfiles(_terminal.Profiles);
        }
    }

    private async Task ReadKeymapAsync()
    {
        var reply = await _session.RequestAsync(new GetKeymapRequest());
        if (reply is KeymapReply keymap)
        {
            ApplyKeymap(Keymap.From(keymap.ToData()));
        }
    }

    // The window's keys (with its own bindings), and the few a terminal or a tool passes back to it.
    private void ApplyKeymap(Keymap keymap)
    {
        _keys.SetKeymap(keymap);
        _terminal.SetKeymap(keymap);
        ApplyToolKeys(keymap);
        FkeyBar.SetKeymap(keymap);
    }

    private async Task ReadVolumesAsync()
    {
        var reply = await _session.RequestAsync(new ListVolumesRequest());
        switch (reply)
        {
            case VolumesReply volumes:
                _sidebar.SetDrives(volumes.Volumes);
                break;
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                _sidebar.SetDrives(null);
                if (!_volumesLogged)
                {
                    _volumesLogged = true;
                    Diag.Info(Target, "the core cannot list volumes yet (list_volumes); the Drives section stays hidden");
                }
                break;
            case ErrorReply error:
                _sidebar.SetDrives(null);
                Diag.Info(Target, "list_volumes failed", new LogField("code", error.Code), new LogField("error", error.Message));
                break;
        }
    }

    private void ApplySettings(UiSettings settings, bool firstStart)
    {
        var previous = _settings;
        _settings = settings;
        ApplyHeavyLogging(settings.HeavyLogging, firstStart);
        if ((firstStart || settings.DualPane != previous.DualPane) && !IsOwnWrite(ShellState.DualPaneKey, settings.DualPane))
        {
            if (!firstStart)
            {
                // Another window, or a hand edit of cabinetos.json: this window follows and writes nothing back.
                Diag.Info(Target, "dual pane follows the configuration", new LogField("dual", settings.DualPane));
            }
            if (!HoldDualForCompact(settings.DualPane))
            {
                ApplyDual(settings.DualPane);
            }
        }
        if ((firstStart || settings.Sidebar != previous.Sidebar) && !IsOwnWrite(ShellState.SidebarKey, settings.Sidebar))
        {
            if (!firstStart)
            {
                Diag.Info(Target, "the sidebar follows the configuration", new LogField("sidebar", settings.Sidebar));
            }
            if (!HoldSidebarForCompact(settings.Sidebar))
            {
                ApplySidebar(settings.Sidebar);
            }
        }
        UpdateLayoutText();
        if (firstStart || settings.Layout != previous.Layout)
        {
            ApplyDockPlacement(DockLayout.PlacementFor(settings.Layout));
            ApplyRailLayout(settings.Layout == "rail");
        }
        ApplyStoredDockSize(settings);
        ApplyRailSettings(settings, previous, firstStart);
        ApplyColumnSettings(settings);
        ApplyCompactSettings(settings, previous);
        if (firstStart || settings.Selection != previous.Selection)
        {
            // panes.selection applies at once: the marks stay, only the keys mark differently.
            foreach (var pane in _panes)
            {
                pane.Selection.SetStyle(settings.Selection);
            }
        }
        ApplyFolderSizes(settings, previous, firstStart);
        foreach (var pane in _panes)
        {
            // What a pane without its own order (Ctrl+F3 to Ctrl+F6) is sorted by.
            pane.DefaultSort = new SortSpec(settings.SortKey, settings.SortDescending);
        }
        if (!firstStart && (settings.ShowHidden != previous.ShowHidden
            || settings.SortKey != previous.SortKey
            || settings.SortDescending != previous.SortDescending))
        {
            // The core applies panes.* to new listings; list both folders again.
            foreach (var pane in _panes.Where(p => p.Path.Length > 0))
            {
                _ = pane.ReloadAsync();
            }
        }
    }

    // While a toggle's own set_value is on its way, the configuration the core
    // sends meanwhile may still hold the old value: it must not flip the view back.
    private bool IsOwnWrite(string key, bool value)
    {
        if (!_pendingSettings.TryGetValue(key, out var pending))
        {
            return false;
        }
        if (pending == value)
        {
            _pendingSettings.Remove(key);
        }
        return true;
    }

    private async Task PersistAsync(string key, bool value)
    {
        _pendingSettings[key] = value;
        if (!await _settingsWriter.SetAsync(key, value))
        {
            _pendingSettings.Remove(key);
        }
    }

    private async Task SaveLastPathsAsync()
    {
        var paths = _panes.Select(p => p.Path).Where(p => p.Length > 0).ToList();
        if (paths.Count == 0 || paths.SequenceEqual(_shell.LastPaths, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }
        // Closing must stay quick: a core that does not answer within a second is not waited for.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await _settingsWriter.SetAsync(ShellState.LastPathsKey, paths, deadline.Token);
    }

    private void OnCoreEvent(CoreEvent coreEvent)
    {
        if (_closing)
        {
            // The core is being shut down: saving the last paths still brings a
            // config_changed, and reading the configuration now would only fail.
            return;
        }
        switch (coreEvent)
        {
            case ListingRefreshedEvent refreshed:
                _ = _panes.Any(p => p.ApplyRefresh(refreshed));
                break;
            case ListingLostEvent lost:
                foreach (var pane in _panes.Where(p => p.ListingId == lost.ListingId))
                {
                    _ = pane.ApplyLostAsync(lost);
                }
                break;
            case ConfigChangedEvent changed:
                Diag.Info(Target, "configuration changed", new LogField("changed", string.Join(",", changed.Changed)));
                if (changed.Changed.Count == 0)
                {
                    ShowNotice("cabinetos.json is valid again.");
                }
                if (changed.Changed.Any(key => key.StartsWith("marketplace", StringComparison.Ordinal)))
                {
                    OnMarketIndexChanged();
                }
                _ = ReadConfigSafelyAsync();
                break;
            case ConfigErrorEvent error:
                _configErrors++;
                var where = error.Line is { } line ? $" line {line}, column {error.Column}" : "";
                ShowNotice($"cabinetos.json{where}: {error.Message}", isError: true);
                break;
            case KeymapChangedEvent keymap:
                ApplyKeymap(Keymap.From(keymap.Keymap));
                break;
            case ThemeChangedEvent changed:
                _themes.Apply(changed.Theme);
                if (ThemesView.IsOpen)
                {
                    _picker.MarkCurrent(changed.Theme.Id);
                }
                return;
            case VolumesChangedEvent volumes:
                // A USB stick or a mapped share came or went: the Drives section follows.
                _sidebar.SetDrives(volumes.Volumes);
                return;
            case TerminalExitedEvent exited:
                _ = _terminal.OnExitedAsync(exited);
                return;
            case PluginStateChangedEvent or PluginCrashedEvent:
                _market.OnEvent(coreEvent);
                _ = RefreshPluginsAsync();
                break;
            case PluginEventEvent pluginEvent:
                OnPluginEvent(pluginEvent);
                return;
            case PreviewAppliedEvent or PreviewCancelledEvent:
                OnPreviewEvent(coreEvent);
                return;
            case InstallProgressEvent or InstallFinishedEvent:
                _market.OnEvent(coreEvent);
                return;
            case ToolsChangedEvent:
                _market.OnEvent(coreEvent);
                _toolsLoading = ReloadToolsAsync();
                return;
            case JobProgressEvent or JobStateChangedEvent or JobConflictEvent:
                _transfers.OnEvent(coreEvent);
                OnJobEvent(coreEvent);
                return;
            case MeasureProgressEvent or MeasureFinishedEvent:
                OnMeasureEvent(coreEvent);
                return;
            case UpdateStateChangedEvent or UpdateProgressEvent:
                OnUpdateEvent(coreEvent);
                return;
        }
        _ = RefreshCommandsAsync(coreEvent);
    }

    private void OnJobEvent(CoreEvent coreEvent)
    {
        if (coreEvent is JobStateChangedEvent { State: { IsFinal: true } state } changed && _transfers.Find(changed.JobId) is { } job)
        {
            var verb = TransferText.Words(job.Kind).Verb;
            switch (state.Type)
            {
                case JobState.Failed:
                    ShowNotice($"{verb} failed: {state.Message}", isError: true);
                    break;
                case JobState.CompletedWithErrors:
                    ShowNotice($"{TransferText.Title(job)}.", isError: true);
                    break;
            }
        }
    }

    private async Task ReadConfigSafelyAsync()
    {
        try
        {
            await ReadConfigAsync(firstStart: false);
        }
        catch (IOException error)
        {
            Diag.Info(Target, "cannot read the configuration", new LogField("error", error.Message));
        }
    }

    private async Task RefreshCommandsAsync(CoreEvent coreEvent)
    {
        if (await _router.OnCoreEventAsync(coreEvent))
        {
            await _palette.RefreshAsync();
        }
    }

    private async Task OnCoreLostAsync(string reason)
    {
        if (_closing)
        {
            return;
        }
        // The core stops on any panic (docs/diagnostics.md); the UI starts it
        // again, at most three times a minute. Its jobs ended with it.
        var now = DateTime.UtcNow;
        while (_restarts.Count > 0 && now - _restarts.Peek() > TimeSpan.FromMinutes(1))
        {
            _restarts.Dequeue();
        }
        _transfers.Reset();
        // The core closes its shells when it stops; the tabs go with them.
        _terminal.Reset();
        // Its plugins start over too: what the review or the list showed is stale.
        _plugins.Reset();
        _pluginsUnavailable = false;
        ReviewView.Close();
        PluginsView.Close();
        // Its downloads ended with it; a shown marketplace reads the index again once it runs.
        _market.Reset();
        _marketRead = false;
        if (_restarts.Count >= 3)
        {
            await ShowStartFailureAsync($"The core stopped three times within a minute. Last reason: {reason}");
            return;
        }
        _restarts.Enqueue(now);
        ShowNotice($"The core stopped ({reason}). Starting it again…", isError: true);
        try
        {
            await _session.StartAsync();
        }
        catch (CoreLaunchException error)
        {
            await ShowStartFailureAsync(error.Message);
            return;
        }
        _settingsWriter.Reset();
        _unavailable.Clear();
        await LoadAsync(firstStart: false);
        if (MarketView.IsOpen)
        {
            _marketRead = true;
            _ = _market.RefreshAsync();
        }
        ShowNotice("The core is running again.");
    }

    private async Task ShowStartFailureAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "CabinetOS could not start its core",
            Content = new ScrollViewer
            {
                MaxHeight = 360,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            },
            PrimaryButtonText = "Try again",
            CloseButtonText = "Close CabinetOS",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
        {
            _started = true;
            await StartAsync();
        }
        else
        {
            Close();
        }
    }

    // WinUI allows one ContentDialog at a time; a second one would throw. While one is open the
    // router runs no command, and Esc closes it wherever the keyboard is (docs/ui.md, "Dialogs").
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_openDialog is not null)
        {
            return ContentDialogResult.None;
        }
        _openDialog = dialog;
        UpdateModal();
        // A selectable text in the dialog, which can hold the keyboard, takes Esc for itself.
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                dialog.Hide();
            }
        };
        dialog.Opened += (_, _) => FocusDialog(dialog);
        var title = dialog.Title as string ?? "";
        Diag.Info(Target, "dialog shown", new LogField("title", title));
        var result = ContentDialogResult.None;
        try
        {
            result = await dialog.ShowAsync();
            return result;
        }
        finally
        {
            _openDialog = null;
            UpdateModal();
            Diag.Info(Target, "dialog closed", new LogField("title", title), new LogField("result", result.ToString()));
        }
    }

    // The dialog's default button (else Close) takes the keyboard, so Enter does what the dialog
    // offers first; WinUI would give it to the first focusable part, a link in About.
    private static void FocusDialog(ContentDialog dialog)
    {
        var name = dialog.DefaultButton switch
        {
            ContentDialogButton.Primary => "PrimaryButton",
            ContentDialogButton.Secondary => "SecondaryButton",
            _ => "CloseButton",
        };
        if (FindNamed(dialog, name) is Control button && button.Focus(FocusState.Programmatic))
        {
            return;
        }
        if (FocusManager.FindFirstFocusableElement(dialog) is UIElement first)
        {
            first.Focus(FocusState.Programmatic);
        }

        static DependencyObject? FindNamed(DependencyObject parent, string name)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement { Name: var childName, Visibility: Visibility.Visible } && childName == name)
                {
                    return child;
                }
                if (FindNamed(child, name) is { } found)
                {
                    return found;
                }
            }
            return null;
        }
    }

    // docs/ui.md, "Dialogs": while a dialog is open the router runs only that dialog's own commands.
    private void UpdateModal()
    {
        if (_openDialog is { } dialog)
        {
            _router.SetModal(dialog.Title as string ?? "dialog");
        }
        else if (ReviewView.IsOpen)
        {
            _router.SetModal("permissions review", "plugins.grant", "overlay.close");
        }
        else
        {
            _router.SetModal(null);
        }
    }

    // ----- Commands -----

    private void RegisterCommands()
    {
        _router.RegisterUiHandler("palette.show", _ => TogglePalette());
        _router.RegisterUiHandler("overlay.close", _ => CloseOverlay());
        // The palette lists every command with its keys and edits them: it is
        // the shortcut editor of this version.
        _router.RegisterUiHandler("keys.open", _ => _palette.Open());
        _router.RegisterUiHandler("view.toggleDualPane", invocation =>
        {
            if (RefuseInCompact("dual pane"))
            {
                return;
            }
            // The user chose a layout: it stays when a preview closes.
            _previewMadeDual = false;
            ApplyDual(!_dual);
            _ = PersistAsync(ShellState.DualPaneKey, _dual);
        });
        _router.RegisterUiHandler("view.focusOtherPane", _ => FocusOtherPane());
        _router.RegisterUiHandler("view.toggleSidebar", invocation =>
        {
            if (RefuseInCompact("sidebar"))
            {
                return;
            }
            ApplySidebar(!_sidebarOpen);
            _ = PersistAsync(ShellState.SidebarKey, _sidebarOpen);
        });
        _router.RegisterUiHandler("go.toPath", GoToPathAsync);
        // Not in the core's registry yet: this handler runs it all the same (the router's rule for window commands).
        _router.RegisterUiHandler("window.new", _ => OpenNewWindow());

        // The shell's own commands, in the core's registry since protocol 9
        // (target ui, keys in the keymap, so each one can be rebound).
        // A pane's breadcrumb row names its pane; a key or the palette means the active one.
        _router.RegisterUiHandler("go.back", invocation =>
        {
            var pane = ActivatePaneOf(invocation);
            return pane.IsLocked ? StayInLockedTab() : pane.GoBackAsync(invocation.RequestId);
        });
        _router.RegisterUiHandler("go.forward", invocation =>
        {
            var pane = ActivatePaneOf(invocation);
            return pane.IsLocked ? StayInLockedTab() : pane.GoForwardAsync(invocation.RequestId);
        });
        _router.RegisterUiHandler("go.up", invocation => ActivatePaneOf(invocation).GoUpAsync(invocation.RequestId));
        _router.RegisterUiHandler("pane.openSelected", invocation => Active.Search is null ? OpenAsync(invocation) : OpenHitAsync(invocation));
        _router.RegisterUiHandler("file.copyToOtherPane", ListingOnly(invocation => TransferToOtherPaneAsync(JobKind.Copy, invocation)));
        _router.RegisterUiHandler("file.moveToOtherPane", ListingOnly(invocation => TransferToOtherPaneAsync(JobKind.Move, invocation)));
        _router.RegisterUiHandler("file.newFolder", ListingOnly(NewFolderAsync));
        _router.RegisterUiHandler("file.openInOtherPane", ListingOnly(OpenInOtherPaneAsync));
        _router.RegisterUiHandler("file.delete", ListingOnly(invocation => DeleteAsync(invocation, permanent: false)));
        _router.RegisterUiHandler("file.deletePermanently", ListingOnly(invocation => DeleteAsync(invocation, permanent: true)));
        _router.RegisterUiHandler("file.rename", ListingOnly(invocation => Active.FocusIndex >= 0 ? RenameAtAsync(Active, Active.FocusIndex, invocation.RequestId) : Task.CompletedTask));
        _router.RegisterUiHandler("file.properties", ListingOnly(ShowPropertiesAsync));
        _router.RegisterUiHandler("edit.cut", ListingOnly(_ => PutOnClipboard(ClipboardMode.Cut)));
        _router.RegisterUiHandler("edit.copy", ListingOnly(_ => PutOnClipboard(ClipboardMode.Copy)));
        _router.RegisterUiHandler("edit.paste", ListingOnly(PasteAsync));
        _router.RegisterUiHandler("edit.selectAll", ListingOnly(_ => Active.Selection.SelectAll()));
        _router.RegisterUiHandler("edit.toggleSelection", ListingOnly(_ =>
        {
            Active.Selection.ToggleFocusAndAdvance();
            _paneViews[_active].ScrollToFocus();
        }));
        // F2 in the open palette: the highlighted command (the pencil passes its own).
        _router.RegisterUiHandler("keys.rebind", invocation =>
        {
            if ((CommandArgs.Text(invocation.Args, "command") ?? _palette.HighlightedCommandId) is { } command)
            {
                _palette.StartRecording(command);
            }
        });

        // The window's own buttons and menus, in the core's registry since protocol 11: the
        // buttons pass arguments (a path, a decision) that the palette and keys leave out.
        _router.RegisterUiHandler("transfer.pause", invocation => ControlShownAsync(JobActions.Pause, invocation));
        _router.RegisterUiHandler("transfer.resume", invocation => ControlShownAsync(JobActions.Resume, invocation));
        _router.RegisterUiHandler("transfer.cancel", invocation => ControlShownAsync(JobActions.Cancel, invocation));
        _router.RegisterUiHandler("transfer.close", _ =>
        {
            ReturnFocusFromFlyout();
            _transfers.Close();
        });
        _router.RegisterUiHandler("transfer.minimize", _ =>
        {
            ReturnFocusFromFlyout();
            _transfers.Minimize();
        });
        _router.RegisterUiHandler("transfer.restore", _ => _transfers.Restore());
        _router.RegisterUiHandler("transfer.next", _ => _transfers.ShowNext());
        _router.RegisterUiHandler("conflict.resolve", ResolveConflictAsync);
        _router.RegisterUiHandler("sidebar.pin", PinAsync);
        _router.RegisterUiHandler("sidebar.unpin", UnpinAsync);
        RegisterCommanderCommands();
        RegisterTabCommands();
        RegisterTerminalCommands();
        RegisterSearchCommands();
        RegisterFindCommands();
        RegisterQuickOpenCommands();
        RegisterShellCommands();
        RegisterPluginCommands();
        RegisterToolCommands();
        RegisterThemeCommands();
        RegisterMarketCommands();
        RegisterRailCommands();
        RegisterColumnCommands();
        RegisterCompactCommands();
        RegisterAboutCommand();

        _router.Completed += OnCommandCompleted;
        // A command acts where the cursor keys waiting for the next frame leave the cursor
        // (FilePane.ApplyCursorKeys; docs/ui.md, "Scrolling").
        _router.Executing += _ => ApplyCursorKeys();
        // A plugin's command from the palette or a key gets the active pane's files, as the
        // context menu passes them (docs/plugins.md, "What the shell passes").
        _router.PluginArgs = _ =>
        {
            ApplyCursorKeys();
            return ActivePaneFiles();
        };
    }

    private void ApplyCursorKeys()
    {
        foreach (var view in _paneViews)
        {
            view.ApplyCursorKeys();
        }
    }

    private JsonElement ActivePaneFiles()
    {
        var pane = Active;
        var fields = new List<(string Name, object? Value)>();
        if (pane.Search is null)
        {
            if (pane.EntryAt(pane.FocusIndex) is { } focused)
            {
                fields.Add(("path", focused.Path));
            }
            if (pane.Selection.SelectedCount > 0)
            {
                fields.Add(("paths", pane.Targets().Select(t => t.Path).ToList()));
            }
        }
        else if (pane.FocusedHit is { } hit)
        {
            fields.Add(("path", hit.Hit.Path));
            fields.Add(("paths", new List<string> { hit.Hit.Path }));
        }
        return CommandArgs.Object([.. fields]);
    }

    private void OnCommandCompleted(CommandOutcome outcome)
    {
        var name = _router.Find(outcome.CommandId) is { } info ? $"{info.Category}: {info.Title}" : outcome.CommandId;
        switch (outcome.Kind)
        {
            case CommandOutcomeKind.NotAvailable:
                ShowNotice($"{name} arrives in a later version.");
                break;
            case CommandOutcomeKind.Failed:
                ShowNotice($"{name}: {outcome.ErrorMessage}", isError: true);
                break;
            case CommandOutcomeKind.CoreResult when outcome.CommandId == "help.about":
                // A core that still runs help.about itself: the window's About view shows instead of its raw result.
                _ = ShowAboutAsync();
                break;
            case CommandOutcomeKind.CoreResult when OpensPreview(outcome):
                break;
            case CommandOutcomeKind.CoreResult when _router.Find(outcome.CommandId) is { Source.Kind: "program" } program:
                // A program started (program.<name>): a line in the status bar, not a dialog.
                ShowNotice($"{program.Title}: started.");
                break;
            case CommandOutcomeKind.CoreResult when outcome.Result is { } result:
                _ = ShowResultAsync(name, result);
                break;
        }
    }

    private async Task ShowResultAsync(string title, JsonElement result)
    {
        var text = new StringBuilder();
        if (result.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in result.EnumerateObject())
            {
                text.Append(property.Name).Append(": ").AppendLine(property.Value.ToString());
            }
        }
        else
        {
            text.Append(result.ToString());
        }
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = title,
            Content = new TextBlock { Text = text.ToString().TrimEnd(), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "Close",
        };
        await ShowDialogAsync(dialog);
    }

    private async Task GoToPathAsync(CommandInvocation invocation)
    {
        var pane = PaneOf(invocation);
        if (CommandArgs.Text(invocation.Args, "path") is { Length: > 0 } path)
        {
            ActivatePaneOf(invocation);
            // The pane takes the keyboard before the address box collapses (see the palette).
            FocusActivePane();
            EndAddressEdit();
            await Active.NavigateAsync(path, invocation.RequestId);
            FocusActivePane();
        }
        else
        {
            BeginAddressEdit(pane);
        }
    }

    // The pane a command names comes to the front (a click on its breadcrumb row); the active one otherwise.
    private PaneModel ActivatePaneOf(CommandInvocation invocation)
    {
        var pane = PaneOf(invocation);
        if (pane != _active && (_dual || pane == 0))
        {
            SetActive(pane);
        }
        return _panes[pane];
    }

    private void TogglePalette()
    {
        if (_palette.IsOpen)
        {
            _palette.Close();
        }
        else
        {
            EndAddressEdit();
            FileMenu.Close();
            PromptView.Cancel();
            // One overlay at a time: Ctrl+Shift+P from Quick Open shows the commands instead.
            CloseQuickOpen(returnFocus: false);
            // Ctrl+Shift+P from a shell: the keyboard goes back there when the palette closes.
            _paletteFromTerminal = Dock.HasTerminalFocus;
            _palette.Open();
            // Once the palette has the keyboard: a review left open is a Cancel; the list and
            // the picker come back from the palette.
            ReviewView.Close();
            PluginsView.Close();
            HideThemePicker(restore: true);
        }
    }

    private void ReturnFocusAfterPalette()
    {
        if (_paletteFromTerminal && _dockVisible)
        {
            _paletteFromTerminal = false;
            FocusTerminal();
            return;
        }
        _paletteFromTerminal = false;
        FocusActivePane();
    }

    // Esc: the palette, then the context menu, then an edit in place, then the address box
    // (the design's order), then the search results (back to the folder). The plugin review
    // and the plugin list come right after the palette: they cover the window. Last, an open
    // transfer flyout folds into the pill: it never takes the keyboard, so without this a
    // keyboard user could not put it away (Article 7).
    private void CloseOverlay()
    {
        if (_palette.IsOpen)
        {
            _palette.Close();
        }
        else if (_quickOpen.IsOpen)
        {
            CloseQuickOpen(returnFocus: true);
        }
        else if (PromptView.IsOpen)
        {
            PromptView.Cancel();
        }
        else if (MenuEditorView.IsOpen)
        {
            MenuEditorView.Cancel();
        }
        else if (ThemesView.IsOpen)
        {
            CloseThemePicker();
        }
        else if (ReviewView.IsOpen)
        {
            CloseReview();
        }
        else if (PluginsView.IsOpen)
        {
            ClosePlugins();
        }
        else if (MarketView.IsOpen)
        {
            CloseMarketLevel();
        }
        else if (FileMenu.IsOpen)
        {
            FileMenu.Close();
        }
        else if (_contextMenu.IsOpen || _windowsMenu.IsOpen)
        {
            // The flyouts take Esc themselves; this is the palette's and the snapshot aid's way.
            _contextMenu.Close();
            _windowsMenu.Close();
        }
        else if (_paneViews.FirstOrDefault(v => v.IsRenaming) is { } renaming)
        {
            renaming.CancelRename();
        }
        else if (QuickText.Visibility == Visibility.Visible)
        {
            EndQuickSearch();
        }
        else if (IsEditingAddress)
        {
            // The pane takes the keyboard before the address box collapses (see the palette).
            FocusActivePane();
            EndAddressEdit();
        }
        else if ((_railLayout || _searchInSidebar) && (IsFocusWithin(Rail) || (_sidebarOpen && IsFocusWithin(SidebarHost))))
        {
            LeaveSidebarSearch();
            // Esc in the rail layout's rail or sidebar (a button, the tree, the search field or one of its hits) gives the
            // keyboard back to the pane, and a search ends as it does anywhere. The window's key handler takes Esc
            // before the tree's and the field's own handlers see it (it runs first, as the tunnelling PreviewKeyDown).
            if (!EndSearch(focusPane: true))
            {
                FocusPaneOrEditor();
            }
            Diag.Info(Target, "Esc gave the keyboard from the rail layout's sidebar back to the pane");
        }
        else if (!CloseFindForEscape() && !EndSearch(focusPane: true) && _transfers.IsFlyoutOpen)
        {
            // What the flyout's minimize button does: a running job folds into the pill, an ended one closes.
            _transfers.Minimize();
        }
    }

    private bool IsFocusWithin(UIElement container)
    {
        var focused = RootGrid.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
        for (var element = focused; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element == container)
            {
                return true;
            }
        }
        return false;
    }

    // ----- File commands -----

    private async Task TransferToOtherPaneAsync(JobKind kind, CommandInvocation invocation)
    {
        var verb = TransferText.Words(kind).Verb;
        if (!_dual)
        {
            ShowNotice($"{verb} to the other pane needs two panes: press Ctrl+Shift+D.");
            return;
        }
        var sources = Active.Targets();
        if (sources.Count == 0 || Other.Path.Length == 0)
        {
            return;
        }
        // Restore Selection (Num /) brings these marks back after the listing changed.
        Active.RememberMarks();
        var start = await _transfers.StartAsync(kind, sources.Select(s => s.Path).ToList(), Other.Path, invocation.RequestId);
        if (!start.Started)
        {
            ShowNotice($"{verb}: {start.ErrorMessage}", isError: true);
        }
    }

    private async Task DeleteAsync(CommandInvocation invocation, bool permanent)
    {
        var targets = Active.Targets();
        if (targets.Count == 0)
        {
            return;
        }
        if (permanent)
        {
            // A link is named as one (docs/ui.md, "Links and cloud files"): only the link goes.
            var view = Active.View;
            var (title, body) = DeleteText.Permanent(targets
                .Select(t => new DeleteTarget(t.Name, t.IsFolder, view is null ? LinkKind.None : EntryFacts.LinkOf(view, t.Index)))
                .ToList());
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                // A dialog sits in the popup layer, outside the root's RequestedTheme (a light theme).
                RequestedTheme = RootGrid.ActualTheme,
                Title = title,
                Content = new TextBlock
                {
                    Text = body,
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Delete permanently",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
            {
                Diag.Request(LogLevel.Info, invocation.RequestId, Target, "permanent delete cancelled", new LogField("items", targets.Count));
                FocusActivePane();
                return;
            }
            FocusActivePane();
        }
        Active.RememberMarks();
        var start = await _transfers.StartAsync(JobKind.Delete(permanent), targets.Select(t => t.Path).ToList(), null, invocation.RequestId);
        if (!start.Started)
        {
            ShowNotice($"Delete: {start.ErrorMessage}", isError: true);
        }
    }

    private async Task OpenAsync(CommandInvocation invocation)
    {
        var pane = Active;
        if (pane.EntryAt(pane.FocusIndex) is not { } entry)
        {
            return;
        }
        if (entry.IsFolder)
        {
            await pane.NavigateAsync(entry.Path, invocation.RequestId);
            return;
        }
        // An installed Tool Extension that opens this name gets it (a .md in Markdown Preview);
        // without one, the file opens in its default application as before.
        if (await ToolForAsync(entry.Name) is { } tool)
        {
            await OpenInToolAsync(tool, entry.Path);
            return;
        }
        if (_unavailable.Contains("open_path"))
        {
            ShowNotice("Opening files needs a newer core.");
            return;
        }
        // open_path runs in the core, a background process: let it bring the application to the front.
        if (_session.CoreProcessId is { } corePid)
        {
            WindowsPlatform.AllowForeground(corePid);
        }
        var reply = await RequestSafelyAsync(new OpenPathRequest(entry.Path) { Id = invocation.RequestId });
        switch (reply)
        {
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                Unavailable("open_path", "Opening files needs a newer core.");
                break;
            case ErrorReply error:
                ShowNotice($"Cannot open {entry.Name}: {error.Message}", isError: true);
                break;
        }
    }

    private async Task OpenInOtherPaneAsync(CommandInvocation invocation)
    {
        if (Active.EntryAt(Active.FocusIndex) is not { } entry)
        {
            return;
        }
        if (!entry.IsFolder)
        {
            ShowNotice("Open in other pane opens folders.");
            return;
        }
        if (!EnsureDual())
        {
            return;
        }
        await Other.NavigateAsync(entry.Path, invocation.RequestId);
    }

    private async Task NewFolderAsync(CommandInvocation invocation)
    {
        var pane = Active;
        if (pane.Path.Length == 0 || pane.View is null)
        {
            return;
        }
        if (_unavailable.Contains("create_directory"))
        {
            ShowNotice("Making folders needs a newer core.");
            return;
        }
        // The name the listing does not have yet; if another program takes it
        // meanwhile, the core says already_exists and the next free one is tried.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var name = pane.FreeName("New folder");
            var reply = await RequestSafelyAsync(new CreateDirectoryRequest(DisplayFormat.Join(pane.Path, name)) { Id = attempt == 0 ? invocation.RequestId : "" });
            switch (reply)
            {
                case OkReply:
                    var index = await pane.SelectWhenListedAsync(name, TimeSpan.FromSeconds(2));
                    if (index < 0 && await pane.ReloadAsync())
                    {
                        index = pane.View?.IndexOfName(name) ?? -1;
                    }
                    if (index >= 0)
                    {
                        pane.Selection.MoveTo(index, SelectMode.Single);
                        await RenameAtAsync(pane, index, requestId: null);
                    }
                    return;
                case ErrorReply { Code: ErrorCodes.AlreadyExists }:
                    await pane.ReloadAsync();
                    continue;
                case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                    Unavailable("create_directory", "Making folders needs a newer core.");
                    return;
                case ErrorReply error:
                    ShowNotice($"New folder: {error.Message}", isError: true);
                    return;
                default:
                    return;
            }
        }
    }

    private async Task RenameAtAsync(PaneModel pane, int index, string? requestId)
    {
        if (_unavailable.Contains("rename"))
        {
            ShowNotice("Renaming needs a newer core.");
            return;
        }
        var view = _paneViews[pane.Index];
        if (pane.EntryAt(index) is not { } entry)
        {
            return;
        }
        var newName = await view.BeginRenameAsync(index, entry.Name, selectStem: !entry.IsFolder);
        if (newName is null)
        {
            return;
        }
        // The refresh that shows the new name selects it (by name: the ID may change).
        pane.ExpectName(newName);
        var reply = await RequestSafelyAsync(new RenameRequest(entry.Path, newName) { Id = requestId ?? "" });
        switch (reply)
        {
            case OkReply:
                break;
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                pane.ForgetExpectedName();
                Unavailable("rename", "Renaming needs a newer core.");
                break;
            case ErrorReply error:
                pane.ForgetExpectedName();
                var row = pane.View?.IndexOfName(entry.Name) ?? -1;
                view.ShowRowNote(row >= 0 ? row : index, error.Message);
                break;
            default:
                pane.ForgetExpectedName();
                break;
        }
    }

    private void PutOnClipboard(ClipboardMode mode)
    {
        var targets = Active.Targets();
        if (targets.Count == 0)
        {
            return;
        }
        var text = _clipboard.Set(targets.Select(t => t.Path).ToList(), mode);
        try
        {
            // Other programs get the paths as text, one per line (a terminal, an editor).
            var package = new DataPackage { RequestedOperation = mode == ClipboardMode.Cut ? DataPackageOperation.Move : DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            _systemClipboardHolds = true;
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        {
            _systemClipboardHolds = false;
            Diag.Info(Target, "the Windows clipboard is busy; the paths stay in CabinetOS only", new LogField("error", error.Message));
        }
        var items = targets.Count == 1 ? targets[0].Name : $"{targets.Count:N0} items";
        ShowNotice(mode == ClipboardMode.Cut ? $"Cut {items}. Ctrl+V moves them." : $"Copied {items}. Ctrl+V copies them.");
    }

    private async Task PasteAsync(CommandInvocation invocation)
    {
        var pane = Active;
        if (pane.Path.Length == 0)
        {
            return;
        }
        // Other text copied since (anywhere) replaces the paths, as Windows' clipboard
        // would; unless Windows' clipboard never got them (it was busy).
        if (_systemClipboardHolds && await ReadClipboardTextAsync() is { } systemText)
        {
            _clipboard.KeepIfSystemTextIs(systemText);
        }
        if (_clipboard.Plan(pane.Path) is not { } plan)
        {
            ShowNotice("Nothing to paste: copy or cut files first (Ctrl+C, Ctrl+X).");
            return;
        }
        var start = await _transfers.StartAsync(plan.Kind, plan.Sources, plan.Destination, invocation.RequestId);
        if (start.Started)
        {
            _clipboard.OnPasted();
        }
        else
        {
            ShowNotice($"Paste: {start.ErrorMessage}", isError: true);
        }
    }

    // What Windows' clipboard holds as text: "" when it holds something else, null when it cannot be read.
    private static async Task<string?> ReadClipboardTextAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            return content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : "";
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task ShowPropertiesAsync(CommandInvocation invocation)
    {
        var pane = Active;
        if (pane.View is not { } view)
        {
            return;
        }
        var culture = CultureInfo.CurrentCulture;
        var rows = new List<(string Label, string Value)>();
        string title;
        var targets = pane.Targets();
        var folderScope = CommandArgs.Text(invocation.Args, "scope") == "folder" || targets.Count == 0;
        // What the "Windows Properties" button hands Windows' own sheet (file.windowsProperties).
        List<string> paths = folderScope ? [pane.Path] : [.. targets.Select(t => t.Path)];
        if (folderScope)
        {
            title = pane.FolderName;
            var (files, folders, bytes) = PropertiesText.Tally(view, Enumerable.Range(0, view.Count));
            rows.Add(("Location", DisplayFormat.Parent(pane.Path) ?? pane.Path));
            rows.Add(("Contains", $"{files:N0} files, {folders:N0} folders"));
            rows.Add(("Size of the files", DisplayFormat.SizeWithBytes(bytes, culture)));
        }
        else if (targets.Count == 1)
        {
            var entry = targets[0];
            title = entry.Name;
            rows.AddRange(PropertiesText.ForEntry(view, entry.Index, pane.Path, pane.Detail(entry.Index, entry.Name, entry.IsFolder), culture));
        }
        else
        {
            title = $"{targets.Count:N0} items";
            var (files, folders, bytes) = PropertiesText.Tally(view, targets.Select(t => t.Index));
            rows.Add(("Location", pane.Path));
            rows.Add(("Contains", $"{files:N0} files, {folders:N0} folders"));
            rows.Add(("Size of the files", DisplayFormat.SizeWithBytes(bytes, culture)));
        }

        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 6, MinWidth = 360 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Label, Foreground = ThemeResources.Brush("CbTextTertiaryBrush") };
            var value = new TextBlock { Text = rows[i].Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = title,
            Content = grid,
            // Windows' own sheet has what this one leaves out (security, versions, sharing); a core
            // that cannot show it gets no button.
            SecondaryButtonText = _unavailable.Contains("show_properties") ? "" : "Windows Properties",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        var result = await ShowDialogAsync(dialog);
        FocusActivePane();
        if (result == ContentDialogResult.Secondary)
        {
            await _router.ExecuteAsync("file.windowsProperties", CommandArgs.Object(("paths", paths)), "button");
        }
    }

    private async Task ControlShownAsync(string action, CommandInvocation invocation)
    {
        if (_transfers.Shown is not { } job)
        {
            return;
        }
        if (await _transfers.ControlAsync(job.Id, action, invocation.RequestId) is { } error)
        {
            ShowNotice($"The job could not {action}: {error}", isError: true);
        }
    }

    private async Task ResolveConflictAsync(CommandInvocation invocation)
    {
        var id = CommandArgs.Number(invocation.Args, "conflict_id");
        var resolution = CommandArgs.Text(invocation.Args, "resolution");
        if (id is null || resolution is null)
        {
            // From the palette or a key: no decision given, so the flyout shows the waiting conflict.
            if (_transfers.Conflicts.Current is not null)
            {
                _transfers.Restore();
            }
            else
            {
                ShowNotice("No conflict waits for a decision.");
            }
            return;
        }
        if (_transfers.Conflicts.Waiting.FirstOrDefault(c => c.ConflictId == id) is not { } conflict)
        {
            return;
        }
        // Before the card closes: a button pressed by a screen reader or Voice Access holds the
        // keyboard, which a closing card would hand to the Back button (live check, 2026-09-28).
        ReturnFocusFromFlyout();
        var apply = CommandArgs.Bool(invocation.Args, "apply_to_same_kind");
        if (await _transfers.ResolveAsync(conflict, new Resolution(resolution), apply, invocation.RequestId) is { } error)
        {
            ShowNotice($"{ConflictText.Name(conflict)}: {error}", isError: true);
        }
    }

    // The flyout never keeps the keyboard (docs/ui.md, "The transfer flyout"): a pointer never gives
    // it the focus, and whatever else did (Tab, assistive technology) hands it back to the pane
    // before the part with the focus closes.
    private void ReturnFocusFromFlyout()
    {
        if (IsFocusWithin(TransferView))
        {
            FocusActivePane();
        }
    }

    private async Task PinAsync(CommandInvocation invocation)
    {
        var path = CommandArgs.Text(invocation.Args, "path") ?? Active.Path;
        if (path.Length == 0 || _sidebar.IsPinned(path))
        {
            return;
        }
        _shell = _shell with { Pinned = [.. _shell.Pinned, path] };
        SetPinnedFolders();
        await SavePinnedAsync();
    }

    private async Task UnpinAsync(CommandInvocation invocation)
    {
        // The sidebar's menu names the row; the palette and keys mean the active pane's folder.
        var path = CommandArgs.Text(invocation.Args, "path") ?? Active.Path;
        if (!_sidebar.IsPinned(path))
        {
            if (invocation.Args is null)
            {
                ShowNotice($"{DisplayFormat.FolderName(path)} is not pinned to the sidebar.");
            }
            return;
        }
        _shell = _shell with { Pinned = _shell.Pinned.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList() };
        SetPinnedFolders();
        await SavePinnedAsync();
    }

    private async Task SavePinnedAsync()
    {
        if (!await _settingsWriter.SetAsync(ShellState.PinnedKey, _shell.Pinned) && _settingsWriter.IsAvailable)
        {
            ShowNotice("The pinned folders could not be saved in cabinetos.json.", isError: true);
        }
    }

    private async Task<CoreReply?> RequestSafelyAsync(CoreRequest request)
    {
        try
        {
            return await _session.RequestAsync(request);
        }
        catch (IOException error)
        {
            ShowNotice(error.Message, isError: true);
            return null;
        }
    }

    // A request this core does not know: the feature hides, with one line in the log.
    private void Unavailable(string request, string notice)
    {
        if (_unavailable.Add(request))
        {
            Diag.Info(Target, $"the core does not answer {request}; the UI stops offering it");
        }
        ShowNotice(notice);
    }

    // A registry command's first binding as the menu shows it: "F5", "Ctrl+K Ctrl+H".
    private string? KeysOf(string commandId) =>
        _router.Find(commandId)?.Keys is [var first, ..] && KeySequence.TryParse(first, out var keys)
            ? string.Join(' ', keys.DisplayParts())
            : null;

    // ----- Transfers (design view E) -----

    private void UpdateTransfers()
    {
        if (_transfers.IsPillVisible && _transfers.Shown is { } job)
        {
            TransferPill.Visibility = Visibility.Visible;
            PillText.Text = TransferText.PillText(job);
            PillFill.Scale = new Vector3((float)TransferText.Fraction(job), 1, 1);
        }
        else
        {
            TransferPill.Visibility = Visibility.Collapsed;
        }
        var running = _transfers.Visible.Any(j => !j.IsFinal);
        if (running && !_speedTimer.IsRunning)
        {
            _speedTimer.Start();
        }
        else if (!running && _speedTimer.IsRunning)
        {
            _speedTimer.Stop();
        }
    }

    // ----- Panes -----

    // window.new (docs/ui.md, "Two windows"): another CabinetOS.exe at the active pane's folder. It
    // starts a core of its own (PLAN.md, "Process layout") and shares the configuration and the logs.
    private void OpenNewWindow()
    {
        if (Environment.ProcessPath is not { } exe)
        {
            return;
        }
        var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in _args.ForNewWindow(Active.Path))
        {
            start.ArgumentList.Add(arg);
        }
        // The snapshot aid's steps are this window's: a new window would run them again, window.new among them.
        start.Environment.Remove(DevSnapshots.FolderEnv);
        start.Environment.Remove(DevSnapshots.StepsEnv);
        try
        {
            using var process = System.Diagnostics.Process.Start(start);
            Diag.Info(Target, "new window started", new LogField("pid", process?.Id), new LogField("path", Active.Path));
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Diag.Warn(Target, "a new window could not start", new LogField("error", error.Message));
            ShowNotice($"A new window could not start: {error.Message}", isError: true);
        }
    }

    // keepTools: the compact overlay hides the right pane for a while and keeps its tools and previews for the way back.
    private void ApplyDual(bool dual, bool keepTools = false)
    {
        _dual = dual;
        if (!dual && !keepTools)
        {
            // The right pane goes away, and its tool tabs with it (the tool's process ends).
            CloseToolTabs(1, focusPane: false);
            if (_previewViews?[1].IsShown == true)
            {
                // A preview in the pane that goes away is dropped, not left waiting for a key nobody can press.
                var dropped = _previewViews[1].Session?.Id ?? "";
                EndPreviewView(_previewViews[1]);
                _ = RequestSafelyAsync(new PreviewCancelRequest(dropped));
            }
        }
        RightColumn.Width = dual ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        RightSide.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
        ApplyPaneGaps();
        UpdateDualButton();
        foreach (var pane in _panes)
        {
            pane.IsDual = dual;
        }
        if (!dual && _active != 0)
        {
            SetActive(0);
            _paneViews[0].Focus(FocusState.Programmatic);
        }
        if (_tabViews is not null)
        {
            UpdateTabRows();
        }
        // Two panes show 3 parts of a path whole, one pane 5 (Breadcrumbs).
        UpdateCrumbs();
    }

    private void ApplySidebar(bool open)
    {
        // The rail layout's sidebar holds the tree, the search field or a web page: what hides must not keep the keyboard.
        if (!open && _sidebarOpen && _railLayout && IsFocusWithin(SidebarHost))
        {
            FocusPaneOrEditor();
        }
        _sidebarOpen = open;
        SidebarHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        UpdateSidebarChrome();
    }

    private void FocusOtherPane()
    {
        if (!_dual)
        {
            return;
        }
        // A tool tab in front covers the other pane's list, which then cannot take the keyboard: its page does.
        if (_editorViews[1 - _active].IsOpen)
        {
            FocusEditorPage(1 - _active);
            return;
        }
        _paneViews[1 - _active].Focus(FocusState.Keyboard);
    }

    private void OnPaneActivated(FilePane view) => SetActive(Array.IndexOf(_paneViews, view));

    private void SetActive(int index)
    {
        if (index < 0 || index == _active && _panes[index].IsActive)
        {
            return;
        }
        _active = index;
        for (var i = 0; i < _panes.Length; i++)
        {
            _panes[i].IsActive = i == index;
        }
        UpdateStatus();
        UpdateNavigationButtons();
        UpdateCrumbs();
        _sidebar.SetActivePath(Active.Path);
        _terminal.SetActiveFolder(Active.Path);
        ScheduleToolContext();
        UpdateTabRows();
        ScheduleWindowState();
        _ = UpdateWorkspaceAsync();
    }

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        using var timed = FrameParts.Time(FramePart.Status);
        if (e.PropertyName == nameof(PaneModel.Path) && sender is PaneModel moved)
        {
            // The tab in front follows the folder; the row's title and the saved tabs with it.
            SyncTabFolder(moved);
        }
        if (e.PropertyName is nameof(PaneModel.Path) or nameof(PaneModel.Selection) or nameof(PaneModel.Rows))
        {
            // The cursor and the marks of either pane are part of what the window tells the core.
            ScheduleWindowState();
        }
        if (e.PropertyName == nameof(PaneModel.Path) && _searchPane >= 0 && sender == _panes[_searchPane])
        {
            // The pane went elsewhere (Backspace, a crumb, a lost folder): its results are stale.
            EndSearch(focusPane: false);
        }
        // Each pane's breadcrumb row follows its own pane, active or not.
        if (e.PropertyName == nameof(PaneModel.Path))
        {
            UpdateCrumbs();
        }
        if (e.PropertyName is nameof(PaneModel.Path) or nameof(PaneModel.CanGoBack) or nameof(PaneModel.CanGoForward) or nameof(PaneModel.CanGoUp))
        {
            UpdateNavigationButtons();
        }
        if (sender != Active)
        {
            return;
        }
        switch (e.PropertyName)
        {
            case nameof(PaneModel.Path):
                EndQuickSearch();
                _sidebar.SetActivePath(Active.Path);
                _terminal.SetActiveFolder(Active.Path);
                ScheduleToolContext();
                _ = UpdateWorkspaceAsync();
                break;
            case nameof(PaneModel.Count) or nameof(PaneModel.Selection) or nameof(PaneModel.Rows):
                UpdateStatus();
                ScheduleToolContext();
                break;
            case nameof(PaneModel.Sizes):
                // A measured folder that is selected adds to the selected size.
                UpdateStatus();
                break;
        }
    }

    // ----- Keyboard -----

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        LogHeavyKey(e);
        HandleWindowKey(e);
        // The character this key types, if any, arrives next (CharacterReceived): a key the window
        // took (a binding, a chord) must not also go into a quick search.
        _keyTakenByWindow = e.Handled;
    }

    private void HandleWindowKey(KeyRoutedEventArgs e)
    {
        if (_openDialog is { } dialog)
        {
            if (IsFocusWithin(dialog))
            {
                // A key in the dialog passes here on its way in (WinUI routes the popup's keys through the
                // window's root): Tab, the arrows, Enter and Space are the dialog's buttons', Esc its own
                // (ShowDialogAsync), and no binding of the window runs.
                return;
            }
            // The keyboard is under an open dialog: nothing under it reacts. Esc closes the dialog, as it
            // would there; any other key takes the keyboard back into it.
            e.Handled = true;
            Diag.Info(Target, "key held by a dialog", new LogField("key", e.Key.ToString()), new LogField("dialog", dialog.Title as string ?? ""));
            if (e.Key == VirtualKey.Escape)
            {
                dialog.Hide();
            }
            else
            {
                FocusDialog(dialog);
            }
            return;
        }
        if (MenuEditorView.IsOpen && !PromptView.IsOpen)
        {
            // The menu's edit mode holds the keyboard as a dialog does: no binding of the window runs under it. Its own
            // keys (arrows, Alt+Up, Delete, Insert, Ctrl+S, Esc) act there; Tab, Enter and Space go to its buttons.
            var editKey = KeyNames.ComboFor((int)e.Key, CurrentModifiers());
            if (!MenuEditorView.HasKeyboard)
            {
                MenuEditorView.TakeKeyboard();
                e.Handled = true;
            }
            if (editKey is { } editCombo && MenuEditorView.HandleKey(editCombo))
            {
                e.Handled = true;
            }
            return;
        }
        if (RootGrid.XamlRoot is { } focusRoot && FocusManager.GetFocusedElement(focusRoot) is WebView2 page)
        {
            // A web page (the terminal, a tool) takes its own keys and passes the
            // window's back as messages (TerminalKeys); a key that also arrives
            // here must not run twice, unless the page never got it (PageKeyboard).
            HandleKeyForPage(page, e);
            return;
        }
        if (HandlePreviewKey(e))
        {
            return;
        }
        var virtualKey = (int)e.Key;
        var modifiers = CurrentModifiers();
        if (_palette.IsRecording)
        {
            e.Handled = true;
            if (e.Key == VirtualKey.Escape)
            {
                _palette.CancelRecording();
            }
            else if (KeyNames.ComboFor(virtualKey, modifiers) is { } recorded)
            {
                _palette.Record(recorded);
            }
            return;
        }
        if (KeyNames.ComboFor(virtualKey, modifiers) is not { } combo)
        {
            return;
        }
        switch (_keys.OnKey(combo, CurrentContexts()))
        {
            case KeyOutcome.Run run:
                e.Handled = true;
                _ = _router.ExecuteAsync(run.Command, KeyArguments(run.Command, run.Keys), "key", TakeKeyTrace());
                break;
            case KeyOutcome.Pending:
                e.Handled = true;
                break;
            case KeyOutcome.NotBound notBound:
                e.Handled = true;
                ShowNotice($"{notBound.First.ToDisplay()} {notBound.Second.ToDisplay()} is not bound to a command.");
                break;
        }
    }

    private HashSet<string> CurrentContexts()
    {
        var contexts = new HashSet<string>(StringComparer.Ordinal);
        if (_palette.IsOpen)
        {
            contexts.Add(KeyContexts.PaletteOpen);
        }
        var focused = RootGrid.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
        if (focused is TextBox or PasswordBox or RichEditBox or AutoSuggestBox)
        {
            // A text box inside a pane (rename in place) is text input, not the files view:
            // F5 must not start a copy while a name is being typed (keybindings.md).
            contexts.Add(KeyContexts.TextInput);
            return contexts;
        }
        for (var element = focused; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is FilePane)
            {
                contexts.Add(KeyContexts.FilesView);
                break;
            }
        }
        return contexts;
    }

    private static KeyModifiers CurrentModifiers()
    {
        var modifiers = KeyModifiers.None;
        if (IsDown(VirtualKey.Control))
        {
            modifiers |= KeyModifiers.Ctrl;
        }
        if (IsDown(VirtualKey.Shift))
        {
            modifiers |= KeyModifiers.Shift;
        }
        if (IsDown(VirtualKey.Menu))
        {
            modifiers |= KeyModifiers.Alt;
        }
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
        {
            modifiers |= KeyModifiers.Win;
        }
        return modifiers;
    }

    private static bool IsDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private void UpdateChordIndicator()
    {
        if (_keys.PendingFirst is { } first)
        {
            // keybindings.md, "Chords": the status bar says a chord is waiting.
            ChordText.Text = $"{first.ToDisplay()} was pressed. Waiting for the second key…";
            ChordText.Visibility = Visibility.Visible;
            _chordTimer.Interval = TimeSpan.FromMilliseconds(_keys.Keymap.ChordWindowMs + 20);
            _chordTimer.Start();
        }
        else
        {
            ChordText.Visibility = Visibility.Collapsed;
            _chordTimer.Stop();
        }
    }

    // ----- Status bar -----

    private void UpdateStatus()
    {
        var pane = Active;
        if (pane.Search is { } search)
        {
            ItemsText.Text = search.Rows is { } rows ? SearchModel.Hits(rows.Count) : "Searching…";
            SelectionText.Text = pane.FocusedHit?.Hit.Path ?? "";
            return;
        }
        ItemsText.Text = pane.Count == 1 ? "1 item" : $"{pane.Count:N0} items";
        var (count, bytes, anyFile) = pane.SelectionSize();
        var only = count == 1 ? pane.EntryAt(pane.Selection.SelectedUnordered.First()) : null;
        SelectionText.Text = count switch
        {
            0 => "",
            // A measured folder says how big it is; a file's size is in its row.
            1 when only is { IsFolder: true } && anyFile => $"1 selected · {only.Name}, {DisplayFormat.Bytes(bytes)}",
            1 => $"1 selected · {only?.Name}",
            // Folders have no size in the listing: the files' bytes are added up, and a folder's once measured.
            _ => anyFile ? $"{count:N0} selected, {DisplayFormat.Bytes(bytes)}" : $"{count:N0} selected",
        };
        if (SelectionText.Text != _selectionShown)
        {
            _selectionShown = SelectionText.Text;
            // What the status bar says about the selection, for the live checks that read it.
            Diag.Info(Target, "selection shown", new LogField("text", _selectionShown));
        }
    }

    private string _selectionShown = "";

    private void ShowNotice(string text, bool isError = false)
    {
        NoticeText.Text = text;
        NoticeText.Foreground = ThemeResources.Brush(isError ? "CbErrorTextBrush" : "CbStatusTextBrush");
        _noticeTimer.Stop();
        if (text.Length > 0)
        {
            // What the status bar told the user, for the log and the live checks that read it.
            Diag.Info(Target, "notice shown", new LogField("text", text), new LogField("error", isError));
            _noticeTimer.Start();
        }
    }

    private void SetPinnedFolders()
    {
        // Paths only; nothing is read from the disk (brief §1).
        string downloads;
        try
        {
            downloads = Windows.Storage.UserDataPaths.GetDefault().Downloads;
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        {
            downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
        _sidebar.SetPinned(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            downloads,
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            _shell.Pinned);
        _sidebar.SetActivePath(Active.Path);
    }
}
