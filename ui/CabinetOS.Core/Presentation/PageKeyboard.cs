namespace CabinetOS.Core.Presentation;

/// <summary>What the window does next about a web page that should have the keyboard.</summary>
public enum PageKeyboardStep
{
    /// <summary>Windows sends the keys to the page: nothing to do.</summary>
    Done,

    /// <summary>Nobody wants the page to have the keys any more (it hid, or the keyboard went elsewhere).</summary>
    Stop,

    /// <summary>The page does not have the keys: take the focus away from it and give it back.</summary>
    HandOverAgain,

    /// <summary>Tried often enough; the log says so and the window's own key handling takes over.</summary>
    GiveUp,
}

/// <summary>
/// Whether a WebView2 page (the terminal, a Tool Extension) really has the
/// keyboard. XAML's focus on a WebView2 is not enough: WinUI moves the keys
/// into the page's browser only when the page's controller is visible at
/// that moment, and a page whose dock or pane was collapsed a moment ago
/// becomes visible to its controller only at the next frame. WinUI then
/// keeps the move pending and never makes it, so XAML's focus sits on the
/// page while Windows sends the keys to the window's own input window,
/// where the window ignores them (they belong to the page). The live check
/// of 2026-09-29 lost every key that way after Ctrl+P with the Markdown
/// Preview open in the other pane. The input window that has the keys
/// tells: a page's is Chromium's (<c>Chrome_WidgetWin_*</c>,
/// <c>Chrome_RenderWidgetHostHWND</c>), the window's is WinUI's.
/// </summary>
public static class PageKeyboard
{
    /// <summary>How often the window takes the focus away and gives it back before it gives up.</summary>
    public const int Attempts = 3;

    /// <summary>How long after a hand-over the window looks whether the keys arrived: a frame, and Chromium's own move.</summary>
    public static readonly TimeSpan CheckAfter = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// How many frames XAML must have drawn after a hand-over before the window looks, however long that takes (the
    /// window waits for them at most <see cref="FramesWait"/>). A page's browser takes the keys only while its controller
    /// is visible, and WinUI makes it visible at a frame it draws after the page was shown. A quiet machine draws one in
    /// 16 ms, so <see cref="CheckAfter"/> decides. A busy one draws a frame every few hundred milliseconds (0.5 s and more
    /// on the laptop with two test runs beside), and a window that looked after 150 ms, handed the keys over again
    /// and gave up after 600 ms had made every hand-over before the page could take the keys: the terminal had no
    /// keyboard in 1 to 8 of 20 rounds of the window test, and a window whose log says "did not get the keyboard"
    /// has no way to get it until a key reaches the window.
    /// </summary>
    public const int FramesBeforeCheck = 2;

    /// <summary>The longest the window waits for <see cref="FramesBeforeCheck"/> frames: with nothing drawn (the window is minimised) none come.</summary>
    public static readonly TimeSpan FramesWait = TimeSpan.FromSeconds(3);

    /// <summary>Whether the window of class <paramref name="windowClass"/> is a page's input window.</summary>
    public static bool IsPageWindow(string? windowClass) =>
        windowClass is not null && windowClass.StartsWith("Chrome_", StringComparison.Ordinal);

    /// <summary>
    /// What to do <see cref="CheckAfter"/> a hand-over: <paramref name="stillWanted"/>
    /// (the page is shown and nothing else asked for the keyboard since),
    /// <paramref name="xamlOnPage"/> (XAML's focus is on the page),
    /// <paramref name="windowClass"/> (the input window that has the keys),
    /// and <paramref name="attempt"/> (hand-overs made again so far).
    /// </summary>
    public static PageKeyboardStep Next(bool stillWanted, bool xamlOnPage, string? windowClass, int attempt)
    {
        if (!stillWanted || !xamlOnPage)
        {
            return PageKeyboardStep.Stop;
        }
        if (IsPageWindow(windowClass))
        {
            return PageKeyboardStep.Done;
        }
        return attempt < Attempts ? PageKeyboardStep.HandOverAgain : PageKeyboardStep.GiveUp;
    }

    /// <summary>
    /// A key reached the window while XAML's focus is on a page. The page
    /// saw it too (and passes the window's keys back itself) only when the
    /// keys go to the page's input window; otherwise the window must act on
    /// it, or the key is lost.
    /// </summary>
    public static bool PageSawKey(string? windowClass) => IsPageWindow(windowClass);
}
