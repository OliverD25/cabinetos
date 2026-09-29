using CabinetOS.Core.Listing;

namespace CabinetOS.Tests;

/// <summary>Quick search: letters typed in a pane jump to a name (Q1; docs/ui.md, "Total Commander's keys").</summary>
public class QuickSearchTests
{
    private long _now = 10_000;

    private QuickSearch Create() => new(() => _now);

    [Fact]
    public void Letters_typed_within_a_second_make_one_search_and_a_pause_starts_a_new_one()
    {
        var quick = Create();
        Assert.Equal("r", quick.Type('r'));
        _now += 400;
        Assert.Equal("re", quick.Type('e'));
        _now += 999;
        Assert.Equal("rep", quick.Type('p'));
        Assert.True(quick.IsActive);

        _now += QuickSearch.QuietMs;
        Assert.False(quick.IsActive);
        Assert.Equal("n", quick.Type('n'));
    }

    [Fact]
    public void Esc_clears_the_search()
    {
        var quick = Create();
        quick.Type('a');
        quick.Clear();
        Assert.False(quick.IsActive);
        Assert.Equal("", quick.Text);
        Assert.Equal("b", quick.Type('b'));
    }

    [Fact]
    public void A_new_search_starts_after_the_cursor_and_a_longer_one_at_it()
    {
        var quick = Create();
        quick.Type('r');
        // The same letter again finds the next name that starts with it, as in Explorer.
        Assert.Equal(6u, quick.FirstFrom(cursor: 5));
        quick.Type('e');
        // The row the cursor is on may still match the longer text.
        Assert.Equal(5u, quick.FirstFrom(cursor: 5));
        Assert.Equal(0u, Create().FirstFrom(cursor: -1));
    }

    [Theory]
    [InlineData("rep", "rep*")]
    [InlineData("Звіт", "Звіт*")]
    [InlineData("a?c", "a?c*")]
    public void The_typed_text_is_the_start_of_a_name(string text, string pattern) =>
        Assert.Equal(pattern, QuickSearch.PatternFor(text));

    [Theory]
    [InlineData("a;b")]
    [InlineData("a|b")]
    [InlineData("")]
    public void Text_that_would_change_the_pattern_s_meaning_finds_nothing(string text) =>
        Assert.Null(QuickSearch.PatternFor(text));
}
