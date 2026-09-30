using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Tabs;

/// <summary>
/// One tab of a pane (docs/ui.md, "Tabs"): a folder with its own history,
/// order and lock, or a Tool Extension showing a file. The pane's live state
/// (listing, cursor, marks, history) belongs to the tab in front; the tabs
/// behind hold theirs here, until they come to the front again.
/// </summary>
public sealed class PaneTab
{
    /// <summary>A tab that shows the folder <paramref name="folder"/>.</summary>
    public PaneTab(string folder, bool locked = false)
    {
        Path = folder;
        Locked = locked;
    }

    private PaneTab(string file, string tool, string toolName)
    {
        Path = file;
        Tool = tool;
        ToolName = toolName;
    }

    /// <summary>A tab that shows <paramref name="file"/> in the Tool Extension <paramref name="tool"/>.</summary>
    public static PaneTab ForTool(string file, string tool, string toolName) => new(file, tool, toolName);

    /// <summary>The folder the tab shows, or the file its tool shows. Kept current by the window while the tab is in front.</summary>
    public string Path { get; set; }

    /// <summary>
    /// A locked tab stays on its folder: going into another folder opens a new
    /// tab instead (Total Commander's rule).
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>The Tool Extension's ID, or null for a folder.</summary>
    public string? Tool { get; }

    /// <summary>The Tool Extension's name, for the tab's tooltip; null for a folder.</summary>
    public string? ToolName { get; }

    /// <summary>Whether the tab shows a tool, not a folder.</summary>
    public bool IsTool => Tool is not null;

    /// <summary>The tab's own order (Ctrl+F3 to Ctrl+F6), or null for <c>panes.sort</c>. Valid while the tab is behind.</summary>
    public SortSpec? Sort { get; set; }

    /// <summary>The folders Back goes to, the nearest first. Valid while the tab is behind.</summary>
    public IReadOnlyList<string> Back { get; set; } = [];

    /// <summary>The folders Forward goes to, the nearest first. Valid while the tab is behind.</summary>
    public IReadOnlyList<string> Forward { get; set; } = [];

    /// <summary>The name of the row the cursor was on. Valid while the tab is behind.</summary>
    public string? CursorName { get; set; }

    /// <summary>The names of the marked rows. Valid while the tab is behind.</summary>
    public IReadOnlyList<string> MarkedNames { get; set; } = [];

    /// <summary>How far down its list the tab was scrolled, in pixels. Valid while the tab is behind.</summary>
    public double ScrollOffset { get; set; }

    /// <summary>
    /// The text of the tab's find widget (Ctrl+F; docs/ui.md, "Find in pane"),
    /// which filters its list; null while the widget is closed. Valid while the tab is behind.
    /// </summary>
    public string? FindQuery { get; set; }

    /// <summary>What the tab says: the folder's name, or the file's name for a tool.</summary>
    public string Title => IsTool ? System.IO.Path.GetFileName(Path) : DisplayFormat.FolderName(Path);

    /// <summary>A copy of a folder tab for a new tab: same folder and order, fresh history, not locked.</summary>
    public PaneTab Duplicate(string? folder = null) => new(folder ?? Path) { Sort = Sort };
}
