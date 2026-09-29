using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Memory;

namespace CabinetOS.Core.Listing;

/// <summary>
/// A directory listing read straight from the core's shared-memory section
/// (docs/ipc.md, "The listing section"; brief §4, the data channel). Nothing
/// is copied: names come from the UTF-16 arena as spans, and a string is
/// made only when a caller asks for one, one visible row at a time.
/// </summary>
/// <remarks>
/// The only hand-written <c>unsafe</c> code of the UI lives in this class.
/// A view is used on one thread (the UI thread). After <see cref="Dispose"/>
/// every accessor returns an empty value instead of touching unmapped memory.
/// </remarks>
public sealed class ListingView : IDisposable
{
    private readonly SafeHandle _section;
    private readonly ulong _size;
    private readonly uint _entriesOffset;
    private readonly uint _metaOffset;
    private readonly uint _namesOffset;
    private readonly uint _namesLength;
    private nint _base;

    private ListingView(SafeHandle section, nint address, ulong size, in ListingHeader header)
    {
        _section = section;
        _base = address;
        _size = size;
        Count = (int)header.EntryCount;
        Generation = header.Generation;
        _entriesOffset = header.EntriesOffset;
        _metaOffset = header.MetaOffset;
        _namesOffset = header.NameArenaOffset;
        _namesLength = header.NameArenaLen;
    }

    /// <summary>Entries in the listing.</summary>
    public int Count { get; }

    /// <summary>1 for a listing's first section, one more for each refresh.</summary>
    public uint Generation { get; }

    /// <summary>The bytes that hold the listing.</summary>
    public ulong SectionSize => _size;

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed => _base == 0;

    /// <summary>
    /// Maps <paramref name="section"/> read-only and checks its header. Takes
    /// ownership of the handle in every case: it is closed when the view is
    /// disposed, or at once when mapping or checking fails.
    /// </summary>
    /// <exception cref="InvalidDataException">The section does not hold a listing this build can read.</exception>
    /// <exception cref="IOException">Windows refused to map the section.</exception>
    public static ListingView Open(SafeHandle section, ulong sectionSize)
    {
        ArgumentNullException.ThrowIfNull(section);
        nint address = 0;
        try
        {
            if (sectionSize < ListingLayout.HeaderSize || sectionSize > int.MaxValue)
            {
                throw new InvalidDataException($"a listing section of {sectionSize} bytes is impossible");
            }
            // Mapping more than the section holds fails, which is reported below.
            address = PInvoke.MapViewOfFile(section, FILE_MAP.FILE_MAP_READ, 0, 0, (nuint)sectionSize);
            if (address == 0)
            {
                throw new IOException($"cannot map the listing section: Windows error {Marshal.GetLastPInvokeError()}");
            }
            ListingHeader header;
            unsafe
            {
                // The view is at least HeaderSize bytes long (checked above) and
                // page-aligned, so the header lies inside it and is aligned.
                header = *(ListingHeader*)address;
            }
            Validate(header, sectionSize);
            return new ListingView(section, address, sectionSize, header);
        }
        catch
        {
            if (address != 0)
            {
                Unmap(address);
            }
            section.Dispose();
            throw;
        }
    }

    /// <summary>Checks magic, version and that every part lies inside the section.</summary>
    internal static void Validate(in ListingHeader header, ulong sectionSize)
    {
        if (header.Magic != ListingLayout.Magic)
        {
            throw new InvalidDataException($"wrong magic 0x{header.Magic:X8}");
        }
        if (header.Version != ListingLayout.Version)
        {
            throw new InvalidDataException($"layout version {header.Version}; this build reads {ListingLayout.Version}");
        }
        if (header.EntryCount > int.MaxValue)
        {
            throw new InvalidDataException($"{header.EntryCount} entries");
        }
        var count = (ulong)header.EntryCount;
        CheckPart("entries", header.EntriesOffset, count * ListingLayout.EntrySize, 8, sectionSize);
        CheckPart("metadata", header.MetaOffset, count * ListingLayout.MetaSize, 8, sectionSize);
        CheckPart("names", header.NameArenaOffset, header.NameArenaLen, 2, sectionSize);
    }

    /// <summary>Entry <paramref name="index"/>'s ID: its file reference number, or a name hash.</summary>
    public ulong Id(int index) => Entry(index).Id;

    /// <summary>What entry <paramref name="index"/> is.</summary>
    public EntryKind Kind(int index) => ListingLayout.KindFromRaw(Entry(index).Kind);

    /// <summary>Entry <paramref name="index"/>'s flags (<see cref="ListingLayout.FlagIdIsNameHash"/>).</summary>
    public byte Flags(int index) => Entry(index).Flags;

    /// <summary>
    /// Entry <paramref name="index"/>'s name, straight from the arena. Valid
    /// until the view is disposed; empty when the entry is malformed.
    /// </summary>
    public ReadOnlySpan<char> NameSpan(int index)
    {
        var entry = Entry(index);
        var start = (ulong)entry.NameOffset;
        var end = start + ((ulong)entry.NameLen * 2);
        if (_base == 0 || (start & 1) != 0 || end > _namesLength)
        {
            return [];
        }
        unsafe
        {
            // start and end lie inside the name arena (checked just above), the
            // arena lies inside the mapped view (checked in Validate), and the
            // view stays mapped until Dispose, which needs this same thread.
            return new ReadOnlySpan<char>((void*)(_base + (nint)_namesOffset + (nint)start), entry.NameLen);
        }
    }

