namespace CabinetOS.Core.Terminal;

/// <summary>What Ctrl+` (<c>view.toggleTerminal</c>) does.</summary>
public enum SummonAction
{
    /// <summary>The terminal has the keyboard: it goes back to the active pane.</summary>
    HandBackToPane,

    /// <summary>The second Ctrl+` from a pane whose session is shown: the dock hides.</summary>
    Hide,

    /// <summary>The pane's session is the shown tab but has no keyboard: it gets it.</summary>
    FocusShown,

    /// <summary>The dock shows the pane's most recent session, which gets the keyboard.</summary>
    ShowSession,

    /// <summary>The pane has no session: a new one of the default profile starts in the pane's folder.</summary>
    OpenNew,
}

/// <summary>
/// What the window knows when Ctrl+` is pressed.
/// </summary>
/// <param name="DockVisible">Whether the Tool Dock is shown.</param>
/// <param name="TerminalHasKeyboard">Whether the terminal's page has the keyboard (the key was pressed in it).</param>
/// <param name="Pane">The active pane: where the key was pressed, or where the keyboard goes back to.</param>
/// <param name="ShownPane">The pane the shown tab belongs to; null when there is no tab.</param>
/// <param name="PaneSession">The pane's most recent running session; null when it has none.</param>
/// <param name="CameBackByToggle">
/// The keyboard came to this pane from the terminal by Ctrl+`, and the terminal has not had it since:
/// the next Ctrl+` from the pane is the "second" one, which hides the dock as before.
/// </param>
public readonly record struct SummonState(
    bool DockVisible,
    bool TerminalHasKeyboard,
    int Pane,
    int? ShownPane,
    ulong? PaneSession,
    bool CameBackByToggle);

/// <summary>The decision: what to do, and the session it is about (for <see cref="SummonAction.ShowSession"/>).</summary>
public readonly record struct Summon(SummonAction Action, ulong? Session = null);

/// <summary>
/// Active Summoning (docs/ui.md, "The terminal"): Ctrl+` reaches the terminal of the pane it is
/// pressed in, and a pane never takes the terminal anywhere by itself (the Zero-Hijack rule).
/// <list type="bullet">
/// <item>In the terminal: the keyboard goes back to the pane.</item>
/// <item>In a pane with the dock hidden: the dock shows the pane's most recent session, or a new one.</item>
/// <item>In a pane whose session is the shown tab: it gets the keyboard; right after Ctrl+` brought the
/// keyboard back from it, the dock hides instead.</item>
/// <item>In a pane while the other pane's session is shown: this pane's session is shown, or a new one;
/// switching panes and pressing Ctrl+` never hides.</item>
/// </list>
/// </summary>
public static class TerminalSummoning
{
    /// <summary>Decides for <paramref name="state"/>.</summary>
    public static Summon Decide(SummonState state)
    {
        if (state.DockVisible && state.TerminalHasKeyboard)
        {
            return new Summon(SummonAction.HandBackToPane);
        }
        if (state.DockVisible && state.ShownPane == state.Pane)
        {
            return new Summon(state.CameBackByToggle ? SummonAction.Hide : SummonAction.FocusShown);
        }
        return state.PaneSession is { } session
            ? new Summon(SummonAction.ShowSession, session)
            : new Summon(SummonAction.OpenNew);
    }

    /// <summary>
    /// The same, for a way in that never hides the dock or hands the keyboard back (the dock's
    /// "show" commands, Ctrl+Alt+P): the pane's session is shown and gets the keyboard.
    /// </summary>
    public static Summon DecideShow(SummonState state) =>
        Decide(state with { TerminalHasKeyboard = false, CameBackByToggle = false });
}
