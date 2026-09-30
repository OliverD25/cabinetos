using CabinetOS.Core.Shell;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace CabinetOS.Views;

/// <summary>
/// Quick Open's overlay (Phase 16; docs/ui.md, "Quick Open"): the command
/// palette's surface with the workspace's files and folders. It shows the
/// <see cref="QuickOpenModel"/> and reports what the user does; the window
/// decides where a row opens and switches to the commands on <c>&gt;</c>.
/// </summary>
public sealed partial class QuickOpen : UserControl
{
    // A shorter wait than the Search view's 150 ms: Quick Open asks for 50 names, and the core ranks from its index.
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(80);

    private readonly Storyboard _entrance;
    private readonly DispatcherQueueTimer _searchTimer;
    private readonly List<Grid> _rows = [];
    private QuickOpenModel? _model;
    private IReadOnlyList<QuickOpenRow>? _shownRows;
    private int _shownHighlight = -1;

    // The text the window put in the box: TextBox raises TextChanged later, so the change it causes is known by its text.
    private string? _setText;

    /// <summary>Creates the overlay, hidden.</summary>
    public QuickOpen()
    {
        InitializeComponent();
        _entrance = (Storyboard)Resources["Entrance"];
        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = SearchDelay;
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => _ = _model?.SearchAsync(Input.Text);
        Input.TextChanged += (_, _) =>
        {
            var set = _setText;
            _setText = null;
            if (_model is { IsOpen: true } && set != Input.Text)
            {
                QueryChanged?.Invoke(Input.Text);
            }
        };
        Panel.PreviewKeyDown += OnPanelKeyDown;
        Scrim.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Scrim))
            {
                CloseRequested?.Invoke();
            }
        };
        SizeChanged += (_, e) => Panel.Width = Math.Max(200, Math.Min(640, e.NewSize.Width - 32));
    }

    /// <summary>Raised with the text as it is typed; the window reads a leading <c>&gt;</c> or calls <see cref="Search"/>.</summary>
    public event Action<string>? QueryChanged;

    /// <summary>A row is to open: in the active pane, or with Ctrl in the other one.</summary>
    public event Action<QuickOpenRow, bool>? OpenRequested;

    /// <summary>A click outside the panel.</summary>
    public event Action? CloseRequested;

    /// <summary>The state shown.</summary>
    public QuickOpenModel? Model
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
        }
    }

    /// <summary>Whether the overlay is shown.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>The box's text.</summary>
    public string Text => Input.Text;

    /// <summary>Shows the overlay with an empty box that has the keyboard.</summary>
    public void Show()
    {
        SetText("");
        _shownRows = null;
        Visibility = Visibility.Visible;
        _entrance.Begin();
        Render();
        Input.Focus(FocusState.Programmatic);
    }

    /// <summary>Hides the overlay; a search waiting to go out is dropped.</summary>
    public void Hide()
    {
        _searchTimer.Stop();
        Visibility = Visibility.Collapsed;
    }

    /// <summary>Sends the box's text once the keys pause.</summary>
    public void Search()
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    /// <summary>Puts text in the box as if typed (the snapshot aid).</summary>
    public void Type(string text)
    {
        Input.Text = text;
        Input.SelectionStart = text.Length;
    }

    /// <summary>
    /// Opens the highlighted row (Enter; with Ctrl in the other pane). The
    /// text as typed is searched first when its answer is not in yet, so
    /// Enter never opens a row of an older text.
    /// </summary>
    public async Task OpenHighlightedAsync(bool otherPane)
    {
        if (_model is null)
        {
            return;
        }
        if (_searchTimer.IsRunning || Input.Text != _model.Query)
        {
            _searchTimer.Stop();
            await _model.SearchAsync(Input.Text);
        }
        if (_model.Highlighted is { } row)
        {
            OpenRequested?.Invoke(row, otherPane);
        }
    }

    private void SetText(string text)
    {
        if (Input.Text != text)
        {
            _setText = text;
            Input.Text = text;
        }
    }

    private void OnPanelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }
        switch (e.Key)
        {
            case VirtualKey.Down:
                _model.MoveHighlight(1);
                break;
            case VirtualKey.Up:
                _model.MoveHighlight(-1);
                break;
            case VirtualKey.PageDown:
                _model.MoveHighlight(8);
                break;
            case VirtualKey.PageUp:
                _model.MoveHighlight(-8);
                break;
            case VirtualKey.Enter:
                _ = OpenHighlightedAsync(IsCtrlDown());
                break;
            case VirtualKey.Tab:
                // Quick Open keeps the keyboard while it is open, as the palette does.
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private static bool IsCtrlDown() =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;

    // The rows are made again when the model has new ones (at most 50); a new highlight only repaints two of them.
    private void Render()
    {
        if (_model is null)
        {
            return;
        }
        CountText.Text = _model.CountText;
        if (!ReferenceEquals(_shownRows, _model.Rows))
        {
            _shownRows = _model.Rows;
            _shownHighlight = -1;
            List.Children.Clear();
            _rows.Clear();
            for (var i = 0; i < _model.Rows.Count; i++)
            {
                var row = MakeRow(_model.Rows[i], i);
                _rows.Add(row);
                List.Children.Add(row);
            }
            if (_model.Rows.Count == 0 && PaletteInput.FileQuery(_model.Query) is null)
            {
                List.Children.Add(new TextBlock
                {
                    Margin = new Thickness(10, 4, 10, 6),
                    FontSize = 12,
                    Foreground = ThemeResources.Brush("CbTextTertiaryBrush"),
                    Text = _model.Root.Length > 0 ? $"Finds files and folders in {_model.Root}" : "Finds files and folders on the indexed volumes",
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
        }
        if (_shownHighlight != _model.Highlight)
        {
            Paint(_shownHighlight, highlighted: false);
            _shownHighlight = _model.Highlight;
            Paint(_shownHighlight, highlighted: true);
            if (_shownHighlight >= 0 && _shownHighlight < _rows.Count)
            {
                _rows[_shownHighlight].StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            }
        }
    }

    private void Paint(int index, bool highlighted)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }
        var row = _rows[index];
        row.Background = highlighted ? ThemeResources.Brush("CbSelectedFillBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        row.Children[0].Visibility = highlighted ? Visibility.Visible : Visibility.Collapsed;
    }

    private Grid MakeRow(QuickOpenRow item, int index)
    {
        var row = new Grid
        {
            Height = 32,
            Padding = new Thickness(0, 0, 10, 0),
            ColumnSpacing = 10,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 320 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // The accent bar of the highlighted row, as the palette's rows have it.
        row.Children.Add(new Rectangle
        {
            Width = 3,
            Height = 16,
            RadiusX = 1.5,
            RadiusY = 1.5,
            Fill = ThemeResources.Brush("CbAccentBrush"),
            Visibility = Visibility.Collapsed,
        });
        var icon = new FontIcon
        {
            FontSize = 14,
            Glyph = item.IsFolder ? "" : "",
            Foreground = ThemeResources.Brush(item.IsFolder ? "CbFolderBrush" : "CbTextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 1);
        row.Children.Add(icon);
        var name = new TextBlock
        {
            FontSize = 13,
            Text = item.Name,
            Foreground = ThemeResources.Brush("CbTextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 2);
        row.Children.Add(name);
        // The folder it is in, in the mono font (SHELL_REDESIGN.md §3).
        var folder = new TextBlock
        {
            FontSize = 11,
            FontFamily = (FontFamily)Application.Current.Resources["CbMonoFont"],
            Text = item.Folder,
            Foreground = ThemeResources.Brush("CbTextTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(folder, 3);
        row.Children.Add(folder);
        AutomationProperties.SetName(row, item.Folder.Length > 0 ? $"{item.Name}, in {item.Folder}" : item.Name);
        ToolTipService.SetToolTip(row, item.Path);
        row.PointerEntered += (_, _) => _model?.SetHighlight(index);
        row.Tapped += (_, _) =>
        {
            if (_model is not null && index < _model.Rows.Count)
            {
                OpenRequested?.Invoke(_model.Rows[index], IsCtrlDown());
            }
        };
        return row;
    }
}
