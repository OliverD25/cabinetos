using CabinetOS.Core.Listing;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Tests;

/// <summary>
/// Measured folder sizes in the Size column (Space, Shift+Alt+Enter; docs/ui.md,
/// "Total Commander's keys"): the running total while the core counts, then the total.
/// </summary>
public class FolderSizesTests
{
    [Fact]
    public void A_measure_shows_its_running_total_then_the_total()
    {
        var sizes = new FolderSizes();
        Assert.Equal([@"C:\a\Pictures", @"C:\a\src"], sizes.Start(4, [@"C:\a\Pictures", @"C:\a\src"]));
        Assert.Equal(new FolderSize(0, 0, 0, 0, Done: false), sizes.Get("Pictures"));
        Assert.True(sizes.IsMeasuring);

        Assert.True(sizes.Apply(new MeasureProgressEvent(4, @"C:\a\Pictures", 10, 2, 5000)));
        Assert.Equal(new FolderSize(5000, 10, 2, 0, Done: false), sizes.Get("Pictures".AsSpan()));

        Assert.True(sizes.Apply(new MeasureFinishedEvent(4,
            [new MeasureResult(@"C:\a\Pictures", 12, 2, 6000, 1), new MeasureResult(@"C:\a\src", 3, 0, 30, 0)], Cancelled: false)));
        Assert.Equal(new FolderSize(6000, 12, 2, 1, Done: true), sizes.Get("Pictures"));
        Assert.Equal(new FolderSize(30, 3, 0, 0, Done: true), sizes.Get("src"));
        Assert.False(sizes.IsMeasuring);
    }

    [Fact]
    public void Another_pane_s_measure_changes_nothing_here()
    {
        var sizes = new FolderSizes();
        sizes.Start(4, [@"C:\a\src"]);
        Assert.False(sizes.Apply(new MeasureProgressEvent(9, @"C:\a\src", 1, 1, 1)));
        Assert.False(sizes.Apply(new MeasureFinishedEvent(9, [new MeasureResult(@"C:\a\src", 1, 1, 1, 0)], Cancelled: false)));
        Assert.Equal(new FolderSize(0, 0, 0, 0, Done: false), sizes.Get("src"));
    }

    [Fact]
    public void A_cancelled_measure_keeps_what_was_counted_to_the_end_and_drops_the_rest()
    {
        var sizes = new FolderSizes();
        sizes.Start(5, [@"C:\a\one", @"C:\a\two"]);
        sizes.Apply(new MeasureFinishedEvent(5, [new MeasureResult(@"C:\a\one", 1, 0, 10, 0)], Cancelled: true));
        Assert.Equal(new FolderSize(10, 1, 0, 0, Done: true), sizes.Get("one"));
        Assert.Null(sizes.Get("two"));
    }

    [Fact]
    public void A_folder_counted_already_or_being_counted_is_not_asked_again_until_it_is_done()
    {
        var sizes = new FolderSizes();
        sizes.Start(6, [@"C:\a\one"]);
        Assert.Empty(sizes.Start(7, [@"C:\a\one"]));
        sizes.Apply(new MeasureFinishedEvent(6, [new MeasureResult(@"C:\a\one", 1, 0, 10, 0)], Cancelled: false));
        // Counted: the user may count again (Space twice, a folder that changed).
        Assert.Equal([@"C:\a\one"], sizes.Start(8, [@"C:\a\one"]));
    }

    [Fact]
    public void The_running_measures_are_listed_by_ID_until_they_end()
    {
        var sizes = new FolderSizes();
        Assert.Empty(sizes.RunningIds);
        sizes.Start(6, [@"C:\a\x"]);
        sizes.Start(7, [@"C:\a\y", @"C:\a\z"]);
        Assert.Equal([6UL, 7UL], sizes.RunningIds.Order());

        // A measure that took nothing (its folders were being counted) is not running.
        sizes.Start(8, [@"C:\a\x"]);
        Assert.Equal([6UL, 7UL], sizes.RunningIds.Order());

        sizes.Apply(new MeasureFinishedEvent(6, [new MeasureResult(@"C:\a\x", 1, 0, 1, 0)], Cancelled: false));
        Assert.Equal([7UL], sizes.RunningIds);
        // Asking for the IDs changes nothing: a cancel keeps the sizes shown until the core's measure_finished.
        Assert.Equal(new FolderSize(1, 1, 0, 0, Done: true), sizes.Get("x"));
        Assert.Equal(new FolderSize(0, 0, 0, 0, Done: false), sizes.Get("y"));
    }

    [Fact]
    public void Only_folders_without_a_size_are_asked_for_when_a_listing_opens()
    {
        var sizes = new FolderSizes();
        string[] listing = [@"C:\a\one", @"C:\a\two", @"C:\a\three"];
        Assert.Equal(listing, sizes.NotMeasured(listing));

        // One is counted, one is being counted: neither is asked again, and the order of the rest stays.
        sizes.Start(1, [@"C:\a\one", @"C:\a\two"]);
        sizes.Apply(new MeasureFinishedEvent(1, [new MeasureResult(@"C:\a\one", 1, 0, 10, 0)], Cancelled: true));
        sizes.Start(2, [@"C:\a\two"]);
        Assert.Equal([@"C:\a\three"], sizes.NotMeasured(listing));
        Assert.Empty(sizes.NotMeasured([@"C:\a\one", @"C:\a\two"]));
        Assert.Empty(sizes.NotMeasured([]));

        // Once the pane leaves the folder, nothing is known.
        sizes.Clear();
        Assert.Equal(listing, sizes.NotMeasured(listing));
    }

    [Fact]
    public void Leaving_the_folder_forgets_the_sizes_and_names_the_measures_to_cancel()
    {
        var sizes = new FolderSizes();
        sizes.Start(6, [@"C:\a\x"]);
        sizes.Start(7, [@"C:\a\y"]);
        sizes.Apply(new MeasureFinishedEvent(6, [new MeasureResult(@"C:\a\x", 1, 0, 1, 0)], Cancelled: false));

        Assert.Equal([7UL], sizes.Clear());
        Assert.Null(sizes.Get("x"));
        Assert.Null(sizes.Get("y"));
        Assert.False(sizes.Apply(new MeasureProgressEvent(7, @"C:\a\y", 1, 0, 1)));
    }
}
