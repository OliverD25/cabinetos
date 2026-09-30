using System.Globalization;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Shell;
using CabinetOS.Services;
using CabinetOS.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;

namespace CabinetOS;

// The shell of Phase 16 (the creator's SHELL_REDESIGN.md; docs/ui.md, "The top row", "The breadcrumb row"): one top
// row that is also the window's drag area, and in each pane a breadcrumb row with Back, Forward and Up. What they
// decide without a window is in CabinetOS.Core.Shell, with tests.
public sealed partial class MainWindow
{
    private const string ShellTarget = "cabinetos_ui::shell";

    // Windows draws the caption buttons this high (TitleBarHeightOption.Standard): the top row is never lower.
    private const double CaptionButtonsHeight = 32;

    private PaneCrumbs[] _crumbViews = null!;
    private CommandCenterPlace _centerPlace = new(false, 0, 0);
    private string _dragRegions = "";

    private void SetUpShell()
    {
        _crumbViews = [LeftCrumbs, RightCrumbs];
        for (var i = 0; i < _crumbViews.Length; i++)
        {
            _crumbViews[i].PaneIndex = i;
            _crumbViews[i].RunCommand = (id, args, trigger) => _router.ExecuteAsync(id, args, trigger);
        }
        MenuButton.Click += (_, _) => _ = _router.ExecuteAsync("menu.show", trigger: "button");
        CommandCenter.Click += (_, _) => _ = _router.ExecuteAsync("quickOpen.show", trigger: "button");
        SettingsButton.Click += (_, _) => _ = _router.ExecuteAsync("settings.open", trigger: "button");
        _palette.Opened += UpdatePaletteButton;
        _palette.Closed += UpdatePaletteButton;
        // The top row's layout: the command center between its clusters, and the drag area around its controls.
        TopRowGrid.SizeChanged += (_, _) => LayOutTopRow();
        TopLeft.SizeChanged += (_, _) => LayOutTopRow();
        TopRight.SizeChanged += (_, _) => LayOutTopRow();
        CommandCenterFrame.SizeChanged += (_, _) => UpdateDragRegions();
    }

    // ----- The top row -----

    /// <summary>
    /// Places the command center (<see cref="TopRowLayout"/>): centred in the
    /// window, clamp(200px, 34%, 380px) wide, never over the clusters beside
    /// it, hidden below 640 px of window width. Then the drag area follows.
    /// </summary>
    private void LayOutTopRow()
    {
        var width = RootGrid.ActualWidth;
        var leftEnd = TopLeft.ActualWidth + TopLeft.Margin.Left;
        var rightStart = TopRowGrid.ActualWidth - TopRight.ActualWidth;
        var place = TopRowLayout.Place(width, leftEnd, rightStart);
        if (place != _centerPlace)
        {
            _centerPlace = place;
            CommandCenterFrame.Visibility = place.Visible ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(CommandCenterFrame, place.Left);
            CommandCenterFrame.Width = Math.Max(0, place.Width);
        }
        Canvas.SetTop(CommandCenterFrame, Math.Max(0, (TopRowGrid.ActualHeight - CommandCenterFrame.Height) / 2));
        UpdateDragRegions();
    }

    // Windows' caption buttons take the right end of the row: RightInset in physical pixels, once the window shows.
    private void FitCaptionSpace()
    {
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        var inset = AppWindow.TitleBar.RightInset / scale;
        CaptionSpace.Width = inset > 0 ? inset : 3 * WindowMetrics.Current.CaptionButtonWidth;
    }

