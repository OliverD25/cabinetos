using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Themes;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>The theme mapper: every token from the palette, the default theme equal to the design tokens, accents, Mica, the terminal.</summary>
public class ThemeTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The Windows default blue's shades, with the design's #60CDFF as Light2.</summary>
    private static readonly AccentShades DesignAccent = ThemeMapper.Shades(new Argb(0xFF, 0x60, 0xCD, 0xFF), light: false);

    internal static ColorTheme Shipped(string id) =>
        JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(Repo.Root, "sdk", "themes", id + ".json")), ProtocolJson.Default.ColorTheme)!;

    [Fact]
    public void The_default_theme_maps_to_exactly_the_design_tokens_in_App_xaml()
    {
        var look = ThemeMapper.Map(Shipped("default"), DesignAccent);
        var resources = XDocument.Load(Path.Combine(Repo.Root, "ui", "CabinetOS", "App.xaml"))
            .Descendants()
            .Where(e => ((string?)e.Attribute(X + "Key"))?.StartsWith("Cb", StringComparison.Ordinal) == true)
            .ToDictionary(e => (string)e.Attribute(X + "Key")!);
        var unthemed = new HashSet<string> { "CbGraphFillBrush", "CbDangerHoverFillBrush", "CbScrimBrush" };

        foreach (var (key, color) in look.Brushes)
        {
            Assert.True(resources.TryGetValue(key, out var element), $"{key} is not in App.xaml");
            Assert.Equal(Xaml + "SolidColorBrush", element.Name);
            var written = (string)element.Attribute("Color")!;
            if (!written.StartsWith('{'))
            {
                Assert.Equal((key, written), (key, color.ToString()));
            }
        }
        foreach (var (key, acrylic) in look.Acrylics)
        {
            var element = resources[key];
            Assert.Equal((key, (string)element.Attribute("TintColor")!), (key, acrylic.Tint.ToString()));
            Assert.Equal((key, (string)element.Attribute("FallbackColor")!), (key, acrylic.Fallback.ToString()));
            Assert.Equal(double.Parse((string)element.Attribute("TintOpacity")!, CultureInfo.InvariantCulture), acrylic.TintOpacity, 3);
            Assert.Equal(double.Parse((string)element.Attribute("TintLuminosityOpacity")!, CultureInfo.InvariantCulture), acrylic.LuminosityOpacity, 2);
        }
        foreach (var (key, gradient) in look.Gradients)
        {
            var stops = resources[key].Descendants(Xaml + "GradientStop").Select(s => (string)s.Attribute("Color")!).ToList();
            Assert.Equal(2, stops.Count);
            foreach (var (written, mapped) in stops.Zip([gradient.First, gradient.Second]))
            {
                if (!written.StartsWith('{'))
                {
                    Assert.Equal((key, written), (key, mapped.ToString()));
                }
            }
        }
        // Every themable token of App.xaml is set by the theme; the rest are the few meant to stay.
        var themed = look.Brushes.Keys.Concat(look.Acrylics.Keys).Concat(look.Gradients.Keys).ToHashSet();
        Assert.All(resources.Where(r => r.Value.Name.LocalName.EndsWith("Brush", StringComparison.Ordinal)),
            r => Assert.True(themed.Contains(r.Key) || unthemed.Contains(r.Key), $"{r.Key} is in App.xaml but no theme sets it"));
    }

    [Fact]
    public void Every_palette_key_lands_on_a_brush()
    {
        var nord = Shipped("nord");
        var baseline = Flatten(ThemeMapper.Map(nord, DesignAccent));
        var palette = JsonSerializer.SerializeToNode(nord.Palette, ProtocolJson.Default.ThemePalette)!.AsObject();
        var keys = palette.Select(p => p.Key).Where(k => k != "fileTypeColors")
            .Concat(palette["fileTypeColors"]!.AsObject().Select(p => $"fileTypeColors.{p.Key}"))
            .ToList();
        Assert.Equal(16 + 8, keys.Count);

        foreach (var key in keys)
        {
            var changed = JsonNode.Parse(palette.ToJsonString())!.AsObject();
            if (key.StartsWith("fileTypeColors.", StringComparison.Ordinal))
            {
                changed["fileTypeColors"]![key["fileTypeColors.".Length..]] = "#123457";
            }
            else
            {
                changed[key] = "#123457";
            }
            var theme = nord with { Palette = changed.Deserialize(ProtocolJson.Default.ThemePalette)! };
            Assert.NotEqual(baseline, Flatten(ThemeMapper.Map(theme, DesignAccent)));
        }
    }

    [Fact]
    public void A_null_accent_follows_the_Windows_accent_and_a_theme_accent_sets_every_shade()
    {
        var system = ThemeMapper.Shades(new Argb(0xFF, 0x00, 0x78, 0xD4), light: false);
        var followed = ThemeMapper.Map(Shipped("default"), system);
        Assert.True(followed.FollowsSystemAccent);
        Assert.Same(system, followed.AccentShades);
        Assert.Equal(system.Light2, followed.Accent);
        Assert.Equal(system.Light2, followed.Brushes["CbAccentBrush"]);

        var nord = ThemeMapper.Map(Shipped("nord"), system);
        Assert.False(nord.FollowsSystemAccent);
        Assert.Equal("#FF88C0D0", nord.Accent.ToString());
        Assert.Equal(nord.Accent, nord.AccentShades.Light2);
        Assert.Equal("#2688C0D0", nord.Brushes["CbPluginBadgeFillBrush"].ToString());
        Assert.Equal(nord.Accent, nord.Gradients["CbPaletteInputAccentBrush"].First);
        // The shades step away from the accent: lighter above Light2, darker below.
        Assert.True(Luma(nord.AccentShades.Light3) > Luma(nord.AccentShades.Light2));
        Assert.True(Luma(nord.AccentShades.Light2) > Luma(nord.AccentShades.Base));
        Assert.True(Luma(nord.AccentShades.Dark1) > Luma(nord.AccentShades.Dark3));
    }

    [Fact]
    public void A_light_theme_draws_WinUI_in_light_mode_with_Dark1_as_the_accent()
    {
        var nord = Shipped("nord");
        var light = ThemeMapper.Map(nord with { Kind = ColorTheme.Light }, DesignAccent);
        Assert.True(light.IsLight);
        Assert.Equal(light.AccentShades.Dark1, light.Accent);
        Assert.Equal("#FF88C0D0", light.Accent.ToString());
        Assert.True(Luma(light.AccentShades.Light1) > Luma(light.AccentShades.Dark1));

        var systemLight = ThemeMapper.Shades(new Argb(0xFF, 0x00, 0x5F, 0xB8), light: true);
        var followed = ThemeMapper.Map(Shipped("default") with { Kind = ColorTheme.Light }, systemLight);
        Assert.Equal(systemLight.Dark1, followed.Accent);
        Assert.False(ThemeMapper.Map(nord, DesignAccent).IsLight);
    }

    [Fact]
    public void A_system_theme_takes_the_Windows_mode_and_its_own_colours_only_in_dark_mode()
    {
        var shipped = Shipped("default");
        Assert.Equal((ColorTheme.System, true, false), (shipped.Kind, shipped.FollowsSystemMode, shipped.IsLight));

        var dark = ThemeMapper.Map(shipped, DesignAccent, systemIsLight: false);
        Assert.Equal((false, true), (dark.IsLight, dark.FollowsSystemMode));
        Assert.Equal(Flatten(ThemeMapper.Map(shipped with { Kind = ColorTheme.Dark }, DesignAccent)), Flatten(dark));

        // Light mode: the window's light colours, the Windows accent's light-mode shade, the theme's (plain) Mica.
        var systemLight = ThemeMapper.Shades(new Argb(0xFF, 0x00, 0x5F, 0xB8), light: true);
        var light = ThemeMapper.Map(shipped, systemLight, systemIsLight: true);
        Assert.Equal((true, true), (light.IsLight, light.FollowsSystemMode));
        Assert.Equal("#FF1B1B1B", light.Brushes["CbTextPrimaryBrush"].ToString());
        Assert.Equal(systemLight.Dark1, light.Accent);
        Assert.Null(light.Mica);
        Assert.Equal(("#383A42FF", "#FAFAFA00"), (light.Terminal.Foreground, light.Terminal.Background));
        // The dialog's footer and the notes sit a step darker than the dialog, as Windows' light surfaces do.
        Assert.Equal(("#FFFCFCFC", "#FFF3F3F3"), (light.Brushes["CbDialogFillBrush"].ToString(), light.Brushes["CbDialogFooterFillBrush"].ToString()));
        // Dark text on light layers.
        Assert.True(Luma(light.Brushes["CbTextPrimaryBrush"]) < 40 && Luma(light.Brushes["CbDialogFillBrush"]) > 240);

        // A theme with a mode of its own keeps it, whatever Windows is set to.
        var nord = ThemeMapper.Map(Shipped("nord"), DesignAccent, systemIsLight: true);
        Assert.Equal((false, false), (nord.IsLight, nord.FollowsSystemMode));
        Assert.Equal(Flatten(ThemeMapper.Map(Shipped("nord"), DesignAccent)), Flatten(nord));
    }

    [Fact]
    public void No_mica_is_plain_Mica_and_a_tint_keeps_its_colour_and_opacity()
    {
        Assert.Null(ThemeMapper.Map(Shipped("default"), DesignAccent).Mica);
        var mica = ThemeMapper.Map(Shipped("nord"), DesignAccent).Mica!;
        Assert.Equal(("#FF2E3440", 0.88), (mica.Tint.ToString(), mica.Opacity));
    }

    [Fact]
    public void The_terminal_gets_the_scheme_over_a_clear_background_and_16_colours()
    {
        var terminal = ThemeMapper.Map(Shipped("nord"), DesignAccent).Terminal;
        Assert.Equal("#D8DEE9FF", terminal.Foreground);
        // Alpha 0: the cells show the panel; xterm.js draws reverse video with the opaque colour.
        Assert.Equal("#2E344000", terminal.Background);
        Assert.Equal("#D8DEE9FF", terminal.Cursor);
        Assert.Equal("#88C0D04D", terminal.Selection);
        Assert.Equal(16, terminal.Ansi.Count);
        Assert.Equal(("#3B4252FF", "#ECEFF4FF"), (terminal.Ansi[0], terminal.Ansi[15]));
    }

    [Fact]
    public void The_file_types_folder_and_levels_come_from_the_palette()
    {
        var look = ThemeMapper.Map(Shipped("nord"), DesignAccent);
        Assert.Equal(8, look.FileTypes.Count);
        Assert.Equal("#FF88C0D0", look.FileTypes["md"].ToString());
        Assert.Equal("#FF88C0D0", look.FileTypes["MD"].ToString());
        Assert.Equal(("#FFEBCB8B", "#FFF2DDB4"), (look.Brushes["CbFolderBrush"].ToString(), look.Brushes["CbFolderFrontBrush"].ToString()));
        Assert.Equal(("#FFA3BE8C", "#FFEBCB8B", "#FFBF616A"), (look.Levels.Low.ToString(), look.Levels.Medium.ToString(), look.Levels.High.ToString()));
        Assert.Equal(look.Levels.High, look.Brushes["CbErrorTextBrush"]);
        Assert.Equal(look.Levels.High, look.Levels.For("high"));
    }

    [Fact]
    public void Each_shipped_theme_maps()
    {
        foreach (var id in new[] { "default", "nord", "catppuccin-mocha", "rose-pine-moon" })
        {
            var look = ThemeMapper.Map(Shipped(id), DesignAccent);
            Assert.Equal(id, look.Id);
            Assert.NotEmpty(look.Brushes);
        }
    }

    [Fact]
    public void A_colour_that_is_not_one_names_its_key()
    {
        var nord = Shipped("nord");
        var error = Assert.Throws<ThemeFormatException>(() => ThemeMapper.Map(nord with { Palette = nord.Palette with { LayerFill = "red" } }, DesignAccent));
        Assert.Equal("palette.layerFill", error.Key);
        Assert.Contains("red", error.Message);
        Assert.Equal("terminal.ansi",
            Assert.Throws<ThemeFormatException>(() => ThemeMapper.Map(nord with { Terminal = nord.Terminal with { Ansi = ["#000000"] } }, DesignAccent)).Key);
    }

    [Theory]
    [InlineData("#88C0D0", "#FF88C0D0", "#88C0D0FF")]
    [InlineData("#d8dee98b", "#8BD8DEE9", "#D8DEE98B")]
    [InlineData("#00000059", "#59000000", "#00000059")]
    public void Colours_read_as_the_theme_writes_them(string text, string xaml, string css)
    {
        Assert.True(Argb.TryParse(text, out var color));
        Assert.Equal((xaml, css), (color.ToString(), color.ToCss()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("88C0D0")]
    [InlineData("#88C0D")]
    [InlineData("#GGGGGG")]
    [InlineData("rgb(1,2,3)")]
    public void Anything_else_is_not_a_colour(string? text) => Assert.False(Argb.TryParse(text, out _));

    private static double Luma(Argb c) => (0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B);

    private static string Flatten(ThemeLook look) => string.Join(';',
        look.Brushes.OrderBy(b => b.Key).Select(b => $"{b.Key}={b.Value}")
            .Concat(look.Acrylics.OrderBy(a => a.Key).Select(a => $"{a.Key}={a.Value}"))
            .Concat(look.Gradients.OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Value}"))
            .Concat(look.FileTypes.OrderBy(f => f.Key).Select(f => $"{f.Key}={f.Value}"))
            .Append($"levels={look.Levels}")
            .Append($"terminal={look.Terminal.Foreground},{look.Terminal.Background},{string.Join(',', look.Terminal.Ansi)}"));

    [Fact]
    public void Text_on_an_accent_fill_follows_the_accents_lightness()
    {
        // The design's light blue keeps the design's near-black.
        var nearBlack = new Argb(0xFF, 0x11, 0x11, 0x11);
        Assert.Equal(nearBlack, ThemeMapper.Map(Shipped("default"), DesignAccent).Brushes["CbOnAccentBrush"]);
        Assert.Equal(nearBlack, ThemeMapper.OnAccent(new Argb(0xFF, 0x60, 0xCD, 0xFF)));

        // The dark accents of light themes get white: near-black falls below 4.5:1 on them.
        Assert.Equal(Argb.White, ThemeMapper.OnAccent(new Argb(0xFF, 0x09, 0x69, 0xDA)));
        foreach (var id in new[] { "github-light", "catppuccin-latte" })
        {
            var look = ThemeMapper.Map(Collection(id), DesignAccent);
            var onAccent = look.Brushes["CbOnAccentBrush"];
            Assert.Equal((id, Argb.White), (id, onAccent));
            Assert.True(look.Accent.ContrastWith(onAccent) >= 4.5, $"{id}: {look.Accent.ContrastWith(onAccent):F2}:1");
            Assert.True(look.Accent.ContrastWith(nearBlack) < 4.5, $"{id}: near-black would have passed");
        }
    }

    private static ColorTheme Collection(string id) =>
        JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(Repo.Root, "sdk", "themes", "collection", id + ".json")), ProtocolJson.Default.ColorTheme)!;
}
