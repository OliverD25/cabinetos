using System.Text.Json;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Settings;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Phase 19c: the compact overlay's parts that need no window (docs/ui.md, "Compact overlay"): the size it opens
/// at, where it opens, the size as <c>ui.compactOverlay</c> keeps it, and the settings reading it.
/// </summary>
public class CompactOverlayTests
{
    [Fact]
    public void Ui_compactOverlay_is_two_whole_numbers_or_null_and_the_settings_read_it()
    {
        Assert.Null(UiSettings.Defaults.CompactOverlay);
        using var config = JsonDocument.Parse("""{"version":1,"ui":{"compactOverlay":{"width":400,"height":500}}}""");
        Assert.True(Schemas.Config.Evaluate(config.RootElement).IsValid);
        Assert.Equal(new CompactSize(400, 500), UiSettings.FromConfig(config.RootElement).CompactOverlay);

        // Absent, null, and what the core would refuse (the window never meets it): the default size.
        foreach (var text in new[]
        {
            """{"version":1}""",
            """{"version":1,"ui":{"compactOverlay":null}}""",
            """{"version":1,"ui":{"compactOverlay":{"width":400}}}""",
            """{"version":1,"ui":{"compactOverlay":{"width":400,"height":"tall"}}}""",
            """{"version":1,"ui":{"compactOverlay":{"width":400.5,"height":500}}}""",
            """{"version":1,"ui":{"compactOverlay":{"width":239,"height":500}}}""",
            """{"version":1,"ui":{"compactOverlay":{"width":400,"height":4001}}}""",
            """{"version":1,"ui":{"compactOverlay":[400,500]}}""",
        })
        {
            using var none = JsonDocument.Parse(text);
            Assert.Null(UiSettings.FromConfig(none.RootElement).CompactOverlay);
        }

        // The schema agrees with the core: null is fine, a side out of bounds or a missing one is not.
        using var cleared = JsonDocument.Parse("""{"version":1,"ui":{"compactOverlay":null}}""");
        Assert.True(Schemas.Config.Evaluate(cleared.RootElement).IsValid);
        foreach (var bad in new[]
        {
            """{"version":1,"ui":{"compactOverlay":{"width":239,"height":500}}}""",
            """{"version":1,"ui":{"compactOverlay":{"width":400,"height":4001}}}""",
            """{"version":1,"ui":{"compactOverlay":{"width":400}}}""",
        })
        {
            using var refused = JsonDocument.Parse(bad);
            Assert.False(Schemas.Config.Evaluate(refused.RootElement).IsValid, bad);
        }
    }

    [Theory]
    // Nothing saved: 480 by 640, in the room of an ordinary work area.
    [InlineData(null, null, 1920, 1040, 480, 640, false)]
    // A saved size comes back as it is, and says it came from the file.
    [InlineData(400, 500, 1920, 1040, 400, 500, true)]
    // Under the window's minimum (360 wide, 480 high): raised to it.
    [InlineData(240, 300, 1920, 1040, 360, 480, true)]
    // Larger than the work area: cut to it (1920 by 1080 at 125 % leaves 1536 by 832 DIPs).
    [InlineData(3000, 3000, 1536, 832, 1536, 832, true)]
    // The default on a screen shorter than it: cut to the room.
    [InlineData(null, null, 1280, 560, 480, 560, false)]
    // A screen smaller than the minimum: the minimum wins, the window is not made smaller than it can be.
    [InlineData(null, null, 300, 400, 360, 480, false)]
    // A work area of fractional DIPs (a scale of 1.75): whole pixels below it.
    [InlineData(4000, 4000, 1097.14, 594.28, 1097, 594, true)]
    public void The_drawer_opens_at_the_saved_size_or_480_by_640_raised_to_the_minimum_and_cut_to_the_work_area(
        int? width, int? height, double areaWidth, double areaHeight, int expectedWidth, int expectedHeight, bool expectedSaved)
    {
        CompactSize? saved = width is { } w && height is { } h ? new CompactSize(w, h) : null;
        var (size, fromFile) = CompactOverlayLayout.Decide(saved, areaWidth, areaHeight);
        Assert.Equal(new CompactSize(expectedWidth, expectedHeight), size);
        Assert.Equal(expectedSaved, fromFile);
    }

    [Fact]
    public void The_drawer_opens_where_the_window_was_and_is_moved_only_as_far_as_keeps_it_on_the_work_area()
    {
        // A 1920 by 1040 work area that starts at (0, 0), and one on a second screen to the left of it.
        Assert.Equal(300, CompactOverlayLayout.Place(300, 600, 0, 1920));
        // Past the right edge: its right edge meets the area's.
        Assert.Equal(1920 - 600, CompactOverlayLayout.Place(1700, 600, 0, 1920));
        // Left of the area (a window half off the screen): at the area's start.
        Assert.Equal(0, CompactOverlayLayout.Place(-250, 600, 0, 1920));
        Assert.Equal(-1920, CompactOverlayLayout.Place(-2500, 600, -1920, 1920));
        Assert.Equal(-600, CompactOverlayLayout.Place(-200, 600, -1920, 1920));
        // Larger than the area: the start, never before it.
        Assert.Equal(0, CompactOverlayLayout.Place(400, 2000, 0, 1920));
    }

    [Fact]
    public void A_window_s_size_is_saved_as_whole_dips_within_the_core_s_bounds_and_as_the_json_set_value_takes()
    {
        // 600 by 800 pixels at 125 %: 480 by 640 DIPs, which round-trips through the entry's pixels.
        Assert.Equal(new CompactSize(480, 640), CompactOverlayLayout.ToSetting(600, 800, 1.25));
        Assert.Equal(new CompactSize(480, 640), CompactOverlayLayout.ToSetting(840, 1120, 1.75));
        Assert.Equal(new CompactSize(400, 500), CompactOverlayLayout.ToSetting(400, 500, 1));
        // A maximized drawer on a large screen, or a tiny one: the core's bounds, so the write is never refused.
        Assert.Equal(new CompactSize(4000, 4000), CompactOverlayLayout.ToSetting(9000, 9000, 1));
        Assert.Equal(new CompactSize(240, 240), CompactOverlayLayout.ToSetting(10, 10, 1));
        // A scale that is not known (0) is taken as 1.
        Assert.Equal(new CompactSize(500, 500), CompactOverlayLayout.ToSetting(500, 500, 0));

        Assert.Equal("""{"width":480,"height":640}""", CompactOverlayLayout.ToJson(new CompactSize(480, 640)).GetRawText());
        var json = CompactOverlayLayout.ToJson(new CompactSize(520, 700)).GetRawText();
        using var config = JsonDocument.Parse("""{"version":1,"ui":{"compactOverlay":""" + json + "}}");
        Assert.True(Schemas.Config.Evaluate(config.RootElement).IsValid);
        Assert.Equal(new CompactSize(520, 700), UiSettings.FromConfig(config.RootElement).CompactOverlay);
    }

    [Fact]
    public void The_defaults_and_minimums_are_the_plan_s()
    {
        Assert.Equal(new CompactSize(480, 640), CompactOverlayLayout.Default);
        Assert.Equal(360, CompactOverlayLayout.MinWidth);
        // The core's bounds: what the schema says.
        Assert.Equal(240, CompactOverlayLayout.MinSetting);
        Assert.Equal(4000, CompactOverlayLayout.MaxSetting);
    }
}