    /// <summary>Entry <paramref name="index"/>'s name as a new string.</summary>
    public string Name(int index) => new(NameSpan(index));

    /// <summary>Entry <paramref name="index"/>'s size, times and attributes.</summary>
    public ListingMeta Meta(int index)
    {
        if (_base == 0 || (uint)index >= (uint)Count)
        {
            return default;
        }
        unsafe
        {
            // index < Count, and Validate proved Count metadata records fit in
            // the view at an 8-byte-aligned offset.
            return ((ListingMeta*)(_base + (nint)_metaOffset))[index];
        }
    }

    /// <summary>Entry <paramref name="index"/>'s size in bytes; 0 for directories.</summary>
    public ulong Size(int index) => Meta(index).Size;

    /// <summary>Entry <paramref name="index"/>'s last write time (UTC).</summary>
    public DateTime Modified(int index) => FromFileTime(Meta(index).Modified);

    /// <summary>Entry <paramref name="index"/>'s creation time (UTC).</summary>
    public DateTime Created(int index) => FromFileTime(Meta(index).Created);

    /// <summary>Entry <paramref name="index"/>'s last access time (UTC).</summary>
    public DateTime Accessed(int index) => FromFileTime(Meta(index).Accessed);

    /// <summary>Entry <paramref name="index"/>'s <c>FILE_ATTRIBUTE_*</c> bits.</summary>
    public uint Attributes(int index) => Meta(index).Attributes;

    /// <summary>Entry <paramref name="index"/>'s <c>IO_REPARSE_TAG_*</c>, or 0 when it is not a reparse point.</summary>
    public uint ReparseTag(int index) => Meta(index).ReparseTag;

    /// <summary>Whether entry <paramref name="index"/> can be opened as a folder (a directory, or a link to one).</summary>
    public bool IsFolder(int index) =>
        Kind(index) == EntryKind.Directory || (Attributes(index) & ListingLayout.AttributeDirectory) != 0;

    /// <summary>The index of the entry with <paramref name="id"/>, or -1.</summary>
    public int IndexOfId(ulong id)
    {
        for (var i = 0; i < Count; i++)
        {
            if (Id(i) == id)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// The index of the entry named exactly <paramref name="name"/>, else of the
    /// first one named so in any case, or -1. A case-sensitive folder can hold
    /// <c>Report.txt</c> and <c>report.txt</c> side by side.
    /// </summary>
    public int IndexOfName(ReadOnlySpan<char> name)
    {
        var anyCase = -1;
        for (var i = 0; i < Count; i++)
        {
            var candidate = NameSpan(i);
            if (candidate.SequenceEqual(name))
            {
                return i;
            }
            if (anyCase < 0 && candidate.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                anyCase = i;
            }
        }
        return anyCase;
    }

    /// <summary>
    /// The indexes of the entries named exactly as one of <paramref name="names"/>,
    /// in listing order, in one pass: a refresh or Restore Selection marks the
    /// rows that had those names. A name the listing does not have is skipped.
    /// </summary>
    public List<int> IndexesOfNames(IReadOnlyCollection<string> names)
    {
        var found = new List<int>();
        if (names.Count == 0)
        {
            return found;
        }
        var wanted = new HashSet<string>(names, StringComparer.Ordinal).GetAlternateLookup<ReadOnlySpan<char>>();
        for (var i = 0; i < Count && found.Count < wanted.Set.Count; i++)
        {
            if (wanted.Contains(NameSpan(i)))
            {
                found.Add(i);
            }
        }
        return found;
    }

    /// <summary>Unmaps the view and closes the section handle.</summary>
    public void Dispose()
    {
        var address = _base;
        if (address == 0)
        {
            return;
        }
        _base = 0;
        Unmap(address);
        _section.Dispose();
    }

    private ListingEntry Entry(int index)
    {
        if (_base == 0 || (uint)index >= (uint)Count)
        {
            return default;
        }
        unsafe
        {
            // index < Count, and Validate proved Count entries fit in the view at
            // an 8-byte-aligned offset.
            return ((ListingEntry*)(_base + (nint)_entriesOffset))[index];
        }
    }

    // Called once per mapping: Dispose clears _base before calling it.
    private static void Unmap(nint address) => PInvoke.UnmapViewOfFile(new MEMORY_MAPPED_VIEW_ADDRESS(address));

    private static void CheckPart(string part, uint offset, ulong length, uint alignment, ulong sectionSize)
    {
        if (offset % alignment != 0)
        {
            throw new InvalidDataException($"{part} at {offset} is not {alignment}-byte aligned");
        }
        if (offset + length > sectionSize)
        {
            throw new InvalidDataException($"{part} end at {offset + length}, past the section's {sectionSize} bytes");
        }
    }

    private static DateTime FromFileTime(long ticks) =>
        ticks > 0 && ticks <= DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(ticks) : DateTime.MinValue;
}
