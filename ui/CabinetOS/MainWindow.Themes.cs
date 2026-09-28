using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
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
        _themes.Applied += _ =>
        {
            // What paints outside the brushes: the terminal page, the caption buttons, and
            // the level dots that open views drew with the colours of the theme before.
            SendTerminalTheme();
            UpdateCaptionColors();
            PluginsView.Repaint();
            ReviewView.Repaint();
            MarketView.Repaint();
        };
        _picker = new ThemePickerModel(_session);
        ThemesView.Model = _picker;
        ThemesView.RunCommand = (id, args, trigger) => _router.ExecuteAsync(id, args, trigger);
        ThemesView.SystemAccent = () =>
        {
            var light = _themes.Current?.IsLight == true;
            var shades = _themes.SystemAccent(light);
            return light ? shades.Dark1 : shades.Light2;
        };
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
        ThemesView.Open();
        await _picker.LoadAsync(_themes.Theme?.Id);
    }

    private async Task ApplyChosenThemeAsync(CommandInvocation invocation)
    {
        var index = CommandArgs.Number(invocation.Args, "index") is { } chosen ? (int)chosen : (int?)null;
        if (await _picker.ApplyAsync(index, invocation.RequestId))
        {
            // theme_changed follows and repaints the window; the picker's job is done.
            CloseThemePicker();
        }
    }

    private void CloseThemePicker()
    {
        ThemesView.Close();
        FocusActivePane();
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
