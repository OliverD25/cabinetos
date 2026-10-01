using CabinetOS.Core.Listing;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Shell;

/// <summary>
/// What a pane's toolbar row and path row say (docs/ui.md, "The pane's rows";
/// the creator's SHELL_REDESIGN.md v2 §2): the drive chip's letter, the
/// drive's free space from the core's <c>list_volumes</c> (the window reads
/// nothing from the disk), and the filter label at the path row's right end.
/// </summary>
public static class PaneRows
{
    /// <summary>The filter label while nothing filters the pane.</summary>
    public const string NoFilter = "*.*";

    /// <summary>
    /// The path row's filter label: <c>*.*</c> normally, <c>*query*</c> while
    /// the pane's find holds <paramref name="findQuery"/>. A text of spaces
    /// filters nothing, so it reads <c>*.*</c> too.
    /// </summary>
    public static string FilterLabel(string? findQuery) =>
        string.IsNullOrWhiteSpace(findQuery) ? NoFilter : $"*{findQuery}*";

    /// <summary>
    /// The drive chip's text for a pane showing <paramref name="path"/>:
    /// <c>C:</c>, <c>\\</c> for a network share, nothing for no path.
    /// </summary>
    public static string DriveLabel(string path) =>
        DriveMemory.LetterOf(path) is { } letter ? $"{letter}:"
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\"
        : "";

    /// <summary>
    /// The free space of <paramref name="path"/>'s drive as the toolbar shows
    /// it, <c>118 GB free</c>, from the volumes the core listed; nothing for a
    /// share, a drive the core did not list, or no list at all.
    /// </summary>
    public static string FreeSpace(IReadOnlyList<VolumeDetails>? volumes, string path)
    {
        if (volumes is null || DriveMemory.LetterOf(path) is not { } letter)
        {
            return "";
        }
        foreach (var volume in volumes)
        {
            if (volume.DriveLetter is { Length: > 0 } drive && char.ToUpperInvariant(drive[0]) == letter)
            {
                return $"{DisplayFormat.Bytes(volume.FreeBytes)} free";
            }
        }
        return "";
    }
}
