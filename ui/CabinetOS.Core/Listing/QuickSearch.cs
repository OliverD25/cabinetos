namespace CabinetOS.Core.Listing;

/// <summary>
/// Quick search in a pane (Q1): letters typed where no key is bound jump to
/// the first name that starts with them, as in Explorer and Total Commander.
/// After a second without a key a new search starts; Esc clears it. The core
/// finds the name (<c>match_entries</c> with <c>first_from</c>); this keeps
/// the typed text and says where to look from.
/// </summary>
public sealed class QuickSearch(Func<long> nowMilliseconds)
{
    /// <summary>How long a pause starts a new search.</summary>
    public const int QuietMs = 1000;

    private long _last;

    /// <summary>What was typed in this search.</summary>
    public string Text { get; private set; } = "";

    /// <summary>Whether a search is under way: something typed less than a second ago.</summary>
    public bool IsActive => Text.Length > 0 && nowMilliseconds() - _last < QuietMs;

    /// <summary>A typed character; returns the search's text, a new one after a pause.</summary>
    public string Type(char character)
    {
        var now = nowMilliseconds();
        if (Text.Length > 0 && now - _last >= QuietMs)
        {
            Text = "";
        }
        Text += character;
        _last = now;
        return Text;
    }

    /// <summary>Ends the search (Esc, another folder).</summary>
    public void Clear() => Text = "";

    /// <summary>
    /// The row to look from, the core going round to the start: after the
    /// cursor for a search's first letter, so the same letter again goes on
    /// to the next name; at the cursor for a longer text, which may still match it.
    /// </summary>
    public uint FirstFrom(int cursor) => (uint)Math.Max(0, Text.Length <= 1 ? cursor + 1 : cursor);

    /// <summary>
    /// The names starting with <paramref name="text"/>, as a pattern of
    /// <c>match_entries</c>; null for text with <c>;</c> or <c>|</c>, which
    /// the pattern syntax reads as separators.
    /// </summary>
    public static string? PatternFor(string text) =>
        text.Length == 0 || text.AsSpan().IndexOfAny(';', '|') >= 0 ? null : text + "*";
}
