using System.Diagnostics;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Platform;
using Microsoft.UI.Xaml.Media;

namespace CabinetOS.Services;

/// <summary>
/// Measures the frames on the UI thread and logs one <c>frame stats</c> line
/// per second: frames drawn, the worst gap, the gaps over 20 and 33 ms, and
/// where the UI thread's time went (<see cref="FrameParts"/>, and WinUI's own
/// time for each frame). A run (the snapshot aid's <c>scroll:</c> step) adds
/// a <c>scroll run</c> line with its whole frame table and the machine's CPU
/// load. Off unless <c>CABINETOS_UI_FRAMESTATS=1</c>, because listening to
/// every frame keeps the window drawing even when nothing changes. Heavy
/// mode turns it on too (<c>heavyOnly</c>): its lines then have the target
/// <c>heavy::frames</c>, so the normal log stays as it was.
/// </summary>
public sealed class FrameMonitor
{
    /// <summary>The environment variable that turns the monitor on.</summary>
    public const string EnableEnv = "CABINETOS_UI_FRAMESTATS";

    private const string Target = "cabinetos_ui::frames";

    // The parts in the order FramePart lists them, as the log names them.
    private static readonly string[] PartNames = ["measure", "arrange", "bind", "details", "icons", "selection", "status", "requests", "row_measure"];

    private readonly Func<int?> _corePid;
    private readonly bool _heavyOnly;
    private bool _started;
    private long _last;
    private long _secondStart;
    private double _workMs;
    private double _layoutInFrameMs;
    private FrameTable _second = new();
    private FrameTable? _run;
    private string _runLabel = "";
    private CpuReading _runCpu;
    private long _runStart;
    private long[] _runCalls = [];

    /// <summary>A monitor; <paramref name="corePid"/> names the core's process for the CPU load of a run.</summary>
    public FrameMonitor(Func<int?> corePid, bool heavyOnly = false)
    {
        _corePid = corePid;
        _heavyOnly = heavyOnly;
    }

    /// <summary>Whether the variable asks for frame statistics.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable(EnableEnv) == "1";

