using System.Globalization;
using System.Text.Json;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Themes;
using CabinetOS.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace CabinetOS;

// A theme's sizes and chrome elements, applied live like its colours (docs/ui.md, "Metrics and chrome").
public sealed partial class MainWindow
{
    /// <summary>
    /// A theme was applied: when its sizes or chrome differ from the window's,
    /// the window lays itself out again with them, without a restart. A
    /// theme without metrics gives the default look's, so switching back to
    /// <c>default</c> restores every size.
    /// </summary>
    private void ApplyMetrics(ThemeLook look)
    {
        if (!WindowMetrics.Take(look.Metrics, look.Chrome))
        {
            return;
        }
        LayOutWithMetrics();
        var m = look.Metrics;
        Diag.Info(Target, "metrics applied", new LogField("theme", look.Id), new LogField("preset", m.IsPreset),
            new LogField("row_height", m.RowHeight), new LogField("font_size", m.FontSize), new LogField("top_row_height", m.TopRowHeight),
            new LogField("breadcrumb_row_height", m.BreadcrumbRowHeight), new LogField("tab_row", m.TabRow),
            new LogField("fkey_bar", look.Chrome.FkeyBar), new LogField("row_stripes", look.Chrome.RowStripes), new LogField("hairlines", look.Chrome.Hairlines),
            new LogField("ignored", m.Ignored.Count == 0 ? null : string.Join(",", m.Ignored)));
    }

    // Every size of the window's own elements from the metrics in effect, then every view's.
    private void LayOutWithMetrics()
    {
        var m = WindowMetrics.Current;
        var chrome = WindowMetrics.Chrome;
        var control = WindowMetrics.Corners(m.RadiusControl);
        // WinUI's own controls made from now on (dialogs, menus' text boxes) take the theme's control radius too.
        Application.Current.Resources["ControlCornerRadius"] = control;

        // The top row (Phase 16): its height (never lower than Windows' caption buttons), its buttons, the pill, the
        // command center and the app icon.
        TopRow.Height = new GridLength(Math.Max(m.TopRowHeight, CaptionButtonsHeight));
        AppTile.CornerRadius = WindowMetrics.Inner(4);
        foreach (var button in new[] { MenuButton, DualButton, TerminalButton, MarketplaceButton, PaletteButton, SettingsButton })
        {
            button.Width = button.Height = m.TopRowButtonSize;
            button.CornerRadius = control;
        }
        WorkspacePill.Height = m.WorkspacePillHeight;
        WorkspacePill.CornerRadius = WorkspacePillFrame.CornerRadius = WindowMetrics.Corners(m.WorkspacePillRadius);
        CommandCenterFrame.Height = m.CommandCenterHeight;
        CommandCenterFrame.CornerRadius = WindowMetrics.Corners(m.CommandCenterRadius);
        CommandCenter.CornerRadius = WindowMetrics.Corners(Math.Max(0, m.CommandCenterRadius - 1));
        // Inside the frame's 1 px border: the subtle button style's own 32 px would push the text down and be cut.
        CommandCenter.Height = Math.Max(0, m.CommandCenterHeight - 2);
        foreach (var crumbs in _crumbViews)
        {
            crumbs.ApplyMetrics();
        }
        foreach (var find in _findViews)
        {
            find.ApplyMetrics();
        }

        // Body: the space at its edges, between the sidebar and the panes, and between the panes; under the top
        // row the space between surfaces, as the top row is one.
        Body.Padding = new Thickness(m.BodyPadding, m.Gap, m.BodyPadding, m.BodyPadding);
        SidebarColumn.Margin = new Thickness(0, 0, m.Gap, 0);
        Rail.Margin = new Thickness(0, 0, m.Gap, 0);
        // A gap too narrow to grab (a theme's gap 0) keeps a 6 px handle for the divider, laid over the edges it joins.
        SidebarSplitter.Width = Math.Max(m.Gap, 6);
        SidebarSplitter.Margin = new Thickness(0, 0, -Math.Max(0, (6 - m.Gap) / 2), 0);
        ApplyPaneGaps();

        // Status bar, and the transfer flyout that sits above it (and above the function keys).
        StatusRow.Height = new GridLength(m.StatusBarHeight);
        StatusGrid.Padding = WindowMetrics.Pad(m.StatusBarPaddingX);
        StatusGrid.ColumnSpacing = StatusKeys.Spacing = m.StatusBarGap;
        TransferView.Margin = new Thickness(0, 0, 16, m.StatusBarHeight + (chrome.FkeyBar ? m.FkeyBarHeight : 0) + 10);

        foreach (var pane in _paneViews)
        {
            pane.ApplyMetrics();
        }
        foreach (var editor in new[] { LeftEditor, RightEditor })
        {
            editor.ApplyMetrics();
        }
        foreach (var tabs in _tabViews)
        {
            tabs.ApplyMetrics();
        }
        foreach (var preview in _previewViews)
        {
            preview.ApplyMetrics();
        }
        SidebarView.ApplyMetrics();
        SearchPanelView.ApplyMetrics();
        Rail.ApplyMetrics();
        Dock.ApplyMetrics(_dockPlacement == DockPlacement.Bottom);
        MarketView.ApplyMetrics();
        FkeyBar.ApplyMetrics();
        UpdateWidths(RootGrid.ActualWidth);
        ApplyDockSize();
        UpdateDockHeader();
        UpdateCrumbs();
        UpdateLayoutText();
        LayOutTopRow();
    }

