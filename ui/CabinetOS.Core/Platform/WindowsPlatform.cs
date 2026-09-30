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

    /// <summary>
    /// The first window of class <paramref name="className"/> among the
    /// descendants of <paramref name="parent"/>, depth first, or 0.
    /// </summary>
    public static unsafe nint FindDescendant(nint parent, string className)
    {
        var child = Windows.Win32.Foundation.HWND.Null;
        while (true)
        {
            child = PInvoke.FindWindowEx(new Windows.Win32.Foundation.HWND(parent), child, null, null);
            if (child.IsNull)
            {
                return 0;
            }
            if (ClassOf(child) == className)
            {
                return (nint)child.Value;
            }
            if (FindDescendant((nint)child.Value, className) is not 0 and var found)
            {
                return found;
            }
        }
    }

    /// <summary>
    /// Posts one key message to <paramref name="window"/>, as Windows sends a
    /// real key's: down or up, with Alt held (a system key) or not. The keys
    /// of the cursor block are the extended keys they are on a keyboard.
    /// </summary>
    public static void PostKey(nint window, int virtualKey, bool up, bool alt)
    {
        var scan = PInvoke.MapVirtualKey((uint)virtualKey, Windows.Win32.UI.Input.KeyboardAndMouse.MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC);
        var extended = virtualKey is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x6F;
        var lParam = 1u | (scan << 16) | (extended ? 1u << 24 : 0) | (alt ? 1u << 29 : 0) | (up ? (1u << 30) | (1u << 31) : 0);
        var message = (alt ? 0x104u : 0x100u) + (up ? 1u : 0u);
        _ = PInvoke.PostMessage(new Windows.Win32.Foundation.HWND(window), message, (nuint)virtualKey, (nint)(int)lParam);
    }

    /// <summary>The calling thread's key state: 256 bytes, the high bit of a key's byte set while it is down.</summary>
    public static byte[] KeyState()
    {
        var state = new byte[256];
        _ = PInvoke.GetKeyboardState(state);
        return state;
    }

    /// <summary>Makes <paramref name="state"/> the calling thread's key state (what <see cref="KeyState"/> reads).</summary>
    public static void SetKeyState(byte[] state) => _ = PInvoke.SetKeyboardState(state);

    private static unsafe string ClassOf(Windows.Win32.Foundation.HWND window)
    {
        var name = stackalloc char[128];
        var length = PInvoke.GetClassName(window, name, 128);
        return new string(name, 0, Math.Max(0, length));
    }

    /// <summary>Where the client area of <paramref name="window"/> starts on the screen, in pixels.</summary>
    public static (int X, int Y) ClientOrigin(nint window)
    {
        var point = new System.Drawing.Point(0, 0);
        _ = PInvoke.ClientToScreen(new Windows.Win32.Foundation.HWND(window), ref point);
        return (point.X, point.Y);
    }
}
