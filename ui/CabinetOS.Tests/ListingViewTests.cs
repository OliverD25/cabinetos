using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CabinetOS.Core.Listing;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>The shared-memory reader against the byte diagram in docs/ipc.md.</summary>
[Collection(HandleTests.Name)]
public class ListingViewTests
{
    private static readonly long Modified = new DateTime(2026, 9, 28, 1, 2, 3, DateTimeKind.Utc).ToFileTimeUtc();
    private static readonly long Created = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToFileTimeUtc();

    [Fact]
    public void The_layout_is_pinned_to_the_byte_diagram()
    {
        Assert.Equal(40, Unsafe.SizeOf<ListingHeader>());
        Assert.Equal(16, Unsafe.SizeOf<ListingEntry>());
        Assert.Equal(40, Unsafe.SizeOf<ListingMeta>());
        Assert.Equal((ListingLayout.HeaderSize, ListingLayout.EntrySize, ListingLayout.MetaSize), (40, 16, 40));

        Assert.Equal(
            [0, 4, 8, 12, 16, 20, 24, 28, 32, 36],
            new[] { "Magic", "Version", "EntryCount", "NameArenaOffset", "NameArenaLen", "Generation", "MetaOffset", "EntriesOffset", "Flags", "Reserved" }
                .Select(f => (int)Marshal.OffsetOf<ListingHeader>(f)));
        Assert.Equal(
            [0, 8, 12, 14, 15],
            new[] { "Id", "NameOffset", "NameLen", "Kind", "Flags" }.Select(f => (int)Marshal.OffsetOf<ListingEntry>(f)));
        Assert.Equal(
            [0, 8, 16, 24, 32, 36],
            new[] { "Size", "Modified", "Created", "Accessed", "Attributes", "Reserved" }.Select(f => (int)Marshal.OffsetOf<ListingMeta>(f)));

        Assert.Equal(0x534C4243u, ListingLayout.Magic);
        Assert.Equal("CBLS"u8.ToArray(), BitConverter.GetBytes(ListingLayout.Magic));
        Assert.Equal(2u, ListingLayout.Version);
        Assert.Equal(1, ListingLayout.FlagIdIsNameHash);
        Assert.Equal(
            [EntryKind.Unknown, EntryKind.File, EntryKind.Directory, EntryKind.Link, EntryKind.Unknown],
            new byte[] { 0, 1, 2, 3, 200 }.Select(ListingLayout.KindFromRaw));
    }

