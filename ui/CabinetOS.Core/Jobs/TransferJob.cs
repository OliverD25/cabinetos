using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Jobs;

/// <summary>
/// One job as the transfer flyout shows it (design view E): what it works on,
/// and the last state and progress the core sent. The UI computes nothing
/// about the work; it keeps the core's numbers and the last 40 speed samples
/// for the graph (the Dumb UI Rule, brief §1).
/// </summary>
public sealed class TransferJob
{
    /// <summary>Points of the speed graph: the design's 40 samples, one every 500 ms.</summary>
    public const int SampleCount = 40;

    private readonly double[] _speeds = new double[SampleCount];

    internal TransferJob(ulong id, long order)
    {
        Id = id;
        Order = order;
    }

    /// <summary>The core's job ID.</summary>
    public ulong Id { get; }

    /// <summary>When this UI first heard of the job; a higher number is newer.</summary>
    public long Order { get; }

    /// <summary>Copy, move or delete; null until the UI knows (a job another client started).</summary>
    public JobKind? Kind { get; private set; }

    /// <summary>The paths the job was started with.</summary>
    public IReadOnlyList<string> Sources { get; private set; } = [];

    /// <summary>The folder a copy or move goes into.</summary>
    public string? Destination { get; private set; }

    /// <summary>Whether the kind and the paths are known.</summary>
    public bool IsDescribed => Kind is not null;

    /// <summary>The state the core last reported.</summary>
    public JobState State { get; private set; } = new(JobState.Queued);

    /// <summary>The last <c>job_progress</c>, if any came.</summary>
    public JobProgressEvent? Progress { get; private set; }

    /// <summary>Whether the job has ended: no more events follow.</summary>
    public bool IsFinal => State.IsFinal;

    /// <summary>Whether the user closed it, or it ended quietly out of sight.</summary>
    public bool IsDismissed { get; internal set; }

    /// <summary>
    /// The speed samples, oldest first: bytes per second, or items per second
    /// for a job that counts items (<see cref="CountsItems"/>).
    /// </summary>
    public IReadOnlyList<double> Speeds => _speeds;

    /// <summary>
    /// Whether the job has no bytes to count (a delete, a move on one volume):
    /// its pace is items per second (<c>items_per_second</c>, protocol 11).
    /// </summary>
    public bool CountsItems => Progress is { BytesTotal: 0 };

    internal bool EndHandled { get; set; }

    internal void Describe(JobKind kind, IReadOnlyList<string> sources, string? destination)
    {
        Kind = kind;
        Sources = sources;
        Destination = destination;
    }

    internal void ApplyState(JobState state)
    {
        // A final state is final: an older snapshot cannot bring the job back.
        if (!IsFinal)
        {
            State = state;
        }
    }

    internal void ApplyProgress(JobProgressEvent progress)
    {
        if (IsFinal || (Progress is { } current && progress.ElapsedMs < current.ElapsedMs))
        {
            return;
        }
        Progress = progress;
        State = progress.State;
    }

    internal void Sample()
    {
        Array.Copy(_speeds, 1, _speeds, 0, SampleCount - 1);
        _speeds[SampleCount - 1] = State.Type != JobState.Running || Progress is not { } progress
            ? 0
            : CountsItems ? progress.ItemsPerSecond ?? 0 : progress.SpeedBps;
    }
}