    // The space between the panes: half the gap on each side. Under hairlines with no gap
    // their two borders overlap into one line, as in the handout (margin-left: -1px).
    private void ApplyPaneGaps()
    {
        var gap = WindowMetrics.Current.Gap;
        var overlap = gap == 0 && WindowMetrics.Chrome.Hairlines ? 1 : 0;
        LeftSide.Margin = _dual ? new Thickness(0, 0, gap / 2, 0) : new Thickness(0);
        RightSide.Margin = new Thickness((gap / 2) - overlap, 0, 0, 0);
    }

    // The status bar's layout: a density preset's name before it ("Commander Compact · Terminal: bottom").
    private void UpdateLayoutText()
    {
        var layout = _settings.Layout switch
        {
            "right" => "Terminal: right",
            "rail" => "Activity rail",
            _ => "Terminal: bottom",
        };
        LayoutText.Text = WindowMetrics.Current.IsPreset && _themes?.Current is { } look ? $"{look.Name} · {layout}" : layout;
    }

    // ----- The snapshot aid's steps for sizes and themes (docs/ui.md, "Metrics and chrome") -----

    // size:<width>x<height>: the window's content that many device-independent pixels wide and high.
    private async Task SizeForSnapshotAsync(string argument)
    {
        var parts = argument.Split('x');
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
        {
            return;
        }
        // The content is not exactly the client area Windows sizes (the title bar's frame): correct by what came out.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (dx, dy) = (width - RootGrid.ActualWidth, height - RootGrid.ActualHeight);
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
            {
                break;
            }
            GrowClient(dx, dy);
            await Task.Delay(800);
        }
        Diag.Info("cabinetos_ui::snapshot", "window sized", new LogField("width", RootGrid.ActualWidth), new LogField("height", RootGrid.ActualHeight),
            new LogField("scale", RootGrid.XamlRoot?.RasterizationScale));
    }

    // fit:<pixels>: the window as high as makes the active pane's list that many pixels high.
    private async Task FitListForSnapshotAsync(double listHeight)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var delta = listHeight - _paneViews[_active].MeasureRows().Viewport;
            if (Math.Abs(delta) < 0.5)
            {
                break;
            }
            GrowClient(0, delta);
            await Task.Delay(800);
        }
        Diag.Info("cabinetos_ui::snapshot", "list fitted", new LogField("list_height", _paneViews[_active].MeasureRows().Viewport),
            new LogField("window", $"{RootGrid.ActualWidth:0.#}x{RootGrid.ActualHeight:0.#}"));
    }

    // The window grown (or shrunk) by this many device-independent pixels: through its outer size,
    // which changes the content one for one (the client size Windows reports leaves out the caption area).
    private void GrowClient(double dx, double dy)
    {
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        var size = AppWindow.Size;
        AppWindow.Resize(new SizeInt32(size.Width + (int)Math.Round(dx * scale), size.Height + (int)Math.Round(dy * scale)));
    }

    // theme:<id>: the theme in effect, as the picker sets it (set_value ui.theme); waits until it is applied.
    private async Task SwitchThemeForSnapshotAsync(string id)
    {
        using var value = JsonDocument.Parse($"\"{id}\"");
        var reply = await _session.RequestAsync(new SetValueRequest("ui.theme", value.RootElement.Clone()));
        for (var waited = 0; waited < 5000 && _themes.Current?.Id != id; waited += 100)
        {
            await Task.Delay(100);
        }
        await Task.Delay(500);
        Diag.Info("cabinetos_ui::snapshot", "theme switched", new LogField("theme", _themes.Current?.Id), new LogField("reply", reply.GetType().Name));
    }

    // layout:<label>: what the acceptance of a density preset asks about the window as it is laid out
    // now: each pane's list height, its whole rows and their height, the texts cut short; the function
    // keys' widths; every corner radius over 3 px outside the overlays; and the keymap's fingerprint.
    private void LogLayoutForSnapshot(string label)
    {
        var fields = new List<LogField>
        {
            new("label", label),
            new("theme", _themes.Current?.Id),
            new("window", $"{RootGrid.ActualWidth:0}x{RootGrid.ActualHeight:0}"),
        };
        for (var i = 0; i < _paneViews.Length; i++)
        {
            var pane = _paneViews[i];
            if (pane.Visibility != Visibility.Visible || pane.ActualWidth <= 0)
            {
                continue;
            }
            var (viewport, whole, height, trimmed) = pane.MeasureRows();
            fields.Add(new($"pane{i}_width", Math.Round(pane.ActualWidth, 1)));
            fields.Add(new($"pane{i}_list_height", Math.Round(viewport, 1)));
            fields.Add(new($"pane{i}_whole_rows", whole));
            fields.Add(new($"pane{i}_row_height", Math.Round(height, 2)));
            fields.Add(new($"pane{i}_trimmed", trimmed.Count == 0 ? "none" : string.Join(" | ", trimmed)));
        }
        if (FkeyBar.Visibility == Visibility.Visible)
        {
            fields.Add(new("fkeys", string.Join(" | ", FkeyBar.Measure().Select(k => $"{k.Name} {k.Width:0} px{(k.Trimmed ? " cut short" : "")}"))));
        }
        // The sizes the metrics set, element by element: two looks compare by this line.
        var named = new (string Name, FrameworkElement Element)[]
        {
            ("top", TopBar), ("menu", MenuButton), ("tile", AppTile), ("pill", WorkspacePillFrame), ("center", CommandCenterFrame),
            ("dual", DualButton), ("settings", SettingsButton), ("body", Body), ("sidebar", SidebarView), ("lefttabs", LeftTabs),
            ("leftcrumbs", LeftCrumbs), ("left", LeftPane), ("righttabs", RightTabs), ("rightcrumbs", RightCrumbs), ("right", RightPane),
            ("fkeys", FkeyBar), ("status", StatusGrid), ("dock", Dock),
        };
        var sizes = named.Where(n => n.Element.Visibility == Visibility.Visible)
            .Select(n => string.Create(CultureInfo.InvariantCulture, $"{n.Name}={n.Element.ActualWidth:0.#}x{n.Element.ActualHeight:0.#}"));
        fields.Add(new("sizes", string.Join(" ", sizes)));
        fields.Add(new("pane_sizes", _paneViews[0].SizeSignature()));
        var (radius, over) = RadiiOverThree();
        fields.Add(new("max_radius", radius));
        fields.Add(new("radius_over_3", over.Count == 0 ? "none" : string.Join(" | ", over.Take(16))));
        var bindings = _keys.Keymap.Bindings.Select(b => $"{b.Keys}={b.Command}@{b.When}").Order(StringComparer.Ordinal).ToList();
        var print = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", bindings))))[..12];
        fields.Add(new("keymap", $"{bindings.Count} bindings, {print}"));
        Diag.Info("cabinetos_ui::snapshot", "layout measured", [.. fields]);
    }

    // The window's content outside its overlays (the palette's frame, menus, the transfer card and
    // dialogs sit in RootGrid, not in ShellContent): the largest radius drawn, and each over 3 px.
    private (double Max, List<string> Over) RadiiOverThree()
    {
        var max = 0.0;
        var over = new List<string>();
        var pending = new Stack<DependencyObject>();
        pending.Push(ShellContent);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element is UIElement { Visibility: Visibility.Collapsed })
            {
                continue;
            }
            if (element is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } shown && DrawnRadius(shown) is > 0 and var radius)
            {
                max = Math.Max(max, radius);
                if (radius > 3)
                {
                    var owner = VisualTreeHelper.GetParent(shown) is FrameworkElement { Name.Length: > 0 } parent ? $" in {parent.Name}" : "";
                    over.Add($"{shown.GetType().Name}{(shown.Name.Length > 0 ? "#" + shown.Name : "")}{owner} {radius:0.#}");
                }
            }
            for (var i = VisualTreeHelper.GetChildrenCount(element) - 1; i >= 0; i--)
            {
                pending.Push(VisualTreeHelper.GetChild(element, i));
            }
        }
        return (max, over);
    }

    // The radius an element draws: its largest corner, and never more than half its height or width (a dot, a pill).
    private static double DrawnRadius(FrameworkElement element)
    {
        var half = Math.Min(element.ActualWidth, element.ActualHeight) / 2;
        if (element is Microsoft.UI.Xaml.Shapes.Rectangle rectangle)
        {
            return Math.Min(half, Math.Min(rectangle.RadiusX, rectangle.RadiusY));
        }
        CornerRadius? corners = element switch
        {
            Border border => border.CornerRadius,
            Grid grid => grid.CornerRadius,
            StackPanel stack => stack.CornerRadius,
            RelativePanel relative => relative.CornerRadius,
            ContentPresenter presenter => presenter.CornerRadius,
            Control control => control.CornerRadius,
            _ => null,
        };
        return corners is { } c ? Math.Min(half, Math.Max(Math.Max(c.TopLeft, c.TopRight), Math.Max(c.BottomRight, c.BottomLeft))) : 0;
    }
}
