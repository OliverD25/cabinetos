using CabinetOS.Core.Listing;

namespace CabinetOS.Core.Presentation;

/// <summary>One row a delete concerns: its name, and the link it is, if any.</summary>
public sealed record DeleteTarget(string Name, bool IsFolder, LinkKind Link = LinkKind.None);

/// <summary>
/// The words of the permanent-delete question (Shift+Delete). A link is
/// named as a link, with what stays: deleting a junction removes the link,
/// never the files of the folder it points to.
/// </summary>
public static class DeleteText
{
    private const string NoUndo = "not go to the Recycle Bin, and this cannot be undone.";

    /// <summary>The dialog's title and text.</summary>
    public static (string Title, string Body) Permanent(IReadOnlyList<DeleteTarget> targets)
    {
        if (targets is [{ Link: not LinkKind.None } link])
        {
            return ("Delete the link permanently?",
                $"“{link.Name}” is {WhatLink(link.Link)}. Only the link is deleted; {WhatStays(link)} The link does {NoUndo}");
        }
        var single = targets.Count == 1;
        var what = single ? $"“{targets[0].Name}”" : $"These {targets.Count:N0} items";
        var body = $"{what} will be deleted for good. {(single ? "It does" : "They do")} {NoUndo}";
        var links = targets.Count(t => t.Link != LinkKind.None);
        if (links > 0)
        {
            body += links switch
            {
                1 => " 1 of them is a link: only the link is deleted, not what it points to.",
                2 when targets.Count == 2 => " Both are links: only the links are deleted, not what they point to.",
                _ when links == targets.Count => $" All {links:N0} are links: only the links are deleted, not what they point to.",
                _ => $" {links:N0} of them are links: only the links are deleted, not what they point to.",
            };
        }
        return (single ? "Delete 1 item permanently?" : $"Delete {targets.Count:N0} items permanently?", body);
    }

    private static string WhatLink(LinkKind link) => link switch
    {
        LinkKind.Junction => "a junction",
        LinkKind.MountPoint => "a mount point",
        LinkKind.SymbolicLink => "a symbolic link",
        _ => "a link",
    };

    private static string WhatStays(DeleteTarget link) => link switch
    {
        { Link: LinkKind.MountPoint } => "the volume it shows keeps its files.",
        { IsFolder: true } => "the folder it points to keeps its files.",
        _ => "the file it points to stays.",
    };
}
