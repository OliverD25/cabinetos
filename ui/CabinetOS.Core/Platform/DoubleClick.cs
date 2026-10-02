using Windows.Win32;

namespace CabinetOS.Core.Platform;

/// <summary>The double-click time Windows keeps for the user (Settings, Bluetooth &amp; devices, Mouse).</summary>
public static class DoubleClick
{
    private const long DefaultMilliseconds = 500;

    /// <summary>The longest time between two clicks that make a double-click, in milliseconds; 500 when Windows says none.</summary>
    public static long Milliseconds()
    {
        var time = PInvoke.GetDoubleClickTime();
        return time > 0 ? time : DefaultMilliseconds;
    }
}
