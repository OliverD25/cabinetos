using System.Text.Json;
using CabinetOS.Core.Tabs;
using CabinetOS.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace CabinetOS.Views;

/// <summary>
/// A pane's tab strip (Phases 12 and 16; docs/ui.md, "Tabs"). It shows the
/// pane's <see cref="TabStrip"/>, one tab or more, and reports what the user
/// does as commands (<c>tab.select</c>, <c>tab.close</c>, <c>tab.new</c> from
/// its "+", <c>tab.toggleLock</c>, <c>tab.moveToOtherPane</c>), each with the
/// pane and the tab it concerns: the window decides and changes the strip,
/// and the strip follows its <see cref="TabStrip.Changed"/>. A middle click
/// closes any tab; the tab in front shows its ×. The keyboard never rests on
/// the strip. The tabs are the window's own elements, drawn as the design
/// draws them: flat, a 2 px bar over the tab in front, no icon.
/// </summary>
public sealed partial class PaneTabs : UserControl
{
    // The design's tab: 10 px at each side, at most 160 px wide, a 1 px hairline at its right, the × 16 px wide with 6 px before it.
    private const double TabPadding = 10;
    private const double MaxTabWidth = 160;
    private const double HairlineWidth = 1;
    private const double CloseWidth = 16;
    private const double CloseGap = 2;
    private const double ClosePadding = 5;
    private const double StateGlyphWidth = 12;
    private const double StateGlyphGap = 6;

    // A fill that draws nothing but still takes the pointer: a tab with no fill must hear it over its whole box.
    private static readonly Brush Clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private readonly List<PaneTab> _shown = [];
    private readonly List<Cell> _cells = [];
    private TabStrip? _strip;
    private bool _accent;
    private bool _settleQueued;
    private bool _scrollPending;
    private bool _addPinned;
    private int _lastFront = -1;

    /// <summary>Creates the row, hidden.</summary>
    public PaneTabs()
    {
        InitializeComponent();
        AddButton.Click += (_, _) => _ = RunCommand?.Invoke("tab.new", CommandArgs.Object(("pane", PaneIndex)), "button");
        Scroller.SizeChanged += (_, _) => QueueSettle(scroll: true);
        TabList.SizeChanged += (_, _) => QueueSettle(scroll: false);
        Loaded += (_, _) =>
        {
            NameAddButton();
            QueueSettle(scroll: true);
        };
        ApplyMetrics();
    }

    /// <summary>Which pane this row belongs to: 0 left, 1 right.</summary>
    public int PaneIndex { get; set; }

    /// <summary>Runs a command through the window's router: (command, args, trigger).</summary>
    public Func<string, JsonElement?, string, Task>? RunCommand { get; set; }

    /// <summary>A registry command's first key as the menu shows it, or null.</summary>
    public Func<string, string?>? KeysOf { get; set; }

    /// <summary>Whether the other pane is shown, so a tab may move there.</summary>
    public bool CanMoveToOtherPane { get; set; }

    /// <summary>The pane's tabs; the row follows them.</summary>
    public TabStrip? Model
    {
        get => _strip;
        set
        {
            if (_strip is not null)
            {
                _strip.Changed -= Refresh;
            }
            _strip = value;
            if (_strip is not null)
            {
                _strip.Changed += Refresh;
            }
            _shown.Clear();
            Refresh();
        }
    }

    /// <summary>
    /// Whether the strip belongs to the active pane: the 2 px bar over its
    /// front tab is the accent then, and white at 30 % in the other pane.
    /// </summary>
    public bool ShowsAccent
    {
        get => _accent;
        set
        {
            if (_accent != value)
            {
                _accent = value;
                foreach (var cell in _cells)
                {
                    Layout(cell);
                    Paint(cell);
                }
            }
        }
    }

    /// <summary>The tabs as the row shows them, for the snapshot aid's log: titles, the one in front marked with *.</summary>
    public string Describe() => _strip is null
        ? ""
        : string.Join(" | ", _strip.Tabs.Select((tab, i) => $"{(i == _strip.ActiveIndex ? "*" : "")}{tab.Title}{(tab.Locked ? " (locked)" : "")}{(tab.IsTool ? " (tool)" : "")}"));

