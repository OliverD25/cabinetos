using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Listing;

/// <summary>
/// The type names and icon keys of one listing section, asked for a page at
/// a time as rows come into view (<c>describe_entries</c>, docs/ipc.md "Type
/// names and icons"). A refresh brings a new generation, and the pages are
/// asked for again.
/// </summary>
public sealed class EntryDetailsCache
{
    /// <summary>Entries per request: a few screens of rows (the core takes up to 512).</summary>
    public const int PageSize = 128;

    private readonly Dictionary<int, EntryDetail> _details = [];
    private readonly HashSet<int> _requested = [];

    /// <summary>The listing the details belong to.</summary>
    public ulong ListingId { get; private set; }

    /// <summary>The section's generation.</summary>
    public uint Generation { get; private set; }

    /// <summary>A new listing, or a refreshed section of the same one: everything is asked again.</summary>
    public void Reset(ulong listingId, uint generation)
    {
        ListingId = listingId;
        Generation = generation;
        _details.Clear();
        _requested.Clear();
    }

    /// <summary>Entry <paramref name="index"/>'s detail, once its page came.</summary>
    public EntryDetail? Get(int index) => _details.GetValueOrDefault(index);

    /// <summary>
    /// The pages that cover rows <paramref name="first"/> to <paramref name="last"/>
    /// of a listing of <paramref name="count"/> entries and were not asked for
    /// yet, as (from, count); they are marked as asked.
    /// </summary>
    public IReadOnlyList<(uint From, uint Count)> TakePagesToRequest(int first, int last, int count)
    {
        if (count <= 0 || ListingId == 0)
        {
            return [];
        }
        first = Math.Clamp(first, 0, count - 1);
        last = Math.Clamp(last, first, count - 1);
        var pages = new List<(uint, uint)>();
        for (var page = first / PageSize; page <= last / PageSize; page++)
        {
            if (_requested.Add(page))
            {
                var from = page * PageSize;
                pages.Add(((uint)from, (uint)Math.Min(PageSize, count - from)));
            }
        }
        return pages;
    }

    /// <summary>A page that failed can be asked for again.</summary>
    public void Forget(uint from) => _requested.Remove((int)(from / PageSize));

    /// <summary>
    /// Takes a reply; false when it is for another listing or an older
    /// section (the rows then wait for the next one).
    /// </summary>
    public bool Apply(EntryDetailsReply reply)
    {
        if (reply.ListingId != ListingId || reply.Generation != Generation)
        {
            return false;
        }
        for (var i = 0; i < reply.Details.Count; i++)
        {
            _details[(int)reply.From + i] = reply.Details[i];
        }
        return true;
    }
}

/// <summary>
/// What the core said, by extension. A type name depends only on the
/// extension and the folder attribute (the core itself keeps it per
/// extension), and so does an <c>ext:</c> icon key. So a row whose page has
/// not come yet shows a known extension's name at once, instead of flashing
/// the built-in "TXT File" after every refresh. Programs, icons and
/// shortcuts (<c>path:</c> keys, whose icon is in the file) are not guessed.
/// </summary>
public sealed class ExtensionDetails
{
    private const string FolderKey = "\\folder";
    private readonly Dictionary<string, EntryDetail> _byExtension = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers what the core said about an entry named <paramref name="name"/>.</summary>
    public void Learn(ReadOnlySpan<char> name, bool isFolder, EntryDetail detail)
    {
        if (detail.IconKey.StartsWith("path:", StringComparison.Ordinal))
        {
            return;
        }
        _byExtension[isFolder ? FolderKey : Extension(name)] = detail;
    }

    /// <summary>A detail for an entry whose own has not come yet, or null.</summary>
    public EntryDetail? Guess(ReadOnlySpan<char> name, bool isFolder)
    {
        var key = isFolder ? FolderKey : Extension(name);
        return key is ".exe" or ".ico" or ".lnk" ? null : _byExtension.GetValueOrDefault(key);
    }

    // The extension with its dot, lower case; "" for none (the core's "generic").
    private static string Extension(ReadOnlySpan<char> name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 || dot == name.Length - 1 ? "" : name[dot..].ToString().ToLowerInvariant();
    }
}

/// <summary>Which of the core's icon sizes to ask for.</summary>
public static class IconSizes
{
    /// <summary>The sizes <c>get_icon</c> offers.</summary>
    public static readonly uint[] Offered = [16, 24, 32, 48];

    /// <summary>
    /// The smallest offered size that covers <paramref name="dip"/> device-independent
    /// pixels at <paramref name="scale"/> (24 for a 16 px icon at 150 %), else the largest.
    /// </summary>
    public static uint For(double scale, double dip = 16)
    {
        var physical = dip * Math.Max(1, scale);
        foreach (var size in Offered)
        {
            if (size >= physical - 0.01)
            {
                return size;
            }
        }
        return Offered[^1];
    }
}
