using CabinetOS.Core.Protocol;
using CabinetOS.Core.QuickView;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace CabinetOS.Views;

/// <summary>
/// The Quick View panel (ADR 0023, item W3): a Fluent card over the panes, centred, 72 % of the window's width and
/// 80 % of its height, at least 480 × 360, inside a 24 px margin (8 px in the compact overlay). A 40 px top bar (the
/// icon, the name, "3 of 120", the viewer button when two or more viewers claim the kind, Open and Close), the
/// content (the icon card, the thumbnail, or a viewer's page in <see cref="PageLayer"/>), the line under it and the
/// install offer, and a 28 px bottom line with the size, the date and the viewer's details. It never takes the
/// keyboard: its buttons do not take focus, and the window keeps the keys in the pane's list. What it shows comes
/// from the window's <see cref="QuickViewSession"/>; this class only draws.
/// </summary>
public sealed partial class QuickViewPanel : UserControl
{
    private readonly Storyboard _entrance;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();

    /// <summary>Creates the panel, hidden.</summary>
    public QuickViewPanel()
    {
        InitializeComponent();
        _entrance = (Storyboard)Resources["Entrance"];
        OpenButton.Click += (_, _) => OpenClicked?.Invoke();
        CloseButton.Click += (_, _) => CloseClicked?.Invoke();
        InstallButton.Click += (_, _) => InstallClicked?.Invoke();
        SizeChanged += (_, _) => FitCard();
        ViewerMenu.Closed += (_, _) => MenuClosed?.Invoke();
    }

    /// <summary>Open was clicked: the file opens as Enter opens it.</summary>
    public event Action? OpenClicked;

    /// <summary>Close was clicked.</summary>
    public event Action? CloseClicked;

    /// <summary>The offer's button was clicked (install, or try again).</summary>
    public event Action? InstallClicked;

    /// <summary>A viewer was chosen in the viewer button's list: its ID, or <c>none</c>.</summary>
    public event Action<string>? ViewerChosen;

    /// <summary>The viewer button's list closed (the keyboard goes back to the list).</summary>
    public event Action? MenuClosed;

    /// <summary>Whether the panel is shown.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Whether the window is the compact overlay: the card then fills it less 8 px.</summary>
    public bool Compact { get; set; }

    /// <summary>Where the viewers' WebView2 frames go: over the card and the thumbnail.</summary>
    public Grid Pages => PageLayer;

    /// <summary>The area a viewer's page has, in device-independent pixels (the page's CSS pixels).</summary>
    public Windows.Foundation.Size PageSize => new(Math.Max(1, ContentArea.ActualWidth), Math.Max(1, ContentArea.ActualHeight));

    /// <summary>The thumbnail's image, whose <c>ImageOpened</c> ends the thumbnail's time.</summary>
    public Image Thumbnail => ThumbnailImage;

    /// <summary>The card's own element, for the snapshot aid and the frame that ends the card's time.</summary>
    public Border CardElement => Card;

    /// <summary>Whether Windows' animations are on (Settings, Accessibility, Visual effects).</summary>
    public bool AnimationsOn => _uiSettings.AnimationsEnabled;

    /// <summary>Shows the panel with the 120 ms entrance (none when Windows' animations are off).</summary>
    public void Open()
    {
        var wasOpen = IsOpen;
        Visibility = Visibility.Visible;
        FitCard();
        if (!wasOpen && AnimationsOn)
        {
            _entrance.Begin();
        }
        else
        {
            Card.Opacity = 1;
        }
    }

    /// <summary>Hides the panel.</summary>
    public void Close()
    {
        _entrance.Stop();
        ViewerMenu.Hide();
        Visibility = Visibility.Collapsed;
        ThumbnailImage.Source = null;
        OpenToolTips.Close(XamlRoot);
    }

    /// <summary>The top bar's title, position and icon.</summary>
    public void SetTitle(string name, string position, ImageSource? icon)
    {
        TitleText.Text = name;
        PositionText.Text = position;
        TitleIcon.Source = icon;
    }

    /// <summary>The icon card: the row's icon, the name, the type, and the facts (size and date, or a folder's size).</summary>
    public void SetCard(ImageSource? icon, string name, string type, string facts)
    {
        CardIcon.Source = icon;
        CardName.Text = name;
        CardType.Text = type;
        CardFacts.Text = facts;
    }

    /// <summary>The icon of the card and the title, once a sharper one arrived.</summary>
    public void SetIcon(ImageSource? icon)
    {
        CardIcon.Source = icon;
        TitleIcon.Source = icon;
    }

    /// <summary>The card's facts line (a folder's size grows while it is measured).</summary>
    public void SetCardFacts(string facts) => CardFacts.Text = facts;

    /// <summary>The bottom line.</summary>
    public void SetFacts(string facts) => FactsText.Text = facts;

