using System.ComponentModel;
using CabinetOS.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace CabinetOS.Views;

/// <summary>
/// One palette row: category, title, plugin badge, keycaps, and the pencil
/// that records new keys (or a lock for the Immutable System Tier).
/// </summary>
public sealed partial class PaletteRowView : UserControl
{
    private readonly Storyboard _pulse;
    private PaletteRow? _row;

    /// <summary>Creates a row; the repeater recycles it.</summary>
    public PaletteRowView()
    {
        InitializeComponent();
        _pulse = (Storyboard)Resources["Pulse"];
        DataContextChanged += (_, _) => Bind(DataContext as PaletteRow);
        PointerEntered += (_, _) =>
        {
            if (_row is not null)
            {
                Hovered?.Invoke(_row);
            }
        };
        Tapped += OnTapped;
        PencilButton.Click += (_, _) =>
        {
            if (_row is not null)
            {
                RebindRequested?.Invoke(_row);
            }
        };
    }

    /// <summary>The pointer moved onto the row: it takes the highlight.</summary>
    public event Action<PaletteRow>? Hovered;

    /// <summary>The row was clicked: run its command.</summary>
    public event Action<PaletteRow>? RunRequested;

    /// <summary>The pencil was clicked: record new keys.</summary>
    public event Action<PaletteRow>? RebindRequested;

    private void Bind(PaletteRow? row)
    {
        if (_row is not null)
        {
            _row.PropertyChanged -= OnRowChanged;
        }
        _row = row;
        if (row is null)
        {
            return;
        }
        row.PropertyChanged += OnRowChanged;
        CategoryText.Text = row.CategoryText;
        TitleText.Text = row.Title;
        Badge.Visibility = row.Badge is null ? Visibility.Collapsed : Visibility.Visible;
        BadgeText.Text = row.Badge ?? "";
        PencilButton.Visibility = row.IsImmutable ? Visibility.Collapsed : Visibility.Visible;
        LockIcon.Visibility = row.IsImmutable ? Visibility.Visible : Visibility.Collapsed;
        BuildKeycaps(row);
        UpdateHighlight();
        UpdateRecording();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PaletteRow.IsHighlighted):
                UpdateHighlight();
                break;
            case nameof(PaletteRow.IsRecording) or nameof(PaletteRow.RecordingText) or nameof(PaletteRow.InlineError):
                UpdateRecording();
                break;
        }
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != this; element = VisualTreeHelper.GetParent(element))
        {
            if (element == PencilButton)
            {
                return;
            }
        }
        if (_row is not null)
        {
            RunRequested?.Invoke(_row);
        }
    }

    private void UpdateHighlight() =>
        VisualStateManager.GoToState(this, _row?.IsHighlighted == true ? "Highlighted" : "Plain", false);

    private void UpdateRecording()
    {
        var recording = _row?.IsRecording == true;
        var error = _row?.InlineError;
        RecordingPanel.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        RecordingText.Text = _row?.RecordingText ?? "";
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = !recording && error is not null ? Visibility.Visible : Visibility.Collapsed;
        Keycaps.Visibility = !recording && error is null ? Visibility.Visible : Visibility.Collapsed;
        if (recording)
        {
            _pulse.Begin();
        }
        else
        {
            _pulse.Stop();
        }
    }

    private void BuildKeycaps(PaletteRow row)
    {
        Keycaps.Children.Clear();
        foreach (var part in row.Keycaps)
        {
            if (part.IsSeparator)
            {
                Keycaps.Children.Add(new TextBlock
                {
                    Text = part.Text,
                    FontSize = 11,
                    Padding = new Thickness(2, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ThemeResources.Brush("CbTextTertiaryBrush"),
                });
            }
            else
            {
                Keycaps.Children.Add(new Border
                {
                    Padding = new Thickness(6, 1, 6, 1),
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(1),
                    Background = ThemeResources.Brush("CbKeycapFillBrush"),
                    BorderBrush = ThemeResources.Brush("CbKeycapStrokeBrush"),
                    Child = new TextBlock
                    {
                        Text = part.Text,
                        Style = (Style)ThemeResources.Get("CbKeycapTextStyle")!,
                    },
                });
            }
        }
    }
}
