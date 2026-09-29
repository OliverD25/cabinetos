using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Preview;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace CabinetOS.Views;

/// <summary>
/// The preview of proposed changes (docs/ui.md, "The preview pane"): a bar
/// with the title and "Enter applies · Esc cancels", over one row per change.
/// A row names what applying it does, the item, and the target in the accent
/// colour; a delete is tinted red. The pane only shows and scrolls: the window
/// decides what Enter and Esc do (<see cref="PreviewKeys"/>), before its keymap.
/// </summary>
public sealed partial class PreviewPane : UserControl
{
    private const string Target = "cabinetos_ui::preview";

    private readonly RowFactory _factory = new();

    /// <summary>Creates the pane, hidden.</summary>
    public PreviewPane()
    {
        InitializeComponent();
        Rows.ItemTemplate = _factory;
        HintText.Text = PreviewSession.Hint;
        ApplyMetrics();
    }

    /// <summary>Which pane this preview covers: 0 left, 1 right.</summary>
    public int PaneIndex { get; set; }

    /// <summary>Whether a preview is shown.</summary>
    public bool IsShown => Visibility == Visibility.Visible;

    /// <summary>The preview on show, or null.</summary>
    public PreviewSession? Session { get; private set; }

    /// <summary>Whether the keyboard is in the pane.</summary>
    public bool HasFocus =>
        XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused && IsInside(focused);

    /// <summary>
    /// Lays the pane out with the window's sizes now: its corners as a pane's
    /// and the bar as high as the editor's tab strip (Article 6).
    /// </summary>
    public void ApplyMetrics()
    {
        var m = WindowMetrics.Current;
        Surface.CornerRadius = WindowMetrics.Corners(m.RadiusSurface);
        BarRow.Height = new GridLength(m.EditorTabHeight);
        Bar.BorderBrush = ThemeResources.Brush(WindowMetrics.Chrome.Hairlines ? "CbHairlineBrush" : "CbDividerBrush");
        TitleText.FontSize = m.FontSize;
        Rows.ItemsSource = null;
        if (Session is { } session)
        {
            Rows.ItemsSource = session.Lines;
        }
    }

    /// <summary>Shows <paramref name="session"/> over the pane's list.</summary>
    public void Show(PreviewSession session)
    {
        Session = session;
        TitleText.Text = session.Title;
        ToolTipService.SetToolTip(TitleText, session.Title);
        CountText.Text = session.Count;
        AutomationProperties.SetName(Scroller, $"{session.Title}, {session.Count}. {PreviewSession.Hint}");
        ShowError(null);
        Rows.ItemsSource = session.Lines;
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        Visibility = Visibility.Visible;
        Diag.Info(Target, "preview shown", new LogField("preview", session.Id), new LogField("title", session.Title),
            new LogField("rows", session.Lines.Count), new LogField("pane", PaneIndex));
    }

    /// <summary>Hides the preview; the pane's own list is under it.</summary>
    public void Hide()
    {
        if (Session is not { } session)
        {
            return;
        }
        Session = null;
        Rows.ItemsSource = null;
        Visibility = Visibility.Collapsed;
        Diag.Info(Target, "preview closed", new LogField("preview", session.Id), new LogField("pane", PaneIndex));
    }

    /// <summary>Says why applying failed, under the rows; null hides the note.</summary>
    public void ShowError(string? text)
    {
        ErrorText.Text = text ?? "";
        ErrorBar.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Gives the keyboard to the pane, which scrolls with the arrows, PageUp, PageDown, Home and End.</summary>
    public bool FocusPane() => Scroller.Focus(FocusState.Programmatic);

    /// <summary>Scrolls for a key of the list; false when the key is not one.</summary>
    public bool Scroll(VirtualKey key)
    {
        var row = Math.Max(1, WindowMetrics.Current.RowHeight);
        var page = Math.Max(row, Scroller.ViewportHeight - row);
        double? target = key switch
        {
            VirtualKey.Down => Scroller.VerticalOffset + row,
            VirtualKey.Up => Scroller.VerticalOffset - row,
            VirtualKey.PageDown => Scroller.VerticalOffset + page,
            VirtualKey.PageUp => Scroller.VerticalOffset - page,
            VirtualKey.Home => 0,
            VirtualKey.End => Scroller.ScrollableHeight,
            _ => null,
        };
        if (target is not { } offset)
        {
            return false;
        }
        Scroller.ChangeView(null, Math.Clamp(offset, 0, Scroller.ScrollableHeight), null, disableAnimation: true);
        return true;
    }

    private bool IsInside(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, this))
            {
                return true;
            }
        }
        return false;
    }

    // One row: the verb, the item's name (bright) with "→ target" in the accent colour, and the item's folder dim
    // at the end, where a long path may be cut without losing the name.
    private sealed class RowView : Grid
    {
        private readonly TextBlock _verb = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CharacterSpacing = 50, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        private readonly TextBlock _arrow = new() { Text = "\u2192", VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _target = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        private readonly StackPanel _main = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _folder = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(14, 0, 0, 0) };
        private int _version = -1;

        public RowView()
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _name.MaxWidth = 420;
            _target.MaxWidth = 320;
            _main.Children.Add(_name);
            _main.Children.Add(_arrow);
            _main.Children.Add(_target);
            SetColumn(_main, 1);
            SetColumn(_folder, 2);
            Children.Add(_verb);
            Children.Add(_main);
            Children.Add(_folder);
        }

        public void Bind(PreviewLine line)
        {
            var m = WindowMetrics.Current;
            if (_version != WindowMetrics.Version)
            {
                _version = WindowMetrics.Version;
                Height = m.RowHeight;
                Padding = WindowMetrics.Pad(m.RowPaddingX);
                _verb.FontSize = Math.Max(9, m.SecondaryFontSize - 1);
                _name.FontSize = _arrow.FontSize = _target.FontSize = m.FontSize;
                _folder.FontSize = m.SecondaryFontSize;
                CornerRadius = WindowMetrics.Corners(m.RowRadius);
            }
            _verb.Text = line.Verb.ToUpperInvariant();
            _name.Text = line.Name;
            _folder.Text = line.Folder;
            _target.Text = line.Target;
            _arrow.Visibility = _target.Visibility = line.HasTarget ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(_main, line.Description);
            _name.Foreground = ThemeResources.Brush("CbRowTextBrush");
            _folder.Foreground = ThemeResources.Brush("CbTextTertiaryBrush");
            _arrow.Foreground = ThemeResources.Brush("CbHintTextBrush");
            _target.Foreground = ThemeResources.Brush("CbAccentBrush");
            var error = ThemeResources.Brush("CbErrorTextBrush");
            _verb.Foreground = line.IsDelete ? error : ThemeResources.Brush("CbHintTextBrush");
            // A delete is tinted red: the error colour at about 12 % behind the whole row.
            Background = line.IsDelete && error is SolidColorBrush solid
                ? new SolidColorBrush(Color.FromArgb(0x20, solid.Color.R, solid.Color.G, solid.Color.B))
                : new SolidColorBrush(Colors.Transparent);
            AutomationProperties.SetName(this, line.Description);
        }
    }

    private sealed class RowFactory : IElementFactory
    {
        private readonly Stack<RowView> _pool = new();

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var row = _pool.Count > 0 ? _pool.Pop() : new RowView();
            if (args.Data is PreviewLine line)
            {
                row.Bind(line);
            }
            return row;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (args.Element is RowView row)
            {
                _pool.Push(row);
            }
        }
    }
}
