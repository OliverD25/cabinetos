using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Themes;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// The metrics mapper (docs/ui.md, "Metrics and chrome"): every name of the
/// theme schema with its bounds and the default look's value, Commander
/// Compact's values, and the chrome switches.
/// </summary>
public partial class MetricsTests
{
    private static readonly AccentShades DesignAccent = ThemeMapper.Shades(new Argb(0xFF, 0x60, 0xCD, 0xFF), light: false);

    private static ColorTheme Shipped(string id) =>
        JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(Repo.Root, "sdk", "themes", id + ".json")), ProtocolJson.Default.ColorTheme)!;

    // "... Whole pixels from 0 to 64; left out, 8." in the schema's description of each metric.
    [GeneratedRegex(@"left out, (?<value>\d+(\.\d+)?)\.$")]
    private static partial Regex LeftOut();

    [Fact]
    public void Every_metric_of_the_theme_schema_is_mapped_with_its_bounds_and_the_default_look_when_absent()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo.Root, "sdk", "themes", "theme.schema.json")));
        var properties = schema.RootElement.GetProperty("$defs").GetProperty("Metrics").GetProperty("properties");
        var names = properties.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(93, names.Count);
        Assert.Equal(names.Order(StringComparer.Ordinal), MetricsMapper.Specs.Select(s => s.Name).Order(StringComparer.Ordinal));

        var defaults = MetricsMapper.Map(null);
        foreach (var spec in MetricsMapper.Specs)
        {
            var property = properties.GetProperty(spec.Name);
            Assert.Equal((spec.Name, property.GetProperty("minimum").GetDouble()), (spec.Name, spec.Min));
            Assert.Equal((spec.Name, property.GetProperty("maximum").GetDouble()), (spec.Name, spec.Max));
            Assert.Equal((spec.Name, property.GetProperty("type").GetString() == "integer"), (spec.Name, spec.Unit == MetricUnit.Pixels));
            var leftOut = LeftOut().Match(property.GetProperty("description").GetString()!);
            Assert.True(leftOut.Success, $"{spec.Name}: the schema names no default");
            Assert.Equal((spec.Name, double.Parse(leftOut.Groups["value"].Value, CultureInfo.InvariantCulture)), (spec.Name, spec.Default));
            // The default look's value when the theme leaves it out.
            Assert.Equal((spec.Name, spec.Default), (spec.Name, defaults[spec.Name]));
            Assert.False(defaults.IsSet(spec.Name), spec.Name);
        }
        Assert.False(defaults.IsPreset);

        // One typed property per name, so the window's code cannot ask for a metric that is not one.
        var typed = typeof(ThemeMetrics).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(double) && p.GetIndexParameters().Length == 0)
            .ToDictionary(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..], p => (double)p.GetValue(defaults)!);
        Assert.Equal(MetricsMapper.Specs.Select(s => s.Name).Order(StringComparer.Ordinal), typed.Keys.Order(StringComparer.Ordinal));
        Assert.All(MetricsMapper.Specs, spec => Assert.Equal((spec.Name, spec.Default), (spec.Name, typed[spec.Name])));
    }

    [Fact]
    public void Commander_Compact_maps_to_every_one_of_its_values_and_turns_the_chrome_on()
    {
        var compact = Shipped("commander-compact");
        Assert.NotNull(compact.Metrics);
        Assert.Equal(93, compact.Metrics.Count);

        var look = ThemeMapper.Map(compact, DesignAccent);
        var metrics = look.Metrics;
        foreach (var (name, value) in compact.Metrics)
        {
            Assert.Equal((name, value), (name, metrics[name]));
            Assert.True(metrics.IsSet(name), name);
        }
        Assert.True(metrics.IsPreset);
        Assert.Empty(metrics.Ignored);
        Assert.Equal(new ChromeLook(FkeyBar: true, RowStripes: true, Hairlines: true), look.Chrome);
        // v2 of the shell redesign (SHELL_REDESIGN.md §1 and §2, the bracketed values): the top row, the sidebar's
        // workspace row, the pane's three rows and its tabs.
        Assert.Equal((32.0, 26.0, 22.0, 26.0), (metrics.TopRowHeight, metrics.TopRowButtonSize, metrics.QuickOpenChipHeight, metrics.WorkspaceHeaderHeight));
        Assert.Equal((28.0, 24.0, 20.0, 20.0), (metrics.TabRow, metrics.ToolbarRowHeight, metrics.PathRowHeight, metrics.NavButtonSize));
        Assert.Equal((6.0, 170.0), (metrics.TabRadius, metrics.TabMaxWidth));

        // The handout's numbers, through the typed names the window uses.
        Assert.Equal((12.0, 1.3, 20.0, 24.0, 0.0, 0.0), (metrics.FontSize, metrics.LineHeight, metrics.RowHeight, metrics.PaneHeaderHeight, metrics.Gap, metrics.RadiusSurface));
        Assert.Equal((1.6, 0.9, 0.7, 60.0, 8.0), (metrics.NameColumnWeight, metrics.ModifiedColumnWeight, metrics.TypeColumnWeight, metrics.SizeColumnWidth, metrics.ColumnGap));
        Assert.Equal(22.0, metrics.PinnedRowHeight());
        // The column view's columns (ADR 0016): narrower in the dense look.
        Assert.Equal(180.0, metrics.ColumnViewWidth);
        // clamp(150px, 17%, 190px) and clamp(100px, 26%, 200px).
        Assert.Equal(924 * 0.17, metrics.SidebarWidth(924), 6);
        Assert.Equal((150.0, 190.0), (metrics.SidebarWidth(700), metrics.SidebarWidth(1600)));
        Assert.Equal((100.0, 130.0, 200.0), (metrics.BottomDockHeight(300), metrics.BottomDockHeight(500), metrics.BottomDockHeight(1000)));
        // backdropOpacity .94 without a tint of its own: Mica's dark base laid over Mica.
        Assert.Equal(new MicaLook(new Argb(0xFF, 0x20, 0x20, 0x20), 0.914), look.Mica);
    }

    [Fact]
    public void The_default_theme_keeps_the_default_look_and_switching_back_restores_every_size()
    {
        var compact = ThemeMapper.Map(Shipped("commander-compact"), DesignAccent);
        var back = ThemeMapper.Map(Shipped("default"), DesignAccent);

        Assert.NotEqual(compact.Metrics, back.Metrics);
        Assert.Equal(MetricsMapper.Default, back.Metrics);
        Assert.Equal(ChromeLook.None, back.Chrome);
        Assert.Null(back.Mica);
        Assert.Equal((30.0, 13.0, 32.0), (back.Metrics.RowHeight, back.Metrics.FontSize, back.Metrics.PinnedRowHeight()));
        Assert.Equal(220.0, back.Metrics.ColumnViewWidth);
        // v2 of the shell redesign's default values: the pane's three rows, the sidebar's workspace row, the chip, the tabs.
        Assert.Equal((36.0, 28.0, 24.0, 28.0, 24.0), (back.Metrics.TabRow, back.Metrics.ToolbarRowHeight, back.Metrics.PathRowHeight, back.Metrics.WorkspaceHeaderHeight, back.Metrics.QuickOpenChipHeight));
        Assert.Equal((8.0, 160.0), (back.Metrics.TabRadius, back.Metrics.TabMaxWidth));
        // The design's clamp(180px, 20%, 224px) and clamp(120px, 30%, 240px).
        Assert.Equal((180.0, 200.0, 224.0), (back.Metrics.SidebarWidth(800), back.Metrics.SidebarWidth(1000), back.Metrics.SidebarWidth(2000)));
        Assert.Equal((120.0, 150.0, 240.0), (back.Metrics.BottomDockHeight(300), back.Metrics.BottomDockHeight(500), back.Metrics.BottomDockHeight(1000)));
    }

    [Fact]
    public void A_theme_that_sets_some_metrics_keeps_the_default_look_for_the_rest()
    {
        var metrics = MetricsMapper.Map(new Dictionary<string, double> { ["rowHeight"] = 22, ["sidebarRowHeight"] = 30 });

        Assert.Equal((22.0, true), (metrics.RowHeight, metrics.IsSet("rowHeight")));
        Assert.Equal((13.0, false), (metrics.FontSize, metrics.IsSet("fontSize")));
        Assert.Equal(30.0, metrics.PinnedRowHeight());
        Assert.True(metrics.IsPreset);
    }

    [Fact]
    public void Values_are_held_to_their_bounds_pixels_are_whole_and_unknown_names_change_nothing()
    {
        var metrics = MetricsMapper.Map(new Dictionary<string, double>
        {
            ["rowHeight"] = 10,
            ["paneHeaderHeight"] = 20.5,
            ["lineHeight"] = 3,
            ["typeColumnWeight"] = 0.75,
            ["rowHight"] = 20,
        });

        Assert.Equal((14.0, 21.0, 2.5, 0.75), (metrics.RowHeight, metrics.PaneHeaderHeight, metrics.LineHeight, metrics.TypeColumnWeight));
        Assert.Equal(["rowHight"], metrics.Ignored);
        Assert.Throws<ArgumentException>(() => metrics["rowHight"]);

        // A lower limit above its upper limit (the core refuses it) still gives one width.
        var crossed = MetricsMapper.Map(new Dictionary<string, double> { ["sidebarMinWidth"] = 300, ["sidebarMaxWidth"] = 200 });
        Assert.Equal((300.0, 300.0), (crossed.SidebarWidth(500), crossed.SidebarWidth(5000)));
    }

    [Fact]
    public void Each_chrome_element_is_off_unless_the_theme_turns_it_on()
    {
        Assert.Equal(ChromeLook.None, ChromeLook.From(null));
        Assert.Equal(new ChromeLook(true, false, false), ChromeLook.From(new ThemeChrome(FkeyBar: true)));
        Assert.Equal(new ChromeLook(false, true, false), ChromeLook.From(new ThemeChrome(RowStripes: true, Hairlines: false)));
    }

    [Fact]
    public void Backdrop_opacity_above_plain_mica_lays_mica_base_over_it_in_either_mode()
    {
        Assert.Null(ThemeMapper.DenserMica(0.86, light: false));
        Assert.Null(ThemeMapper.DenserMica(0.5, light: true));
        Assert.Equal(new MicaLook(new Argb(0xFF, 0xF3, 0xF3, 0xF3), 0.786), ThemeMapper.DenserMica(0.94, light: true));
        Assert.Equal(new MicaLook(new Argb(0xFF, 0x20, 0x20, 0x20), 1.0), ThemeMapper.DenserMica(1, light: false));

        // A theme's own tint wins: backdropOpacity does not change it.
        var nord = Shipped("nord") with { Metrics = new Dictionary<string, double> { ["backdropOpacity"] = 1 } };
        Assert.Equal(new MicaLook(new Argb(0xFF, 0x2E, 0x34, 0x40), 0.88), ThemeMapper.Map(nord, DesignAccent).Mica);
    }
}
