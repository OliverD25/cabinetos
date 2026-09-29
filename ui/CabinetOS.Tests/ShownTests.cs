using CabinetOS.Core.Presentation;

namespace CabinetOS.Tests;

/// <summary>A row sets a value on its elements only when it differs from the one shown (docs/ui.md, "Scrolling").</summary>
public class ShownTests
{
    [Fact]
    public void A_value_is_taken_when_it_differs_from_the_one_shown()
    {
        var shown = new Shown<string?>();

        Assert.True(shown.Take("Today 09:11"));
        // Equal by value, not by reference: a new string with the same text is the same text.
        Assert.False(shown.Take(new string("Today 09:11".ToCharArray())));
        Assert.True(shown.Take("Yesterday 16:20"));
        Assert.True(shown.Take(null));
        Assert.False(shown.Take(null));
    }

    [Fact]
    public void A_forgotten_value_is_taken_again()
    {
        var shown = new Shown<bool>();
        Assert.True(shown.Take(false));
        Assert.False(shown.Take(false));

        shown.Forget();

        Assert.True(shown.Take(false));
    }
}
