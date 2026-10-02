namespace CabinetOS.Core.Presentation;

/// <summary>
/// A click that leaves a web page (the terminal, a tool) for a control of the
/// window. The page's browser has Windows' keyboard focus, so the click gives
/// it to WinUI's input window (any click that brings Windows' focus there from
/// another window does), and WinUI then moves XAML's focus a second time,
/// by the pointer, to the window's root (a ScrollViewer around all the
/// window's content). The control the click focused loses it, and every key
/// after the click reaches the window with no pane, no box and no binding
/// context behind it: the real-key runs of 2026-10-02 clicked from the
/// terminal into the left pane, and Home, Enter and Backspace did nothing
/// (docs/log/2026-10-02/terminal-click-focus-report.md). Nothing else in the
/// window ever focuses its root, so such a move is refused and the clicked
/// control keeps the keyboard.
/// </summary>
public static class ClickFocus
{
    /// <summary>
    /// Whether to refuse a focus move: <paramref name="byPointer"/> (the move's
    /// focus state is the pointer's), <paramref name="toWindowRoot"/> (it goes
    /// to an element around the window's content, none of its controls),
    /// <paramref name="fromWindowControl"/> (XAML's focus is on a control of
    /// the window now) and <paramref name="fromPage"/> (that control is a web
    /// page, whose keys are its browser's: its own hand-over decides there).
    /// </summary>
    public static bool RefuseMove(bool byPointer, bool toWindowRoot, bool fromWindowControl, bool fromPage) =>
        byPointer && toWindowRoot && fromWindowControl && !fromPage;
}
