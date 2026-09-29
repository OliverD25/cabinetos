using System.Diagnostics;
using Windows.Win32;

namespace CabinetOS.Core.Platform;

/// <summary>
/// CPU times at one moment: the whole machine's, summed over all its logical
/// processors (busy, and busy plus idle), and those of the window's process
/// and its core's.
/// </summary>
public readonly record struct CpuReading(TimeSpan MachineBusy, TimeSpan MachineAll, TimeSpan Ui, TimeSpan Core);

/// <summary>
/// How busy the machine was between two readings, in percent of all its
/// logical processors (Task Manager's "CPU"): the window, its core, and
/// everything else. A scroll's frame times mean little on a busy machine,
/// so each measured run states it (docs/ui.md, "Scrolling").
/// </summary>
public sealed record CpuLoad(double TotalPercent, double UiPercent, double CorePercent)
{
    /// <summary>Every other process: what the machine did that this window and its core did not.</summary>
    public double OthersPercent => Math.Max(0, TotalPercent - UiPercent - CorePercent);

    /// <summary>The load between <paramref name="before"/> and <paramref name="after"/>.</summary>
    public static CpuLoad Between(CpuReading before, CpuReading after)
    {
        var all = (after.MachineAll - before.MachineAll).Ticks;
        if (all <= 0)
        {
            return new CpuLoad(0, 0, 0);
        }
        double Percent(TimeSpan from, TimeSpan to) => Math.Clamp(100.0 * (to - from).Ticks / all, 0, 100);
        return new CpuLoad(
            Percent(before.MachineBusy, after.MachineBusy),
            Percent(before.Ui, after.Ui),
            Percent(before.Core, after.Core));
    }

    /// <summary>Reads the times now: the machine's (<c>GetSystemTimes</c>), this process's and the core's (<paramref name="corePid"/>, when it runs).</summary>
    public static CpuReading Read(int? corePid)
    {
        TimeSpan busy = default, all = default;
        if (PInvoke.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            // Kernel time includes the idle time.
            var idleTicks = Ticks(idle);
            var allTicks = Ticks(kernel) + Ticks(user);
            busy = TimeSpan.FromTicks(allTicks - idleTicks);
            all = TimeSpan.FromTicks(allTicks);
        }
        return new CpuReading(busy, all, ProcessTime(Environment.ProcessId), corePid is { } pid ? ProcessTime(pid) : TimeSpan.Zero);
    }

    private static long Ticks(System.Runtime.InteropServices.ComTypes.FILETIME time) =>
        ((long)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    private static TimeSpan ProcessTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.TotalProcessorTime;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return TimeSpan.Zero;
        }
    }
}
