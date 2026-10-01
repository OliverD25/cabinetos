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

    private const string Window = "InputSiteWindowClass";
    private const string Chromium = "Chrome_WidgetWin_0";

    // What the window does on each check of one hand-over, given what each check sees: the class of the input window that has
    // the keys. A hand-over again follows each check that does not see the page's, so the next check has one more attempt.
    private static List<PageKeyboardStep> Chain(params string?[] seen)
    {
        var steps = new List<PageKeyboardStep>();
        for (var attempt = 0; attempt < seen.Length; attempt++)
        {
            var step = PageKeyboard.Next(stillWanted: true, xamlOnPage: true, seen[attempt], attempt);
            steps.Add(step);
            if (step != PageKeyboardStep.HandOverAgain)
            {
                break;
            }
        }
        return steps;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(PageKeyboard.Attempts)]
    public void A_page_that_takes_the_keyboard_late_is_found_at_whichever_check_it_has_it_the_last_one_too(int checkWithKeys)
    {
        // The first checks see the window's own input window; the page has the keys only at the check numbered so.
        var seen = Enumerable.Range(0, checkWithKeys + 1).Select(check => check == checkWithKeys ? Chromium : Window).ToArray();
        var steps = Chain(seen);
        Assert.Equal(checkWithKeys + 1, steps.Count);
        Assert.All(steps.Take(checkWithKeys), step => Assert.Equal(PageKeyboardStep.HandOverAgain, step));
        Assert.Equal(PageKeyboardStep.Done, steps[^1]);
    }

    [Fact]
    public void A_page_that_never_takes_the_keyboard_is_handed_it_again_exactly_Attempts_times_and_then_given_up()
    {
        var steps = Chain(Enumerable.Repeat<string?>(Window, PageKeyboard.Attempts + 3).ToArray());
        // Attempts hand-overs again, one check after each and one after the first hand-over: the last of them gives up.
        Assert.Equal(PageKeyboard.Attempts + 1, steps.Count);
        Assert.Equal(PageKeyboard.Attempts, steps.Count(step => step == PageKeyboardStep.HandOverAgain));
        Assert.Equal(PageKeyboardStep.GiveUp, steps[^1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Window)]
    public void A_check_that_sees_no_page_window_gives_up_at_the_limit_and_stays_given_up(string? seen)
    {
        // No input window at all (the window is not active) is no page's either.
        Assert.Equal(PageKeyboardStep.HandOverAgain, PageKeyboard.Next(true, true, seen, PageKeyboard.Attempts - 1));
        Assert.Equal(PageKeyboardStep.GiveUp, PageKeyboard.Next(true, true, seen, PageKeyboard.Attempts));
        Assert.Equal(PageKeyboardStep.GiveUp, PageKeyboard.Next(true, true, seen, PageKeyboard.Attempts + 7));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Window)]
    [InlineData(Chromium)]
    public void A_chain_that_nobody_wants_ends_at_any_check_without_a_hand_over(string? seen)
    {
        // The terminal hid, or the keyboard went to a pane: the check ends the chain, whichever attempt it is.
        for (var attempt = 0; attempt <= PageKeyboard.Attempts + 1; attempt++)
        {
            Assert.Equal(PageKeyboardStep.Stop, PageKeyboard.Next(stillWanted: false, xamlOnPage: true, seen, attempt));
            Assert.Equal(PageKeyboardStep.Stop, PageKeyboard.Next(stillWanted: true, xamlOnPage: false, seen, attempt));
        }
    }

    [Fact]
    public void Every_chain_of_checks_ends_in_at_most_Attempts_plus_one_checks_whatever_the_checks_see()
    {
        // All the ways the window can look on each check: wanted or not, XAML's focus on the page or not, the input window.
        var looks = new List<(bool Wanted, bool OnPage, string? Class)>();
        foreach (var wanted in new[] { true, false })
        {
            foreach (var onPage in new[] { true, false })
            {
                foreach (var windowClass in new[] { null, "", Window, Chromium })
                {
                    looks.Add((wanted, onPage, windowClass));
                }
            }
        }
        // Every sequence of looks for Attempts + 1 checks: the chain has ended by its last one, with no hand-over again after it.
        var sequences = looks.Count;
        for (var i = 1; i < PageKeyboard.Attempts + 1; i++)
        {
            sequences *= looks.Count;
        }
        for (var n = 0; n < sequences; n++)
        {
            var rest = n;
            var ended = false;
            for (var attempt = 0; attempt <= PageKeyboard.Attempts; attempt++)
            {
                var look = looks[rest % looks.Count];
                rest /= looks.Count;
                if (PageKeyboard.Next(look.Wanted, look.OnPage, look.Class, attempt) != PageKeyboardStep.HandOverAgain)
                {
                    ended = true;
                    break;
                }
            }
            Assert.True(ended, $"sequence {n} never ended");
        }
    }
}
