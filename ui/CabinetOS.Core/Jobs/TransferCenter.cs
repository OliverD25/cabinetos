using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Jobs;

/// <summary>How <see cref="TransferCenter.StartAsync"/> ended: the job, or the core's refusal.</summary>
public sealed record JobStart(TransferJob? Job, string? ErrorCode, string? ErrorMessage)
{
    /// <summary>Whether the core queued the job.</summary>
    public bool Started => Job is not null;
}

/// <summary>
/// The UI's side of the job engine (docs/jobs.md): starts jobs, follows
/// <c>job_progress</c>, <c>job_state_changed</c> and <c>job_conflict</c>, and
/// decides what the transfer flyout and the status-bar pill show. Jobs belong
/// to the core, so this also shows jobs other clients started (learned with
/// <c>list_jobs</c>). Used on one thread, the UI thread.
/// </summary>
/// <remarks>
/// The flyout shows one job at a time: the newest one, or the one a conflict
/// is about. Its "N more" link cycles through the others. A job that ends
/// quietly (completed or cancelled) out of sight leaves by itself; one that
/// ends with errors or fails stays until the user closes it.
/// </remarks>
public sealed class TransferCenter(ICoreChannel core)
{
    private const string Target = "cabinetos_ui::jobs";

    private readonly Dictionary<ulong, TransferJob> _jobs = [];
    private long _order;
    private bool _learning;

    /// <summary>Raised after anything the flyout or the pill shows changed.</summary>
    public event Action? Changed;

    /// <summary>The conflicts that wait for a decision.</summary>
    public ConflictQueue Conflicts { get; } = new();

    /// <summary>The job the flyout (or the pill) shows, or null.</summary>
    public TransferJob? Shown { get; private set; }

    /// <summary>Whether the flyout is folded into the status-bar pill.</summary>
    public bool IsMinimized { get; private set; }

    /// <summary>Whether the flyout is on screen.</summary>
    public bool IsFlyoutOpen => Shown is not null && !IsMinimized;

    /// <summary>Whether the status-bar pill is on screen.</summary>
    public bool IsPillVisible => Shown is not null && IsMinimized;

    /// <summary>The jobs the flyout can show, newest first.</summary>
    public IReadOnlyList<TransferJob> Visible =>
        _jobs.Values.Where(j => !j.IsDismissed).OrderByDescending(j => j.Order).ToList();

    /// <summary>How many other jobs the "N more" link cycles through.</summary>
    public int OthersCount => _jobs.Values.Count(j => !j.IsDismissed && j != Shown);

    /// <summary>The job with <paramref name="jobId"/>, if this UI knows it.</summary>
    public TransferJob? Find(ulong jobId) => _jobs.GetValueOrDefault(jobId);

    /// <summary>
    /// Starts a job (<c>start_job</c>) under <paramref name="requestId"/>, the
    /// command's ULID, so the key press and the core's work share one ID in
    /// both logs. A started job opens the flyout.
    /// </summary>
    public async Task<JobStart> StartAsync(JobKind kind, IReadOnlyList<string> sources, string? destination, string? requestId = null)
    {
        var request = new StartJobRequest(kind, sources) { Destination = destination, Id = requestId ?? "" };
        CoreReply reply;
        try
        {
            reply = await core.RequestAsync(request);
        }
        catch (IOException error)
        {
            return new JobStart(null, "disconnected", error.Message);
        }
        switch (reply)
        {
            case JobStartedReply started:
                var job = GetOrCreate(started.JobId, out _);
                job.Describe(kind, sources, destination);
                job.IsDismissed = false;
                ShowInstead(job);
                IsMinimized = false;
                Diag.Request(LogLevel.Info, request.Id, Target, "job started",
                    new LogField("job_id", started.JobId), new LogField("kind", kind.Type),
                    new LogField("sources", sources.Count), new LogField("destination", destination));
                Raise();
                return new JobStart(job, null, null);
            case ErrorReply error:
                Diag.Request(LogLevel.Info, request.Id, Target, "job refused",
                    new LogField("kind", kind.Type), new LogField("code", error.Code), new LogField("error", error.Message));
                return new JobStart(null, error.Code, error.Message);
            default:
                return new JobStart(null, "protocol", $"unexpected reply {reply.GetType().Name}");
        }
    }

