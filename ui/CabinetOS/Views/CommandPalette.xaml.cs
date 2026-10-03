using System.ComponentModel;
using System.Text.Json;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Shell;
using CabinetOS.Services;
using CabinetOS.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace CabinetOS.Views;

/// <summary>
/// The command palette overlay (design view B): a transparent full-window
/// scrim with the Acrylic panel. The core ranks commands as the user types
/// (after 30 ms of quiet); the chosen one runs through the command router.
/// </summary>
public sealed partial class CommandPalette : UserControl
{
    private const string Target = "cabinetos_ui::palette";
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(30);

    private readonly Storyboard _entrance;
    private readonly DispatcherQueueTimer _searchTimer;
    private PaletteModel? _model;
    private bool _settingText;
    private bool _highlightPending;

    /// <summary>Creates the palette, hidden.</summary>
    public CommandPalette()
    {
        InitializeComponent();
        _entrance = (Storyboard)Resources["Entrance"];
        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = SearchDelay;
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) =>
        {
            if (_model is { } model)
            {
                Diag.Observe(model.SearchAsync(Input.Text), Target, "the palette's search for the typed text failed");
            }
        };
        Input.TextChanged += (_, _) =>
        {
            if (!_settingText)
            {
                _searchTimer.Stop();
                _searchTimer.Start();
            }
        };
        Panel.PreviewKeyDown += OnPanelKeyDown;
        Scrim.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Scrim))
            {
                _ = RunCommand?.Invoke("overlay.close", null, "mouse");
            }
        };
        List.ElementPrepared += OnElementPrepared;
        List.ElementClearing += OnElementClearing;
        // The list joins the window's live tree at the palette's first layout: a highlight set before then comes into view now.
        List.Loaded += (_, _) =>
        {
            if (_highlightPending)
            {
                BringHighlightIntoView();
            }
        };
        SizeChanged += (_, e) => Panel.Width = Math.Max(200, Math.Min(640, e.NewSize.Width - 32));
    }

    /// <summary>Runs a command through the window's router: (command, args, trigger).</summary>
    public Func<string, JsonElement?, string, Task>? RunCommand { get; set; }

    /// <summary>Backspace in the empty box: back to Quick Open's files (Phase 16; PaletteInput.AfterBackspace).</summary>
    public event Action? FilesRequested;

    /// <summary>Gives the keyboard back (to the pane, or the shell it came from) when the palette closes.</summary>
    public Action? ReturnFocus { get; set; }

    /// <summary>The palette's state.</summary>
    public PaletteModel? Model
    {
        get => _model;
        set
        {
            if (_model is not null)
            {
                _model.PropertyChanged -= OnModelChanged;
                _model.Opened -= OnOpened;
                _model.Closed -= OnClosed;
            }
            _model = value;
            List.ItemsSource = value?.Rows;
            if (_model is not null)
            {
                _model.PropertyChanged += OnModelChanged;
                _model.Opened += OnOpened;
                _model.Closed += OnClosed;
            }
        }
    }

    /// <summary>Puts text in the input as if typed (development snapshots).</summary>
    public void TypeQuery(string text)
    {
        Input.Text = text;
        Input.SelectionStart = text.Length;
    }

    private void OnOpened()
    {
        _settingText = true;
        Input.Text = "";
        _settingText = false;
        Visibility = Visibility.Visible;
        _entrance.Begin();
        Input.Focus(FocusState.Programmatic);
    }

    private void OnClosed()
    {
        _searchTimer.Stop();
        // The keyboard goes back before the panel collapses: a collapsing focused input
        // hands it to whatever comes next, maybe the other pane, which then becomes active.
        ReturnFocus?.Invoke();
        Visibility = Visibility.Collapsed;
        OpenToolTips.Close(XamlRoot);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PaletteModel.CountText):
                CountText.Text = _model?.CountText ?? "";
                break;
            case nameof(PaletteModel.HighlightIndex):
                BringHighlightIntoView();
                break;
            case nameof(PaletteModel.IsRecording):
                // Keys go to the recorder; letters must not also land in the query.
                Input.IsReadOnly = _model?.IsRecording == true;
                break;
        }
    }

    private void BringHighlightIntoView()
    {
        // WinUI's ItemsRepeater can hand out a row only while it is in the window's live tree. The palette starts collapsed,
        // and its list is the content of a ScrollViewer whose template WinUI applies at the palette's first layout; until
        // then the list is not live. VisualTreeHelper.GetParent answers null for a row there, so the repeater takes a row it
        // recycled (every answer clears the rows) for one without a parent and adds it to its children a second time:
        // GetOrCreateElement threw "Element is already the child of another element" when a second answer, or an answer
        // and a key, came before that first layout (2026-10-03). The list's Loaded comes once it is live.
        _highlightPending = !List.IsLoaded;
        if (_highlightPending)
        {
            return;
        }
        if (_model is { HighlightIndex: >= 0 } model && model.HighlightIndex < model.Rows.Count)
        {
            var row = List.GetOrCreateElement(model.HighlightIndex);
            // A row the list makes for this call (outside the rows it has laid out, as at its first layout) is measured but
            // not laid out. StartBringIntoView's target is the render size, 0 x 0 for that row, and the row stayed outside the
            // visible part (the palette-burst test); its measured size is the row's own.
            var size = row.RenderSize.Height > 0 ? row.RenderSize : row.DesiredSize;
            row.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = false,
                TargetRect = new Windows.Foundation.Rect(0, 0, size.Width, size.Height),
            });
        }
    }

    /// <summary>
    /// The snapshot aid's look at the list: whether it is in the window's live tree, where the highlighted
    /// row lies in the list's visible part (NaN when the list has no laid-out row for it), the visible
    /// part's height and the scroll offset, and whether the row lies whole inside (1 px for rounding).
    /// </summary>
    internal (bool ListLoaded, double RowTop, double RowBottom, double Viewport, double Offset, bool HighlightShown) HighlightState()
    {
        var viewport = ListScroller.ViewportHeight;
        var offset = ListScroller.VerticalOffset;
        if (!List.IsLoaded || _model is not { HighlightIndex: >= 0 } model
            || List.TryGetElement(model.HighlightIndex) is not FrameworkElement { ActualHeight: > 0 } row)
        {
            return (List.IsLoaded, double.NaN, double.NaN, viewport, offset, false);
        }
        var bounds = row.TransformToVisual(ListScroller).TransformBounds(new Windows.Foundation.Rect(0, 0, row.ActualWidth, row.ActualHeight));
        return (true, bounds.Top, bounds.Bottom, viewport, offset, bounds.Top >= -1 && bounds.Bottom <= viewport + 1);
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
                // Run what the core ranked for the text as typed, not a stale list.
                _searchTimer.Stop();
                Diag.Observe(RunHighlightedAsync(), Target, "running the palette's highlighted command failed");
                break;
            case VirtualKey.Tab:
                // The palette keeps the focus while it is open.
                break;
            case VirtualKey.Back when !_model.IsRecording
                && PaletteInput.AfterBackspace(PaletteMode.Commands, Input.Text) == PaletteMode.Files && Input.SelectionLength == 0:
                FilesRequested?.Invoke();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private async Task RunHighlightedAsync()
    {
        if (_model is null)
        {
            return;
        }
        if (Input.Text != _model.Query)
        {
            await _model.SearchAsync(Input.Text);
        }
        await _model.RunHighlightedAsync();
    }

    private Task Rebind(PaletteRow row, string trigger) =>
        RunCommand?.Invoke("keys.rebind", CommandArgs.With("command", row.Info.Id), trigger) ?? Task.CompletedTask;

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is PaletteRowView row)
        {
            row.Hovered += OnRowHovered;
            row.RunRequested += OnRowRun;
            row.RebindRequested += OnRowRebind;
        }
    }

    private void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is PaletteRowView row)
        {
            row.Hovered -= OnRowHovered;
            row.RunRequested -= OnRowRun;
            row.RebindRequested -= OnRowRebind;
        }
    }

    private void OnRowHovered(PaletteRow row)
    {
        if (_model is not null && !_model.IsRecording)
        {
            _model.SetHighlight(_model.Rows.IndexOf(row));
        }
    }

    private void OnRowRun(PaletteRow row) => _ = _model?.RunAsync(row);

    private void OnRowRebind(PaletteRow row) => _ = Rebind(row, "mouse");
}
