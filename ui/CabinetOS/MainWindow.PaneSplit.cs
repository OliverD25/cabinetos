using System.Globalization;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Settings;
using CabinetOS.Views;
using Microsoft.UI.Xaml;

namespace CabinetOS;

// The divider between the two file panes (docs/ui.md, "The divider between the panes"). The left pane's share of the width
// the two panes have is ui.paneSplit: 0.2 to 0.8, or null for equal. A drag of the divider writes it once, when the pointer
// lets go; a double-click on the divider and view.equalPanes write null; the file's value applies at start and at
// config_changed (the three ways of Article 6 and 7: the window, the palette, the file). The window lays the share on the two
// star columns of PanesGrid, so the proportion survives a resize of the window, the sidebar or the dock. PaneSplit decides
// the numbers; this file lays them onto the window.
public sealed partial class MainWindow
{
    private const string PaneSplitTarget = "cabinetos_ui::panesplit";
    private const string PaneSplitKey = "ui.paneSplit";

    // ui.paneSplit as the window knows it: its own last write, or what the file said. null is equal panes.
    private double? _paneSplit;

    // The share laid out now: the setting kept within what this window's width allows, or the drag's share while it goes on.
    private double _paneSplitShare = PaneSplit.Equal;
    private bool _paneSplitDragging;
    private double _paneSplitDragStart;
    private double _paneSplitDragStartShare;

    // Writes on their way: the configuration the core sends meanwhile may still hold the old value and must not undo the drag.
    private int _paneSplitWrites;

    private void SetUpPaneSplit()
    {
        PaneSplitter.DragStarted += StartPaneSplitDrag;
        PaneSplitter.Dragged += delta => ResizePanes(PaneSplit.Drag(_paneSplitDragStart, delta, PanesGrid.ActualWidth, MinPaneWidth()));
        PaneSplitter.DragCompleted += EndPaneSplitDrag;
        PaneSplitter.DoubleClicked += () => SetEqualPanes("divider");
        PanesGrid.SizeChanged += (_, _) => ApplyPaneSplit();
    }

    private void RegisterPaneSplitCommands() => _router.RegisterUiHandler("view.equalPanes", _ => SetEqualPanes("command"));

    private static double MinPaneWidth() => PaneSplit.MinPaneWidth(WindowMetrics.Current);

    // The columns follow the share: the left column's star is the share and the right column's the rest, so the panes are
    // that share of whatever width they have. With one pane the left takes all of it and the divider is gone.
    private void ApplyPaneSplit()
    {
        var available = PanesGrid.ActualWidth;
        if (!_paneSplitDragging)
        {
            // Before the first layout there is no width to hold a minimum against: the setting as it is.
            _paneSplitShare = available > 0
                ? PaneSplit.Resolve(_paneSplit, available, MinPaneWidth())
                : _paneSplit is { } share ? Math.Clamp(share, PaneSplit.MinShare, PaneSplit.MaxShare) : PaneSplit.Equal;
        }
        LeftColumn.Width = new GridLength(_dual ? _paneSplitShare : 1, GridUnitType.Star);
        RightColumn.Width = _dual ? new GridLength(1 - _paneSplitShare, GridUnitType.Star) : new GridLength(0);
        // A gap too narrow to grab (a theme's gap 0) keeps a 6 px handle, laid over the edges it joins: it is centred on the
        // seam between the two columns, which is the middle of the gap.
        var width = Math.Max(WindowMetrics.Current.Gap, 6);
        PaneSplitter.Width = width;
        PaneSplitter.Margin = new Thickness(0, 0, -width / 2, 0);
        PaneSplitter.Visibility = _dual ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartPaneSplitDrag()
    {
        _paneSplitDragging = true;
        _paneSplitDragStart = LeftColumn.ActualWidth;
        _paneSplitDragStartShare = _paneSplitShare;
    }

    private void ResizePanes(double share)
    {
        _paneSplitShare = share;
        ApplyPaneSplit();
    }

    // One write per drag, when it ends. A press that moved nothing (half of a double-click, or a drag in a window with no room to
    // move it) changes nothing and writes nothing.
    private void EndPaneSplitDrag()
    {
        _paneSplitDragging = false;
        if (Math.Abs(_paneSplitShare - _paneSplitDragStartShare) < 1e-9)
        {
            return;
        }
        _paneSplit = PaneSplit.ToSetting(_paneSplitShare);
        ApplyPaneSplit();
        LogPaneSplit("drag");
        _ = PersistPaneSplitAsync(_paneSplit);
    }

    // The divider's double-click and view.equalPanes: equal panes, and null in the file.
    private void SetEqualPanes(string how)
    {
        if (_paneSplit is null)
        {
            return;
        }
        _paneSplit = null;
        ApplyPaneSplit();
        LogPaneSplit(how);
        _ = PersistPaneSplitAsync(null);
    }

    private async Task PersistPaneSplitAsync(double? value)
    {
        _paneSplitWrites++;
        try
        {
            var written = value is { } share
                ? await _settingsWriter.SetNumberAsync(PaneSplitKey, share)
                : await _settingsWriter.SetNullAsync(PaneSplitKey);
            if (!written)
            {
                Diag.Warn(PaneSplitTarget, "the pane split was not saved", new LogField("share", value));
            }
        }
        finally
        {
            _paneSplitWrites--;
        }
    }

    // ui.paneSplit at start and after an edit of the file or another window's drag. The window's own write on its way, and a drag,
    // are not undone by a configuration read before they landed; the configuration that follows the window's own save changes nothing.
    private void ApplyStoredPaneSplit(UiSettings settings, UiSettings previous, bool firstStart)
    {
        if ((!firstStart && settings.PaneSplitShare == previous.PaneSplitShare) || _paneSplitWrites > 0 || _paneSplitDragging
            || settings.PaneSplitShare == _paneSplit)
        {
            return;
        }
        _paneSplit = settings.PaneSplitShare;
        ApplyPaneSplit();
        LogPaneSplit(firstStart ? "start" : "config");
    }

    // The snapshot aid's until:pane-split:<share or none>.
    private bool PaneSplitIs(string text) => text == "none"
        ? _paneSplit is null
        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var share) && _paneSplit is { } held && Math.Abs(held - share) < 1e-9;

