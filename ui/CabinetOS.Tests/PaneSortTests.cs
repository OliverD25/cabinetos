using CabinetOS.Core.Listing;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Tests;

/// <summary>A pane's own sort (Ctrl+F3 to Ctrl+F6, docs/ui.md "Total Commander's keys").</summary>
public class PaneSortTests
{
    [Theory]
    [InlineData("name", false, "name", "name", true)]
    [InlineData("name", true, "name", "name", false)]
    [InlineData("name", false, "extension", "extension", false)]
    [InlineData("extension", false, "extension", "extension", true)]
    // Total Commander puts the largest and the newest first.
    [InlineData("name", false, "size", "size", true)]
    [InlineData("size", true, "size", "size", false)]
    [InlineData("name", false, "modified", "modified", true)]
    [InlineData("modified", true, "modified", "modified", false)]
    [InlineData("kind", false, "name", "name", false)]
    public void The_same_key_again_reverses_and_a_new_key_starts_in_its_own_direction(
        string key, bool descending, string pressed, string nextKey, bool nextDescending) =>
        Assert.Equal(new SortSpec(nextKey, nextDescending), PaneSort.Next(new SortSpec(key, descending), pressed));

    [Theory]
    [InlineData("name", false, "name, A to Z")]
    [InlineData("name", true, "name, Z to A")]
    [InlineData("extension", false, "extension, A to Z")]
    [InlineData("size", true, "size, largest first")]
    [InlineData("size", false, "size, smallest first")]
    [InlineData("modified", true, "date modified, newest first")]
    [InlineData("modified", false, "date modified, oldest first")]
    [InlineData("kind", false, "kind, A to Z")]
    public void A_sort_is_said_in_words(string key, bool descending, string words) =>
        Assert.Equal(words, PaneSort.Describe(new SortSpec(key, descending)));

    [Fact]
    public void Only_the_extension_key_needs_protocol_12()
    {
        Assert.Equal(12u, PaneSort.ProtocolFor(PaneSort.Extension));
        Assert.Equal(0u, PaneSort.ProtocolFor(PaneSort.Size));
    }
}
