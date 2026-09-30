using System.Diagnostics;
using System.Text.Json;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Market;
using CabinetOS.Core.Plugins;
using CabinetOS.Core.Protocol;
using CabinetOS.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace CabinetOS.Views;

/// <summary>
/// The marketplace (design view C, <c>marketplace.browse</c>, Ctrl+Shift+X),
/// in place of the main column: the nav with the tabs and their counts, the
/// search field, the card grid, and the detail column of the selected card.
/// Actions that ask the core (refresh, install, uninstall, Source) run
/// commands through the window's router; the tab, the search text and the
/// selection go straight to the model.
/// </summary>
public sealed partial class MarketplaceView : UserControl
{
    private readonly Storyboard _detailEntrance;
    private readonly Style _cardStyle;
    private readonly List<NavRowView> _navRows = [];
    private Dictionary<string, Card> _cards = new(StringComparer.Ordinal);
    private List<string> _order = [];
    private MarketplaceModel? _model;
    private MarketItem? _detailItem;

    // An opening that showed no card yet: its first cards are logged at the frame that draws them (Article 12).
    private long _openedTicks;

    /// <summary>Creates the view, hidden.</summary>
    public MarketplaceView()
    {
        InitializeComponent();
        _detailEntrance = (Storyboard)Resources["DetailEntrance"];
        _cardStyle = (Style)Resources["CardButtonStyle"];
        SearchField.TextChanged += (_, _) => _ = _model?.QueryChangedAsync(SearchField.Text);
        RefreshButton.Click += (_, _) => Run("market.refresh", null);
        CloseDetailButton.Click += (_, _) => _model?.Select(null);
        PrimaryButton.Click += (_, _) => RunForSelected("market.install");
        SourceButton.Click += (_, _) => RunForSelected("market.source");
        UninstallButton.Click += (_, _) => RunForSelected("market.uninstall");
    }

    /// <summary>Runs a command through the window's router: (command, args, trigger).</summary>
    public Func<string, JsonElement?, string, Task>? RunCommand { get; set; }

    /// <summary>
    /// Lays the marketplace out with the window's sizes and chrome now
    /// (docs/ui.md, "Metrics and chrome"): its corners as a pane's, the tabs
    /// of its side list, and the cards' gap, padding and corners. Under
    /// hairlines it has no frame of its own, as in the handout: the sidebar's
    /// and the status bar's lines bound it.
    /// </summary>
    public void ApplyMetrics()
    {
        var m = WindowMetrics.Current;
        MarketFrame.CornerRadius = WindowMetrics.Corners(m.RadiusSurface);
        MarketFrame.BorderThickness = new Thickness(WindowMetrics.Chrome.Hairlines ? 0 : 1);
        // Its controls take the control radius; the items' icon tiles keep theirs, as the handout's marketplace does.
        var control = WindowMetrics.Corners(m.RadiusControl);
        SearchField.CornerRadius = control;
        foreach (var button in new[] { RefreshButton, CloseDetailButton, PrimaryButton, SourceButton, UninstallButton })
        {
            button.CornerRadius = control;
        }
        foreach (var row in _navRows)
        {
            SizeNavRow(row);
        }
        Cards.Spacing = m.MarketplaceCardGap;
        foreach (var card in _cards.Values)
        {
            SizeCard(card.Root);
            card.Tile.CornerRadius = WindowMetrics.Inner(8);
        }
        Cards.InvalidateMeasure();
        DetailTile.CornerRadius = WindowMetrics.Inner(10);
        foreach (var box in Facts.Children.OfType<Border>())
        {
            box.CornerRadius = WindowMetrics.Inner(6);
        }
        if (_detailItem is { } shown)
        {
            // Its capability rows are made with the sizes of the moment.
            FillDetail(shown);
        }
    }

    private static void SizeNavRow(NavRowView row)
    {
        var m = WindowMetrics.Current;
        row.Button.MinHeight = m.MarketplaceTabHeight;
        row.Button.CornerRadius = WindowMetrics.Corners(m.MarketplaceTabRadius);
        if (row.Button.Content is Grid grid)
        {
            grid.Height = m.MarketplaceTabHeight;
        }
    }

    private static void SizeCard(Button card)
    {
        var m = WindowMetrics.Current;
        card.Padding = WindowMetrics.Pad(m.MarketplaceCardPaddingX, m.MarketplaceCardPaddingY);
        card.CornerRadius = WindowMetrics.Corners(m.MarketplaceCardRadius);
    }

    /// <summary>Whether the marketplace is shown.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>The marketplace's state.</summary>
    public MarketplaceModel? Model
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

    /// <summary>Shows the marketplace; the keyboard goes to the search field.</summary>
    public void Open()
    {
        Visibility = Visibility.Visible;
        _openedTicks = Cards.Children.Count == 0 ? Stopwatch.GetTimestamp() : 0;
        Render();
        SearchField.Focus(FocusState.Programmatic);
    }

