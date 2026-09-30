using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Presentation;
using CabinetOS.Services;
using CabinetOS.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CabinetOS.Views;

/// <summary>
/// One file row. The repeater gives it a <see cref="RowItem"/>; the row reads
/// name, kind, time, type and size from shared memory right then, and keeps
/// nothing else, so recycling it for another index costs a few reads. It
/// sets only the values that differ from what it shows (<see cref="Shown{T}"/>):
/// WinUI lays a text out again even when it gets the same string, and while
/// a folder scrolls most rows repeat their time, type or size.
/// </summary>
public sealed partial class FileRow : UserControl
{
    // Segoe Fluent Icons: Document (folders draw FolderGlyph; a link adds LinkBadge).
    private const string FileGlyph = "\uE8A5";

    private static readonly Brush Clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private readonly Brush _plainIconBrush;
    private readonly Brush _plainSizeBrush;
    private int _metricsVersion = -1;
    private Shown<bool> _striped;
    private Shown<bool> _sizeCounting;
    private Shown<string> _name;
    private Shown<string> _second;
    private Shown<string> _size;
    private Shown<string> _type;
    private Shown<string> _state;
    private Shown<IconShown> _icon;
    private Shown<ImageSource?> _image;
    private Shown<Brush?> _glyphBrush;
    private Shown<bool> _link;
    private Shown<bool> _cloud;
    private Shown<bool> _cursor;
    private RowItem? _item;
    private SearchRowItem? _hit;
    private bool _selected;
    private bool _pointerOver;

    /// <summary>
    /// Shows <paramref name="data"/>: a listing's row or a search hit. The pane
    /// calls it when the repeater prepares the row; the row does not listen to
    /// its own DataContextChanged: with it, a held key's scrolling made WinUI
    /// ask for a full garbage collection every 2 to 3 s (docs/ui.md, "Scrolling").
    /// </summary>
    public void Show(object? data)
    {
        var started = FrameParts.Start();
        // A recycled row built with the sizes of the theme before.
        if (_metricsVersion != WindowMetrics.Version)
        {
            ApplyMetrics();
        }
        if (data is SearchRowItem hit)
        {
            BindHit(hit);
        }
        else
        {
            Bind(data as RowItem);
        }
        FrameParts.Stop(FramePart.Bind, started);
    }

    /// <summary>Creates a row; the repeater recycles it for many indexes.</summary>
    public FileRow()
    {
        InitializeComponent();
        _plainIconBrush = Icon.Foreground;
        _plainSizeBrush = SizeText.Foreground;
        Icon.Glyph = FileGlyph;
        ApplyMetrics();
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        // A row can be dragged out (to a tool's page); the pane decides what goes with it.
        CanDrag = true;
        DragStarting += (_, e) => DragStartingRow?.Invoke(this, e);
        DropCompleted += (_, e) => DropCompletedRow?.Invoke(this, e.DropResult);
    }

    /// <summary>A drag of this row starts; the pane fills the data or cancels it.</summary>
    public event Action<FileRow, DragStartingEventArgs>? DragStartingRow;

    /// <summary>The drag that started from this row ended, dropped or not.</summary>
    public event Action<FileRow, Windows.ApplicationModel.DataTransfer.DataPackageOperation>? DropCompletedRow;

    // Which of the three icon elements shows.
    private enum IconShown
    {
        Image,
        Folder,
        File,
    }

    /// <summary>The row's index in its listing, or -1.</summary>
    public int Index { get; private set; } = -1;

    /// <summary>The theme's stroke for a file named <paramref name="name"/> (the editor tab's glyph too).</summary>
    public static Brush IconBrushFor(string name) =>
        ThemeBrushes.FileType(DisplayFormat.Extension(name)) ?? ThemeResources.Brush("CbRowMetaBrush");

    /// <summary>The key of the shell icon the row shows or waits for, or null.</summary>
    public string? IconKey { get; private set; }

