using System.Text.Json;
using CabinetOS.Core.Terminal;
using CabinetOS.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace CabinetOS.Views;

/// <summary>
/// The Tool Dock: the panel under or beside the panes. Its first occupant is
/// the terminal (design view A, "Integrated terminal"): one tab per shell
/// session, "+" for the default shell and a menu of the others, the folder
/// sync caption, and the xterm.js page in one WebView2. Every button runs a
/// command through the window's router.
/// </summary>
public sealed partial class ToolDock : UserControl
{
    // Cascadia Code at 12 px is about 7.2 px a cell; the page's line height 1.25 makes 15 px a row.
    private const double CellWidth = 7.2;
    private const double CellHeight = 15;

    /// <summary>Creates the dock; the terminal's WebView2 starts when the first shell does.</summary>
    public ToolDock()
    {
        InitializeComponent();
        TerminalPage = new WebViewHost(TerminalFrame, "terminal");
        NewButton.Click += (_, _) => Run("terminal.new");
        CloseButton.Click += (_, _) => Run("view.toggleTerminal", CommandArgs.Object(("visible", false)));
        ReloadButton.Click += (_, _) => Run("terminal.reload");
    }

    /// <summary>Runs a command by ID through the window's router: (command, arguments, trigger).</summary>
    public Func<string, JsonElement?, string, Task>? RunCommand { get; set; }

    /// <summary>
    /// Lays the dock out with the window's sizes and chrome now (docs/ui.md,
    /// "Metrics and chrome"): its corners, the header and its buttons. Under
    /// hairlines only the edge that meets the panes has a line: the top of a
    /// dock at the bottom (<paramref name="bottom"/>), the left of one beside
    /// them. The window draws the tabs again with <see cref="SetTabs"/>.
    /// </summary>
    public void ApplyMetrics(bool bottom)
    {
        var m = WindowMetrics.Current;
        Frame.CornerRadius = WindowMetrics.Corners(m.RadiusSurface);
        if (WindowMetrics.Chrome.Hairlines)
        {
            Frame.BorderBrush = ThemeResources.Brush("CbHairlineStrongBrush");
            Frame.BorderThickness = bottom ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        }
        else
        {
            Frame.BorderBrush = ThemeResources.Brush("CbTerminalStrokeBrush");
            Frame.BorderThickness = new Thickness(1);
        }
        HeaderRow.Height = new GridLength(m.TerminalHeaderHeight);
        HeaderGrid.Padding = new Thickness(m.TerminalPaddingX, 0, 6, 0);
        foreach (var button in new[] { NewButton, ProfilesButton })
        {
            button.Height = m.TerminalTabHeight;
            button.CornerRadius = WindowMetrics.Corners(m.RadiusControl);
        }
        NewButton.MinWidth = m.TerminalTabHeight;
        CloseButton.Width = CloseButton.Height = CloseButton.MinWidth = Math.Min(24, m.TerminalHeaderHeight);
        CloseButton.CornerRadius = WindowMetrics.Corners(m.RadiusControl);
    }

    /// <summary>The terminal page's WebView2.</summary>
    internal WebViewHost TerminalPage { get; }

    /// <summary>Whether the terminal page has the keyboard.</summary>
    public bool HasTerminalFocus =>
        XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused && IsInside(focused, TerminalFrame);

    /// <summary>Gives the terminal page the keyboard (the page then focuses its shown terminal).</summary>
    public bool FocusTerminalPage() => TerminalPage.View?.Focus(FocusState.Programmatic) ?? false;

    /// <summary>How many cells the body holds now, for a new session; the page then fits it exactly.</summary>
    public (ushort Cols, ushort Rows) EstimateCells()
    {
        var width = Math.Max(0, Body.ActualWidth - 30);
        var height = Math.Max(0, Body.ActualHeight - 12);
        return ((ushort)Math.Clamp((int)(width / CellWidth), 20, 1000), (ushort)Math.Clamp((int)(height / CellHeight), 5, 500));
    }

