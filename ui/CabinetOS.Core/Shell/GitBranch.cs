namespace CabinetOS.Core.Shell;

/// <summary>
/// The branch the workspace pill shows (docs/ui.md, "The top row"): the name
/// in the folder's <c>.git\HEAD</c>. Only that one small file is read, off
/// the UI thread, and no git program runs. A HEAD that names no branch (a
/// detached HEAD's commit, a worktree's <c>.git</c> file) shows nothing.
/// </summary>
public static class GitBranch
{
    /// <summary>The most bytes read from HEAD; a branch line is far shorter.</summary>
    public const int MaxBytes = 1024;

    private const string Prefix = "ref: refs/heads/";

    /// <summary>The branch that <paramref name="head"/>, a HEAD file's text, names; null when it names none.</summary>
    public static string? FromHead(string? head)
    {
        if (head is null)
        {
            return null;
        }
        var line = head.Split('\n', 2)[0].Trim();
        if (!line.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }
        var name = line[Prefix.Length..].Trim();
        return name.Length > 0 ? name : null;
    }

    /// <summary>
    /// The branch of the repository whose root is <paramref name="folder"/>:
    /// null when the folder has no <c>.git\HEAD</c> file or it cannot be read.
    /// Blocks on the disk, so the window calls it on a background thread.
    /// </summary>
    public static string? Read(string folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }
        try
        {
            var head = System.IO.Path.Combine(folder, ".git", "HEAD");
            if (!File.Exists(head))
            {
                return null;
            }
            using var stream = new FileStream(head, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[MaxBytes];
            var read = stream.ReadAtLeast(buffer, MaxBytes, throwOnEndOfStream: false);
            return FromHead(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
