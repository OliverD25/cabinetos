using System.Text.Json;
using CabinetOS.Core.Shell;
using CabinetOS.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace CabinetOS.Views;

/// <summary>
/// A pane's toolbar row and path row (v2 of the shell redesign; docs/ui.md,
/// "The pane's rows"). The toolbar: Back, Forward and Up, the drive chip,
/// the drive's free space and Find. The path row: the path's segments
/// (<see cref="Breadcrumbs"/>), a text box in their place while a path is
/// typed (Ctrl+L), and the filter label (<see cref="PaneRows.FilterLabel"/>).
/// Every click is a command with the pane's index, so the window decides and
/// the rows only show. Their buttons never take the keyboard from the pane.
/// </summary>
public sealed partial class PaneCrumbs : UserControl
{
    private readonly List<Button> _segments = [];
    private IReadOnlyList<CrumbSegment> _shown = [];
    private string _path = "";
    private bool _dual = true;
    private bool _active;
    private bool _findOpen;

    /// <summary>Creates the rows, empty.</summary>
    public PaneCrumbs()
    {
        InitializeComponent();
        BackButton.Click += (_, _) => Run("go.back");
        ForwardButton.Click += (_, _) => Run("go.forward");
        UpButton.Click += (_, _) => Run("go.up");
        // The drive commands name their pane: Alt+F1 is the left one's, Alt+F2 the right one's.
        DriveChip.Click += (_, _) => _ = RunCommand?.Invoke(PaneIndex == 0 ? "go.chooseDriveLeft" : "go.chooseDriveRight", null, "button");
        FindButton.Click += (_, _) => Run("search.toggle");
        FilterLabel.Click += (_, _) => Run("search.focus");
        OpenWithButton.Click += (_, _) => _ = RunCommand?.Invoke("palette.show", CommandArgs.With("query", "Editor"), "button");
        CrumbScroller.Tapped += OnPathAreaTapped;
        CrumbScroller.SizeChanged += (_, _) => ScrollToEnd();
        AddressEdit.KeyDown += OnAddressKeyDown;
        AddressEdit.LostFocus += (_, _) => EndEdit();
        ApplyMetrics();
    }

    /// <summary>Which pane these rows belong to: 0 left, 1 right.</summary>
    public int PaneIndex { get; set; }

    /// <summary>Runs a command through the window's router: (command, args, trigger).</summary>
    public Func<string, JsonElement?, string, Task>? RunCommand { get; set; }

    /// <summary>
    /// Whether the rows belong to the active pane (or the only one): the
    /// toolbar's fill is then the active pane's tab in front's, white 9 %,
    /// else white 5 %, as the tab strip draws that tab.
    /// </summary>
    public bool IsActivePane
    {
        get => _active;
        set
        {
            _active = value;
            Toolbar.Background = ThemeResources.Brush(ToolbarFillKey);
        }
    }

    /// <summary>The brush the toolbar is filled with now, by its name (the snapshot aid's log compares it with the tab's).</summary>
    public string ToolbarFillKey => _active ? "CbFrontTabFillBrush" : "CbFrontTabInactiveFillBrush";

    /// <summary>Whether a path is being typed (Ctrl+L).</summary>
    public bool IsEditing => AddressEdit.Visibility == Visibility.Visible;

    /// <summary>The segments as the row shows them, "C: › … › Projects › fileforge" (logs and the snapshot aid).</summary>
    public string Text => Breadcrumbs.Text(_shown);

    /// <summary>Whether the segments fit the path row without scrolling (the snapshot aid's log).</summary>
    public bool CrumbsFit => CrumbScroller.ScrollableWidth < 0.5;

    /// <summary>The filter label as shown.</summary>
    public string FilterLabelText => FilterText.Text;

    /// <summary>The drive chip's text and the free space as shown.</summary>
    public (string Drive, string Free) DriveShown => (DriveText.Text, FreeText.Text);

    /// <summary>Whether the Find button shows the find open (in the accent).</summary>
    public bool FindShownOpen => _findOpen;

    /// <summary>The text box of Ctrl+L.</summary>
    public TextBox AddressBox => AddressEdit;

    /// <summary>The drive chip, under which the drive list opens.</summary>
    public FrameworkElement DriveAnchor => DriveChip;

    /// <summary>The toolbar row's and the path row's heights now.</summary>
    public (double Toolbar, double Path) RowHeights => (Toolbar.ActualHeight, PathRow.ActualHeight);

