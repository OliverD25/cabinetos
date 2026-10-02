namespace CabinetOS.Core.Tabs;

/// <summary>What a tab shows before its name: a folder, a lock, or the glyph of the tool it shows.</summary>
public enum TabGlyph
{
    /// <summary>A folder tab: the design's two-tone folder.</summary>
    Folder,

    /// <summary>A locked folder tab: the lock, in the folder's place.</summary>
    Lock,

    /// <summary>A tab that shows a tool.</summary>
    Tool,
}

/// <summary>The heights in a pane's tab strip: the tab in front, the tabs behind it, and the space under those.</summary>
public sealed record TabHeights(double Front, double Behind, double BehindMargin);

/// <summary>
/// The look of a pane's tab strip in v2 of the shell redesign (docs/ui.md,
/// "Tabs in the shell"; the creator's SHELL_REDESIGN.md §2): a recessed band
/// with the tab in front as a card standing on its bottom edge, the other
/// tabs lower, 3 px above that edge, with 1 px dividers between them but
/// none next to the tab in front, and the close button on the tab in front
/// only while the pane has more than one tab. No accent line on any tab.
/// </summary>
public static class TabLook
{
    /// <summary>The space under a tab behind the one in front.</summary>
    public const double BehindMargin = 3;

    /// <summary>
    /// The heights for a strip <paramref name="tabRow"/> high. The tab in front
    /// is lower than the strip by a quarter of what the strip has over 20 px;
    /// the others by twice that and 2 px more: the handout's 32 and 26 px in
    /// the default look's 36 px strip, 26 and 22 px in Commander Compact's 28.
    /// </summary>
    public static TabHeights Heights(double tabRow)
    {
        var step = Math.Max(0, Math.Round((tabRow - 20) / 4));
        return new TabHeights(tabRow - step, Math.Max(0, tabRow - (2 * step) - 2), BehindMargin);
    }

    /// <summary>
    /// Whether the tab at <paramref name="index"/> of <paramref name="count"/>
    /// has a divider at its right: a tab behind the front one, not the one just
    /// left of it (no divider next to the tab in front), and not the last one.
    /// </summary>
    public static bool Divider(int index, int front, int count) =>
        index != front && index != front - 1 && index < count - 1;

    /// <summary>
    /// Whether the tab shows its close button: only the tab in front, and
    /// only when it may close (<paramref name="canClose"/>: the pane has more
    /// than one tab). The others close with a middle click or their menu.
    /// </summary>
    public static bool ShowsClose(bool isFront, bool canClose) => isFront && canClose;

    /// <summary>What <paramref name="tab"/> shows before its name: its tool's glyph, a lock, or a folder.</summary>
    public static TabGlyph Glyph(PaneTab tab) => tab.IsTool ? TabGlyph.Tool : tab.Locked ? TabGlyph.Lock : TabGlyph.Folder;
}