    /// <summary>Hides the marketplace.</summary>
    public void Close()
    {
        Visibility = Visibility.Collapsed;
        OpenToolTips.Close(XamlRoot);
    }

    /// <summary>Gives the keyboard to the search field.</summary>
    public void FocusSearch() => SearchField.Focus(FocusState.Programmatic);

    /// <summary>
    /// Draws the detail column again with the theme's level colours, which its
    /// capability dots take when they are made (a theme changed while shown).
    /// </summary>
    public void Repaint()
    {
        _detailItem = null;
        Render();
    }

    private void Render()
    {
        if (_model is not { } model || !IsOpen)
        {
            return;
        }
        RenderNav(model);
        CaptionText.Text = model.Status == MarketStatus.Ready ? model.Caption : "";
        RefreshButton.IsEnabled = model.Status != MarketStatus.Loading;
        RenderCards(model);
        if (model.Notice is { } notice)
        {
            NoticeTitle.Text = notice.Title;
            NoticeDetail.Text = notice.Detail;
            NoticeDetail.Visibility = notice.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoticePanel.Visibility = Visibility.Visible;
            CardScroller.Visibility = Visibility.Collapsed;
        }
        else
        {
            NoticePanel.Visibility = Visibility.Collapsed;
            CardScroller.Visibility = Visibility.Visible;
        }
        RenderDetail(model);
    }

    // The nav: one 32 px row per tab, the count on the right, the accent pill and a fill on the one
    // shown. Made once and updated in place, so a row with the keyboard keeps it while events come.
    private void RenderNav(MarketplaceModel model)
    {
        if (_navRows.Count == 0)
        {
            foreach (var (id, title) in MarketTabs.All)
            {
                var row = NavRow(id, title);
                SizeNavRow(row);
                _navRows.Add(row);
            }
        }
        foreach (var row in _navRows)
        {
            var selected = model.Tab == row.Tab;
            row.Button.Background = selected ? ThemeResources.Brush("CbHoverFillBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            row.Pill.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            row.Count.Text = model.Status == MarketStatus.Ready ? model.CountOf(row.Tab).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        }
    }

    private NavRowView NavRow(string id, string title)
    {
        var row = new Grid { Height = 32, Padding = new Thickness(10, 0, 10, 0), ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeResources.Brush("CbTextPrimaryBrush") });
        var count = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeResources.Brush("CbHintTextBrush"),
        };
        Grid.SetColumn(count, 1);
        row.Children.Add(count);
        var pill = new Rectangle
        {
            Width = 3,
            Height = 16,
            RadiusX = 1.5,
            RadiusY = 1.5,
            Margin = new Thickness(-10, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = ThemeResources.Brush("CbAccentBrush"),
            Visibility = Visibility.Collapsed,
        };
        row.Children.Add(pill);
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["CbSidebarRowButtonStyle"],
            Margin = new Thickness(0),
            Content = row,
        };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => _model?.SetTab(id);
        NavRows.Children.Add(button);
        return new NavRowView(id, button, count, pill);
    }

    // Cards are made once per item and updated in place, so 30 progress events a second only change words.
    private void RenderCards(MarketplaceModel model)
    {
        var items = model.Items;
        var order = items.Select(item => item.Id).ToList();
        if (!order.SequenceEqual(_order) || items.Any(item => !_cards.TryGetValue(item.Id, out var card) || !ReferenceEquals(card.Item, item)))
        {
            var cards = new Dictionary<string, Card>(StringComparer.Ordinal);
            Cards.Children.Clear();
            foreach (var item in items)
            {
                var card = _cards.TryGetValue(item.Id, out var existing) && ReferenceEquals(existing.Item, item)
                    ? existing
                    : new Card(item, _cardStyle, id => _model?.Select(id));
                SizeCard(card.Root);
                cards[item.Id] = card;
                Cards.Children.Add(card.Root);
            }
            _cards = cards;
            _order = order;
            if (_openedTicks != 0 && Cards.Children.Count > 0)
            {
                LogFirstCards(_openedTicks, Cards.Children.Count);
                _openedTicks = 0;
            }
        }
        foreach (var item in items)
        {
            _cards[item.Id].Update(model.SelectedId == item.Id, StateLine(model, item));
        }
    }

