using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Themes;
using CabinetOS.Services;

namespace CabinetOS;

// Colour themes: the core's theme applied live (docs/ui.md, "Themes").
public sealed partial class MainWindow
{
    private ThemeApplier _themes = null!;
    private ThemePickerModel _picker = null!;
    private bool _themesUnavailable;

    private void SetUpThemes()
    {
        _themes = new ThemeApplier(RootGrid, _backdrop);
        _themes.Applied += look =>
        {
            // The sizes and chrome first (docs/ui.md, "Metrics and chrome"): the terminal page takes both.
            ApplyMetrics(look);
            // What paints outside the brushes: the terminal page, the caption buttons, and
            // the level dots that open views drew with the colours of the theme before.
            SendTerminalTheme();
            UpdateCaptionColors();
            PluginsView.Repaint();
            ReviewView.Repaint();
            MarketView.Repaint();
            ThemesView.Repaint();
        };
        _picker = new ThemePickerModel(_session);
        _picker.Preview += theme =>
        {
            if (theme is null)
            {
                _themes.EndPreview();
            }
            else
            {
                _themes.Preview(theme);
            }
        };
        ThemesView.Model = _picker;
        ThemesView.RunCommand = (id, args, trigger) => _router.ExecuteAsync(id, args, trigger);
        ThemesView.SystemAccent = () =>
        {
            var light = _themes.Current?.IsLight == true;
            var shades = _themes.SystemAccent(light);
            return light ? shades.Dark1 : shades.Light2;
        };
        ThemesView.SystemIsLight = () => _themes.SystemIsLight;
    }

    // The snapshot aid's mode: step: light or dark as if Windows were set so, or "windows" to ask Windows again.
    private void ForceSystemMode(string mode)
    {
        _themes.ModeOverride = mode switch
        {
            "light" => true,
            "dark" => false,
            _ => null,
        };
        _themes.SystemColorsChanged();
    }

    private void RegisterThemeCommands()
    {
        _router.RegisterUiHandler("preferences.selectColorTheme", _ => OpenThemePickerAsync());
        // The picker's Enter and click; not in the core's registry.
        _router.RegisterLocal("theme.apply", ApplyChosenThemeAsync);
    }

    private async Task OpenThemePickerAsync()
    {
        FileMenu.Close();
        if (GalleryView.IsOpen)
        {
            // The picker's preview and the gallery's are one screen's colours: the gallery ends first.
            CloseGallery(restore: true, focusPane: false);
        }
        CloseOtherOverlays(Overlay.ThemePicker);
        _picker.BeginPreviews();
        ThemesView.Open();
        await _picker.LoadAsync(_themes.Theme?.Id);
        // What the picker lists, for the window tests (a theme installed from the gallery is in it).
        Diag.Info("cabinetos_ui::theme", "theme picker listed", new LogField("themes", _picker.Rows.Count),
            new LogField("ids", string.Join(",", _picker.Rows.Select(row => row.Info.Id))));
    }

    private async Task ApplyChosenThemeAsync(CommandInvocation invocation)
    {
        var index = CommandArgs.Number(invocation.Args, "index") is { } chosen ? (int)chosen : (int?)null;
        if (await _picker.ApplyAsync(index, invocation.RequestId))
        {
            // theme_changed follows and makes the preview on screen the theme in effect: painting
            // the old theme back in between would flash it.
            CloseThemePicker(restore: false);
        }
    }

    private void CloseThemePicker(bool restore = true)
    {
        // The pane takes the keyboard before the picker collapses (see the palette).
        FocusActivePane();
        HideThemePicker(restore);
    }

    // Every way the picker goes away ends its previews here: restore paints the theme in effect again.
    private void HideThemePicker(bool restore)
    {
        // Closed first, so the repaint of the restore does not build the picker's rows again.
        ThemesView.Close();
        _picker.EndPreviews(restore);
    }

    // The snapshot aid's stand-in for Mica, which is not part of the window's content:
    // dark Mica's base (or light's), with the theme's tint laid over it.
    private Windows.UI.Color SnapshotBackdrop()
    {
        var look = _themes.Current;
        var base_ = look is { IsLight: true } ? new Argb(0xFF, 0xF3, 0xF3, 0xF3) : new Argb(0xFF, 0x20, 0x20, 0x20);
        var color = look?.Mica is { } mica ? base_.Mix(mica.Tint, mica.Opacity) : base_;
        return Windows.UI.Color.FromArgb(0xFF, color.R, color.G, color.B);
    }

    // At start and after the core restarted: the theme in effect.
    private async Task ReadThemeAsync()
    {
        if (_themesUnavailable)
        {
            return;
        }
        switch (await _session.RequestAsync(new GetThemeRequest()))
        {
            case ThemeReply reply:
                _themes.Apply(reply.Theme);
                break;
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                // A core before protocol 10: the design's tokens stay.
                _themesUnavailable = true;
                Diag.Info(Target, "the core has no themes yet (get_theme); the design's colours stay");
                break;
            case ErrorReply error:
                Diag.Info(Target, "get_theme failed", new LogField("code", error.Code), new LogField("error", error.Message));
                break;
        }
    }
}
