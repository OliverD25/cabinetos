using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Plugins;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace CabinetOS.Services;

/// <summary>
/// Applies the core's theme to the running window (docs/ui.md, "Themes").
/// Every design token is one brush object in the app's resources, and every
/// element that uses a token holds that object; so the applier changes the
/// brushes' colours in place and everything repaints at once, without a
/// restart and without walking the visual tree. WinUI's own accent brushes
/// are changed the same way. The theme's kind switches the content between
/// dark and light, the Mica tint goes to the backdrop, and the terminal gets
/// its colours through <see cref="Applied"/>.
/// </summary>
internal sealed class ThemeApplier
{
    private const string Target = "cabinetos_ui::theme";

    // WinUI's accent brushes and the shade each takes, in dark ("Default") and light mode.
    private static readonly (string Key, Func<AccentShades, Argb> Dark, Func<AccentShades, Argb> Light)[] WinUiAccent =
    [
        ("AccentFillColorDefaultBrush", s => s.Light2, s => s.Dark1),
        ("AccentFillColorSecondaryBrush", s => s.Light2.WithAlpha(0xE6), s => s.Dark1.WithAlpha(0xE6)),
        ("AccentFillColorTertiaryBrush", s => s.Light2.WithAlpha(0xCC), s => s.Dark1.WithAlpha(0xCC)),
        ("AccentFillColorSelectedTextBackgroundBrush", s => s.Base, s => s.Base),
        ("AccentTextFillColorPrimaryBrush", s => s.Light3, s => s.Dark2),
        ("AccentTextFillColorSecondaryBrush", s => s.Light3, s => s.Dark3),
        ("AccentTextFillColorTertiaryBrush", s => s.Light2, s => s.Dark1),
    ];

    private readonly FrameworkElement _root;
    private readonly TintedMicaBackdrop _backdrop;
    private readonly UISettings _system = new();
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    /// <summary>An applier for the window whose content is <paramref name="root"/>.</summary>
    public ThemeApplier(FrameworkElement root, TintedMicaBackdrop backdrop)
    {
        _root = root;
        _backdrop = backdrop;
        // The Windows accent changed: a theme that follows it is applied again.
        _system.ColorValuesChanged += (_, _) => root.DispatcherQueue.TryEnqueue(() =>
        {
            if (Theme is { } theme && Current is { FollowsSystemAccent: true })
            {
                Apply(theme);
            }
        });
    }

    /// <summary>A theme was applied: the terminal and anything else that paints outside the brushes follow.</summary>
    public event Action<ThemeLook>? Applied;

    /// <summary>The theme in effect, or null before the core sent one.</summary>
    public ColorTheme? Theme { get; private set; }

    /// <summary>What it mapped to.</summary>
    public ThemeLook? Current { get; private set; }

    /// <summary>
    /// Applies <paramref name="theme"/>. A theme with a colour that is not one
    /// is not applied, not even in part (as the core does); the log says why.
    /// </summary>
    public bool Apply(ColorTheme theme)
    {
        ThemeLook look;
        try
        {
            look = ThemeMapper.Map(theme, SystemAccent(theme.IsLight));
        }
        catch (ThemeFormatException error)
        {
            Diag.Warn(Target, "a theme was not applied", new LogField("theme", theme.Id), new LogField("error", error.Message));
            return false;
        }
        var resources = Application.Current.Resources;
        foreach (var (key, color) in look.Brushes)
        {
            if (Find(resources, key) is SolidColorBrush brush)
            {
                brush.Color = ToColor(color);
            }
        }
        foreach (var (key, acrylic) in look.Acrylics)
        {
            if (Find(resources, key) is AcrylicBrush brush)
            {
                brush.TintColor = ToColor(acrylic.Tint);
                brush.TintOpacity = acrylic.TintOpacity;
                brush.TintLuminosityOpacity = acrylic.LuminosityOpacity;
                brush.FallbackColor = ToColor(acrylic.Fallback);
            }
        }
        foreach (var (key, gradient) in look.Gradients)
        {
            if (Find(resources, key) is LinearGradientBrush { GradientStops.Count: 2 } brush)
            {
                brush.GradientStops[0].Color = ToColor(gradient.First);
                brush.GradientStops[1].Color = ToColor(gradient.Second);
            }
        }
        ApplyAccent(resources, look.AccentShades);
        ThemeBrushes.Apply(look);
        CapabilityLevels.Current = look.Levels;
        _backdrop.SetTint(look.Mica);
        // WinUI's own controls in the theme's mode; the tokens above are the same objects in both.
        _root.RequestedTheme = look.IsLight ? ElementTheme.Light : ElementTheme.Dark;

        Theme = theme;
        Current = look;
        Diag.Info(Target, "theme applied", new LogField("theme", look.Id), new LogField("kind", theme.Kind),
            new LogField("accent", look.FollowsSystemAccent ? "system" : look.Accent.ToString()),
            new LogField("mica", look.Mica is { } mica ? $"{mica.Tint} at {mica.Opacity}" : "plain"));
        Applied?.Invoke(look);
        return true;
    }

