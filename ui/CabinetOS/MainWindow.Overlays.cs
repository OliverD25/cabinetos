using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Presentation;

namespace CabinetOS;

// One overlay at a time (docs/keybindings.md, "One overlay at a time"; the keys audit's proposal P4). The palette, Quick Open,
// a prompt (the drive list too), the theme picker and the plugin list each call CloseOtherOverlays before they show, so at
// most one is open and Esc closes the one on screen. Each closes the way Esc closes it: the keyboard goes to the pane first
// (see the palette), and the new overlay takes it a moment later in the same turn.
public sealed partial class MainWindow
{
    private IReadOnlySet<Overlay> OpenOverlays()
    {
        var open = new HashSet<Overlay>();
        if (_palette.IsOpen)
        {
            open.Add(Overlay.Palette);
        }
        if (_quickOpen.IsOpen)
        {
            open.Add(Overlay.QuickOpen);
        }
        if (PromptView.IsOpen)
        {
            open.Add(Overlay.Prompt);
        }
        if (ThemesView.IsOpen)
        {
            open.Add(Overlay.ThemePicker);
        }
        if (PluginsView.IsOpen)
        {
            open.Add(Overlay.PluginList);
        }
        return open;
    }

    // Closes what is open besides `opening`: null for the marketplace, which covers them all.
    private void CloseOtherOverlays(Overlay? opening)
    {
        var closing = OverlayRule.ToClose(opening, OpenOverlays());
        if (closing.Count == 0)
        {
            return;
        }
        foreach (var overlay in closing)
        {
            switch (overlay)
            {
                case Overlay.Palette:
                    _palette.Close();
                    break;
                case Overlay.QuickOpen:
                    CloseQuickOpen(returnFocus: true);
                    break;
                case Overlay.Prompt:
                    PromptView.Cancel();
                    break;
                case Overlay.ThemePicker:
                    CloseThemePicker();
                    break;
                case Overlay.PluginList:
                    ClosePlugins();
                    break;
            }
        }
        Diag.Info(Target, "overlays closed for another", new LogField("opening", opening?.ToString() ?? "marketplace"),
            new LogField("closed", string.Join(",", closing)));
    }

    // The snapshot aid's focus: step lists them, so a test can say which overlays are open at a moment.
    private string OpenOverlayNames() => string.Join(",", OpenOverlays().Order());
}
