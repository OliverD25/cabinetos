namespace CabinetOS.Core.Terminal;

/// <summary>
/// Where a file pane lies across the dock: its left edge and its width, in device-independent pixels,
/// measured from the dock's left edge (the dock under the panes starts where the panes do).
/// </summary>
public readonly record struct PaneSpan(double X, double Width);

/// <summary>One half of the split dock: the pane it sits under, its place, its shown session, and its tabs.</summary>
/// <param name="Pane">The file pane it belongs to: 0 left, 1 right.</param>
/// <param name="X">Where the half starts, from the dock's left edge.</param>
/// <param name="Width">How wide the half is; the width of its pane.</param>
/// <param name="Session">The session the half shows; null when its pane has none (the half shows the hint).</param>
/// <param name="Tabs">The sessions of the half's pane, in the header's order: its tab row.</param>
public sealed record SplitHalf(int Pane, double X, double Width, ulong? Session, IReadOnlyList<ulong> Tabs)
{
    /// <summary>Whether the pane has no session: the half shows <see cref="TerminalSplitLayout.Hint"/> and nothing else.</summary>
    public bool Empty => Session is null;
}

/// <summary>
/// The split mirror (docs/ui.md, "The terminal"): Ctrl+\ in the terminal splits the Tool Dock under the two
/// panes, the left pane's sessions under the left pane and the right pane's under the right. Each half has its
/// own tab row (only its pane's sessions) and its own header; the halves are as wide as the panes and lie under
/// them, so a half follows when the panes' divider moves. The rules are here, away from the window, so they are
/// tested without one.
/// </summary>
public static class TerminalSplitLayout
{
    /// <summary>What an empty half shows: no session of its pane, and the key that starts one.</summary>
    public const string Hint = "Ctrl+` starts a shell for this pane";

    /// <summary>
    /// Whether the dock shows two halves: the setting <c>terminal.split</c> is on and the dock sits under the
    /// panes. Beside them (<c>ui.layout</c> <c>right</c>) there is nothing to be under, so the dock shows one
    /// view and the setting stays for when it goes back under them.
    /// </summary>
    public static bool Active(bool setting, bool dockUnderPanes) => setting && dockUnderPanes;

    /// <summary>
    /// The halves. With two panes shown, one under each, as wide as its pane; with one pane shown, one half
    /// that is that pane's (the left, which is the pane a single pane always is) across the whole dock, and
    /// the right pane's sessions wait until the second pane comes back. Each half's session is the pane's one
    /// shown last (<see cref="ShownIn"/>).
    /// </summary>
    /// <param name="tabs">The sessions in the header's order.</param>
    /// <param name="dual">Whether two file panes are shown.</param>
    /// <param name="left">The left pane's place across the dock.</param>
    /// <param name="right">The right pane's place across the dock.</param>
    /// <param name="dockWidth">The dock's width.</param>
    public static IReadOnlyList<SplitHalf> Halves(IReadOnlyList<TabFacts> tabs, bool dual, PaneSpan left, PaneSpan right, double dockWidth)
    {
        if (!dual)
        {
            return [Half(tabs, 0, new PaneSpan(0, dockWidth), dockWidth)];
        }
        return [Half(tabs, 0, left, dockWidth), Half(tabs, 1, right, dockWidth)];
    }

    /// <summary>
    /// The session a pane's half shows: the pane's one shown last (a tab that just opened counts as shown), also
    /// when its shell has ended and its tab waits to close; null when the pane has no session.
    /// </summary>
    public static ulong? ShownIn(IEnumerable<TabFacts> tabs, int pane) =>
        tabs.Where(t => t.Pane == pane)
            .OrderByDescending(t => t.LastShown)
            .ThenByDescending(t => t.Session)
            .Select(t => (ulong?)t.Session)
            .FirstOrDefault();