    /// <summary>The Windows accent's shades now, for a theme without an accent of its own.</summary>
    public AccentShades SystemAccent(bool light)
    {
        Argb Get(UIColorType type)
        {
            var c = _system.GetColorValue(type);
            return new Argb(0xFF, c.R, c.G, c.B);
        }
        return new AccentShades(
            Get(UIColorType.Accent),
            Get(UIColorType.AccentLight1), Get(UIColorType.AccentLight2), Get(UIColorType.AccentLight3),
            Get(UIColorType.AccentDark1), Get(UIColorType.AccentDark2), Get(UIColorType.AccentDark3));
    }

    // WinUI's accent: the SystemAccentColor* values for anything resolved from now on, and the
    // accent brushes its controls already hold, in both modes' dictionaries.
    private void ApplyAccent(ResourceDictionary resources, AccentShades shades)
    {
        resources["SystemAccentColor"] = ToColor(shades.Base);
        resources["SystemAccentColorLight1"] = ToColor(shades.Light1);
        resources["SystemAccentColorLight2"] = ToColor(shades.Light2);
        resources["SystemAccentColorLight3"] = ToColor(shades.Light3);
        resources["SystemAccentColorDark1"] = ToColor(shades.Dark1);
        resources["SystemAccentColorDark2"] = ToColor(shades.Dark2);
        resources["SystemAccentColorDark3"] = ToColor(shades.Dark3);
        foreach (var (theme, light) in new[] { ("Default", false), ("Dark", false), ("Light", true) })
        {
            foreach (var dictionary in ThemeDictionaries(resources, theme))
            {
                foreach (var (key, dark, lightShade) in WinUiAccent)
                {
                    if (dictionary.TryGetValue(key, out var value) && value is SolidColorBrush brush)
                    {
                        try
                        {
                            brush.Color = ToColor(light ? lightShade(shades) : dark(shades));
                        }
                        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
                        {
                            Report(key, error.Message);
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<ResourceDictionary> ThemeDictionaries(ResourceDictionary root, string theme)
    {
        foreach (var dictionary in Walk(root))
        {
            if (dictionary.ThemeDictionaries.TryGetValue(theme, out var value) && value is ResourceDictionary themed)
            {
                yield return themed;
            }
        }

        static IEnumerable<ResourceDictionary> Walk(ResourceDictionary dictionary)
        {
            yield return dictionary;
            foreach (var merged in dictionary.MergedDictionaries)
            {
                foreach (var inner in Walk(merged))
                {
                    yield return inner;
                }
            }
        }
    }

    private object? Find(ResourceDictionary resources, string key)
    {
        if (resources.TryGetValue(key, out var value))
        {
            return value;
        }
        Report(key, "not in App.xaml");
        return null;
    }

    private void Report(string key, string why)
    {
        if (_reported.Add(key))
        {
            Diag.Warn(Target, "a theme token could not be set", new LogField("key", key), new LogField("why", why));
        }
    }

    private static Color ToColor(Argb color) => Color.FromArgb(color.A, color.R, color.G, color.B);
}
