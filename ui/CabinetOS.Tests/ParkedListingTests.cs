using CabinetOS.Core.Listing;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Tabs;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// The listing a pane keeps for the tab that went behind last (docs/ui.md,
/// "Tabs"): handed back to that tab only, one per pane, and let go (closed in
/// the core) when its time is up, when the core says its folder changed or
/// is gone, when its tab goes, or when it no longer fits its tab; after a
/// restart of the core it is let go without a close.
/// </summary>
public class ParkedListingTests
{
    private static readonly SortSpec ByName = new(PaneSort.Name, false);

    private readonly FakeChannel _core = new(_ => new OkReply());
    private long _now = 1_000;

    private ParkedListing Slot(long lifetimeMs = ParkedListing.DefaultLifetimeMs) => new(_core, () => _now, lifetimeMs);

    private static ListingView View(params string[] names)
    {
        var bytes = TestSections.Build([.. names.Select((name, i) => new SyntheticEntry((ulong)i + 1, name, 1))]);
        return ListingView.Open(new SectionHandle(TestSections.CreateSection(bytes)), (ulong)bytes.Length);
    }

    private static ParkedListing.Entry Entry(PaneTab tab, ulong listingId, string path = @"C:\data", SortSpec? order = null) =>
        new(tab, path, order ?? ByName, listingId, View("a.txt", "b.txt", "c.txt"), Focus: 2, Anchor: 1, Selected: [1, 2]);

    private List<ulong> Closed() => [.. _core.Requests.OfType<CloseListingRequest>().Select(r => r.ListingId)];

    [Fact]
    public void The_tab_that_went_behind_takes_its_listing_back_with_its_cursor_and_marks_and_nothing_is_closed()
    {
        var slot = Slot();
        var tab = new PaneTab(@"C:\data");
        var other = new PaneTab(@"C:\data");
        var entry = Entry(tab, 7);
        slot.Park(entry);

        // Another tab, even on the same folder, does not get it, and it stays kept.
        Assert.Null(slot.TakeBack(other, ByName));
        Assert.Same(entry, slot.Held);

        _now += 1_500;
        var back = slot.TakeBack(tab, ByName);

        Assert.Same(entry, back);
        Assert.Null(slot.Held);
        Assert.Null(slot.DueInMs);
        Assert.False(back!.View.IsDisposed);
        Assert.Equal((2, 1), (back.Focus, back.Anchor));
        Assert.Equal([1, 2], back.Selected);
        Assert.Empty(Closed());
        back.View.Dispose();
    }

    [Fact]
    public void A_pane_keeps_one_listing_so_the_next_tab_behind_lets_the_first_go()
    {
        var slot = Slot();
        var first = Entry(new PaneTab(@"C:\one"), 7, @"C:\one");
        var second = Entry(new PaneTab(@"C:\two"), 9, @"C:\two");

        slot.Park(first);
        slot.Park(second);

        Assert.Same(second, slot.Held);
        Assert.True(first.View.IsDisposed);
        Assert.Equal([7UL], Closed());
        slot.Release("test over");
    }

    [Fact]
    public void The_listing_goes_when_its_time_is_up_and_not_before()
    {
        var slot = Slot();
        var entry = Entry(new PaneTab(@"C:\data"), 7);
        slot.Park(entry);
        Assert.Equal(30_000, slot.DueInMs);

        _now += 29_999;
        Assert.False(slot.ReleaseIfExpired());
        Assert.Equal(1, slot.DueInMs);
        Assert.Empty(Closed());

        _now += 1;
        Assert.True(slot.ReleaseIfExpired());
        Assert.Null(slot.Held);
        Assert.True(entry.View.IsDisposed);
        Assert.Equal([7UL], Closed());
        Assert.False(slot.ReleaseIfExpired());
    }

    [Fact]
    public void The_lifetime_comes_from_the_test_hook_when_it_is_a_positive_number()
    {
        Assert.Equal(4_000, ParkedListing.LifetimeFrom("4000"));
        Assert.Equal(ParkedListing.DefaultLifetimeMs, ParkedListing.LifetimeFrom(null));
        Assert.Equal(ParkedListing.DefaultLifetimeMs, ParkedListing.LifetimeFrom("0"));
        Assert.Equal(ParkedListing.DefaultLifetimeMs, ParkedListing.LifetimeFrom("-5"));
        Assert.Equal(ParkedListing.DefaultLifetimeMs, ParkedListing.LifetimeFrom("soon"));

        var slot = Slot(lifetimeMs: 4_000);
        slot.Park(Entry(new PaneTab(@"C:\data"), 7));
        _now += 4_000;
        Assert.True(slot.ReleaseIfExpired());
    }

    [Theory]
    [InlineData("its folder changed")]
    [InlineData("its folder is gone")]
    public void An_event_for_the_kept_listing_lets_it_go_and_one_for_another_does_not(string why)
    {
        var slot = Slot();
        var entry = Entry(new PaneTab(@"C:\data"), 7);
        slot.Park(entry);

        Assert.False(slot.Release(8, why));
        Assert.False(slot.Release(0, why));
        Assert.Same(entry, slot.Held);

        Assert.True(slot.Release(7, why));
        Assert.Null(slot.Held);
        Assert.True(entry.View.IsDisposed);
        Assert.Equal([7UL], Closed());
    }

    [Fact]
    public void A_tab_whose_folder_or_order_changed_lists_again_and_its_old_listing_goes()
    {
        var slot = Slot();
        var tab = new PaneTab(@"C:\data");
        slot.Park(Entry(tab, 7));
        // panes.sort changed while the tab was behind: the listing is in the old order.
        Assert.Null(slot.TakeBack(tab, new SortSpec(PaneSort.Size, true)));
        Assert.Null(slot.Held);

        slot.Park(Entry(tab, 9));
        tab.Path = @"C:\elsewhere";
        Assert.Null(slot.TakeBack(tab, ByName));
        Assert.Equal([7UL, 9UL], Closed());

        // Case and a closing backslash do not make another folder.
        var same = new PaneTab(@"c:\DATA\");
        var entry = Entry(same, 11);
        slot.Park(entry);
        Assert.Same(entry, slot.TakeBack(same, ByName));
        entry.View.Dispose();
    }

    [Fact]
    public void A_closed_or_moved_tab_lets_its_listing_go()
    {
        var slot = Slot();
        var kept = new PaneTab(@"C:\data");
        var front = new PaneTab(@"C:\other");
        slot.Park(Entry(kept, 7));

        slot.ReleaseUnlessAmong([front, kept]);
        Assert.NotNull(slot.Held);

        slot.ReleaseUnlessAmong([front]);
        Assert.Null(slot.Held);
        Assert.Equal([7UL], Closed());
    }

    [Fact]
    public void After_a_restart_of_the_core_the_listing_is_forgotten_without_a_close()
    {
        var slot = Slot();
        var entry = Entry(new PaneTab(@"C:\data"), 7);
        slot.Park(entry);

        slot.Forget("the core started again");

        Assert.Null(slot.Held);
        Assert.True(entry.View.IsDisposed);
        Assert.Empty(Closed());
        // Nothing is left to expire or release.
        Assert.Null(slot.DueInMs);
        Assert.False(slot.Release(7, "its folder changed"));
    }
}
