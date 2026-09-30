using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace CabinetOS.Views;

/// <summary>What a context menu row is.</summary>
public enum MenuEntryKind
{
    /// <summary>A command.</summary>
    Item,

    /// <summary>A thin line between groups.</summary>
    Separator,

    /// <summary>A group's label, such as "FROM PLUGINS".</summary>
    Header,
}

/// <summary>One row of the context menu, or one icon of its strip: every one runs a command.</summary>
public sealed record MenuEntry(
    MenuEntryKind Kind,
    string Title = "",
    string? Glyph = null,
    string? CommandId = null,
    JsonElement? Args = null,
    string? Keys = null,
    string? Badge = null,
    bool IsEnabled = true,
    string? Tooltip = null)
{
    /// <summary>A separator line.</summary>
    public static MenuEntry Separator { get; } = new(MenuEntryKind.Separator);

    /// <summary>A group label.</summary>
    public static MenuEntry Header(string title) => new(MenuEntryKind.Header, title);
}

/// <summary>
/// The file context menu (design view D): Acrylic, 260 px, at the pointer and
/// kept inside the window, with the icon strip (Cut, Copy, Paste, Rename,
/// Delete) and the rows. A click outside or Esc closes it; every entry runs a
/// command through the router.
/// </summary>
public sealed partial class FileContextMenu : UserControl
{
    // Plugins bring no icon yet: a stable color from the design's palette marks each one.
    private static readonly Color[] PluginColors =
    [
        Color.FromArgb(0xFF, 0xF0, 0x90, 0x6C),
        Color.FromArgb(0xFF, 0xF2, 0xC0, 0x63),
        Color.FromArgb(0xFF, 0xC9, 0xB6, 0xFF),
        Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F),
        Color.FromArgb(0xFF, 0x60, 0xCD, 0xFF),
        Color.FromArgb(0xFF, 0xE0, 0x70, 0x5E),
    ];

    private readonly Storyboard _entrance;
    private double? _rowHeight;
    private string _described = "";

    /// <summary>Creates the menu, hidden.</summary>
    public FileContextMenu()
    {
        InitializeComponent();
        _entrance = (Storyboard)Resources["Entrance"];
        Scrim.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Scrim))
            {
                e.Handled = true;
                Close();
            }
        };
    }

    /// <summary>Raised when the menu closes, so the window can give the pane its focus back.</summary>
    public event Action? Closed;

    /// <summary>Runs a command through the window's router: (command, arguments, trigger).</summary>
    public Func<string, JsonElement?, string, Task>? RunCommand { get; set; }

    /// <summary>Whether the menu is on screen.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>The rows' titles as shown ("|" between them), for the snapshot aid's log; empty while closed.</summary>
    public string Describe() => IsOpen ? _described : "";

    /// <summary>
    /// Shows the menu at <paramref name="at"/> (window coordinates), kept inside
    /// the window. From the keyboard, the first row shows the focus rectangle.
    /// The top row's dropdowns pass their own <paramref name="rowHeight"/>
    /// (the theme's dropdownRowHeight); the context menu's is menuRowHeight.
    /// </summary>
    public void Show(Point at, IReadOnlyList<MenuEntry> strip, IReadOnlyList<MenuEntry> items, bool fromKeyboard = false, double? rowHeight = null)
    {
        _rowHeight = rowHeight;
        _described = string.Join("|", items.Where(i => i.Kind == MenuEntryKind.Item).Select(i => i.Title));
        Build(strip, items);
        Visibility = Visibility.Visible;
        Panel.Measure(new Size(Panel.Width, double.PositiveInfinity));
        var window = XamlRoot?.Size ?? new Size(ActualWidth, ActualHeight);
        // The design clamps x to the window width − 270; y so the whole menu stays visible.
        var x = Math.Max(4, Math.Min(at.X, window.Width - 270));
        var y = Math.Max(4, Math.Min(at.Y, window.Height - Panel.DesiredSize.Height - 8));
        Canvas.SetLeft(Panel, x);
        Canvas.SetTop(Panel, y);
        _entrance.Begin();
        var first = Items.Children.OfType<Button>().FirstOrDefault(b => b.IsEnabled)
            ?? Strip.Children.OfType<Button>().FirstOrDefault(b => b.IsEnabled);
        first?.Focus(fromKeyboard ? FocusState.Keyboard : FocusState.Programmatic);
    }

    /// <summary>Closes the menu.</summary>
    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }
        // The window gives the pane its focus first: a collapsing menu with the
        // focus would hand it to whatever comes next, maybe the other pane.
        Closed?.Invoke();
        Visibility = Visibility.Collapsed;
        OpenToolTips.Close(XamlRoot);
    }

    private void Build(IReadOnlyList<MenuEntry> strip, IReadOnlyList<MenuEntry> items)
    {
        Strip.Children.Clear();
        Strip.ColumnDefinitions.Clear();
        StripBorder.Visibility = strip.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < strip.Count; i++)
        {
            Strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var entry = strip[i];
            var button = new Button
            {
                Width = 36,
                Height = WindowMetrics.Current.MenuRowHeight,
                CornerRadius = WindowMetrics.Corners(WindowMetrics.Current.RadiusControl),
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Center,
                Style = (Style)ThemeResources.Get("CbSubtleButtonStyle")!,
                Content = new FontIcon { Glyph = entry.Glyph ?? "", FontSize = 14 },
                IsEnabled = entry.IsEnabled,
            };
            ToolTipService.SetToolTip(button, entry.Tooltip ?? entry.Title);
            AutomationProperties.SetName(button, entry.Title);
            Grid.SetColumn(button, i);
            button.Click += (_, _) => Run(entry);
            Strip.Children.Add(button);
        }

        Items.Children.Clear();
        foreach (var entry in items)
        {
            Items.Children.Add(entry.Kind switch
            {
                MenuEntryKind.Separator => new Rectangle
                {
                    Height = 1,
                    Margin = new Thickness(8, 4, 8, 4),
                    Fill = ThemeResources.Brush("CbDividerBrush"),
                },
                MenuEntryKind.Header => new TextBlock
                {
                    Text = entry.Title.ToUpperInvariant(),
                    Padding = new Thickness(10, 6, 10, 2),
                    Style = (Style)ThemeResources.Get("CbSectionLabelStyle")!,
                    FontSize = 10,
                },
                _ => ItemButton(entry),
            });
        }
    }

    private Button ItemButton(MenuEntry entry)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement icon = entry.Badge is null
            ? new FontIcon { Glyph = entry.Glyph ?? "", FontSize = 14, Width = 14 }
            : new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(PluginColors[StableHash(entry.Badge) % (uint)PluginColors.Length]),
            };
        row.Children.Add(icon);

        var title = new TextBlock { Text = entry.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(title, 1);
        row.Children.Add(title);

        if (entry.Badge is not null)
        {
            var badge = new Border
            {
                Padding = new Thickness(6, 1, 6, 1),
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center,
                Background = ThemeResources.Brush("CbPluginBadgeFillBrush"),
                Child = new TextBlock { Text = entry.Badge, FontSize = 10, Foreground = ThemeResources.Brush("CbAccentBrush") },
            };
            Grid.SetColumn(badge, 2);
            row.Children.Add(badge);
        }
        if (!string.IsNullOrEmpty(entry.Keys))
        {
            var keys = new TextBlock
            {
                Text = entry.Keys,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = (FontFamily)ThemeResources.Get("CbMonoFont")!,
                Foreground = ThemeResources.Brush("CbHintTextBrush"),
            };
            Grid.SetColumn(keys, 3);
            row.Children.Add(keys);
        }

        var m = WindowMetrics.Current;
        var button = new Button
        {
            Content = row,
            IsEnabled = entry.IsEnabled,
            Style = (Style)Resources["MenuItemStyle"],
            Height = _rowHeight ?? m.MenuRowHeight,
            CornerRadius = WindowMetrics.Corners(m.RadiusControl),
            FontSize = m.FontSize,
        };
        AutomationProperties.SetName(button, entry.Title);
        if (entry.Tooltip is not null)
        {
            ToolTipService.SetToolTip(button, entry.Tooltip);
        }
        button.Click += (_, _) => Run(entry);
        return button;
    }

    // string.GetHashCode changes with every start; the plugin's color must not.
    private static uint StableHash(string text)
    {
        var hash = 17u;
        foreach (var c in text)
        {
            hash = unchecked((hash * 31) + c);
        }
        return hash;
    }

    private void Run(MenuEntry entry)
    {
        Close();
        if (entry.CommandId is { } command)
        {
            _ = RunCommand?.Invoke(command, entry.Args, "menu");
        }
    }
}