    /// <summary>Shows <paramref name="path"/> for a window with two panes (<paramref name="dual"/>) or one.</summary>
    public void Show(string path, bool dual)
    {
        if (path == _path && dual == _dual && _segments.Count > 0)
        {
            return;
        }
        _path = path;
        _dual = dual;
        Rebuild();
    }

    /// <summary>Which of Back, Forward and Up work: the others are drawn at 30 % and do nothing.</summary>
    public void SetNav(NavState state)
    {
        Set(BackButton, state.Back);
        Set(ForwardButton, state.Forward);
        Set(UpButton, state.Up);

        static void Set(Button button, bool works)
        {
            button.IsEnabled = works;
            button.Opacity = NavState.Opacity(works);
        }
    }

    /// <summary>Whether Back, Forward and Up work, for the snapshot aid's log: "back forward up" with a dash for one that does not.</summary>
    public string NavText => $"{(BackButton.IsEnabled ? "back" : "-")} {(ForwardButton.IsEnabled ? "forward" : "-")} {(UpButton.IsEnabled ? "up" : "-")}";

    /// <summary>The drive chip's letter (<see cref="PaneRows.DriveLabel"/>) and the drive's free space (<see cref="PaneRows.FreeSpace"/>).</summary>
    public void SetDrive(string drive, string free)
    {
        DriveText.Text = drive;
        DriveChip.Visibility = drive.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(DriveChip, drive.Length > 0 ? $"Drive {drive}" : "Drive");
        ToolTipService.SetToolTip(DriveChip, $"Switch drive ({(PaneIndex == 0 ? "Alt+F1" : "Alt+F2")})");
        FreeText.Text = free;
    }

    /// <summary>
    /// What the pane's find holds: the Find button is in the accent while it
    /// is open, and the filter label reads <c>*query*</c> while it has a text.
    /// </summary>
    public void SetFind(bool open, string? query)
    {
        _findOpen = open;
        FindIcon.Foreground = ThemeResources.Brush(open ? "CbAccentBrush" : "CbTextSecondaryBrush");
        FilterText.Text = PaneRows.FilterLabel(open ? query : null);
    }

