using System.ComponentModel;
using System.Diagnostics;
using CabinetOS.Core.Diagnostics;
using CabinetOS.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace CabinetOS.Views;

/// <summary>
/// One file pane (design view A): header, column headings, and the rows in a
/// virtualizing <c>ItemsRepeater</c>. Only the rows on screen exist. Keys the
/// window's keymap does not claim arrive here: the arrows move the selection
/// like in any list, and Enter and Backspace run commands through the router.
/// </summary>
public sealed partial class FilePane : UserControl
{
    private const string Target = "cabinetos_ui::pane";
    private const double RowHeight = 30;
    private const double ListPadding = 4;

    private PaneModel? _model;
    private int _markedIndex = -1;
    private NavigationTiming? _timing;
    private long _firstRowTicks;
    private bool _renderingHooked;

    /// <summary>Creates the pane; <see cref="Model"/> gives it its content.</summary>
    public FilePane()
    {
        InitializeComponent();
        Repeater.ElementPrepared += OnElementPrepared;
        Repeater.ElementClearing += OnElementClearing;
        Repeater.Tapped += OnRowTapped;
        Repeater.DoubleTapped += OnRowDoubleTapped;
        KeyDown += OnKeyDown;
        GotFocus += (_, _) => Activated?.Invoke(this);
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => Focus(FocusState.Pointer)), handledEventsToo: true);
        Header.SizeChanged += (_, e) => PathText.MaxWidth = Math.Max(0, e.NewSize.Width * 0.45);
    }

    /// <summary>Raised when the pane gets the focus: it becomes the active pane.</summary>
    public event Action<FilePane>? Activated;

    /// <summary>Runs a command by ID through the window's router: (command, trigger).</summary>
    public Func<string, string, Task>? RunCommand { get; set; }

    /// <summary>The pane's model.</summary>
    public PaneModel? Model
    {
        get => _model;
        set
        {
            if (_model is not null)
            {
                _model.PropertyChanged -= OnModelChanged;
            }
            _model = value;
            if (_model is not null)
            {
                _model.PropertyChanged += OnModelChanged;
            }
            UpdateHeader();
            UpdateActivity();
            ApplyRows();
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PaneModel.Rows):
                ApplyRows();
                break;
            case nameof(PaneModel.SelectedIndex):
                MarkSelection();
                break;
            case nameof(PaneModel.Path):
                UpdateHeader();
                break;
            case nameof(PaneModel.ShowsActiveStroke) or nameof(PaneModel.HasBrightTitle):
                UpdateActivity();
                break;
            case nameof(PaneModel.Message):
                UpdateMessage();
                break;
        }
    }

    private void UpdateHeader()
    {
        TitleText.Text = _model?.FolderName ?? "";
        PathText.Text = _model?.Path ?? "";
    }

    private void UpdateActivity()
    {
        var state = _model switch
        {
            { ShowsActiveStroke: true } => "Active",
            { HasBrightTitle: true } => "OnlyPane",
            _ => "Inactive",
        };
        VisualStateManager.GoToState(this, state, false);
    }

    private void UpdateMessage()
    {
        MessageText.Text = _model?.Message ?? "";
        MessageText.Visibility = string.IsNullOrEmpty(_model?.Message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyRows()
    {
        _markedIndex = -1;
        _timing = _model?.PendingTiming;
        if (_model is not null)
        {
            _model.PendingTiming = null;
        }
        _firstRowTicks = 0;
        Repeater.ItemsSource = _model?.Rows;
        UpdateMessage();
        if (_timing is not null && !_renderingHooked)
        {
            CompositionTarget.Rendering += OnFirstFrame;
            _renderingHooked = true;
        }
        MarkSelection();
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is FileRow row)
        {
            row.IsSelected = args.Index == _model?.SelectedIndex;
        }
        if (_timing is not null && _firstRowTicks == 0)
        {
            _firstRowTicks = Stopwatch.GetTimestamp();
        }
    }

    private void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is FileRow row)
        {
            row.IsSelected = false;
        }
    }

    // Logs "listing shown" at the first frame after the first row exists: from
    // the key press (or the start of navigation) to pixels, for Phase 5's check.
    private void OnFirstFrame(object? sender, object e)
    {
        if (_timing is not { } timing)
        {
            Unhook();
            return;
        }
        if (_firstRowTicks == 0 && (_model?.Count ?? 0) > 0)
        {
            return;
        }
        var now = Stopwatch.GetTimestamp();
        Diag.Request(LogLevel.Info, timing.RequestId, Target, "listing shown",
            new LogField("path", timing.Path),
            new LogField("entries", timing.Entries),
            new LogField("core_us", timing.CoreUs),
            new LogField("reply_ms", Math.Round(Stopwatch.GetElapsedTime(timing.StartedTicks, timing.RepliedTicks).TotalMilliseconds, 1)),
            new LogField("first_row_ms", _firstRowTicks == 0 ? null : Math.Round(Stopwatch.GetElapsedTime(timing.StartedTicks, _firstRowTicks).TotalMilliseconds, 1)),
            new LogField("first_frame_ms", Math.Round(Stopwatch.GetElapsedTime(timing.StartedTicks, now).TotalMilliseconds, 1)));
        _timing = null;
        Unhook();

        void Unhook()
        {
            CompositionTarget.Rendering -= OnFirstFrame;
            _renderingHooked = false;
        }
    }

    private void MarkSelection()
    {
        var selected = _model?.SelectedIndex ?? -1;
        if (_markedIndex >= 0 && Repeater.TryGetElement(_markedIndex) is FileRow previous)
        {
            previous.IsSelected = false;
        }
        if (selected >= 0 && Repeater.TryGetElement(selected) is FileRow current)
        {
            current.IsSelected = true;
        }
        _markedIndex = selected;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }
        var alt = IsDown(VirtualKey.Menu);
        var handled = true;
        switch (e.Key)
        {
            case VirtualKey.Left when alt:
                _ = Run("go.back");
                break;
            case VirtualKey.Right when alt:
                _ = Run("go.forward");
                break;
            case VirtualKey.Up when alt:
                _ = Run("go.up");
                break;
            case VirtualKey.Up:
                MoveSelection(_model.SelectedIndex - 1);
                break;
            case VirtualKey.Down:
                MoveSelection(_model.SelectedIndex + 1);
                break;
            case VirtualKey.Home:
                MoveSelection(0);
                break;
            case VirtualKey.End:
                MoveSelection(_model.Count - 1);
                break;
            case VirtualKey.PageUp:
                MoveSelection(_model.SelectedIndex - RowsPerPage());
                break;
            case VirtualKey.PageDown:
                MoveSelection(_model.SelectedIndex + RowsPerPage());
                break;
            case VirtualKey.Enter:
                _ = Run("pane.openSelected");
                break;
            case VirtualKey.Back:
                _ = Run("go.up");
                break;
            default:
                handled = false;
                break;
        }
        e.Handled = handled;
    }

    private Task Run(string commandId) => RunCommand?.Invoke(commandId, "key") ?? Task.CompletedTask;

    private void MoveSelection(int index)
    {
        if (_model is null || _model.Count == 0)
        {
            return;
        }
        _model.SelectedIndex = index;
        ScrollIntoView(_model.SelectedIndex);
    }

    private int RowsPerPage() => Math.Max(1, (int)((Scroller.ViewportHeight - (2 * ListPadding)) / RowHeight) - 1);

    private void ScrollIntoView(int index)
    {
        var top = ListPadding + (index * RowHeight);
        var bottom = top + RowHeight;
        var offset = Scroller.VerticalOffset;
        var viewport = Scroller.ViewportHeight;
        if (top - ListPadding < offset)
        {
            Scroller.ChangeView(null, Math.Max(0, top - ListPadding), null, disableAnimation: true);
        }
        else if (bottom + ListPadding > offset + viewport)
        {
            Scroller.ChangeView(null, bottom + ListPadding - viewport, null, disableAnimation: true);
        }
    }

    private void OnRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (RowFrom(e.OriginalSource) is { Index: >= 0 } row && _model is not null)
        {
            _model.SelectedIndex = row.Index;
        }
    }

    private void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RowFrom(e.OriginalSource) is { Index: >= 0 } row && _model is not null)
        {
            _model.SelectedIndex = row.Index;
            _ = RunCommand?.Invoke("pane.openSelected", "mouse");
        }
    }

    private FileRow? RowFrom(object source)
    {
        for (var element = source as DependencyObject; element is not null && element != Repeater; element = VisualTreeHelper.GetParent(element))
        {
            if (element is FileRow row)
            {
                return row;
            }
        }
        return null;
    }

    private static bool IsDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
}
