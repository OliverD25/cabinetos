using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Jobs;

/// <summary>
/// The conflicts that wait for the user, in the order they came; the flyout
/// shows one at a time. The core may send a waiting conflict twice (again
/// after <c>hello</c>, docs/jobs.md), so conflicts are keyed by their ID.
/// </summary>
public sealed class ConflictQueue
{
    private readonly List<JobConflictEvent> _waiting = [];
    private readonly HashSet<ulong> _seen = [];

    /// <summary>The conflict shown now, or null.</summary>
    public JobConflictEvent? Current => _waiting.Count > 0 ? _waiting[0] : null;

    /// <summary>How many wait, the current one included.</summary>
    public int Count => _waiting.Count;

    /// <summary>Every waiting conflict, oldest first.</summary>
    public IReadOnlyList<JobConflictEvent> Waiting => _waiting;

    /// <summary>Queues a conflict; false for one already seen, waiting or decided.</summary>
    public bool Add(JobConflictEvent conflict)
    {
        if (!_seen.Add(conflict.ConflictId))
        {
            return false;
        }
        _waiting.Add(conflict);
        return true;
    }

    /// <summary>
    /// Removes a decided conflict. With <paramref name="applyToSameKind"/> the
    /// core also decides the job's other waiting conflicts of that kind, so they
    /// leave the queue too. Returns how many left.
    /// </summary>
    public int Resolve(JobConflictEvent conflict, bool applyToSameKind) =>
        _waiting.RemoveAll(c => c.ConflictId == conflict.ConflictId
            || (applyToSameKind && c.JobId == conflict.JobId && c.Kind.Type == conflict.Kind.Type));

    /// <summary>Drops the conflicts of a job that ended: they no longer wait.</summary>
    public int RemoveJob(ulong jobId) => _waiting.RemoveAll(c => c.JobId == jobId);

    /// <summary>Forgets everything (the core was started again; its IDs start over).</summary>
    public void Clear()
    {
        _waiting.Clear();
        _seen.Clear();
    }
}
