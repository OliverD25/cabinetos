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
}