    /// <summary>
    /// Reads the core's jobs (<c>list_jobs</c>): at start, a running copy of an
    /// earlier session shows in the pill; later, it names jobs this UI only
    /// heard of through events.
    /// </summary>
    public async Task LoadAsync()
    {
        CoreReply reply;
        try
        {
            reply = await core.RequestAsync(new ListJobsRequest());
        }
        catch (IOException error)
        {
            Diag.Info(Target, "cannot list the jobs", new LogField("error", error.Message));
            return;
        }
        if (reply is not JobsReply jobs)
        {
            return;
        }
        foreach (var info in jobs.Jobs)
        {
            if (!_jobs.TryGetValue(info.JobId, out var job))
            {
                // The core keeps its last 100 finished jobs; old ones are not news.
                if (info.State.IsFinal)
                {
                    continue;
                }
                job = GetOrCreate(info.JobId, out _);
            }
            job.Describe(info.Kind, info.Sources, info.Destination);
            job.ApplyProgress(info.ToProgress());
            if (job.IsFinal)
            {
                End(job);
            }
            else if (Shown is null)
            {
                Shown = job;
                IsMinimized = true;
            }
        }
        Raise();
    }

    /// <summary>Takes a core event; returns whether it was a job event.</summary>
    public bool OnEvent(CoreEvent coreEvent)
    {
        switch (coreEvent)
        {
            case JobProgressEvent progress:
            {
                var job = Track(progress.JobId);
                job.ApplyProgress(progress);
                if (job.IsFinal)
                {
                    End(job);
                }
                break;
            }
            case JobStateChangedEvent changed:
            {
                var job = Track(changed.JobId);
                job.ApplyState(changed.State);
                if (job.IsFinal)
                {
                    End(job);
                }
                break;
            }
            case JobConflictEvent conflict:
            {
                // An ended job waits for no decision: such a conflict came late, and the
                // core would refuse any answer to it.
                if (Find(conflict.JobId) is { IsFinal: true } || !Conflicts.Add(conflict))
                {
                    return true;
                }
                Track(conflict.JobId);
                // A decision is needed: the flyout opens, on the job whose conflict is first in line.
                IsMinimized = false;
                FollowConflict();
                break;
            }
            default:
                return false;
        }
        Raise();
        return true;
    }

    /// <summary>Takes one speed sample of every running job (every 500 ms).</summary>
    public void SampleSpeeds()
    {
        var any = false;
        foreach (var job in _jobs.Values.Where(j => !j.IsFinal && !j.IsDismissed))
        {
            job.Sample();
            any = true;
        }
        if (any)
        {
            Raise();
        }
    }

    /// <summary>Pauses, resumes or cancels a job; returns the core's refusal, or null.</summary>
    public async Task<string?> ControlAsync(ulong jobId, string action, string? requestId = null)
    {
        try
        {
            var reply = await core.RequestAsync(new JobControlRequest(jobId, action) { Id = requestId ?? "" });
            return reply switch
            {
                OkReply => null,
                ErrorReply error => error.Message,
                _ => $"unexpected reply {reply.GetType().Name}",
            };
        }
        catch (IOException error)
        {
            return error.Message;
        }
    }

    /// <summary>
    /// Sends the user's decision (<c>resolve_conflict</c>). The conflict leaves
    /// the queue once the core took it, or when the core no longer knows it
    /// (another client decided). Returns the core's refusal, or null.
    /// </summary>
    public async Task<string?> ResolveAsync(JobConflictEvent conflict, Resolution resolution, bool applyToSameKind, string? requestId = null)
    {
        var request = new ResolveConflictRequest(conflict.JobId, conflict.ConflictId, resolution)
        {
            ApplyToSameKind = applyToSameKind,
            Id = requestId ?? "",
        };
        CoreReply reply;
        try
        {
            reply = await core.RequestAsync(request);
        }
        catch (IOException error)
        {
            return error.Message;
        }
        switch (reply)
        {
            case OkReply:
                Conflicts.Resolve(conflict, applyToSameKind);
                FollowConflict();
                Raise();
                return null;
            case ErrorReply { Code: ErrorCodes.NoSuchConflict or ErrorCodes.NoSuchJob } gone:
                Conflicts.Resolve(conflict, applyToSameKind: false);
                if (gone.Code == ErrorCodes.NoSuchJob && Find(conflict.JobId) is { IsFinal: false } job)
                {
                    // The core no longer has the job; it would stay on screen as waiting forever.
                    Forget(job);
                }
                FollowConflict();
                Raise();
                return null;
            case ErrorReply error:
                return error.Message;
            default:
                return $"unexpected reply {reply.GetType().Name}";
        }
    }

