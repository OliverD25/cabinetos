using CabinetOS.Core.Protocol;
using CabinetOS.Core.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace CabinetOS.Views;

/// <summary>
/// The right-click menu of a file pane (Phase 18): WinUI's own <see cref="CommandBarFlyout"/>, as
/// Windows 11's Explorer shows it (Article 3). The icon row is its primary commands, icon only with
/// the title and keys as a tooltip; the list is its secondary commands, with the key text of the
/// keymap. The control places itself and flips near the screen's edges; arrows move, Enter runs,
/// Esc and a click outside close it. <see cref="ContextMenuModel"/> decides the entries; this class
/// only draws them. An entry runs after the menu has closed and the pane has its focus back, so a
/// command that takes the focus (Rename's text box) keeps it.
/// </summary>
internal sealed class ContextMenuFlyout
{
    // Building and templating a CommandBarFlyout costs more than a frame (about 30 ms, then a 60 ms frame, measured
    // 2026-09-30), so each menu shape is built once and kept: a row's menu and the empty space's menu, and a few more
    // when the file adds filters. The same shape again only has its buttons' enabled state set (Article 1).
    private const int KeptShapes = 6;

    private readonly Dictionary<string, Built> _built = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recent = new();

    // The menu in front: shown, or waiting for the one before it to be gone.
    private Built? _front;

    // The flyout WinUI has: asked to show, its Closed not come yet. WinUI drops a ShowAt on a flyout that is still
    // closing, and holds another flyout's back until then, so a new menu waits here for that Closed (_pending).
    private CommandBarFlyout? _shown;
    private (FrameworkElement Target, Point At)? _pending;

    // The flyout WinUI says is open (its Opened came, its Closed not yet): a ShowAt WinUI dropped never gets here.
    private CommandBarFlyout? _onScreen;
    private ContextMenuView? _view;
    private ContextMenuEntry? _chosen;

    /// <summary>A flyout built for one shape, with its buttons and the entries they stand for.</summary>
    private sealed record Built(CommandBarFlyout Flyout, List<(AppBarButton Button, bool Quick, int Index)> Buttons);

    /// <summary>Runs a chosen entry, once the menu is closed.</summary>
    public Action<ContextMenuEntry>? Run { get; set; }

    /// <summary>Raised when the menu closed, chosen or not, before the chosen entry runs.</summary>
    public event Action? Closed;

    /// <summary>Raised when WinUI has put the menu on screen (its <c>Opened</c>), not only been asked to.</summary>
    public event Action? Opened;

    /// <summary>Whether WinUI has the menu on screen now: for the snapshot aid's log.</summary>
    public bool IsOnScreen => _onScreen is not null;

    /// <summary>Whether the menu is on screen.</summary>
    public bool IsOpen => _front is not null;

    /// <summary>
    /// Where the menu's buttons were (the window's coordinates) when an entry was chosen, so "Edit
    /// Menu…" can put its edit mode in the same place; null when WinUI had not laid them out.
    /// </summary>
    public Rect? ChosenBounds { get; private set; }

    /// <summary>The list's titles ("|" between them) while open, else empty: for the snapshot aid's log.</summary>
    public string DescribeItems() => IsOpen ? _view!.DescribeItems() : "";

    /// <summary>The icon row's titles while open, else empty.</summary>
    public string DescribeQuickActions() => IsOpen ? _view!.DescribeQuickActions() : "";

    /// <summary>
    /// Shows <paramref name="view"/> at <paramref name="at"/> (the window's coordinates) over
    /// <paramref name="target"/>, the pane.
    /// </summary>
    public void Show(FrameworkElement target, Point at, ContextMenuView view)
    {
        _shown?.Hide();
        _pending = null;
        var shape = Shape(view);
        if (!_built.TryGetValue(shape, out var built))
        {
            built = Build(view);
            _built[shape] = built;
            if (_recent.Count >= KeptShapes && _recent.Last is { } oldest)
            {
                _built.Remove(oldest.Value);
                _recent.RemoveLast();
            }
        }
        else
        {
            _recent.Remove(shape);
        }
        _recent.AddFirst(shape);
        foreach (var (button, quick, index) in built.Buttons)
        {
            button.IsEnabled = (quick ? view.QuickActions[index] : view.Items[index]).IsEnabled;
        }
        _front = built;
        _view = view;
        _chosen = null;
        ChosenBounds = null;
        var origin = target.TransformToVisual(null).TransformPoint(new Point(0, 0));
        var position = new Point(at.X - origin.X, at.Y - origin.Y);
        if (_shown is null)
        {
            ShowNow(built, target, position);
        }
        else
        {
            _pending = (target, position);
        }
    }

