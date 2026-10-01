using CabinetOS.Core.Diagnostics;

namespace CabinetOS.Tests;

/// <summary>The frame table of a scroll run (docs/ui.md, "Scrolling"): where the UI thread's milliseconds go.</summary>
public class FrameTableTests
{
    private static double[] Parts(double measure = 0, double bind = 0, double details = 0)
    {
        var parts = new double[FrameParts.Count];
        parts[(int)FramePart.Measure] = measure;
        parts[(int)FramePart.Bind] = bind;
        parts[(int)FramePart.Details] = details;
        return parts;
    }

    [Fact]
    public void The_summary_counts_slow_frames_and_splits_the_time_by_part()
    {
        var table = new FrameTable();
        // 18 quick frames, one of 25 ms, one of 50 ms.
        for (var i = 0; i < 18; i++)
        {
            table.Add(new FrameSample(16.7, 4, Parts(measure: 2, bind: 1)));
        }
        table.Add(new FrameSample(25, 20, Parts(measure: 12, bind: 5)));
        table.Add(new FrameSample(50, 30, Parts(measure: 20, bind: 8, details: 15)));

        var summary = table.Summarize();

        Assert.Equal((20, 2, 1), (summary.Frames, summary.Over20, summary.Over33));
        Assert.Equal(10.0, summary.PercentOver20, 3);
        Assert.Equal(50, summary.Worst);
        Assert.Equal(16.7, summary.Median, 3);
        Assert.False(summary.MeetsGoal);
        // Over the slow frames only: the mean of the two.
        Assert.Equal(2, summary.Slow.Frames);
        Assert.Equal(37.5, summary.Slow.GapMs, 3);
        Assert.Equal(16, summary.Slow.PartMs[(int)FramePart.Measure], 3);
        Assert.Equal(7.5, summary.Slow.PartMs[(int)FramePart.Details], 3);
        // The worst frame as it was.
        Assert.Equal(15, summary.WorstFrame.PartMs[(int)FramePart.Details], 3);
        Assert.Equal(30, summary.WorstFrame.WorkMs, 3);
        // Over all frames: 18 × 2 + 12 + 20 = 68 ms of measure in 20 frames.
        Assert.Equal(3.4, summary.All.PartMs[(int)FramePart.Measure], 3);
    }

    [Fact]
    public void The_goal_is_no_frame_over_33_ms_and_fewer_than_5_percent_over_20_ms()
    {
        var table = new FrameTable();
        for (var i = 0; i < 99; i++)
        {
            table.Add(new FrameSample(16.7, 3, Parts()));
        }
        table.Add(new FrameSample(30, 20, Parts()));
        Assert.True(table.Summarize().MeetsGoal);

        // Two vsyncs measure 33.3 ms, and a little jitter; a frame of 33.4 ms or more dropped two.
        table.Add(new FrameSample(33.5, 20, Parts()));
        Assert.False(table.Summarize().MeetsGoal);

        var busy = new FrameTable();
        for (var i = 0; i < 19; i++)
        {
            busy.Add(new FrameSample(16.7, 3, Parts()));
        }
        busy.Add(new FrameSample(21, 3, Parts()));
        Assert.Equal(5.0, busy.Summarize().PercentOver20, 3);
        Assert.False(busy.Summarize().MeetsGoal);
    }

    [Fact]
    public void A_frame_whose_ui_thread_work_passes_one_60_hz_frame_is_counted_whatever_the_gap()
    {
        // With the display asleep Windows draws 30 frames a second: every gap is 33 ms, so the
        // gaps say little; the work on the UI thread still says which frames would drop at 60 Hz.
        var table = new FrameTable();
        var parts = new double[FrameParts.Count];
        parts[(int)FramePart.Status] = 2;
        parts[(int)FramePart.Measure] = 9;
        parts[(int)FramePart.Bind] = 3;
        parts[(int)FramePart.Arrange] = 1;
        // WinUI's frame (5 ms) held 4 ms of the rows' layout; the other 6 ms ran outside it.
        table.Add(new FrameSample(33.3, 5, parts, LayoutInFrameMs: 4));
        table.Add(new FrameSample(33.3, 16, Parts()));
        table.Add(new FrameSample(33.3, 4, Parts()));

        var summary = table.Summarize();

        // 5 ms of frame, 6 ms of layout outside it, 2 ms of status bar: 13 ms. Binding is inside the measure pass.
        Assert.Equal(0, summary.BusyOver16);
        Assert.Equal(16, summary.BusiestMs, 3);
        Assert.Equal(13, FrameTable.Busy(new FrameSample(33.3, 5, parts, LayoutInFrameMs: 4)), 3);
        // All of it inside the frame: the frame's time alone, and the status bar.
        Assert.Equal(7, FrameTable.Busy(new FrameSample(33.3, 5, parts, LayoutInFrameMs: 10)), 3);
    }

