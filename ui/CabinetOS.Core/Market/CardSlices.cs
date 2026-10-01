using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Market;

/// <summary>
/// The marketplace's cards, made in parts (docs/ui.md, "The marketplace";
/// the speed review's proposal C): the cards that fill the view at once,
/// the rest a slice at a time, each in a turn of its own, so no frame makes
/// them all. Every new set of cards (an index read, a tab, a search) starts
/// a new generation, and the slices still to come of the one before give
/// nothing. The cards are made in the items' order, which is the order the
/// keyboard reaches them in.
/// </summary>
public sealed class CardSlices
{
    private int _perSlice = 1;

    /// <summary>One more for each set of cards; a slice of an older one is not made.</summary>
    public int Generation { get; private set; }

    /// <summary>The items the cards are for, in order: the made ones and those still to come.</summary>
    public IReadOnlyList<MarketItem> Items { get; private set; } = [];

    /// <summary>How many of <see cref="Items"/> have their card: always the first ones.</summary>
    public int Made { get; private set; }

    /// <summary>The slices made of this set, the first one included.</summary>
    public int Slices { get; private set; }

    /// <summary>Whether every item has its card.</summary>
    public bool IsComplete => Made >= Items.Count;

    /// <summary>
    /// A new set of cards for <paramref name="items"/>: the slices of the set
    /// before stop, and the first <paramref name="first"/> items are returned,
    /// to be made at once. The rest come <paramref name="perSlice"/> at a time
    /// from <see cref="Next"/>.
    /// </summary>
    public IReadOnlyList<MarketItem> Start(IReadOnlyList<MarketItem> items, int first, int perSlice)
    {
        Generation++;
        Items = items;
        _perSlice = Math.Max(1, perSlice);
        Made = 0;
        Slices = 0;
        return Take(Math.Max(1, first));
    }

    /// <summary>
    /// The next slice of the set of <paramref name="generation"/>: empty once
    /// every card is made, or when a newer set took its place.
    /// </summary>
    public IReadOnlyList<MarketItem> Next(int generation) => generation == Generation ? Take(_perSlice) : [];

    /// <summary>Whether <paramref name="items"/> are this set's: the same items, the same objects, in the same order.</summary>
    public bool Holds(IReadOnlyList<MarketItem> items)
    {
        if (items.Count != Items.Count)
        {
            return false;
        }
        for (var i = 0; i < items.Count; i++)
        {
            if (!ReferenceEquals(items[i], Items[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Whether <paramref name="items"/> name this set's items in the same
    /// order, as the index read again gives them (new objects, the same IDs).
    /// </summary>
    public bool HasSameIds(IReadOnlyList<MarketItem> items)
    {
        if (items.Count != Items.Count)
        {
            return false;
        }
        for (var i = 0; i < items.Count; i++)
        {
            if (!string.Equals(items[i].Id, Items[i].Id, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// How many cards fill a view of <paramref name="width"/> by
    /// <paramref name="height"/> pixels: the grid's columns (as many
    /// <paramref name="minItemWidth"/> columns as fit, <paramref name="spacing"/>
    /// apart) times the rows of <paramref name="cardHeight"/> it shows, the
    /// one cut at the bottom included. A view not laid out yet counts as one row.
    /// </summary>
    public static int PerScreen(double width, double height, double minItemWidth, double spacing, double cardHeight)
    {
        var rows = height > 0 && cardHeight > 0 && double.IsFinite(height)
            ? (int)Math.Ceiling((height + spacing) / (cardHeight + spacing))
            : 1;
        return Columns(width, minItemWidth, spacing) * Math.Max(1, rows);
    }

    /// <summary>The grid's columns in <paramref name="width"/>: as many as fit at the minimum width, at least one.</summary>
    public static int Columns(double width, double minItemWidth, double spacing) =>
        width > 0 && double.IsFinite(width) ? Math.Max(1, (int)Math.Floor((width + spacing) / (minItemWidth + spacing))) : 1;

    private IReadOnlyList<MarketItem> Take(int count)
    {
        var from = Made;
        var to = Math.Min(Items.Count, from + count);
        if (to <= from)
        {
            return [];
        }
        Made = to;
        Slices++;
        var slice = new MarketItem[to - from];
        for (var i = from; i < to; i++)
        {
            slice[i - from] = Items[i];
        }
        return slice;
    }
}
