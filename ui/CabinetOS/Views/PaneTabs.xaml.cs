using System.Text.Json;
using CabinetOS.Core.Tabs;
using CabinetOS.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace CabinetOS.Views;

/// <summary>
/// A pane's tab strip (Phases 12 and 16, and v2 of the shell redesign;
/// docs/ui.md, "Tabs in the shell"). It shows the pane's
/// <see cref="TabStrip"/>, one tab or more, and reports what the user does as
/// commands (<c>tab.select</c>, <c>tab.close</c>, <c>tab.new</c> from its "+",
/// <c>tab.toggleLock</c>, <c>tab.moveToOtherPane</c>), each with the pane and
/// the tab it concerns: the window decides and changes the strip, and the
/// strip follows its <see cref="TabStrip.Changed"/>. A middle click closes
/// any tab; the tab in front shows its × while the pane has more than one
/// (<see cref="TabLook"/>). The keyboard never rests on the strip. The tabs
/// are the window's own elements, drawn as the design draws them: a recessed
/// band, the tab in front a card in the toolbar row's fill, the others lower
/// with dividers between them, a glyph before every name, no accent line.
/// </summary>
public sealed partial class PaneTabs : UserControl
{
    // The design's tab: 9 px before its glyph, 6 after its ×, 7 between them; the folder glyph 14 px, the × 16 px wide.
    private const double PaddingLeft = 9;
    private const double PaddingRight = 6;
    private const double ItemGap = 7;
    private const double GlyphSize = 14;
    private const double CloseWidth = 16;
    private const double DividerInset = 6;

    // A fill that draws nothing but still takes the pointer: a tab with no fill must hear it over its whole box.
    private static readonly Brush Clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private readonly List<PaneTab> _shown = [];
    private readonly List<Cell> _cells = [];
    private TabStrip? _strip;
    private bool _active;
    private bool _settleQueued;
    private bool _scrollPending;
    private bool _addPinned;
    private int _lastFront = -1;

