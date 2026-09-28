using CabinetOS.Core.Listing;
using CabinetOS.Core.Presentation;
using CabinetOS.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CabinetOS.Views;

/// <summary>
/// One file row. The repeater gives it a <see cref="RowItem"/>; the row reads
/// name, kind, time, type and size from shared memory right then, and keeps
/// nothing else, so recycling it for another index costs a few reads.
/// </summary>
public sealed partial class FileRow : UserControl
{
    // Segoe Fluent Icons: Link, FolderFill, Document.
    private const string LinkGlyph = "";
    private const string FolderGlyph = "";
    private const string FileGlyph = "";

    // The design's file-type strokes (docs/design/README.md, "Color").
    private static readonly Dictionary<string, SolidColorBrush> TypeBrushes = new(StringComparer.Ordinal)
    {
        ["md"] = Brush(0x60, 0xCD, 0xFF),
        ["rs"] = Brush(0xF0, 0x90, 0x6C),
        ["toml"] = Brush(0xC9, 0xB6, 0xFF),
        ["exe"] = Brush(0x6C, 0xCB, 0x5F),
        ["dll"] = Brush(0x6C, 0xCB, 0x5F),
        ["bin"] = Brush(0xE0, 0x70, 0x5E),
        ["pdf"] = Brush(0xE0, 0x70, 0x5E),
        ["zip"] = Brush(0xF2, 0xC0, 0x63),
    };

    private static readonly SolidColorBrush FolderBrush = Brush(0xF2, 0xC0, 0x63);

    private readonly Brush _plainIconBrush;
    private RowItem? _item;
    private bool _selected;
    private bool _pointerOver;

    /// <summary>Creates a row; the repeater recycles it for many indexes.</summary>
    public FileRow()
    {
        InitializeComponent();
        _plainIconBrush = Icon.Foreground;
        DataContextChanged += (_, _) => Bind(DataContext as RowItem);
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
    }

    /// <summary>The row's index in its listing, or -1.</summary>
    public int Index { get; private set; } = -1;

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
    }

    private void Bind(RowItem? item)
    {
        _item = item;
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

    // The shell's type name and icon once the core sent them (protocol 9); the built-in text and glyph until then.
    private void BindDetails(RowItem item)
    {
        var view = item.View;
        var index = item.Index;
        var name = view.NameSpan(index);
        var kind = view.Kind(index);
        var isFolder = view.IsFolder(index);
        var detail = item.Details?.Detail(index, name, isFolder);
        TypeText.Text = detail?.TypeName ?? DisplayFormat.TypeText(name, kind, isFolder);
        IconKey = detail?.IconKey;
        if (IconKey is not null && item.Details?.Icon(IconKey) is { } image)
        {
            IconImage.Source = image;
            IconImage.Visibility = Visibility.Visible;
            Icon.Visibility = Visibility.Collapsed;
            return;
        }
        IconImage.Source = null;
        IconImage.Visibility = Visibility.Collapsed;
        Icon.Visibility = Visibility.Visible;
        if (kind == EntryKind.Link)
        {
            Icon.Glyph = LinkGlyph;
            Icon.Foreground = isFolder ? FolderBrush : _plainIconBrush;
        }
        else if (isFolder)
        {
            Icon.Glyph = FolderGlyph;
            Icon.Foreground = FolderBrush;
        }
        else
        {
            Icon.Glyph = FileGlyph;
            Icon.Foreground = TypeBrushes.TryGetValue(DisplayFormat.Extension(name), out var brush) ? brush : _plainIconBrush;
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

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(0xFF, r, g, b));
}
