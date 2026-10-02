using CabinetOS.Core.Listing;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Tests;

/// <summary>
/// A click on a column heading sorts the pane by that column, and a double-click on it fits the
/// column (docs/ui.md, "A pane's order" and "Column widths").
/// </summary>
public class HeaderClicksTests
{
    private const long DoubleClick = 500;

    private long _now = 10_000;

    private HeaderClicks Clicks() => new(() => _now, () => DoubleClick);

    [Theory]
    [InlineData(ListColumn.Name, "name")]
    [InlineData(ListColumn.Modified, "modified")]
    [InlineData(ListColumn.Type, "extension")]
    [InlineData(ListColumn.Size, "size")]
    public void Each_heading_presses_the_key_of_its_column(ListColumn column, string key) =>
        Assert.Equal(key, PaneSort.ForColumn(column));

    [Fact]
    public void A_click_on_a_heading_starts_the_column_in_its_own_direction_and_a_second_click_reverses_it()
    {
        // Size starts with the largest first, as Ctrl+F6 does; the same heading again reverses it.
        var bySize = PaneSort.Next(new SortSpec(PaneSort.Name, false), PaneSort.ForColumn(ListColumn.Size));
        Assert.Equal(new SortSpec(PaneSort.Size, true), bySize);
        var reversed = PaneSort.Next(bySize, PaneSort.ForColumn(ListColumn.Size));
        Assert.Equal(new SortSpec(PaneSort.Size, false), reversed);
        // Type is the extension, which starts from A to Z; another heading starts that column.
        var byType = PaneSort.Next(reversed, PaneSort.ForColumn(ListColumn.Type));
        Assert.Equal(new SortSpec(PaneSort.Extension, false), byType);
        Assert.Equal(new SortSpec(PaneSort.Modified, true), PaneSort.Next(byType, PaneSort.ForColumn(ListColumn.Modified)));
    }

    [Fact]
    public void The_first_click_sorts_at_once_without_waiting_for_a_double_click()
    {
        var clicks = Clicks();
        Assert.Equal(HeaderTap.Sort, clicks.Tap(ListColumn.Size));
    }

    [Fact]
    public void Two_clicks_of_a_double_click_sort_once_and_the_double_click_asks_to_undo_that_sort()
    {
        var clicks = Clicks();
        Assert.Equal(HeaderTap.Sort, clicks.Tap(ListColumn.Type));
        _now += 120;
        Assert.Equal(HeaderTap.Ignore, clicks.Tap(ListColumn.Type));
        Assert.True(clicks.DoubleTap(ListColumn.Type));
        // Asked once per double-click.
        Assert.False(clicks.DoubleTap(ListColumn.Type));
    }

    [Fact]
    public void A_double_click_whose_first_click_was_not_seen_asks_for_no_undo()
    {
        var clicks = Clicks();
        Assert.False(clicks.DoubleTap(ListColumn.Modified));
    }

    [Fact]
    public void A_double_click_on_another_heading_than_the_last_sort_undoes_nothing()
    {
        var clicks = Clicks();
        Assert.Equal(HeaderTap.Sort, clicks.Tap(ListColumn.Size));
        _now += 100;
        Assert.False(clicks.DoubleTap(ListColumn.Name));
    }

    [Fact]
    public void A_click_after_the_double_click_time_sorts_again_and_reverses_the_order()
    {
        var clicks = Clicks();
        Assert.Equal(HeaderTap.Sort, clicks.Tap(ListColumn.Size));
        _now += DoubleClick + 1;
        Assert.Equal(HeaderTap.Sort, clicks.Tap(ListColumn.Size));
    }

    [Fact]
    public void A_click_on_another_heading_within_the_double_click_time_is_a_sort_of_its_own()
    {
        var clicks = Clicks();
        Assert.Equal(HeaderTap.Sort, clicks.Tap(ListColumn.Size));
        _now += 100;
        Assert.Equal(HeaderTap.Sort, clicks.Tap(ListColumn.Modified));
    }

    [Fact]
    public void A_late_double_click_event_asks_for_no_undo()
    {
        var clicks = Clicks();
        Assert.Equal(HeaderTap.Sort, clicks.Tap(ListColumn.Name));
        _now += DoubleClick + 1;
        Assert.False(clicks.DoubleTap(ListColumn.Name));
    }
}
