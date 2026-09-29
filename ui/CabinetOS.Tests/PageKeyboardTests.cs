using CabinetOS.Core.Presentation;

namespace CabinetOS.Tests;

/// <summary>
/// The keyboard handed to a web page (the terminal, a tool): the window checks
/// that Windows really sends the keys there, and hands them over again when
/// WinUI left them in the window (the live check of 2026-09-29, Ctrl+P with
/// the Markdown Preview open).
/// </summary>
public class PageKeyboardTests
{
    [Theory]
    [InlineData("Chrome_WidgetWin_0", true)]
    [InlineData("Chrome_WidgetWin_1", true)]
    [InlineData("Chrome_RenderWidgetHostHWND", true)]
    [InlineData("InputSiteWindowClass", false)]
    [InlineData("Microsoft.UI.Content.DesktopChildSiteBridge", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_chromiums_input_windows_are_a_pages(string? windowClass, bool page)
    {
        Assert.Equal(page, PageKeyboard.IsPageWindow(windowClass));
        Assert.Equal(page, PageKeyboard.PageSawKey(windowClass));
    }

    [Fact]
    public void Keys_in_the_pages_input_window_need_nothing_more()
    {
        Assert.Equal(PageKeyboardStep.Done, PageKeyboard.Next(stillWanted: true, xamlOnPage: true, "Chrome_WidgetWin_0", attempt: 0));
    }

    [Fact]
    public void Xamls_focus_on_the_page_with_the_keys_in_the_window_is_handed_over_again_then_given_up()
    {
        // The fault: XAML's focus on the terminal, the keys in WinUI's own input window.
        for (var attempt = 0; attempt < PageKeyboard.Attempts; attempt++)
        {
            Assert.Equal(PageKeyboardStep.HandOverAgain, PageKeyboard.Next(true, true, "InputSiteWindowClass", attempt));
        }
        Assert.Equal(PageKeyboardStep.GiveUp, PageKeyboard.Next(true, true, "InputSiteWindowClass", PageKeyboard.Attempts));
        Assert.Equal(PageKeyboardStep.HandOverAgain, PageKeyboard.Next(true, true, null, 0));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void A_page_that_hid_or_lost_xamls_focus_meanwhile_is_left_alone(bool stillWanted, bool xamlOnPage)
    {
        // The user went on (the terminal hid, the palette opened): no focus is pulled back to the page.
        Assert.Equal(PageKeyboardStep.Stop, PageKeyboard.Next(stillWanted, xamlOnPage, "InputSiteWindowClass", 0));
        Assert.Equal(PageKeyboardStep.Stop, PageKeyboard.Next(stillWanted, xamlOnPage, "Chrome_WidgetWin_0", 0));
    }

    [Fact]
    public void The_check_waits_for_a_frame_and_chromiums_own_move()
    {
        Assert.InRange(PageKeyboard.CheckAfter.TotalMilliseconds, 50, 300);
        Assert.InRange(PageKeyboard.Attempts, 1, 5);
    }
}
