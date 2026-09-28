using System.Globalization;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Presentation;

/// <summary>
/// The rows of the Properties dialog (docs/ui.md, "File operations"), from
/// the listing's metadata; nothing is read from the disk.
/// </summary>
public static class PropertiesText
{
    /// <summary>
    /// One entry's rows: the type as the Type column shows it (a link's kind,
    /// the shell's name, else the built-in one), the location, the size
    /// (files), the times and the attributes.
    /// </summary>
    public static IReadOnlyList<(string Label, string Value)> ForEntry(ListingView view, int index, string folder, EntryDetail? detail, CultureInfo culture)
    {
        var isFolder = view.IsFolder(index);
        var type = DisplayFormat.RowType(view.NameSpan(index), view.Kind(index), isFolder, EntryFacts.LinkOf(view, index), detail);
        var rows = new List<(string Label, string Value)>
        {
            ("Type", type),
            ("Location", folder),
        };
        if (!isFolder)
        {
            rows.Add(("Size", DisplayFormat.SizeWithBytes(view.Size(index), culture)));
        }
        rows.Add(("Created", FullTime(view.Created(index), culture)));
        rows.Add(("Modified", FullTime(view.Modified(index), culture)));
        rows.Add(("Accessed", FullTime(view.Accessed(index), culture)));
        var attributes = DisplayFormat.AttributeNames(view.Attributes(index));
        rows.Add(("Attributes", attributes.Length == 0 ? "None" : attributes));
        return rows;
    }

    /// <summary>The files, the folders and the bytes of the files among some rows of the listing.</summary>
    public static (int Files, int Folders, ulong Bytes) Tally(ListingView view, IEnumerable<int> indexes)
    {
        int files = 0, folders = 0;
        ulong bytes = 0;
        foreach (var index in indexes)
        {
            if (view.IsFolder(index))
            {
                folders++;
            }
            else
            {
                files++;
                bytes += view.Size(index);
            }
        }
        return (files, folders, bytes);
    }

    private static string FullTime(DateTime utc, CultureInfo culture) =>
        utc == DateTime.MinValue ? "" : utc.ToLocalTime().ToString("G", culture);
}
