using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Themes;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CabinetOS.Services;

/// <summary>
/// Mica with a theme's tint (docs/themes.md, <c>mica</c>). WinUI's
/// <c>MicaBackdrop</c> has no tint, so this backdrop drives a
/// <c>MicaController</c> itself: a null tint is plain Mica (the controller's
/// own values), a tint sets its colour and opacity, and the change shows at
/// once. The light or dark Mica follows the window content's theme.
/// </summary>
internal sealed partial class TintedMicaBackdrop : SystemBackdrop
{
    private const string Target = "cabinetos_ui::theme";

    private MicaController? _controller;
    private MicaLook? _tint;

    /// <summary>Whether this PC draws Mica at all (not over some remote sessions, for example).</summary>
    public bool IsSupported { get; private set; } = true;

    /// <summary>Tints the Mica, or shows it plain for null.</summary>
    public void SetTint(MicaLook? tint)
    {
        _tint = tint;
        Apply();
    }

    /// <inheritdoc/>
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (!MicaController.IsSupported())
        {
            IsSupported = false;
            Diag.Info(Target, "Mica is not supported here; the window has no backdrop");
            return;
        }
        _controller = new MicaController();
        Apply();
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot));
    }

    /// <summary>
    /// The window's light or dark changed (a theme's <c>kind</c>): Mica
    /// follows. The base version is not called: on Windows App SDK 2.5 it
    /// throws "The parameter is incorrect" for this target, which ended the
    /// window when a light theme was applied (seen 2026-09-28).
    /// </summary>
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        if (_controller is { } controller && target is not null && xamlRoot is not null)
        {
            controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, xamlRoot));
        }
    }

    /// <inheritdoc/>
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        if (_controller is { } controller)
        {
            controller.RemoveSystemBackdropTarget(disconnectedTarget);
            controller.Dispose();
            _controller = null;
        }
    }

    private void Apply()
    {
        if (_controller is not { } controller)
        {
            return;
        }
        if (_tint is not { } tint)
        {
            controller.ResetProperties();
            return;
        }
        var color = Color.FromArgb(0xFF, tint.Tint.R, tint.Tint.G, tint.Tint.B);
        controller.TintColor = color;
        controller.TintOpacity = (float)tint.Opacity;
        // Where Mica cannot draw (the window is inactive with transparency off, battery saver), the tint alone shows.
        controller.FallbackColor = color;
    }
}
