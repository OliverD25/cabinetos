using System.Globalization;
using CabinetOS.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace CabinetOS.Views;

/// <summary>
/// A pane's column view (ADR 0016; docs/ui.md, "The column view"): one
/// <see cref="ColumnList"/> per folder of the pane's <see cref="PaneColumns"/>,
/// side by side in a strip that scrolls sideways. The column with the
/// keyboard shows the pane's own rows and selection, so the list's keys, the
/// find and the quick search move there; the others show their own listings.
/// The view only shows and reports: the pane and its columns decide.
/// </summary>
public sealed partial class ColumnView : UserControl
{
    private readonly List<ColumnList> _lists = [];
    private PaneColumns? _columns;

    /// <summary>Creates the view; <see cref="Columns"/> gives it its columns.</summary>
    public ColumnView()
    {
        InitializeComponent();
    }

    /// <summary>The columns shown, or null.</summary>
    internal PaneColumns? Columns
    {
        get => _columns;
        set
        {
            _columns = value;
            Sync(bringIntoView: true);
        }
    }

    /// <summary>A row was clicked: (column, row).</summary>
    public event Action<int, int>? RowTapped;

    /// <summary>A row was double-clicked: (column, row, whether it is a folder).</summary>
    public event Action<int, int, bool>? RowDoubleTapped;

    /// <summary>A right-click: (column, row or -1 for a column's empty space, where, in the window's coordinates).</summary>
    public event Action<int, int, Point>? RowRightTapped;

    /// <summary>The column the keyboard is in, as shown, or null.</summary>
    public ColumnList? KeyboardList => _columns is { } columns && columns.Keyboard < _lists.Count ? _lists[columns.Keyboard] : null;

    /// <summary>How far the strip is scrolled sideways, its visible width and its whole width (the snapshot aid's log).</summary>
    public (double Offset, double Viewport, double Extent) StripScroll => (Strip.HorizontalOffset, Strip.ViewportWidth, Strip.ExtentWidth);

    /// <summary>
    /// Shows the columns as they are now: a column per folder, each with its
    /// rows, cursor and marks. With <paramref name="bringIntoView"/> the strip
    /// scrolls so the keyboard's column is whole on screen: after an open,
    /// that is the newest column.
    /// </summary>
    public void Sync(bool bringIntoView)
    {
        var columns = _columns;
        var count = columns?.Count ?? 0;
        while (_lists.Count < count)
        {
            var list = new ColumnList { Column = _lists.Count };
            list.RowTapped += (l, row) => RowTapped?.Invoke(l.Column, row);
            list.RowDoubleTapped += (l, row, folder) => RowDoubleTapped?.Invoke(l.Column, row, folder);
            list.RowRightTapped += (l, row, point) => RowRightTapped?.Invoke(l.Column, row, point);
            list.SidewaysWheel += ScrollSideways;
            _lists.Add(list);
            Lists.Children.Add(list);
        }
        while (_lists.Count > count)
        {
            var last = _lists[^1];
            last.DetailsSource = null;
            last.Show("", null, null, keyboard: false, active: false, message: null);
            Lists.Children.Remove(last);
            _lists.RemoveAt(_lists.Count - 1);
        }
        if (columns is null)
        {
            return;
        }
        var pane = columns.Pane;
        for (var i = 0; i < count; i++)
        {
            var list = _lists[i];
            var folder = columns.FolderAt(i);
            if (i == columns.Keyboard)
            {
                list.DetailsSource = pane;
                list.Show(folder, pane.Rows, pane.Selection, keyboard: true, pane.IsActive || !pane.IsDual, pane.Selection.IsFiltered ? null : pane.Message);
            }
            else
            {
                list.DetailsSource = null;
                var listing = columns.ListingAt(i);
                var message = listing?.Rows is null ? "This folder is no longer available." : listing.Rows.Count == 0 ? "This folder is empty." : null;
                list.Show(folder, listing?.Rows, listing?.Selection, keyboard: false, pane.IsActive, message);
            }
        }
        if (bringIntoView)
        {
            BringKeyboardIntoView();
        }
    }

    /// <summary>Marks the keyboard column's rows as the pane's selection says now.</summary>
    public void MarkKeyboard() => KeyboardList?.Mark();

    /// <summary>Scrolls the keyboard column's row shown at <paramref name="position"/> into view.</summary>
    public void ScrollKeyboardRowIntoView(int position) => KeyboardList?.ScrollIntoView(position);

    /// <summary>Lays every column out with the window's sizes now.</summary>
    public void ApplyMetrics()
    {
        foreach (var list in _lists)
        {
            list.ApplyMetrics();
        }
        BringKeyboardIntoView();
    }

    /// <summary>Shows the icon of <paramref name="key"/> in every column's rows that wait for it.</summary>
    public void RefreshIcon(string key)
    {
        foreach (var list in _lists)
        {
            list.RefreshIcon(key);
        }
    }

    /// <summary>Binds every column's icons again (the screen's scale changed the icon size).</summary>
    public void RefreshDetails()
    {
        foreach (var list in _lists)
        {
            list.RefreshDetails();
        }
    }

    /// <summary>The keyboard column's rows <paramref name="from"/> on got their details from the core.</summary>
    public void RefreshKeyboardDetails(int from, int count) => KeyboardList?.RefreshDetails(from, count);

    /// <summary>
    /// What each column shows on screen, for the snapshot aid's log: its
    /// rows' names, a folder's with "&gt;" for its chevron, "|" between rows.
    /// </summary>
    public IReadOnlyList<string> DescribeRows() =>
        [.. _lists.Select(list => string.Join("|", list.RealizedRows.Select(row => row.ShowsChevron ? row.RowName + ">" : row.RowName)))];

    /// <summary>Whether the keyboard's column lies whole inside the strip's visible part.</summary>
    public bool KeyboardInView()
    {
        if (KeyboardList is not { } list || list.ActualWidth <= 0)
        {
            return false;
        }
        var left = list.TransformToVisual(Strip).TransformPoint(new Point(0, 0)).X;
        return left >= -0.5 && left + list.ActualWidth <= Strip.ViewportWidth + 0.5;
    }

    /// <summary>The widths of the columns as laid out, "|" between them (the snapshot aid's log).</summary>
    public string Widths() => string.Join("|", _lists.Select(l => l.ActualWidth.ToString("0.#", CultureInfo.InvariantCulture)));

    private void ScrollSideways(double delta) =>
        Strip.ChangeView(Math.Max(0, Strip.HorizontalOffset - delta), null, null, disableAnimation: true);

    // The keyboard's column whole on screen: right-aligned when it lies beyond the right edge, left-aligned beyond the left.
    private void BringKeyboardIntoView()
    {
        if (_columns is not { } columns || _lists.Count == 0)
        {
            return;
        }
        Strip.UpdateLayout();
        var width = WindowMetrics.Current.ColumnViewWidth;
        var left = columns.Keyboard * width;
        var right = left + width;
        var offset = Strip.HorizontalOffset;
        var target = offset;
        if (right > offset + Strip.ViewportWidth)
        {
            target = right - Strip.ViewportWidth;
        }
        if (left < target)
        {
            target = left;
        }
        if (Math.Abs(target - offset) > 0.5)
        {
            Strip.ChangeView(Math.Max(0, target), null, null, disableAnimation: true);
        }
    }
}
