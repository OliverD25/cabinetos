using CabinetOS.Core.Listing;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Restore Selection (Num /, <c>edit.restoreSelection</c>): the marks a file
/// command or an unmark cleared come back, found by name.
/// </summary>
[Collection(HandleTests.Name)]
public class MarkMemoryTests
{
    private static ListingView Open(params string[] names)
    {
        var bytes = TestSections.Build([.. names.Select((name, i) => new SyntheticEntry((ulong)i + 1, name, 1))]);
        return ListingView.Open(new SectionHandle(TestSections.CreateSection(bytes)), (ulong)bytes.Length);
    }

    private static SelectionModel Select(SelectionStyle style, int count, params int[] rows)
    {
        var selection = new SelectionModel();
        selection.SetStyle(style);
        selection.Reset(count, 0);
        selection.Clear();
        selection.SetMarks(rows, mark: true);
        return selection;
    }

    [Fact]
    public void The_cursor_row_alone_is_not_a_mark_but_any_other_selection_is()
    {
        Assert.False(MarkMemory.HasMarks(Select(SelectionStyle.Windows, 5, 0)));
        Assert.False(MarkMemory.HasMarks(Select(SelectionStyle.Commander, 5)));
        Assert.True(MarkMemory.HasMarks(Select(SelectionStyle.Windows, 5, 0, 3)));
        Assert.True(MarkMemory.HasMarks(Select(SelectionStyle.Commander, 5, 0)));
        Assert.True(MarkMemory.HasMarks(Select(SelectionStyle.Windows, 5, 2)));
    }

    [Fact]
    public void Marks_come_back_by_name_in_another_listing_of_the_folder()
    {
        using var before = Open("a.txt", "b.txt", "c.txt", "d.txt");
        var memory = new MarkMemory();
        memory.Remember(before, Select(SelectionStyle.Commander, 4, 1, 3));
        Assert.Equal(["b.txt", "d.txt"], memory.Names);

        // The listing changed: a new file at the top, c.txt deleted, d.txt moved away.
        using var after = Open("0.txt", "a.txt", "b.txt", "e.txt");
        var selection = Select(SelectionStyle.Commander, 4, 0);
        Assert.Equal(1, memory.Restore(after, selection));
        Assert.Equal([2], selection.Selected);
    }

    [Fact]
    public void Nothing_is_remembered_without_marks_so_the_last_marks_stay()
    {
        using var view = Open("a", "b", "c");
        var memory = new MarkMemory();
        Assert.True(memory.IsEmpty);
        memory.Remember(view, Select(SelectionStyle.Windows, 3, 0, 2));
        memory.Remember(view, Select(SelectionStyle.Windows, 3, 0));
        Assert.Equal(["a", "c"], memory.Names);
    }
}
