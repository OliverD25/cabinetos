using System.Text.Json;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Themes;
using CabinetOS.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;

namespace CabinetOS.Views;

/// <summary>
/// The theme picker (<c>preferences.selectColorTheme</c>, Ctrl+K Ctrl+T):
/// the palette's frame with one row per theme, each with a swatch (the
/// accent over the Mica tint), its name and author, and a check on the
/// theme in effect. Up and Down move, and the window shows the highlighted
/// theme as a preview; Enter or a click applies through the window's router,
/// Esc and the scrim close and bring the theme in effect back.
/// </summary>
public sealed partial class ThemePicker : UserControl
{
    private readonly Storyboard _entrance;
    private ThemePickerModel? _model;

    // The sizes the rows keep while the picker is open. A previewed density preset changes the
    // window's metrics; rows that shrank under the pointer would put another row under it, whose
    // preview would grow them back, and so on.
    private ThemeMetrics? _metrics;

    /// <summary>Creates the picker, hidden.</summary>
    public ThemePicker()
    {
        InitializeComponent();
        _entrance = (Storyboard)Resources["Entrance"];
        Panel.PreviewKeyDown += OnKeyDown;
        Scrim.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Scrim))
            {
                _ = RunCommand?.Invoke("overlay.close", null, "mouse");
            }
        };
        SizeChanged += (_, e) => Panel.Width = Math.Max(200, Math.Min(640, e.NewSize.Width - 32));
    }

    /// <summary>Runs a command through the window's router: (command, args, trigger).</summary>
    public Func<string, JsonElement?, string, Task>? RunCommand { get; set; }

    /// <summary>The accent a theme without one shows: the Windows accent.</summary>
    public Func<Argb>? SystemAccent { get; set; }

    /// <summary>Whether Windows is in light mode, which a theme of kind <c>system</c> shows.</summary>
    public Func<bool>? SystemIsLight { get; set; }

    /// <summary>Whether the picker is shown.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>The picker's state.</summary>
    public ThemePickerModel? Model
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

    /// <summary>Shows the picker and gives it the keyboard.</summary>
    public void Open()
    {
        if (!IsOpen)
        {
            _metrics = WindowMetrics.Current;
        }
        Visibility = Visibility.Visible;
        _entrance.Begin();
        Render();
        Panel.Focus(FocusState.Programmatic);
    }

    /// <summary>Hides the picker.</summary>
    public void Close()
    {
        Visibility = Visibility.Collapsed;
        _metrics = null;
        OpenToolTips.Close(XamlRoot);
    }

    /// <summary>
    /// Draws the rows again: their swatches take the Windows accent and, for
    /// a theme of kind <c>system</c>, Windows' mode, when they are made.
    /// </summary>
    public void Repaint()
    {
        if (IsOpen)
        {
            Render();
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Up:
                _model?.Move(-1);
                e.Handled = true;
                break;
            case VirtualKey.Down:
                _model?.Move(1);
                e.Handled = true;
                break;
            case VirtualKey.Home:
                _model?.SetHighlight(0);
                e.Handled = true;
                break;
            case VirtualKey.End:
                _model?.SetHighlight((_model?.Rows.Count ?? 1) - 1);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                _ = RunCommand?.Invoke("theme.apply", null, "key");
                e.Handled = true;
                break;
            case VirtualKey.Tab:
                // The picker keeps the keyboard while it is open, as the palette does.
                e.Handled = true;
                break;
        }
    }

    private void Render()
    {
        Rows.Children.Clear();
        if (_model is not { } model)
        {
            return;
        }
        CountText.Text = model.Rows.Count == 1 ? "1 theme" : $"{model.Rows.Count} themes";
        for (var i = 0; i < model.Rows.Count; i++)
        {
            Rows.Children.Add(RowFor(model.Rows[i], i, i == model.Highlight));
        }
        FooterText.Text = model.Error ?? "↑↓ or hover to preview · ↵ apply · Esc keeps the current · themes live in %LOCALAPPDATA%\\CabinetOS\\themes";
        FooterText.Foreground = ThemeResources.Brush(model.Error is null ? "CbHintTextBrush" : "CbErrorTextBrush");
    }

    // A row, 36 px: the highlight's pill and fill, the swatch, name and author, and a check on the theme in effect.
    private Grid RowFor(ThemeChoice choice, int index, bool highlighted)
    {
        var accent = choice.Info.Accent is { } own && Argb.TryParse(own, out var parsed) ? parsed : SystemAccent?.Invoke() ?? new Argb(0xFF, 0x60, 0xCD, 0xFF);
        // Plain Mica: its light or dark base, as the theme would show it now.
        var light = choice.Info.Kind == ColorTheme.Light || (choice.Info.Kind == ColorTheme.System && SystemIsLight?.Invoke() == true);
        var tint = choice.Tint is { } tintText && Argb.TryParse(tintText, out var tintColor)
            ? tintColor
            : light ? new Argb(0xFF, 0xF3, 0xF3, 0xF3) : new Argb(0xFF, 0x20, 0x20, 0x20);
        var swatch = new Border
        {
            Width = 40,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeResources.Brush("CbOverlayStrokeBrush"),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, tint.R, tint.G, tint.B)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(Color.FromArgb(0xFF, accent.R, accent.G, accent.B)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTipService.SetToolTip(swatch, choice.Info.Accent is null ? "Follows the Windows accent colour" : $"Accent {choice.Info.Accent}");

        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = choice.Info.Name, Foreground = ThemeResources.Brush("CbTextPrimaryBrush") });
        name.Children.Add(new TextBlock
        {
            Text = choice.Info.Author,
            FontSize = 12,
            Foreground = ThemeResources.Brush("CbHintTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (choice.Info.Kind is ColorTheme.Light or ColorTheme.System)
        {
            name.Children.Add(new TextBlock
            {
                Text = choice.Info.Kind == ColorTheme.Light ? "light" : "light or dark, as Windows is set",
                FontSize = 11,
                Foreground = ThemeResources.Brush("CbHintTextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        if (choice.Info.HasMetrics)
        {
            // A density preset (list_themes' has_metrics): it changes sizes, not only colours.
            var preset = new Border
            {
                Padding = new Thickness(6, 1, 6, 1),
                CornerRadius = new CornerRadius(3),
                Background = ThemeResources.Brush("CbBadgeFillBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "density preset", FontSize = 10, Foreground = ThemeResources.Brush("CbTextSecondaryBrush") },
            };
            ToolTipService.SetToolTip(preset, "This theme changes the window's sizes and elements too, not only its colours");
            name.Children.Add(preset);
        }

        var check = new FontIcon
        {
            Glyph = "",
            FontSize = 12,
            Foreground = ThemeResources.Brush("CbAccentBrush"),
            Visibility = choice.IsCurrent ? Visibility.Visible : Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(check, "The theme in effect");

        var m = _metrics ?? WindowMetrics.Current;
        var row = new Grid
        {
            Height = m.PaletteRowHeight,
            Padding = new Thickness(12, 0, 12, 0),
            ColumnSpacing = 12,
            CornerRadius = WindowMetrics.Corners(m.RadiusControl),
            Background = highlighted ? ThemeResources.Brush("CbSelectedFillBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(swatch);
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        Grid.SetColumn(check, 2);
        row.Children.Add(check);
        if (highlighted)
        {
            row.Children.Add(new Rectangle
            {
                Width = m.SelectionBarWidth,
                Height = Math.Clamp(m.PaletteRowHeight - 4, 0, 16),
                RadiusX = m.SelectionBarWidth / 2,
                RadiusY = m.SelectionBarWidth / 2,
                Margin = new Thickness(-12, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = ThemeResources.Brush("CbAccentBrush"),
            });
        }
        // On a move only: the rows are built again at every highlight change, and a pointer that
        // merely sits over the list would snap the highlight back under itself after each key.
        row.PointerMoved += (_, _) => _model?.SetHighlight(index);
        row.Tapped += (_, _) => _ = RunCommand?.Invoke("theme.apply", CommandArgs.Object(("index", index)), "mouse");
        return row;
    }
}
