using CabinetOS.Core.Listing;
using CabinetOS.Core.Prompts;

namespace CabinetOS.Tests;

/// <summary>
/// The prompts in the palette's frame (docs/ui.md, "Total Commander's keys"):
/// the pattern box and its history, the pinned folders list, and the ranges
/// of rows <c>match_entries</c> answers.
/// </summary>
public class PromptTests
{
    [Fact]
    public void The_pattern_box_offers_the_last_pattern_and_keeps_ten_without_repeats()
    {
        var history = new PatternHistory();
        Assert.Equal("*.*", history.Last);
        Assert.Empty(history.Items);

        for (var i = 0; i < 12; i++)
        {
            history.Add($"*.{i}");
        }
        history.Add("  *.3 ");
        history.Add("");

        Assert.Equal("*.3", history.Last);
        Assert.Equal(PatternHistory.Size, history.Items.Count);
        Assert.Equal(["*.3", "*.11", "*.10", "*.9", "*.8", "*.7", "*.6", "*.5", "*.4", "*.2"], history.Items);
    }

    [Fact]
    public void A_pick_list_filters_by_title_or_detail_and_keeps_its_last_row_for_last()
    {
        var list = new PromptList(PromptKind.Pick,
        [
            new PromptRow("Desktop", @"C:\Users\me\Desktop"),
            new PromptRow("music", @"E:\music"),
            new PromptRow("Downloads", @"C:\Users\me\Downloads"),
            new PromptRow("Pin this folder", @"D:\work", Sticky: true),
        ]);
        Assert.Equal(0, list.Highlight);
        Assert.Equal(4, list.Shown.Count);

        list.SetText("DOWN");
        Assert.Equal(["Downloads", "Pin this folder"], list.Shown.Select(r => r.Title));
        Assert.Equal("Downloads", list.Highlighted!.Title);

        list.SetText(@"e:\");
        Assert.Equal(["music", "Pin this folder"], list.Shown.Select(r => r.Title));

        list.SetText("nothing like it");
        Assert.Equal(["Pin this folder"], list.Shown.Select(r => r.Title));
        Assert.Equal("Pin this folder", list.Highlighted!.Title);
    }

    [Fact]
    public void Up_and_down_stay_in_the_list_and_a_text_list_hands_the_row_to_the_box()
    {
        var picks = new PromptList(PromptKind.Pick, [new PromptRow("a"), new PromptRow("b"), new PromptRow("c")]);
        picks.Move(-1);
        Assert.Equal("a", picks.Highlighted!.Title);
        picks.Move(5);
        Assert.Equal("c", picks.Highlighted!.Title);

        // The pattern box: its rows are earlier patterns; typing does not hide them, and moving
        // to one puts it in the box.
        var patterns = new PromptList(PromptKind.Text, [new PromptRow("*.txt"), new PromptRow("*.md")]);
        Assert.Equal(-1, patterns.Highlight);
        patterns.SetText("*.p");
        Assert.Equal(2, patterns.Shown.Count);
        Assert.Equal("*.txt", patterns.Move(1));
        Assert.Equal("*.md", patterns.Move(1));
        Assert.Equal("*.md", patterns.Move(1));
    }

    [Fact]
    public void A_drive_s_letter_picks_its_row()
    {
        var drives = new PromptList(PromptKind.Pick,
        [
            new PromptRow("System Disk (C:)", "1.2 TB free", Key: 'C'),
            new PromptRow("Data (D:)", "118 GB free", Key: 'D'),
            new PromptRow("Pin this folder"),
        ]);
        Assert.Equal(1, drives.IndexOfKey('d'));
        Assert.Equal(0, drives.IndexOfKey('C'));
        Assert.Equal(-1, drives.IndexOfKey('E'));
    }

    [Fact]
    public void Ranges_of_matches_become_rows_inside_the_listing()
    {
        // A pair that is not a pair is skipped; rows past the listing's end are not rows.
        IReadOnlyList<IReadOnlyList<ulong>> ranges = [[0, 2], [5, 3], [98, 5], [7]];
        Assert.Equal([0, 1, 5, 6, 7, 98, 99], EntryRanges.Rows(ranges, count: 100));
        Assert.Equal(10UL, EntryRanges.Count(ranges));
        Assert.Empty(EntryRanges.Rows([], count: 100));
    }

    [Theory]
    [InlineData("report.TXT", "*.txt")]
    [InlineData("archive.tar.gz", "*.gz")]
    // The core's rule for an extension, as for its sort: from the last dot on, so .gitignore has one.
    [InlineData(".gitignore", "*.gitignore")]
    // No extension: Total Commander's "*.", a name without a dot.
    [InlineData("LICENSE", "*.")]
    [InlineData("trailing.", "*.")]
    public void The_same_extension_is_a_pattern_the_core_matches(string name, string pattern) =>
        Assert.Equal(pattern, PatternHistory.SameExtension(name));
}
