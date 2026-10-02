using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Prompts;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace CabinetOS.Views;

/// <summary>
/// A prompt in the palette's frame (docs/ui.md, "Total Commander's keys"): the
/// same Acrylic panel under the title bar, with a label, a box, a check box
/// when asked for, a few rows and a hint line. The pattern box (Num +, Num -)
/// asks for text and lists earlier patterns; the pinned folders list (Ctrl+D)
/// is a pick list that typing narrows. Up and Down move the highlight, Enter
/// takes it, Esc or a click outside cancels (the window's <c>overlay.close</c>).
/// </summary>
public sealed partial class PromptBox : UserControl
{
    private const string Target = "cabinetos_ui::prompt";

    private readonly Storyboard _entrance;
    private TaskCompletionSource<PromptResult?>? _pending;
    private PromptList _list = new(PromptKind.Text, []);
    private PromptKind _kind;
    private bool _showsInput = true;
    private bool _settingText;
    private string _listText = "";

    /// <summary>Creates the prompt, hidden.</summary>
    public PromptBox()
    {
        InitializeComponent();
        _entrance = (Storyboard)Resources["Entrance"];
        Input.TextChanged += (_, _) =>
        {
            if (!_settingText)
            {
                _list.SetText(Input.Text);
                _listText = Input.Text;
                BuildRows();
            }
        };
        // On the control, not the panel: a list without a box has the keyboard on the control itself.
        PreviewKeyDown += OnKeyDown;
        Scrim.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Scrim))
            {
                Cancel();
            }
        };
    }

    /// <summary>Whether a prompt is shown.</summary>
    public bool IsOpen => _pending is not null;

    /// <summary>Gives the keyboard back (to the pane) when the prompt closes.</summary>
    public Action? ReturnFocus { get; set; }

    /// <summary>A prompt is about to show: the window closes the other overlays first (one overlay at a time).</summary>
    public event Action? Opening;

    /// <summary>
    /// Shows <paramref name="request"/> and waits for the answer: the result,
    /// or null when the user cancelled. A prompt already shown is cancelled.
    /// With an <paramref name="anchor"/> the panel opens under it, narrower
    /// (the drive list under a pane's header); else under the title bar.
    /// </summary>
    public Task<PromptResult?> ShowAsync(PromptRequest request, FrameworkElement? anchor = null)
    {
        Opening?.Invoke();
        Cancel();
        var pending = new TaskCompletionSource<PromptResult?>();
        _pending = pending;
        _kind = request.Kind;
        _showsInput = request.ShowInput;
        _list = new PromptList(request.Kind, request.Rows);
        _listText = request.Text;
        LabelText.Text = request.Label;
        AutomationPropertiesName(request.Label);
        _settingText = true;
        Input.Text = request.Text;
        _settingText = false;
        Input.PlaceholderText = request.Placeholder;
        InputRow.Visibility = request.ShowInput ? Visibility.Visible : Visibility.Collapsed;
        RowsScroller.Padding = new Thickness(6, request.ShowInput ? 0 : 6, 6, 8);
        Option.Visibility = request.Option is null ? Visibility.Collapsed : Visibility.Visible;
        Option.Content = request.Option;
        Option.IsChecked = request.OptionChecked;
        HintText.Text = request.Hint;
        HintBar.Visibility = request.Hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Place(anchor);
        BuildRows();
        Visibility = Visibility.Visible;
        _entrance.Begin();
        if (request.ShowInput)
        {
            Input.Focus(FocusState.Programmatic);
            Input.SelectAll();
        }
        else
        {
            // No box: the prompt itself takes the keys (a letter, the arrows, Enter).
            IsTabStop = true;
            Focus(FocusState.Programmatic);
        }
        Diag.Info(Target, "prompt shown", new LogField("label", request.Label), new LogField("rows", request.Rows.Count));
        return pending.Task;
    }

    /// <summary>Presses <paramref name="key"/> in a list without a box, as a letter key would (the snapshot aid).</summary>
    public void PressKey(char key) => PickByKey(key);

    /// <summary>Puts the highlight on row <paramref name="index"/> (the drive the pane is on).</summary>
    public void Highlight(int index)
    {
        _list.SetHighlight(index);
        MarkHighlight();
    }

    private void Place(FrameworkElement? anchor)
    {
        if (anchor is null)
        {
            Panel.HorizontalAlignment = HorizontalAlignment.Center;
            Panel.Margin = new Thickness(0, 64, 0, 0);
            Panel.Width = 560;
            return;
        }
        var at = anchor.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
        Panel.HorizontalAlignment = HorizontalAlignment.Left;
        Panel.Width = Math.Min(360, Math.Max(240, anchor.ActualWidth));
        Panel.Margin = new Thickness(Math.Max(8, at.X), at.Y + anchor.ActualHeight + 4, 0, 0);
    }

    private void PickByKey(char key)
    {
        var index = _list.IndexOfKey(key);
        if (index >= 0)
        {
            _list.SetHighlight(index);
            Accept();
        }
    }

    /// <summary>Closes the prompt without an answer (Esc, a click outside, another overlay).</summary>
    public void Cancel() => Close(null);

    /// <summary>Takes the box's text and the highlighted row (Enter, the snapshot aid).</summary>
    public void Accept()
    {
        if (_kind == PromptKind.Pick && _list.Highlighted is null)
        {
            return;
        }
        Close(new PromptResult(Input.Text, _kind == PromptKind.Pick ? _list.Highlighted : null, Option.IsChecked == true));
    }

    /// <summary>Whether the rows follow <paramref name="text"/>: XAML has raised the box's text change for it (the snapshot aid waits for this after <see cref="Type"/>).</summary>
    public bool ListFollows(string text) => _listText == text;

    /// <summary>Puts text in the box as if typed (the snapshot aid).</summary>
    public void Type(string text)
    {
        Input.Text = text;
        Input.SelectionStart = text.Length;
    }

    private void AutomationPropertiesName(string label) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Input, label);

    private void Close(PromptResult? result)
    {
        if (_pending is not { } pending)
        {
            return;
        }
        _pending = null;
        // The keyboard goes back before the panel collapses, as the palette's does.
        ReturnFocus?.Invoke();
        IsTabStop = false;
        Visibility = Visibility.Collapsed;
        OpenToolTips.Close(XamlRoot);
        Diag.Info(Target, "prompt closed", new LogField("answered", result is not null));
        pending.TrySetResult(result);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                Move(1);
                break;
            case VirtualKey.Up:
                Move(-1);
                break;
            case VirtualKey.Enter:
                Accept();
                break;
            case VirtualKey.Tab:
                // The prompt keeps the keyboard while it is open.
                break;
            case >= VirtualKey.A and <= VirtualKey.Z when !_showsInput:
                // A list without a box: a letter picks its row at once (a drive, as in Total Commander).
                PickByKey((char)('A' + (e.Key - VirtualKey.A)));
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Move(int delta)
    {
        if (_list.Move(delta) is { } text)
        {
            _settingText = true;
            Input.Text = text;
            Input.SelectAll();
            _settingText = false;
        }
        MarkHighlight();
    }

    private void BuildRows()
    {
        Rows.Children.Clear();
        for (var i = 0; i < _list.Shown.Count; i++)
        {
            Rows.Children.Add(RowView(_list.Shown[i], i));
        }
        MarkHighlight();
    }

    // A row as the palette draws its own, as high as a menu's (32 px by default), the accent pill and the fill when highlighted.
    private Grid RowView(PromptRow row, int index)
    {
        var m = WindowMetrics.Current;
        var root = new Grid
        {
            Height = m.MenuRowHeight,
            CornerRadius = WindowMetrics.Corners(m.RadiusControl),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        root.Children.Add(new Rectangle
        {
            Width = m.SelectionBarWidth,
            Height = Math.Clamp(m.MenuRowHeight - 4, 0, 16),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = ThemeResources.Brush("CbAccentBrush"),
            RadiusX = m.SelectionBarWidth / 2,
            RadiusY = m.SelectionBarWidth / 2,
            Visibility = Visibility.Collapsed,
        });
        var content = new Grid { Padding = new Thickness(10, 0, 10, 0), ColumnSpacing = 10 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (row.Glyph.Length > 0)
        {
            content.Children.Add(new FontIcon { Glyph = row.Glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeResources.Brush("CbTextSecondaryBrush") });
        }
        var title = new TextBlock { Text = row.Title, FontSize = m.FontSize, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(title, 1);
        content.Children.Add(title);
        var detail = new TextBlock
        {
            Text = row.Detail,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeResources.Brush("CbTextTertiaryBrush"),
        };
        Grid.SetColumn(detail, 2);
        content.Children.Add(detail);
        root.Children.Add(content);
        root.PointerEntered += (_, _) =>
        {
            _list.SetHighlight(index);
            MarkHighlight();
        };
        root.Tapped += (_, _) =>
        {
            _list.SetHighlight(index);
            if (_kind == PromptKind.Text)
            {
                _settingText = true;
                Input.Text = row.Title;
                _settingText = false;
            }
            Accept();
        };
        return root;
    }

    private void MarkHighlight()
    {
        for (var i = 0; i < Rows.Children.Count; i++)
        {
            if (Rows.Children[i] is Grid { Children: [Rectangle pill, ..] } row)
            {
                var highlighted = i == _list.Highlight;
                row.Background = highlighted ? ThemeResources.Brush("CbSelectedFillBrush") : new SolidColorBrush(Colors.Transparent);
                pill.Visibility = highlighted ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
}