    /// <summary>The strip's height now, 0 while it is hidden (the snapshot aid's <c>layout:</c> step).</summary>
    public double RowHeight => Visibility == Visibility.Visible ? ActualHeight : 0;

    /// <summary>Lays the strip out with the window's sizes now: the theme's <c>tabRow</c> as its height, the tab text at <c>tabFontSize</c>, the tabs' top corners at <c>tabRadius</c>.</summary>
    public void ApplyMetrics()
    {
        var m = WindowMetrics.Current;
        Root.Height = m.TabRow;
        AddButton.Height = m.TabRow;
        foreach (var cell in _cells)
        {
            Layout(cell);
        }
        NameAddButton();
        QueueSettle(scroll: true);
    }

    // The strip's "+": the key of tab.new in its tooltip (its name for assistive technology is in the markup).
    private void NameAddButton() =>
        ToolTipService.SetToolTip(AddButton, KeysOf?.Invoke("tab.new") is { } keys ? $"New tab ({keys})" : "New tab");

    /// <summary>Makes the row show the strip as it is now: the tabs, their titles, locks and close buttons, and the one in front.</summary>
    public void Refresh()
    {
        if (_strip is not { } strip)
        {
            return;
        }
        var rebuilt = !_shown.SequenceEqual(strip.Tabs);
        if (rebuilt)
        {
            foreach (var cell in _cells)
            {
                TabList.Children.Remove(cell.Outer);
            }
            _cells.Clear();
            _shown.Clear();
            foreach (var tab in strip.Tabs)
            {
                var cell = NewCell();
                TabList.Children.Insert(_cells.Count, cell.Outer);
                _cells.Add(cell);
                _shown.Add(tab);
            }
        }
        for (var i = 0; i < _cells.Count; i++)
        {
            Update(_cells[i], strip.Tabs[i], i, strip);
        }
        if (rebuilt || strip.ActiveIndex != _lastFront)
        {
            _lastFront = strip.ActiveIndex;
            QueueSettle(scroll: true);
        }
    }