    private void ShowNow(Built built, FrameworkElement target, Point position)
    {
        _shown = built.Flyout;
        built.Flyout.ShowAt(target, new FlyoutShowOptions { Position = position, ShowMode = FlyoutShowMode.Standard });
    }

    // Everything a button shows but its enabled state; a click reads its entry from the view in front.
    private static string Shape(ContextMenuView view) => string.Join('\n', view.QuickActions.Concat(view.Items)
        .Select(e => $"{e.Kind}\t{e.CommandId}\t{e.Title}\t{e.Glyph}\t{e.Keys}\t{e.Badge}\t{e.Tooltip}"));

    private Built Build(ContextMenuView view)
    {
        var flyout = new CommandBarFlyout { AlwaysExpanded = true };
        flyout.Opened += (_, _) =>
        {
            if (ReferenceEquals(flyout, _shown))
            {
                _onScreen = flyout;
                Opened?.Invoke();
            }
        };
        flyout.Closed += (_, _) => OnClosed(flyout);
        var buttons = new List<(AppBarButton Button, bool Quick, int Index)>();
        for (var i = 0; i < view.QuickActions.Count; i++)
        {
            var button = QuickButton(view.QuickActions[i], i);
            flyout.PrimaryCommands.Add(button);
            buttons.Add((button, true, i));
        }
        for (var i = 0; i < view.Items.Count; i++)
        {
            var entry = view.Items[i];
            switch (entry.Kind)
            {
                case ContextMenuEntryKind.Separator:
                    flyout.SecondaryCommands.Add(new AppBarSeparator());
                    break;
                case ContextMenuEntryKind.Header:
                    flyout.SecondaryCommands.Add(Header(entry.Title));
                    break;
                default:
                    var button = ItemButton(entry, i);
                    flyout.SecondaryCommands.Add(button);
                    buttons.Add((button, false, i));
                    break;
            }
        }
        return new Built(flyout, buttons);
    }

    /// <summary>Closes the menu without running anything.</summary>
    public void Close()
    {
        if (_pending is not null)
        {
            // Never on screen: nothing to hide.
            _pending = null;
            Finish();
        }
        else
        {
            _shown?.Hide();
        }
    }

    /// <summary>The snapshot aid's click: runs the enabled entry titled <paramref name="title"/>, as a click does.</summary>
    public bool Click(string title)
    {
        if (_view is null || !IsOpen)
        {
            return false;
        }
        var entry = _view.QuickActions.Concat(_view.Items)
            .FirstOrDefault(e => e.CommandId is not null && e.IsEnabled && string.Equals(e.Title, title, StringComparison.Ordinal));
        if (entry is null)
        {
            return false;
        }
        Choose(entry);
        return true;
    }

    private void Choose(ContextMenuEntry entry)
    {
        _chosen = entry;
        ChosenBounds = Bounds();
        Close();
    }

    // The union of the shown buttons: the list's rows span the menu's width, the icon row starts at its top.
    private Rect? Bounds()
    {
        if (_front is not { } built)
        {
            return null;
        }
        Rect? union = null;
        foreach (var (button, _, _) in built.Buttons)
        {
            if (button.ActualWidth <= 0 || button.XamlRoot is null)
            {
                continue;
            }
            var bounds = button.TransformToVisual(null).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            union = union is not { } known ? bounds : new Rect(
                new Point(Math.Min(known.Left, bounds.Left), Math.Min(known.Top, bounds.Top)),
                new Point(Math.Max(known.Right, bounds.Right), Math.Max(known.Bottom, bounds.Bottom)));
        }
        return union;
    }

