using System.Collections.Specialized;
using CabinetOS.Core.Sidebar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace CabinetOS.Views;

/// <summary>
/// The Explorer's folder tree (rail layout): the rows of a
/// <see cref="FolderTreeModel"/>, a header with the lock and locate buttons,
/// and the keys. Clicking a folder or pressing Enter on it goes there in the
/// active pane (<see cref="Navigate"/>); the chevron, Right and Left open and
/// close a row. The model does the work (Article 1: nothing here reads a folder).
/// </summary>
public sealed partial class FolderTreeView : UserControl
{
    private const double IndentStep = 12;

    private FolderTreeModel? _tree;

    /// <summary>Creates the tree; <see cref="Tree"/> fills it.</summary>
    public FolderTreeView()
    {
        InitializeComponent();
        TreeList.ElementPrepared += (_, e) => SizeRow(e.Element);
        LocateButton.Click += (_, _) => LocateRequested?.Invoke();
        LockButton.Click += (_, _) => LockToggled?.Invoke();
        FollowButton.Click += (_, _) => FollowToggled?.Invoke();
        PointerPressed += (_, _) => Focus(FocusState.Pointer);
        GotFocus += (_, _) => OnGotFocus();
        LostFocus += (_, _) => OnLostFocus();
        KeyDown += OnKeyDown;
        ApplyMetrics();
    }

    /// <summary>The user picked a folder (a click, or Enter on the cursor row): the window goes there in the active pane.</summary>
    public event Action<string>? Navigate;

    /// <summary>The lock button was pressed.</summary>
    public event Action? LockToggled;

    /// <summary>The locate button was pressed.</summary>
    public event Action? LocateRequested;

    /// <summary>The pin was pressed: the window runs <c>sidebar.toggleFollow</c>, which writes <c>ui.sidebarAutoReveal</c>.</summary>
    public event Action? FollowToggled;

    /// <summary>Esc: the keyboard goes back to the active pane.</summary>
    public event Action? EscapePressed;

    /// <summary>The tree's rows.</summary>
    public FolderTreeModel? Tree
    {
        get => _tree;
        set
        {
            _tree = value;
            TreeList.ItemsSource = value?.Rows;
        }
    }

    /// <summary>Whether the tree is locked (it does not follow the active pane): the button shows it.</summary>
    public bool IsLocked
    {
        get => _locked;
        set
        {
            _locked = value;
            LockIcon.Glyph = value ? "" : "";
            LockButton.Foreground = ThemeResources.Brush(value ? "CbAccentBrush" : "CbTextSecondaryBrush");
            ToolTipService.SetToolTip(LockButton, value ? "Unlock the tree: it follows the active folder again" : "Lock the tree: it stops following the active folder");
        }
    }

    private bool _locked;

    /// <summary>Whether the tree follows the active pane (<c>ui.sidebarAutoReveal</c>): the pin shows it, and the file moves it.</summary>
    public bool FollowsActivePane
    {
        get => _follows;
        set
        {
            _follows = value;
            FollowIcon.Glyph = value ? "\uE842" : "\uE718";
            FollowButton.Foreground = ThemeResources.Brush(value ? "CbAccentBrush" : "CbTextSecondaryBrush");
            ToolTipService.SetToolTip(FollowButton, value
                ? "The tree follows the active pane (click to keep it where it is)"
                : "The tree stays where it is (click to follow the active pane)");
        }
    }

    private bool _follows = true;

    /// <summary>Lays the header and the rows out with the window's sizes now (docs/ui.md, "Metrics and chrome").</summary>
    public void ApplyMetrics()
    {
        var m = WindowMetrics.Current;
        FoldersLabel.FontSize = m.SidebarHeaderFontSize;
        FoldersLabel.Margin = new Thickness(m.SidebarHeaderPaddingX, m.SidebarHeaderPaddingTop, m.SidebarHeaderPaddingX, m.SidebarHeaderPaddingBottom);
        TreeLayout.Spacing = Math.Min(2, m.SidebarRowInset / 2);
        for (var i = 0; i < (TreeList.ItemsSourceView?.Count ?? 0); i++)
        {
            if (TreeList.TryGetElement(i) is { } row)
            {
                SizeRow(row);
            }
        }
        TreeList.InvalidateMeasure();
    }

