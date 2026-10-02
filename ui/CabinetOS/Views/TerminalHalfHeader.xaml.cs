using System.Text.Json;
using CabinetOS.Core.Terminal;
using CabinetOS.Core.Themes;
using CabinetOS.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace CabinetOS.Views;

/// <summary>
/// The terminal's header (design view A), for the one view of the Tool Dock or for one half of the split mirror
/// (docs/ui.md, "The terminal"): a tab per session with its pane's badge and its mode, "+" for the default shell
/// and a menu of the others, the caption, and close. In the split it also names its pane by a badge in the pane's
/// colour, so a half with no session still says which pane it sits under. Every button runs a command through the
/// window's router.
/// </summary>
public sealed partial class TerminalHalfHeader : UserControl
{
    private int? _pane;
    private TerminalHeaderBadge _badge = new(null, null);

    /// <summary>Creates a header for the one view (until <see cref="BindToPane"/> says it is a half).</summary>
    public TerminalHalfHeader()
    {
        InitializeComponent();
        NewButton.Click += (_, _) => Run("terminal.new", _pane is { } pane ? CommandArgs.Object(("pane", pane)) : null);
        CloseButton.Click += (_, _) => Run("view.toggleTerminal", CommandArgs.Object(("visible", false)));
    }

    /// <summary>Runs a command by ID through the window's router: (command, arguments, trigger).</summary>
    public Func<string, JsonElement?, string, Task>? RunCommand { get; set; }

    /// <summary>The header's close button, which hides the dock; collapsed on the header that is not the last.</summary>
    internal Button Close => CloseButton;

    /// <summary>
    /// The pane the header is the half of in the split mirror, or null for the one view. A half's "+" starts a
    /// session for its own pane, and the pane's badge shows at the header's start.
    /// </summary>
    public void BindToPane(int? pane)
    {
        _pane = pane;
        PaneBadge.Visibility = pane is null ? Visibility.Collapsed : Visibility.Visible;
        if (pane is { } half)
        {
            var look = TerminalHeader.Tab("", half, TerminalMode.Locked, false);
            PaneBadge.Text = look.Badge;
            PaneBadge.Foreground = ThemeResources.Brush(PaneBadgeBrush(half));
            var side = half == 1 ? "right" : "left";
            AutomationProperties.SetName(NewButton, $"New terminal on the {side} pane");
            AutomationProperties.SetName(ProfilesButton, $"Other shells on the {side} pane");
            AutomationProperties.SetName(PaneBadge, $"{side} pane");
        }
        else
        {
            AutomationProperties.SetName(NewButton, "New terminal");
            AutomationProperties.SetName(ProfilesButton, "Other shells");
        }
    }

    /// <summary>The badge's brush key for a pane: the theme's <c>terminalLeftBadge</c> or <c>terminalRightBadge</c>.</summary>
    public static string PaneBadgeBrush(int pane) => pane == 1 ? "CbTerminalRightBadgeBrush" : "CbTerminalLeftBadgeBrush";

    /// <summary>The colours of the badges drawn now, as <c>#AARRGGBB</c>: the half's own, and the shown tab's (for the logs and the tests).</summary>
    internal (string? Half, string? Tab) BadgeColors => (_badge.Half, _badge.Tab);