    /// <summary>Starts listening to frames.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        FrameParts.Enabled = true;
        _secondStart = Stopwatch.GetTimestamp();
        _last = 0;
        _second = new FrameTable();
        CompositionTarget.Rendering += OnRendering;
        CompositionTarget.Rendered += OnRendered;
        Log("frame monitor on");
    }

    /// <summary>Stops listening to frames (heavy mode went off), so the window draws only when it has something to draw.</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }
        _started = false;
        CompositionTarget.Rendering -= OnRendering;
        CompositionTarget.Rendered -= OnRendered;
        FrameParts.Enabled = false;
        Log("frame monitor off");
    }

    /// <summary>Starts collecting a run's frames.</summary>
    public void BeginRun(string label)
    {
        _run = new FrameTable();
        _runLabel = label;
        _runStart = Stopwatch.GetTimestamp();
        _runCpu = CpuLoad.Read(_corePid());
        _runCalls = [.. Enumerable.Range(0, FrameParts.Count).Select(i => FrameParts.CallsOf((FramePart)i))];
    }

    /// <summary>Ends the run and logs its table (<c>scroll run</c>); null when no run was going.</summary>
    public FrameSummary? EndRun(params LogField[] extra)
    {
        if (_run is not { } run)
        {
            return null;
        }
        _run = null;
        var load = CpuLoad.Between(_runCpu, CpuLoad.Read(_corePid()));
        var summary = run.Summarize();
        var fields = new List<LogField>
        {
            new("label", _runLabel),
            new("seconds", Math.Round(Stopwatch.GetElapsedTime(_runStart).TotalSeconds, 2)),
            new("frames", summary.Frames),
            new("over_20ms", summary.Over20),
            new("over_33ms", summary.Over33),
            new("percent_over_20ms", Math.Round(summary.PercentOver20, 1)),
            new("median_ms", Math.Round(summary.Median, 1)),
            new("p95_ms", Math.Round(summary.P95, 1)),
            new("worst_ms", Math.Round(summary.Worst, 1)),
            new("goal_met", summary.MeetsGoal),
            // Frames whose UI-thread work passed one 60 Hz frame: what would drop at 60 Hz, whatever the display asks for.
            new("busy_over_16ms", summary.BusyOver16),
            new("percent_busy_over_16ms", summary.Frames == 0 ? 0 : Math.Round(100.0 * summary.BusyOver16 / summary.Frames, 1)),
            new("busiest_ms", Math.Round(summary.BusiestMs, 1)),
            new("cpu_total_percent", Math.Round(load.TotalPercent, 1)),
            new("cpu_ui_percent", Math.Round(load.UiPercent, 1)),
            new("cpu_core_percent", Math.Round(load.CorePercent, 1)),
            new("cpu_others_percent", Math.Round(load.OthersPercent, 1)),
        };
        fields.AddRange(extra);
        for (var i = 0; i < PartNames.Length; i++)
        {
            // How many times each part ran in the run: rows bound, pages applied, requests sent.
            fields.Add(new($"{PartNames[i]}_calls", FrameParts.CallsOf((FramePart)i) - _runCalls[i]));
        }
        AddColumn(fields, "all", summary.All);
        AddColumn(fields, "slow", summary.Slow);
        AddColumn(fields, "worst", summary.WorstFrame);
        Diag.Info(Target, "scroll run", [.. fields]);
        return summary;
    }

    private static void AddColumn(List<LogField> fields, string name, FrameColumn column)
    {
        fields.Add(new($"{name}_frames", column.Frames));
        fields.Add(new($"{name}_gap_ms", Math.Round(column.GapMs, 2)));
        fields.Add(new($"{name}_work_ms", Math.Round(column.WorkMs, 2)));
        fields.Add(new($"{name}_busy_ms", Math.Round(column.BusyMs, 2)));
        for (var i = 0; i < PartNames.Length; i++)
        {
            fields.Add(new($"{name}_{PartNames[i]}_ms", Math.Round(column.PartMs[i], 2)));
        }
    }

    // After WinUI drew a frame: its own time for it, and the rows' layout since the frame began (the layout it ran).
    private void OnRendered(object? sender, RenderedEventArgs e)
    {
        _workMs += e.FrameDuration.TotalMilliseconds;
        // The parts were taken when the frame began, so what they hold now ran in it.
        _layoutInFrameMs += FrameParts.Peek(FramePart.Measure) + FrameParts.Peek(FramePart.Arrange);
    }

    private void OnRendering(object? sender, object e)
    {
        var now = Stopwatch.GetTimestamp();
        var parts = FrameParts.Take();
        if (_last != 0)
        {
            var frame = new FrameSample(Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds, _workMs, parts, _layoutInFrameMs);
            _second.Add(frame);
            _run?.Add(frame);
            if (_run is not null && frame.GapMs >= FrameSummary.DroppedMs)
            {
                // During a run each dropped frame is told apart, so a rare slow one can be traced.
                var fields = new List<LogField> { new("gap_ms", Math.Round(frame.GapMs, 1)), new("work_ms", Math.Round(frame.WorkMs, 1)) };
                for (var i = 0; i < PartNames.Length; i++)
                {
                    fields.Add(new($"{PartNames[i]}_ms", Math.Round(parts[i], 2)));
                }
                Diag.Info(Target, "slow frame", [.. fields]);
            }
        }
        _last = now;
        _workMs = 0;
        _layoutInFrameMs = 0;
        if (Stopwatch.GetElapsedTime(_secondStart, now) >= TimeSpan.FromSeconds(1))
        {
            LogSecond();
            _secondStart = now;
            _second = new FrameTable();
        }
    }

    private void LogSecond()
    {
        var summary = _second.Summarize();
        var all = summary.All;
        var fields = new List<LogField>
        {
            new("frames", summary.Frames),
            new("worst_ms", Math.Round(summary.Worst, 1)),
            new("gaps_over_20ms", summary.Over20),
            new("gaps_over_33ms", summary.Over33),
            new("busy_over_16ms", summary.BusyOver16),
            // Sums over the second, so seconds add up: ms per frame is a sum divided by frames.
            new("work_ms", Math.Round(all.WorkMs * all.Frames, 1)),
            new("busy_ms", Math.Round(_second.BusySum(), 1)),
        };
        for (var i = 0; i < PartNames.Length; i++)
        {
            fields.Add(new($"{PartNames[i]}_ms", Math.Round(all.PartMs[i] * all.Frames, 1)));
        }
        Log("frame stats", [.. fields]);
    }

    private void Log(string message, params LogField[] fields)
    {
        if (_heavyOnly)
        {
            Diag.Heavy("frames", message, fields);
        }
        else
        {
            Diag.Info(Target, message, fields);
        }
    }
}
