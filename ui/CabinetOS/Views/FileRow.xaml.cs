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
/// nothing else, so recycling it for another index costs a few reads.
/// </summary>
public sealed partial class FileRow : UserControl
{
    // Segoe Fluent Icons: Document (folders draw FolderGlyph; a link adds LinkBadge).
    private const string FileGlyph = "";

    private readonly Brush _plainIconBrush;
    private RowItem? _item;
    private SearchRowItem? _hit;
    private bool _selected;
    private bool _pointerOver;

    /// <summary>Creates a row; the repeater recycles it for many indexes.</summary>
    public FileRow()
    {
        InitializeComponent();
        _plainIconBrush = Icon.Foreground;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SearchRowItem hit)
            {
                BindHit(hit);
            }
            else
            {
                Bind(DataContext as RowItem);
            }
        };
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
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
        set => CursorOutline.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The name's text, where inline rename puts its text box.</summary>
    public FrameworkElement NameElement => NameText;

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

    // A search hit (docs/ui.md, "Search"): its folder where a listing shows the time, and no size.
    private void BindHit(SearchRowItem hit)
    {
        _item = null;
        _hit = hit;
        Index = hit.Index;
        NameText.Text = hit.Name;
        ModifiedText.Text = hit.FolderText;
        SizeText.Text = "";
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
        var name = view.NameSpan(index);
        var isFolder = view.IsFolder(index);

        NameText.Text = name.ToString();
        ModifiedText.Text = DisplayFormat.Modified(view.Modified(index), DateTime.Now);
        SizeText.Text = DisplayFormat.Size(view.Size(index), isFolder);
        BindDetails(item);
        _pointerOver = false;
        UpdateState();
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
        TypeText.Text = DisplayFormat.RowType(name, kind, isFolder, link, detail);
        LinkBadge.Visibility = link != LinkKind.None ? Visibility.Visible : Visibility.Collapsed;
        CloudMark.Visibility = EntryFacts.IsNotOnDisk(attributes) ? Visibility.Visible : Visibility.Collapsed;
        IconKey = detail is null ? null : DisplayFormat.IconKeyFor(detail, name, isFolder, attributes);
        if (IconKey is not null && details?.Icon(IconKey) is { } image)
        {
            IconImage.Source = image;
            IconImage.Visibility = Visibility.Visible;
            Icon.Visibility = Visibility.Collapsed;
            FolderIcon.Visibility = Visibility.Collapsed;
            return;
        }
        IconImage.Source = null;
        IconImage.Visibility = Visibility.Collapsed;
        // A folder until its shell icon comes: the design's two-tone folder in the theme's colours
        // (a link to a folder too: its badge says it is a link).
        FolderIcon.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
        Icon.Visibility = isFolder ? Visibility.Collapsed : Visibility.Visible;
        if (!isFolder)
        {
            Icon.Glyph = FileGlyph;
            Icon.Foreground = ThemeBrushes.FileType(DisplayFormat.Extension(name)) ?? _plainIconBrush;
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

    private void UpdateState() =>
        VisualStateManager.GoToState(this, _selected ? "Selected" : _pointerOver ? "PointerOver" : "Normal", false);
}
