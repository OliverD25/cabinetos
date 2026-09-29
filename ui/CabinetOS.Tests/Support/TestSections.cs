using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using CabinetOS.Core.Listing;

namespace CabinetOS.Tests.Support;

/// <summary>One entry of a synthetic listing.</summary>
internal sealed record SyntheticEntry(ulong Id, string Name, byte Kind, byte Flags = 0, ulong Size = 0,
    long Modified = 0, long Created = 0, long Accessed = 0, uint Attributes = 0, uint ReparseTag = 0);

/// <summary>One row of a synthetic preview: the path, the change byte, the target, and what the path is on disk.</summary>
internal sealed record SyntheticPreviewRow(string Path, byte Change, string? To, EntryKind Kind);

/// <summary>
/// Builds listing sections byte by byte from the diagram in docs/ipc.md,
/// independently of the C# structs, so the structs are checked against it.
/// </summary>
internal static class TestSections
{
    public static byte[] Build(IReadOnlyList<SyntheticEntry> entries, uint generation = 1, uint magic = 0x534C4243, uint version = 2)
    {
        const int header = 40, entrySize = 16, metaSize = 40;
        var entriesOffset = header;
        var metaOffset = entriesOffset + (entrySize * entries.Count);
        var arenaOffset = metaOffset + (metaSize * entries.Count);
        var names = entries.Select(e => Encoding.Unicode.GetBytes(e.Name)).ToArray();
        var arenaLength = names.Sum(n => n.Length);
        var bytes = new byte[arenaOffset + arenaLength];
        var span = bytes.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)arenaOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)arenaLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], generation);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)metaOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)entriesOffset);

        var nameOffset = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var at = entriesOffset + (i * entrySize);
            BinaryPrimitives.WriteUInt64LittleEndian(span[at..], e.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 8)..], (uint)nameOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 12)..], (ushort)e.Name.Length);
            span[at + 14] = e.Kind;
            span[at + 15] = e.Flags;

            var meta = metaOffset + (i * metaSize);
            BinaryPrimitives.WriteUInt64LittleEndian(span[meta..], e.Size);
            BinaryPrimitives.WriteInt64LittleEndian(span[(meta + 8)..], e.Modified);
            BinaryPrimitives.WriteInt64LittleEndian(span[(meta + 16)..], e.Created);
            BinaryPrimitives.WriteInt64LittleEndian(span[(meta + 24)..], e.Accessed);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(meta + 32)..], e.Attributes);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(meta + 36)..], e.ReparseTag);

            names[i].CopyTo(span[(arenaOffset + nameOffset)..]);
            nameOffset += names[i].Length;
        }
        return bytes;
    }

    /// <summary>
    /// A preview section as docs/ipc.md, "The listing section", draws it: the
    /// paths as names, then the targets in the same arena, then the rows.
    /// </summary>
    public static byte[] BuildPreview(IReadOnlyList<SyntheticPreviewRow> rows)
    {
        const int header = 40, entrySize = 16, metaSize = 40, previewSize = 12;
        var entriesOffset = header;
        var metaOffset = entriesOffset + (entrySize * rows.Count);
        var arenaOffset = metaOffset + (metaSize * rows.Count);
        var names = rows.Select(r => Encoding.Unicode.GetBytes(r.Path)).ToArray();
        var targets = rows.Select(r => r.To is null ? [] : Encoding.Unicode.GetBytes(r.To)).ToArray();
        var arenaLength = names.Sum(n => n.Length) + targets.Sum(t => t.Length);
        var previewOffset = (arenaOffset + arenaLength + 3) & ~3;
        var bytes = new byte[previewOffset + (previewSize * rows.Count)];
        var span = bytes.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], 0x534C4243);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)rows.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)arenaOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)arenaLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)metaOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)entriesOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[32..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[36..], (uint)previewOffset);

        var nameOffset = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var at = entriesOffset + (i * entrySize);
            BinaryPrimitives.WriteUInt64LittleEndian(span[at..], (ulong)i);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 8)..], (uint)nameOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 12)..], (ushort)rows[i].Path.Length);
            span[at + 14] = (byte)rows[i].Kind;
            names[i].CopyTo(span[(arenaOffset + nameOffset)..]);
            nameOffset += names[i].Length;
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var at = previewOffset + (i * previewSize);
            BinaryPrimitives.WriteUInt32LittleEndian(span[at..], (uint)(targets[i].Length == 0 ? 0 : nameOffset));
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 4)..], (uint)(targets[i].Length / 2));
            span[at + 8] = rows[i].Change;
            targets[i].CopyTo(span[(arenaOffset + nameOffset)..]);
            nameOffset += targets[i].Length;
        }
        return bytes;
    }

    /// <summary>
    /// Puts <paramref name="content"/> into a new unnamed section and returns a
    /// second handle to it, as the core hands one over. The caller owns it.
    /// </summary>
    public static nint CreateSection(byte[] content, int extraBytes = 0)
    {
        using var file = MemoryMappedFile.CreateNew(null, Math.Max(1, content.Length + extraBytes));
        using (var view = file.CreateViewAccessor())
        {
            view.WriteArray(0, content, 0, content.Length);
        }
        var source = file.SafeMemoryMappedFileHandle.DangerousGetHandle();
        var self = GetCurrentProcess();
        if (!DuplicateHandle(self, source, self, out var copy, 0, false, DuplicateSameAccess))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return copy;
    }

    /// <summary>Whether a handle value is still open in this process.</summary>
    public static bool IsOpen(nint handle) => GetHandleInformation(handle, out _);

    private const uint DuplicateSameAccess = 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target,
        uint access, bool inherit, uint options);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetHandleInformation(nint handle, out uint flags);
}
