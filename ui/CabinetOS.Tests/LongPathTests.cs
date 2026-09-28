using CabinetOS.Core.Jobs;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Tests;

/// <summary>
/// Edge cases, class B: paths longer than 260 characters (docs/ui.md, "Edge
/// cases"). The fixture's folder is 333 characters long, with files in it.
/// </summary>
public class LongPathTests
{
    private const string Segment = "segment-of-a-long-path-0123456789";
    private static readonly string Deep = @"C:\Users\me\AppData\Local\Temp\edge\long\" + string.Join('\\', Enumerable.Repeat(Segment, 8));

    [Fact]
    public void The_fixture_path_is_beyond_max_path() => Assert.True(Deep.Length > 300, Deep.Length.ToString());

    [Fact]
    public void A_short_path_is_shown_whole()
    {
        Assert.Equal(@"C:\Users\me\notes.md", DisplayFormat.ShortPath(@"C:\Users\me\notes.md", 60));
        Assert.Equal(@"C:\", DisplayFormat.ShortPath(@"C:\", 2));
    }

    [Fact]
    public void A_long_path_keeps_its_root_and_its_last_names_around_an_ellipsis()
    {
        var file = Deep + @"\deep file.txt";

        var shown = DisplayFormat.ShortPath(file, 60);

        Assert.Equal($@"C:\…\{Segment}\deep file.txt", shown);
        Assert.True(shown.Length <= 60);
        // Room for more keeps more names from the end, never a part of one.
        Assert.Equal($@"C:\…\{Segment}\{Segment}\deep file.txt", DisplayFormat.ShortPath(file, 90));
        // A last name longer than the room is kept whole; the view cuts its end.
        Assert.Equal(@"C:\…\deep file.txt", DisplayFormat.ShortPath(file, 10));
    }

    [Fact]
    public void A_share_keeps_its_server_and_share_as_the_root()
    {
        var path = @"\\server\share\" + string.Join('\\', Enumerable.Repeat(Segment, 9)) + @"\report.pdf";

        Assert.Equal($@"\\server\share\…\{Segment}\report.pdf", DisplayFormat.ShortPath(path, 70));
    }

    [Fact]
    public void Names_beyond_ascii_are_never_cut_inside()
    {
        var emoji = char.ConvertFromUtf32(0x1F4C1) + " photos";
        var path = Deep + $@"\Ґанок\{emoji}\Звіт 2026.txt";

        Assert.Equal($@"C:\…\Ґанок\{emoji}\Звіт 2026.txt", DisplayFormat.ShortPath(path, 40));
    }

    [Fact]
    public void An_extended_length_prefix_is_not_a_server()
    {
        // \\?\ lifts the 260 limit; the crumbs name the drive, not a server called "?".
        Assert.Equal(["C:", "Users", "me"], DisplayFormat.Crumbs(@"\\?\C:\Users\me").Select(c => c.Label));
        Assert.Equal(@"C:\Users", DisplayFormat.Crumbs(@"\\?\C:\Users\me")[1].Path);
        Assert.Equal([@"\\server", "share", "docs"], DisplayFormat.Crumbs(@"\\?\UNC\server\share\docs").Select(c => c.Label));
        Assert.Equal(@"\\server\share", DisplayFormat.Crumbs(@"\\?\UNC\server\share\docs")[1].Path);
    }

    [Fact]
    public void The_flyout_line_keeps_both_ends_of_long_paths()
    {
        var job = new TransferJob(1, 1);
        job.Describe(JobKind.Copy, [Deep + @"\deep file.txt"], @"C:\Users\me\names\Ґанок");

        var line = TransferText.ShortSubtitle(job, 60);

        // The destination fits whole; the source keeps its drive and its file.
        Assert.Equal(@"C:\…\deep file.txt → C:\Users\me\names\Ґанок", line);
        var both = new TransferJob(2, 2);
        both.Describe(JobKind.Move, [Deep + @"\deep file.txt"], Deep + @"\Ґанок");
        Assert.Equal(@"C:\…\deep file.txt → C:\…\Ґанок", TransferText.ShortSubtitle(both, 60));
        // The whole line stays for the tooltip.
        Assert.Equal($@"{Deep}\deep file.txt → C:\Users\me\names\Ґанок", TransferText.Subtitle(job));
    }

    [Theory]
    // Everything fits: every crumb, no ellipsis.
    [InlineData(new double[] { 30, 60, 60 }, 400, true, 1, false)]
    // Too long: the drive, the ellipsis, and the last crumbs that fit.
    [InlineData(new double[] { 30, 200, 200, 200, 80, 120 }, 400, true, 4, true)]
    // Only the last crumb fits beside the ellipsis: the drive goes too.
    [InlineData(new double[] { 30, 200, 380 }, 400, false, 2, true)]
    // The last crumb alone is wider than the bar: it is still the one shown.
    [InlineData(new double[] { 30, 200, 900 }, 400, false, 2, true)]
    public void The_crumbs_fit_with_the_last_ones_shown(double[] widths, double available, bool root, int firstTail, bool ellipsis)
    {
        var fit = CrumbFit.Fit(widths, available, separator: 14, ellipsis: 24);

        Assert.Equal((root, firstTail, ellipsis), (fit.ShowRoot, fit.FirstTail, fit.Ellipsis));
    }
}
