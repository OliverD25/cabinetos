using System.Text.Json;
using CabinetOS.Core.Tabs;
using CabinetOS.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
/// the strip.
/// </summary>
public sealed partial class PaneTabs : UserControl
{
    private readonly List<PaneTab> _shown = [];
    private TabStrip? _strip;
    private bool _updating;
    private bool _accent;

    /// <summary>Creates the row, hidden.</summary>
    public PaneTabs()
    {
        InitializeComponent();
        Strip.SelectionChanged += OnSelectionChanged;
        Strip.TabCloseRequested += OnCloseRequested;
        Strip.AddTabButtonClick += (_, _) => _ = RunCommand?.Invoke("tab.new", CommandArgs.Object(("pane", PaneIndex)), "button");
        Strip.SizeChanged += (_, _) => PositionAccent();
        Strip.Loaded += (_, _) => NameAddButton();
        OpenWithButton.Click += (_, _) => _ = RunCommand?.Invoke("palette.show", CommandArgs.With("query", "Editor"), "button");
        AccentLine.Fill = ThemeResources.Brush("CbTabFrontBarInactiveBrush");
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
                AccentLine.Fill = ThemeResources.Brush(value ? "CbAccentBrush" : "CbTabFrontBarInactiveBrush");
                PositionAccent();
            }
        }
    }

    /// <summary>The tabs as the row shows them, for the snapshot aid's log: titles, the one in front marked with *.</summary>
    public string Describe() => _strip is null
        ? ""
        : string.Join(" | ", _strip.Tabs.Select((tab, i) => $"{(i == _strip.ActiveIndex ? "*" : "")}{tab.Title}{(tab.Locked ? " (locked)" : "")}{(tab.IsTool ? " (tool)" : "")}"));

    /// <summary>The strip's height now, 0 while it is hidden (the snapshot aid's <c>layout:</c> step).</summary>
    public double RowHeight => Visibility == Visibility.Visible ? ActualHeight : 0;

    /// <summary>Lays the strip out with the window's sizes now: the theme's <c>tabRow</c> as its height, the tab text at the base size.</summary>
    public void ApplyMetrics()
    {
        var m = WindowMetrics.Current;
        Strip.Height = m.TabRow;
        Strip.FontSize = m.FontSize;
        OpenWithButton.Height = Math.Max(14, m.TabRow - 4);
        OpenWithButton.CornerRadius = WindowMetrics.Corners(m.RadiusControl);
        foreach (var item in Strip.TabItems.OfType<TabViewItem>())
        {
            SizeItem(item);
        }
        NameAddButton();
        PositionAccent();
    }

    private static void SizeItem(TabViewItem item)
    {
        var m = WindowMetrics.Current;
        item.Height = item.MinHeight = m.TabRow;
        item.FontSize = m.TabFontSize;
        item.Padding = new Thickness(10, 0, 4, 0);
        // The top corners follow the theme's tabRadius.
        item.CornerRadius = WindowMetrics.TopCorners(m.TabRadius);
    }

    // The strip's "+" (24 px): a name for assistive technology, and the key in its tooltip.
    private void NameAddButton()
    {
        if (FindNamed(Strip, "AddButton") is not Button add)
        {
            return;
        }
        var size = Math.Min(24, WindowMetrics.Current.TabRow);
        add.Width = add.Height = size;
        add.IsTabStop = false;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(add, "New tab");
        ToolTipService.SetToolTip(add, KeysOf?.Invoke("tab.new") is { } keys ? $"New tab ({keys})" : "New tab");
    }

    private static DependencyObject? FindNamed(DependencyObject parent, string name)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { Name: var childName } && childName == name)
            {
                return child;
            }
            if (FindNamed(child, name) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>Makes the row show the strip as it is now: the tabs, their titles, locks and close buttons, and the one in front.</summary>
    public void Refresh()
    {
        if (_strip is not { } strip)
        {
            return;
        }
        _updating = true;
        try
        {
            if (!_shown.SequenceEqual(strip.Tabs))
            {
                Strip.TabItems.Clear();
                _shown.Clear();
                foreach (var tab in strip.Tabs)
                {
                    _shown.Add(tab);
                    Strip.TabItems.Add(NewItem(tab));
                }
            }
            for (var i = 0; i < strip.Count; i++)
            {
                Update((TabViewItem)Strip.TabItems[i], strip.Tabs[i], i);
            }
            if (Strip.SelectedIndex != strip.ActiveIndex)
            {
                Strip.SelectedIndex = strip.ActiveIndex;
            }
        }
        finally
        {
            _updating = false;
        }
        PositionAccent();
    }

    private TabViewItem NewItem(PaneTab tab)
    {
        // The row never takes the keyboard: a click selects the tab, and the pane keeps its keys.
        var item = new TabViewItem { IsTabStop = false, AllowFocusOnInteraction = false, Tag = tab };
        SizeItem(item);
        item.SizeChanged += (_, _) => PositionAccent();
        // A middle click closes any tab, the one in front or not.
        item.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(item).Properties.IsMiddleButtonPressed && Strip.TabItems.IndexOf(item) is >= 0 and var index)
            {
                e.Handled = true;
                Run("tab.close", index);
            }
        };
        item.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            ShowMenu(item, e.TryGetPosition(item, out var at) ? at : new Point(0, item.ActualHeight));
        };
        return item;
    }

    private void Update(TabViewItem item, PaneTab tab, int index)
    {
        var title = tab.Title.Length > 0 ? tab.Title : tab.Path;
        if (!Equals(item.Header, title))
        {
            item.Header = title;
        }
        var glyph = tab.IsTool ? "\uE8A5" : tab.Locked ? "\uE72E" : "\uE8B7";
        if (item.IconSource is not FontIconSource icon || icon.Glyph != glyph)
        {
            item.IconSource = new FontIconSource { Glyph = glyph, FontSize = 14 };
        }
        // The tab in front shows its ×; the others close with a middle click or their menu.
        var front = index == _strip?.ActiveIndex;
        item.IsClosable = front && (_strip?.CanClose(index) ?? false);
        item.FontWeight = front ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        ToolTipService.SetToolTip(item, tab.IsTool ? $"{tab.ToolName}: {tab.Path}" : tab.Locked ? $"{tab.Path} (locked)" : tab.Path);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, tab.Locked ? $"{title}, locked" : title);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PositionAccent();
        if (_updating || _strip is null)
        {
            return;
        }
        // A click on a tab: the window decides, and the row follows the strip.
        var index = Strip.SelectedIndex;
        if (index >= 0 && index != _strip.ActiveIndex)
        {
            Run("tab.select", index);
        }
    }

    private void OnCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) =>
        Run("tab.close", Strip.TabItems.IndexOf(args.Tab));

    private void Run(string command, int tab) =>
        _ = RunCommand?.Invoke(command, CommandArgs.Object(("pane", PaneIndex), ("tab", tab)), "button");

    private void ShowMenu(TabViewItem item, Point at)
    {
        var index = Strip.TabItems.IndexOf(item);
        if (_strip is null || index < 0 || index >= _strip.Count)
        {
            return;
        }
        var tab = _strip.Tabs[index];
        var menu = new MenuFlyout();
        if (!tab.IsTool)
        {
            menu.Items.Add(MenuItem(tab.Locked ? "Unlock tab" : "Lock tab", tab.Locked ? "\uE785" : "\uE72E", "tab.toggleLock", index));
            menu.Items.Add(MenuItem("Move to other pane", "\uE8AB", "tab.moveToOtherPane", index, enabled: CanMoveToOtherPane && _strip.CanClose(index)));
        }
        menu.Items.Add(MenuItem("Close tab", "\uE711", "tab.close", index, enabled: _strip.CanClose(index)));
        menu.ShowAt(item, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = at });
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

    // The 2 px bar lies over the tab in front: the accent in the active pane, white at 30 % in the other.
    private void PositionAccent()
    {
        if (_strip is null || Visibility != Visibility.Visible || Strip.SelectedIndex < 0
            || Strip.ContainerFromIndex(Strip.SelectedIndex) is not FrameworkElement item || item.ActualWidth <= 0)
        {
            AccentLine.Visibility = Visibility.Collapsed;
            return;
        }
        var bounds = item.TransformToVisual(Root).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
        AccentLine.Margin = new Thickness(bounds.X, bounds.Y, 0, 0);
        AccentLine.Width = bounds.Width;
        AccentLine.Visibility = Visibility.Visible;
    }
}