    /// <summary>
    /// Brings the row of <paramref name="node"/> into view and lets go of the
    /// keyboard's cursor there. Returns false when the node has no row (a
    /// closed folder's child). Until the tree has been laid out in its
    /// scrolling window (the window is still starting, or the tree is hidden)
    /// the scroll waits for the layout: asked earlier, the list draws its rows
    /// around the row it was asked for, far from the window, and shows none.
    /// </summary>
    public bool ScrollTo(FolderNode node, Action? scrolled = null)
    {
        var index = _tree?.Rows.IndexOf(node) ?? -1;
        if (index < 0)
        {
            return false;
        }
        if (!IsLaidOut())
        {
            _pendingScroll = node;
            _pendingDone = scrolled;
            LayoutUpdated -= OnLayoutUpdated;
            LayoutUpdated += OnLayoutUpdated;
            return true;
        }
        // The row is placed by a layout pass before it is brought into view (the documented order for a repeater): a row that
        // was only asked for has no place yet, the scroll goes nowhere, and the list draws its rows far from the window.
        var row = TreeList.GetOrCreateElement(index);
        TreeList.UpdateLayout();
        row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
        scrolled?.Invoke();
        return true;
    }

    private FolderNode? _pendingScroll;
    private Action? _pendingDone;

    // The tree is in the visual tree, shown, and its scrolling window has a size and has measured the list.
    private bool IsLaidOut() =>
        IsLoaded && Visibility == Visibility.Visible && TreeList.ActualHeight > 0 && FindScroller() is { ViewportHeight: > 0 };

    private void OnLayoutUpdated(object? sender, object e)
    {
        if (!IsLaidOut())
        {
            return;
        }
        LayoutUpdated -= OnLayoutUpdated;
        if (_pendingScroll is { } node)
        {
            var done = _pendingDone;
            _pendingScroll = null;
            _pendingDone = null;
            ScrollTo(node, done);
        }
    }

    /// <summary>How many rows the list has drawn (realized), wherever they lie.</summary>
    public int RealizedRowCount => VisualTreeHelper.GetChildrenCount(TreeList);

