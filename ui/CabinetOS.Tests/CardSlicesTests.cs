using System.Text.Json;
using CabinetOS.Core.Market;
using CabinetOS.Core.Plugins;
using CabinetOS.Core.Protocol;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// The marketplace's cards in parts (docs/ui.md, "The marketplace"): the
/// cards that fill the view at once, the rest a slice per turn, a new set of
/// items cancelling the slices of the one before. The view makes a card for
/// each item <see cref="CardSlices"/> hands out, in that order.
/// </summary>
public class CardSlicesTests
{
    // 120 items: plugins and tools by turns, as an index of a grown collection may list them (the themes have their own catalogue).
    private static readonly MarketItem[] Index = [.. Enumerable.Range(0, 120).Select(i => Item(i))];

    private static MarketItem Item(int i)
    {
        var kind = i % 2 == 0 ? ExtensionKinds.Plugin : ExtensionKinds.Tool;
        using var manifest = JsonDocument.Parse("{}");
        return new MarketItem($"item-{i:000}", kind, $"Item {i}", new MarketAuthor("CabinetOS", false, null), "1.0.0", "For the tests.", 1000,
            new MarketDownload($"files/item-{i:000}-1.0.0.zip", new string('a', 64)), manifest.RootElement.Clone(), "0.1.0", "MIT");
    }

    private static async Task<MarketplaceModel> ModelAsync()
    {
        var core = new FakeChannel(request => request switch
        {
            MarketplaceRefreshRequest => new MarketplaceIndexReply(Index, @"C:\market\index.json", 1790000000000),
            ListPluginsRequest => new PluginsReply([]),
            ListThemesRequest => new ThemesReply([]),
            ListToolsRequest => new ToolsReply([]),
            _ => new OkReply(),
        });
        var market = new MarketplaceModel(core, _ => Task.CompletedTask);
        Assert.True(await market.RefreshAsync());
        return market;
    }

    // What the view does: a new set clears the grid and makes its first slice; each turn makes the next slice of its own set.
    private sealed class Grid(CardSlices slices)
    {
        public List<string> Cards { get; } = [];

        public int Start(IReadOnlyList<MarketItem> items, int perScreen)
        {
            Cards.Clear();
            Cards.AddRange(slices.Start(items, perScreen, perScreen).Select(item => item.Id));
            return slices.Generation;
        }

        // One dispatcher turn: false when the set's slices are over (all made, or a newer set took its place).
        public bool Turn(int generation)
        {
            var slice = slices.Next(generation);
            Cards.AddRange(slice.Select(item => item.Id));
            return slice.Count > 0;
        }
    }

    [Fact]
    public async Task A_model_with_120_items_shows_the_first_slice_at_once_and_all_after_the_slices_in_index_order()
    {
        var market = await ModelAsync();
        var items = market.Items;
        Assert.Equal(120, items.Count);
        var slices = new CardSlices();
        var grid = new Grid(slices);

        var generation = grid.Start(items, perScreen: 12);

        // At once: the cards that fill the view, the first twelve of the index.
        Assert.Equal(items.Take(12).Select(item => item.Id), grid.Cards);
        Assert.False(slices.IsComplete);
        var turns = 0;
        while (grid.Turn(generation))
        {
            turns++;
            Assert.Equal(12 + (turns * 12), grid.Cards.Count);
        }
        // Nine more slices of a screenful each, and the grid holds every item in the index's order, the order the keyboard follows.
        Assert.Equal(9, turns);
        Assert.Equal(10, slices.Slices);
        Assert.True(slices.IsComplete);
        Assert.Equal(items.Select(item => item.Id), grid.Cards);
        Assert.True(slices.Holds(market.Items));
    }

    [Fact]
    public async Task A_filter_change_during_the_slices_leaves_only_the_filtered_cards()
    {
        var market = await ModelAsync();
        var slices = new CardSlices();
        var grid = new Grid(slices);
        var all = grid.Start(market.Items, perScreen: 12);
        Assert.True(grid.Turn(all));
        Assert.Equal(24, grid.Cards.Count);

        // The Tools tab while slices of Discover are still to come: a new set, as the view starts one when the items differ.
        market.SetTab(MarketTabs.Tools);
        Assert.False(slices.Holds(market.Items));
        var themes = grid.Start(market.Items, perScreen: 12);

        // Discover's next turn makes nothing: its slices stopped.
        Assert.False(grid.Turn(all));
        while (grid.Turn(themes))
        {
        }
        Assert.Equal(60, grid.Cards.Count);
        Assert.All(grid.Cards, id => Assert.Equal(1, int.Parse(id[5..], System.Globalization.CultureInfo.InvariantCulture) % 2));
        Assert.Equal(Index.Where(item => item.Kind == ExtensionKinds.Tool).Select(item => item.Id), grid.Cards);
        Assert.False(grid.Turn(all));
    }

    [Fact]
    public void The_same_items_are_the_same_set_and_other_objects_are_not()
    {
        var slices = new CardSlices();
        slices.Start(Index, 12, 12);

        Assert.True(slices.Holds([.. Index]));
        // An install's new item for the same ID is another object: its card is made again.
        Assert.False(slices.Holds([.. Index.Take(119), Index[119] with { InstalledVersion = "1.0.0" }]));
        Assert.False(slices.Holds(Index[..119]));
        // The index read again: new objects with the same IDs in the same order, so the grid keeps its place.
        Assert.True(slices.HasSameIds([.. Index.Select(item => item with { })]));
        Assert.False(slices.HasSameIds([.. Index.Reverse()]));
        Assert.False(slices.HasSameIds(Index[1..]));
    }

    [Fact]
    public void A_set_smaller_than_a_screen_is_complete_at_once()
    {
        var slices = new CardSlices();

        Assert.Equal(5, slices.Start(Index[..5], 12, 12).Count);

        Assert.True(slices.IsComplete);
        Assert.Empty(slices.Next(slices.Generation));
        Assert.Equal(1, slices.Slices);
        Assert.Empty(slices.Start([], 12, 12));
        Assert.True(slices.IsComplete);
    }

    [Theory]
    // 2 columns of 230 px in 700 px; 600 px over cards of 141 px and 10 px gaps is 4 rows and a cut one.
    [InlineData(700, 600, 141, 10)]
    // 4 columns in 1000 px (240 px a column with its gap, the last needs none).
    [InlineData(1000, 600, 141, 20)]
    // One card wider than the view still makes a column; a view not laid out yet counts as one row.
    [InlineData(100, 0, 141, 1)]
    [InlineData(1000, double.NaN, 141, 4)]
    [InlineData(0, 600, 141, 5)]
    public void A_screen_holds_its_columns_times_its_rows(double width, double height, double cardHeight, int expected) =>
        Assert.Equal(expected, CardSlices.PerScreen(width, height, 230, 10, cardHeight));
}
