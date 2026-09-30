using CabinetOS.Core.Shell;

namespace CabinetOS.Tests;

/// <summary>
/// Where a context menu's top-left corner goes (docs/ui.md, "The context menu"): at the point, as Explorer opens it, and above
/// or to the left of it when it would not fit. The rule is pure arithmetic; the end-to-end tests read the real menu's place.
/// </summary>
public class MenuPlacementTests
{
    private const double Width = 300;
    private const double Height = 270;

    private static (double X, double Y) Corner(double x, double y, double windowWidth = 1200, double windowHeight = 700) =>
        MenuPlacement.Corner(x, y, Width, Height, windowWidth, windowHeight);

    [Fact]
    public void A_menu_that_fits_has_its_corner_at_the_point()
    {
        Assert.Equal((300.0, 200.0), Corner(300, 200));
    }

    [Fact]
    public void A_menu_that_does_not_fit_below_ends_at_the_point()
    {
        // 500 + 270 is past the window's 700 less the margin: the menu hangs above, its bottom at the point.
        Assert.Equal((300.0, 230.0), Corner(300, 500));
    }

    [Fact]
    public void A_menu_that_does_not_fit_on_the_right_ends_at_the_point()
    {
        Assert.Equal((700.0, 200.0), Corner(1000, 200));
    }

    [Fact]
    public void Near_the_bottom_right_corner_it_goes_up_and_to_the_left()
    {
        Assert.Equal((900.0, 430.0), Corner(1200, 700));
    }

    [Fact]
    public void A_menu_that_just_fits_stays_below_and_one_pixel_more_flips_it()
    {
        var last = 700 - MenuPlacement.Margin - Height;
        Assert.Equal((10.0, last), Corner(10, last));
        Assert.Equal(last + 1 - Height, Corner(10, last + 1).Y);
    }

    [Fact]
    public void A_menu_that_fits_neither_way_sits_against_the_bottom_edge()
    {
        // A 420 px window: 270 fits neither below 200 nor above it.
        var (_, y) = Corner(300, 200, windowHeight: 420);
        Assert.Equal(420 - Height - MenuPlacement.Margin, y);
    }

    [Fact]
    public void A_menu_taller_than_the_window_starts_at_the_margin()
    {
        var (_, y) = MenuPlacement.Corner(300, 100, Width, 900, 1200, 700);
        Assert.Equal(MenuPlacement.Margin, y);
    }

    /// <summary>
    /// A point outside the window (the keyboard's menu for a row the list had made but scrolled out of view, found by
    /// the speed review of 2026-10-01): the menu still lies inside the window. WinUI ended the process when a flyout
    /// was shown there.
    /// </summary>
    [Theory]
    [InlineData(281, 1638)]
    [InlineData(281, 5000)]
    [InlineData(-40, -300)]
    [InlineData(2400, 350)]
    public void A_point_outside_the_window_gives_a_menu_inside_it(double x, double y)
    {
        var (left, top) = Corner(x, y);
        Assert.InRange(left, 0, 1200 - Width);
        Assert.InRange(top, 0, 700 - Height);
    }

    [Fact]
    public void A_menu_wider_than_the_window_starts_at_the_margin()
    {
        var (x, _) = MenuPlacement.Corner(50, 100, 1000, Height, 800, 700);
        Assert.Equal(MenuPlacement.Margin, x);
    }
}
