namespace CabinetOS.Core.Diagnostics;

/// <summary>The parts of the UI thread's work that a scroll causes (docs/ui.md, "Scrolling").</summary>
public enum FramePart
{
    /// <summary>The rows' measure pass. The repeater makes, recycles and binds rows in it, so it holds <see cref="Bind"/>.</summary>
    Measure,

    /// <summary>The rows' arrange pass.</summary>
    Arrange,

    /// <summary>Rows bound to their entries, with their marks (inside <see cref="Measure"/>).</summary>
    Bind,

    /// <summary>A page of type names and icon keys (<c>describe_entries</c>) applied to the rows.</summary>
    Details,

    /// <summary>Icons decoded and put on the rows.</summary>
    Icons,

    /// <summary>The selection's marks on the rows after the focus moved.</summary>
    Selection,

    /// <summary>The status bar and the window's other answers to a selection change.</summary>
    Status,

    /// <summary><c>describe_entries</c> requests made and sent.</summary>
    Requests,

    /// <summary>The rows' own measure (their texts laid out), inside <see cref="Measure"/>.</summary>
    RowMeasure,
}

/// <summary>
/// The time of each <see cref="FramePart"/> since the last frame, kept only
/// while frame statistics are on (<c>CABINETOS_UI_FRAMESTATS=1</c>). Only
/// the UI thread calls it, so nothing is locked.
/// </summary>
public static class FrameParts
{
    /// <summary>How many parts there are.</summary>
    public const int Count = 9;

    private static readonly long[] Ticks = new long[Count];
    private static readonly long[] Calls = new long[Count];

    /// <summary>Whether the parts are timed.</summary>
    public static bool Enabled { get; set; }

    /// <summary>A start time for <see cref="Stop"/>, or 0 while timing is off.</summary>
    public static long Start() => Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

    /// <summary>Adds the time since <paramref name="started"/> to <paramref name="part"/>; nothing for a start of 0.</summary>
    public static void Stop(FramePart part, long started)
    {
        if (started != 0)
        {
            Ticks[(int)part] += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            Calls[(int)part]++;
        }
    }

    /// <summary>How many times <paramref name="part"/> was timed since the program started (a run subtracts two readings).</summary>
    public static long CallsOf(FramePart part) => Calls[(int)part];

    /// <summary>Times <paramref name="part"/> until the result is disposed (a <c>using</c> statement).</summary>
    public static PartTimer Time(FramePart part) => new(part, Start());

    /// <summary>The milliseconds of <paramref name="part"/> since the last <see cref="Take"/>, without starting again.</summary>
    public static double Peek(FramePart part) => Ticks[(int)part] * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Each part's milliseconds since the last call; the count starts again from zero.</summary>
    public static double[] Take()
    {
        var ms = new double[Count];
        for (var i = 0; i < Count; i++)
        {
            ms[i] = Ticks[i] * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Ticks[i] = 0;
        }
        return ms;
    }
}

/// <summary>A part's time from <see cref="FrameParts.Time"/> to <see cref="Dispose"/>.</summary>
public readonly struct PartTimer(FramePart part, long started) : IDisposable
{
    /// <summary>Adds the time since the start to the part.</summary>
    public void Dispose() => FrameParts.Stop(part, started);
}

/// <summary>
/// One frame: the gap since the frame before (what the user sees), WinUI's
/// time for the frame (<c>RenderedEventArgs.FrameDuration</c>: the layout it
/// ran and the drawing), each part's time on the UI thread in that gap, and
/// how much of the rows' layout ran inside WinUI's frame. Layout can also
/// run outside a frame, when the thread has nothing else to do: a frame of
/// 8 ms followed a 44 ms measure pass that ran before it.
/// </summary>
public sealed record FrameSample(double GapMs, double WorkMs, double[] PartMs, double LayoutInFrameMs = 0);

/// <summary>The mean gap, frame time, UI-thread work (<see cref="FrameTable.Busy"/>) and parts of some frames.</summary>
public sealed record FrameColumn(int Frames, double GapMs, double WorkMs, double[] PartMs, double BusyMs = 0);

/// <summary>What a run of frames came to: the counts the goal is about, and where the time went.</summary>
public sealed record FrameSummary(int Frames, int Over20, int Over33, double Median, double P95, double Worst,
    FrameColumn All, FrameColumn Slow, FrameColumn WorstFrame, int BusyOver16 = 0, double BusiestMs = 0, int BusyOver20 = 0, int BusyOver33 = 0)
{
    /// <summary>One frame at 60 Hz: UI-thread work beyond it drops a frame on a 60 Hz display.</summary>
    public const double FrameAt60HzMs = 16.7;

    /// <summary>A gap longer than this is a frame over 20 ms.</summary>
    public const double SlowMs = 20;

    /// <summary>A gap this long or longer dropped at least two frames at 60 Hz (two vsyncs are 33.3 ms).</summary>
    public const double DroppedMs = 33.4;

    /// <summary>The share of frames over 20 ms, in percent.</summary>
    public double PercentOver20 => Frames == 0 ? 0 : 100.0 * Over20 / Frames;

    /// <summary>The goal of Phase 5's scrolling: no frame over 33 ms, and fewer than 5 % over 20 ms.</summary>
    public bool MeetsGoal => Frames > 0 && Over33 == 0 && PercentOver20 < 5;
}

