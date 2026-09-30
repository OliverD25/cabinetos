namespace CabinetOS.Core.Shell;

/// <summary>What the palette's frame lists: files and folders (Quick Open) or commands.</summary>
public enum PaletteMode
{
    /// <summary>Quick Open (Ctrl+P): the workspace's files and folders.</summary>
    Files,

    /// <summary>The command palette (Ctrl+Shift+P): the commands.</summary>
    Commands,
}

/// <summary>
/// How the palette's text picks its mode (docs/ui.md, "Quick Open"; the
/// creator's SHELL_REDESIGN.md §3). Quick Open lists files; a <c>&gt;</c>
/// typed at the start of its text switches to the commands, and the rest of
/// the text is the command query. The command palette shows its <c>&gt;</c>
/// as a glyph in front of the box, so its text is the query as it is;
/// Backspace in its empty box goes back to the files.
/// </summary>
public static class PaletteInput
{
    /// <summary>The character that switches Quick Open to the commands.</summary>
    public const char CommandPrefix = '>';

    /// <summary>
    /// The mode and the query for <paramref name="text"/> typed in
    /// <paramref name="mode"/>. <c>Switched</c> says the text carried the
    /// prefix: the box then holds <c>Query</c> alone.
    /// </summary>
    public static (PaletteMode Mode, string Query, bool Switched) Read(PaletteMode mode, string text)
    {
        if (mode == PaletteMode.Files && text.StartsWith(CommandPrefix))
        {
            return (PaletteMode.Commands, text[1..].TrimStart(), true);
        }
        return (mode, text, false);
    }

    /// <summary>The mode after Backspace in a box holding <paramref name="text"/>: the commands' empty box goes back to the files.</summary>
    public static PaletteMode AfterBackspace(PaletteMode mode, string text) =>
        mode == PaletteMode.Commands && text.Length == 0 ? PaletteMode.Files : mode;

    /// <summary>The query sent to the core's <c>search</c> for Quick Open: the text without spaces at its ends; null when nothing is left to search.</summary>
    public static string? FileQuery(string text) => text.Trim() is { Length: > 0 } query ? query : null;
}