    /// <summary>
    /// How many of the drawn rows lie inside the sidebar's scrolling window: what the user sees. A tree can have every row in
    /// its model and rows drawn far from the window (the list was asked to scroll before it was laid out) and show none.
    /// </summary>
    public int VisibleRowCount()
    {
        if (FindScroller() is not { } scroller)
        {
            return 0;
        }
        var window = new Windows.Foundation.Rect(0, 0, scroller.ActualWidth, scroller.ActualHeight);
        var visible = 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(TreeList); i++)
        {
            if (VisualTreeHelper.GetChild(TreeList, i) is FrameworkElement row && row.ActualHeight > 0)
            {
                var bounds = row.TransformToVisual(scroller).TransformBounds(new Windows.Foundation.Rect(0, 0, row.ActualWidth, row.ActualHeight));
                bounds.Intersect(window);
                if (!bounds.IsEmpty && bounds.Height > 0)
                {
                    visible++;
                }
            }
        }
        return visible;
    }

    // The scrolling window the tree lies in (the sidebar's), once the tree is in the visual tree.
    private ScrollViewer? FindScroller()
    {
        for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer scroller)
            {
                return scroller;
            }
        }
        return null;
    }

    /// <summary>How opaque the name of a drawn row is (1; less for a hidden folder); null when the row is not drawn.</summary>
    public double? NameOpacity(FolderNode node)
    {
        var index = _tree?.Rows.IndexOf(node) ?? -1;
        return index >= 0 && TreeList.TryGetElement(index) is Grid { Children: [_, _, _, Grid { Children: [_, Button { Content: UIElement name }] }] }
            ? name.Opacity
            : null;
    }

    /// <summary>Gives the tree the keyboard; the cursor goes to the current folder's row, or the first one.</summary>
    public bool FocusTree() => Focus(FocusState.Keyboard);

    // A row: its inset for the depth, its height, and the button's corners follow the window's sizes.
    private static void SizeRow(UIElement element)
    {
        if (element is not Grid { Children: [Border current, Border cursor, Microsoft.UI.Xaml.Shapes.Rectangle bar, Grid content] } row)
        {
            return;
        }
        var m = WindowMetrics.Current;
        var height = m.PinnedRowHeight();
        row.Height = height;
        current.CornerRadius = cursor.CornerRadius = WindowMetrics.Corners(m.SidebarRowRadius);
        current.Margin = cursor.Margin = WindowMetrics.Pad(m.SidebarRowInset, 0);
        bar.Width = m.SelectionBarWidth;
        bar.RadiusX = bar.RadiusY = m.SelectionBarWidth / 2;
        bar.Height = Math.Clamp(height - 4, 0, 16);
        bar.Margin = new Thickness(m.SidebarRowInset, 0, 0, 0);
        if (content.Children is [Button chevron, Button button])
        {
            chevron.Height = Math.Min(24, height);
            button.Height = Math.Max(0, height - 2);
            button.CornerRadius = WindowMetrics.Corners(m.SidebarRowRadius);
            button.FontSize = m.FontSize;
        }
    }

    // ----- What the template calls -----

    /// <summary>The left inset of a row of depth <paramref name="depth"/>.</summary>
    public static Thickness Indent(int depth) => new(WindowMetrics.Current.DriveRowPaddingX + (depth * IndentStep), 0, WindowMetrics.Current.SidebarRowInset, 0);

    /// <summary>A row with no sub-folders keeps its chevron's place and shows none.</summary>
    public static double ChevronOpacity(bool hasChildren) => hasChildren ? 1 : 0;

    /// <summary>
    /// The icon and the name of a hidden folder are dim: the tree shows it only because the active pane is in it. The window's
    /// panes draw hidden entries as the others, so this is the only place the look exists.
    /// </summary>
    public static double NameOpacityOf(bool isHidden) => isHidden ? HiddenOpacity : 1;

    private const double HiddenOpacity = 0.55;

    /// <summary>The tooltip: the folder's path, and why it could not be read when it could not.</summary>
    public static string Tip(string path, string? error) => error is null ? path : $"{path}\n{error}";

    // ----- Mouse -----

    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FolderNode node })
        {
            _tree?.SetCursor(node);
            Navigate?.Invoke(node.Path);
        }
    }

    private void OnChevronClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FolderNode node } && _tree is { } tree)
        {
            tree.SetCursor(node);
            _ = tree.ToggleAsync(node);
        }
    }

    // ----- Keyboard -----

    // The cursor is drawn only while the tree has the keyboard: it starts on the current folder's row.
    private void OnGotFocus()
    {
        if (_tree is { Cursor: null } tree)
        {
            tree.SetCursor(tree.Current ?? (tree.Rows.Count > 0 ? tree.Rows[0] : null));
            if (tree.Cursor is { } node)
            {
                ScrollTo(node);
            }
        }
    }

    private void OnLostFocus()
    {
        // A focus that moved to one of this control's own parts (a header button) keeps the cursor.
        if (FocusState == FocusState.Unfocused && !HasFocusInside())
        {
            _tree?.SetCursor(null);
        }
    }

    private bool HasFocusInside() =>
        XamlRoot is { } root && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is DependencyObject focused && IsInside(focused);

    private bool IsInside(DependencyObject element)
    {
        for (var up = element; up is not null; up = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(up))
        {
            if (up == this)
            {
                return true;
            }
        }
        return false;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_tree is not { } tree)
        {
            return;
        }
        var handled = true;
        switch (e.Key)
        {
            case VirtualKey.Down:
                tree.MoveCursor(1);
                break;
            case VirtualKey.Up:
                tree.MoveCursor(-1);
                break;
            case VirtualKey.PageDown:
                tree.MoveCursor(10);
                break;
            case VirtualKey.PageUp:
                tree.MoveCursor(-10);
                break;
            case VirtualKey.Home:
                tree.CursorToStart();
                break;
            case VirtualKey.End:
                tree.CursorToEnd();
                break;
            case VirtualKey.Right:
                _ = tree.CursorRightAsync();
                break;
            case VirtualKey.Left:
                tree.CursorLeft();
                break;
            case VirtualKey.Space when tree.Cursor is { } toggled:
                _ = tree.ToggleAsync(toggled);
                break;
            case VirtualKey.Enter when tree.Cursor is { } chosen:
                Navigate?.Invoke(chosen.Path);
                break;
            case VirtualKey.Escape:
                EscapePressed?.Invoke();
                break;
            default:
                handled = false;
                break;
        }
        if (handled)
        {
            e.Handled = true;
            if (tree.Cursor is { } cursor && e.Key is not (VirtualKey.Escape or VirtualKey.Enter))
            {
                ScrollTo(cursor);
            }
        }
    }
}