    /// <summary>The sessions of one pane, in the header's order.</summary>
    public static IReadOnlyList<ulong> Sessions(IEnumerable<TabFacts> tabs, int pane) =>
        [.. tabs.Where(t => t.Pane == pane).Select(t => t.Session)];

    /// <summary>
    /// The columns of the dock's header and body for two halves: how wide the first half's column is (from the
    /// dock's left edge to the first half's right edge) and the gap before the second half, which is the
    /// panes' gap. With one half the gap is 0.
    /// </summary>
    public static (double First, double Gap) Columns(IReadOnlyList<SplitHalf> halves)
    {
        var first = halves[0].X + halves[0].Width;
        return (first, halves.Count > 1 ? Math.Max(0, halves[1].X - first) : 0);
    }

    /// <summary>
    /// Alt+[ and Alt+]: the session <paramref name="step"/> places from <paramref name="shown"/> among the
    /// tabs of the half that has the keyboard (<paramref name="pane"/>), round its ends. With no split the
    /// tabs are all of them (<paramref name="pane"/> null). Null when there is no tab to go to.
    /// </summary>
    public static ulong? CycleTarget(IReadOnlyList<TabFacts> tabs, int? pane, ulong? shown, int step) =>
        TerminalTabs.Cycle(pane is { } half ? Sessions(tabs, half) : [.. tabs.Select(t => t.Session)], shown, step);

    /// <summary>
    /// Ctrl+Shift+W: the session it closes, the shown one of the half that has the keyboard; null when that
    /// half shows the hint and there is nothing to close.
    /// </summary>
    public static ulong? CloseTarget(IReadOnlyList<SplitHalf> halves, int keyboardPane) =>
        halves.FirstOrDefault(h => h.Pane == keyboardPane)?.Session;

    /// <summary>
    /// What a half shows once <paramref name="closed"/> is gone: the pane's session shown last among the others,
    /// else null, the hint. The other half is not touched, and the split setting stays.
    /// </summary>
    public static ulong? AfterClose(IEnumerable<TabFacts> tabs, int pane, ulong closed) =>
        ShownIn(tabs.Where(t => t.Session != closed), pane);

    /// <summary>
    /// Ctrl+Shift+T: the pane the new session is bound to, and so the half it opens in. The key acts for the
    /// active pane, also while the other half has the keyboard, and with one pane shown the left is the only
    /// pane there is.
    /// </summary>
    public static int NewTabPane(bool dual, int activePane) => dual ? activePane : 0;

    /// <summary>
    /// The session the one view shows when the split goes away: the one the half that had the keyboard showed,
    /// else the other half's, else the last tab. Null without tabs.
    /// </summary>
    public static ulong? ShownWhenUnsplit(IReadOnlyList<TabFacts> tabs, int keyboardPane) =>
        ShownIn(tabs, keyboardPane) ?? ShownIn(tabs, 1 - keyboardPane) ?? tabs.Select(t => (ulong?)t.Session).LastOrDefault();

    /// <summary>
    /// The pane that counts as showing a session when Ctrl+` is pressed in <paramref name="activePane"/>
    /// (<see cref="TerminalSummoning"/>'s <c>ShownPane</c>): with the split on, the active pane's own half when
    /// it shows a session, whichever half has the keyboard; else the pane of the shown tab.
    /// </summary>
    public static int? SummonShownPane(bool split, IReadOnlyList<TabFacts> tabs, int activePane, int? shownPane) =>
        split && ShownIn(tabs, activePane) is not null ? activePane : shownPane;

    private static SplitHalf Half(IReadOnlyList<TabFacts> tabs, int pane, PaneSpan span, double dockWidth)
    {
        var x = Math.Clamp(span.X, 0, Math.Max(0, dockWidth - 1));
        var width = Math.Clamp(span.Width, 1, Math.Max(1, dockWidth - x));
        return new SplitHalf(pane, x, width, ShownIn(tabs, pane), Sessions(tabs, pane));
    }
}
