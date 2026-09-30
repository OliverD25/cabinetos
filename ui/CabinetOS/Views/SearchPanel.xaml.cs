using CabinetOS.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace CabinetOS.Views;

/// <summary>
/// The Search view: a field, the "Whole volume" box and the hits of the
/// window's search (docs/ui.md, "Search"). The rail layout has it as a view;
/// the classic and right layouts show it in the sidebar's place while it is
/// asked for (Phase 16 removed the command bar's field). The pane shows the
/// same hits. Enter on a hit, or a click, goes to it in the active pane.
/// </summary>
public sealed partial class SearchPanel : UserControl
{
    private bool _settingBox;

    /// <summary>Creates the view.</summary>
    public SearchPanel()
    {
        InitializeComponent();
        QueryBox.TextChanged += (_, _) =>
        {
            if (!_settingBox)
            {
                QueryChanged?.Invoke(QueryBox.Text);
            }
        };
        QueryBox.KeyDown += OnQueryKeyDown;
        WholeVolumeBox.Click += (_, _) =>
        {
            if (!_settingBox)
            {
                WholeVolumeChanged?.Invoke(WholeVolumeBox.IsChecked == true);
            }
        };
        HitList.ElementPrepared += (_, e) => SizeHit(e.Element);
        // Esc on a hit (or the check box) leaves the search as it does in the field.
        KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape && !e.Handled)
            {
                e.Handled = true;
                Cancelled?.Invoke();
            }
        };
        ApplyMetrics();
    }

    /// <summary>How many hits the view lists now.</summary>
    public int HitCount => HitList.ItemsSourceView?.Count ?? 0;

    /// <summary>The user typed: the window puts the text into the search.</summary>
    public event Action<string>? QueryChanged;

    /// <summary>Enter in the field: the window searches at once.</summary>
    public event Action? SearchNow;

    /// <summary>Esc in the field or on a hit: the window leaves the search and gives the keyboard to the pane.</summary>
    public event Action? Cancelled;

    /// <summary>A hit was picked: the window goes to it in the active pane.</summary>
    public event Action<SearchRowItem>? HitChosen;

    /// <summary>The "Whole volume" box was switched.</summary>
    public event Action<bool>? WholeVolumeChanged;

    /// <summary>The field's text (set by the window when a search ends or the snapshot aid types; it does not raise <see cref="QueryChanged"/>).</summary>
    public string Query
    {
        get => QueryBox.Text;
        set
        {
            if (QueryBox.Text == value)
            {
                return;
            }
            _settingBox = true;
            QueryBox.Text = value;
            _settingBox = false;
        }
    }

    /// <summary>Sets the "Whole volume" box to the search's state; it does not raise <see cref="WholeVolumeChanged"/>.</summary>
    public void SetWholeVolume(bool wholeVolume)
    {
        if (WholeVolumeBox.IsChecked == wholeVolume)
        {
            return;
        }
        _settingBox = true;
        WholeVolumeBox.IsChecked = wholeVolume;
        _settingBox = false;
    }

    /// <summary>Shows the search's state: its header and note and the hits, or nothing when no search runs.</summary>
    public void Show(PaneSearch? search)
    {
        if (search is null)
        {
            SummaryText.Text = "";
            ToolTipService.SetToolTip(SummaryText, null);
            HitList.ItemsSource = null;
            return;
        }
        SummaryText.Text = search.Header;
        ToolTipService.SetToolTip(SummaryText, search.Note.Length > 0 ? $"{search.Header}\n{search.Scope}\n{search.Note}" : $"{search.Header}\n{search.Scope}");
        HitList.ItemsSource = search.Rows;
    }

    /// <summary>Gives the keyboard to the field and selects its text, so typing starts a new search.</summary>
    public void FocusQuery()
    {
        QueryBox.Focus(FocusState.Keyboard);
        QueryBox.SelectAll();
    }

    /// <summary>Lays the view out with the window's sizes now (docs/ui.md, "Metrics and chrome").</summary>
    public void ApplyMetrics()
    {
        var m = WindowMetrics.Current;
        Frame.BorderThickness = new Thickness(0, 0, WindowMetrics.Chrome.Hairlines ? 1 : 0, 0);
        SearchLabel.FontSize = m.SidebarHeaderFontSize;
        SearchLabel.Margin = new Thickness(4, Math.Max(0, m.SidebarHeaderPaddingTop - 8), 4, 0);
        for (var i = 0; i < (HitList.ItemsSourceView?.Count ?? 0); i++)
        {
            if (HitList.TryGetElement(i) is { } row)
            {
                SizeHit(row);
            }
        }
        HitList.InvalidateMeasure();
    }

    private static void SizeHit(UIElement element)
    {
        if (element is not Button button)
        {
            return;
        }
        var m = WindowMetrics.Current;
        button.CornerRadius = WindowMetrics.Corners(m.SidebarRowRadius);
        button.FontSize = m.FontSize;
    }

    private void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                SearchNow?.Invoke();
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                Cancelled?.Invoke();
                break;
            case VirtualKey.Down when HitList.TryGetElement(0) is Button first:
                e.Handled = true;
                first.Focus(FocusState.Keyboard);
                break;
        }
    }

    private void OnHitClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SearchRowItem row })
        {
            HitChosen?.Invoke(row);
        }
    }
}
