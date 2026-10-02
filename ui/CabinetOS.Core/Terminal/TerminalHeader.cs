namespace CabinetOS.Core.Terminal;

/// <summary>
/// How one tab of the terminal's header reads: the profile, a badge with its pane, and its mode as a
/// toggle (or as plain text with a tooltip when the profile cannot be linked).
/// </summary>
/// <param name="Title">The profile's name.</param>
/// <param name="Badge"><c>[Left]</c> or <c>[Right]</c>, drawn in the accent colour of the active pane.</param>
/// <param name="ModeText"><c>Locked</c> or <c>Linked</c>.</param>
/// <param name="ModeToggles">Whether the mode is a button that runs <c>terminal.setMode</c> with <paramref name="NextMode"/>.</param>
/// <param name="ModeTip">The mode's tooltip: what it means, and why a profile cannot be linked.</param>
/// <param name="ModeName">The mode's accessible name, unique among the tabs (the snapshot aid's <c>click:</c> finds it by this).</param>
/// <param name="NextMode">The mode the toggle sets.</param>
public sealed record TerminalTabLook(
    string Title,
    string Badge,
    string ModeText,
    bool ModeToggles,
    string ModeTip,
    string ModeName,
    TerminalMode NextMode);

/// <summary>The texts of the terminal's header (docs/ui.md, "The terminal"), on their own so they are tested without a window.</summary>
public static class TerminalHeader
{
    /// <summary>The look of the tab of a <paramref name="profile"/> session bound to <paramref name="pane"/> (0 left, 1 right).</summary>
    public static TerminalTabLook Tab(string profile, int pane, TerminalMode mode, bool linkable)
    {
        var side = pane == 1 ? "right" : "left";
        var modeText = mode == TerminalMode.Linked ? "Linked" : "Locked";
        var tip = (linkable, mode) switch
        {
            (false, _) => $"Locked: {profile} cannot follow a pane, because no prompt hook can be added to it.",
            (true, TerminalMode.Linked) => $"Linked to the {side} pane. Click to lock it.",
            _ => $"Locked: the shell stays where you take it. Click to link it to the {side} pane.",
        };
        return new TerminalTabLook(
            profile,
            pane == 1 ? "[Right]" : "[Left]",
            modeText,
            linkable,
            tip,
            $"{modeText}, {profile} on the {side} pane",
            mode == TerminalMode.Linked ? TerminalMode.Locked : TerminalMode.Linked);
    }

    /// <summary>
    /// One line for the logs and the snapshot aid's <c>terminal-state:</c> step: each tab as
    /// <c>session profile [Pane] Mode</c>, the shown one marked with <c>*</c>, joined by <c> | </c>.
    /// </summary>
    public static string Describe(IEnumerable<(ulong Session, string Profile, int Pane, TerminalMode Mode, bool Shown)> tabs) =>
        string.Join(" | ", tabs.Select(t =>
            $"{(t.Shown ? "*" : "")}{t.Session} {t.Profile} {(t.Pane == 1 ? "[Right]" : "[Left]")} {(t.Mode == TerminalMode.Linked ? "Linked" : "Locked")}"));
}

/// <summary>A tab as the order of the tabs needs it.</summary>
/// <param name="Session">The core's session ID.</param>
/// <param name="Pane">Its pane: 0 left, 1 right.</param>
/// <param name="Running">Whether its shell still runs.</param>
/// <param name="LastShown">When it was last the shown tab, as a growing count; 0 for never.</param>
public readonly record struct TabFacts(ulong Session, int Pane, bool Running, long LastShown);

/// <summary>Which tab a summoning or a tab key goes to.</summary>
public static class TerminalTabs
{
    /// <summary>
    /// The pane's most recent running session: the one shown last, else the newest. Null when the
    /// pane has none (an ended one closes a moment later and does not count).
    /// </summary>
    public static ulong? MostRecent(IEnumerable<TabFacts> tabs, int pane) =>
        tabs.Where(t => t.Pane == pane && t.Running)
            .OrderByDescending(t => t.LastShown)
            .ThenByDescending(t => t.Session)
            .Select(t => (ulong?)t.Session)
            .FirstOrDefault();

    /// <summary>
    /// The tab <paramref name="step"/> places from <paramref name="shown"/> among
    /// <paramref name="sessions"/> (in the header's order), round the ends. Null without tabs.
    /// </summary>
    public static ulong? Cycle(IReadOnlyList<ulong> sessions, ulong? shown, int step)
    {
        if (sessions.Count == 0)
        {
            return null;
        }
        var at = shown is { } current ? IndexOf(sessions, current) : -1;
        if (at < 0)
        {
            return step >= 0 ? sessions[0] : sessions[^1];
        }
        var next = ((at + step) % sessions.Count + sessions.Count) % sessions.Count;
        return sessions[next];
    }

    private static int IndexOf(IReadOnlyList<ulong> sessions, ulong session)
    {
        for (var i = 0; i < sessions.Count; i++)
        {
            if (sessions[i] == session)
            {
                return i;
            }
        }
        return -1;
    }
}