    /// <summary>
    /// Folds the flyout into the pill. A job that has ended leaves instead,
    /// and the pill shows the next one, if any.
    /// </summary>
    public void Minimize()
    {
        if (Shown is null)
        {
            return;
        }
        if (Shown.IsFinal)
        {
            Shown.IsDismissed = true;
            Shown = Visible.FirstOrDefault();
        }
        IsMinimized = true;
        Raise();
    }

    /// <summary>Opens the flyout from the pill.</summary>
    public void Restore()
    {
        if (Shown is not null && IsMinimized)
        {
            IsMinimized = false;
            Raise();
        }
    }

    /// <summary>Closes an ended job ("Close"); the flyout moves on to the next job, or hides.</summary>
    public void Close()
    {
        if (Shown is not { IsFinal: true } job)
        {
            return;
        }
        job.IsDismissed = true;
        Shown = Visible.FirstOrDefault();
        Raise();
    }

    /// <summary>Shows the next older job, then wraps around (the "N more" link).</summary>
    public void ShowNext()
    {
        var visible = Visible;
        if (visible.Count < 2)
        {
            return;
        }
        var index = Shown is null ? -1 : IndexOf(visible, Shown);
        Shown = visible[(index + 1) % visible.Count];
        Raise();
    }

    /// <summary>Forgets every job: the core was started again, and its jobs ended with it.</summary>
    public void Reset()
    {
        _jobs.Clear();
        Conflicts.Clear();
        Shown = null;
        IsMinimized = false;
        Raise();
    }

    private void Forget(TransferJob job)
    {
        _jobs.Remove(job.Id);
        Conflicts.RemoveJob(job.Id);
        if (Shown == job)
        {
            Shown = Visible.FirstOrDefault();
        }
    }

    private TransferJob Track(ulong jobId)
    {
        var job = GetOrCreate(jobId, out var created);
        if (created)
        {
            if (Shown is null)
            {
                // Someone else's job: shown in the pill, without taking the screen.
                Shown = job;
                IsMinimized = true;
            }
            _ = LearnAsync();
        }
        return job;
    }

    private async Task LearnAsync()
    {
        if (_learning)
        {
            return;
        }
        _learning = true;
        try
        {
            await LoadAsync();
        }
        finally
        {
            _learning = false;
        }
    }

    private TransferJob GetOrCreate(ulong jobId, out bool created)
    {
        created = !_jobs.TryGetValue(jobId, out var job);
        if (job is null)
        {
            job = new TransferJob(jobId, ++_order);
            _jobs[jobId] = job;
        }
        return job;
    }

    private void End(TransferJob job)
    {
        if (job.EndHandled)
        {
            return;
        }
        job.EndHandled = true;
        Conflicts.RemoveJob(job.Id);
        var quiet = job.State.Type is JobState.Completed or JobState.Cancelled;
        if (job != Shown)
        {
            if (quiet)
            {
                job.IsDismissed = true;
            }
        }
        else if (IsMinimized && quiet)
        {
            job.IsDismissed = true;
            Shown = Visible.FirstOrDefault();
        }
        else if (IsMinimized)
        {
            // Errors or a failure: the user should see it, not a pill.
            IsMinimized = false;
        }
        FollowConflict();
    }

    // A waiting decision comes first: the flyout shows the job of the conflict at the head of the queue.
    private void FollowConflict()
    {
        if (Conflicts.Current is { } conflict && Find(conflict.JobId) is { } job && job != Shown)
        {
            job.IsDismissed = false;
            ShowInstead(job);
            IsMinimized = false;
        }
    }

    // A job that ended quietly and was already seen gives way for good; one with errors waits its turn.
    private void ShowInstead(TransferJob job)
    {
        if (Shown is { IsFinal: true } previous && previous != job
            && previous.State.Type is JobState.Completed or JobState.Cancelled)
        {
            previous.IsDismissed = true;
        }
        Shown = job;
    }

    private static int IndexOf(IReadOnlyList<TransferJob> jobs, TransferJob job)
    {
        for (var i = 0; i < jobs.Count; i++)
        {
            if (jobs[i] == job)
            {
                return i;
            }
        }
        return -1;
    }

    private void Raise() => Changed?.Invoke();
}