    [Fact]
    public void Frames_whose_ui_thread_work_passes_20_and_33_ms_are_counted_with_the_limits_of_the_gaps()
    {
        // A laptop's display path can hold a frame back for 80 ms while the UI thread works 8 ms: the gaps
        // are huge, the work is small, and the panel goal (livecheck.ps1 -Panel) judges the work.
        var table = new FrameTable();
        table.Add(new FrameSample(80, 8, Parts()));
        table.Add(new FrameSample(80, 8, Parts()));
        // Work is WinUI's frame time plus the parts that run outside it: 12 + 9 (details) = 21 ms.
        table.Add(new FrameSample(17, 12, Parts(details: 9)));
        // Exactly 20 ms is not over 20 ms; 33.3 ms is over 20 ms and not yet a dropped pair of frames (33.4 ms).
        table.Add(new FrameSample(17, 20, Parts()));
        table.Add(new FrameSample(17, 33.3, Parts()));
        table.Add(new FrameSample(17, 33.4, Parts()));
        table.Add(new FrameSample(17, 50, Parts()));

        var summary = table.Summarize();

        Assert.Equal(7, summary.Frames);
        Assert.Equal((4, 2), (summary.BusyOver20, summary.BusyOver33));
        // The gaps see only the two frames the display path held back.
        Assert.Equal((2, 2), (summary.Over20, summary.Over33));
    }

    [Theory]
    [InlineData("150", true, 150, 0)]
    [InlineData("150/2", true, 150, 2)]
    [InlineData("0", false, 0, 0)]
    [InlineData("150/0", false, 0, 0)]
    [InlineData("pages", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    public void The_scroll_step_reads_its_pages_and_its_rhythm(string argument, bool valid, int pages, int everyFrames)
    {
        Assert.Equal(valid, ScrollStep.TryParse(argument, out var step));
        if (valid)
        {
            Assert.Equal((pages, everyFrames), (step.Pages, step.EveryFrames));
            Assert.Equal(argument, step.ToString());
        }
    }

    [Fact]
    public void An_empty_run_has_no_frames_and_no_slow_column()
    {
        var summary = new FrameTable().Summarize();

        Assert.Equal(0, summary.Frames);
        Assert.Equal(0, summary.Slow.Frames);
        Assert.Equal(0, summary.PercentOver20);
        Assert.False(summary.MeetsGoal);
    }

    [Fact]
    public void The_cpu_load_is_a_share_of_every_logical_processor_split_into_window_core_and_others()
    {
        // 32 processors for one second: 32 s of processor time, 8 s of it busy.
        var before = new CabinetOS.Core.Platform.CpuReading(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(1000), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        var after = new CabinetOS.Core.Platform.CpuReading(TimeSpan.FromSeconds(108), TimeSpan.FromSeconds(1032), TimeSpan.FromSeconds(11.6), TimeSpan.FromSeconds(5.32));

        var load = CabinetOS.Core.Platform.CpuLoad.Between(before, after);

        Assert.Equal(25, load.TotalPercent, 3);
        Assert.Equal(5, load.UiPercent, 3);
        Assert.Equal(1, load.CorePercent, 3);
        Assert.Equal(19, load.OthersPercent, 3);
        // No time passed: no load, rather than a division by zero.
        Assert.Equal(0, CabinetOS.Core.Platform.CpuLoad.Between(after, after).TotalPercent);
        // A real reading of this machine is a load between 0 and 100 %.
        var now = CabinetOS.Core.Platform.CpuLoad.Read(null);
        Thread.Sleep(50);
        Assert.InRange(CabinetOS.Core.Platform.CpuLoad.Between(now, CabinetOS.Core.Platform.CpuLoad.Read(null)).TotalPercent, 0, 100);
    }

    [Fact]
    public void Each_part_is_timed_between_frames_and_taken_once()
    {
        FrameParts.Enabled = true;
        try
        {
            _ = FrameParts.Take();
            var started = FrameParts.Start();
            Thread.Sleep(5);
            FrameParts.Stop(FramePart.Details, started);

            var first = FrameParts.Take();
            var second = FrameParts.Take();

            Assert.InRange(first[(int)FramePart.Details], 4, 500);
            Assert.Equal(0, first[(int)FramePart.Bind]);
            Assert.All(second, ms => Assert.Equal(0, ms));
        }
        finally
        {
            FrameParts.Enabled = false;
        }
        // Off: no time is taken, and nothing is added.
        Assert.Equal(0, FrameParts.Start());
        FrameParts.Stop(FramePart.Details, 0);
        Assert.All(FrameParts.Take(), ms => Assert.Equal(0, ms));
    }
}
