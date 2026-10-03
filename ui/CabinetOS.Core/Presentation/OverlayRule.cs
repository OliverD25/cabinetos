namespace CabinetOS.Core.Presentation;

/// <summary>
/// The panels that float over the window and take the keyboard until Esc. The permissions review is not one of
/// them: it is a dialog of the window (the router refuses every command but its own while it shows), and it opens
/// over the plugin list or the marketplace that asked for it.
/// </summary>
public enum Overlay
{
    /// <summary>The command palette (Ctrl+Shift+P).</summary>
    Palette,

    /// <summary>Quick Open (Ctrl+P).</summary>
    QuickOpen,

    /// <summary>A prompt in the palette's frame: the drive list, the pattern box, the pinned folders, a plugin's question.</summary>
    Prompt,

    /// <summary>The theme picker (Ctrl+K Ctrl+T).</summary>
    ThemePicker,

    /// <summary>The plugin list.</summary>
    PluginList,

    /// <summary>Quick View (Space): the floating panel over the panes; the keyboard stays in the pane's list.</summary>
    QuickView,
}

/// <summary>
/// The rule of one overlay at a time (docs/keybindings.md, "One overlay at a time"): opening an overlay closes every
/// other one first, so at most one is open and Esc closes the one on screen. Before it, Ctrl+K Ctrl+T opened the
/// theme picker over the palette, Quick Open, a prompt or the drive list, and the first Esc closed the one under it.
/// </summary>
public static class OverlayRule
{
    /// <summary>
    /// The open overlays that <paramref name="opening"/> closes first: all but itself, in the order of the
    /// <see cref="Overlay"/> values. <paramref name="opening"/> is null for a view that is not one of the overlays but
    /// covers them (the marketplace): it closes them all.
    /// </summary>
    public static IReadOnlyList<Overlay> ToClose(Overlay? opening, IReadOnlySet<Overlay> open) =>
        [.. Enum.GetValues<Overlay>().Where(overlay => overlay != opening && open.Contains(overlay))];
}
