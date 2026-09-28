using System.Globalization;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Edge cases, class A: names beyond ASCII (docs/ui.md, "Edge cases"). The
/// fixture's names: Ukrainian and Russian letters, Japanese, Chinese, an
/// emoji, surrogate pairs, café in NFC and in NFD, right-to-left, a
/// 255-unit name, and names that differ only by case.
/// </summary>
[Collection(HandleTests.Name)]
public class NamesBeyondAsciiTests
{
    private const string Nfc = "caf\u00E9.txt";
    private const string Nfd = "cafe\u0301.txt";
    private static readonly string Fraktur = string.Concat(new[] { 0x1D518, 0x1D52B, 0x1D526, 0x1D520, 0x1D52C, 0x1D521, 0x1D522 }.Select(char.ConvertFromUtf32)) + ".txt";
    private static readonly string Emoji = char.ConvertFromUtf32(0x1F4C1) + " photos";
    private static readonly string Longest = new string('a', 251) + ".txt";

    private static readonly string[] Names =
    [
        "case", "Ґанок", Emoji, "中文文件夹", Longest, Nfc, Nfd, "Звіт 2026.txt", "Їжак і Єнот.md", "مستند.txt", Fraktur, "日本語のファイル.txt",
        "Report.txt", "report.txt",
    ];

    private static ListingView Open(IReadOnlyList<string> names, out byte[] bytes)
    {
        bytes = TestSections.Build(names.Select((name, i) => new SyntheticEntry((ulong)i + 1, name, i < 4 ? (byte)2 : (byte)1)).ToList());
        return ListingView.Open(new SectionHandle(TestSections.CreateSection(bytes)), (ulong)bytes.Length);
    }

    [Fact]
    public void Every_name_comes_back_from_the_listing_unit_for_unit_in_the_cores_order()
    {
        using var view = Open(Names, out _);

        // The shell shows the core's order as it is (natural order, folders first); it never sorts.
        Assert.Equal(Names, Enumerable.Range(0, view.Count).Select(i => view.NameSpan(i).ToString()));
        Assert.Equal(255, view.NameSpan(4).Length);
        Assert.Equal(18, view.NameSpan(10).Length);
        Assert.True(char.IsSurrogatePair(view.NameSpan(10)[0], view.NameSpan(10)[1]));
    }

    [Fact]
    public void An_exact_name_is_found_before_one_that_differs_only_by_case()
    {
        // A case-sensitive folder can hold both; the rename and new-folder follow-ups select by name.
        using var view = Open(Names, out _);

        Assert.Equal(13, view.IndexOfName("report.txt"));
        Assert.Equal(12, view.IndexOfName("Report.txt"));
        // Without an exact name, any case (a case-insensitive folder shows the name as it is stored).
        Assert.Equal(12, view.IndexOfName("REPORT.TXT"));
        Assert.Equal(-1, view.IndexOfName("report.md"));
    }

    [Fact]
    public void Café_in_NFC_and_in_NFD_are_two_names_side_by_side()
    {
        using var view = Open(Names, out _);

        Assert.Equal(5, view.IndexOfName(Nfc));
        Assert.Equal(6, view.IndexOfName(Nfd));
        Assert.NotEqual(Nfc, Nfd);
    }

    [Theory]
    [InlineData("Їжак і Єнот.md", "Їжак і Єнот")]
    [InlineData("Звіт 2026.txt", "Звіт 2026")]
    [InlineData("cafe\u0301.txt", "cafe\u0301")]
    [InlineData("مستند.txt", "مستند")]
    [InlineData("archive.tar.gz", "archive.tar")]
    [InlineData(".gitignore", ".gitignore")]
    [InlineData("README", "README")]
    [InlineData("ends with a dot.", "ends with a dot")]
    public void Rename_selects_the_stem_without_the_extension(string name, string stem)
    {
        Assert.Equal(stem.Length, DisplayFormat.RenameStem(name, isFolder: false));
        Assert.Equal(name.Length, DisplayFormat.RenameStem(name, isFolder: true));
    }

    [Fact]
    public void Rename_never_splits_a_surrogate_pair()
    {
        var stem = DisplayFormat.RenameStem(Fraktur, isFolder: false);

        Assert.Equal(14, stem);
        Assert.False(char.IsHighSurrogate(Fraktur[stem - 1]));
        Assert.Equal(Emoji.Length, DisplayFormat.RenameStem(Emoji, isFolder: false));
        Assert.Equal(251, DisplayFormat.RenameStem(Longest, isFolder: false));
    }

    [Fact]
    public void The_crumbs_of_a_path_beyond_ascii_keep_every_name_whole()
    {
        var path = $@"C:\Users\me\names\中文文件夹\{Emoji}\Звіт";

        Assert.Equal(["C:", "Users", "me", "names", "中文文件夹", Emoji, "Звіт"], DisplayFormat.Crumbs(path).Select(c => c.Label));
        Assert.Equal($@"C:\Users\me\names\中文文件夹\{Emoji}", DisplayFormat.Crumbs(path)[5].Path);
        Assert.Equal("Звіт", DisplayFormat.FolderName(path));
    }

    [Fact]
    public void Properties_names_the_type_the_type_column_shows()
    {
        using var view = Open(Names, out _);
        var culture = CultureInfo.InvariantCulture;
        var markdown = new EntryDetail("Markdown Source File", "ext:.md");

        var rows = PropertiesText.ForEntry(view, 8, @"C:\names", markdown, culture);
        Assert.Equal(("Type", "Markdown Source File"), rows[0]);
        Assert.Equal(("Location", @"C:\names"), rows[1]);

        // Before the shell's name arrives: the built-in one, as the Type column shows it then.
        Assert.Equal(("Type", "MD File"), PropertiesText.ForEntry(view, 8, @"C:\names", null, culture)[0]);
        Assert.Equal(("Type", "Folder"), PropertiesText.ForEntry(view, 1, @"C:\names", null, culture)[0]);
    }
}