    /// <summary>
    /// The toolbar's items that are shown whole, in order, for the snapshot
    /// aid's log: "back forward up drive free find"; an item cut short or
    /// pushed past the row's end is left out.
    /// </summary>
    public string ToolbarItems()
    {
        var items = new (string Name, FrameworkElement Element)[]
        {
            ("back", BackButton), ("forward", ForwardButton), ("up", UpButton), ("drive", DriveChip), ("free", FreeText), ("find", FindButton), ("openwith", OpenWithButton),
        };
        var whole = new List<string>();
        foreach (var (name, element) in items)
        {
            if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0)
            {
                continue;
            }
            // The row's Auto columns get their whole width; what does not fit is pushed past the row's right end and cut there.
            var right = element.TransformToVisual(Toolbar).TransformPoint(new Point(element.ActualWidth, 0)).X;
            if (right <= Toolbar.ActualWidth - Toolbar.Padding.Right + 0.5 && element is not TextBlock { IsTextTrimmed: true })
            {
                whole.Add(name);
            }
        }
        return string.Join(' ', whole);
    }

    /// <summary>Turns the segments into a text box holding <paramref name="path"/>, all selected (Ctrl+L).</summary>
    public void BeginEdit(string path)
    {
        AddressEdit.Text = path;
        AddressEdit.Visibility = Visibility.Visible;
        CrumbScroller.Visibility = Visibility.Collapsed;
        AddressEdit.Focus(FocusState.Programmatic);
        AddressEdit.SelectAll();
    }

    /// <summary>Shows the segments again (Esc, Enter, or the box lost the keyboard).</summary>
    public void EndEdit()
    {
        if (!IsEditing)
        {
            return;
        }
        AddressEdit.Visibility = Visibility.Collapsed;
        CrumbScroller.Visibility = Visibility.Visible;
    }

    /// <summary>Lays the rows out with the window's sizes now: the theme's toolbarRowHeight, pathRowHeight and navButtonSize.</summary>
    public void ApplyMetrics()
    {
        var m = WindowMetrics.Current;
        Toolbar.Height = m.ToolbarRowHeight;
        PathRow.Height = m.PathRowHeight;
        // The handout's 22 x 20 buttons with radius 2 in both looks: navButtonSize high, 2 px wider.
        var corners = WindowMetrics.Corners(Math.Min(2, m.RadiusControl));
        foreach (var button in new[] { BackButton, ForwardButton, UpButton, FindButton, OpenWithButton })
        {
            button.Height = m.NavButtonSize;
            button.Width = m.NavButtonSize + 2;
            button.CornerRadius = corners;
        }
        DriveChip.Height = m.NavButtonSize;
        DriveChip.CornerRadius = corners;
        FilterLabel.Height = Math.Max(14, m.PathRowHeight - 6);
        FilterLabel.CornerRadius = corners;
        AddressEdit.Height = Math.Max(16, m.PathRowHeight - 4);
        AddressEdit.CornerRadius = WindowMetrics.Corners(m.RadiusControl);
        AddressEdit.Padding = new Thickness(6, WindowMetrics.TextTop(AddressEdit.Height, 11), 6, 0);
        foreach (var segment in _segments)
        {
            SizeSegment(segment);
        }
        Toolbar.Background = ThemeResources.Brush(ToolbarFillKey);
        FindIcon.Foreground = ThemeResources.Brush(_findOpen ? "CbAccentBrush" : "CbTextSecondaryBrush");
    }

    private void Rebuild()
    {
        Crumbs.Children.Clear();
        _segments.Clear();
        _shown = Breadcrumbs.Segments(_path, _dual);
        for (var i = 0; i < _shown.Count; i++)
        {
            var segment = _shown[i];
            var last = i == _shown.Count - 1;
            var button = new Button
            {
                Content = segment.Label,
                Style = (Style)ThemeResources.Get("CbSubtleButtonStyle")!,
                FontFamily = (FontFamily)ThemeResources.Get("CbMonoFont")!,
                FontSize = 11,
                MinWidth = 0,
                Padding = new Thickness(4, 0, 4, 0),
                AllowFocusOnInteraction = false,
                IsTabStop = false,
                Foreground = ThemeResources.Brush(last ? "CbTextPrimaryBrush" : "CbCrumbTextBrush"),
                Tag = segment.Path,
            };
            SizeSegment(button);
            if (segment.IsEllipsis)
            {
                AutomationProperties.SetName(button, "Folders in between");
                AutomationProperties.SetHelpText(button, segment.Path);
            }
            else
            {
                AutomationProperties.SetName(button, segment.Path);
            }
            // The "…" says where it goes; a whole segment says its full path.
            ToolTipService.SetToolTip(button, segment.Path);
            var path = segment.Path;
            button.Click += (_, _) => _ = RunCommand?.Invoke("go.toPath", CommandArgs.Object(("path", path), ("pane", PaneIndex)), "crumb");
            _segments.Add(button);
            Crumbs.Children.Add(button);
            if (!last)
            {
                Crumbs.Children.Add(new TextBlock
                {
                    Text = "›",
                    FontFamily = (FontFamily)ThemeResources.Get("CbMonoFont")!,
                    FontSize = 11,
                    Margin = new Thickness(1, 0, 1, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ThemeResources.Brush("CbCrumbSeparatorBrush"),
                    IsHitTestVisible = false,
                });
            }
        }
        ScrollToEnd();
    }

    private static void SizeSegment(Button button)
    {
        var m = WindowMetrics.Current;
        button.Height = Math.Max(14, m.PathRowHeight - 6);
        button.CornerRadius = WindowMetrics.Corners(Math.Min(2, m.RadiusControl));
    }

    // A path still too wide after the collapse scrolls; its end, the folder shown, stays in view.
    private void ScrollToEnd()
    {
        CrumbScroller.UpdateLayout();
        CrumbScroller.ChangeView(CrumbScroller.ScrollableWidth, null, null, disableAnimation: true);
    }

    // A click beside the segments types a path, as the address bar's did.
    private void OnPathAreaTapped(object sender, TappedRoutedEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != CrumbScroller; element = VisualTreeHelper.GetParent(element))
        {
            if (element is Button)
            {
                return;
            }
        }
        _ = RunCommand?.Invoke("go.toPath", CommandArgs.Object(("pane", PaneIndex)), "mouse");
    }

    private void OnAddressKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            var path = AddressEdit.Text.Trim().Trim('"');
            _ = RunCommand?.Invoke("go.toPath", CommandArgs.Object(("path", path), ("pane", PaneIndex)), "address");
        }
    }

    private void Run(string command) => _ = RunCommand?.Invoke(command, CommandArgs.Object(("pane", PaneIndex)), "button");
}
