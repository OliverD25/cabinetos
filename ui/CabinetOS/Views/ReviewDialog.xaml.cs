using System.Globalization;
using System.Text.Json;
using CabinetOS.Core.Plugins;
using CabinetOS.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace CabinetOS.Views;

/// <summary>
/// The permissions review dialog (design view C): the plugin's tile, "Review
/// permissions", "{name} by {author}", the sandbox sentence, one row per
/// capability (level dot, name, LEVEL, reason), the disabled "Trust" box,
/// and Cancel / "Allow and install". Esc, Cancel and the scrim cancel; the
/// buttons run commands through the window's router.
/// </summary>
public sealed partial class ReviewDialog : UserControl
{
    private readonly Storyboard _entrance;

    /// <summary>Creates the dialog, hidden.</summary>
    public ReviewDialog()
    {
        InitializeComponent();
        _entrance = (Storyboard)Resources["Entrance"];
        SandboxText.Text = PermissionReview.Sandbox;
        ToolTipService.SetToolTip(TrustHolder, PermissionReview.TrustNotYet);
        CancelButton.Click += (_, _) => Run("overlay.close");
        AllowButton.Click += (_, _) => Run("plugins.grant");
        Scrim.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Scrim))
            {
                Run("overlay.close", "mouse");
            }
        };
    }

    /// <summary>Runs a command through the window's router: (command, args, trigger).</summary>
    public Func<string, JsonElement?, string, Task>? RunCommand { get; set; }

    /// <summary>The review on screen, or null.</summary>
    public PermissionReview? Review { get; private set; }

    /// <summary>Whether the dialog is shown.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>The dialog opened or closed: while it is open, only its own commands run (docs/ui.md, "Dialogs").</summary>
    public event Action? OpenChanged;

    /// <summary>Shows <paramref name="review"/>; the keyboard goes to Cancel, so Enter does not grant by accident.</summary>
    public void Open(PermissionReview review)
    {
        Review = review;
        var tile = review.Tile;
        Tile.Background = new SolidColorBrush(Hex(tile.Color));
        TileText.Text = tile.Text;
        SubtitleText.Text = review.Subtitle;
        TrustBox.Content = review.TrustText;
        Rows.Children.Clear();
        foreach (var row in review.Rows)
        {
            Rows.Children.Add(RowFor(row));
        }
        ShowBusy(false);
        ShowError(null);
        Visibility = Visibility.Visible;
        _entrance.Begin();
        CancelButton.Focus(FocusState.Programmatic);
        OpenChanged?.Invoke();
    }

    /// <summary>
    /// Draws the rows again with the theme's level colours, which they take
    /// when they are made (a theme changed while the dialog is open).
    /// </summary>
    public void Repaint()
    {
        if (!IsOpen || Review is not { } review)
        {
            return;
        }
        Rows.Children.Clear();
        foreach (var row in review.Rows)
        {
            Rows.Children.Add(RowFor(row));
        }
    }

    /// <summary>Hides the dialog.</summary>
    public void Close()
    {
        var wasOpen = IsOpen;
        Review = null;
        Visibility = Visibility.Collapsed;
        OpenToolTips.Close(XamlRoot);
        if (wasOpen)
        {
            OpenChanged?.Invoke();
        }
    }

    /// <summary>A grant is on its way: the buttons wait.</summary>
    public void ShowBusy(bool busy)
    {
        AllowButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
    }

    /// <summary>Why the grant failed, under the rows; null hides it.</summary>
    public void ShowError(string? text)
    {
        ErrorText.Text = text ?? "";
        ErrorText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // A capability: an 8 px level dot, the name (600), LEVEL in the level's color (10 px), and the reason (12 px).
    private static Border RowFor(CapabilityRow row)
    {
        var levelBrush = new SolidColorBrush(Hex(row.Color));
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = row.Name, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock
        {
            Text = row.LevelText,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 50,
            Foreground = levelBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(new TextBlock
        {
            Text = row.Detail,
            FontSize = 12,
            Foreground = ThemeResources.Brush("CbStatusTextBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        var line = new Grid { ColumnSpacing = 12 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = levelBrush, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) });
        Grid.SetColumn(text, 1);
        line.Children.Add(text);
        return new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(6),
            Background = ThemeResources.Brush("CbCardFillBrush"),
            BorderBrush = ThemeResources.Brush("CbDividerBrush"),
            BorderThickness = new Thickness(1),
            Child = line,
        };
    }

    private void Run(string command, string trigger = "button") => _ = RunCommand?.Invoke(command, null, trigger);

    /// <summary>A color from the design's <c>#RRGGBB</c>.</summary>
    internal static Color Hex(string text) =>
        Color.FromArgb(0xFF,
            byte.Parse(text.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(text.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(text.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}
