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

    /// <summary>Whether this row is the pane's selected row.</summary>
    public bool IsSelected
    {
        get => _selected;
        set
        {
            _selected = value;
            UpdateState();
        }
    }

    private void Bind(RowItem? item)
    {
        if (item is null)
        {
            Index = -1;
            return;
        }
        var view = item.View;
        var index = item.Index;
        Index = index;
        var name = view.NameSpan(index);
        var kind = view.Kind(index);
        var isFolder = view.IsFolder(index);

        NameText.Text = name.ToString();
        ModifiedText.Text = DisplayFormat.Modified(view.Modified(index), DateTime.Now);
        TypeText.Text = DisplayFormat.TypeText(name, kind, isFolder);
        SizeText.Text = DisplayFormat.Size(view.Size(index), isFolder);

        if (kind == EntryKind.Link)
        {
            Icon.Glyph = "";
            Icon.Foreground = isFolder ? FolderBrush : _plainIconBrush;
        }
        else if (isFolder)
        {
            Icon.Glyph = "";
            Icon.Foreground = FolderBrush;
        }
        else
        {
            Icon.Glyph = "";
            Icon.Foreground = TypeBrushes.TryGetValue(DisplayFormat.Extension(name), out var brush) ? brush : _plainIconBrush;
        }
        _pointerOver = false;
        UpdateState();
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
