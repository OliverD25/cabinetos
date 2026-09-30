using System.Text.Json;
using CabinetOS.Core.Shell;
using CabinetOS.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace CabinetOS.Views;

/// <summary>
/// A pane's breadcrumb row (Phase 16; docs/ui.md, "The breadcrumb row"):
/// Back, Forward and Up, then the path's segments (<see cref="Breadcrumbs"/>),
/// and a text box in their place while a path is typed (Ctrl+L). Every click
/// is a command with the pane's index, so the window decides and the row only
/// shows. Its buttons never take the keyboard from the pane with a click; Tab
/// reaches them.
/// </summary>
public sealed partial class PaneCrumbs : UserControl
{
    private readonly List<Button> _segments = [];
    private IReadOnlyList<CrumbSegment> _shown = [];
    private string _path = "";
    private bool _dual = true;
    private bool _active;

    /// <summary>Creates the row, empty.</summary>
    public PaneCrumbs()
    {
        InitializeComponent();
        BackButton.Click += (_, _) => Run("go.back");
        ForwardButton.Click += (_, _) => Run("go.forward");
        UpButton.Click += (_, _) => Run("go.up");
        CrumbScroller.Tapped += OnPathAreaTapped;
        CrumbScroller.SizeChanged += (_, _) => ScrollToEnd();
        AddressEdit.KeyDown += OnAddressKeyDown;
        AddressEdit.LostFocus += (_, _) => EndEdit();
        ApplyMetrics();
    }

    /// <summary>Which pane this row belongs to: 0 left, 1 right.</summary>
    public int PaneIndex { get; set; }

    /// <summary>Runs a command through the window's router: (command, args, trigger).</summary>
    public Func<string, JsonElement?, string, Task>? RunCommand { get; set; }

    /// <summary>Whether the row belongs to the active pane: its fill is then the accent's tint.</summary>
    public bool IsActivePane
    {
        get => _active;
        set
        {
            _active = value;
            Root.Background = value ? ThemeResources.Brush("CbCrumbRowActiveFillBrush") : null;
        }
    }

    /// <summary>Whether a path is being typed (Ctrl+L).</summary>
    public bool IsEditing => AddressEdit.Visibility == Visibility.Visible;

    /// <summary>The segments as the row shows them, "C: › … › Projects › fileforge" (logs and the snapshot aid).</summary>
    public string Text => Breadcrumbs.Text(_shown);

    /// <summary>The text box of Ctrl+L.</summary>
    public TextBox AddressBox => AddressEdit;

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

    /// <summary>Lays the row out with the window's sizes now: the theme's breadcrumbRowHeight and navButtonSize.</summary>
    public void ApplyMetrics()
    {
        var m = WindowMetrics.Current;
        Root.Height = m.BreadcrumbRowHeight;
        foreach (var button in new[] { BackButton, ForwardButton, UpButton })
        {
            button.Width = button.Height = m.NavButtonSize;
            button.CornerRadius = WindowMetrics.Corners(Math.Min(m.RadiusControl, m.NavButtonSize / 2));
        }
        AddressEdit.Height = Math.Max(16, m.BreadcrumbRowHeight - 4);
        AddressEdit.CornerRadius = WindowMetrics.Corners(m.RadiusControl);
        AddressEdit.Padding = new Thickness(6, WindowMetrics.TextTop(AddressEdit.Height, 11), 6, 0);
        foreach (var segment in _segments)
        {
            SizeSegment(segment);
        }
        if (_active)
        {
            Root.Background = ThemeResources.Brush("CbCrumbRowActiveFillBrush");
        }
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
        button.Height = Math.Max(14, m.BreadcrumbRowHeight - 6);
        button.CornerRadius = WindowMetrics.Corners(m.RadiusControl);
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
