using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Presentation;
using CabinetOS.Services;
using CabinetOS.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CabinetOS.Views;

/// <summary>
/// One row of the column view (ADR 0016; docs/ui.md, "The column view"):
/// the file row's icon and name, and a chevron on a folder, read from shared
/// memory when the repeater shows it, as <see cref="FileRow"/> does. It sets
/// only the values that differ from what it shows.
/// </summary>
public sealed partial class ColumnRow : UserControl
{
    // Segoe Fluent Icons: Document (folders draw FolderGlyph).
    private const string FileGlyph = "";

    private readonly Brush _plainIconBrush;
    private int _metricsVersion = -1;
    private Shown<string> _name;
    private Shown<string> _state;
    private Shown<bool> _folder;
    private Shown<bool> _cursor;
    private Shown<ImageSource?> _image;
    private Shown<Brush?> _glyphBrush;
    private Shown<int> _icon;
    private RowItem? _item;
    private bool _selected;
    private bool _away;
    private bool _pointerOver;

    /// <summary>Creates a row; the column's repeater recycles it for many indexes.</summary>
    public ColumnRow()
    {
        InitializeComponent();
        _plainIconBrush = Icon.Foreground;
        Icon.Glyph = FileGlyph;
        ApplyMetrics();
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
    }

    /// <summary>The row's index in its listing, or -1.</summary>
    public int Index { get; private set; } = -1;

    /// <summary>The key of the shell icon the row shows or waits for, or null.</summary>
    public string? IconKey { get; private set; }

    /// <summary>The name's text, where inline rename puts its text box.</summary>
    public FrameworkElement NameElement => NameText;

    /// <summary>The name the row shows (the snapshot aid's log).</summary>
    public string RowName => NameText.Text;

    /// <summary>Whether the row shows the chevron of a folder.</summary>
    public bool ShowsChevron => Chevron.Visibility == Visibility.Visible;

    /// <summary>
    /// Selected or marked, and whether its column has the keyboard: there the
    /// row has the list's fill and accent bar, elsewhere a quieter fill.
    /// </summary>
    public void SetSelected(bool selected, bool away)
    {
        if (_selected != selected || _away != away)
        {
            _selected = selected;
            _away = away;
            UpdateState();
        }
    }

    /// <summary>Whether the row shows the keyboard's outline.</summary>
    public bool ShowsCursor
    {
        get => CursorOutline.Visibility == Visibility.Visible;
        set
        {
            if (_cursor.Take(value))
            {
                CursorOutline.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>Shows <paramref name="data"/>, a listing's row; the column calls it when its repeater prepares the row.</summary>
    public void Show(object? data)
    {
        var started = FrameParts.Start();
        if (_metricsVersion != WindowMetrics.Version)
        {
            ApplyMetrics();
        }
        _item = data as RowItem;
        if (_item is not { } item)
        {
            Index = -1;
            IconKey = null;
            FrameParts.Stop(FramePart.Bind, started);
            return;
        }
        var view = item.View;
        Index = item.Index;
        var isFolder = view.IsFolder(item.Index);
        var name = view.Name(item.Index);
        if (_name.Take(name))
        {
            NameText.Text = name;
        }
        if (_folder.Take(isFolder))
        {
            Chevron.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
        }
        BindIcon(item);
        _pointerOver = false;
        UpdateState();
        FrameParts.Stop(FramePart.Bind, started);
    }

    /// <summary>Shows the icon again: it arrived after the row was bound.</summary>
    public void RefreshDetails()
    {
        if (_item is { } item)
        {
            BindIcon(item);
        }
    }

    /// <summary>Lays the row out with the window's sizes now (docs/ui.md, "Metrics and chrome"): the list's row height, padding, corners, bar and text size.</summary>
    public void ApplyMetrics()
    {
        _metricsVersion = WindowMetrics.Version;
        var m = WindowMetrics.Current;
        Root.Height = m.RowHeight;
        Root.CornerRadius = WindowMetrics.Corners(m.RowRadius);
        CursorOutline.CornerRadius = Root.CornerRadius;
        Pill.Width = m.SelectionBarWidth;
        Pill.RadiusX = Pill.RadiusY = m.SelectionBarWidth / 2;
        Pill.Height = Math.Clamp(m.RowHeight - 4, 0, 16);
        Cell.Margin = WindowMetrics.Pad(m.RowPaddingX);
        Cell.ColumnSpacing = m.RowIconGap;
        NameText.FontSize = m.FontSize;
        _state.Forget();
        UpdateState();
    }

    // The shell's icon once the core sent it for the column with the keyboard, or what its extension had; the glyphs until then.
    private void BindIcon(RowItem item)
    {
        var view = item.View;
        var index = item.Index;
        var name = view.NameSpan(index);
        var isFolder = view.IsFolder(index);
        var attributes = view.Attributes(index);
        var detail = item.Details?.Detail(index, name, isFolder);
        IconKey = detail is null ? null : DisplayFormat.IconKeyFor(detail, name, isFolder, attributes);
        if (IconKey is not null && item.Details?.Icon(IconKey) is { } image)
        {
            if (_image.Take(image))
            {
                IconImage.Source = image;
            }
            ShowIcon(0);
            return;
        }
        ShowIcon(isFolder ? 1 : 2);
        if (!isFolder)
        {
            var brush = ThemeBrushes.FileType(DisplayFormat.Extension(name)) ?? _plainIconBrush;
            if (_glyphBrush.Take(brush))
            {
                Icon.Foreground = brush;
            }
        }
    }

    // 0 the shell's image, 1 the folder, 2 the file glyph.
    private void ShowIcon(int shown)
    {
        if (_icon.Take(shown))
        {
            IconImage.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
            FolderIcon.Visibility = shown == 1 ? Visibility.Visible : Visibility.Collapsed;
            Icon.Visibility = shown == 2 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = true;
        UpdateState();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = false;
        UpdateState();
    }

    private void UpdateState()
    {
        var state = _selected ? (_away ? "SelectedAway" : "Selected") : _pointerOver ? "PointerOver" : "Normal";
        if (_state.Take(state))
        {
            VisualStateManager.GoToState(this, state, false);
        }
    }
}
