namespace CabinetOS.Core.Prompts;

/// <summary>
/// The patterns of the pattern box (Num +, Num -), newest first: the box
/// offers the last one, and lists ten. Kept in memory for the session, like
/// Total Commander's list.
/// </summary>
public sealed class PatternHistory
{
    /// <summary>How many patterns the box lists.</summary>
    public const int Size = 10;

    private readonly List<string> _items = [];

    /// <summary>The patterns, newest first.</summary>
    public IReadOnlyList<string> Items => _items;

    /// <summary>What the box offers: the last pattern, <c>*.*</c> the first time.</summary>
    public string Last => _items.Count > 0 ? _items[0] : "*.*";

    /// <summary>A pattern the user ran: it moves to the front, once.</summary>
    public void Add(string pattern)
    {
        var trimmed = pattern.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }
        _items.Remove(trimmed);
        _items.Insert(0, trimmed);
        if (_items.Count > Size)
        {
            _items.RemoveRange(Size, _items.Count - Size);
        }
    }

    /// <summary>
    /// The pattern of every file with <paramref name="name"/>'s extension
    /// (Alt+Num +, Alt+Num -): <c>*.txt</c>. The extension is the core's, from
    /// the last dot on, so <c>.gitignore</c> has one; a name without one gives
    /// Total Commander's <c>*.</c>, the names without a dot.
    /// </summary>
    public static string SameExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 || dot == name.Length - 1 ? "*." : "*" + name[dot..].ToLowerInvariant();
    }
}
