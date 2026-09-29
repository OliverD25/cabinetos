using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CabinetOS.Core.Platform;

/// <summary>The few things the UI asks Windows itself, outside any window.</summary>
public static class WindowsPlatform
{
    /// <summary>The oldest build CabinetOS runs on: Windows 11 22H2 (ADR 0004).</summary>
    public const int MinimumBuild = 22621;

    /// <summary>Whether <paramref name="version"/> is Windows 11 22H2 or newer.</summary>
    public static bool IsSupported(Version version) =>
        version.Major > 10 || (version.Major == 10 && version.Build >= MinimumBuild);

    /// <summary>
    /// Shows a plain Windows message box, for errors that happen before (or
    /// instead of) the window: an unsupported Windows version.
    /// </summary>
    public static void ShowError(string title, string text) =>
        PInvoke.MessageBox(default, text, title, MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONERROR);

    /// <summary>
    /// Lets the core bring a window to the front: <c>open_path</c> runs in the
    /// core, a background process, and Windows would otherwise open the
    /// application behind this window (docs/ipc.md, "Opening files").
    /// Returns false when Windows refused; the file still opens.
    /// </summary>
    public static bool AllowForeground(int processId) => PInvoke.AllowSetForegroundWindow((uint)processId);

    /// <summary>The calling thread's ID, as Windows numbers threads.</summary>
    public static uint CurrentThreadId() => PInvoke.GetCurrentThreadId();

    /// <summary>
    /// The window that holds the keyboard in the input queue of thread
    /// <paramref name="threadId"/>: its class and the process that owns it,
    /// or null when none has it. A WebView2 page's input window belongs to
    /// its browser process, so the process says which page has the keys.
    /// </summary>
    public static unsafe (nint Window, string Class, int ProcessId)? KeyboardFocus(uint threadId)
    {
        var info = new Windows.Win32.UI.WindowsAndMessaging.GUITHREADINFO { cbSize = (uint)sizeof(Windows.Win32.UI.WindowsAndMessaging.GUITHREADINFO) };
        if (!PInvoke.GetGUIThreadInfo(threadId, ref info) || info.hwndFocus.IsNull)
        {
            return null;
        }
        uint processId;
        _ = PInvoke.GetWindowThreadProcessId(info.hwndFocus, &processId);
        var name = stackalloc char[128];
        var length = PInvoke.GetClassName(info.hwndFocus, name, 128);
        return ((nint)info.hwndFocus.Value, new string(name, 0, Math.Max(0, length)), (int)processId);
    }

    /// <summary>Window <paramref name="window"/>'s rectangle on the screen, in pixels, or null.</summary>
    public static (int Left, int Top, int Right, int Bottom)? ScreenRect(nint window) =>
        PInvoke.GetWindowRect(new Windows.Win32.Foundation.HWND(window), out var rect)
            ? (rect.left, rect.top, rect.right, rect.bottom)
            : null;

    /// <summary>Where the client area of <paramref name="window"/> starts on the screen, in pixels.</summary>
    public static (int X, int Y) ClientOrigin(nint window)
    {
        var point = new System.Drawing.Point(0, 0);
        _ = PInvoke.ClientToScreen(new Windows.Win32.Foundation.HWND(window), ref point);
        return (point.X, point.Y);
    }
}
