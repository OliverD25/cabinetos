using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Settings;
using CabinetOS.Core.Shell;
using CabinetOS.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace CabinetOS;

// The compact overlay (docs/ui.md, "Compact overlay"): view.toggleCompactOverlay makes the window a small always-on-top
// drawer with one pane, no sidebar and no dock, and back. It is a mode, not a setting: what the window was (its place and
// size, the dual pane, the sidebar, the dock) is kept here and comes back on the way out, and none of the three layout
// keys is written to the file. Only the drawer's size is (ui.compactOverlay, Article 6), half a second after a resize.
// Not WinUI's CompactOverlay presenter: it keeps a video's aspect and has a caption of its own.
public sealed partial class MainWindow
{
    private const string CompactCommand = "view.toggleCompactOverlay";
    private const string CompactKey = "ui.compactOverlay";
    private const double CompactStatusGap = 6;
    private const double CompactSelectionWidth = 90;
    private const double CompactNarrowWidth = 420;
    private const double FullSelectionWidth = 360;

    // What the window was before the drawer. Dual and Sidebar follow the file while the drawer is on (a hand edit of
    // ui.dualPane or ui.sidebar is for the way back); Known is the size last applied or saved, to tell a resize from an echo.
    private sealed class CompactEntry
    {
        public required bool Dual { get; set; }

        public required bool Sidebar { get; set; }

        public required bool Dock { get; init; }

        public required int Active { get; init; }

        public required RectInt32 Bounds { get; init; }

        public required bool Maximized { get; init; }

        public required int? MinWidth { get; init; }

        public required CompactSize Known { get; set; }
    }

    // Null while the window is whole.
    private CompactEntry? _compact;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _compactTimer;

    // Saves of ui.compactOverlay on their way: a configuration read meanwhile may still hold the size before them.
    private int _compactWrites;