    /// <summary>
    /// The top row is the window's drag area (SetTitleBar), except over its
    /// controls: those rectangles pass the pointer through, so a click on the
    /// menu, the pill, the command center or a view button reaches it.
    /// </summary>
    private void UpdateDragRegions()
    {
        if (RootGrid.XamlRoot is not { } root || TopBar.ActualWidth <= 0)
        {
            return;
        }
        var scale = root.RasterizationScale;
        var rects = new List<RectInt32>();
        foreach (var element in new FrameworkElement[] { MenuButton, WorkspacePillFrame, CommandCenterFrame, DualButton, TerminalButton, MarketplaceButton, PaletteButton, SettingsButton })
        {
            if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0)
            {
                continue;
            }
            var bounds = element.TransformToVisual(null).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            rects.Add(new RectInt32(
                (int)Math.Floor(bounds.X * scale),
                (int)Math.Floor(bounds.Y * scale),
                (int)Math.Ceiling(bounds.Width * scale),
                (int)Math.Ceiling(bounds.Height * scale)));
        }
        var text = string.Join(";", rects.Select(r => $"{r.X},{r.Y},{r.Width},{r.Height}"));
        if (text == _dragRegions)
        {
            return;
        }
        _dragRegions = text;
        try
        {
            InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, [.. rects]);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Diag.Warn(ShellTarget, "the top row's controls could not be taken out of the drag area", new LogField("error", error.Message));
        }
    }

    // The view buttons show what is on: the accent for dual panes, the terminal, the marketplace, the palette.
    private void UpdateDualButton() => DualIcon.Foreground = ThemeResources.Brush(_dual ? "CbAccentBrush" : "CbTextSecondaryBrush");

    private void UpdatePaletteButton() => PaletteIcon.Foreground = ThemeResources.Brush(_palette.IsOpen ? "CbAccentBrush" : "CbTextSecondaryBrush");

    // ----- The breadcrumb rows -----

    /// <summary>Each pane's breadcrumb row shows its folder; the active pane's row is tinted.</summary>
    private void UpdateCrumbs()
    {
        if (_crumbViews is null)
        {
            return;
        }
        for (var i = 0; i < _crumbViews.Length; i++)
        {
            _crumbViews[i].Show(_panes[i].Path, _dual);
            _crumbViews[i].IsActivePane = _dual ? i == _active : i == 0;
        }
    }

    /// <summary>Each pane's Back, Forward and Up: Forward without a forward entry is drawn at 30 % (NavState).</summary>
    private void UpdateNavigationButtons()
    {
        if (_crumbViews is null)
        {
            return;
        }
        for (var i = 0; i < _crumbViews.Length; i++)
        {
            var pane = _panes[i];
            _crumbViews[i].SetNav(new NavState(pane.CanGoBack, pane.CanGoForward, pane.CanGoUp));
        }
    }

    // The pane a command names ({"pane": 0 or 1}, the breadcrumb row's buttons), else the active one.
    private int PaneOf(Core.Commands.CommandInvocation invocation) =>
        CommandArgs.Number(invocation.Args, "pane") is { } named and <= 1 ? (int)named : _active;

    /// <summary>Ctrl+L: the active pane's breadcrumb row becomes a text box with its path.</summary>
    private void BeginAddressEdit(int pane)
    {
        if (pane != _active)
        {
            SetActive(pane);
        }
        _crumbViews[pane].BeginEdit(_panes[pane].Path);
    }

    /// <summary>Every breadcrumb row shows its segments again.</summary>
    private void EndAddressEdit()
    {
        if (_crumbViews is null)
        {
            return;
        }
        foreach (var crumbs in _crumbViews)
        {
            crumbs.EndEdit();
        }
    }

    private bool IsEditingAddress => _crumbViews is not null && _crumbViews.Any(c => c.IsEditing);

    // ----- The snapshot aid -----

    // shell:<label>: what the top row and each pane's rows show, in the log ("shell state"), for the checks that read it.
    private void LogShellState(string label)
    {
        var fields = new List<LogField>
        {
            new("label", label),
            new("window", string.Create(CultureInfo.InvariantCulture, $"{RootGrid.ActualWidth:0}x{RootGrid.ActualHeight:0}")),
            new("top_row", Math.Round(TopBar.ActualHeight, 1)),
            new("command_center", _centerPlace.Visible ? string.Create(CultureInfo.InvariantCulture, $"{_centerPlace.Left:0}+{_centerPlace.Width:0}") : "hidden"),
            new("left_cluster_end", Math.Round(TopLeft.ActualWidth, 1)),
            new("right_cluster_start", Math.Round(TopRowGrid.ActualWidth - TopRight.ActualWidth, 1)),
            new("workspace", WorkspaceName.Text),
            new("branch", WorkspaceBranch.Visibility == Visibility.Visible ? WorkspaceBranch.Text : ""),
            new("active_pane", _active),
        };
        for (var i = 0; i < _panes.Length; i++)
        {
            var pane = _panes[i];
            fields.Add(new($"pane{i}_path", pane.Path));
            fields.Add(new($"pane{i}_crumbs", _crumbViews[i].Text));
            fields.Add(new($"pane{i}_nav", _crumbViews[i].NavText));
            fields.Add(new($"pane{i}_shown", pane.ShownCount));
            fields.Add(new($"pane{i}_count", pane.Count));
            fields.Add(new($"pane{i}_selected", string.Join("|", pane.SelectedNames().Take(20))));
            fields.Add(new($"pane{i}_all_selected", string.Join("|", pane.AllSelectedNames().Take(20))));
            fields.Add(new($"pane{i}_cursor", pane.FocusName ?? ""));
            fields.Add(new($"pane{i}_scroll", Math.Round(_paneViews[i].ScrollOffset, 1)));
            fields.Add(new($"pane{i}_find", pane.Find.Query ?? ""));
            fields.Add(new($"pane{i}_find_open", _findViews[i].IsOpen));
            fields.Add(new($"pane{i}_find_count", _findViews[i].Count));
            fields.Add(new($"pane{i}_tabs", _tabViews[i].Describe()));
            fields.Add(new($"pane{i}_tab_row", Math.Round(_tabViews[i].RowHeight, 1)));
            fields.Add(new($"pane{i}_crumb_row", Math.Round(_crumbViews[i].ActualHeight, 1)));
        }
        Diag.Info("cabinetos_ui::snapshot", "shell state", [.. fields]);
    }
}