/// <summary>The frames of a run (a snapshot-aid scroll, or a second of the live check), summarized.</summary>
public sealed class FrameTable
{
    private readonly List<FrameSample> _frames = [];

    /// <summary>The frames so far.</summary>
    public int Count => _frames.Count;

    /// <summary>Adds a frame.</summary>
    public void Add(FrameSample frame) => _frames.Add(frame);

    /// <summary>The UI-thread work of every frame so far, added up.</summary>
    public double BusySum() => _frames.Sum(Busy);

    /// <summary>The counts and the columns: every frame, the frames over 20 ms, and the worst one.</summary>
    public FrameSummary Summarize()
    {
        if (_frames.Count == 0)
        {
            var none = Column([]);
            return new FrameSummary(0, 0, 0, 0, 0, 0, none, none, none);
        }
        var gaps = _frames.Select(f => f.GapMs).Order().ToList();
        var worst = _frames.MaxBy(f => f.GapMs)!;
        var busy = _frames.Select(Busy).ToList();
        return new FrameSummary(
            _frames.Count,
            _frames.Count(f => f.GapMs > FrameSummary.SlowMs),
            _frames.Count(f => f.GapMs >= FrameSummary.DroppedMs),
            Percentile(gaps, 0.5),
            Percentile(gaps, 0.95),
            worst.GapMs,
            Column(_frames),
            Column(_frames.Where(f => f.GapMs > FrameSummary.SlowMs).ToList()),
            Column([worst]),
            busy.Count(ms => ms > FrameSummary.FrameAt60HzMs),
            busy.Max(),
            // The same limits as the gaps: the live check's panel goal asks them of the UI-thread work instead.
            busy.Count(ms => ms > FrameSummary.SlowMs),
            busy.Count(ms => ms >= FrameSummary.DroppedMs));
    }

    /// <summary>
    /// The UI thread's work for a frame: WinUI's frame time, the rows' layout
    /// that ran outside it (binding happens inside the measure pass), and
    /// the parts that run outside layout (type-name pages, icons, marks, the
    /// status bar, requests). It does not depend on how often the display
    /// asks for frames.
    /// </summary>
    public static double Busy(FrameSample frame)
    {
        var layout = frame.PartMs[(int)FramePart.Measure] + frame.PartMs[(int)FramePart.Arrange];
        return frame.WorkMs + Math.Max(0, layout - frame.LayoutInFrameMs)
            + frame.PartMs[(int)FramePart.Details] + frame.PartMs[(int)FramePart.Icons]
            + frame.PartMs[(int)FramePart.Selection] + frame.PartMs[(int)FramePart.Status] + frame.PartMs[(int)FramePart.Requests];
    }

    private static FrameColumn Column(IReadOnlyList<FrameSample> frames)
    {
        var parts = new double[FrameParts.Count];
        if (frames.Count == 0)
        {
            return new FrameColumn(0, 0, 0, parts);
        }
        foreach (var frame in frames)
        {
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] += frame.PartMs[i];
            }
        }
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] /= frames.Count;
        }
        return new FrameColumn(frames.Count, frames.Average(f => f.GapMs), frames.Average(f => f.WorkMs), parts, frames.Average(Busy));
    }

    // The nearest-rank percentile of sorted values.
    private static double Percentile(IReadOnlyList<double> sorted, double share) =>
        sorted[Math.Clamp((int)Math.Ceiling(share * sorted.Count) - 1, 0, sorted.Count - 1)];
}

/// <summary>
/// The snapshot aid's <c>scroll:</c> step (docs/ui.md, "Scrolling"):
/// <c>scroll:150</c> is 150 PageDowns at 30 a second, as a held key repeats;
/// <c>scroll:150/2</c> is one PageDown every second frame.
/// </summary>
public sealed record ScrollStep(int Pages, int EveryFrames)
{
    /// <summary>Reads <c>pages</c> or <c>pages/frames</c>; both must be positive.</summary>
    public static bool TryParse(string argument, out ScrollStep step)
    {
        step = new ScrollStep(0, 0);
        var parts = argument.Split('/');
        if (parts.Length > 2 || !Positive(parts[0], out var pages))
        {
            return false;
        }
        var every = 0;
        if (parts.Length == 2 && !Positive(parts[1], out every))
        {
            return false;
        }
        step = new ScrollStep(pages, every);
        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => EveryFrames > 0 ? $"{Pages}/{EveryFrames}" : $"{Pages}";

    private static bool Positive(string text, out int value) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value) && value > 0;
}
