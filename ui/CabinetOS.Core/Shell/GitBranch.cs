using CabinetOS.Core.Presentation;

namespace CabinetOS.Core.Shell;

/// <summary>A git repository as the top row sees it: the folder that holds its <c>.git</c>, and its branch (null: none shown).</summary>
public sealed record GitRepository(string Root, string? Branch);

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
    /// The repository that holds <paramref name="folder"/>: the nearest
    /// folder at or above it with a <c>.git</c> folder or file, and the branch
    /// its HEAD names. Null outside a repository. Until workspaces exist
    /// (docs/ui.md, "The top row"), this is what the workspace pill and Quick
    /// Open take for the workspace. Blocks on the disk, so the window calls it
    /// on a background thread; it reads one small file and asks whether
    /// <c>.git</c> exists once per folder on the way up.
    /// </summary>
    public static GitRepository? FindRepository(string folder)
    {
        // C:\repo\ is C:\repo; a drive's root keeps its backslash.
        var start = folder.TrimEnd('\\', '/');
        if (start.EndsWith(':'))
        {
            start += '\\';
        }
        for (var current = start; !string.IsNullOrEmpty(current); current = DisplayFormat.Parent(current))
        {
            string git;
            try
            {
                git = System.IO.Path.Combine(current, ".git");
            }
            catch (ArgumentException)
            {
                return null;
            }
            if (Directory.Exists(git) || File.Exists(git))
            {
                return new GitRepository(current, Read(current));
            }
        }
        return null;
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
