using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CabinetOS.Views;

/// <summary>
/// Tooltips of overlays that collapse under a resting mouse. WinUI closes a
/// tooltip when the pointer leaves its element, and an element that
/// collapses under the mouse is never left: the palette's "Change
/// keybinding (F2)" stayed on screen (live check, 2026-09-28). Worse, the
/// tooltip can open after the overlay closed, from the hover delay WinUI
/// started while the element was under the mouse (the re-run of 2026-09-29).
/// </summary>
internal static class OpenToolTips
{
    /// <summary>Closes the tooltips on screen, when an overlay closes.</summary>
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

    /// <summary>
    /// Gives <paramref name="owner"/> a tooltip that closes itself when it
    /// opens for an element that is not shown any more: the hover delay can
    /// end after the element's overlay collapsed. Null text removes it.
    /// </summary>
    public static void Set(FrameworkElement owner, string? text)
    {
        if (text is null)
        {
            ToolTipService.SetToolTip(owner, null);
            return;
        }
        if (ToolTipService.GetToolTip(owner) is ToolTip existing)
        {
            existing.Content = text;
            return;
        }
        var tip = new ToolTip { Content = text };
        tip.Opened += (_, _) =>
        {
            if (!IsShown(owner))
            {
                tip.IsOpen = false;
            }
        };
        ToolTipService.SetToolTip(owner, tip);
    }

    /// <summary>Whether <paramref name="element"/> is in the window's tree with nothing above it collapsed.</summary>
    public static bool IsShown(FrameworkElement element)
    {
        if (!element.IsLoaded || element.XamlRoot is null)
        {
            return false;
        }
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: Visibility.Collapsed })
            {
                return false;
            }
        }
        return true;
    }
}
