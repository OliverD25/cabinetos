using System.Globalization;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Edge cases, class C: links and cloud files (docs/ui.md, "Links and cloud
/// files"), on fake listings. The listing marks a link (kind 3) but does not
/// name its kind yet, and a cloud placeholder by its attributes.
/// </summary>
[Collection(HandleTests.Name)]
public class LinksAndCloudFilesTests
{
    private const uint Directory = 0x10;
    private const uint ReparsePoint = 0x400;
    private const uint Offline = 0x1000;
    private const uint RecallOnOpen = 0x40000;
    private const uint Pinned = 0x80000;
    private const uint Unpinned = 0x100000;
    private const uint RecallOnDataAccess = 0x400000;

    private static ListingView Open(params SyntheticEntry[] entries)
    {
        var bytes = TestSections.Build(entries);
        return ListingView.Open(new SectionHandle(TestSections.CreateSection(bytes)), (ulong)bytes.Length);
    }

    [Fact]
    public void A_link_in_the_listing_is_a_link_of_a_kind_the_listing_does_not_name_yet()
    {
        using var view = Open(
            new SyntheticEntry(1, "junction", 3, Attributes: Directory | ReparsePoint),
            new SyntheticEntry(2, "notes.md", 3, Attributes: ReparsePoint),
            new SyntheticEntry(3, "folder", 2, Attributes: Directory),
            // A OneDrive file carries the reparse attribute too, but it is a file to the user.
            new SyntheticEntry(4, "report.docx", 1, Attributes: ReparsePoint | RecallOnDataAccess));

        Assert.Equal([LinkKind.Unknown, LinkKind.Unknown, LinkKind.None, LinkKind.None],
            Enumerable.Range(0, view.Count).Select(i => EntryFacts.LinkOf(view, i)));
    }

    [Theory]
    [InlineData(LinkKind.Junction, true, "Junction")]
    [InlineData(LinkKind.MountPoint, true, "Mount point")]
    [InlineData(LinkKind.SymbolicLink, true, "Symbolic link to a folder")]
    [InlineData(LinkKind.SymbolicLink, false, "Symbolic link to a file")]
    [InlineData(LinkKind.Unknown, true, "Link to a folder")]
    [InlineData(LinkKind.Unknown, false, "Link to a file")]
    public void A_link_names_its_kind_in_the_type_column_whatever_the_shell_calls_its_target(LinkKind link, bool isFolder, string expected)
    {
        var name = isFolder ? "junction" : "notes.md";
        // The core names a type by the folder attribute and the extension: a junction is a "File folder" to it.
        var shell = new EntryDetail(isFolder ? "File folder" : "Markdown Source File", isFolder ? "folder" : "ext:.md");

        Assert.Equal(expected, DisplayFormat.RowType(name, EntryKind.Link, isFolder, link, shell));
        // The same before the core's page of details comes.
        Assert.Equal(expected, DisplayFormat.RowType(name, EntryKind.Link, isFolder, link, null));
        Assert.Equal("Markdown Source File", DisplayFormat.RowType("notes.md", EntryKind.File, false, LinkKind.None, new EntryDetail("Markdown Source File", "ext:.md")));
    }

    [Fact]
    public void Properties_names_a_link_as_the_type_column_does()
    {
        using var view = Open(new SyntheticEntry(1, "junction", 3, Attributes: Directory | ReparsePoint));

        var rows = PropertiesText.ForEntry(view, 0, @"C:\edge\links", new EntryDetail("File folder", "folder"), CultureInfo.InvariantCulture);

        Assert.Equal(("Type", "Link to a folder"), rows[0]);
    }

    [Theory]
    // Online only (OneDrive's cloud mark), and after "Free up space".
    [InlineData(ReparsePoint | RecallOnDataAccess, true)]
    [InlineData(ReparsePoint | RecallOnDataAccess | Unpinned, true)]
    // A folder whose list of files is still in the cloud.
    [InlineData(Directory | ReparsePoint | RecallOnOpen, true)]
    [InlineData(Offline, true)]
    // "Always keep on this device", and a file that was downloaded once.
    [InlineData(ReparsePoint | Pinned, false)]
    [InlineData(ReparsePoint, false)]
    [InlineData(0u, false)]
    public void A_row_not_on_this_disk_is_known_by_its_attributes(uint attributes, bool notOnDisk) =>
        Assert.Equal(notOnDisk, EntryFacts.IsNotOnDisk(attributes));

    [Fact]
    public void Showing_a_row_not_on_this_disk_asks_for_no_icon_that_reads_it()
    {
        // A program's icon is read from its file (a path: key): for a placeholder that read would download it.
        var program = new EntryDetail("Application", "path:396bbcd455199596");

        Assert.Equal("ext:.exe", DisplayFormat.IconKeyFor(program, "Setup.EXE", false, ReparsePoint | RecallOnDataAccess));
        Assert.Equal("generic", DisplayFormat.IconKeyFor(new EntryDetail("File", "path:1111222233334444"), "tool", false, Offline));
        // A file on this disk keeps its own icon.
        Assert.Equal("path:396bbcd455199596", DisplayFormat.IconKeyFor(program, "Setup.exe", false, ReparsePoint | Pinned));
        // Keys that read no file stay as they are.
        Assert.Equal("ext:.docx", DisplayFormat.IconKeyFor(new EntryDetail("Microsoft Word Document", "ext:.docx"), "a.docx", false, RecallOnDataAccess));
        Assert.Equal("folder", DisplayFormat.IconKeyFor(new EntryDetail("File folder", "folder"), "cloud", true, Directory | RecallOnOpen));
    }

    [Fact]
    public void A_placeholder_shows_its_logical_size()
    {
        // The listing's size is the file's end, what it holds, not what it takes on this disk (nothing yet).
        using var view = Open(new SyntheticEntry(1, "video.mp4", 1, Size: 734_003_200, Attributes: ReparsePoint | RecallOnDataAccess));

        Assert.Equal("700 MB", DisplayFormat.Size(view.Size(0), view.IsFolder(0), CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(LinkKind.Junction, true, "\u201Cjunction\u201D is a junction. Only the link is deleted; the folder it points to keeps its files.")]
    [InlineData(LinkKind.MountPoint, true, "\u201Cjunction\u201D is a mount point. Only the link is deleted; the volume it shows keeps its files.")]
    [InlineData(LinkKind.SymbolicLink, false, "\u201Cjunction\u201D is a symbolic link. Only the link is deleted; the file it points to stays.")]
    [InlineData(LinkKind.Unknown, true, "\u201Cjunction\u201D is a link. Only the link is deleted; the folder it points to keeps its files.")]
    public void Deleting_a_link_permanently_says_link_and_that_what_it_points_to_stays(LinkKind link, bool isFolder, string first)
    {
        var (title, body) = DeleteText.Permanent([new DeleteTarget("junction", isFolder, link)]);

        Assert.Equal("Delete the link permanently?", title);
        Assert.Equal(first + " The link does not go to the Recycle Bin, and this cannot be undone.", body);
    }

    [Fact]
    public void Deleting_several_rows_permanently_counts_the_links_among_them()
    {
        var (title, body) = DeleteText.Permanent(
        [
            new DeleteTarget("a.txt", false),
            new DeleteTarget("junction", true, LinkKind.Junction),
            new DeleteTarget("docs", true),
        ]);

        Assert.Equal("Delete 3 items permanently?", title);
        Assert.EndsWith("1 of them is a link: only the link is deleted, not what it points to.", body);
        var two = DeleteText.Permanent([new DeleteTarget("j1", true, LinkKind.Junction), new DeleteTarget("j2", true, LinkKind.Unknown)]).Body;
        Assert.EndsWith("Both are links: only the links are deleted, not what they point to.", two);
        // No link: the words as before.
        Assert.Equal(("Delete 1 item permanently?", "\u201Ca.txt\u201D will be deleted for good. It does not go to the Recycle Bin, and this cannot be undone."),
            DeleteText.Permanent([new DeleteTarget("a.txt", false)]));
    }
}
