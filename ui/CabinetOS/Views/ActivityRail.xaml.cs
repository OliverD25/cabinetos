using CabinetOS.Core.Sidebar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace CabinetOS.Views;

/// <summary>
/// The activity rail of the rail layout (design README, "Activity rail"): a 44 px
/// column of 36 px buttons with a 3 px accent pill on the one whose view or
/// panel is on show. It only draws what <see cref="RailModel"/> holds and says
/// which button was pressed; the window decides what that does.
/// </summary>
public sealed partial class ActivityRail : UserControl
{
    private const string AccentBrush = "CbAccentBrush";

    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
    private string _order = "";
    private RailModel? _model;
    private Func<RailButton, bool> _isActive = _ => false;

    // One button and the parts that change: the pill, and the badge's two looks.
    private sealed record Item(Button Button, Rectangle Pill, Ellipse Dot, ProgressRing Spinner, RailButton Rail);

    /// <summary>Creates the rail; <see cref="Bind"/> gives it its buttons.</summary>
    public ActivityRail()
    {
        InitializeComponent();
    }

    /// <summary>A button was pressed (by mouse, or by Enter or Space on it).</summary>
    public event Action<RailButton>? Clicked;

    /// <summary>Shift+Up (<c>-1</c>) or Shift+Down (<c>1</c>) on a button: the window moves it.</summary>
    public event Action<string, int>? MoveRequested;

    /// <summary>
    /// Shows <paramref name="model"/>'s buttons; <paramref name="isActive"/>
    /// says which one wears the pill. The rail follows the model's changes.
    /// </summary>
    public void Bind(RailModel model, Func<RailButton, bool> isActive)
    {
        if (_model is not null)
        {
            _model.Changed -= OnModelChanged;
        }
        _model = model;
        _isActive = isActive;
        model.Changed += OnModelChanged;
        Rebuild();
    }

    /// <summary>The pills follow the state again (a view was shown, the marketplace or the terminal opened or closed).</summary>
    public void Refresh()
    {
        foreach (var item in _items.Values)
        {
            Paint(item);
        }
    }

    /// <summary>Gives the keyboard to the button <paramref name="id"/>; false when there is none.</summary>
    public bool FocusButton(string id) => _items.TryGetValue(id, out var item) && item.Button.Focus(FocusState.Keyboard);

    /// <summary>Lays the buttons out with the window's sizes now (docs/ui.md, "Metrics and chrome").</summary>
    public void ApplyMetrics()
    {
        var m = WindowMetrics.Current;
        foreach (var item in _items.Values)
        {
            item.Button.CornerRadius = WindowMetrics.Corners(m.SidebarRowRadius);
            item.Pill.Width = m.SelectionBarWidth;
            item.Pill.RadiusX = item.Pill.RadiusY = m.SelectionBarWidth / 2;
        }
    }

    // A badge only repaints; a new order, or new tools, make the buttons again.
    private void OnModelChanged()
    {
        if (_model is not null && string.Join('|', _model.Order) == _order)
        {
            Refresh();
        }
        else
        {
            Rebuild();
        }
    }

    // The buttons are made again when the order or the tools change; the one that had the keyboard gets it back.
    private void Rebuild()
    {
        if (_model is null)
        {
            return;
        }
        var focused = _items.Values.FirstOrDefault(i => i.Button.FocusState != FocusState.Unfocused)?.Rail.Id;
        _order = string.Join('|', _model.Order);
        Buttons.Children.Clear();
        _items.Clear();
        foreach (var rail in _model.Buttons)
        {
            var item = Create(rail);
            _items[rail.Id] = item;
            Buttons.Children.Add(item.Button);
        }
        ApplyMetrics();
        Refresh();
        if (focused is not null)
        {
            FocusButton(focused);
        }
    }

    private Item Create(RailButton rail)
    {
        var pill = new Rectangle
        {
            Width = 3,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = ThemeResources.Brush(AccentBrush),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        UIElement icon = rail.Glyph.Length > 0
            ? new FontIcon { FontSize = 16, Glyph = rail.Glyph }
            : new TextBlock
            {
                Text = rail.Initials,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                CharacterSpacing = 20,
            };
        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = ThemeResources.Brush(AccentBrush),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        var spinner = new ProgressRing
        {
            Width = 12,
            Height = 12,
            IsActive = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 4, 0),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        var content = new Grid { Width = 36, Height = 36 };
        content.Children.Add(pill);
        content.Children.Add(new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { icon } });
        content.Children.Add(dot);
        content.Children.Add(spinner);
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["CbSubtleButtonStyle"],
            Width = 36,
            Height = 36,
            MinWidth = 36,
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = content,
            Tag = rail,
        };
        ToolTipService.SetToolTip(button, Tooltip(rail));
        AutomationProperties.SetName(button, rail.Title);
        button.Click += (_, _) => Clicked?.Invoke(rail);
        button.KeyDown += OnKeyDown;
        return new Item(button, pill, dot, spinner, rail);
    }

    private void Paint(Item item)
    {
        var active = _isActive(item.Rail);
        item.Pill.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        // The design's white at full strength for the button in use, .55 for the others.
        item.Button.Foreground = ThemeResources.Brush(active ? "CbTextPrimaryBrush" : "CbTextSecondaryBrush");
        var badge = _model?.BadgeOf(item.Rail.Id);
        item.Dot.Visibility = badge == "dot" ? Visibility.Visible : Visibility.Collapsed;
        item.Spinner.Visibility = badge == "spinner" ? Visibility.Visible : Visibility.Collapsed;
        item.Spinner.IsActive = badge == "spinner";
        AutomationProperties.SetName(item.Button, badge switch
        {
            "dot" => $"{item.Rail.Title}, has news",
            "spinner" => $"{item.Rail.Title}, working",
            _ => item.Rail.Title,
        });
    }

    // The keys the design names for the built-in views, in the tooltip like the top row's buttons.
    private static string Tooltip(RailButton rail) => rail.Id switch
    {
        RailModel.Explorer => "Explorer (Ctrl+Shift+E)",
        RailModel.Search => "Search (Ctrl+Shift+F)",
        RailModel.Marketplace => "Marketplace (Ctrl+Shift+X)",
        RailModel.Terminal => "Terminal (Ctrl+`)",
        _ => rail.Title,
    };

    // Up and Down walk the buttons; Shift with them moves the button (the order is ui.rail).
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not Button { Tag: RailButton rail } || e.Key is not (VirtualKey.Up or VirtualKey.Down))
        {
            return;
        }
        e.Handled = true;
        var delta = e.Key == VirtualKey.Up ? -1 : 1;
        if ((Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
        {
            MoveRequested?.Invoke(rail.Id, delta);
            return;
        }
        var ids = Buttons.Children.OfType<Button>().Select(b => ((RailButton)b.Tag).Id).ToList();
        var next = ids.IndexOf(rail.Id) + delta;
        if (next >= 0 && next < ids.Count)
        {
            FocusButton(ids[next]);
        }
    }
}