    // "pane split": how it changed (drag, divider, command, config or start), the setting now and the share laid out, with the room
    // the panes share, so that the line says what a reader needs to judge the drag.
    private void LogPaneSplit(string how) => Diag.Info(PaneSplitTarget, "pane split", new LogField("how", how),
        new LogField("setting", _paneSplit), new LogField("share", Math.Round(_paneSplitShare, 4)),
        new LogField("room", Math.Round(PanesGrid.ActualWidth, 1)));

    // ----- The snapshot aid's steps for the dividers -----

    // pane-divider:<pixels> drags the divider between the panes until the left pane's column is that many pixels wide, and lets go
    // (the pointer's own start, move and release); pane-divider-reset is the double-click on it; sidebar-divider-reset is the
    // double-click on the sidebar's divider; pane-split:<label> logs what the panes and the divider show, once the layout has
    // settled. The three that write do not wait: an until:split-saved right after them sees the write end.
    private async Task RunDividerStepAsync(string kind, string argument)
    {
        switch (kind)
        {
            case "pane-divider" when double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out var width):
                StartPaneSplitDrag();
                ResizePanes(PaneSplit.Drag(_paneSplitDragStart, width - _paneSplitDragStart, PanesGrid.ActualWidth, MinPaneWidth()));
                EndPaneSplitDrag();
                break;
            case "pane-divider-reset":
                SetEqualPanes("divider");
                break;
            case "sidebar-divider-reset":
                ResetSidebarWidth();
                break;
            case "pane-split":
                // The panes' widths come from XAML's next frames, which a busy machine draws late.
                await SettleFramesAsync();
                LogPaneSplitShown(argument);
                break;
        }
    }

    private void LogPaneSplitShown(string label) => Diag.Info(PaneSplitTarget, "pane split shown",
        new LogField("label", label), new LogField("setting", _paneSplit), new LogField("share", Math.Round(_paneSplitShare, 4)),
        new LogField("dual", _dual), new LogField("splitter_visible", PaneSplitter.Visibility == Visibility.Visible),
        new LogField("splitter_width", Math.Round(PaneSplitter.ActualWidth, 1)), new LogField("room", Math.Round(PanesGrid.ActualWidth, 1)),
        new LogField("left_width", Math.Round(LeftSide.ActualWidth, 1)), new LogField("right_width", _dual ? Math.Round(RightSide.ActualWidth, 1) : null),
        new LogField("left_column", Math.Round(LeftColumn.ActualWidth, 1)), new LogField("min_pane", Math.Round(MinPaneWidth(), 1)));
}
