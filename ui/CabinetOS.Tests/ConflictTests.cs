using System.Globalization;
using CabinetOS.Core.Jobs;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Tests;

/// <summary>The conflict queue and what the conflict card says and offers.</summary>
public class ConflictTests
{
    private static JobConflictEvent Conflict(ulong id, ulong job, string kind = ConflictKind.FileExists, ulong? size = null) =>
        new(id, job, new ConflictKind(kind, Size: size), $@"C:\src\file{id}.txt", $@"D:\dst\file{id}.txt");

    [Fact]
    public void Conflicts_wait_in_order_and_a_resent_one_is_not_queued_twice()
    {
        var queue = new ConflictQueue();
        Assert.True(queue.Add(Conflict(1, 7)));
        Assert.True(queue.Add(Conflict(2, 7)));
        Assert.False(queue.Add(Conflict(1, 7)));

        Assert.Equal([1UL, 2UL], queue.Waiting.Select(c => c.ConflictId));
        Assert.Equal(1UL, queue.Current!.ConflictId);
    }

    [Fact]
    public void A_decided_conflict_does_not_come_back_when_the_core_resends_it()
    {
        var queue = new ConflictQueue();
        var first = Conflict(1, 7);
        queue.Add(first);
        queue.Resolve(first, applyToSameKind: false);

        Assert.False(queue.Add(first));
        Assert.Null(queue.Current);
    }

    [Fact]
    public void Apply_to_all_takes_the_jobs_other_conflicts_of_that_kind_along()
    {
        var queue = new ConflictQueue();
        var first = Conflict(1, 7);
        queue.Add(first);
        queue.Add(Conflict(2, 7));
        queue.Add(Conflict(3, 7, ConflictKind.AccessDenied));
        queue.Add(Conflict(4, 8));

        Assert.Equal(2, queue.Resolve(first, applyToSameKind: true));

        Assert.Equal([3UL, 4UL], queue.Waiting.Select(c => c.ConflictId));
    }

    [Fact]
    public void An_ended_job_takes_its_conflicts_along()
    {
        var queue = new ConflictQueue();
        queue.Add(Conflict(1, 7));
        queue.Add(Conflict(2, 8));
        queue.Add(Conflict(3, 7));

        Assert.Equal(2, queue.RemoveJob(7));
        Assert.Equal([2UL], queue.Waiting.Select(c => c.ConflictId));
    }

    [Fact]
    public void The_card_says_what_happened_in_plain_words()
    {
        var invariant = CultureInfo.InvariantCulture;
        Assert.Equal("file1.txt", ConflictText.Name(Conflict(1, 7)));
        Assert.Equal("already exists", ConflictText.KindText(Conflict(1, 7), invariant));
        Assert.Equal("access denied", ConflictText.KindText(Conflict(1, 7, ConflictKind.AccessDenied), invariant));
        Assert.Equal("the Recycle Bin cannot take it (21 GB)",
            ConflictText.KindText(Conflict(1, 7, ConflictKind.RecycleBinTooSmall, 21UL * 1024 * 1024 * 1024), invariant));
        var io = new JobConflictEvent(1, 7, new ConflictKind(ConflictKind.Io, Code: 23, Message: "Data error."), @"C:\a", null);
        Assert.Equal("failed: Data error. (error 23)", ConflictText.KindText(io, invariant));
    }

    [Fact]
    public void A_file_that_exists_shows_both_sizes_and_times()
    {
        var now = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Local);
        var sourceTime = new DateTime(2026, 9, 28, 9, 11, 0, DateTimeKind.Local).ToUniversalTime().ToFileTimeUtc();
        var destTime = new DateTime(2026, 9, 1, 8, 5, 0, DateTimeKind.Local).ToUniversalTime().ToFileTimeUtc();
        var conflict = new JobConflictEvent(1, 7, new ConflictKind(ConflictKind.FileExists, 18 * 1024, sourceTime, 12, destTime), @"C:\a.txt", @"D:\a.txt");

        var sides = ConflictText.Sides(conflict, now, CultureInfo.InvariantCulture);

        Assert.NotNull(sides);
        Assert.Equal("18 KB · Today 09:11", sides.Value.New);
        Assert.StartsWith("12 B · 09/01/2026", sides.Value.Existing);
        Assert.Null(ConflictText.Sides(Conflict(2, 7, ConflictKind.AccessDenied), now));
    }

    [Fact]
    public void Each_kind_offers_only_the_decisions_that_fit_it()
    {
        Assert.Equal(["overwrite", "skip", "rename", "retry", "cancel_job"], ConflictText.Options(new ConflictKind(ConflictKind.FileExists)));
        Assert.Equal(["overwrite", "skip", "retry", "cancel_job"], ConflictText.Options(new ConflictKind(ConflictKind.AccessDenied)));
        Assert.Equal(["delete_permanently", "skip"], ConflictText.Options(new ConflictKind(ConflictKind.RecycleBinTooSmall)));
        Assert.Equal(["retry", "skip", "cancel_job"], ConflictText.Options(new ConflictKind(ConflictKind.SharingViolation)));
        Assert.Equal("Delete permanently", ConflictText.Label(Resolution.DeletePermanentlyType));
        Assert.Equal("Cancel job", ConflictText.Label(Resolution.CancelJobType));
    }
}
