using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Settings;
using CabinetOS.Services;
using CabinetOS.ViewModels;
using CabinetOS.Views;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Windows.UI.Core;

namespace CabinetOS;

/// <summary>
/// The window (design view A with the palette of view B). It is the
/// composition root: it owns the connection to the core, the command router,
/// the key state machine, the panes and the palette, and turns every button,
/// key and menu choice into a command ID for the router (brief §5).
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string Target = "cabinetos_ui::shell";

    private readonly CoreSession _session = new();
    private readonly CommandRouter _router;
    private readonly ChordStateMachine _keys = new(() => Environment.TickCount64);
    private readonly PaneModel[] _panes;
    private readonly FilePane[] _paneViews;
    private readonly SidebarModel _sidebar = new();
    private readonly PaletteModel _palette;
    private readonly DispatcherQueueTimer _chordTimer;
    private readonly DispatcherQueueTimer _noticeTimer;
    private readonly bool _selfTestCrash;
    private UiSettings _settings = UiSettings.Defaults;
    private bool _dual = true;
    private bool _sidebarOpen = true;
    private int _active;
    private bool _closing;
    private bool _closed;
    private bool _started;
    private bool _volumesLogged;
    private readonly Queue<DateTime> _restarts = new();

    /// <summary>Creates the window; the core starts once the content is loaded.</summary>
    public MainWindow(bool selfTestCrash)
    {
        InitializeComponent();
        _selfTestCrash = selfTestCrash;
        _router = new CommandRouter(_session);
        _panes = [new PaneModel(0, _session), new PaneModel(1, _session)];
        _paneViews = [LeftPane, RightPane];
        _palette = new PaletteModel(_session, _router);

        SetUpWindow();
        _chordTimer = DispatcherQueue.CreateTimer();
        _chordTimer.IsRepeating = false;
        _chordTimer.Tick += (_, _) => _keys.ExpireIfDue();
        _noticeTimer = DispatcherQueue.CreateTimer();
        _noticeTimer.IsRepeating = false;
        _noticeTimer.Interval = TimeSpan.FromSeconds(5);
        _noticeTimer.Tick += (_, _) => NoticeText.Text = "";

        for (var i = 0; i < _panes.Length; i++)
        {
            var pane = _panes[i];
            var view = _paneViews[i];
            view.Model = pane;
            view.RunCommand = (id, trigger) => _router.ExecuteAsync(id, trigger: trigger);
            view.Activated += OnPaneActivated;
            pane.PropertyChanged += OnPaneChanged;
            pane.Notice += text => ShowNotice(text, isError: true);
        }
        _panes[0].IsActive = true;

        SidebarView.Model = _sidebar;
        SidebarView.Navigate += path => _ = _router.ExecuteAsync("go.toPath", CommandArgs.With("path", path), "sidebar");
        SetPinnedFolders();

        Palette.Model = _palette;
        Palette.RunCommand = (id, args, trigger) => _router.ExecuteAsync(id, args, trigger);
        _palette.Closed += () => _paneViews[_active].Focus(FocusState.Programmatic);
        _palette.KeymapUpdated += keymap => _keys.SetKeymap(Keymap.From(keymap));

        BackButton.Click += (_, _) => _ = _router.ExecuteAsync("go.back", trigger: "button");
        ForwardButton.Click += (_, _) => _ = _router.ExecuteAsync("go.forward", trigger: "button");
        UpButton.Click += (_, _) => _ = _router.ExecuteAsync("go.up", trigger: "button");
        DualButton.Click += (_, _) => _ = _router.ExecuteAsync("view.toggleDualPane", trigger: "button");
        PaletteButton.Click += (_, _) => _ = _router.ExecuteAsync("palette.show", trigger: "button");
        PaletteKeycap.Click += (_, _) => _ = _router.ExecuteAsync("palette.show", trigger: "button");
        CrumbBar.Tapped += OnCrumbBarTapped;
        AddressEdit.KeyDown += OnAddressKeyDown;
        AddressEdit.LostFocus += (_, _) => EndAddressEdit();

        RegisterCommands();
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
        };
        RootGrid.Loaded += OnLoaded;
        AppWindow.Closing += OnClosing;

        if (FrameMonitor.Enabled)
        {
            new FrameMonitor().Start();
        }
        ApplyDual(true);
        UpdateStatus();
        UpdateNavigationButtons();
    }

    private PaneModel Active => _panes[_active];

    // ----- Window -----

    private void SetUpWindow()
    {
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
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
            presenter.PreferredMinimumWidth = (int)(760 * root.RasterizationScale);
            presenter.PreferredMinimumHeight = (int)(480 * root.RasterizationScale);
        }
        if (!_started)
        {
            _started = true;
            _ = StartAsync();
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
        // The design's clamp(180px, 20%, 224px) and clamp(120px, 22%, 240px).
        SidebarView.Width = Math.Clamp(windowWidth * 0.2, 180, 224);
        SearchBox.Width = Math.Clamp(windowWidth * 0.22, 120, 240);
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closed)
        {
            return;
        }
        // The window hides at once; the core gets its shutdown without the UI thread waiting.
        args.Cancel = true;
        if (_closing)
        {
            return;
        }
        _closing = true;
        sender.Hide();
        Diag.Info(Target, "window closing");
        await _session.StopAsync();
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
        await DevSnapshots.RenderAsync(RootGrid, "window");
        if (DevSnapshots.Query is { } query)
        {
            await _router.ExecuteAsync("palette.show", trigger: "snapshot");
            Palette.TypeQuery(query);
            await Task.Delay(800);
            await DevSnapshots.RenderAsync(RootGrid, "palette");
        }
    }

    private async Task LoadAsync(bool firstStart)
    {
        try
        {
            await ReadConfigAsync(firstStart);
            var keymap = ReadKeymapAsync();
            var commands = _router.RefreshAsync();
            var volumes = ReadVolumesAsync();
            if (firstStart)
            {
                await OpenFirstFoldersAsync();
            }
            else
            {
                foreach (var pane in _panes)
                {
                    pane.ForgetListing();
                    if (pane.Path.Length > 0)
                    {
                        await pane.ReloadAsync();
                    }
                }
            }
            await Task.WhenAll(keymap, commands, volumes);
        }
        catch (IOException error)
        {
            Diag.Warn(Target, "loading the session failed", new LogField("error", error.Message));
        }
    }

    private async Task OpenFirstFoldersAsync()
    {
        // Dual pane on first start (PLAN.md, consistency check B): the profile on
        // the left, its Documents on the right when the profile lists one, else C:\.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await _panes[0].NavigateAsync(profile);
        var left = _panes[0].View;
        var documents = left?.IndexOfName("Documents") ?? -1;
        var right = documents >= 0 && left!.IsFolder(documents) ? DisplayFormat.Join(profile, "Documents") : @"C:\";
        await _panes[1].NavigateAsync(right);
        _paneViews[_active].Focus(FocusState.Programmatic);
    }

    private async Task ReadConfigAsync(bool firstStart)
    {
        var reply = await _session.RequestAsync(new GetConfigRequest());
        if (reply is ConfigReply config)
        {
            ApplySettings(UiSettings.FromConfig(config.Config), firstStart);
        }
    }

    private async Task ReadKeymapAsync()
    {
        var reply = await _session.RequestAsync(new GetKeymapRequest());
        if (reply is KeymapReply keymap)
        {
            _keys.SetKeymap(Keymap.From(keymap.ToData()));
        }
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
        if (firstStart || settings.DualPane != previous.DualPane)
        {
            ApplyDual(settings.DualPane);
        }
        if (firstStart || settings.Sidebar != previous.Sidebar)
        {
            ApplySidebar(settings.Sidebar);
        }
        LayoutText.Text = settings.Layout switch
        {
            "right" => "Terminal: right",
            "rail" => "Activity rail",
            _ => "Terminal: bottom",
        };
        if (firstStart && settings.Layout == "rail")
        {
            Diag.Info(Target, "the rail layout arrives in a later phase; showing the sidebar");
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

    private void OnCoreEvent(CoreEvent coreEvent)
    {
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
                _ = ReadConfigSafelyAsync();
                break;
            case ConfigErrorEvent error:
                var where = error.Line is { } line ? $" line {line}, column {error.Column}" : "";
                ShowNotice($"cabinetos.json{where}: {error.Message}", isError: true);
                break;
            case KeymapChangedEvent keymap:
                _keys.SetKeymap(Keymap.From(keymap.Keymap));
                _ = RefreshCommandsSafelyAsync();
                break;
            case PluginStateChangedEvent or PluginCrashedEvent:
                _ = RefreshCommandsSafelyAsync();
                break;
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

    private async Task RefreshCommandsSafelyAsync()
    {
        try
        {
            await _router.RefreshAsync();
            await _palette.RefreshAsync();
        }
        catch (IOException error)
        {
            Diag.Info(Target, "cannot read the command list", new LogField("error", error.Message));
        }
    }

    private async Task OnCoreLostAsync(string reason)
    {
        if (_closing)
        {
            return;
        }
        // The core stops on any panic (docs/diagnostics.md); the UI starts it
        // again, at most three times a minute.
        var now = DateTime.UtcNow;
        while (_restarts.Count > 0 && now - _restarts.Peek() > TimeSpan.FromMinutes(1))
        {
            _restarts.Dequeue();
        }
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
        await LoadAsync(firstStart: false);
        ShowNotice("The core is running again.");
    }

    private async Task ShowStartFailureAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
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
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _started = true;
            await StartAsync();
        }
        else
        {
            Close();
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
        _router.RegisterUiHandler("view.toggleDualPane", _ => ApplyDual(!_dual));
        _router.RegisterUiHandler("view.focusOtherPane", _ => FocusOtherPane());
        _router.RegisterUiHandler("view.toggleSidebar", _ => ApplySidebar(!_sidebarOpen));
        _router.RegisterUiHandler("go.toPath", GoToPathAsync);

        // Navigation the core's registry does not list yet: UI-only commands.
        _router.RegisterLocal("go.back", invocation => Active.GoBackAsync(invocation.RequestId));
        _router.RegisterLocal("go.forward", invocation => Active.GoForwardAsync(invocation.RequestId));
        _router.RegisterLocal("go.up", invocation => Active.GoUpAsync(invocation.RequestId));
        _router.RegisterLocal("pane.openSelected", invocation => Active.OpenSelectedAsync(invocation.RequestId));
        _router.RegisterLocal("keys.rebind", invocation =>
        {
            if (CommandArgs.Text(invocation.Args, "command") is { } command)
            {
                _palette.StartRecording(command);
            }
        });

        _router.Completed += OnCommandCompleted;
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
            Title = title,
            Content = new TextBlock { Text = text.ToString().TrimEnd(), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "Close",
        };
        await dialog.ShowAsync();
    }

    private async Task GoToPathAsync(CommandInvocation invocation)
    {
        if (CommandArgs.Text(invocation.Args, "path") is { Length: > 0 } path)
        {
            EndAddressEdit();
            await Active.NavigateAsync(path, invocation.RequestId);
            _paneViews[_active].Focus(FocusState.Programmatic);
        }
        else
        {
            BeginAddressEdit();
        }
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
            _palette.Open();
        }
    }

    private void CloseOverlay()
    {
        if (_palette.IsOpen)
        {
            _palette.Close();
        }
        else if (AddressEdit.Visibility == Visibility.Visible)
        {
            EndAddressEdit();
            _paneViews[_active].Focus(FocusState.Programmatic);
        }
    }

    private void ApplyDual(bool dual)
    {
        _dual = dual;
        RightColumn.Width = dual ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        RightPane.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
        LeftPane.Margin = dual ? new Thickness(0, 0, 4, 0) : new Thickness(0);
        DualLabel.Text = dual ? "Dual" : "Single";
        var brush = dual ? ThemeResources.Brush("CbAccentBrush") : ThemeResources.Brush("CbTextSecondaryBrush");
        DualIcon.Foreground = brush;
        DualLabel.Foreground = brush;
        foreach (var pane in _panes)
        {
            pane.IsDual = dual;
        }
        if (!dual && _active != 0)
        {
            SetActive(0);
            _paneViews[0].Focus(FocusState.Programmatic);
        }
    }

    private void ApplySidebar(bool open)
    {
        _sidebarOpen = open;
        SidebarView.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FocusOtherPane()
    {
        if (_dual)
        {
            _paneViews[1 - _active].Focus(FocusState.Keyboard);
        }
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
    }

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender != Active)
        {
            return;
        }
        switch (e.PropertyName)
        {
            case nameof(PaneModel.Path):
                UpdateCrumbs();
                _sidebar.SetActivePath(Active.Path);
                UpdateNavigationButtons();
                break;
            case nameof(PaneModel.CanGoBack) or nameof(PaneModel.CanGoForward) or nameof(PaneModel.CanGoUp):
                UpdateNavigationButtons();
                break;
            case nameof(PaneModel.Count) or nameof(PaneModel.SelectedName) or nameof(PaneModel.Rows):
                UpdateStatus();
                break;
        }
    }

    // ----- Address bar -----

    private void UpdateCrumbs()
    {
        Crumbs.Children.Clear();
        var crumbs = DisplayFormat.Crumbs(Active.Path);
        for (var i = 0; i < crumbs.Count; i++)
        {
            var (label, path) = crumbs[i];
            var button = new Button
            {
                Content = label,
                Style = (Style)ThemeResources.Get("CbCrumbButtonStyle")!,
                Tag = path,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, path);
            button.Click += (_, _) => _ = _router.ExecuteAsync("go.toPath", CommandArgs.With("path", path), "crumb");
            Crumbs.Children.Add(button);
            if (i < crumbs.Count - 1)
            {
                Crumbs.Children.Add(new FontIcon
                {
                    Glyph = "\uE76C",
                    FontSize = 10,
                    Opacity = 0.5,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
        }
        SearchBox.PlaceholderText = $"Search {Active.FolderName}";
        CrumbScroller.UpdateLayout();
        CrumbScroller.ChangeView(CrumbScroller.ScrollableWidth, null, null, disableAnimation: true);
    }

    private void OnCrumbBarTapped(object sender, TappedRoutedEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != CrumbBar; element = VisualTreeHelper.GetParent(element))
        {
            if (element is Button)
            {
                return;
            }
        }
        _ = _router.ExecuteAsync("go.toPath", trigger: "mouse");
    }

    private void BeginAddressEdit()
    {
        AddressEdit.Text = Active.Path;
        AddressEdit.Visibility = Visibility.Visible;
        CrumbBar.Visibility = Visibility.Collapsed;
        AddressEdit.Focus(FocusState.Programmatic);
        AddressEdit.SelectAll();
    }

    private void EndAddressEdit()
    {
        if (AddressEdit.Visibility == Visibility.Collapsed)
        {
            return;
        }
        AddressEdit.Visibility = Visibility.Collapsed;
        CrumbBar.Visibility = Visibility.Visible;
    }

    private void OnAddressKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            var path = AddressEdit.Text.Trim().Trim('"');
            _ = _router.ExecuteAsync("go.toPath", CommandArgs.With("path", path), "address");
        }
    }

    // ----- Keyboard -----

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var virtualKey = (int)e.Key;
        var modifiers = CurrentModifiers();
        if (_palette.IsRecording)
        {
            e.Handled = true;
            if (e.Key == VirtualKey.Escape)
            {
                _palette.CancelRecording();
            }
            else if (!KeyNames.IsModifier(virtualKey) && KeyNames.FromVirtualKey(virtualKey) is { } recorded)
            {
                _palette.Record(new KeyCombo(modifiers, recorded));
            }
            return;
        }
        if (KeyNames.IsModifier(virtualKey) || KeyNames.FromVirtualKey(virtualKey) is not { } name)
        {
            return;
        }
        var combo = new KeyCombo(modifiers, name);
        switch (_keys.OnKey(combo, CurrentContexts()))
        {
            case KeyOutcome.Run run:
                e.Handled = true;
                _ = _router.ExecuteAsync(run.Command, trigger: "key");
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
            contexts.Add(KeyContexts.TextInput);
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

    // ----- Status bar and command bar -----

    private void UpdateStatus()
    {
        var pane = Active;
        ItemsText.Text = pane.Count == 1 ? "1 item" : $"{pane.Count:N0} items";
        SelectionText.Text = pane.SelectedName is { } name ? $"1 selected · {name}" : "";
    }

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = Active.CanGoBack;
        ForwardButton.IsEnabled = Active.CanGoForward;
        UpButton.IsEnabled = Active.CanGoUp;
    }

    private void ShowNotice(string text, bool isError = false)
    {
        NoticeText.Text = text;
        NoticeText.Foreground = ThemeResources.Brush(isError ? "CbErrorTextBrush" : "CbStatusTextBrush");
        _noticeTimer.Stop();
        if (text.Length > 0)
        {
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
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
        _sidebar.SetPinned(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            downloads,
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }
}