    private Cell NewCell()
    {
        var cell = new Cell();
        cell.Row.Children.Add(cell.State);
        cell.Row.Children.Add(cell.Title);
        cell.Row.Children.Add(cell.Close);
        cell.Face.Child = cell.Row;
        cell.Outer.Child = cell.Face;

        cell.Close.Content = cell.CloseText;
        cell.Close.Style = (Style)Resources["TabGlyphButtonStyle"];
        cell.Close.IsTabStop = false;
        cell.Close.AllowFocusOnInteraction = false;
        AutomationProperties.SetName(cell.Close, "Close tab");
        cell.Close.Click += (_, _) => Run("tab.close", _cells.IndexOf(cell));

        // Hover and a press are drawn here, since the tabs are built here: a tab with no fill shows one under the pointer.
        cell.Outer.PointerEntered += (_, _) => Hover(cell, true);
        cell.Outer.PointerExited += (_, _) => Hover(cell, false);
        cell.Outer.PointerCanceled += (_, _) => Hover(cell, false);
        cell.Outer.PointerCaptureLost += (_, _) => Hover(cell, false);
        cell.Outer.PointerPressed += (_, e) =>
        {
            var index = _cells.IndexOf(cell);
            var point = e.GetCurrentPoint(cell.Outer).Properties;
            if (index < 0)
            {
                return;
            }
            // A middle click closes any tab, the one in front or not; a click on another tab brings it to the front.
            if (point.IsMiddleButtonPressed)
            {
                e.Handled = true;
                Run("tab.close", index);
            }
            else if (point.IsLeftButtonPressed && index != _strip?.ActiveIndex)
            {
                Run("tab.select", index);
            }
        };
        cell.Outer.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            ShowMenu(cell, e.TryGetPosition(cell.Outer, out var at) ? at : new Point(0, cell.Outer.ActualHeight));
        };
        return cell;
    }

    private void Update(Cell cell, PaneTab tab, int index, TabStrip strip)
    {
        var title = tab.Title.Length > 0 ? tab.Title : tab.Path;
        if (cell.Title.Text != title)
        {
            cell.Title.Text = title;
        }
        // No icon for a folder: a mark stays only where the tab has a state.
        cell.Glyph = tab.IsTool ? "" : tab.Locked ? "" : null;
        if (cell.Glyph is not null && cell.State.Glyph != cell.Glyph)
        {
            cell.State.Glyph = cell.Glyph;
        }
        cell.Front = index == strip.ActiveIndex;
        // The tab in front has a ×; the others close with a middle click or their menu.
        cell.Closable = cell.Front && strip.CanClose(index);
        ToolTipService.SetToolTip(cell.Outer, tab.IsTool ? $"{tab.ToolName}: {tab.Path}" : tab.Locked ? $"{tab.Path} (locked)" : tab.Path);
        AutomationProperties.SetName(cell.Outer, tab.Locked ? $"{title}, locked" : title);
        Layout(cell);
        Paint(cell);
    }

    // What the tab's size depends on: the theme's metrics, and whether it shows a mark and its ×.
    private void Layout(Cell cell)
    {
        var m = WindowMetrics.Current;
        cell.Outer.Height = m.TabRow;
        // The 2 px bar is the face's own top border, so it follows the curve when a theme rounds the corners.
        cell.Face.CornerRadius = WindowMetrics.TopCorners(m.TabRadius);
        cell.Title.FontSize = cell.CloseText.FontSize = m.TabFontSize;
        cell.State.Visibility = cell.Glyph is null ? Visibility.Collapsed : Visibility.Visible;
        // In the other pane the tab in front shows its × only under the pointer: the design draws that tab without one.
        var close = cell.Closable && (_accent || cell.Hover);
        cell.Close.Visibility = close ? Visibility.Visible : Visibility.Collapsed;
        var right = close ? ClosePadding : TabPadding;
        cell.Face.Padding = new Thickness(TabPadding, 0, right, 0);
        // The name takes what the tab's 160 px leave after its padding, hairline, mark and ×; it ends with an ellipsis.
        var around = TabPadding + right + HairlineWidth
            + (cell.Glyph is null ? 0 : StateGlyphWidth + StateGlyphGap)
            + (close ? CloseGap + CloseWidth : 0);
        cell.Title.MaxWidth = Math.Max(0, MaxTabWidth - around);
    }

    // What the tab looks like: the design's tab in front (of the active pane or of the other), and the others, resting or under the pointer.
    private void Paint(Cell cell)
    {
        Brush fill, bar, text;
        if (cell.Front)
        {
            fill = ThemeResources.Brush(_accent ? "CbTabActiveFillBrush" : "CbHoverFillBrush");
            bar = ThemeResources.Brush(_accent ? "CbAccentBrush" : "CbTabFrontBarInactiveBrush");
            // White at 85 % in the other pane: the nearest token is the row text's 90 %.
            text = ThemeResources.Brush(_accent ? "CbTextPrimaryBrush" : "CbRowTextBrush");
        }
        else
        {
            fill = cell.Hover ? ThemeResources.Brush("CbHoverFillBrush") : Clear;
            bar = Clear;
            text = ThemeResources.Brush(cell.Hover ? "CbTextPrimaryBrush" : "CbTabInactiveTextBrush");
        }
        cell.Face.Background = fill;
        cell.Face.BorderBrush = bar;
        // The hairline at the right of every tab but the one in front; it keeps its 1 px there, so a tab's width does not change with the front.
        cell.Outer.BorderBrush = cell.Front ? Clear : ThemeResources.Brush("CbShellHairlineBrush");
        cell.Title.Foreground = text;
        cell.State.Foreground = text;
        cell.Title.FontWeight = cell.Front ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void Hover(Cell cell, bool on)
    {
        if (cell.Hover != on)
        {
            cell.Hover = on;
            Layout(cell);
            Paint(cell);
        }
    }

    // What depends on the tabs' widths is settled after the layout pass, not inside it: where the "+" sits, and, when asked,
    // the tab in front coming into view (when it changes or the strip's width does).
    private void QueueSettle(bool scroll)
    {
        _scrollPending |= scroll;
        if (_settleQueued)
        {
            return;
        }
        _settleQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _settleQueued = false;
            Settle();
        });
    }

    private void Settle()
    {
        if (_strip is null || !IsLoaded)
        {
            return;
        }
        UpdateLayout();
        if (PlaceAddButton())
        {
            UpdateLayout();
        }
        if (_scrollPending)
        {
            ScrollToFront();
        }
    }

    // The "+" is right after the last tab while the tabs fit. When they do not, it leaves the scrolling row for its own
    // place at the strip's end, so it does not scroll away. Whether they fit does not depend on where the "+" is (the
    // tabs' widths plus its 24 px against the room), so the two places cannot flip each other.
    private bool PlaceAddButton()
    {
        var room = Root.ActualWidth;
        if (room <= 0)
        {
            return false;
        }
        var pin = _cells.Sum(cell => cell.Outer.ActualWidth) + AddButton.Width > room + 0.5;
        if (pin == _addPinned)
        {
            return false;
        }
        _addPinned = pin;
        if (pin)
        {
            TabList.Children.Remove(AddButton);
            PinnedAdd.Child = AddButton;
        }
        else
        {
            PinnedAdd.Child = null;
            TabList.Children.Add(AddButton);
        }
        return true;
    }

    private void ScrollToFront()
    {
        if (_strip is null || (uint)_strip.ActiveIndex >= (uint)_cells.Count)
        {
            return;
        }
        var tab = _cells[_strip.ActiveIndex].Outer;
        if (tab.ActualWidth <= 0 || Scroller.ViewportWidth <= 0)
        {
            return;
        }
        _scrollPending = false;
        var left = tab.TransformToVisual(TabList).TransformPoint(new Point(0, 0)).X;
        var right = left + tab.ActualWidth;
        if (left < Scroller.HorizontalOffset)
        {
            Scroller.ChangeView(left, null, null, disableAnimation: true);
        }
        else if (right > Scroller.HorizontalOffset + Scroller.ViewportWidth)
        {
            Scroller.ChangeView(Math.Min(left, right - Scroller.ViewportWidth), null, null, disableAnimation: true);
        }
    }

    private void Run(string command, int tab)
    {
        if (tab >= 0)
        {
            _ = RunCommand?.Invoke(command, CommandArgs.Object(("pane", PaneIndex), ("tab", tab)), "button");
        }
    }

    private void ShowMenu(Cell cell, Point at)
    {
        var index = _cells.IndexOf(cell);
        if (_strip is null || index < 0 || index >= _strip.Count)
        {
            return;
        }
        var tab = _strip.Tabs[index];
        var menu = new MenuFlyout();
        if (!tab.IsTool)
        {
            menu.Items.Add(MenuItem(tab.Locked ? "Unlock tab" : "Lock tab", tab.Locked ? "" : "", "tab.toggleLock", index));
            menu.Items.Add(MenuItem("Move to other pane", "", "tab.moveToOtherPane", index, enabled: CanMoveToOtherPane && _strip.CanClose(index)));
        }
        menu.Items.Add(MenuItem("Close tab", "", "tab.close", index, enabled: _strip.CanClose(index)));
        menu.ShowAt(cell.Outer, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = at });
    }

    private MenuFlyoutItem MenuItem(string text, string glyph, string command, int index, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
        if (KeysOf?.Invoke(command) is { } keys)
        {
            item.KeyboardAcceleratorTextOverride = keys;
        }
        item.Click += (_, _) => Run(command, index);
        return item;
    }

    // One tab's elements and what its look depends on. Outer holds the 1 px hairline at the right, Face the fill and the
    // 2 px top bar (its top border), Row the mark, the name and the ×.
    private sealed class Cell
    {
        public Border Outer { get; } = new() { BorderThickness = new Thickness(0, 0, HairlineWidth, 0) };

        public Border Face { get; } = new() { BorderThickness = new Thickness(0, 2, 0, 0) };

        public StackPanel Row { get; } = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        public FontIcon State { get; } = new()
        {
            FontSize = StateGlyphWidth,
            Width = StateGlyphWidth,
            Margin = new Thickness(0, 0, StateGlyphGap, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        public TextBlock Title { get; } = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            IsHitTestVisible = false,
        };

        public TextBlock CloseText { get; } = new() { Text = "×", FontWeight = FontWeights.Normal };

        public Button Close { get; } = new()
        {
            Width = CloseWidth,
            Height = CloseWidth,
            Margin = new Thickness(CloseGap, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        public string? Glyph { get; set; }

        public bool Front { get; set; }

        public bool Closable { get; set; }

        public bool Hover { get; set; }
    }
}
