using System.ComponentModel;
using CabinetOS.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace CabinetOS.Views;

/// <summary>The sidebar (design view A): pinned folders and drives.</summary>
public sealed partial class Sidebar : UserControl
{
    private SidebarModel? _model;

    /// <summary>Creates the sidebar; <see cref="Model"/> fills it.</summary>
    public Sidebar() => InitializeComponent();

    /// <summary>Raised with a folder the user picked: the window runs <c>go.toPath</c>.</summary>
    public event Action<string>? Navigate;

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
