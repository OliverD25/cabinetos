using System.ComponentModel;
using System.Text.Json;
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
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(30);

    private readonly Storyboard _entrance;
    private readonly DispatcherQueueTimer _searchTimer;
    private PaletteModel? _model;
    private bool _settingText;

    /// <summary>Creates the palette, hidden.</summary>
    public CommandPalette()
    {
        InitializeComponent();
        _entrance = (Storyboard)Resources["Entrance"];
        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = SearchDelay;
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => _ = _model?.SearchAsync(Input.Text);
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
        if (_model is { HighlightIndex: >= 0 } model && model.HighlightIndex < model.Rows.Count)
        {
            List.GetOrCreateElement(model.HighlightIndex).StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
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
                // Run what the core ranked for the text as typed, not a stale list.
                _searchTimer.Stop();
                _ = RunHighlightedAsync();
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