    // "marketplace cards shown": from the opening to the first frame after the cards were laid out, as "listing shown" is.
    private void LogFirstCards(long opened, int count)
    {
        void OnFrame(object? sender, object e)
        {
            if (IsOpen && Cards.ActualHeight <= 0 && Stopwatch.GetElapsedTime(opened) < TimeSpan.FromSeconds(5))
            {
                return;
            }
            CompositionTarget.Rendering -= OnFrame;
            if (IsOpen)
            {
                Diag.Info("cabinetos_ui::market", "marketplace cards shown", new LogField("cards", count),
                    new LogField("ms", Math.Round(Stopwatch.GetElapsedTime(opened).TotalMilliseconds, 1)));
            }
        }
        CompositionTarget.Rendering += OnFrame;
    }

    // What a card says about itself in its footer: installing, an update, installed, applied.
    private static string StateLine(MarketplaceModel model, MarketItem item) => model.ActionFor(item) switch
    {
        MarketAction.Installing => model.InstallOf(item.Id) is { Total: > 0 } progress ? $"Installing {(int)(progress.Fraction * 100)}%" : "Installing…",
        MarketAction.Update => "Update available",
        MarketAction.Installed => "Installed",
        MarketAction.Applied => "Applied",
        _ => "",
    };

    private void RenderDetail(MarketplaceModel model)
    {
        if (model.Selected is not { } item)
        {
            Detail.Visibility = Visibility.Collapsed;
            _detailItem = null;
            return;
        }
        if (Detail.Visibility == Visibility.Collapsed)
        {
            Detail.Visibility = Visibility.Visible;
            _detailEntrance.Begin();
        }
        if (!ReferenceEquals(_detailItem, item))
        {
            _detailItem = item;
            FillDetail(item);
        }

        var action = model.ActionFor(item);
        PrimaryText.Text = MarketText.ActionText(action);
        PrimaryButton.IsEnabled = action is MarketAction.Install or MarketAction.InstallAndApply or MarketAction.Update;
        var progress = model.InstallOf(item.Id);
        ProgressBar.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        ProgressScale.ScaleX = progress?.Fraction ?? 0;

        // Only what the marketplace installed: the core refuses the rest (trust rule 7).
        UninstallButton.Visibility = model.IsInstalled(item) && action != MarketAction.Installing ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.IsEnabled = model.CanUninstall(item);
        ToolTipService.SetToolTip(UninstallButton, model.CanUninstall(item)
            ? "Remove the files the marketplace installed"
            : "The theme in effect cannot be removed: choose another theme first (Ctrl+K Ctrl+T).");
        var note = MarketText.Note(model, item);
        DetailNote.Text = note;
        DetailNote.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var error = model.ErrorOf(item.Id);
        DetailError.Text = error ?? "";
        DetailError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // The parts of the detail column that change only with the item.
    private void FillDetail(MarketItem item)
    {
        var tile = MarketText.Tile(item);
        DetailTile.Background = new SolidColorBrush(ReviewDialog.Hex(tile.Color));
        DetailTileText.Text = tile.Text;
        DetailName.Text = item.Name;
        DetailByline.Text = MarketText.Byline(item);
        RatingValue.Text = MarketText.Rating(item);
        RatingStar.Visibility = item.Rating is null ? Visibility.Collapsed : Visibility.Visible;
        RatingLabel.Text = MarketText.RatingsLabel(item);
        InstallsValue.Text = MarketText.Installs(item);
        SizeValue.Text = MarketText.Size(item);
        LongText.Text = item.Long.Length > 0 ? item.Long : item.Description;

        var source = MarketText.SourceUri(item);
        SourceButton.IsEnabled = source is not null;
        ToolTipService.SetToolTip(SourceButton, source?.ToString() ?? "The index gives no web page for this publisher.");

        CapabilityRows.Children.Clear();
        CapabilitiesSection.Visibility = item.Kind == ExtensionKinds.Plugin ? Visibility.Visible : Visibility.Collapsed;
        var capabilities = item.Capabilities ?? [];
        if (capabilities.Count == 0)
        {
            CapabilityRows.Children.Add(new TextBlock
            {
                Text = "Asks for no capabilities: it can only compute within its limits.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeResources.Brush("CbStatusTextBrush"),
            });
        }
        foreach (var capability in capabilities)
        {
            var row = new CapabilityRow(capability.Name, capability.Level ?? "unknown", capability.Reason,
                capability.Roots is { Count: > 0 } roots ? string.Join("; ", roots) : null, false);
            CapabilityRows.Children.Add(CapabilityLine(row));
        }
    }

    // A capability: an 8 px dot in the level's colour, the name (600), the reason (11 px).
    private static Border CapabilityLine(CapabilityRow row)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = row.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeResources.Brush("CbTextPrimaryBrush") });
        text.Children.Add(new TextBlock
        {
            Text = row.Detail,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeResources.Brush("CbStatusTextBrush"),
        });
        var line = new Grid { ColumnSpacing = 10 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(0, 5, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Fill = new SolidColorBrush(ReviewDialog.Hex(row.Color)),
        };
        ToolTipService.SetToolTip(dot, $"{row.LevelText} level");
        line.Children.Add(dot);
        Grid.SetColumn(text, 1);
        line.Children.Add(text);
        return new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = WindowMetrics.Inner(6),
            Background = ThemeResources.Brush("CbCardFillBrush"),
            Child = line,
        };
    }

    private void RunForSelected(string command)
    {
        if (_model?.SelectedId is { } id)
        {
            Run(command, CommandArgs.With("id", id));
        }
    }

    private void Run(string command, JsonElement? args) => _ = RunCommand?.Invoke(command, args, "button");

    // One row of the nav: its tab, and what changes on it.
    private sealed record NavRowView(string Tab, Button Button, TextBlock Count, Rectangle Pill);

    /// <summary>
    /// One card: the 40 px tile, the name with the verified check, the author,
    /// two lines of description, and the footer (rating, installs, what is
    /// installed, the kind chip). Built once; <see cref="Update"/> changes the
    /// stroke and the state words.
    /// </summary>
    private sealed class Card
    {
        private readonly TextBlock _state;

        public Card(MarketItem item, Style style, Action<string> select)
        {
            Item = item;
            var tile = MarketText.Tile(item);
            var tileBox = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = WindowMetrics.Inner(8),
                Background = new SolidColorBrush(ReviewDialog.Hex(tile.Color)),
                Child = new TextBlock
                {
                    Text = tile.Text,
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = ThemeResources.Brush("CbOnAccentBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var name = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeResources.Brush("CbTextPrimaryBrush"),
            };
            name.Inlines.Add(new Run { Text = item.Name });
            if (item.Author.Verified)
            {
                // The design's check: a filled accent circle (Segoe Fluent Icons' CompletedSolid).
                name.Inlines.Add(new Run { Text = " " });
                name.Inlines.Add(new Run
                {
                    Text = "",
                    FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                    FontSize = 12,
                    Foreground = ThemeResources.Brush("CbAccentBrush"),
                });
                ToolTipService.SetToolTip(name, "The index marks this publisher as verified. CabinetOS does not check publishers yet.");
            }
            var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(name);
            heading.Children.Add(new TextBlock
            {
                Text = item.Author.Name,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeResources.Brush("CbStatusTextBrush"),
            });
            // Installing, installed or applied: top right, so the footer keeps the design's facts.
            _state = Small("", "CbAccentBrush");
            _state.VerticalAlignment = VerticalAlignment.Top;
            _state.Visibility = Visibility.Collapsed;
            var top = new Grid { ColumnSpacing = 12 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.Children.Add(tileBox);
            Grid.SetColumn(heading, 1);
            top.Children.Add(heading);
            Grid.SetColumn(_state, 2);
            top.Children.Add(_state);

            var description = new TextBlock
            {
                Text = item.Description,
                FontSize = 12,
                MinHeight = 34,
                MaxLines = 2,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeResources.Brush("CbPaneTitleInactiveBrush"),
            };

            var facts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (item.Rating is null)
            {
                facts.Children.Add(Small("No ratings", "CbStatusTextBrush"));
            }
            else
            {
                facts.Children.Add(Small("★", "CbRatingStarBrush"));
                facts.Children.Add(Small(MarketText.Rating(item), "CbTextPrimaryBrush"));
                facts.Children.Add(Small(MarketText.RatingCount(item), "CbStatusTextBrush"));
            }
            if (MarketText.InstallsLine(item) is { Length: > 0 } installs)
            {
                facts.Children.Add(Small(installs, "CbStatusTextBrush"));
            }
            var chip = new Border
            {
                Padding = new Thickness(6, 1, 6, 1),
                CornerRadius = new CornerRadius(3),
                Background = ThemeResources.Brush("CbBadgeFillBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = Small(MarketText.KindLabel(item), "CbStatusTextBrush"),
            };
            var footer = new Grid { ColumnSpacing = 8 };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.Children.Add(facts);
            Grid.SetColumn(chip, 1);
            footer.Children.Add(chip);

            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(top);
            content.Children.Add(description);
            content.Children.Add(footer);

            Root = new Button { Style = style, Content = content };
            AutomationProperties.SetName(Root, $"{item.Name}, {MarketText.KindLabel(item)}, by {item.Author.Name}");
            Root.Click += (_, _) => select(item.Id);
            Tile = tileBox;
        }

        public MarketItem Item { get; }

        public Button Root { get; }

        public Border Tile { get; }

        public void Update(bool selected, string state)
        {
            Root.BorderBrush = ThemeResources.Brush(selected ? "CbAccentBrush" : "CbDividerBrush");
            _state.Text = state;
            _state.Visibility = state.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static TextBlock Small(string text, string brush) => new()
        {
            Text = text,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeResources.Brush(brush),
        };
    }
}