    private void OnClosed(CommandBarFlyout flyout)
    {
        if (ReferenceEquals(flyout, _onScreen))
        {
            _onScreen = null;
        }
        if (!ReferenceEquals(flyout, _shown))
        {
            return;
        }
        _shown = null;
        if (_pending is { } pending && _front is { } front)
        {
            // A newer menu replaced this one while it was closing: it goes on screen now.
            _pending = null;
            ShowNow(front, pending.Target, pending.At);
        }
        else if (_front is { } closed && ReferenceEquals(closed.Flyout, flyout))
        {
            Finish();
        }
    }

    private void Finish()
    {
        _front = null;
        var chosen = _chosen;
        _chosen = null;
        Closed?.Invoke();
        if (chosen is not null)
        {
            Run?.Invoke(chosen);
        }
    }

    private AppBarButton QuickButton(ContextMenuEntry entry, int index)
    {
        var button = new AppBarButton
        {
            Label = entry.Title,
            Icon = Icon(entry),
            IsEnabled = entry.IsEnabled,
        };
        ToolTipService.SetToolTip(button, entry.Tooltip ?? entry.Title);
        AutomationProperties.SetName(button, entry.Title);
        button.Click += (_, _) =>
        {
            if (_view is { } view)
            {
                Choose(view.QuickActions[index]);
            }
        };
        return button;
    }

    private AppBarButton ItemButton(ContextMenuEntry entry, int index)
    {
        // A plugin's name follows its command's title: the flyout's rows have no place for a badge.
        var label = entry.Badge is { } badge ? $"{entry.Title} ({badge})" : entry.Title;
        var button = new AppBarButton
        {
            Label = label,
            IsEnabled = entry.IsEnabled,
        };
        if (Icon(entry) is { } icon)
        {
            button.Icon = icon;
        }
        if (!string.IsNullOrEmpty(entry.Keys))
        {
            button.KeyboardAcceleratorTextOverride = entry.Keys;
        }
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) =>
        {
            if (_view is { } view)
            {
                Choose(view.Items[index]);
            }
        };
        return button;
    }

    private static IconElement? Icon(ContextMenuEntry entry)
    {
        if (entry.Glyph is { } glyph)
        {
            return new FontIcon { Glyph = glyph };
        }
        // Plugins bring no icon yet: their square in the plugin's stable color, as the old menu drew it.
        if (entry.Badge is { } badge)
        {
            return new PathIcon
            {
                Data = new RectangleGeometry { Rect = new Rect(0, 0, 14, 14) },
                Foreground = new SolidColorBrush(FileContextMenu.PluginColor(badge)),
            };
        }
        return null;
    }

    private static AppBarElementContainer Header(string title) => new()
    {
        IsTabStop = false,
        Content = new TextBlock
        {
            Text = title.ToUpperInvariant(),
            Margin = new Thickness(12, 6, 12, 2),
            FontSize = 10,
            Style = (Style)ThemeResources.Get("CbSectionLabelStyle")!,
        },
    };
}

/// <summary>
/// Windows' own context menu (Shift+right-click, <c>menu.showShell</c>; Phase 18) as the core built
/// it (<c>shell_menu</c>): a <see cref="MenuFlyout"/>, whose submenus fit the shell's "Send to" and
/// "Give access to". A click runs the item in the core (<c>shell_menu_invoke</c>) once the menu has
/// closed; the window waits for nothing.
/// </summary>
internal sealed class ShellMenuFlyout
{
    // One flyout and its items, kept and filled again at each opening: new menu items cost a template each, and
    // Windows' menu has 20 to 40 of them (Article 1).
    private readonly List<MenuFlyoutItem> _items = [];
    private readonly List<MenuFlyoutSeparator> _separators = [];
    private readonly List<MenuFlyoutSubItem> _submenus = [];
    private MenuFlyout? _flyout;

    // The menu in front, shown or waiting for the flyout to finish closing (as ContextMenuFlyout does, for the same
    // reason: WinUI drops a ShowAt on a flyout that is still closing).
    private ShellMenuReply? _menu;
    private bool _shown;
    private (FrameworkElement Target, Point At)? _pending;
    private uint? _chosen;
    private (int Items, int Separators, int Submenus) _used;

    /// <summary>Runs a chosen item: (menu, item).</summary>
    public Action<ulong, uint>? Invoke { get; set; }

    /// <summary>Raised when the menu closed, chosen or not, before the chosen item runs.</summary>
    public event Action? Closed;

