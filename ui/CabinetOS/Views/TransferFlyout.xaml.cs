using System.Numerics;
using System.Text.Json;
using CabinetOS.Core.Jobs;
using CabinetOS.Core.Protocol;
using CabinetOS.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace CabinetOS.Views;

/// <summary>
/// The file operations flyout (design view E), bottom right: one job's title,
/// "source → destination", the speed graph, the progress bar and its footer,
/// and the conflict card when a file waits for a decision. It draws what the
/// <see cref="TransferCenter"/> holds, which is what the core sent; every
/// button runs a command through the router. It never takes the keyboard
/// focus, so F5 and the arrows keep working in the pane.
/// </summary>
public sealed partial class TransferFlyout : UserControl
{
    private readonly Storyboard _entrance;
    private TransferCenter? _center;
    private bool _wasOpen;
    private ulong? _shownConflict;

    /// <summary>Creates the flyout, hidden.</summary>
    public TransferFlyout()
    {
        InitializeComponent();
        _entrance = (Storyboard)Resources["Entrance"];
        // The design's 400 ms width transition, on the compositor: the bar never stutters with the UI thread.
        ProgressFill.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(400) };
        ProgressFill.Scale = new Vector3(0, 1, 1);
        ProgressFill.SizeChanged += (_, e) => ProgressFill.CenterPoint = new Vector3(0, (float)(e.NewSize.Height / 2), 0);
        Graph.SizeChanged += (_, _) => DrawGraph();
        MinimizeButton.Click += (_, _) => Run("transfer.minimize");
        MoreLink.Click += (_, _) => Run("transfer.next");
        PauseButton.Click += (_, _) => Run(_center?.Shown?.State.Type == JobState.Paused ? "transfer.resume" : "transfer.pause");
        EndButton.Click += (_, _) => Run(_center?.Shown?.IsFinal == true ? "transfer.close" : "transfer.cancel");
        CancelJobButton.Click += (_, _) => Resolve(Resolution.CancelJobType);
    }

    /// <summary>Runs a command through the window's router: (command, arguments, trigger).</summary>
    public Func<string, JsonElement?, string, Task>? RunCommand { get; set; }

    /// <summary>The jobs to show.</summary>
    public TransferCenter? Center
    {
        get => _center;
        set
        {
            if (_center is not null)
            {
                _center.Changed -= Render;
            }
            _center = value;
            if (_center is not null)
            {
                _center.Changed += Render;
            }
            Render();
        }
    }

    private void Render()
    {
        if (_center is not { IsFlyoutOpen: true, Shown: { } job } center)
        {
            Visibility = Visibility.Collapsed;
            _wasOpen = false;
            return;
        }
        Visibility = Visibility.Visible;
        if (!_wasOpen)
        {
            _wasOpen = true;
            _entrance.Begin();
        }

        TitleText.Text = TransferText.Title(job);
        var subtitle = TransferText.Subtitle(job);
        SubtitleText.Text = subtitle;
        ToolTipService.SetToolTip(SubtitleText, subtitle);
        var others = center.OthersCount;
        MoreLink.Visibility = others > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreLink.Content = $"{others} more";

        SpeedText.Text = TransferText.SpeedText(job);
        DrawGraph();
        ProgressFill.Scale = new Vector3((float)TransferText.Fraction(job), 1, 1);
        PercentText.Text = TransferText.PercentText(job);
        DoneText.Text = TransferText.DoneText(job);
        EtaText.Text = TransferText.EtaText(job);

        var pause = TransferText.PauseLabel(job);
        PauseButton.Visibility = pause is null ? Visibility.Collapsed : Visibility.Visible;
        PauseButton.Content = pause;
        EndButton.Content = TransferText.EndLabel(job);
        // Cancel warns on hover (the design's red); Close after the end does not.
        var endStyle = job.IsFinal ? (Style)Resources["FooterButtonStyle"] : (Style)ThemeResources.Get("CbDangerButtonStyle")!;
        if (!ReferenceEquals(EndButton.Style, endStyle))
        {
            EndButton.Style = endStyle;
        }

        RenderConflict(center, job);
    }

    private void RenderConflict(TransferCenter center, TransferJob job)
    {
        var conflict = center.Conflicts.Current;
        if (conflict is null || conflict.JobId != job.Id)
        {
            ConflictCard.Visibility = Visibility.Collapsed;
            _shownConflict = null;
            return;
        }
        ConflictCard.Visibility = Visibility.Visible;
        ConflictCount.Text = center.Conflicts.Count > 1 ? $"1 of {center.Conflicts.Count} waiting" : "";
        if (_shownConflict == conflict.ConflictId)
        {
            return;
        }
        _shownConflict = conflict.ConflictId;
        ConflictName.Text = ConflictText.Name(conflict);
        ToolTipService.SetToolTip(ConflictName, conflict.Source);
        ConflictKindText.Text = ConflictText.KindText(conflict);
        if (ConflictText.Sides(conflict, DateTime.Now) is { } sides)
        {
            NewSideText.Text = sides.New;
            ExistingSideText.Text = sides.Existing;
            ConflictSides.Visibility = Visibility.Visible;
        }
        else
        {
            ConflictSides.Visibility = Visibility.Collapsed;
        }

        var options = ConflictText.Options(conflict.Kind);
        ConflictButtons.Children.Clear();
        foreach (var option in options.Where(o => o != Resolution.CancelJobType))
        {
            var button = new Button
            {
                Content = ConflictText.Label(option),
                Style = (Style)Resources["CardButtonStyle"],
            };
            if (option == Resolution.RenameType)
            {
                ToolTipService.SetToolTip(button, "Keep both: the new one gets a free name, such as \"name (2).txt\"");
            }
            button.Click += (_, _) => Resolve(option);
            ConflictButtons.Children.Add(button);
        }
        CancelJobButton.Visibility = options.Contains(Resolution.CancelJobType) ? Visibility.Visible : Visibility.Collapsed;
        ApplyToAll.IsChecked = false;
    }

    private void Resolve(string resolution)
    {
        if (_center?.Conflicts.Current is not { } conflict)
        {
            return;
        }
        Run("conflict.resolve", CommandArgs.Object(
            ("conflict_id", conflict.ConflictId),
            ("resolution", resolution),
            ("apply_to_same_kind", ApplyToAll.IsChecked == true)));
    }

    private void DrawGraph()
    {
        if (_center?.Shown is not { } job || Graph.ActualWidth <= 0)
        {
            return;
        }
        var points = TransferText.Graph(job.Speeds, Graph.ActualWidth, Graph.ActualHeight);
        var line = new PointCollection();
        var area = new PointCollection { new Point(0, Graph.ActualHeight) };
        foreach (var (x, y) in points)
        {
            line.Add(new Point(x, y));
            area.Add(new Point(x, y));
        }
        area.Add(new Point(Graph.ActualWidth, Graph.ActualHeight));
        GraphLine.Points = line;
        GraphFill.Points = area;
    }

    private void Run(string commandId, JsonElement? args = null) => _ = RunCommand?.Invoke(commandId, args, "flyout");
}