    /// <summary>Draws the tabs: a green dot for a running shell, its profile, and ×.</summary>
    internal void SetTabs(IReadOnlyList<TerminalTab> tabs, TerminalTab? shown)
    {
        TabStrip.Children.Clear();
        foreach (var tab in tabs)
        {
            TabStrip.Children.Add(TabFor(tab, tab == shown));
        }
        StartingText.Visibility = Visibility.Collapsed;
    }

    /// <summary>The caption right of the tabs.</summary>
    public void SetCaption(string text) => CaptionText.Text = text;

    /// <summary>The menu of the other shells (<c>terminal.profiles</c> without the default).</summary>
    public void SetProfiles(TerminalProfiles profiles)
    {
        ProfilesMenu.Items.Clear();
        foreach (var name in profiles.Names)
        {
            var item = new MenuFlyoutItem { Text = name == profiles.DefaultProfile ? $"{name} (default)" : name };
            item.Click += (_, _) => Run("terminal.new", CommandArgs.With("profile", name));
            ProfilesMenu.Items.Add(item);
        }
        ProfilesButton.Visibility = profiles.Names.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows "Starting the terminal…" until the first tab.</summary>
    public void ShowStarting() => StartingText.Visibility = Visibility.Visible;

    /// <summary>The page's process ended: says so, with Reload.</summary>
    public void ShowStopped(string title, string reason)
    {
        StoppedText.Text = title;
        StoppedReason.Text = reason;
        StoppedPanel.Visibility = Visibility.Visible;
        TerminalFrame.Visibility = Visibility.Collapsed;
    }

    /// <summary>The page runs again.</summary>
    public void HideStopped()
    {
        StoppedPanel.Visibility = Visibility.Collapsed;
        TerminalFrame.Visibility = Visibility.Visible;
    }

    private FrameworkElement TabFor(TerminalTab tab, bool active)
    {
        var textBrush = ThemeResources.Brush(active ? "CbTextPrimaryBrush" : "CbStatusTextBrush");
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = ThemeResources.Brush(tab.Running ? "CbRunningBrush" : "CbTextDisabledBrush"),
        });
        title.Children.Add(new TextBlock { Text = tab.Profile, VerticalAlignment = VerticalAlignment.Center, Foreground = textBrush });

        var m = WindowMetrics.Current;
        var select = new Button
        {
            Content = title,
            Style = (Style)ThemeResources.Get("CbDockTabButtonStyle")!,
            Height = m.TerminalTabHeight,
            Padding = new Thickness(10, 0, 4, 0),
            CornerRadius = WindowMetrics.Corners(m.RadiusControl),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(select, $"{tab.Profile}, session {tab.SessionId}");
        select.Click += (_, _) => Run("terminal.show", CommandArgs.Object(("session", tab.SessionId)));

        var close = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 8 },
            Style = (Style)ThemeResources.Get("CbDockTabButtonStyle")!,
            Width = 18,
            Height = 18,
            MinWidth = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, $"Close {tab.Profile}");
        ToolTipService.SetToolTip(close, "Close this shell");
        close.Click += (_, _) => Run("terminal.close", CommandArgs.Object(("session", tab.SessionId)));

        close.Width = close.Height = close.MinWidth = Math.Min(18, m.TerminalTabHeight - 2);
        close.CornerRadius = WindowMetrics.Corners(m.RadiusControl);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(select);
        row.Children.Add(close);
        return new Border
        {
            Height = m.TerminalTabHeight,
            CornerRadius = WindowMetrics.Corners(m.RadiusControl),
            Background = active ? ThemeResources.Brush("CbTabActiveFillBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = row,
        };
    }

    private void Run(string command, JsonElement? args = null) => _ = RunCommand?.Invoke(command, args, "button");

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current == ancestor)
            {
                return true;
            }
        }
        return false;
    }
}
