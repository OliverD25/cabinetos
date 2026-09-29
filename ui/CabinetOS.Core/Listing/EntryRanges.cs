namespace CabinetOS.Core.Listing;

/// <summary>
/// The rows <c>match_entries</c> answers, as <c>[start, count]</c> pairs of
/// section indexes: short even when every row of 100,000 matches.
/// </summary>
public static class EntryRanges
{
    /// <summary>The rows of <paramref name="ranges"/> that a listing of <paramref name="count"/> rows has, in order.</summary>
    public static IEnumerable<int> Rows(IReadOnlyList<IReadOnlyList<ulong>> ranges, int count)
    {
        foreach (var range in ranges)
        {
            if (range is not [var start, var length] || start >= (ulong)count)
            {
                continue;
            }
            var end = Math.Min((ulong)count, start + length);
            for (var row = start; row < end; row++)
            {
                yield return (int)row;
            }
        }
    }

    /// <summary>How many rows <paramref name="ranges"/> names.</summary>
    public static ulong Count(IReadOnlyList<IReadOnlyList<ulong>> ranges) =>
        ranges.Aggregate(0UL, (sum, range) => range is [_, var length] ? sum + length : sum);
}
