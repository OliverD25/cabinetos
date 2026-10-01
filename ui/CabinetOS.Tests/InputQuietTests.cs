using CabinetOS.Core.Presentation;

namespace CabinetOS.Tests;

/// <summary>
/// The quiet moment the window waits for before work done ahead that costs
/// a frame (the marketplace view's preparation; docs/ui.md, "The
/// marketplace"): a second without a key or the pointer.
/// </summary>
public class InputQuietTests
{
    private long _now = 50_000;

    [Fact]
    public void A_window_without_input_is_quiet_at_once()
    {
        var quiet = new InputQuiet(() => _now);

        Assert.True(quiet.IsQuiet);
        Assert.Equal(0, quiet.WaitMs);
    }

    [Fact]
    public void A_key_or_the_pointer_puts_it_off_for_the_quiet_time_and_each_new_one_again()
    {
        var quiet = new InputQuiet(() => _now);

        quiet.Touch();
        Assert.False(quiet.IsQuiet);
        Assert.Equal(InputQuiet.DefaultQuietMs, quiet.WaitMs);

        _now += 600;
        Assert.Equal(400, quiet.WaitMs);
        // A held key repeats, a wheel turns on: the wait starts over each time.
        quiet.Touch();
        _now += 999;
        Assert.Equal(1, quiet.WaitMs);
        Assert.False(quiet.IsQuiet);

        _now += 1;
        Assert.True(quiet.IsQuiet);
        _now += 60_000;
        Assert.Equal(0, quiet.WaitMs);
    }

    [Fact]
    public void The_quiet_time_can_be_set()
    {
        var quiet = new InputQuiet(() => _now, quietMs: 250);

        quiet.Touch();
        _now += 249;
        Assert.False(quiet.IsQuiet);
        _now += 1;
        Assert.True(quiet.IsQuiet);
    }
}
