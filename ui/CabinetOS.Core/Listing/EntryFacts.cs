namespace CabinetOS.Core.Listing;

/// <summary>What kind of link an entry is (docs/ui.md, "Links and cloud files").</summary>
public enum LinkKind
{
    /// <summary>Not a link.</summary>
    None,

    /// <summary>A link whose kind the listing does not name.</summary>
    Unknown,

    /// <summary>A junction: a folder that stands for another folder on a local volume.</summary>
    Junction,

    /// <summary>A symbolic link, to a file or to a folder.</summary>
    SymbolicLink,

    /// <summary>A mount point: a folder that stands for a whole volume.</summary>
    MountPoint,
}

/// <summary>
/// Facts about a listing entry beyond its name, size and times: whether it
/// is a link and of what kind, and whether its data is on this disk.
/// </summary>
public static class EntryFacts
{
    /// <summary><c>FILE_ATTRIBUTE_OFFLINE</c>: the data was moved to other storage.</summary>
    public const uint AttributeOffline = 0x1000;

    /// <summary><c>FILE_ATTRIBUTE_RECALL_ON_OPEN</c>: a folder whose list of files is still in the cloud.</summary>
    public const uint AttributeRecallOnOpen = 0x40000;

    /// <summary><c>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS</c>: reading the file downloads it (OneDrive's "online only").</summary>
    public const uint AttributeRecallOnDataAccess = 0x400000;

    /// <summary>
    /// Whether the entry's data is not on this disk (a cloud placeholder not
    /// downloaded, or an offline file): showing its row must not read it.
    /// The core's entry flag (<see cref="ListingLayout.FlagNotOnDisk"/>) is
    /// set from these same attributes, so the attributes alone decide.
    /// </summary>
    public static bool IsNotOnDisk(uint attributes) =>
        (attributes & (AttributeOffline | AttributeRecallOnOpen | AttributeRecallOnDataAccess)) != 0;

    /// <summary>
    /// The link kind of entry <paramref name="index"/>: kind 3 is a link, and
    /// its flags say which (docs/ipc.md, "The listing section"). A link of a
    /// kind no flag names (a WSL link) is <see cref="LinkKind.Unknown"/>.
    /// </summary>
    public static LinkKind LinkOf(ListingView view, int index)
    {
        if (view.Kind(index) != EntryKind.Link)
        {
            return LinkKind.None;
        }
        var flags = view.Flags(index);
        // A mount point is a junction to a volume: its own flag comes first.
        return (flags & ListingLayout.FlagMountPoint) != 0 ? LinkKind.MountPoint
            : (flags & ListingLayout.FlagJunction) != 0 ? LinkKind.Junction
            : (flags & ListingLayout.FlagSymbolicLink) != 0 ? LinkKind.SymbolicLink
            : LinkKind.Unknown;
    }
}
