using System.Globalization;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Tabs;

/// <summary>
/// The listing of the tab that went behind last in one pane (docs/ui.md,
/// "Tabs"; the speed review's proposal E). It stays open in shared memory,
/// and the core keeps watching its folder, for a while, so a switch back to
/// that tab shows it again without listing the folder anew. One per pane, so
/// a pane holds at most its own listing and this one. Whatever lets it go
/// closes it in the core, as the pane closes its own; after a restart of the
/// core it is only let go here (<see cref="Forget"/>).
/// </summary>
public sealed class ParkedListing(ICoreChannel core, Func<long> nowMilliseconds, long lifetimeMs = ParkedListing.DefaultLifetimeMs)
{
    /// <summary>The variable that sets the lifetime in milliseconds, for the end-to-end tests.</summary>
    public const string LifetimeEnv = "CABINETOS_UI_PARKED_LISTING_MS";

    /// <summary>
    /// How long a listing is kept: long enough to look at another tab and
    /// come back, short enough that a tab left behind does not hold its
    /// section (about 10 MB for 100,000 entries) and its folder's watch for long.
    /// </summary>
    public const long DefaultLifetimeMs = 30_000;

    private const string Target = "cabinetos_ui::tabs";

    private Entry? _held;
    private long _parkedAt;

    /// <summary>
    /// A listing kept for <paramref name="Owner"/>: its folder, the order it
    /// is in, the listing and the cursor, the anchor and the marked rows as
    /// the pane had them when the tab went behind.
    /// </summary>
    public sealed record Entry(PaneTab Owner, string Path, SortSpec Order, ulong ListingId, ListingView View, int Focus, int Anchor, IReadOnlyList<int> Selected);

    /// <summary>The listing kept now, or null.</summary>
    public Entry? Held => _held;

    /// <summary>How long the listing is kept, in milliseconds.</summary>
    public long LifetimeMs => lifetimeMs;

    /// <summary>The time left until the kept listing goes, in milliseconds; null when none is kept.</summary>
    public long? DueInMs => _held is null ? null : Math.Max(0, _parkedAt + lifetimeMs - nowMilliseconds());

    /// <summary><see cref="LifetimeEnv"/>'s milliseconds, or the default when it is not a positive number.</summary>
    public static long LifetimeFrom(string? setting) =>
        long.TryParse(setting, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) && ms > 0 ? ms : DefaultLifetimeMs;

    /// <summary>Keeps <paramref name="entry"/>; the listing kept before it is let go.</summary>
    public void Park(Entry entry)
    {
        if (_held is { } before && !ReferenceEquals(before, entry))
        {
            Release("another tab went behind");
        }
        _held = entry;
        _parkedAt = nowMilliseconds();
        Diag.Info(Target, "tab listing kept", new LogField("path", entry.Path), new LogField("listing_id", entry.ListingId),
            new LogField("entries", entry.View.Count), new LogField("for_ms", lifetimeMs));
    }

    /// <summary>
    /// Hands back the listing kept for <paramref name="tab"/> when it still
    /// is the tab's folder in <paramref name="order"/>; null otherwise. A
    /// listing kept for this tab that no longer fits it is let go: the tab
    /// then lists its folder, as it did before listings were kept.
    /// </summary>
    public Entry? TakeBack(PaneTab tab, SortSpec order)
    {
        if (_held is not { } held || !ReferenceEquals(held.Owner, tab))
        {
            return null;
        }
        if (held.View.IsDisposed || !TabRules.SameFolder(held.Path, tab.Path) || held.Order != order)
        {
            Release("the tab's folder or order changed");
            return null;
        }
        _held = null;
        Diag.Info(Target, "tab listing taken back", new LogField("path", held.Path), new LogField("listing_id", held.ListingId),
            new LogField("entries", held.View.Count), new LogField("kept_ms", nowMilliseconds() - _parkedAt));
        return held;
    }

    /// <summary>Lets the kept listing go once its time is up; true when it did.</summary>
    public bool ReleaseIfExpired()
    {
        if (DueInMs is not { } due || due > 0)
        {
            return false;
        }
        Release("its time was up");
        return true;
    }

    /// <summary>
    /// Lets the kept listing go when it is <paramref name="listingId"/>: the
    /// core said its folder changed or is gone. True when it was the kept one.
    /// </summary>
    public bool Release(ulong listingId, string why)
    {
        if (listingId == 0 || _held?.ListingId != listingId)
        {
            return false;
        }
        Release(why);
        return true;
    }

    /// <summary>Lets the kept listing go when its tab is not among <paramref name="tabs"/> any more (closed, or moved to the other pane).</summary>
    public void ReleaseUnlessAmong(IEnumerable<PaneTab> tabs)
    {
        if (_held is { } held && !tabs.Any(tab => ReferenceEquals(tab, held.Owner)))
        {
            Release("its tab went");
        }
    }

    /// <summary>Lets the kept listing go, here and in the core.</summary>
    public void Release(string why)
    {
        if (Take(why, "tab listing released") is { } gone)
        {
            _ = CloseAsync(gone.ListingId);
        }
    }

    /// <summary>
    /// Lets the kept listing go here only: the core started again or stops,
    /// so its listing IDs mean nothing now and must not be closed (a new core
    /// may have given one to another listing).
    /// </summary>
    public void Forget(string why) => Take(why, "tab listing forgotten");

    private Entry? Take(string why, string message)
    {
        if (_held is not { } held)
        {
            return null;
        }
        _held = null;
        held.View.Dispose();
        Diag.Info(Target, message, new LogField("why", why), new LogField("path", held.Path), new LogField("listing_id", held.ListingId),
            new LogField("kept_ms", nowMilliseconds() - _parkedAt));
        return held;
    }

    private async Task CloseAsync(ulong listingId)
    {
        try
        {
            await core.RequestAsync(new CloseListingRequest(listingId));
        }
        catch (IOException error)
        {
            Diag.Debug(Target, "cannot close a kept listing", new LogField("listing_id", listingId), new LogField("error", error.Message));
        }
    }
}
