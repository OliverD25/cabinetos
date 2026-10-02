using CabinetOS.Core.Presentation;

namespace CabinetOS.Core.Terminal;

/// <summary>What the window knows about the shown session when it draws the header's caption.</summary>
/// <param name="Profile">The shown session's profile; null without a shown tab.</param>
/// <param name="Running">Whether its shell still runs.</param>
/// <param name="ExitCode">The shell's exit code, once it ended.</param>
/// <param name="StartFolder">The folder the session started in.</param>
/// <param name="Folder">The shell's folder as its prompt hook last reported it (<c>terminal_folder_changed</c>); null before the first report and for a shell without a hook (cmd).</param>
public readonly record struct CaptionFacts(string? Profile, bool Running, uint? ExitCode, string? StartFolder, string? Folder);

/// <summary>The caption's text and its tooltip (the full folder, or null).</summary>
public sealed record CaptionLook(string Text, string? Tip);

/// <summary>
/// The header's caption (docs/ui.md, "The terminal"): how the shown shell ended; else the folder it is in, as
/// its prompt hook reports it ("in docs"), which follows a <c>cd</c> of the user's own and a linked shell's
/// following; before the first report, the folder it started in ("started in docs"). The tooltip names the
/// whole folder.
/// </summary>
public static class TerminalCaption
{
    /// <summary>Decides for <paramref name="facts"/>.</summary>
    public static CaptionLook Decide(CaptionFacts facts)
    {
        if (facts.Profile is null)
        {
            return new CaptionLook("", null);
        }
        if (!facts.Running)
        {
            return new CaptionLook($"{facts.Profile} exited with code {facts.ExitCode}", null);
        }
        if (facts.Folder is { Length: > 0 } folder)
        {
            return new CaptionLook($"in {DisplayFormat.FolderName(folder)}", folder);
        }
        return facts.StartFolder is { Length: > 0 } start
            ? new CaptionLook($"started in {DisplayFormat.FolderName(start)}", start)
            : new CaptionLook("", null);
    }

    /// <summary>
    /// Whether a reported <paramref name="folder"/> is <paramref name="wanted"/>: Windows folders compare without
    /// case, and a trailing backslash does not count (the snapshot aid's <c>until:terminal-folder:</c>).
    /// </summary>
    public static bool IsFolder(string? folder, string wanted) =>
        folder is not null && string.Equals(folder.TrimEnd('\\'), wanted.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