    [Fact]
    public void Reads_every_field_of_a_synthetic_section()
    {
        var bytes = TestSections.Build(
        [
            new SyntheticEntry(0x0001_0000_0000_1234, "Documents", 2, Attributes: 0x10, Modified: Modified, Created: Created),
            new SyntheticEntry(0x0002_0000_0000_5678, "budget-2026.xlsx", 1, Size: 18_432, Modified: Modified, Created: Created, Accessed: Modified, Attributes: 0x20),
            new SyntheticEntry(0x8000_0000_dead_beef, "Link to repo", 3, Flags: 1, Attributes: 0x410),
            new SyntheticEntry(4, "Фото 😀", 9),
        ], generation: 3);
        var handle = TestSections.CreateSection(bytes);
        using var view = ListingView.Open(new SectionHandle(handle), (ulong)bytes.Length);

        Assert.Equal(4, view.Count);
        Assert.Equal(3u, view.Generation);

        Assert.Equal(0x0001_0000_0000_1234UL, view.Id(0));
        Assert.Equal(EntryKind.Directory, view.Kind(0));
        Assert.Equal("Documents", view.NameSpan(0).ToString());
        Assert.True(view.IsFolder(0));
        Assert.Equal(new DateTime(2026, 9, 28, 1, 2, 3, DateTimeKind.Utc), view.Modified(0));
        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), view.Created(0));

        Assert.Equal(EntryKind.File, view.Kind(1));
        Assert.Equal("budget-2026.xlsx", view.Name(1));
        Assert.Equal(18_432UL, view.Size(1));
        Assert.Equal(0x20u, view.Attributes(1));
        Assert.Equal(view.Modified(1), view.Accessed(1));
        Assert.False(view.IsFolder(1));

        Assert.Equal(EntryKind.Link, view.Kind(2));
        Assert.Equal(ListingLayout.FlagIdIsNameHash, view.Flags(2));
        Assert.True(view.IsFolder(2));
        Assert.Equal(DateTime.MinValue, view.Modified(2));

        Assert.Equal(EntryKind.Unknown, view.Kind(3));
        Assert.Equal("Фото 😀", view.Name(3));

        Assert.Equal(1, view.IndexOfId(0x0002_0000_0000_5678));
        Assert.Equal(-1, view.IndexOfId(99));
        Assert.Equal(0, view.IndexOfName("documents"));
        Assert.Equal(-1, view.IndexOfName("Downloads"));

        Assert.Equal(0UL, view.Id(4));
        Assert.Equal(0UL, view.Id(-1));
        Assert.True(view.NameSpan(4).IsEmpty);
    }

    [Fact]
    public void An_empty_folder_is_a_valid_listing()
    {
        var bytes = TestSections.Build([]);
        Assert.Equal(40, bytes.Length);
        var handle = TestSections.CreateSection(bytes);
        using var view = ListingView.Open(new SectionHandle(handle), 40);
        Assert.Equal(0, view.Count);
    }

    [Fact]
    public void After_dispose_nothing_reads_unmapped_memory_and_the_handle_is_closed()
    {
        var bytes = TestSections.Build([new SyntheticEntry(1, "a", 1, Size: 5)]);
        var handle = TestSections.CreateSection(bytes);
        var view = ListingView.Open(new SectionHandle(handle), (ulong)bytes.Length);
        Assert.Equal("a", view.Name(0));
        view.Dispose();
        view.Dispose();

        Assert.True(view.IsDisposed);
        Assert.False(TestSections.IsOpen(handle));
        Assert.True(view.NameSpan(0).IsEmpty);
        Assert.Equal(0UL, view.Size(0));
        Assert.Equal(EntryKind.Unknown, view.Kind(0));
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("entries past the end")]
    [InlineData("misaligned metadata")]
    [InlineData("names past the end")]
    public void A_section_that_is_not_a_valid_listing_is_refused_and_its_handle_closed(string fault)
    {
        var bytes = TestSections.Build([new SyntheticEntry(1, "abc", 1)], magic: fault == "magic" ? 0x12345678u : 0x534C4243u, version: fault == "version" ? 3u : 2u);
        switch (fault)
        {
            case "entries past the end":
                BitConverter.TryWriteBytes(bytes.AsSpan(8), 1000u);
                break;
            case "misaligned metadata":
                BitConverter.TryWriteBytes(bytes.AsSpan(24), 58u);
                break;
            case "names past the end":
                BitConverter.TryWriteBytes(bytes.AsSpan(16), 4096u);
                break;
        }
        var handle = TestSections.CreateSection(bytes);
        Assert.Throws<InvalidDataException>(() => ListingView.Open(new SectionHandle(handle), (ulong)bytes.Length));
        Assert.False(TestSections.IsOpen(handle));
    }

    [Fact]
    public void A_name_outside_the_arena_reads_as_empty()
    {
        var bytes = TestSections.Build([new SyntheticEntry(1, "ok", 1), new SyntheticEntry(2, "bad", 1)]);
        // Entry 1's name length now runs past the arena.
        BitConverter.TryWriteBytes(bytes.AsSpan(40 + 16 + 12), (ushort)500);
        var handle = TestSections.CreateSection(bytes);
        using var view = ListingView.Open(new SectionHandle(handle), (ulong)bytes.Length);
        Assert.Equal("ok", view.Name(0));
        Assert.Equal("", view.Name(1));
    }

    [Fact]
    public void A_size_larger_than_the_section_cannot_be_mapped()
    {
        var bytes = TestSections.Build([new SyntheticEntry(1, "a", 1)]);
        var handle = TestSections.CreateSection(bytes);
        Assert.Throws<IOException>(() => ListingView.Open(new SectionHandle(handle), 1 << 20));
        Assert.False(TestSections.IsOpen(handle));
    }
}
