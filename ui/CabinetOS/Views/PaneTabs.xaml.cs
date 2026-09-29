using System.Text.Json;
using CabinetOS.Core.Tabs;
using CabinetOS.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace CabinetOS.Views;

/// <summary>
/// A pane's tab row (Phase 12; docs/ui.md, "Tabs"). It shows the pane's
/// <see cref="TabStrip"/> from the second tab on and reports what the user
/// does as commands (<c>tab.select</c>, <c>tab.close</c>, <c>tab.toggleLock</c>,
/// <c>tab.moveToOtherPane</c>), each with the pane and the tab it concerns:
/// the window decides and changes the strip, and the row follows its
/// <see cref="TabStrip.Changed"/>. The keyboard never rests on the row.
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
        Strip.SizeChanged += (_, _) => PositionAccent();
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
    /// Whether the row belongs to the active pane: only its front tab has the
    /// 2 px accent line, as the active pane's header has its bright title.
    /// </summary>
    public bool ShowsAccent
    {
        get => _accent;
        set
        {
            if (_accent != value)
            {
                _accent = value;
                PositionAccent();
            }
        }
    }

    /// <summary>The tabs as the row shows them, for the snapshot aid's log: titles, the one in front marked with *.</summary>
    public string Describe() => _strip is null
        ? ""
        : string.Join(" | ", _strip.Tabs.Select((tab, i) => $"{(i == _strip.ActiveIndex ? "*" : "")}{tab.Title}{(tab.Locked ? " (locked)" : "")}{(tab.IsTool ? " (tool)" : "")}"));

    /// <summary>The row's height now, 0 while it is hidden (the snapshot aid's <c>layout:</c> step).</summary>
    public double RowHeight => Visibility == Visibility.Visible ? ActualHeight : 0;

    /// <summary>Lays the row out with the window's sizes now: the theme's <c>tabRow</c> as its height, the tab text at the base size.</summary>
    public void ApplyMetrics()
    {
        var m = WindowMetrics.Current;
        Strip.Height = m.TabRow;
        Strip.FontSize = m.FontSize;
        foreach (var item in Strip.TabItems.OfType<TabViewItem>())
        {
            SizeItem(item);
        }
        PositionAccent();
    }

    private static void SizeItem(TabViewItem item)
    {
        var m = WindowMetrics.Current;
        item.Height = item.MinHeight = m.TabRow;
        item.FontSize = m.TabFontSize;
        // The same top corners as the workspace tab in the title bar (the theme's tabRadius).
        item.CornerRadius = WindowMetrics.TopCorners(m.TabRadius);
    }

    /// <summary>Makes the row show the strip as it is now: the tabs, their titles, locks and close buttons, and the one in front.</summary>
    public void Refresh()
    {
        if (_strip is not { } strip)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        _updating = true;
        try
        {
            Visibility = strip.ShowsRow ? Visibility.Visible : Visibility.Collapsed;
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
        item.IsClosable = _strip?.CanClose(index) ?? false;
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

    // The accent line lies over the tab in front, when the row belongs to the active pane.
    private void PositionAccent()
    {
        if (!_accent || _strip is null || Visibility != Visibility.Visible || Strip.SelectedIndex < 0
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
