using System.Text.Json;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Market;
using CabinetOS.Core.Themes;
using CabinetOS.Services;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace CabinetOS.Views;

/// <summary>
/// The theme gallery (<c>themes.browse</c>, docs/ui.md, "The theme gallery"), in place of the main column as the
/// Extensions page is: the heading, the search field, the filter chips, and the themes as tiles, each painted in
/// the theme's own colours. The keyboard stays on the view (the arrows, Home, End, Enter, Delete, Tab, and typing,
/// which goes to the search field); the pointer selects with a click and installs and applies with a double-click.
/// What a tile does runs through the window's router (<c>gallery.activate</c>, <c>gallery.remove</c>); the
/// selection, the filter and the search text go straight to the model, which also previews the selected tile on
/// the whole window.
/// </summary>
public sealed partial class ThemeGalleryView : UserControl
{
    private const string Target = "cabinetos_ui::theme";

    // A tile is about 145 px tall (docs/ui.md): the count of tiles that fill a view, and the first slice at least.
    private const double TileHeight = 145;
    private const int FirstSlice = 12;
    private const int SliceSize = 8;

    private readonly Style _tileStyle;
    private readonly SolidColorBrush _transparent = new(Colors.Transparent);
    private readonly Dictionary<string, TileView> _views = new(StringComparer.Ordinal);
    private readonly List<(GalleryFilter Filter, Button Button, TextBlock Text)> _chips = [];
    private string _chipOrder = "";
    private ThemeGalleryModel? _model;

    // The tiles in the grid, in order: their ids; how many of them are made (the rest follow in slices); and the set's number.
    private IReadOnlyList<string> _shown = [];
    private int _made;
    private int _generation;

    // The selected tile is to scroll into view once it exists and is laid out.
    private bool _scrollPending;
    private bool _layoutHooked;
    private int _scrollTries;

    /// <summary>Creates the view, hidden.</summary>
    public ThemeGalleryView()
    {
        InitializeComponent();
        _tileStyle = (Style)Resources["TileButtonStyle"];
        SearchField.TextChanged += (_, _) => _ = _model?.QueryChangedAsync(SearchField.Text);
        PreviewKeyDown += OnKeyDown;
        KeyRoot.CharacterReceived += OnCharacter;
    }

    /// <summary>Runs a command through the window's router: (command, args, trigger).</summary>
    public Func<string, JsonElement?, string, Task>? RunCommand { get; set; }

    /// <summary>Whether the gallery is shown.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>The gallery's state.</summary>
    public ThemeGalleryModel? Model
    {
        get => _model;
        set
        {
            if (_model is not null)
            {
                _model.Changed -= Render;
            }
            _model = value;
            if (_model is not null)
            {
                _model.Changed += Render;
            }
            Render();
        }
    }

    /// <summary>Shows the gallery with an empty search; the keyboard goes to the tiles.</summary>
    public void Open()
    {
        Visibility = Visibility.Visible;
        SearchField.Text = "";
        _scrollPending = true;
        _scrollTries = 0;
        Render();
        KeyRoot.Focus(FocusState.Programmatic);
    }

    /// <summary>Hides the gallery.</summary>
    public void Close()
    {
        Visibility = Visibility.Collapsed;
        _generation++;
        OpenToolTips.Close(XamlRoot);
    }

    /// <summary>Whether every tile of the model has its view: the slices are done (the snapshot aid's <c>until:gallery-ready</c>).</summary>
    public bool TilesComplete => _model is { } model && _made >= _shown.Count && _shown.Count == model.Tiles.Count;

    /// <summary>Gives the keyboard to the tiles.</summary>
    public void FocusGrid() => KeyRoot.Focus(FocusState.Programmatic);

    /// <summary>Gives the keyboard to the search field (Ctrl+F).</summary>
    public void FocusSearch()
    {
        SearchField.Focus(FocusState.Programmatic);
        SearchField.SelectAll();
    }

    private int Columns => CardSlices.Columns(Tiles.ActualWidth, Tiles.MinItemWidth, Tiles.Spacing);

    private bool SearchHasFocus => XamlRoot is { } root && ReferenceEquals(FocusManager.GetFocusedElement(root), SearchField);

