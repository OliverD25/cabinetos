using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Tabs;

/// <summary>What one pane shows right now, as the window reads it from its own state.</summary>
/// <param name="Tabs">The pane's tabs.</param>
/// <param name="Active">The tab in front.</param>
/// <param name="Cursor">The full path of the row the cursor is on, or null.</param>
/// <param name="Marked">The full paths of the marked rows, in the pane's order.</param>
/// <param name="FrontFolder">
/// The folder the tab in front reports instead of its own path, or null: in
/// the column view (ADR 0016) the column with the keyboard, whose rows the
/// cursor and the marks are.
/// </param>
public sealed record PaneSnapshot(IReadOnlyList<PaneTab> Tabs, int Active, string? Cursor, IReadOnlyList<string> Marked, string? FrontFolder = null);

/// <summary>
/// Builds the <c>window_state</c> request (docs/ipc.md, "What the window
/// shows"): the shape the protocol text gives, from what the window holds.
/// </summary>
public static class WindowStateBuilder
{
    /// <summary>
    /// The most marked paths one message carries. A pane with 100,000 rows
    /// marked would cost the UI thread that many names on every change, and
    /// the core only stores the state; what is over the limit is left out.
    /// </summary>
    public const int MaxMarked = 1000;

    /// <summary>The request for the pane at <paramref name="activePane"/> (0 left, 1 right) having the keyboard.</summary>
    public static WindowStateRequest Build(int activePane, PaneSnapshot left, PaneSnapshot right) =>
        new(activePane == 0 ? "left" : "right", new WindowPanesState(Pane(left), Pane(right)));

    private static WindowPaneState Pane(PaneSnapshot pane) => new(
        [.. pane.Tabs.Select((tab, index) => new WindowTabState(index == pane.Active && pane.FrontFolder is { } front ? front : tab.Path, tab.Locked, tab.Tool))],
        (uint)Math.Max(0, pane.Active),
        pane.Cursor,
        [.. pane.Marked.Take(MaxMarked)]);
}
