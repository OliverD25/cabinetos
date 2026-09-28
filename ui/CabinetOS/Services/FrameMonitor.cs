using System.Diagnostics;
using CabinetOS.Core.Diagnostics;
using Microsoft.UI.Xaml.Media;

namespace CabinetOS.Services;

/// <summary>
/// Measures the time between frames on the UI thread and logs one line per
/// second: frames drawn and the worst gap. Off unless
/// <c>CABINETOS_UI_FRAMESTATS=1</c>, because listening to every frame keeps
/// the window drawing even when nothing changes.
/// </summary>
public sealed class FrameMonitor
{
    /// <summary>The environment variable that turns the monitor on.</summary>
    public const string EnableEnv = "CABINETOS_UI_FRAMESTATS";

    private long _last;
    private long _windowStart;
    private int _frames;
    private double _worstMs;
    private int _over20;
    private int _over33;

    /// <summary>Whether the variable asks for frame statistics.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable(EnableEnv) == "1";

    /// <summary>Starts listening to frames.</summary>
    public void Start()
    {
        _windowStart = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
        Diag.Info("cabinetos_ui::frames", "frame monitor on");
    }

    private void OnRendering(object? sender, object e)
    {
        var now = Stopwatch.GetTimestamp();
        if (_last != 0)
        {
            var gapMs = Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds;
            _frames++;
            _worstMs = Math.Max(_worstMs, gapMs);
            if (gapMs > 20)
            {
                _over20++;
            }
            if (gapMs > 33.4)
            {
                _over33++;
            }
        }
        _last = now;
        if (Stopwatch.GetElapsedTime(_windowStart, now) >= TimeSpan.FromSeconds(1))
        {
            Diag.Info("cabinetos_ui::frames", "frame stats",
                new LogField("frames", _frames),
                new LogField("worst_ms", Math.Round(_worstMs, 1)),
                new LogField("gaps_over_20ms", _over20),
                new LogField("gaps_over_33ms", _over33));
            _windowStart = now;
            _frames = 0;
            _worstMs = 0;
            _over20 = 0;
            _over33 = 0;
        }
    }
}