    /// <summary>
    /// The thumbnail, drawn scaled to fit and at most twice its size in device pixels, centred
    /// (<paramref name="scale"/> is the screen's). Null takes it away.
    /// </summary>
    public void SetThumbnail(ImageSource? source, int width, int height, double scale)
    {
        ThumbnailImage.Source = source;
        if (source is null)
        {
            return;
        }
        ThumbnailImage.MaxWidth = 2.0 * width / scale;
        ThumbnailImage.MaxHeight = 2.0 * height / scale;
    }

    /// <summary>
    /// What fills the content: the card, the thumbnail, or the page. The page comes in with a 100 ms cross-fade from
    /// whichever showed (none when Windows' animations are off).
    /// </summary>
    public void ShowPicture(QuickViewPicture picture, FrameworkElement? page)
    {
        CardView.Visibility = picture == QuickViewPicture.Card ? Visibility.Visible : Visibility.Collapsed;
        ThumbnailImage.Visibility = picture == QuickViewPicture.Thumbnail ? Visibility.Visible : Visibility.Collapsed;
        foreach (var child in PageLayer.Children.OfType<FrameworkElement>())
        {
            var shown = picture == QuickViewPicture.Page && child == page;
            if (!shown)
            {
                child.Opacity = 0;
                child.IsHitTestVisible = false;
                continue;
            }
            child.IsHitTestVisible = true;
            if (child.Opacity >= 1)
            {
                continue;
            }
            if (AnimationsOn)
            {
                var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(100) };
                Storyboard.SetTarget(fade, child);
                Storyboard.SetTargetProperty(fade, "Opacity");
                var board = new Storyboard();
                board.Children.Add(fade);
                board.Begin();
            }
            else
            {
                child.Opacity = 1;
            }
        }
    }

    /// <summary>The line under the thumbnail, or none.</summary>
    public void SetStatus(string? text)
    {
        StatusText.Text = text ?? "";
        StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The offer bar: its line, the button's text (none: no button) and the note under it (none: no note).</summary>
    public void SetOffer(string? text, string? button, string? note)
    {
        OfferBar.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        OfferText.Text = text ?? "";
        InstallButton.Content = button;
        InstallButton.Visibility = button is null ? Visibility.Collapsed : Visibility.Visible;
        OfferNote.Text = note ?? "";
        OfferNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The viewer button: hidden without <paramref name="choices"/>; else the current viewer's name and the list.</summary>
    public void SetViewerChoices(IReadOnlyList<QuickViewer> choices, string? current, bool showButton)
    {
        ViewerButton.Visibility = showButton ? Visibility.Visible : Visibility.Collapsed;
        if (!showButton)
        {
            return;
        }
        var currentName = choices.FirstOrDefault(c => c.Id == current)?.Name ?? "No viewer";
        ViewerButton.Content = $"Viewer: {currentName}";
        ViewerMenu.Items.Clear();
        foreach (var viewer in choices)
        {
            // Plain items with a check mark: they are invoked like any menu item (by mouse, keys and UI Automation).
            var item = new MenuFlyoutItem { Text = viewer.Name, Icon = viewer.Id == current ? new FontIcon { Glyph = "\uE73E" } : null };
            var id = viewer.Id;
            item.Click += (_, _) => ViewerChosen?.Invoke(id);
            ViewerMenu.Items.Add(item);
        }
        ViewerMenu.Items.Add(new MenuFlyoutSeparator());
        var none = new MenuFlyoutItem { Text = "No viewer (thumbnail only)", Icon = current is null ? new FontIcon { Glyph = "\uE73E" } : null };
        none.Click += (_, _) => ViewerChosen?.Invoke(QuickViewChoice.NoViewer);
        ViewerMenu.Items.Add(none);
    }

    /// <summary>What the panel shows now, for the snapshot aid: the viewer button's text, the line and the offer.</summary>
    public (string Viewer, string Status, string Offer, string Button, string Facts, string Card) Describe() => (
        ViewerButton.Visibility == Visibility.Visible ? ViewerButton.Content as string ?? "" : "",
        StatusText.Visibility == Visibility.Visible ? StatusText.Text : "",
        OfferBar.Visibility == Visibility.Visible ? OfferText.Text : "",
        InstallButton.Visibility == Visibility.Visible ? InstallButton.Content as string ?? "" : "",
        FactsText.Text,
        CardView.Visibility == Visibility.Visible ? $"{CardName.Text}|{CardType.Text}|{CardFacts.Text}" : "");

    /// <summary>The card's size and place, for the snapshot aid.</summary>
    public (double Width, double Height) CardSize => (Card.ActualWidth, Card.ActualHeight);

    // 72 % of the window's width and 80 % of its height, at least 480 × 360, inside a 24 px margin; the compact overlay
    // gives it the window less 8 px.
    private void FitCard()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }
        if (Compact)
        {
            Card.Width = Math.Max(1, width - 8);
            Card.Height = Math.Max(1, height - 8);
            return;
        }
        var maxWidth = Math.Max(1, width - 48);
        var maxHeight = Math.Max(1, height - 48);
        Card.Width = Math.Min(maxWidth, Math.Max(480, width * 0.72));
        Card.Height = Math.Min(maxHeight, Math.Max(360, height * 0.80));
    }
}
