using System.Text.Json;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Themes;

namespace CabinetOS.Tests;

/// <summary>The divider between the two file panes (docs/ui.md, "The divider between the panes").</summary>
public class PaneSplitTests
{
    // The default look's narrowest pane: Name 120, three columns of 40, no gaps, 14 px of padding each side.
    private static readonly double MinPane = PaneSplit.MinPaneWidth(MetricsMapper.Default);

    [Fact]
    public void A_pane_keeps_the_room_its_columns_need()
    {
        var m = MetricsMapper.Default;
        Assert.Equal(m.NameColumnMinWidth + (3 * 40) + (3 * m.ColumnGap) + (2 * m.ColumnHeaderPaddingX), MinPane);
        Assert.True(MinPane is > 200 and < 400, $"{MinPane}");
    }

    [Fact]
    public void No_setting_gives_equal_panes_and_a_setting_is_kept_in_the_range()
    {
        Assert.Equal(0.5, PaneSplit.Resolve(null, 1200, MinPane));
        Assert.Equal(0.35, PaneSplit.Resolve(0.35, 1200, MinPane));
        // 0.2 of 1200 is 240, under the pane's least: the share cannot be less than the least pane.
        Assert.Equal(MinPane / 1200, PaneSplit.Resolve(0.2, 1200, MinPane), 6);
        Assert.Equal(1 - (MinPane / 1200), PaneSplit.Resolve(0.8, 1200, MinPane), 6);
    }

    [Fact]
    public void In_a_wide_space_the_limits_are_0_2_and_0_8()
    {
        Assert.Equal((0.2, 0.8), PaneSplit.Range(3000, MinPane));
        Assert.Equal(0.2, PaneSplit.Clamp(0.01, 3000, MinPane));
        Assert.Equal(0.8, PaneSplit.Clamp(0.99, 3000, MinPane));
    }

    [Fact]
    public void A_space_too_narrow_for_two_such_panes_leaves_only_equal_panes()
    {
        // 600 px window, a sidebar and the gaps: the main column is about 396 px, so each pane is under its least already.
        Assert.Equal((0.5, 0.5), PaneSplit.Range(396, MinPane));
        Assert.Equal(0.5, PaneSplit.Resolve(0.3, 396, MinPane));
        Assert.Equal(0.5, PaneSplit.Drag(198, 50, 396, MinPane));
        Assert.Equal((0.5, 0.5), PaneSplit.Range(0, MinPane));
    }

    [Fact]
    public void A_share_is_a_proportion_so_a_resize_of_the_window_keeps_it()
    {
        // 0.3 holds from a 1000 px space to a 1600 px space, where the pixels differ.
        Assert.Equal(0.3, PaneSplit.Resolve(0.3, 1000, MinPane));
        Assert.Equal(0.3, PaneSplit.Resolve(0.3, 1600, MinPane));
    }

    [Fact]
    public void A_drag_moves_the_left_pane_by_the_pointer_and_stops_at_the_limits()
    {
        // Equal in 1200 px: 600 wide; 120 px to the right makes it 720, a share of 0.6.
        Assert.Equal(0.6, PaneSplit.Drag(600, 120, 1200, MinPane), 6);
        Assert.Equal(0.4, PaneSplit.Drag(600, -120, 1200, MinPane), 6);
        // Far to the right: the right pane keeps its least.
        Assert.Equal(1 - (MinPane / 1200), PaneSplit.Drag(600, 5000, 1200, MinPane), 6);
        Assert.Equal(MinPane / 1200, PaneSplit.Drag(600, -5000, 1200, MinPane), 6);
        // In a very wide space the 0.2 to 0.8 limits decide.
        Assert.Equal(0.8, PaneSplit.Drag(1500, 5000, 3000, MinPane));
    }

    [Fact]
    public void The_setting_has_three_decimals_from_0_2_to_0_8()
    {
        Assert.Equal(0.437, PaneSplit.ToSetting(0.43712));
        Assert.Equal(0.2, PaneSplit.ToSetting(0.05));
        Assert.Equal(0.8, PaneSplit.ToSetting(0.97));
        Assert.Equal(0.5, PaneSplit.ToSetting(0.5));
    }

    [Theory]
    [InlineData("""{"ui":{"paneSplit":0.35}}""", 0.35)]
    [InlineData("""{"ui":{"paneSplit":0.2}}""", 0.2)]
    [InlineData("""{"ui":{"paneSplit":0.8}}""", 0.8)]
    [InlineData("""{"ui":{"paneSplit":null}}""", null)]
    [InlineData("""{"ui":{}}""", null)]
    [InlineData("""{"ui":{"paneSplit":0.1}}""", null)]
    [InlineData("""{"ui":{"paneSplit":0.9}}""", null)]
    [InlineData("""{"ui":{"paneSplit":"half"}}""", null)]
    [InlineData("""[]""", null)]
    public void The_configuration_gives_the_share_or_nothing_for_a_value_it_would_refuse(string json, double? expected)
    {
        using var config = JsonDocument.Parse(json);
        Assert.Equal(expected, PaneSplit.FromConfig(config.RootElement));
    }
}
