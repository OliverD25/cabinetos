using System.Text.Json;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Settings;
using CabinetOS.Core.Themes;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// The file panes' column widths (docs/ui.md, "Column widths"): the theme's split when the user set
/// nothing, the three grips' drags and their minimums, the fit, <c>ui.columns</c> as JSON, and a
/// theme change that keeps the user's widths.
/// </summary>
public class ColumnLayoutTests
{
    private static readonly ThemeMetrics Default = MetricsMapper.Default;

    private static readonly ThemeMetrics Compact = MetricsMapper.Map(
        JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(Repo.Root, "sdk", "themes", "commander-compact.json")), ProtocolJson.Default.ColorTheme)!.Metrics);

    // A pane about 560 px wide: the list's columns share 532 px in the default look.
    private const double Available = 532;

    /// <summary>
    /// WinUI's Grid on its own terms: the star columns share what the pixel column and the gaps
    /// leave by weight; a star column whose share is under its minimum takes the minimum and
    /// leaves the others to share the rest, again by weight, until no share is under a minimum.
    /// </summary>
    private static double[] GridStars(double available, double gaps, double pixels, double[] weights, double[] minimums)
    {
        var widths = new double[weights.Length];
        var open = Enumerable.Range(0, weights.Length).ToList();
        var space = Math.Max(0, available - gaps - pixels);
        while (open.Count > 0)
        {
            var share = space / open.Sum(i => weights[i]);
            var under = open.Where(i => weights[i] * share < minimums[i]).ToList();
            if (under.Count == 0)
            {
                foreach (var i in open)
                {
                    widths[i] = weights[i] * share;
                }
                break;
            }
            foreach (var i in under)
            {
                widths[i] = minimums[i];
                space = Math.Max(0, space - minimums[i]);
                open.Remove(i);
            }
        }
        return widths;
    }

    [Theory]
    [InlineData("default")]
    [InlineData("commander-compact")]
    public void With_nothing_set_the_columns_are_the_theme_s_split_as_the_grid_makes_it(string theme)
    {
        var metrics = theme == "default" ? Default : Compact;
        var layout = new ColumnLayout(metrics, null);

        // The grids keep today's definitions: the three weights, Name's minimum and Size's pixels.
        var sizes = layout.GridSizes();
        Assert.Equal(new ColumnSize(metrics.NameColumnWeight, true), sizes.Name);
        Assert.Equal(metrics.NameColumnMinWidth, sizes.NameMin);
        Assert.Equal(new ColumnSize(metrics.ModifiedColumnWeight, true), sizes.Modified);
        Assert.Equal(new ColumnSize(metrics.TypeColumnWeight, true), sizes.Type);
        Assert.Equal(new ColumnSize(metrics.SizeColumnWidth, false), sizes.Size);

        // And in numbers the same split, at every width, Name's minimum included (under about 300 px in the default look).
        for (var available = 60.0; available <= 1800; available += 7.25)
        {
            var split = layout.Resolve(available);
            var stars = GridStars(available, 3 * metrics.ColumnGap, metrics.SizeColumnWidth,
                [metrics.NameColumnWeight, metrics.ModifiedColumnWeight, metrics.TypeColumnWeight], [metrics.NameColumnMinWidth, 0, 0]);
            Assert.Equal(stars[0], split.Name, 9);
            Assert.Equal(stars[1], split.Modified, 9);
            Assert.Equal(stars[2], split.Type, 9);
            Assert.Equal(metrics.SizeColumnWidth, split.Size);
        }
        Assert.Equal(new ColumnSplit(234, 128.7, 105.3, 64), Rounded(new ColumnLayout(Default, null).Resolve(Available)));
    }

    [Fact]
    public void The_user_s_widths_win_over_the_weights_and_Name_takes_the_rest_but_never_under_its_minimum()
    {
        var layout = new ColumnLayout(Default, new ColumnWidths(150, 100, 70));
        var sizes = layout.GridSizes();
        Assert.Equal(new ColumnSize(1, true), sizes.Name);
        Assert.Equal(120, sizes.NameMin);
        Assert.Equal((new ColumnSize(150, false), new ColumnSize(100, false), new ColumnSize(70, false)), (sizes.Modified, sizes.Type, sizes.Size));
        Assert.Equal(new ColumnSplit(212, 150, 100, 70), layout.Resolve(Available));
        Assert.Equal(new ColumnSplit(120, 150, 100, 70), layout.Resolve(300));
        // Commander Compact spaces the columns by 8 px: three gaps less for Name.
        Assert.Equal(new ColumnSplit(188, 150, 100, 70), new ColumnLayout(Compact, layout.User).Resolve(Available));
    }

    [Fact]
    public void Each_grip_moves_its_divider_with_the_pointer_and_only_the_two_columns_beside_it()
    {
        var layout = new ColumnLayout(Default, null);
        var start = layout.Resolve(Available);
        var (m, t, s) = (Math.Round(start.Modified), Math.Round(start.Type), Math.Round(start.Size));

        // Name|Modified: Modified's right edge stays; right makes Name wider, left Modified.
        Assert.Equal(new ColumnWidths(m - 30, t, s), layout.Drag(ColumnDivider.NameModified, start, 30));
        Assert.Equal(new ColumnWidths(m + 30, t, s), layout.Drag(ColumnDivider.NameModified, start, -30));
        // Modified|Type: Modified's left edge stays, the divider follows: Modified wider, Type narrower.
        Assert.Equal(new ColumnWidths(m + 40, t - 40, s), layout.Drag(ColumnDivider.ModifiedType, start, 40));
        Assert.Equal(new ColumnWidths(m - 20, t + 20, s), layout.Drag(ColumnDivider.ModifiedType, start, -20));
        // Type|Size: Type's left edge stays; Size keeps the pane's edge.
        Assert.Equal(new ColumnWidths(m, t + 10, s - 10), layout.Drag(ColumnDivider.TypeSize, start, 10));
        Assert.Equal(new ColumnWidths(m, t - 25, s + 25), layout.Drag(ColumnDivider.TypeSize, start, -25));

        // Nothing but the dragged divider moves: the Name column changes only with the first grip.
        foreach (var divider in new[] { ColumnDivider.ModifiedType, ColumnDivider.TypeSize })
        {
            var after = new ColumnLayout(Default, layout.Drag(divider, start, 17.4)).Resolve(Available);
            Assert.Equal(Math.Round(start.Modified) + Math.Round(start.Type) + Math.Round(start.Size), after.Modified + after.Type + after.Size);
        }
        // Whole pixels, as the setting keeps them.
        Assert.Equal(new ColumnWidths(m + 17, t - 17, s), layout.Drag(ColumnDivider.ModifiedType, start, 17.4));
    }

    [Fact]
    public void A_drag_stops_at_40_px_for_Modified_Type_and_Size_and_at_Name_s_minimum()
    {
        var layout = new ColumnLayout(Default, new ColumnWidths(150, 100, 70));
        var start = layout.Resolve(Available);
        Assert.Equal(212, start.Name);

        Assert.Equal(new ColumnWidths(40, 100, 70), layout.Drag(ColumnDivider.NameModified, start, 500));
        // Left, Name gives up its 92 px above the minimum and no more.
        Assert.Equal(new ColumnWidths(242, 100, 70), layout.Drag(ColumnDivider.NameModified, start, -500));
        Assert.Equal(120, new ColumnLayout(Default, layout.Drag(ColumnDivider.NameModified, start, -500)).Resolve(Available).Name);
        Assert.Equal(new ColumnWidths(210, 40, 70), layout.Drag(ColumnDivider.ModifiedType, start, 500));
        Assert.Equal(new ColumnWidths(40, 210, 70), layout.Drag(ColumnDivider.ModifiedType, start, -500));
        Assert.Equal(new ColumnWidths(150, 130, 40), layout.Drag(ColumnDivider.TypeSize, start, 500));
        Assert.Equal(new ColumnWidths(150, 40, 130), layout.Drag(ColumnDivider.TypeSize, start, -500));

        // A column narrower than 40 (a hand edit may make it 24) keeps what it has; a drag never makes it jump.
        var narrow = new ColumnLayout(Default, new ColumnWidths(150, 30, 70));
        var narrowStart = narrow.Resolve(Available);
        Assert.Equal(new ColumnWidths(150, 30, 70), narrow.Drag(ColumnDivider.ModifiedType, narrowStart, 10));
        Assert.Equal(new ColumnWidths(145, 35, 70), narrow.Drag(ColumnDivider.ModifiedType, narrowStart, -5));

        // Name already at its minimum (a narrow pane): the first grip cannot go left at all.
        var full = new ColumnLayout(Default, new ColumnWidths(150, 100, 70)).Resolve(300);
        Assert.Equal(new ColumnWidths(150, 100, 70), layout.Drag(ColumnDivider.NameModified, full, -30));

        // Commander Compact's Name has no minimum: it can go to nothing.
        var compact = new ColumnLayout(Compact, new ColumnWidths(150, 100, 70));
        var compactStart = compact.Resolve(Available);
        Assert.Equal(new ColumnWidths(150 + compactStart.Name, 100, 70), compact.Drag(ColumnDivider.NameModified, compactStart, -1000));
    }

    [Fact]
    public void A_fit_is_the_widest_text_on_screen_with_its_cell_s_room_or_the_heading_and_never_under_40_px()
    {
        Assert.Equal(97, new ColumnMeasure(88.3, 8, 40).Width);
        Assert.Equal(96, new ColumnMeasure(88, 8, 40).Width);
        // The heading with its chevron is wider than every text: the heading.
        Assert.Equal(59, new ColumnMeasure(30, 8, 58.2).Width);
        // Nothing on screen, a short heading: 40.
        Assert.Equal(40, new ColumnMeasure(0, 8, 31).Width);
        Assert.Equal(2000, new ColumnMeasure(5000, 8, 31).Width);

        var layout = new ColumnLayout(Default, null);
        var current = layout.Resolve(Available);
        var (m, t, s) = (Math.Round(current.Modified), Math.Round(current.Type), Math.Round(current.Size));
        Assert.Equal(new ColumnWidths(m, 97, s), layout.Fit(current, Available, ListColumn.Type, new ColumnMeasure(88.3, 8, 40)));
        Assert.Equal(new ColumnWidths(112, t, s), layout.Fit(current, Available, ListColumn.Modified, new ColumnMeasure(104, 8, 60)));
        Assert.Equal(new ColumnWidths(m, t, 52), layout.Fit(current, Available, ListColumn.Size, new ColumnMeasure(51.5, 0, 38)));

        // A fit leaves Name its minimum: a 400 px Type gets what is left.
        Assert.Equal(new ColumnWidths(m, Available - 120 - m - s, s), layout.Fit(current, Available, ListColumn.Type, new ColumnMeasure(400, 8, 40)));

        // Name's heading fits all three.
        var all = layout.FitAll(Available, new ColumnMeasure(104, 8, 60), new ColumnMeasure(88.3, 8, 40), new ColumnMeasure(51.5, 0, 38));
        Assert.Equal(new ColumnWidths(112, 97, 52), all);
        Assert.Equal(271, new ColumnLayout(Default, all).Resolve(Available).Name);
        // Three that do not leave Name its minimum give up the same share each, down to 40.
        var tight = layout.FitAll(300, new ColumnMeasure(200, 8, 60), new ColumnMeasure(150, 8, 40), new ColumnMeasure(40, 0, 38));
        Assert.True(tight.Modified + tight.Type + tight.Size <= 300 - 120, tight.ToString());
        Assert.Equal(40, tight.Size);
        Assert.True(tight.Modified > tight.Type && tight.Type > 40, tight.ToString());
        Assert.Equal(new ColumnWidths(40, 40, 40), layout.FitAll(100, new ColumnMeasure(200, 8, 60), new ColumnMeasure(150, 8, 40), new ColumnMeasure(90, 0, 38)));
    }

    [Fact]
    public void Ui_columns_is_three_whole_pixels_or_null_and_the_settings_read_it()
    {
        Assert.Equal("""{"modified":150,"type":97,"size":70}""", new ColumnLayout(Default, new ColumnWidths(150.4, 96.6, 70)).ToJson().GetRawText());
        Assert.Equal("null", new ColumnLayout(Default, null).ToJson().GetRawText());

        using var config = JsonDocument.Parse("""{"version":1,"ui":{"columns":{"modified":150,"type":97,"size":70}}}""");
        Assert.Equal(new ColumnWidths(150, 97, 70), ColumnLayout.FromConfig(config.RootElement));
        Assert.Equal(new ColumnWidths(150, 97, 70), UiSettings.FromConfig(config.RootElement).Columns);
        Assert.True(Schemas.Config.Evaluate(config.RootElement).IsValid);
        // Equal readings compare equal, so a configuration read again changes nothing.
        Assert.Equal(UiSettings.FromConfig(config.RootElement), UiSettings.FromConfig(config.RootElement));

        foreach (var text in new[]
        {
            """{"version":1,"ui":{"columns":null}}""",
            """{"version":1,"ui":{}}""",
        })
        {
            using var none = JsonDocument.Parse(text);
            Assert.Null(UiSettings.FromConfig(none.RootElement).Columns);
            Assert.True(Schemas.Config.Evaluate(none.RootElement).IsValid, text);
        }
        foreach (var text in new[]
        {
            """{"version":1,"ui":{"columns":{"modified":150,"type":97}}}""",
            """{"version":1,"ui":{"columns":{"modified":150,"type":"wide","size":70}}}""",
            """{"version":1,"ui":{"columns":[150,97,70]}}""",
        })
        {
            using var bad = JsonDocument.Parse(text);
            Assert.Null(UiSettings.FromConfig(bad.RootElement).Columns);
            Assert.False(Schemas.Config.Evaluate(bad.RootElement).IsValid, text);
        }
        // The schema holds the core's bounds, so an editor warns before the core refuses.
        using var small = JsonDocument.Parse("""{"version":1,"ui":{"columns":{"modified":23,"type":97,"size":70}}}""");
        Assert.False(Schemas.Config.Evaluate(small.RootElement).IsValid);
    }

    [Fact]
    public void A_theme_change_keeps_the_user_s_widths_and_the_theme_s_gap_and_minimum_still_apply()
    {
        var user = new ColumnWidths(150, 100, 70);
        var before = new ColumnLayout(Default, user).Resolve(Available);
        var after = new ColumnLayout(Compact, user).Resolve(Available);
        Assert.Equal((150.0, 100.0, 70.0), (after.Modified, after.Type, after.Size));
        Assert.Equal(before.Name - (3 * Compact.ColumnGap), after.Name);
        Assert.Equal(new ColumnGridSizes(new(1, true), 0, new(150, false), new(100, false), new(70, false)), new ColumnLayout(Compact, user).GridSizes());
        // Back to the default look with the same widths.
        Assert.Equal(before, new ColumnLayout(Default, user).Resolve(Available));
    }

    private static ColumnSplit Rounded(ColumnSplit split) =>
        new(Math.Round(split.Name, 1), Math.Round(split.Modified, 1), Math.Round(split.Type, 1), Math.Round(split.Size, 1));
}
