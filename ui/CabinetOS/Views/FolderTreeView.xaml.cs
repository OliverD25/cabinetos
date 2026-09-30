using System.Collections.Specialized;
using CabinetOS.Core.Sidebar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    /// closed folder's child).
    /// </summary>
    public bool ScrollTo(FolderNode node)
    {
        var index = _tree?.Rows.IndexOf(node) ?? -1;
        if (index < 0)
        {
            return false;
        }
        TreeList.GetOrCreateElement(index).StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
        return true;
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