    private static bool IsDown(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    // ----- Keys -----

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_model is not { } model || IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu))
        {
            return;
        }
        var inSearch = SearchHasFocus;
        switch (e.Key)
        {
            case VirtualKey.Up:
                model.Move(0, -1, Columns);
                break;
            case VirtualKey.Down:
                model.Move(0, 1, Columns);
                break;
            // In the search field these keys are the caret's.
            case VirtualKey.Left when !inSearch:
                model.Move(-1, 0, Columns);
                break;
            case VirtualKey.Right when !inSearch:
                model.Move(1, 0, Columns);
                break;
            case VirtualKey.Home when !inSearch:
                model.Home();
                break;
            case VirtualKey.End when !inSearch:
                model.End();
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                if (model.SelectedTile is { Action: not (TileAction.Applied or TileAction.Installing) } tile)
                {
                    Run("gallery.activate", tile.Id, "key");
                }
                return;
            case VirtualKey.Delete when !inSearch:
                e.Handled = true;
                if (model.SelectedTile is { CanRemove: true } removable)
                {
                    Run("gallery.remove", removable.Id, "key");
                }
                return;
            case VirtualKey.Tab:
                // The view keeps the keyboard, as the picker does; Tab steps through the filter chips instead.
                e.Handled = true;
                StepFilter(model, IsDown(VirtualKey.Shift) ? -1 : 1);
                return;
            default:
                return;
        }
        e.Handled = true;
        _scrollPending = true;
        _scrollTries = 0;
        ScrollToSelected();
    }

    // Typing with the keyboard on the tiles goes into the search field.
    private void OnCharacter(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (char.IsControl(e.Character) || e.Character == ' ' || e.OriginalSource is TextBox)
        {
            return;
        }
        if (IsDown(VirtualKey.Control) != IsDown(VirtualKey.Menu))
        {
            // Ctrl or Alt alone is a shortcut, not typing; both together are AltGr, which types.
            return;
        }
        e.Handled = true;
        SearchField.Focus(FocusState.Keyboard);
        SearchField.Text += e.Character;
        SearchField.Select(SearchField.Text.Length, 0);
    }

    private static void StepFilter(ThemeGalleryModel model, int step)
    {
        var order = model.FilterOrder;
        var at = order.ToList().IndexOf(model.Filter);
        model.SetFilter(order[((at < 0 ? 0 : at) + step + order.Count) % order.Count]);
    }

    private void Run(string command, string id, string trigger) => _ = RunCommand?.Invoke(command, CommandArgs.With("id", id), trigger);

    // ----- Drawing -----

    private void Render()
    {
        if (_model is not { } model || !IsOpen)
        {
            return;
        }
        RenderChips(model);
        var tiles = model.Tiles;
        CountText.Text = model.Market.Status is MarketStatus.Loading or MarketStatus.Idle && tiles.Count == 0
            ? ""
            : tiles.Count == 1 ? "1 theme" : $"{tiles.Count} themes";
        var line = model.Error is { } error ? error : model.CatalogueNotice;
        NoticeText.Visibility = line is null ? Visibility.Collapsed : Visibility.Visible;
        NoticeText.Text = line ?? "";
        NoticeText.Foreground = ThemeResources.Brush(model.Error is null ? "CbStatusTextBrush" : "CbErrorTextBrush");
        if (tiles.Count == 0)
        {
            EmptyText.Text = model.Market.Status is MarketStatus.Loading or MarketStatus.Idle ? "Reading the themes catalogue…"
                : model.Filter != GalleryFilter.All || model.Query.Trim().Length > 0 ? "No theme matches."
                : "There are no themes.";
        }
        EmptyText.Visibility = tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderTiles(model);
    }

    private void RenderChips(ThemeGalleryModel model)
    {
        var order = model.FilterOrder;
        var key = string.Join(',', order);
        if (key != _chipOrder)
        {
            // Dark and Light swap places when Windows changes its mode.
            _chipOrder = key;
            _chips.Clear();
            Chips.Children.Clear();
            foreach (var filter in order)
            {
                var chip = MakeChip(filter);
                _chips.Add(chip);
                Chips.Children.Add(chip.Button);
            }
        }
        foreach (var (filter, button, text) in _chips)
        {
            var on = model.Filter == filter;
            button.Background = on ? ThemeResources.Brush("CbSelectedFillBrush") : _transparent;
            text.Foreground = ThemeResources.Brush(on ? "CbTextPrimaryBrush" : "CbTextSecondaryBrush");
            text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private (GalleryFilter Filter, Button Button, TextBlock Text) MakeChip(GalleryFilter filter)
    {
        var title = ThemeGalleryModel.TitleOf(filter);
        var text = new TextBlock { Text = title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["CbSubtleButtonStyle"],
            Height = 26,
            MinWidth = 0,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(13),
            AllowFocusOnInteraction = false,
            IsTabStop = false,
            Content = text,
        };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => _model?.SetFilter(filter);
        return (filter, button, text);
    }

    // The tiles: the first ones that fill the view at once, the rest a slice at a time in turns of their own, so no
    // frame makes them all (as the Extensions page's cards are made). A tile is made once per theme and updated in
    // place, so the progress of an install only changes words.
    private void RenderTiles(ThemeGalleryModel model)
    {
        var tiles = model.Tiles;
        if (!SameShown(tiles))
        {
            StartTiles(tiles);
        }
        var selected = model.SelectedTile?.Id;
        for (var i = 0; i < _made && i < tiles.Count; i++)
        {
            Update(_views[_shown[i]], tiles[i], tiles[i].Id == selected);
        }
        if (_scrollPending)
        {
            ScrollToSelected();
        }
    }

    private bool SameShown(IReadOnlyList<ThemeTile> tiles)
    {
        if (tiles.Count != _shown.Count)
        {
            return false;
        }
        for (var i = 0; i < tiles.Count; i++)
        {
            if (!string.Equals(tiles[i].Id, _shown[i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private void StartTiles(IReadOnlyList<ThemeTile> tiles)
    {
        _shown = [.. tiles.Select(t => t.Id)];
        var generation = ++_generation;
        _made = 0;
        Tiles.Children.Clear();
        if (_views.Count > 200)
        {
            _views.Clear();
        }
        AddTiles(Math.Max(FirstSlice, PerScreen()));
        NextSlice(generation);
    }

    private int PerScreen()
    {
        var padding = TileScroller.Padding;
        var width = TileScroller.ActualWidth - padding.Left - padding.Right;
        var height = TileScroller.ActualHeight - padding.Top - padding.Bottom;
        if (!(width > 0 && height > 0) && XamlRoot?.Size is { Width: > 0 } window)
        {
            width = window.Width - 180 - padding.Left - padding.Right;
            height = window.Height;
        }
        return CardSlices.PerScreen(width, height, Tiles.MinItemWidth, Tiles.Spacing, TileHeight);
    }

    private void AddTiles(int count)
    {
        if (_model is not { } model)
        {
            return;
        }
        var selected = model.SelectedTile?.Id;
        var tiles = model.Tiles;
        for (var to = Math.Min(_shown.Count, _made + count); _made < to; _made++)
        {
            if (_made >= tiles.Count || tiles[_made].Id != _shown[_made])
            {
                // The model moved on: its Changed event starts a new set.
                return;
            }
            var tile = tiles[_made];
            if (!_views.TryGetValue(tile.Id, out var view))
            {
                view = MakeTile(tile.Id);
                _views[tile.Id] = view;
            }
            Tiles.Children.Add(view.Root);
            Update(view, tile, tile.Id == selected);
        }
        if (_scrollPending)
        {
            ScrollToSelected();
        }
    }

    // The next slice at low priority, one per dispatcher turn, so input and frames come between them.
    private void NextSlice(int generation)
    {
        if (_made >= _shown.Count)
        {
            return;
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (generation != _generation || !IsOpen)
            {
                return;
            }
            var before = _made;
            AddTiles(SliceSize);
            if (_made > before)
            {
                NextSlice(generation);
            }
        });
    }

    private void ScrollToSelected()
    {
        var at = _model?.SelectedTile is { } tile ? _shown.ToList().IndexOf(tile.Id) : -1;
        if (at < 0)
        {
            _scrollPending = false;
            return;
        }
        if (at >= _made || !_views.TryGetValue(_shown[at], out var view) || view.Root.ActualHeight <= 0)
        {
            // Not made or not laid out yet: the next layout, or the slice that makes it, asks again.
            if (!_layoutHooked && _scrollTries++ < 30)
            {
                _layoutHooked = true;
                LayoutUpdated += OnLayoutUpdated;
            }
            return;
        }
        _scrollPending = false;
        var top = view.Root.TransformToVisual(TileScroller).TransformPoint(new Point(0, 0)).Y;
        var bottom = top + view.Root.ActualHeight;
        var viewport = TileScroller.ViewportHeight;
        if (top < 0)
        {
            TileScroller.ChangeView(null, TileScroller.VerticalOffset + top - 8, null, disableAnimation: true);
        }
        else if (viewport > 0 && bottom > viewport)
        {
            TileScroller.ChangeView(null, TileScroller.VerticalOffset + (bottom - viewport) + 8, null, disableAnimation: true);
        }
    }

    private void OnLayoutUpdated(object? sender, object e)
    {
        LayoutUpdated -= OnLayoutUpdated;
        _layoutHooked = false;
        if (_scrollPending && IsOpen)
        {
            ScrollToSelected();
        }
    }

    // ----- One tile -----

    private sealed class TileView
    {
        public required string Id { get; init; }

        public Grid Root { get; init; } = new();

        public Button Face { get; init; } = new();

        public Border Surface { get; init; } = new();

        public SolidColorBrush Back { get; } = new();

        public SolidColorBrush Text { get; } = new();

        public SolidColorBrush Faint { get; } = new();

        public SolidColorBrush Hover { get; } = new();

        public SolidColorBrush Soft { get; } = new();

        public SolidColorBrush Accent { get; } = new();

        public TextBlock Name { get; } = new();

        public TextBlock Author { get; } = new();

        public TextBlock MarkText { get; } = new();

        public Border CompactChip { get; set; } = null!;

        public FontIcon Check { get; } = new();

        public TextBlock AppliedText { get; } = new();

        public TextBlock StateLine { get; } = new();

        public StackPanel Actions { get; } = new();

        public Button ActionButton { get; } = new();

        public TextBlock ActionText { get; } = new();

        public Button MoreButton { get; } = new();

        public ThemeTile? Last { get; set; }

        public bool LastSelected { get; set; }

        public bool IsHover { get; set; }
    }

    private TileView MakeTile(string id)
    {
        var view = new TileView { Id = id };
        view.Face.Style = _tileStyle;
        view.Surface.CornerRadius = new CornerRadius(8);
        view.Surface.Background = view.Back;

        // Three short rows in the text colour at 100, 60 and 35 %, the first with the accent's pill, as a file list.
        var rows = new StackPanel { Spacing = 2 };
        rows.Children.Add(BarRow(view, 0.46, 1.0, pill: true));
        rows.Children.Add(BarRow(view, 0.78, 0.6, pill: false));
        rows.Children.Add(BarRow(view, 0.58, 0.35, pill: false));

        view.Name.FontSize = 13;
        view.Name.FontWeight = FontWeights.SemiBold;
        view.Name.Foreground = view.Text;
        view.Name.TextTrimming = TextTrimming.CharacterEllipsis;
        view.Name.Margin = new Thickness(0, 12, 0, 0);
        view.Author.FontSize = 11;
        view.Author.Foreground = view.Text;
        view.Author.Opacity = 0.6;
        view.Author.TextTrimming = TextTrimming.CharacterEllipsis;

        var marks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        marks.Children.Add(MarkChip(view, view.MarkText));
        // Only a density preset shows the second mark.
        view.CompactChip = MarkChip(view, new TextBlock { Text = "Compact" });
        view.CompactChip.Visibility = Visibility.Collapsed;
        marks.Children.Add(view.CompactChip);
        view.StateLine.FontSize = 10;
        view.StateLine.FontWeight = FontWeights.SemiBold;
        view.StateLine.Foreground = view.Accent;
        view.StateLine.VerticalAlignment = VerticalAlignment.Center;
        view.StateLine.TextTrimming = TextTrimming.CharacterEllipsis;
        var markRow = new Grid { Height = 22, Margin = new Thickness(0, 4, 0, 0), ColumnSpacing = 6 };
        markRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        markRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        markRow.Children.Add(marks);
        Grid.SetColumn(view.StateLine, 1);
        view.StateLine.HorizontalAlignment = HorizontalAlignment.Right;
        markRow.Children.Add(view.StateLine);

        var content = new StackPanel();
        content.Children.Add(rows);
        content.Children.Add(view.Name);
        content.Children.Add(view.Author);
        content.Children.Add(markRow);

        // Installed: a check at the top right; the theme in effect: the check and the word "Applied".
        view.Check.Glyph = "";
        view.Check.FontSize = 13;
        view.Check.Foreground = view.Accent;
        view.Check.VerticalAlignment = VerticalAlignment.Center;
        view.AppliedText.Text = "Applied";
        view.AppliedText.FontSize = 11;
        view.AppliedText.FontWeight = FontWeights.SemiBold;
        view.AppliedText.Foreground = view.Text;
        view.AppliedText.VerticalAlignment = VerticalAlignment.Center;
        var corner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        corner.Children.Add(view.Check);
        corner.Children.Add(view.AppliedText);
        ToolTipService.SetToolTip(view.Check, "Installed");

        var inner = new Grid();
        inner.Children.Add(content);
        inner.Children.Add(corner);
        view.Surface.Child = inner;
        view.Face.Content = view.Surface;

        // The actions of the selected or pointed-at tile: Install, Apply or Update, and an overflow with Remove.
        view.Actions.Orientation = Orientation.Horizontal;
        view.Actions.Spacing = 4;
        view.Actions.HorizontalAlignment = HorizontalAlignment.Right;
        view.Actions.VerticalAlignment = VerticalAlignment.Bottom;
        view.Actions.Margin = new Thickness(0, 0, 13, 13);
        view.ActionText.FontSize = 11;
        view.ActionText.FontWeight = FontWeights.SemiBold;
        view.ActionText.Foreground = view.Text;
        StyleSmallButton(view.ActionButton, view);
        view.ActionButton.Padding = new Thickness(10, 0, 10, 0);
        view.ActionButton.Content = view.ActionText;
        view.ActionButton.Click += (_, _) => Run("gallery.activate", id, "button");
        StyleSmallButton(view.MoreButton, view);
        view.MoreButton.Width = 24;
        view.MoreButton.Content = new FontIcon { Glyph = "", FontSize = 12, Foreground = view.Text };
        var remove = new MenuFlyoutItem { Text = "Remove" };
        remove.Click += (_, _) => Run("gallery.remove", id, "menu");
        var flyout = new MenuFlyout();
        flyout.Items.Add(remove);
        view.MoreButton.Flyout = flyout;
        view.Actions.Children.Add(view.ActionButton);
        view.Actions.Children.Add(view.MoreButton);

        view.Root.Children.Add(view.Face);
        view.Root.Children.Add(view.Actions);

        view.Face.Click += (_, _) => OnTileClicked(id);
        view.Face.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            OnTileDoubleTapped(id);
        };
        view.Root.PointerEntered += (_, _) => SetHover(view, true);
        view.Root.PointerExited += (_, _) => SetHover(view, false);
        return view;
    }

    private static Grid BarRow(TileView view, double fill, double opacity, bool pill)
    {
        var row = new Grid { Height = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(11) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fill, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fill, GridUnitType.Star) });
        if (pill)
        {
            row.Children.Add(new Rectangle
            {
                Width = 3,
                Height = 16,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = view.Accent,
                HorizontalAlignment = HorizontalAlignment.Left,
            });
        }
        var bar = new Rectangle { Height = 6, RadiusX = 3, RadiusY = 3, Fill = view.Text, Opacity = opacity, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(bar, 1);
        row.Children.Add(bar);
        return row;
    }

    private static Border MarkChip(TileView view, TextBlock text)
    {
        text.FontSize = 10;
        text.Foreground = view.Text;
        text.Opacity = 0.8;
        return new Border
        {
            BorderBrush = view.Faint,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1),
            Child = text,
        };
    }

    private static void StyleSmallButton(Button button, TileView view)
    {
        button.Style = (Style)Application.Current.Resources["CbSubtleButtonStyle"];
        button.Height = 22;
        button.MinWidth = 0;
        button.CornerRadius = new CornerRadius(4);
        button.Background = view.Soft;
        button.AllowFocusOnInteraction = false;
        button.IsTabStop = false;
    }

    private static Color ToColor(Argb color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    private void SetHover(TileView view, bool hover)
    {
        if (view.IsHover == hover)
        {
            return;
        }
        view.IsHover = hover;
        if (view.Last is { } tile)
        {
            Apply(view, tile, view.LastSelected);
        }
    }

    private void Update(TileView view, ThemeTile tile, bool selected)
    {
        if (view.Last == tile && view.LastSelected == selected)
        {
            return;
        }
        view.Last = tile;
        view.LastSelected = selected;
        Apply(view, tile, selected);
    }

    private void Apply(TileView view, ThemeTile tile, bool selected)
    {
        var colors = tile.Colors;
        view.Back.Color = ToColor(colors.Background);
        view.Text.Color = ToColor(colors.Text);
        view.Accent.Color = ToColor(colors.Accent);
        view.Faint.Color = ToColor(colors.Text.WithAlpha(0x59));
        view.Hover.Color = ToColor(colors.Text.WithAlpha(0x2E));
        view.Soft.Color = ToColor(colors.Text.WithAlpha(0x24));
        view.Name.Text = tile.Name;
        view.Author.Text = tile.Author;
        view.MarkText.Text = tile.Mark;
        view.CompactChip.Visibility = tile.Compact ? Visibility.Visible : Visibility.Collapsed;
        view.Check.Visibility = tile.IsPresent ? Visibility.Visible : Visibility.Collapsed;
        view.AppliedText.Visibility = tile.IsApplied ? Visibility.Visible : Visibility.Collapsed;

        var actionable = tile.Action is TileAction.Install or TileAction.Apply or TileAction.Update;
        var showActions = actionable && (selected || view.IsHover);
        view.Actions.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
        view.ActionText.Text = tile.Action switch
        {
            TileAction.Install => "Install",
            TileAction.Update => "Update",
            _ => "Apply",
        };
        AutomationProperties.SetName(view.ActionButton, $"{view.ActionText.Text} {tile.Name}");
        view.MoreButton.Visibility = tile.CanRemove ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(view.MoreButton, $"More actions for {tile.Name}");
        // A line on the tile for what is going on or on offer, while the actions do not stand in its place.
        view.StateLine.Text = !showActions && !tile.IsApplied ? tile.StateText : "";
        AutomationProperties.SetName(view.Face, tile.AutomationName);

        // The stroke says which tile is selected (the window's accent) or pointed at (the tile's text colour at 18 %).
        view.Surface.BorderBrush = selected ? ThemeResources.Brush("CbAccentBrush") : view.IsHover ? view.Hover : ThemeResources.Brush("CbLayerStrokeBrush");
        view.Surface.BorderThickness = new Thickness(selected ? 2 : 1);
        view.Surface.Padding = new Thickness(selected ? 11 : 12);
    }

    // ----- The pointer -----

    private void OnTileClicked(string id)
    {
        if (_model is not { } model)
        {
            return;
        }
        var row = model.Tiles.ToList().FindIndex(t => t.Id == id);
        if (row >= 0)
        {
            model.Select(row);
        }
        // The click took no keyboard (the tile is no tab stop): keep the keys on the view, as the arrows need.
        if (!SearchHasFocus && XamlRoot is { } root && !ReferenceEquals(FocusManager.GetFocusedElement(root), KeyRoot))
        {
            KeyRoot.Focus(FocusState.Programmatic);
        }
    }

    private void OnTileDoubleTapped(string id)
    {
        OnTileClicked(id);
        if (_model?.Tiles.FirstOrDefault(t => t.Id == id) is { Action: not (TileAction.Applied or TileAction.Installing) })
        {
            Run("gallery.activate", id, "mouse");
        }
    }

    // ----- The snapshot aid -----

    /// <summary>Puts <paramref name="text"/> in the search field, as typing does.</summary>
    public void TypeForSnapshot(string text) => SearchField.Text = text;

    /// <summary>A click, or a double-click, on the tile of <paramref name="id"/>, as the pointer does it.</summary>
    public void ClickForSnapshot(string id, bool doubleClick)
    {
        if (doubleClick)
        {
            OnTileDoubleTapped(id);
        }
        else
        {
            OnTileClicked(id);
        }
    }

    /// <summary>Logs what the gallery shows, for the window tests: "gallery state" with the fields.</summary>
    public void LogState(string label)
    {
        var model = _model;
        var focus = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
        Diag.Info(Target, "gallery state",
            new LogField("label", label),
            new LogField("open", IsOpen),
            new LogField("filter", model?.Filter.ToString() ?? ""),
            new LogField("query", model?.Query ?? ""),
            new LogField("tiles", model?.Tiles.Count ?? 0),
            new LogField("made", _made),
            new LogField("selected", model?.SelectedTile?.Id ?? ""),
            new LogField("applied", model?.CurrentThemeId ?? ""),
            new LogField("selected_action", model?.SelectedTile?.Action.ToString() ?? ""),
            new LogField("previewing", model?.PreviewingName ?? ""),
            new LogField("columns", Columns),
            new LogField("chips", string.Join(",", _chips.Select(c => c.Filter))),
            new LogField("notice", NoticeText.Visibility == Visibility.Visible ? NoticeText.Text : ""),
            new LogField("keyboard", ReferenceEquals(focus, SearchField) ? "search" : ReferenceEquals(focus, KeyRoot) ? "tiles" : "elsewhere"),
            new LogField("ids", string.Join(",", model?.Tiles.Select(t => t.Id) ?? [])));
    }
}
