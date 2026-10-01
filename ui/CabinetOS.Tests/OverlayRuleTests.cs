using CabinetOS.Core.Presentation;

namespace CabinetOS.Tests;

/// <summary>
/// One overlay at a time (docs/keybindings.md, "One overlay at a time"; the keys audit's proposal P4): what an
/// opening overlay closes first.
/// </summary>
public class OverlayRuleTests
{
    private static HashSet<Overlay> Open(params Overlay[] overlays) => [.. overlays];

    [Theory]
    [InlineData(Overlay.Palette)]
    [InlineData(Overlay.QuickOpen)]
    [InlineData(Overlay.Prompt)]
    [InlineData(Overlay.ThemePicker)]
    [InlineData(Overlay.PluginList)]
    public void An_overlay_that_opens_closes_every_other_one_and_never_itself(Overlay opening)
    {
        var everything = Open(Enum.GetValues<Overlay>());

        var closed = OverlayRule.ToClose(opening, everything);

        Assert.DoesNotContain(opening, closed);
        Assert.Equal(Enum.GetValues<Overlay>().Length - 1, closed.Count);
        // Closing them all leaves only the opening one: at most one overlay is open, so Esc closes the one on screen.
        Assert.Equal([opening], everything.Except(closed));
    }

    [Fact]
    public void Nothing_is_closed_when_nothing_else_is_open()
    {
        Assert.Empty(OverlayRule.ToClose(Overlay.ThemePicker, Open()));
        Assert.Empty(OverlayRule.ToClose(Overlay.ThemePicker, Open(Overlay.ThemePicker)));
    }

    [Fact]
    public void The_picker_that_opens_from_the_palette_or_the_drive_list_closes_it()
    {
        Assert.Equal([Overlay.Palette], OverlayRule.ToClose(Overlay.ThemePicker, Open(Overlay.Palette)));
        Assert.Equal([Overlay.Prompt], OverlayRule.ToClose(Overlay.ThemePicker, Open(Overlay.Prompt)));
        Assert.Equal([Overlay.QuickOpen], OverlayRule.ToClose(Overlay.ThemePicker, Open(Overlay.QuickOpen)));
    }

    [Fact]
    public void They_close_in_the_order_of_the_list_whatever_order_they_were_opened_in()
    {
        var closed = OverlayRule.ToClose(Overlay.Palette, Open(Overlay.PluginList, Overlay.Prompt, Overlay.ThemePicker));

        Assert.Equal([Overlay.Prompt, Overlay.ThemePicker, Overlay.PluginList], closed);
    }

    [Fact]
    public void A_view_that_covers_the_overlays_closes_them_all()
    {
        var everything = Open(Enum.GetValues<Overlay>());

        Assert.Equal(Enum.GetValues<Overlay>(), OverlayRule.ToClose(opening: null, everything));
    }
}
