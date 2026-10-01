namespace CabinetOS.Core.Terminal;

/// <summary>
/// How a terminal session is bound to its pane (docs/terminal.md, "Panes and modes"). A locked
/// session stays where the user takes it; a linked one is meant to follow its pane through a
/// prompt hook, which is not built yet, so for now it behaves as a locked one.
/// </summary>
public enum TerminalMode
{
    /// <summary>Nothing the pane does reaches the session.</summary>
    Locked,

    /// <summary>The session follows its pane (once the prompt hook exists).</summary>
    Linked,
}

/// <summary>The wire names of the panes and modes, and the window's pane numbers (0 left, 1 right).</summary>
public static class TerminalBinding
{
    /// <summary><c>left</c> for pane 0, <c>right</c> for pane 1.</summary>
    public static string PaneName(int pane) => pane == 1 ? "right" : "left";

    /// <summary>1 for <c>right</c>, 0 for anything else.</summary>
    public static int PaneIndex(string? name) => name == "right" ? 1 : 0;

    /// <summary><c>locked</c> or <c>linked</c>.</summary>
    public static string ModeName(TerminalMode mode) => mode == TerminalMode.Linked ? "linked" : "locked";

    /// <summary>The mode a wire name says; null for any other text.</summary>
    public static TerminalMode? ParseMode(string? name) => name switch
    {
        "locked" => TerminalMode.Locked,
        "linked" => TerminalMode.Linked,
        _ => null,
    };
}
