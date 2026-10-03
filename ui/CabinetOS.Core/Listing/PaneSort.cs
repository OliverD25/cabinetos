using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Listing;

/// <summary>
/// A pane's own sort (Ctrl+F3 to Ctrl+F6, as in Total Commander). The core
/// sorts; the pane only asks for an order with <c>list_directory</c>.
/// </summary>
public static class PaneSort
{
    /// <summary>The sort keys of <c>list_directory</c> (docs/ipc.md, "Listing a directory").</summary>
    public const string Name = "name";
    public const string Extension = "extension";
    public const string Size = "size";
    public const string Modified = "modified";
    public const string Kind = "kind";

    /// <summary>
    /// The sort after pressing the key of <paramref name="key"/>: the same key
    /// again reverses the order; another key starts in its own direction, the
    /// largest and the newest first for size and time, as Total Commander does.
    /// </summary>
    public static SortSpec Next(SortSpec current, string key) =>
        current.Key == key ? current with { Descending = !current.Descending } : new SortSpec(key, key is Size or Modified);

    /// <summary>
    /// The sort key a click on a column's heading presses, as the Ctrl+F3 to Ctrl+F6 key of that
    /// column does. Type is the extension: the core has no order by the shell's type name, and the
    /// same extensions share a type. The heading's chevron follows the key (<c>FilePane</c>).
    /// </summary>
    public static string ForColumn(ListColumn column) => column switch
    {
        ListColumn.Modified => Modified,
        ListColumn.Type => Extension,
        ListColumn.Size => Size,
        _ => Name,
    };

    /// <summary>The sort in words, for the status bar: "size, largest first".</summary>
    public static string Describe(SortSpec sort) => sort.Key switch
    {
        Size => sort.Descending ? "size, largest first" : "size, smallest first",
        Modified => sort.Descending ? "date modified, newest first" : "date modified, oldest first",
        _ => $"{sort.Key}, {(sort.Descending ? "Z to A" : "A to Z")}",
    };

    /// <summary>The protocol version a core needs for <paramref name="key"/>; 0 for any.</summary>
    public static uint ProtocolFor(string key) => key == Extension ? 12u : 0u;
}
