namespace CabinetOS.Core.Keys;

/// <summary>
/// A command's bindings as a palette row shows them (design view B): the first
/// as keycaps, "+N" for the others, and every binding in the row's tooltip.
/// </summary>
public sealed record BindingSummary(KeySequence? First, int Others, string All)
{
    /// <summary>"+2" when two more bindings follow the first; null when none do.</summary>
    public string? MoreText => Others > 0 ? $"+{Others}" : null;

    /// <summary>The bindings in the keymap's order; one that does not parse is left out.</summary>
    public static BindingSummary Of(IReadOnlyList<string> keys)
    {
        var parsed = keys.Select(k => KeySequence.TryParse(k, out var sequence) ? sequence : null).OfType<KeySequence>().ToList();
        return new BindingSummary(
            parsed.FirstOrDefault(),
            Math.Max(0, parsed.Count - 1),
            string.Join(", ", parsed.Select(s => string.Join(" then ", s.DisplayParts()))));
    }
}
