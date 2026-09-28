using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CabinetOS.Views;

/// <summary>
/// Closes the tooltips on screen. An overlay that collapses while the mouse
/// rests on one of its buttons would leave that button's tooltip behind (the
/// palette's "Change keybinding (F2)", live check 2026-09-28): WinUI closes a
/// tooltip when the pointer leaves, and a collapsed button is never left.
/// </summary>
internal static class OpenToolTips
{
    public static void Close(XamlRoot? root)
    {
        if (root is null)
        {
            return;
        }
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
        {
            if (popup.Child is ToolTip tip)
            {
                tip.IsOpen = false;
            }
        }
    }
}
