using System.Text.Json;
using CabinetOS.Core.Plugins;
using CabinetOS.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace CabinetOS.Views;

/// <summary>
/// The list of <c>plugins.list</c>: every installed plugin with its version,
/// ID, state and the capabilities it asks for (a dot in the level's color),
/// and "Review permissions" for a plugin that waits for grants, "Reload" for
/// one that crashed or could not start. Buttons run commands through the
/// window's router; Esc and a click outside close it.
/// </summary>
public sealed partial class PluginsPanel : UserControl
{
    private (IReadOnlyList<PluginRow> Rows, string Folder)? _shown;

    /// <summary>Creates the panel, hidden.</summary>
    public PluginsPanel()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Run("overlay.close", null);
        Scrim.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Scrim))
            {
                Run("overlay.close", null, "mouse");
            }
        };
        SizeChanged += (_, e) => Panel.Width = Math.Max(200, Math.Min(640, e.NewSize.Width - 32));
    }

    /// <summary>Runs a command through the window's router: (command, args, trigger).</summary>
    public Func<string, JsonElement?, string, Task>? RunCommand { get; set; }

    /// <summary>Whether the panel is shown.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>
    /// Draws the list again with the theme's level colours, which its rows
    /// take when they are made (a theme changed while it is shown).
    /// </summary>
    public void Repaint()
    {
        if (IsOpen && _shown is { } shown)
        {
            Show(shown.Rows, shown.Folder);
        }
    }

    /// <summary>Shows the list, or keeps it up to date while shown.</summary>
    public void Show(IReadOnlyList<PluginRow> rows, string pluginsFolder)
    {
        _shown = (rows, pluginsFolder);
        List.Children.Clear();
        foreach (var row in rows)
        {
            List.Children.Add(CardFor(row));
        }
        SummaryText.Text = rows.Count == 1 ? "1 installed" : $"{rows.Count} installed";
        EmptyText.Text = $"No plugins are installed. A plugin is a folder in {pluginsFolder} (docs/plugins.md).";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!IsOpen)
        {
            Visibility = Visibility.Visible;
            CloseButton.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>Hides the panel.</summary>
    public void Close()
    {
        Visibility = Visibility.Collapsed;
        OpenToolTips.Close(XamlRoot);
    }

    private Border CardFor(PluginRow row)
    {
        var tile = row.Tile;
        var icon = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(ReviewDialog.Hex(tile.Color)),
            Child = new TextBlock
            {
                Text = tile.Text,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = ThemeResources.Brush("CbOnAccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = row.Title, FontWeight = FontWeights.SemiBold, Foreground = ThemeResources.Brush("CbTextPrimaryBrush") });
        title.Children.Add(new TextBlock
        {
            Text = row.Plugin.Id,
            FontFamily = (FontFamily)ThemeResources.Get("CbMonoFont")!,
            FontSize = 11,
            Foreground = ThemeResources.Brush("CbTextTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(title);
        text.Children.Add(new TextBlock
        {
            Text = row.StateText,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeResources.Brush(row.IsProblem ? "CbErrorTextBrush" : "CbTextSecondaryBrush"),
        });
        foreach (var capability in row.Capabilities)
        {
            text.Children.Add(CapabilityLine(capability));
        }

        var actions = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
        if (row.CanReview)
        {
            var review = new Button { Content = "Review permissions", Style = (Style)ThemeResources.Get("AccentButtonStyle")!, FontSize = 12 };
            review.Click += (_, _) => Run("plugins.review", CommandArgs.With("id", row.Plugin.Id));
            actions.Children.Add(review);
        }
        if (row.CanReload)
        {
            var reload = new Button { Content = "Reload", FontSize = 12 };
            ToolTipService.SetToolTip(reload, "Read the plugin's folder again and start it");
            reload.Click += (_, _) => Run("plugins.reload", CommandArgs.With("id", row.Plugin.Id));
            actions.Children.Add(reload);
        }

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            Background = ThemeResources.Brush("CbCardFillBrush"),
            BorderBrush = ThemeResources.Brush("CbDividerBrush"),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
    }

    // One capability: a dot and LEVEL in the level's color, the name, and whether it is allowed.
    private static StackPanel CapabilityLine(CapabilityRow capability)
    {
        var brush = new SolidColorBrush(ReviewDialog.Hex(capability.Color));
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        line.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = brush, VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(new TextBlock { Text = capability.Name, FontSize = 12, Foreground = ThemeResources.Brush("CbTextPrimaryBrush") });
        line.Children.Add(new TextBlock
        {
            Text = capability.LevelText,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        line.Children.Add(new TextBlock
        {
            Text = capability.Granted ? "allowed" : "not allowed yet",
            FontSize = 11,
            Foreground = ThemeResources.Brush("CbHintTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        ToolTipService.SetToolTip(line, capability.DetailWithHosts);
        return line;
    }

    private void Run(string command, JsonElement? args, string trigger = "button") => _ = RunCommand?.Invoke(command, args, trigger);
}
