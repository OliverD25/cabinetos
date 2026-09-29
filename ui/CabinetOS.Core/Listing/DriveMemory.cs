namespace CabinetOS.Core.Listing;

/// <summary>
/// The folder a pane last showed on each drive in this session: the drive
/// list's row (Alt+F1, Alt+F2) goes there, as in Total Commander, and to the
/// drive's root the first time.
/// </summary>
public sealed class DriveMemory
{
    private readonly Dictionary<char, string> _last = [];

    /// <summary>A folder the pane shows now.</summary>
    public void Remember(string path)
    {
        if (LetterOf(path) is { } letter)
        {
            _last[letter] = path;
        }
    }

    /// <summary>Where drive <paramref name="letter"/>'s row takes the pane.</summary>
    public string FolderOn(char letter)
    {
        var upper = char.ToUpperInvariant(letter);
        return _last.TryGetValue(upper, out var path) ? path : $"{upper}:\\";
    }

    /// <summary>The drive letter of <paramref name="path"/>, upper case; null for a share or no path.</summary>
    public static char? LetterOf(string path)
    {
        var plain = path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : path;
        return plain.Length >= 2 && plain[1] == ':' && char.IsAsciiLetter(plain[0]) ? char.ToUpperInvariant(plain[0]) : null;
    }
}