    /// <summary>Whether this row is selected.</summary>
    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected != value)
            {
                _selected = value;
                UpdateState();
            }
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

    /// <summary>The name's text, where inline rename puts its text box.</summary>
    public FrameworkElement NameElement => NameText;

    /// <summary>
    /// Lays the row out with the window's sizes now (docs/ui.md, "Metrics and
    /// chrome"): its height, padding, corners, selection bar, columns, text
    /// sizes, the Size column's figures and the stripes. With
    /// <paramref name="rebind"/> it shows its entry again, as the Commander
    /// look writes dates and types another way.
    /// </summary>
    public void ApplyMetrics(bool rebind = false)
    {
        _metricsVersion = WindowMetrics.Version;
        var m = WindowMetrics.Current;
        Root.Height = m.RowHeight;
        Root.CornerRadius = WindowMetrics.Corners(m.RowRadius);
        CursorOutline.CornerRadius = Root.CornerRadius;
        Pill.Width = m.SelectionBarWidth;
        Pill.RadiusX = Pill.RadiusY = m.SelectionBarWidth / 2;
        Pill.Height = Math.Clamp(m.RowHeight - 4, 0, 16);
        Columns.Margin = WindowMetrics.Pad(m.RowPaddingX);
        Columns.ColumnSpacing = m.ColumnGap;
        WindowMetrics.SetColumns(NameColumn, ModifiedColumn, TypeColumn, SizeColumn);
        ModifiedText.Margin = TypeText.Margin = WindowMetrics.TextGap;
        NameCell.ColumnSpacing = m.RowIconGap;
        NameText.FontSize = m.FontSize;
        ModifiedText.FontSize = TypeText.FontSize = SizeText.FontSize = m.SecondaryFontSize;
        if (WindowMetrics.FiguresFont is { } figures)
        {
            SizeText.FontFamily = figures;
        }
        else
        {
            SizeText.ClearValue(TextBlock.FontFamilyProperty);
        }
        _striped.Forget();
        UpdateStripe();
        _state.Forget();
        UpdateState();
        if (rebind && _item is { } item)
        {
            Bind(item);
        }
        else if (rebind && _hit is { } hit)
        {
            BindHit(hit);
        }
    }

    /// <summary>The row's name, date, type and size texts that are cut short with "…" now (the snapshot aid's check).</summary>
    public IEnumerable<string> TrimmedTexts()
    {
        foreach (var (column, text) in new[] { ("name", NameText), ("modified", ModifiedText), ("type", TypeText), ("size", SizeText) })
        {
            // The size has no ellipsis: a size wider than its column would be cut off at its left edge.
            var cut = text.IsTextTrimmed || (text == SizeText && text.ActualWidth > SizeColumn.ActualWidth + 0.5);
            if (cut)
            {
                yield return $"{column}: {text.Text}";
            }
        }
    }

    // Every other row a shade lighter when the theme turns the stripes on (chrome rowStripes).
    private void UpdateStripe()
    {
        var striped = WindowMetrics.Chrome.RowStripes && Index % 2 == 1;
        if (_striped.Take(striped))
        {
            Root.Background = striped ? ThemeResources.Brush("CbRowStripeBrush") : Clear;
        }
    }

    /// <summary>Shows the type name and icon again: they arrived after the row was bound.</summary>
    public void RefreshDetails()
    {
        if (_item is { } item)
        {
            BindDetails(item);
        }
        else if (_hit is { } hit)
        {
            ShowTypeAndIcon(hit.Name, hit.Hit.IsFolder ? EntryKind.Directory : EntryKind.File, hit.Hit.IsFolder, hit.Details, -1);
        }
    }

    /// <inheritdoc/>
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        var started = FrameParts.Start();
        var size = base.MeasureOverride(availableSize);
        FrameParts.Stop(FramePart.RowMeasure, started);
        return size;
    }

    // A search hit (docs/ui.md, "Search"): its folder where a listing shows the time, and no size.
    private void BindHit(SearchRowItem hit)
    {
        _item = null;
        _hit = hit;
        Index = hit.Index;
        UpdateStripe();
        SetText(NameText, ref _name, hit.Name);
        SetText(ModifiedText, ref _second, hit.FolderText);
        SetText(SizeText, ref _size, "");
        ShowTypeAndIcon(hit.Name, hit.Hit.IsFolder ? EntryKind.Directory : EntryKind.File, hit.Hit.IsFolder, hit.Details, -1);
        _pointerOver = false;
        UpdateState();
    }

    private void Bind(RowItem? item)
    {
        _item = item;
        _hit = null;
        if (item is null)
        {
            Index = -1;
            IconKey = null;
            return;
        }
        var view = item.View;
        var index = item.Index;
        Index = index;
        UpdateStripe();
        var isFolder = view.IsFolder(index);
        SetText(NameText, ref _name, view.Name(index));
        SetText(ModifiedText, ref _second, WindowMetrics.Chrome.Hairlines
            ? DisplayFormat.ModifiedShort(view.Modified(index), DateTime.Now)
            : DisplayFormat.Modified(view.Modified(index), DateTime.Now));
        BindSize(item, isFolder);
        BindDetails(item);
        _pointerOver = false;
        UpdateState();
    }

    /// <summary>Shows a measured folder's size again: the core's count moved on, or ended.</summary>
    public void RefreshSize()
    {
        if (_item is { } item)
        {
            BindSize(item, item.View.IsFolder(item.Index));
        }
    }

    // A file's size from the listing; a folder's once measured (Space, Shift+Alt+Enter), in the
    // tertiary colour while the core still counts.
    private void BindSize(RowItem item, bool isFolder)
    {
        var measured = isFolder ? item.Details?.MeasuredSize(item.View.NameSpan(item.Index)) : null;
        SetText(SizeText, ref _size, measured is { } size ? DisplayFormat.Size(size.Bytes, false) : DisplayFormat.Size(item.View.Size(item.Index), isFolder));
        var counting = measured is { Done: false };
        if (_sizeCounting.Take(counting))
        {
            SizeText.Foreground = counting ? ThemeResources.Brush("CbTextTertiaryBrush") : _plainSizeBrush;
        }
    }

    private void BindDetails(RowItem item)
    {
        var view = item.View;
        var index = item.Index;
        ShowTypeAndIcon(view.NameSpan(index), view.Kind(index), view.IsFolder(index), item.Details, index,
            EntryFacts.LinkOf(view, index), view.Attributes(index));
    }

    // The shell's type name and icon once the core sent them (protocol 9); the built-in text and glyph until then.
    // A link says so in the Type column and on its icon; a row not on this disk shows a cloud and is never read to draw it.
    private void ShowTypeAndIcon(ReadOnlySpan<char> name, EntryKind kind, bool isFolder, IRowDetails? details, int index,
        LinkKind link = LinkKind.None, uint attributes = 0)
    {
        var detail = details?.Detail(index, name, isFolder);
        // The Commander look (hairlines) names types short enough for its narrow column.
        SetText(TypeText, ref _type, WindowMetrics.Chrome.Hairlines
            ? DisplayFormat.ShortType(name, kind, isFolder, link)
            : DisplayFormat.RowType(name, kind, isFolder, link, detail));
        if (_link.Take(link != LinkKind.None))
        {
            LinkBadge.Visibility = link != LinkKind.None ? Visibility.Visible : Visibility.Collapsed;
        }
        var cloud = EntryFacts.IsNotOnDisk(attributes);
        if (_cloud.Take(cloud))
        {
            CloudMark.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
        }
        IconKey = detail is null ? null : DisplayFormat.IconKeyFor(detail, name, isFolder, attributes);
        if (IconKey is not null && details?.Icon(IconKey) is { } image)
        {
            if (_image.Take(image))
            {
                IconImage.Source = image;
            }
            ShowIcon(IconShown.Image);
            return;
        }
        // A folder until its shell icon comes: the design's two-tone folder in the theme's colours
        // (a link to a folder too: its badge says it is a link).
        ShowIcon(isFolder ? IconShown.Folder : IconShown.File);
        if (!isFolder)
        {
            var brush = ThemeBrushes.FileType(DisplayFormat.Extension(name)) ?? _plainIconBrush;
            if (_glyphBrush.Take(brush))
            {
                Icon.Foreground = brush;
            }
        }
    }

    private void ShowIcon(IconShown shown)
    {
        if (_icon.Take(shown))
        {
            IconImage.Visibility = shown == IconShown.Image ? Visibility.Visible : Visibility.Collapsed;
            FolderIcon.Visibility = shown == IconShown.Folder ? Visibility.Visible : Visibility.Collapsed;
            Icon.Visibility = shown == IconShown.File ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void SetText(TextBlock text, ref Shown<string> shown, string value)
    {
        if (shown.Take(value))
        {
            text.Text = value;
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
        var state = _selected ? (WindowMetrics.Chrome.RowStripes ? "SelectedStriped" : "Selected") : _pointerOver ? "PointerOver" : "Normal";
        if (_state.Take(state))
        {
            VisualStateManager.GoToState(this, state, false);
        }
    }
}