    /// <summary>
    /// Lays the header out with the window's sizes now (docs/ui.md, "Metrics and chrome"): the header's height and
    /// the tabs' and buttons'. The window draws the tabs again with <see cref="SetTabs"/>.
    /// </summary>
    public void ApplyMetrics()
    {
        var m = WindowMetrics.Current;
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

    /// <summary>
    /// Draws the tabs: a green dot for a running shell, its profile, its pane's badge (the one view only: in a half
    /// every tab is its pane's, and the header's own badge says so), its mode, and ×.
    /// </summary>
    internal void SetTabs(IReadOnlyList<TerminalTab> tabs, TerminalTab? shown)
    {
        TabStrip.Children.Clear();
        string? tabBadge = null;
        foreach (var tab in tabs)
        {
            TabStrip.Children.Add(TabFor(tab, tab == shown, out var badge));
            if (tab == shown)
            {
                tabBadge = badge;
            }
        }
        _badge = new TerminalHeaderBadge(
            _pane is { } pane ? (ThemeResources.Brush(PaneBadgeBrush(pane)) as SolidColorBrush)?.Color.ToString() : null,
            tabBadge);
    }

    /// <summary>The caption right of the tabs, and its tooltip (the whole folder), if any.</summary>
    public void SetCaption(string text, string? tip)
    {
        CaptionText.Text = text;
        ToolTipService.SetToolTip(CaptionText, tip);
    }

    /// <summary>
    /// The menu of the shells (<c>terminal.profiles</c>); in a half each one starts a session for its pane.
    /// </summary>
    public void SetProfiles(TerminalProfiles profiles)
    {
        ProfilesMenu.Items.Clear();
        foreach (var name in profiles.Names)
        {
            var item = new MenuFlyoutItem { Text = name == profiles.DefaultProfile ? $"{name} (default)" : name };
            item.Click += (_, _) => Run("terminal.new", _pane is { } pane
                ? CommandArgs.Object(("profile", name), ("pane", pane))
                : CommandArgs.With("profile", name));
            ProfilesMenu.Items.Add(item);
        }
        ProfilesButton.Visibility = profiles.Names.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement TabFor(TerminalTab tab, bool active, out string? badgeColor)
    {
        var look = TerminalHeader.Tab(tab.Profile, tab.Pane, tab.Mode, tab.Linkable);
        var textBrush = ThemeResources.Brush(active ? "CbTextPrimaryBrush" : "CbStatusTextBrush");
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = ThemeResources.Brush(tab.Running ? "CbRunningBrush" : "CbTextDisabledBrush"),
        });
        title.Children.Add(new TextBlock { Text = look.Title, VerticalAlignment = VerticalAlignment.Center, Foreground = textBrush });
        // The pane's badge, in the pane's colour: the theme's terminalLeftBadge (the accent unless it says otherwise)
        // or terminalRightBadge. In a half the header's own badge says the pane, so the tab leaves it out.
        var badgeBrush = ThemeResources.Brush(PaneBadgeBrush(tab.Pane));
        badgeColor = (badgeBrush as SolidColorBrush)?.Color.ToString();
        if (_pane is null)
        {
            title.Children.Add(new TextBlock
            {
                Text = look.Badge,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = badgeBrush,
            });
        }

        var m = WindowMetrics.Current;
        var select = new Button
        {
            Content = title,
            Style = (Style)ThemeResources.Get("CbDockTabButtonStyle")!,
            Height = m.TerminalTabHeight,
            Padding = new Thickness(10, 0, 4, 0),
            CornerRadius = WindowMetrics.Corners(m.RadiusControl),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            AllowFocusOnInteraction = false,
            IsTabStop = false,
        };
        AutomationProperties.SetName(select, $"{tab.Profile} {look.Badge}, session {tab.SessionId}");
        select.Click += (_, _) => Run("terminal.show", CommandArgs.Object(("session", tab.SessionId)));
        var mode = ModeFor(tab, look, m);

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
            AllowFocusOnInteraction = false,
            IsTabStop = false,
        };
        AutomationProperties.SetName(close, $"Close {tab.Profile}");
        ToolTipService.SetToolTip(close, "Close this shell");
        close.Click += (_, _) => Run("terminal.close", CommandArgs.Object(("session", tab.SessionId)));

        close.Width = close.Height = close.MinWidth = Math.Min(18, m.TerminalTabHeight - 2);
        close.CornerRadius = WindowMetrics.Corners(m.RadiusControl);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(select);
        row.Children.Add(mode);
        row.Children.Add(close);
        return new Border
        {
            Height = m.TerminalTabHeight,
            CornerRadius = WindowMetrics.Corners(m.RadiusControl),
            Background = active ? ThemeResources.Brush("CbTabActiveFillBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = row,
        };
    }

    // The mode: a small text button that switches it (terminal.setMode), or the same text without a
    // button when the profile cannot be linked; the tooltip says what the mode means, or why not.
    private FrameworkElement ModeFor(TerminalTab tab, TerminalTabLook look, ThemeMetrics m)
    {
        FrameworkElement mode;
        if (look.ModeToggles)
        {
            var toggle = new Button
            {
                Content = new TextBlock { Text = look.ModeText, FontSize = 11 },
                Style = (Style)ThemeResources.Get("CbDockTabButtonStyle")!,
                Height = Math.Min(20, m.TerminalTabHeight - 2),
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(0, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = WindowMetrics.Corners(m.RadiusControl),
                Foreground = ThemeResources.Brush("CbStatusTextBrush"),
                AllowFocusOnInteraction = false,
                IsTabStop = false,
            };
            var next = TerminalBinding.ModeName(look.NextMode);
            toggle.Click += (_, _) => Run("terminal.setMode", CommandArgs.Object(("session", tab.SessionId), ("mode", next)));
            mode = toggle;
        }
        else
        {
            mode = new TextBlock
            {
                Text = look.ModeText,
                FontSize = 11,
                Margin = new Thickness(6, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeResources.Brush("CbTextDisabledBrush"),
            };
        }
        AutomationProperties.SetName(mode, look.ModeName);
        ToolTipService.SetToolTip(mode, look.ModeTip);
        return mode;
    }

    private void Run(string command, JsonElement? args = null) => _ = RunCommand?.Invoke(command, args, "button");

    private readonly record struct TerminalHeaderBadge(string? Half, string? Tab);
}
