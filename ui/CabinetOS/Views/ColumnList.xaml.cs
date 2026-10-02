using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Listing;
using CabinetOS.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace CabinetOS.Views;

/// <summary>
/// One column of the column view (ADR 0016; docs/ui.md, "The column view"):
/// a folder's rows, names only, in a virtualizing <c>ItemsRepeater</c> with
/// its own vertical scroll. It shows what <see cref="ColumnView"/> gives it,
/// the pane's own rows and selection when the keyboard is in it, and tells
/// the view what the pointer did; it decides nothing.
/// </summary>
public sealed partial class ColumnList : UserControl
{
    private readonly HashSet<ColumnRow> _realized = [];
    private readonly RowFactory _rows;
    private ListingRows? _shown;
    private SelectionModel? _selection;
    private bool _keyboard;
    private bool _active;
    private double _rowHeight = 30;
    private double _padding = 4;
    private int _preparedFirst = int.MaxValue;
    private int _preparedLast = -1;
    private bool _detailsQueued;

    /// <summary>Creates an empty column; <see cref="Show"/> gives it rows.</summary>
    public ColumnList()
    {
        InitializeComponent();
        _rows = new RowFactory((DataTemplate)Resources["RowTemplate"], DispatcherQueue);
        Repeater.ItemTemplate = _rows;
        Repeater.ElementPrepared += OnElementPrepared;
        Repeater.ElementClearing += OnElementClearing;
        Repeater.Tapped += (_, e) => Raise(RowTapped, e.OriginalSource);
        Repeater.DoubleTapped += (_, e) =>
        {
            if (RowFrom(e.OriginalSource) is { Index: >= 0 } row)
            {
                RowDoubleTapped?.Invoke(this, row.Index, row.ShowsChevron);
            }
        };
        Root.RightTapped += (_, e) =>
        {
            e.Handled = true;
            RowRightTapped?.Invoke(this, RowFrom(e.OriginalSource)?.Index ?? -1, e.GetPosition(null));
        };
        Scroller.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnWheel), handledEventsToo: true);
        ApplyMetrics();
    }

    /// <summary>Which column this is, from 0.</summary>
    public int Column { get; set; }

    /// <summary>Where the rows' details come from for the column with the keyboard, which asks the core for them; null elsewhere.</summary>
    internal PaneModel? DetailsSource { get; set; }

    /// <summary>A row was clicked: (column, row).</summary>
    public event Action<ColumnList, int>? RowTapped;

    /// <summary>A row was double-clicked: (column, row, whether it is a folder).</summary>
    public event Action<ColumnList, int, bool>? RowDoubleTapped;

    /// <summary>A right-click: (column, row or -1 for the empty space, where, in the window's coordinates).</summary>
    public event Action<ColumnList, int, Point>? RowRightTapped;

    /// <summary>The wheel turned over rows that cannot scroll themselves: the view scrolls sideways by this delta.</summary>
    public event Action<double>? SidewaysWheel;

    /// <summary>The height the rows have to show in.</summary>
    public double ViewportHeight => Scroller.ViewportHeight;

    /// <summary>How far down the rows are scrolled.</summary>
    public double VerticalOffset => Scroller.VerticalOffset;

    /// <summary>The space around the rows.</summary>
    public double ListPadding => _padding;

    /// <summary>The folder the column shows.</summary>
    public string Folder { get; private set; } = "";

    /// <summary>
    /// Shows <paramref name="folder"/>'s <paramref name="rows"/> with
    /// <paramref name="selection"/>'s cursor and marks; <paramref name="keyboard"/>
    /// when the keyboard is in this column, <paramref name="active"/> when its
    /// pane is the active one, and <paramref name="message"/> in place of rows
    /// (an empty or lost folder). Another folder starts at its top; the same
    /// one (a refresh, the keyboard coming or going) keeps its scroll.
    /// </summary>
    public void Show(string folder, ListingRows? rows, SelectionModel? selection, bool keyboard, bool active, string? message)
    {
        _selection = selection;
        _keyboard = keyboard;
        _active = active;
        if (!ReferenceEquals(_shown, rows))
        {
            _shown = rows;
            Repeater.ItemsSource = rows;
        }
        if (!string.Equals(Folder, folder, StringComparison.OrdinalIgnoreCase))
        {
            Folder = folder;
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
        }
        MessageText.Text = message ?? "";
        MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        Mark();
    }

    /// <summary>Marks every row on screen as selected, marked or the cursor, as its selection says now.</summary>
    public void Mark()
    {
        foreach (var row in _realized)
        {
            Mark(row);
        }
    }

    /// <summary>Lays the column out with the window's sizes now: its width, the rows' height, the inset.</summary>
    public void ApplyMetrics()
    {
        var m = WindowMetrics.Current;
        Root.Width = m.ColumnViewWidth;
        _rowHeight = m.RowHeight;
        _padding = WindowMetrics.Chrome.Hairlines ? 0 : 4;
        Scroller.Padding = new Thickness(_padding);
        MessageText.FontSize = m.FontSize;
        MessageText.LineHeight = m.FontSize * m.LineHeight;
        foreach (var row in _realized)
        {
            row.ApplyMetrics();
        }
        Repeater.InvalidateMeasure();
    }

    /// <summary>Scrolls the row shown at <paramref name="position"/> into view.</summary>
    public void ScrollIntoView(int position)
    {
        if (position < 0)
        {
            return;
        }
        var top = _padding + (position * _rowHeight);
        var bottom = top + _rowHeight;
        var offset = Scroller.VerticalOffset;
        var viewport = Scroller.ViewportHeight;
        if (top - _padding < offset)
        {
            Scroller.ChangeView(null, Math.Max(0, top - _padding), null, disableAnimation: true);
        }
        else if (bottom + _padding > offset + viewport)
        {
            Scroller.ChangeView(null, bottom + _padding - viewport, null, disableAnimation: true);
        }
    }

    /// <summary>The row shown at <paramref name="position"/>, when it is on screen.</summary>
    public ColumnRow? RowAt(int position) => position >= 0 ? Repeater.TryGetElement(position) as ColumnRow : null;

    /// <summary>How many rows the column has: the ones on screen are only some of them.</summary>
    internal int RowCount => _shown?.Count ?? 0;

    /// <summary>The rows on screen, top first (the snapshot aid's log).</summary>
    public IEnumerable<ColumnRow> RealizedRows => _realized.Where(r => r.Index >= 0).OrderBy(r => r.Index);

    /// <summary>Shows the icon of <paramref name="key"/> in the rows that wait for it.</summary>
    public void RefreshIcon(string key)
    {
        foreach (var row in _realized)
        {
            if (string.Equals(row.IconKey, key, StringComparison.Ordinal))
            {
                row.RefreshDetails();
            }
        }
    }

    /// <summary>Shows the icons of rows <paramref name="from"/> to <paramref name="from"/> + <paramref name="count"/> again, or of every row.</summary>
    public void RefreshDetails(int from = 0, int count = int.MaxValue)
    {
        foreach (var row in _realized)
        {
            if (row.Index >= from && row.Index - from < count)
            {
                row.RefreshDetails();
            }
        }
    }

    private void Mark(ColumnRow row)
    {
        if (_selection is not { } selection || row.Index < 0)
        {
            row.SetSelected(false, away: false);
            row.ShowsCursor = false;
            return;
        }
        var index = row.Index;
        var selected = selection.IsSelected(index);
        if (_keyboard)
        {
            row.SetSelected(selected, away: false);
            // The outline marks the keyboard's row when the fill alone would not say which it is, as in the list.
            row.ShowsCursor = index == selection.Focus && _active && (!selected || selection.SelectedCount > 1);
        }
        else
        {
            // A column the keyboard left keeps its cursor row lit: the folder the next column shows, as the path goes.
            row.SetSelected(selected || index == selection.Focus, away: true);
            row.ShowsCursor = false;
        }
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        var started = FrameParts.Start();
        if (args.Element is ColumnRow row)
        {
            row.Show(row.DataContext);
            _realized.Add(row);
            Mark(row);
        }
        _rows.MakeAhead(Math.Max(8, (int)(Scroller.ViewportHeight / Math.Max(1, _rowHeight)) + 2));
        if (DetailsSource is not null)
        {
            // One describe_entries per layout pass for the rows it made, as the list asks.
            _preparedFirst = Math.Min(_preparedFirst, args.Index);
            _preparedLast = Math.Max(_preparedLast, args.Index);
            if (!_detailsQueued)
            {
                _detailsQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, AskForDetails);
            }
        }
        FrameParts.Stop(FramePart.Bind, started);
    }

    private void AskForDetails()
    {
        _detailsQueued = false;
        var (first, last) = (_preparedFirst, _preparedLast);
        _preparedFirst = int.MaxValue;
        _preparedLast = -1;
        if (last >= first && DetailsSource is { Search: null } pane)
        {
            pane.EnsureShownDetails(first, last);
        }
    }

    private void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is ColumnRow row)
        {
            _realized.Remove(row);
            row.SetSelected(false, away: false);
            row.ShowsCursor = false;
        }
    }

    // The wheel scrolls a column's own rows while they are longer than it; else, or with Shift, the columns go sideways.
    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var shift = (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
        if (!shift && Scroller.ScrollableHeight > 0)
        {
            return;
        }
        e.Handled = true;
        SidewaysWheel?.Invoke(e.GetCurrentPoint(this).Properties.MouseWheelDelta);
    }

    private void Raise(Action<ColumnList, int>? handler, object source)
    {
        if (RowFrom(source) is { Index: >= 0 } row)
        {
            handler?.Invoke(this, row.Index);
        }
    }

    private ColumnRow? RowFrom(object source)
    {
        for (var element = source as DependencyObject; element is not null && element != Repeater; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ColumnRow row)
            {
                return row;
            }
        }
        return null;
    }
}
