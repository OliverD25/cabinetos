namespace CabinetOS.Core.Presentation;

/// <summary>
/// When the window last had a key or the pointer, so work done ahead that
/// costs a frame (the marketplace view's first layout) waits until the user
/// has stopped for a moment and never takes a frame from typing or
/// scrolling. A window that had no input yet is quiet.
/// </summary>
public sealed class InputQuiet(Func<long> nowMilliseconds, long quietMs = InputQuiet.DefaultQuietMs)
{
    /// <summary>
    /// How long without input counts as quiet: longer than the gap between
    /// a held key's repeats and a wheel's notches, so one that goes on is
    /// never taken for a pause.
    /// </summary>
    public const long DefaultQuietMs = 1_000;

    private long? _last;

    /// <summary>A key or the pointer, now.</summary>
    public void Touch() => _last = nowMilliseconds();

    /// <summary>How long to wait until the window is quiet, in milliseconds; 0 when it is.</summary>
    public long WaitMs => _last is { } last ? Math.Max(0, last + quietMs - nowMilliseconds()) : 0;

    /// <summary>Whether the window had no input for the quiet time.</summary>
    public bool IsQuiet => WaitMs == 0;
}