    /// <summary>Whether the menu is on screen.</summary>
    public bool IsOpen => _menu is not null;

    /// <summary>The top level's texts ("|" between them, a submenu as "Send to›") while open, else empty.</summary>
    public string Describe() => IsOpen
        ? string.Join("|", _menu!.Items.Where(i => !i.Separator).Select(i => i.Items is { Count: > 0 } ? i.Text + "›" : i.Text))
        : "";

    /// <summary>Shows <paramref name="menu"/> at <paramref name="at"/> (the window's coordinates) over <paramref name="target"/>.</summary>
    public void Show(FrameworkElement target, Point at, ShellMenuReply menu)
    {
        if (_shown)
        {
            _flyout!.Hide();
        }
        _pending = null;
        _menu = menu;
        _chosen = null;
        var origin = target.TransformToVisual(null).TransformPoint(new Point(0, 0));
        var position = new Point(at.X - origin.X, at.Y - origin.Y);
        if (_shown)
        {
            _pending = (target, position);
        }
        else
        {
            ShowNow(target, position);
        }
    }

    private void ShowNow(FrameworkElement target, Point position)
    {
        if (_flyout is null)
        {
            _flyout = new MenuFlyout();
            _flyout.Closed += (_, _) => OnClosed();
        }
        var flyout = _flyout;
        // Each kept item leaves its parent before it may be added again.
        foreach (var submenu in _submenus)
        {
            submenu.Items.Clear();
        }
        flyout.Items.Clear();
        _used = (0, 0, 0);
        foreach (var item in _menu!.Items)
        {
            flyout.Items.Add(Element(item));
        }
        _shown = true;
        flyout.ShowAt(target, new FlyoutShowOptions { Position = position, ShowMode = FlyoutShowMode.Standard });
    }

    /// <summary>Closes the menu without running anything; the core releases it after 30 s.</summary>
    public void Close()
    {
        if (_pending is not null)
        {
            _pending = null;
            Finish();
        }
        else if (_shown)
        {
            _flyout!.Hide();
        }
    }

    /// <summary>The snapshot aid's click: runs the item with <paramref name="text"/>, in a submenu too.</summary>
    public bool Click(string text)
    {
        if (_menu is null || !IsOpen)
        {
            return false;
        }
        var item = _menu.Items.Concat(_menu.Items.SelectMany(i => i.Items ?? []))
            .FirstOrDefault(i => i.Id != 0 && string.Equals(i.Text, text, StringComparison.Ordinal));
        if (item is null)
        {
            return false;
        }
        Choose(item.Id);
        return true;
    }

    private MenuFlyoutItemBase Element(ShellMenuItemInfo item)
    {
        if (item.Separator)
        {
            return Next(_separators, ref _used.Separators, () => new MenuFlyoutSeparator());
        }
        if (item.Items is { Count: > 0 } items)
        {
            var sub = Next(_submenus, ref _used.Submenus, () => new MenuFlyoutSubItem());
            sub.Text = item.Text;
            foreach (var child in items)
            {
                sub.Items.Add(Element(child));
            }
            return sub;
        }
        var button = Next(_items, ref _used.Items, () =>
        {
            var created = new MenuFlyoutItem();
            created.Click += (sender, _) => Choose((uint)((MenuFlyoutItem)sender).Tag);
            return created;
        });
        button.Text = item.Text;
        button.Tag = item.Id;
        return button;
    }

    // The next kept element of a kind, or a new one kept from now on.
    private static T Next<T>(List<T> kept, ref int used, Func<T> create)
    {
        if (used == kept.Count)
        {
            kept.Add(create());
        }
        return kept[used++];
    }

    private void Choose(uint id)
    {
        _chosen = id;
        Close();
    }

    private void OnClosed()
    {
        _shown = false;
        if (_pending is { } pending)
        {
            _pending = null;
            ShowNow(pending.Target, pending.At);
        }
        else if (_menu is not null)
        {
            Finish();
        }
    }

    private void Finish()
    {
        var menu = _menu;
        _menu = null;
        var chosen = _chosen;
        _chosen = null;
        Closed?.Invoke();
        if (chosen is { } id && menu is not null)
        {
            Invoke?.Invoke(menu.MenuId, id);
        }
    }
}