    /// <summary>Creates the row, hidden.</summary>
    public PaneTabs()
    {
        InitializeComponent();
        AddButton.Click += (_, _) => _ = RunCommand?.Invoke("tab.new", CommandArgs.Object(("pane", PaneIndex)), "button");
        Scroller.SizeChanged += (_, _) =>
        {
            // The band reaches the strip's end: the row is at least as wide as the room it scrolls in.
            TabRow.MinWidth = Scroller.ActualWidth;
            QueueSettle(scroll: true);
        };
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
    /// Whether the strip belongs to the active pane (or the only one): the
    /// tab in front is then filled white 9 %, else white 5 %, as the toolbar
    /// row under it is.
    /// </summary>
    public bool IsActivePane
    {
        get => _active;
        set
        {
            if (_active != value)
            {
                _active = value;
                foreach (var cell in _cells)
                {
                    Paint(cell);
                }
            }
        }
    }

    /// <summary>The brush the tab in front is filled with now, by its name (the snapshot aid's log compares it with the toolbar's).</summary>
    public string FrontFillKey => _active ? "CbFrontTabFillBrush" : "CbFrontTabInactiveFillBrush";

    /// <summary>The tabs as the row shows them, for the snapshot aid's log: titles, the one in front marked with *.</summary>
    public string Describe() => _strip is null
        ? ""
        : string.Join(" | ", _strip.Tabs.Select((tab, i) => $"{(i == _strip.ActiveIndex ? "*" : "")}{tab.Title}{(tab.Locked ? " (locked)" : "")}{(tab.IsTool ? " (tool)" : "")}"));

    /// <summary>
    /// How the row draws each tab, for the snapshot aid's log: its glyph, and
    /// "divider" and "close" where it shows them, the one in front marked with
    /// * and its height, "folder,divider | *folder,close,32 | folder".
    /// </summary>
    public string DescribeLook() => string.Join(" | ", _cells.Select(cell =>
        $"{(cell.Front ? "*" : "")}{cell.Kind.ToString().ToLowerInvariant()}{(cell.Divider.Visibility == Visibility.Visible ? ",divider" : "")}{(cell.Close.Opacity > 0 ? ",close" : "")}{(cell.Front ? $",{cell.Face.ActualHeight:0}" : "")}"));

    /// <summary>The strip's height now, 0 while it is hidden (the snapshot aid's <c>layout:</c> step).</summary>
    public double RowHeight => Visibility == Visibility.Visible ? ActualHeight : 0;

    /// <summary>Lays the strip out with the window's sizes now: the theme's <c>tabRow</c> as its height, the tab text at <c>tabFontSize</c>, the tabs' top corners at <c>tabRadius</c>, their widest at <c>tabMaxWidth</c>.</summary>
    public void ApplyMetrics()
    {
        var m = WindowMetrics.Current;
        Root.Height = m.TabRow;
        var heights = TabLook.Heights(m.TabRow);
        AddButton.Height = Math.Max(14, Math.Min(22, heights.Behind));
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

    /// <summary>Makes the row show the strip as it is now: the tabs, their titles, glyphs and close buttons, and the one in front.</summary>
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
                TabList.Children.Add(cell.Outer);
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
        cell.Row.Children.Add(cell.FolderGlyph);
        cell.Row.Children.Add(cell.StateGlyph);
        cell.Row.Children.Add(cell.Title);
        cell.Row.Children.Add(cell.Close);
        cell.Face.Child = cell.Row;
        cell.Outer.Children.Add(cell.Band);
        cell.Outer.Children.Add(cell.Face);
        cell.Outer.Children.Add(cell.Divider);

        cell.Close.Content = new FontIcon { Glyph = "", FontSize = 8 };
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
        // A folder shows the design's folder; a locked tab its lock and a tool tab its tool's glyph in the folder's place.
        cell.Kind = TabLook.Glyph(tab);
        var glyph = cell.Kind switch
        {
            TabGlyph.Tool => "",
            TabGlyph.Lock => "",
            _ => null,
        };
        if (glyph is not null && cell.StateGlyph.Glyph != glyph)
        {
            cell.StateGlyph.Glyph = glyph;
        }
        cell.Front = index == strip.ActiveIndex;
        cell.Closable = TabLook.ShowsClose(cell.Front, strip.CanClose(index));
        cell.Divider.Visibility = TabLook.Divider(index, strip.ActiveIndex, strip.Count) ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(cell.Outer, tab.IsTool ? $"{tab.ToolName}: {tab.Path}" : tab.Locked ? $"{tab.Path} (locked)" : tab.Path);
        AutomationProperties.SetName(cell.Outer, tab.Locked ? $"{title}, locked" : title);
        Layout(cell);
        Paint(cell);
    }

    // What the tab's size depends on: the theme's metrics, whether it is in front, and its glyph.
    private void Layout(Cell cell)
    {
        var m = WindowMetrics.Current;
        var heights = TabLook.Heights(m.TabRow);
        cell.Outer.Height = m.TabRow;
        // The tab in front stands on the strip's bottom edge, the band above it; the others are lower, 3 px above that edge,
        // on the band, and the band's bottom line runs under them.
        cell.Face.Height = cell.Front ? heights.Front : heights.Behind;
        cell.Face.Margin = new Thickness(0, 0, 0, cell.Front ? 0 : heights.BehindMargin);
        cell.Face.CornerRadius = WindowMetrics.TopCorners(m.TabRadius);
        cell.Band.Height = cell.Front ? Math.Max(0, m.TabRow - heights.Front) : double.NaN;
        cell.Band.VerticalAlignment = cell.Front ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        cell.Band.BorderThickness = new Thickness(0, 0, 0, cell.Front ? 0 : 1);
        cell.Divider.Height = Math.Max(0, heights.Behind - (2 * DividerInset));
        cell.Divider.Margin = new Thickness(0, 0, 0, heights.BehindMargin + DividerInset);
        cell.Title.FontSize = m.TabFontSize;
        cell.FolderGlyph.Visibility = cell.Kind == TabGlyph.Folder ? Visibility.Visible : Visibility.Collapsed;
        cell.StateGlyph.Visibility = cell.Kind == TabGlyph.Folder ? Visibility.Collapsed : Visibility.Visible;
        // The × keeps its place on every tab, shown or not, so a tab's width does not change when it comes to the front.
        cell.Close.Opacity = cell.Closable ? 1 : 0;
        cell.Close.IsHitTestVisible = cell.Closable;
        // The name takes what the tab's widest leaves after its padding, glyph and ×; it ends with an ellipsis.
        cell.Title.MaxWidth = Math.Max(0, m.TabMaxWidth - (PaddingLeft + PaddingRight + GlyphSize + ItemGap + ItemGap + CloseWidth));
    }

    // What the tab looks like: the card in front (of the active pane or of the other), and the others, resting or under the pointer.
    private void Paint(Cell cell)
    {
        Brush fill, text;
        if (cell.Front)
        {
            fill = ThemeResources.Brush(FrontFillKey);
            text = ThemeResources.Brush("CbTextPrimaryBrush");
        }
        else
        {
            fill = cell.Hover ? ThemeResources.Brush("CbTabHoverFillBrush") : Clear;
            text = ThemeResources.Brush(cell.Hover ? "CbTextPrimaryBrush" : "CbTabInactiveTextBrush");
        }
        cell.Face.Background = fill;
        // The card's top highlight, inset 1 px: white 12 % in the active pane, 8 % in the other; none on the others.
        cell.Face.BorderBrush = cell.Front ? ThemeResources.Brush(_active ? "CbTabHighlightBrush" : "CbHairlineBrush") : Clear;
        cell.Band.Background = ThemeResources.Brush("CbTabBandFillBrush");
        cell.Band.BorderBrush = ThemeResources.Brush("CbShellHairlineBrush");
        cell.Divider.Fill = ThemeResources.Brush("CbTabDividerBrush");
        cell.Title.Foreground = text;
        cell.StateGlyph.Foreground = text;
        cell.Title.FontWeight = cell.Front ? FontWeights.SemiBold : FontWeights.Normal;
        cell.FolderGlyph.Opacity = cell.StateGlyph.Opacity = cell.Front || cell.Hover ? 1 : 0.6;
    }

    private void Hover(Cell cell, bool on)
    {
        if (cell.Hover != on)
        {
            cell.Hover = on;
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
    // band's padding, the tabs' widths and the "+" with its margin against the room), so the two places cannot flip each other.
    private bool PlaceAddButton()
    {
        var room = Root.ActualWidth;
        if (room <= 0)
        {
            return false;
        }
        var add = AddButton.Width + AddButton.Margin.Left + AddButton.Margin.Right;
        var pin = LeftPad.Width + _cells.Sum(cell => cell.Outer.ActualWidth) + add > room + 0.5;
        if (pin == _addPinned)
        {
            return false;
        }
        _addPinned = pin;
        if (pin)
        {
            AddCell.Child = null;
            PinnedAdd.Child = AddButton;
        }
        else
        {
            PinnedAdd.Child = null;
            AddCell.Child = AddButton;
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
        var left = tab.TransformToVisual(TabRow).TransformPoint(new Point(0, 0)).X;
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

    // One tab's elements and what its look depends on. Outer is the tab's whole height and takes the pointer; Band draws
    // the strip's band there (above the card in front, behind the others, with the band's bottom line); Face is the tab
    // itself: the card in front, or the fill a tab behind shows under the pointer; Divider is the line at a tab's right.
    private sealed class Cell
    {
        public Grid Outer { get; } = new() { Background = Clear };

        public Border Band { get; } = new();

        public Border Face { get; } = new()
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(PaddingLeft, 0, PaddingRight, 0),
        };

        public Rectangle Divider { get; } = new()
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
        };

        public StackPanel Row { get; } = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Spacing = ItemGap };

        public Viewbox FolderGlyph { get; } = new()
        {
            Width = GlyphSize,
            Height = GlyphSize,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = new FolderGlyph(),
        };

        public FontIcon StateGlyph { get; } = new()
        {
            FontSize = 12,
            Width = GlyphSize,
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

        public Button Close { get; } = new()
        {
            Width = CloseWidth,
            Height = CloseWidth,
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
        };

        public TabGlyph Kind { get; set; }

        public bool Front { get; set; }

        public bool Closable { get; set; }

        public bool Hover { get; set; }
    }
}
