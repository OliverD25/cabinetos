using System.Globalization;
using CabinetOS.Core.Jobs;
using CabinetOS.Core.Protocol;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>The transfer flyout's model (design view E) against a fake core: every state comes from events.</summary>
public class TransferCenterTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static JobProgressEvent Progress(ulong job, string state, ulong bytesDone, ulong bytesTotal,
        ulong filesDone = 1, ulong filesTotal = 4, ulong speed = 0, ulong? eta = null, ulong elapsed = 1000, ulong failed = 0) =>
        new(job, new JobState(state), bytesDone, bytesTotal, filesDone, filesTotal, 0, failed, 0, null, speed, eta, elapsed);

    private static JobConflictEvent Conflict(ulong id, ulong job, string kind = ConflictKind.FileExists) =>
        new(id, job, new ConflictKind(kind, 10, 133000000000000000, 12, 132000000000000000), $@"C:\src\file{id}.txt", $@"D:\dst\file{id}.txt");

    private static (TransferCenter Center, FakeChannel Core) Create(IReadOnlyList<JobInfo>? jobs = null)
    {
        ulong next = 7;
        var core = new FakeChannel(request => request switch
        {
            StartJobRequest => new JobStartedReply(next++),
            ListJobsRequest => new JobsReply(jobs ?? []),
            JobControlRequest => new OkReply(),
            ResolveConflictRequest => new OkReply(),
            _ => new ErrorReply(ErrorCodes.UnknownRequest, request.Type),
        });
        return (new TransferCenter(core), core);
    }

    [Fact]
    public async Task A_started_copy_opens_the_flyout_and_follows_the_cores_numbers()
    {
        var (center, core) = Create();
        var changes = 0;
        center.Changed += () => changes++;

        var start = await center.StartAsync(JobKind.Copy, [@"C:\photos\a.jpg", @"C:\photos\b.jpg"], @"D:\backup", "01J9ZQ4X7K3M5N8P2R6S0T1V4W");

        Assert.True(start.Started);
        var job = Assert.IsType<TransferJob>(center.Shown);
        Assert.True(center.IsFlyoutOpen);
        Assert.False(center.IsPillVisible);
        var sent = Assert.IsType<StartJobRequest>(Assert.Single(core.Requests));
        Assert.Equal("01J9ZQ4X7K3M5N8P2R6S0T1V4W", sent.Id);
        Assert.Equal(@"D:\backup", sent.Destination);
        Assert.Equal(@"C:\photos → D:\backup", TransferText.Subtitle(job));
        Assert.Equal("Waiting to copy", TransferText.Title(job, Invariant));

        center.OnEvent(new JobStateChangedEvent(7, new JobState(JobState.Scanning)));
        Assert.Equal("Preparing to copy", TransferText.Title(job, Invariant));

        center.OnEvent(Progress(7, JobState.Running, 1_200_000_000, 2_700_000_000, filesDone: 8412, filesTotal: 12480, speed: 610 * 1024 * 1024, eta: 195));
        Assert.Equal("Copying 12,480 items", TransferText.Title(job, Invariant));
        Assert.Equal("44%", TransferText.PercentText(job, Invariant));
        Assert.Equal("1.1 GB of 2.5 GB", TransferText.DoneText(job, Invariant));
        Assert.Equal("3 min 15 s left", TransferText.EtaText(job));
        Assert.Equal("610 MB/s", TransferText.SpeedText(job, Invariant));
        Assert.Equal("Pause", TransferText.PauseLabel(job));
        Assert.Equal("Cancel", TransferText.EndLabel(job));
        Assert.True(changes >= 3);
    }

    [Fact]
    public async Task Pause_resume_and_completion_come_from_the_core_not_from_the_buttons()
    {
        var (center, core) = Create();
        var job = (await center.StartAsync(JobKind.Copy, [@"C:\a"], @"D:\b")).Job!;
        center.OnEvent(Progress(7, JobState.Running, 50, 100, speed: 10));

        Assert.Null(await center.ControlAsync(7, JobActions.Pause));
        Assert.Equal(JobActions.Pause, Assert.IsType<JobControlRequest>(core.Requests[^1]).Action);
        Assert.Equal("Pause", TransferText.PauseLabel(job));

        center.OnEvent(new JobStateChangedEvent(7, new JobState(JobState.Paused)));
        Assert.Equal("Copy paused", TransferText.Title(job, Invariant));
        Assert.Equal("Resume", TransferText.PauseLabel(job));
        Assert.Equal("Paused", TransferText.EtaText(job));
        Assert.Equal("Copy paused · 50%", TransferText.PillText(job, Invariant));

        center.OnEvent(new JobStateChangedEvent(7, new JobState(JobState.Running)));
        center.OnEvent(Progress(7, JobState.Completed, 100, 100, filesDone: 4, elapsed: 2000));
        center.OnEvent(new JobStateChangedEvent(7, new JobState(JobState.Completed)));
        Assert.Equal("Copy complete", TransferText.Title(job, Invariant));
        Assert.Equal("100%", TransferText.PercentText(job, Invariant));
        Assert.Null(TransferText.PauseLabel(job));
        Assert.Equal("Close", TransferText.EndLabel(job));
        Assert.Same(job, center.Shown);

        center.Close();
        Assert.Null(center.Shown);
        Assert.False(center.IsFlyoutOpen);
    }

    [Fact]
    public async Task Minimize_folds_into_the_pill_and_a_quiet_end_takes_the_pill_away()
    {
        var (center, _) = Create();
        var job = (await center.StartAsync(JobKind.Move, [@"C:\a"], @"D:\b")).Job!;
        center.OnEvent(Progress(7, JobState.Running, 30, 100));

        center.Minimize();
        Assert.True(center.IsPillVisible);
        Assert.Equal("Moving · 30%", TransferText.PillText(job, Invariant));

        center.Restore();
        Assert.True(center.IsFlyoutOpen);
        center.Minimize();

        center.OnEvent(new JobStateChangedEvent(7, new JobState(JobState.Completed)));
        Assert.Null(center.Shown);
        Assert.False(center.IsPillVisible);
        Assert.Empty(center.Visible);
    }

    [Fact]
    public async Task A_failure_while_minimized_opens_the_flyout_so_the_user_sees_it()
    {
        var (center, _) = Create();
        var job = (await center.StartAsync(JobKind.Copy, [@"C:\a"], @"D:\b")).Job!;
        center.Minimize();

        center.OnEvent(new JobStateChangedEvent(7, new JobState(JobState.Failed, "the destination folder cannot be created")));

        Assert.True(center.IsFlyoutOpen);
        Assert.Equal("Copy failed", TransferText.Title(job, Invariant));
        Assert.Equal("the destination folder cannot be created", TransferText.Subtitle(job));
    }

    [Fact]
    public async Task Minimizing_an_ended_job_closes_it_and_the_pill_shows_the_next_running_one()
    {
        var (center, _) = Create();
        var first = (await center.StartAsync(JobKind.Copy, [@"C:\a"], @"D:\b")).Job!;
        var second = (await center.StartAsync(JobKind.Delete(false), [@"C:\old"], null)).Job!;
        Assert.Same(second, center.Shown);
        Assert.Equal(1, center.OthersCount);
        Assert.Equal(@"C:\old → Recycle Bin", TransferText.Subtitle(second));

        center.OnEvent(new JobStateChangedEvent(8, new JobState(JobState.Completed)));
        center.Minimize();

        Assert.Same(first, center.Shown);
        Assert.True(center.IsPillVisible);
    }

    [Fact]
    public async Task A_new_job_replaces_a_finished_one_on_screen_and_it_does_not_come_back()
    {
        var (center, _) = Create();
        await center.StartAsync(JobKind.Delete(false), [@"C:\a"], null);
        center.OnEvent(new JobStateChangedEvent(7, new JobState(JobState.Completed)));
        var copy = (await center.StartAsync(JobKind.Copy, [@"C:\b"], @"D:\x")).Job!;

        Assert.Same(copy, center.Shown);
        Assert.Equal(0, center.OthersCount);
        center.OnEvent(new JobStateChangedEvent(8, new JobState(JobState.Cancelled)));
        center.Close();
        Assert.Null(center.Shown);
    }

    [Fact]
    public async Task The_more_link_cycles_through_the_other_jobs()
    {
        var (center, _) = Create();
        var a = (await center.StartAsync(JobKind.Copy, [@"C:\a"], @"D:\x")).Job!;
        var b = (await center.StartAsync(JobKind.Copy, [@"C:\b"], @"D:\x")).Job!;
        var c = (await center.StartAsync(JobKind.Copy, [@"C:\c"], @"D:\x")).Job!;
        Assert.Same(c, center.Shown);
        Assert.Equal(2, center.OthersCount);

        center.ShowNext();
        Assert.Same(b, center.Shown);
        center.ShowNext();
        Assert.Same(a, center.Shown);
        center.ShowNext();
        Assert.Same(c, center.Shown);
    }

    [Fact]
    public async Task A_job_that_ends_quietly_out_of_sight_leaves_but_one_with_errors_stays()
    {
        var (center, _) = Create();
        await center.StartAsync(JobKind.Copy, [@"C:\a"], @"D:\x");
        await center.StartAsync(JobKind.Copy, [@"C:\b"], @"D:\x");
        var shown = (await center.StartAsync(JobKind.Copy, [@"C:\c"], @"D:\x")).Job!;

        center.OnEvent(new JobStateChangedEvent(7, new JobState(JobState.Completed)));
        center.OnEvent(new JobStateChangedEvent(8, new JobState(JobState.CompletedWithErrors)));

        Assert.Same(shown, center.Shown);
        Assert.Equal([9UL, 8UL], center.Visible.Select(j => j.Id));
    }

    [Fact]
    public async Task A_conflict_opens_the_flyout_on_its_job_and_waits_in_the_queue()
    {
        var (center, core) = Create();
        var copy = (await center.StartAsync(JobKind.Copy, [@"C:\src"], @"D:\dst")).Job!;
        await center.StartAsync(JobKind.Copy, [@"C:\other"], @"D:\dst");
        center.Minimize();

        center.OnEvent(Conflict(1, 7));
        center.OnEvent(Conflict(2, 7));
        center.OnEvent(Conflict(1, 7));

        Assert.Same(copy, center.Shown);
        Assert.True(center.IsFlyoutOpen);
        Assert.Equal(2, center.Conflicts.Count);
        Assert.Equal(1UL, center.Conflicts.Current!.ConflictId);

        Assert.Null(await center.ResolveAsync(center.Conflicts.Current, new Resolution(Resolution.SkipType), applyToSameKind: false));
        var sent = Assert.IsType<ResolveConflictRequest>(core.Requests[^1]);
        Assert.Equal((7UL, 1UL, Resolution.SkipType, false), (sent.JobId, sent.ConflictId, sent.Resolution.Type, sent.ApplyToSameKind));
        Assert.Equal(2UL, center.Conflicts.Current!.ConflictId);

        center.OnEvent(new JobStateChangedEvent(7, new JobState(JobState.Cancelled)));
        Assert.Null(center.Conflicts.Current);
    }

    [Fact]
    public async Task The_flyout_follows_the_next_waiting_conflict_to_its_job()
    {
        var (center, _) = Create();
        var first = (await center.StartAsync(JobKind.Copy, [@"C:\a"], @"D:\x")).Job!;
        var second = (await center.StartAsync(JobKind.Copy, [@"C:\b"], @"D:\y")).Job!;
        center.OnEvent(Conflict(1, 7));
        center.OnEvent(Conflict(2, 8));
        Assert.Same(first, center.Shown);

        await center.ResolveAsync(center.Conflicts.Current!, new Resolution(Resolution.SkipType), false);
        Assert.Same(second, center.Shown);

        center.OnEvent(Conflict(3, 7));
        center.OnEvent(new JobStateChangedEvent(8, new JobState(JobState.Cancelled)));
        Assert.Same(first, center.Shown);
        Assert.Equal(3UL, center.Conflicts.Current!.ConflictId);
    }

    [Fact]
    public async Task A_decision_the_core_no_longer_knows_leaves_the_queue_and_a_refusal_stays()
    {
        var reply = (CoreReply)new ErrorReply(ErrorCodes.NoSuchConflict, "no conflict 1");
        var core = new FakeChannel(request => request switch
        {
            StartJobRequest => new JobStartedReply(7),
            ResolveConflictRequest => reply,
            _ => new JobsReply([]),
        });
        var center = new TransferCenter(core);
        await center.StartAsync(JobKind.Copy, [@"C:\src"], @"D:\dst");
        center.OnEvent(Conflict(1, 7));
        center.OnEvent(Conflict(2, 7));

        Assert.Null(await center.ResolveAsync(center.Conflicts.Current!, new Resolution(Resolution.OverwriteType), false));
        Assert.Equal(2UL, center.Conflicts.Current!.ConflictId);

        reply = new ErrorReply(ErrorCodes.InvalidResolution, "delete_permanently does not answer file_exists");
        Assert.Equal("delete_permanently does not answer file_exists",
            await center.ResolveAsync(center.Conflicts.Current, new Resolution(Resolution.DeletePermanentlyType), false));
        Assert.Equal(2UL, center.Conflicts.Current!.ConflictId);
    }

    [Fact]
    public async Task A_quiet_end_while_minimized_hands_the_pill_to_the_job_still_running()
    {
        var (center, _) = Create();
        var older = (await center.StartAsync(JobKind.Copy, [@"C:\a"], @"D:\x")).Job!;
        await center.StartAsync(JobKind.Move, [@"C:\b"], @"D:\y");
        center.OnEvent(Progress(7, JobState.Running, 10, 100));
        center.Minimize();

        center.OnEvent(Progress(8, JobState.Completed, 100, 100));

        Assert.Same(older, center.Shown);
        Assert.True(center.IsPillVisible);
        Assert.Equal([7UL], center.Visible.Select(j => j.Id));
    }

    [Fact]
    public async Task An_end_with_errors_while_minimized_opens_the_flyout()
    {
        var (center, _) = Create();
        var job = (await center.StartAsync(JobKind.Copy, [@"C:\a"], @"D:\x")).Job!;
        center.Minimize();

        center.OnEvent(Progress(7, JobState.CompletedWithErrors, 100, 100, failed: 2));

        Assert.Same(job, center.Shown);
        Assert.True(center.IsFlyoutOpen);
        Assert.Equal("Close", TransferText.EndLabel(job));
    }

    [Fact]
    public async Task A_late_conflict_for_an_ended_job_is_not_queued_and_opens_nothing()
    {
        var (center, _) = Create();
        await center.StartAsync(JobKind.Copy, [@"C:\a"], @"D:\x");
        await center.StartAsync(JobKind.Copy, [@"C:\b"], @"D:\y");
        center.OnEvent(new JobStateChangedEvent(7, new JobState(JobState.Completed)));
        center.Minimize();
        var shown = center.Shown;

        center.OnEvent(Conflict(4, 7));

        Assert.Null(center.Conflicts.Current);
        Assert.Same(shown, center.Shown);
        Assert.True(center.IsPillVisible);
    }

    [Fact]
    public async Task A_conflict_for_a_job_the_core_no_longer_has_leaves_with_the_job_after_the_decision()
    {
        var core = new FakeChannel(request => request switch
        {
            StartJobRequest => new JobStartedReply(7),
            ResolveConflictRequest => new ErrorReply(ErrorCodes.NoSuchJob, "no job 12"),
            _ => new JobsReply([]),
        });
        var center = new TransferCenter(core);
        var mine = (await center.StartAsync(JobKind.Copy, [@"C:\a"], @"D:\x")).Job!;

        // A job this window never saw, which the core does not list: only its conflict arrived.
        center.OnEvent(Conflict(1, 12));
        Assert.Equal(12UL, center.Shown!.Id);

        Assert.Null(await center.ResolveAsync(center.Conflicts.Current!, new Resolution(Resolution.SkipType), false));

        Assert.Null(center.Conflicts.Current);
        Assert.Null(center.Find(12));
        Assert.Same(mine, center.Shown);
        Assert.Equal([7UL], center.Visible.Select(j => j.Id));
    }

    [Fact]
    public async Task A_job_started_elsewhere_is_learned_with_list_jobs_and_shows_in_the_pill()
    {
        var running = new JobInfo(JobKind.Copy, [@"C:\photos"], @"E:\backup", 3, new JobState(JobState.Running),
            10, 100, 1, 10, 0, 0, 0, null, 5, null, 500);
        var (center, core) = Create([running]);

        center.OnEvent(Progress(3, JobState.Running, 20, 100, elapsed: 900));
        await Task.Yield();

        Assert.Contains(core.Requests, r => r is ListJobsRequest);
        var job = Assert.IsType<TransferJob>(center.Shown);
        Assert.True(center.IsPillVisible);
        Assert.True(job.IsDescribed);
        Assert.Equal(@"C:\photos → E:\backup", TransferText.Subtitle(job));
        // The snapshot from list_jobs is older than the event; it does not win.
        Assert.Equal(20UL, job.Progress!.BytesDone);
    }

    [Fact]
    public async Task At_start_a_running_job_of_an_earlier_session_shows_and_old_finished_ones_do_not()
    {
        var old = new JobInfo(JobKind.Copy, [@"C:\a"], @"D:\b", 1, new JobState(JobState.Completed), 1, 1, 1, 1, 0, 0, 0, null, 0, null, 10);
        var live = new JobInfo(JobKind.Move, [@"C:\c"], @"D:\d", 2, new JobState(JobState.Paused), 5, 10, 1, 2, 0, 0, 0, null, 0, null, 20);
        var (center, _) = Create([old, live]);

        await center.LoadAsync();

        Assert.Equal([2UL], center.Visible.Select(j => j.Id));
        Assert.True(center.IsPillVisible);
        Assert.Equal("Move paused · 50%", TransferText.PillText(center.Shown!, Invariant));
    }

    [Fact]
    public async Task Speed_samples_feed_the_graph_and_drop_to_zero_while_paused()
    {
        var (center, _) = Create();
        var job = (await center.StartAsync(JobKind.Copy, [@"C:\a"], @"D:\b")).Job!;
        center.OnEvent(Progress(7, JobState.Running, 50, 100, speed: 4 * 1024 * 1024));
        center.SampleSpeeds();
        center.OnEvent(new JobStateChangedEvent(7, new JobState(JobState.Paused)));
        center.SampleSpeeds();

        Assert.Equal(TransferJob.SampleCount, job.Speeds.Count);
        Assert.Equal([4UL * 1024 * 1024, 0UL], job.Speeds.TakeLast(2));
        var graph = TransferText.Graph(job.Speeds, 348, 56);
        Assert.Equal(TransferJob.SampleCount, graph.Count);
        Assert.Equal((0.0, 56.0), graph[0]);
        Assert.Equal(348, graph[^1].X, 3);
        // The fastest sample reaches near the top: 10 % headroom over the 50 px the line may use.
        Assert.InRange(graph[^2].Y, 6, 12);
    }

    [Fact]
    public async Task A_refused_job_says_why_and_opens_nothing()
    {
        var core = new FakeChannel(_ => new ErrorReply(ErrorCodes.InvalidPath, "a source is already in the destination folder"));
        var center = new TransferCenter(core);

        var start = await center.StartAsync(JobKind.Copy, [@"C:\a\x"], @"C:\a");

        Assert.False(start.Started);
        Assert.Equal(ErrorCodes.InvalidPath, start.ErrorCode);
        Assert.Null(center.Shown);
    }

    [Fact]
    public async Task A_restarted_core_forgets_every_job()
    {
        var (center, _) = Create();
        await center.StartAsync(JobKind.Copy, [@"C:\a"], @"D:\b");
        center.OnEvent(Conflict(1, 7));

        center.Reset();

        Assert.Null(center.Shown);
        Assert.Empty(center.Visible);
        Assert.Null(center.Conflicts.Current);
        Assert.True(center.Conflicts.Add(Conflict(1, 1)));
    }

    [Fact]
    public void A_job_without_bytes_counts_items()
    {
        var job = new TransferJob(1, 1);
        job.Describe(JobKind.Delete(permanent: true), [@"C:\x\a", @"C:\x\b"], null);
        job.ApplyProgress(Progress(1, JobState.Running, 0, 0, filesDone: 3, filesTotal: 12));

        Assert.Equal("Deleting 12 items", TransferText.Title(job, Invariant));
        Assert.Equal(@"C:\x → deleted for good", TransferText.Subtitle(job));
        Assert.Equal("3 of 12 items", TransferText.DoneText(job, Invariant));
        Assert.Equal("25%", TransferText.PercentText(job, Invariant));
        Assert.Equal("", TransferText.SpeedText(job, Invariant));
    }

    [Theory]
    [InlineData(59UL, "59 s left")]
    [InlineData(60UL, "1 min 0 s left")]
    [InlineData(3725UL, "1 h 2 min left")]
    public void Time_left_reads_like_the_design(ulong seconds, string expected)
    {
        var job = new TransferJob(1, 1);
        job.ApplyProgress(Progress(1, JobState.Running, 1, 2, eta: seconds));
        Assert.Equal(expected, TransferText.EtaText(job));
    }
}
