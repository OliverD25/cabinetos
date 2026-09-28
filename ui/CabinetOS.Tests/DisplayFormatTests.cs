using System.Globalization;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Presentation;

namespace CabinetOS.Tests;

public class DisplayFormatTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0UL, "0 B")]
    [InlineData(1000UL, "1000 B")]
    [InlineData(1025UL, "2 KB")]
    [InlineData(18_432UL, "18 KB")]
    [InlineData(1_572_864UL, "1.5 MB")]
    [InlineData(126_701_535_232UL, "118 GB")]
    [InlineData(1_539_316_278_886UL, "1.4 TB")]
    public void Sizes_read_like_the_design(ulong bytes, string text) => Assert.Equal(text, DisplayFormat.Size(bytes, false, Invariant));

    [Fact]
    public void Folders_show_no_size() => Assert.Equal("", DisplayFormat.Size(4096, true, Invariant));

    [Fact]
    public void Times_are_relative_within_a_week()
    {
        var now = new DateTime(2026, 9, 28, 15, 0, 0, DateTimeKind.Local);
        string Format(DateTime local) => DisplayFormat.Modified(local.ToUniversalTime(), now, Invariant);
        Assert.Equal("Today 09:11", Format(new DateTime(2026, 9, 28, 9, 11, 0, DateTimeKind.Local)));
        Assert.Equal("Yesterday 16:20", Format(new DateTime(2026, 9, 27, 16, 20, 0, DateTimeKind.Local)));
        Assert.Equal("Thu 13:40", Format(new DateTime(2026, 9, 24, 13, 40, 0, DateTimeKind.Local)));
        Assert.Equal("09/01/2026 08:00", Format(new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Local)));
        Assert.Equal("", DisplayFormat.Modified(DateTime.MinValue, now, Invariant));
    }

    [Theory]
    [InlineData("report.md", EntryKind.File, false, "MD File")]
    [InlineData("archive.tar.gz", EntryKind.File, false, "GZ File")]
    [InlineData("Makefile", EntryKind.File, false, "File")]
    [InlineData(".gitignore", EntryKind.File, false, "File")]
    [InlineData("trailing.", EntryKind.File, false, "File")]
    [InlineData("src", EntryKind.Directory, true, "Folder")]
    [InlineData("repo", EntryKind.Link, true, "Folder link")]
    [InlineData("tool.exe", EntryKind.Link, false, "Link")]
    public void The_type_column_names_folders_links_and_extensions(string name, EntryKind kind, bool isFolder, string text) =>
        Assert.Equal(text, DisplayFormat.TypeText(name, kind, isFolder));

    [Fact]
    public void Paths_split_into_crumbs_and_parents()
    {
        Assert.Equal([("C:", @"C:\"), ("Users", @"C:\Users"), ("me", @"C:\Users\me")], DisplayFormat.Crumbs(@"C:\Users\me"));
        Assert.Equal([("C:", @"C:\")], DisplayFormat.Crumbs(@"C:\"));
        Assert.Equal([(@"\\nas", @"\\nas"), ("media", @"\\nas\media"), ("films", @"\\nas\media\films")], DisplayFormat.Crumbs(@"\\nas\media\films"));

        Assert.Equal(@"C:\Users", DisplayFormat.Parent(@"C:\Users\me"));
        Assert.Equal(@"C:\", DisplayFormat.Parent(@"C:\Users"));
        Assert.Null(DisplayFormat.Parent(@"C:\"));
        Assert.Equal(@"\\nas\media", DisplayFormat.Parent(@"\\nas\media\films"));
        Assert.Null(DisplayFormat.Parent(@"\\nas\media"));

        Assert.Equal(@"C:\Users", DisplayFormat.Join(@"C:\", "Users"));
        Assert.Equal(@"C:\Users\me", DisplayFormat.Join(@"C:\Users", "me"));
        Assert.Equal("me", DisplayFormat.FolderName(@"C:\Users\me\"));
        Assert.Equal("C:", DisplayFormat.FolderName(@"C:\"));
    }

    [Fact]
    public void Drives_read_like_the_design()
    {
        Assert.Equal("Local Disk (C:)", DisplayFormat.DriveName("C", ""));
        Assert.Equal("Data (D:)", DisplayFormat.DriveName("D", "Data"));
        Assert.Equal("118 GB", DisplayFormat.Bytes(126_701_535_232UL, Invariant));
    }
}