    private void SetUpCompact()
    {
        _compactTimer = DispatcherQueue.CreateTimer();
        _compactTimer.IsRepeating = false;
        _compactTimer.Interval = TimeSpan.FromMilliseconds(500);
        _compactTimer.Tick += (_, _) =>
        {
            _compactTimer.Stop();
            _ = SaveCompactSizeAsync();
        };
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidSizeChange && _compact is not null)
            {
                // The user is still resizing: the save waits for half a second without another resize.
                _compactTimer.Stop();
                _compactTimer.Start();
            }
        };
        RootGrid.SizeChanged += (_, _) =>
        {
            if (_compact is not null)
            {
                FitCompactChrome();
            }
        };
        // A rebound key changes what the status bar says leaves the drawer.
        _router.CommandsChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (_compact is not null)
            {
                UpdateLayoutText();
            }
        });
    }

    private void RegisterCompactCommands() => _router.RegisterUiHandler(CompactCommand, _ =>
    {
        if (_compact is null)
        {
            EnterCompactOverlay();
        }
        else
        {
            LeaveCompactOverlay();
        }
    });

    // ----- In and out -----

    private void EnterCompactOverlay()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter || RootGrid.XamlRoot is not { } root)
        {
            ShowNotice("The compact overlay needs the window in its normal state.", isError: true);
            return;
        }
        if (ShownPreview is not null)
        {
            // The proposal waits for an answer in the second pane, which the drawer does not have.
            ShowNotice("Answer the proposal first: the compact overlay has one pane.");
            return;
        }
        var scale = root.RasterizationScale;
        // A maximized or minimized window has no size of its own to come back to: the normal one is read after it is restored.
        var maximized = presenter.State == OverlappedPresenterState.Maximized;
        if (presenter.State != OverlappedPresenterState.Restored)
        {
            presenter.Restore();
        }
        var bounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var (size, saved) = CompactOverlayLayout.Decide(_settings.CompactOverlay, area.Width / scale, area.Height / scale);
        var width = (int)Math.Round(size.Width * scale);
        var height = (int)Math.Round(size.Height * scale);

        _compact = new CompactEntry
        {
            Dual = _dual,
            Sidebar = _sidebarOpen,
            Dock = _dockVisible,
            Active = _active,
            Bounds = bounds,
            Maximized = maximized,
            MinWidth = presenter.PreferredMinimumWidth,
            Known = size,
        };
        // The minimum first: a smaller window than the full one's 600 px is not allowed until it is lowered.
        presenter.PreferredMinimumWidth = (int)Math.Round(CompactOverlayLayout.MinWidth * scale);
        presenter.IsAlwaysOnTop = true;
        AppWindow.MoveAndResize(new RectInt32(
            CompactOverlayLayout.Place(bounds.X, width, area.X, area.Width),
            CompactOverlayLayout.Place(bounds.Y, height, area.Y, area.Height),
            width,
            height));
        ApplyCompactLayout(on: true);
        FocusActivePane();
        Diag.Info(Target, "compact overlay entered", new LogField("width", size.Width), new LogField("height", size.Height), new LogField("saved", saved),
            new LogField("previous_width", (int)Math.Round(bounds.Width / scale)), new LogField("previous_height", (int)Math.Round(bounds.Height / scale)),
            new LogField("previous_left", bounds.X), new LogField("previous_top", bounds.Y),
            new LogField("dual", _compact.Dual), new LogField("sidebar", _compact.Sidebar), new LogField("dock", _compact.Dock));
    }

    private void LeaveCompactOverlay()
    {
        if (_compact is not { } entry)
        {
            return;
        }
        // A resize the timer has not saved yet is not lost by leaving at once.
        if (_compactTimer?.IsRunning == true)
        {
            _compactTimer.Stop();
            _ = SaveCompactSizeAsync();
        }
        _compact = null;
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = false;
            presenter.PreferredMinimumWidth = entry.MinWidth;
            AppWindow.MoveAndResize(entry.Bounds);
        }
        ApplyCompactLayout(on: false, entry);
        var (restored, corner) = (AppWindow.Size, AppWindow.Position);
        if (entry.Maximized && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored } maximizing)
        {
            maximizing.Maximize();
        }
        FocusActivePane();
        Diag.Info(Target, "compact overlay left", new LogField("width", (int)Math.Round(restored.Width / scale)), new LogField("height", (int)Math.Round(restored.Height / scale)),
            new LogField("left", corner.X), new LogField("top", corner.Y), new LogField("dual", _dual), new LogField("sidebar", _sidebarOpen), new LogField("dock", _dockVisible), new LogField("maximized", entry.Maximized));
    }

    // One pane, no sidebar, no dock, through the code paths the toggles use and without their saves; or what the entry kept.
    private void ApplyCompactLayout(bool on, CompactEntry? entry = null)
    {
        if (on)
        {
            ApplyDual(false, keepTools: true);
            ApplySidebar(false);
            if (_dockVisible)
            {
                SetDockVisible(false);
            }
        }
        else if (entry is not null)
        {
            ApplyDual(entry.Dual);
            ApplySidebar(entry.Sidebar);
            SetDockVisible(entry.Dock);
            if (entry.Dual && entry.Active != _active)
            {
                SetActive(entry.Active);
            }
        }
        // What the drawer has no use for: the rail (a sidebar's buttons), the top row's buttons for the second pane and the dock,
        // and the Quick Open chip, which would run under the buttons on the right at the drawer's narrower widths (Ctrl+P stays).
        Rail.Visibility = !on && _railLayout ? Visibility.Visible : Visibility.Collapsed;
        DualButton.Visibility = TerminalButton.Visibility = QuickOpenChipFrame.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        // A status bar 345 to 465 px wide keeps what the mode says: the encoding and the palette's keycap give way, the gaps
        // narrow and a long selection text is cut short (its full name is in the list).
        EncodingText.Visibility = PaletteKeycap.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        StatusGrid.ColumnSpacing = on ? CompactStatusGap : WindowMetrics.Current.StatusBarGap;
        SelectionText.MaxWidth = on ? CompactSelectionWidth : FullSelectionWidth;
        FitCompactChrome();
        UpdateLayoutText();
    }

    // The narrowest drawers (under 420 px of content) have room for the item count and the mode's text only.
    private void FitCompactChrome() =>
        SelectionText.Visibility = _compact is not null && RootGrid.ActualWidth < CompactNarrowWidth ? Visibility.Collapsed : Visibility.Visible;

    // ----- Keeping the layout out of the file -----

    // While the drawer is on, a configuration that changes ui.dualPane or ui.sidebar is for the way back, not for the drawer.
    private bool HoldDualForCompact(bool dual)
    {
        if (_compact is null)
        {
            return false;
        }
        _compact.Dual = dual;
        return true;
    }

    private bool HoldSidebarForCompact(bool open)
    {
        if (_compact is null)
        {
            return false;
        }
        _compact.Sidebar = open;
        return true;
    }

    // A command that would bring a pane, the sidebar or the dock back says so, and does nothing: the mode is left first.
    private bool RefuseInCompact(string what)
    {
        if (_compact is null)
        {
            return false;
        }
        ShowNotice($"The compact overlay has no {what}: {CompactLeaveText()}.");
        return true;
    }

    // A command that needs the right pane shows it and saves that (ui.dualPane); false when the drawer is on, which has one.
    private bool EnsureDual()
    {
        if (_dual)
        {
            return true;
        }
        if (RefuseInCompact("second pane"))
        {
            return false;
        }
        ApplyDual(true);
        _ = PersistAsync(ShellState.DualPaneKey, true);
        return true;
    }

    // ----- The size -----

    // The status bar names the mode and what leaves it, in the place of the layout's name.
    private string CompactStatusText() => $"Compact overlay · {CompactLeaveText()}";

    private string CompactLeaveText() =>
        _router.Find(CompactCommand) is { } command && ShellMenu.FirstKeys(command.Keys) is { } keys
            ? $"{keys} leaves it"
            : "leave it from the command palette";

    // The window's size in whole DIPs as the file keeps it, or null while the drawer is off or the window is not in its normal state.
    private CompactSize? CurrentCompactSize()
    {
        if (_compact is null || AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored } || RootGrid.XamlRoot is not { } root)
        {
            return null;
        }
        return CompactOverlayLayout.ToSetting(AppWindow.Size.Width, AppWindow.Size.Height, root.RasterizationScale);
    }

    private static bool SameSize(CompactSize a, CompactSize b) => Math.Abs(a.Width - b.Width) <= 1 && Math.Abs(a.Height - b.Height) <= 1;

    // Half a second after the last resize: the size in ui.compactOverlay, through the core. A size that is the one already
    // applied or saved (the entry's own, or the file's coming back) is not a resize.
    private async Task SaveCompactSizeAsync()
    {
        if (_compact is not { } entry || CurrentCompactSize() is not { } size || SameSize(size, entry.Known))
        {
            return;
        }
        entry.Known = size;
        _compactWrites++;
        try
        {
            var refusal = await _settingsWriter.SetOrRefusalAsync(CompactKey, CompactOverlayLayout.ToJson(size));
            if (refusal is null)
            {
                Diag.Info(Target, "compact overlay size saved", new LogField("width", size.Width), new LogField("height", size.Height));
            }
            else
            {
                Diag.Warn(Target, "compact overlay size not saved", new LogField("error", refusal));
            }
        }
        finally
        {
            _compactWrites--;
        }
    }

    // ui.compactOverlay after an edit of the file (or another window's save): while the drawer is on it takes that size at once,
    // and a size that is the window's own (our save coming back) changes nothing. Off, the next entry reads it.
    private void ApplyCompactSettings(UiSettings settings, UiSettings previous)
    {
        if (_compact is not { } entry || _compactWrites > 0 || settings.CompactOverlay == previous.CompactOverlay
            || AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored } || RootGrid.XamlRoot is not { } root)
        {
            return;
        }
        var scale = root.RasterizationScale;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var (size, _) = CompactOverlayLayout.Decide(settings.CompactOverlay, area.Width / scale, area.Height / scale);
        if (SameSize(size, entry.Known))
        {
            return;
        }
        entry.Known = size;
        AppWindow.Resize(new SizeInt32((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale)));
        Diag.Info(Target, "compact overlay follows the configuration", new LogField("width", size.Width), new LogField("height", size.Height));
    }
}
