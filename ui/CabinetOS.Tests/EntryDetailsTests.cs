using CabinetOS.Core.Listing;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Tests;

/// <summary>Type names and icons, asked for a page of rows at a time.</summary>
public class EntryDetailsTests
{
    [Fact]
    public void The_rows_on_screen_are_asked_for_in_pages_and_each_page_once()
    {
        var cache = new EntryDetailsCache();
        cache.Reset(7, 1);

        Assert.Equal([(0u, 128u)], cache.TakePagesToRequest(0, 30, 1000));
        Assert.Empty(cache.TakePagesToRequest(10, 40, 1000));
        Assert.Equal([(128u, 128u), (256u, 128u)], cache.TakePagesToRequest(120, 300, 1000));
        Assert.Equal([(896u, 104u)], cache.TakePagesToRequest(990, 2000, 1000));

        cache.Forget(128);
        Assert.Equal([(128u, 128u)], cache.TakePagesToRequest(130, 131, 1000));
    }

    [Fact]
    public void A_reply_for_another_section_is_not_used()
    {
        var cache = new EntryDetailsCache();
        cache.Reset(7, 2);
        var detail = new EntryDetail("Text Document", "ext:.txt");

        Assert.False(cache.Apply(new EntryDetailsReply(7, 1, 0, [detail])));
        Assert.False(cache.Apply(new EntryDetailsReply(8, 2, 0, [detail])));
        Assert.Null(cache.Get(0));

        Assert.True(cache.Apply(new EntryDetailsReply(7, 2, 5, [detail, new EntryDetail("File folder", "folder")])));
        Assert.Equal(detail, cache.Get(5));
        Assert.Equal("folder", cache.Get(6)!.IconKey);

        cache.Reset(7, 3);
        Assert.Null(cache.Get(5));
        Assert.Single(cache.TakePagesToRequest(0, 0, 10));
    }

    [Fact]
    public void A_known_extension_is_named_at_once_but_programs_wait_for_their_own_icon()
    {
        var known = new ExtensionDetails();
        known.Learn("notes.TXT", false, new EntryDetail("Text Document", "ext:.txt"));
        known.Learn("src", true, new EntryDetail("File folder", "folder"));
        known.Learn("app.exe", false, new EntryDetail("Application", "path:396bbcd455199596"));

        Assert.Equal("Text Document", known.Guess("other.txt", false)!.TypeName);
        Assert.Equal("folder", known.Guess("docs", true)!.IconKey);
        Assert.Null(known.Guess("tool.exe", false));
        Assert.Null(known.Guess("image.png", false));
    }

    [Theory]
    [InlineData(1.0, 16u)]
    [InlineData(1.25, 24u)]
    [InlineData(1.5, 24u)]
    [InlineData(2.0, 32u)]
    [InlineData(2.5, 48u)]
    [InlineData(4.0, 48u)]
    public void The_icon_size_follows_the_screens_scale(double scale, uint size) =>
        Assert.Equal(size, IconSizes.For(scale));
}
