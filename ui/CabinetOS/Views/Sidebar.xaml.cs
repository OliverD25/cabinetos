using System.ComponentModel;
using CabinetOS.Core.Themes;
using CabinetOS.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;

namespace CabinetOS.Views;

/// <summary>
/// The sidebar (design view A): pinned folders and drives. In the rail layout it is
/// the Explorer view, and the folder tree follows the drives (<see cref="ShowTree"/>).
/// </summary>
public sealed partial class Sidebar : UserControl
{
    private SidebarModel? _model;

    /// <summary>Creates the sidebar; <see cref="Model"/> fills it.</summary>
    public Sidebar()
    {
        InitializeComponent();
        TreeSection.Navigate += path => Navigate?.Invoke(path);
        PinnedList.ElementPrepared += (_, e) => SizePinnedRow(e.Element);
        DriveList.ElementPrepared += (_, e) => SizeDriveRow(e.Element);
        ApplyMetrics();
    }

    /// <summary>
    /// Lays the sidebar out with the window's sizes and chrome now (docs/ui.md,
    /// "Metrics and chrome"): the section labels, the rows and their inset,
    /// corners and selection bar, and under hairlines a line on its right.
    /// Its width is the window's (<see cref="ThemeMetrics.SidebarWidth"/>).
    /// </summary>
    public void ApplyMetrics()
    {
        var m = WindowMetrics.Current;
        Frame.BorderThickness = new Thickness(0, 0, WindowMetrics.Chrome.Hairlines ? 1 : 0, 0);
        // The spaces the handout does not name follow the row inset: 4 px above and below the
        // sections (at least 2), and half of it, at most 2 px, between the rows.
        var spacing = Math.Min(2, m.SidebarRowInset / 2);
        Scroller.Padding = new Thickness(0, Math.Max(2, m.SidebarRowInset), 0, Math.Max(2, m.SidebarRowInset));
        Sections.Spacing = PinnedLayout.Spacing = DriveLayout.Spacing = spacing;
        // The first label sits 8 px higher than the others, as the design's first one does (6 of 14).
        PinnedLabel.Margin = new Thickness(m.SidebarHeaderPaddingX, Math.Max(0, m.SidebarHeaderPaddingTop - 8), m.SidebarHeaderPaddingX, m.SidebarHeaderPaddingBottom);
        DrivesLabel.Margin = new Thickness(m.SidebarHeaderPaddingX, m.SidebarHeaderPaddingTop, m.SidebarHeaderPaddingX, m.SidebarHeaderPaddingBottom);
        PinnedLabel.FontSize = DrivesLabel.FontSize = m.SidebarHeaderFontSize;
        TreeSection.ApplyMetrics();
        if (_model is not null)
        {
            _model.ShortFreeLabels = WindowMetrics.Chrome.Hairlines;
        }
        foreach (var (list, size) in new (ItemsRepeater, Action<UIElement>)[] { (PinnedList, SizePinnedRow), (DriveList, SizeDriveRow) })
        {
            for (var i = 0; i < (list.ItemsSourceView?.Count ?? 0); i++)
            {
                if (list.TryGetElement(i) is { } row)
                {
                    size(row);
                }
            }
            list.InvalidateMeasure();
        }
    }

    // A pinned folder's row: the template's button, its fill, its selection bar and its icon and name.
    private static void SizePinnedRow(UIElement element)
    {
        if (element is not Button { Content: Grid { Children: [Border fill, Rectangle bar, StackPanel content] } grid } button)
        {
            return;
        }
        var m = WindowMetrics.Current;
        var height = m.PinnedRowHeight();
        SizeRowButton(button, m);
        button.MinHeight = height;
        grid.Height = height;
        fill.CornerRadius = WindowMetrics.Corners(m.SidebarRowRadius);
        bar.Width = m.SelectionBarWidth;
        bar.RadiusX = bar.RadiusY = m.SelectionBarWidth / 2;
        bar.Height = Math.Clamp(height - 4, 0, 16);
        // The handout gives sidebar rows the drive rows' side padding and the file rows' icon gap.
        content.Padding = WindowMetrics.Pad(m.DriveRowPaddingX);
        content.Spacing = m.RowIconGap;
    }

    // A drive's row: its name line and its usage bar under the name.
    private static void SizeDriveRow(UIElement element)
    {
        if (element is not Button { Content: Grid { Children: [Grid line, Grid usage] } grid } button)
        {
            return;
        }
        var m = WindowMetrics.Current;
        SizeRowButton(button, m);
        grid.Padding = WindowMetrics.Pad(m.DriveRowPaddingX, m.DriveRowPaddingY);
        grid.RowSpacing = Math.Min(5, m.DriveRowPaddingY);
        line.ColumnSpacing = m.RowIconGap;
        usage.Margin = new Thickness(16 + m.RowIconGap, 0, 0, 0);
    }

    private static void SizeRowButton(Button button, ThemeMetrics m)
    {
        button.Margin = WindowMetrics.Pad(m.SidebarRowInset);
        button.CornerRadius = WindowMetrics.Corners(m.SidebarRowRadius);
        button.FontSize = m.FontSize;
    }

    /// <summary>Raised with a folder the user picked: the window runs <c>go.toPath</c>.</summary>
    public event Action<string>? Navigate;

    /// <summary>Gives the keyboard to the first pinned folder (view.showExplorer in a layout without the rail).</summary>
    public void FocusFirstRow()
    {
        if (PinnedList.TryGetElement(0) is Control first)
        {
            first.Focus(FocusState.Keyboard);
        }
    }

    /// <summary>The folder tree under the drives; it shows only in the rail layout.</summary>
    public FolderTreeView Tree => TreeSection;

    /// <summary>Whether the folder tree shows under the drives (the rail layout's Explorer); the other layouts have none.</summary>
    public bool ShowTree
    {
        get => TreeSection.Visibility == Visibility.Visible;
        set => TreeSection.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Raised with a user-pinned folder to unpin: the window runs <c>sidebar.unpin</c>.</summary>
    public event Action<string>? UnpinRequested;

    /// <summary>The sidebar's content.</summary>
    public SidebarModel? Model
    {
        get => _model;
        set
        {
            if (_model is not null)
            {
                _model.PropertyChanged -= OnModelChanged;
            }
            _model = value;
            PinnedList.ItemsSource = value?.Pinned;
            DriveList.ItemsSource = value?.Drives;
            if (_model is not null)
            {
                _model.PropertyChanged += OnModelChanged;
                _model.ShortFreeLabels = WindowMetrics.Chrome.Hairlines;
            }
            UpdateDrives();
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SidebarModel.ShowDrives))
        {
            UpdateDrives();
        }
    }

    private void UpdateDrives() =>
        DrivesSection.Visibility = _model?.ShowDrives == true ? Visibility.Visible : Visibility.Collapsed;

    private void OnPinnedClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PinnedItem item })
        {
            Navigate?.Invoke(item.Path);
        }
    }

    // Only the folders the user pinned can be unpinned; the known folders always stay.
    private void OnPinnedRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PinnedItem { IsUserPinned: true } item } row)
        {
            return;
        }
        e.Handled = true;
        var unpin = new MenuFlyoutItem { Text = "Unpin from sidebar", Icon = new FontIcon { Glyph = "" } };
        unpin.Click += (_, _) => UnpinRequested?.Invoke(item.Path);
        var menu = new MenuFlyout();
        menu.Items.Add(unpin);
        menu.ShowAt(row, new FlyoutShowOptions { Position = e.GetPosition(row) });
    }

    private void OnDriveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
        {
            Navigate?.Invoke(path);
        }
    }
}
