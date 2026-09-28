using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CabinetOS.Views;

/// <summary>Looks up the design tokens of App.xaml from code, for the app's current theme.</summary>
internal static class ThemeResources
{
    /// <summary>The brush <paramref name="key"/> for the current theme; transparent when missing.</summary>
    public static Brush Brush(string key) => Get(key) as Brush ?? new SolidColorBrush(Colors.Transparent);

    /// <summary>The resource <paramref name="key"/>: the app's dictionary first, then its theme dictionary.</summary>
    public static object? Get(string key)
    {
        var resources = Application.Current.Resources;
        if (resources.TryGetValue(key, out var value))
        {
            return value;
        }
        var theme = Application.Current.RequestedTheme == ApplicationTheme.Light ? "Light" : "Default";
        return resources.ThemeDictionaries.TryGetValue(theme, out var dictionary)
            && dictionary is ResourceDictionary themed
            && themed.TryGetValue(key, out var themedValue)
                ? themedValue
                : null;
    }
}
