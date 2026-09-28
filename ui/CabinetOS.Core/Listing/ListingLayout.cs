using System.Runtime.InteropServices;

namespace CabinetOS.Core.Listing;

/// <summary>
/// The C# mirror of the listing section's structs (docs/ipc.md, "The listing
/// section"; <c>cabinetos_protocol::shm</c> in Rust). A test pins every size
/// and offset, so a change on either side is a deliberate, visible act (ADR 0001).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ListingHeader
{
    public uint Magic;
    public uint Version;
    public uint EntryCount;
    public uint NameArenaOffset;
    public uint NameArenaLen;
    public uint Generation;
    public uint MetaOffset;
    public uint EntriesOffset;
    public uint Flags;
    public uint Reserved;
}

/// <summary>One entry: 16 bytes, 8-byte aligned.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ListingEntry
{
    public ulong Id;
    public uint NameOffset;
    public ushort NameLen;
    public byte Kind;
    public byte Flags;
}

/// <summary>An entry's metadata: 40 bytes, 8-byte aligned; times are FILETIME ticks.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ListingMeta
{
    public ulong Size;
    public long Modified;
    public long Created;
    public long Accessed;
    public uint Attributes;
    public uint Reserved;
}

/// <summary>What an entry is; unknown byte values read as <see cref="Unknown"/>.</summary>
public enum EntryKind : byte
{
    Unknown = 0,
    File = 1,
    Directory = 2,
    Link = 3,
}

/// <summary>The layout's constants, from the byte diagram in docs/ipc.md.</summary>
public static class ListingLayout
{
    /// <summary>"CBLS" read as a little-endian 32-bit number.</summary>
    public const uint Magic = 0x534C4243;

    /// <summary>The layout version this build reads.</summary>
    public const uint Version = 2;

    public const int HeaderSize = 40;
    public const int EntrySize = 16;
    public const int MetaSize = 40;

    /// <summary><see cref="ListingEntry.Flags"/> bit: the ID is a hash of the upper-cased name.</summary>
    public const byte FlagIdIsNameHash = 1;

    /// <summary><c>FILE_ATTRIBUTE_DIRECTORY</c>.</summary>
    public const uint AttributeDirectory = 0x10;

    /// <summary><c>FILE_ATTRIBUTE_HIDDEN</c>.</summary>
    public const uint AttributeHidden = 0x2;

    /// <summary>Reads a raw kind byte; unknown values become <see cref="EntryKind.Unknown"/>.</summary>
    public static EntryKind KindFromRaw(byte raw) => raw is >= 1 and <= 3 ? (EntryKind)raw : EntryKind.Unknown;
}
