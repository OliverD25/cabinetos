using System.Text.Json;
using CabinetOS.Core.Terminal;
using CabinetOS.Core.Themes;
using CabinetOS.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CabinetOS.Views;

/// <summary>
/// The Tool Dock: the panel under or beside the panes. Its first occupant is
/// the terminal (design view A, "Integrated terminal"): a header (one view, or one
/// per half in the split mirror) with a tab per shell session, its pane's badge and
/// its mode, "+" for the default shell and a menu of the others, the caption, and
/// the xterm.js page in one WebView2. Every button runs a command through the
/// window's router.
/// </summary>
public sealed partial class ToolDock : UserControl
{
    // Cascadia Code at 12 px is about 7.2 px a cell; the page's line height 1.25 makes 15 px a row.
    private const double CellWidth = 7.2;
    private const double CellHeight = 15;

    private Func<string, JsonElement?, string, Task>? _run;
    private double[]? _halfWidths;

    /// <summary>Creates the dock; the terminal's WebView2 starts when the first shell does.</summary>
    public ToolDock()
    {
        InitializeComponent();
        TerminalPage = new WebViewHost(TerminalFrame, "terminal");
        ReloadButton.Click += (_, _) => Run("terminal.reload");
        // GotFocus bubbles: the page's WebView2 inside the frame got XAML's focus (a click, a hand-over).
        TerminalFrame.GotFocus += (_, _) => TerminalFocused?.Invoke();
    }

    /// <summary>The terminal's page got XAML's focus, however it came.</summary>
    public event Action? TerminalFocused;

    /// <summary>Runs a command by ID through the window's router: (command, arguments, trigger).</summary>
    public Func<string, JsonElement?, string, Task>? RunCommand
    {
        get => _run;
        set
        {
            _run = value;
            LeftHeader.RunCommand = value;
            RightHeader.RunCommand = value;
        }
    }

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
        LeftHeader.ApplyMetrics();
        RightHeader.ApplyMetrics();
    }

    /// <summary>The terminal page's WebView2.</summary>
    internal WebViewHost TerminalPage { get; }

    /// <summary>A control of the header the keyboard can rest on for a moment, while the window hands it to the page again.</summary>
    internal Control HeaderStop => RightHeader.Visibility == Visibility.Visible ? RightHeader.Close : LeftHeader.Close;

    /// <summary>Whether the terminal page has the keyboard.</summary>
    public bool HasTerminalFocus =>
        XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused && IsInside(focused, TerminalFrame);

    /// <summary>
    /// How far the page starts from the dock's left edge: the frame's border. The places the window measures for the halves
    /// are from the dock's edge; the page's are from its own.
    /// </summary>
    public double BodyInset => Frame.BorderThickness.Left;

    /// <summary>How much of the dock's width the frame's border takes: the page is as wide as the dock less this.</summary>
    public double BodyInsets => Frame.BorderThickness.Left + Frame.BorderThickness.Right;

    /// <summary>
    /// How many cells a new session of <paramref name="pane"/> gets to start with: the body's, or in the split the
    /// width of the pane's half; the page then fits it exactly.
    /// </summary>
    public (ushort Cols, ushort Rows) EstimateCells(int pane)
    {
        var whole = _halfWidths is { } halves && pane >= 0 && pane < halves.Length && halves[pane] > 0 ? halves[pane] : Body.ActualWidth;
        var width = Math.Max(0, whole - 30);
        var height = Math.Max(0, Body.ActualHeight - 12);
        return ((ushort)Math.Clamp((int)(width / CellWidth), 20, 1000), (ushort)Math.Clamp((int)(height / CellHeight), 5, 500));
    }

    /// <summary>
    /// Lays the dock out as the one view (<paramref name="halves"/> null) or as the split mirror: a header over each half
    /// at the half's place, a line in the gap between them, and "+" in a half starting a session for its pane. With
    /// one pane shown there is one half, which is the whole dock.
    /// </summary>
    internal void ApplySplit(IReadOnlyList<SplitHalf>? halves)
    {
        if (halves is null)
        {
            _halfWidths = null;
            FirstColumn.Width = new GridLength(1, GridUnitType.Star);
            GapColumn.Width = new GridLength(0);
            SecondColumn.Width = new GridLength(0);
            SplitDivider.Visibility = Visibility.Collapsed;
            RightHeader.Visibility = Visibility.Collapsed;
            LeftHeader.BindToPane(null);
            LeftHeader.Close.Visibility = Visibility.Visible;
            return;
        }
        _halfWidths = [.. halves.Select(h => h.Width)];
        var two = halves.Count > 1;
        var (first, gap) = TerminalSplitLayout.Columns(halves);
        FirstColumn.Width = two ? new GridLength(first) : new GridLength(1, GridUnitType.Star);
        GapColumn.Width = new GridLength(gap);
        SecondColumn.Width = two ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SplitDivider.Visibility = two ? Visibility.Visible : Visibility.Collapsed;
        RightHeader.Visibility = two ? Visibility.Visible : Visibility.Collapsed;
        LeftHeader.BindToPane(halves[0].Pane);
        LeftHeader.Close.Visibility = two ? Visibility.Collapsed : Visibility.Visible;
        if (two)
        {
            RightHeader.BindToPane(halves[1].Pane);
            RightHeader.Close.Visibility = Visibility.Visible;
        }
    }

    /// <summary>The header of the pane's half in the split (the left one for pane 0), or the one view's.</summary>
    internal TerminalHalfHeader HeaderOf(int pane) => pane == 1 ? RightHeader : LeftHeader;

    /// <summary>Draws the one view's tabs: a green dot for a running shell, its profile, its pane's badge, its mode, and ×.</summary>
    internal void SetTabs(IReadOnlyList<TerminalTab> tabs, TerminalTab? shown)
    {
        LeftHeader.SetTabs(tabs, shown);
        StartingText.Visibility = Visibility.Collapsed;
    }

    /// <summary>Draws the tabs of the pane's half in the split: only that pane's sessions, <paramref name="shown"/> marked.</summary>
    internal void SetHalfTabs(int pane, IReadOnlyList<TerminalTab> tabs, TerminalTab? shown)
    {
        HeaderOf(pane).SetTabs(tabs, shown);
        StartingText.Visibility = Visibility.Collapsed;
    }

    /// <summary>The caption right of the tabs, and its tooltip (the whole folder), if any.</summary>
    public void SetCaption(string text, string? tip) => LeftHeader.SetCaption(text, tip);

    /// <summary>The caption of the pane's half in the split.</summary>
    public void SetHalfCaption(int pane, string text, string? tip) => HeaderOf(pane).SetCaption(text, tip);

    /// <summary>The menu of the other shells (<c>terminal.profiles</c> without the default).</summary>
    public void SetProfiles(TerminalProfiles profiles)
    {
        LeftHeader.SetProfiles(profiles);
        RightHeader.SetProfiles(profiles);
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

    private void Run(string command, JsonElement? args = null) => _ = _run?.Invoke(command, args, "button");

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
