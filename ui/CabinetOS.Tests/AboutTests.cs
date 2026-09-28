using System.Globalization;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>The About view's facts: release.json as build/release.ps1 writes it, what sits next to the program, and the lines.</summary>
public class AboutTests
{
    // The shape build/release.ps1 writes (its "requires" part is not read).
    private const string ReleaseJson = """
        {
          "product": "CabinetOS",
          "version": "0.1.0",
          "commit": "0123456789abcdef0123456789abcdef01234567",
          "uncommittedChanges": true,
          "builtUtc": "2026-09-28T21:15:00Z",
          "requires": { "windowsBuild": 22621, "webView2": true }
        }
        """;

    [Fact]
    public void Release_json_gives_the_version_the_commit_the_build_time_and_a_dirty_tree()
    {
        var facts = ReleaseFacts.Parse(ReleaseJson)!;

        Assert.Equal(("0.1.0", "0123456789abcdef0123456789abcdef01234567", true), (facts.Version, facts.Commit, facts.UncommittedChanges));
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 21, 15, 0, TimeSpan.Zero), facts.BuiltUtc);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("")]
    public void Anything_but_a_json_object_is_no_release(string text) => Assert.Null(ReleaseFacts.Parse(text));

    [Fact]
    public void Missing_or_odd_fields_are_left_out_not_guessed()
    {
        var facts = ReleaseFacts.Parse("""{"version":7,"commit":"","builtUtc":"yesterday","uncommittedChanges":"yes"}""")!;

        Assert.Equal((null, null, null, false), (facts.Version, facts.Commit, facts.BuiltUtc, facts.UncommittedChanges));
    }

    [Fact]
    public void The_folder_says_what_a_release_brings_and_a_development_build_lacks()
    {
        var folder = Repo.NewTempFolder("about");
        try
        {
            var development = ReleaseFolder.Read(folder);
            Assert.Equal((false, null, null, null), (development.HasReleaseFile, development.Facts, development.LicensePath, development.NoticesPath));

            File.WriteAllText(Path.Combine(folder, ReleaseFacts.FileName), ReleaseJson);
            File.WriteAllText(Path.Combine(folder, "LICENSE"), "MIT");
            File.WriteAllText(Path.Combine(folder, "THIRD-PARTY-NOTICES.md"), "# Notices");
            var release = ReleaseFolder.Read(folder);
            Assert.True(release.HasReleaseFile);
            Assert.Equal("0.1.0", release.Facts!.Version);
            Assert.Equal((Path.Combine(folder, "LICENSE"), Path.Combine(folder, "THIRD-PARTY-NOTICES.md")), (release.LicensePath, release.NoticesPath));

            // A damaged file is there but says nothing: the view says it could not be read.
            File.WriteAllText(Path.Combine(folder, ReleaseFacts.FileName), "{\"version\":");
            var damaged = ReleaseFolder.Read(folder);
            Assert.Equal((true, null), (damaged.HasReleaseFile, damaged.Facts));
        }
        finally
        {
            Repo.RemoveTempFolder(folder);
        }
    }

    [Fact]
    public void The_lines_name_the_versions_and_the_build_or_say_what_is_missing()
    {
        var culture = CultureInfo.InvariantCulture;
        var core = new PongReply(11, "0.1.0");
        var release = new ReleaseFolder(true, ReleaseFacts.Parse(ReleaseJson), @"C:\CabinetOS\LICENSE", @"C:\CabinetOS\THIRD-PARTY-NOTICES.md");
        var built = new DateTimeOffset(2026, 9, 28, 21, 15, 0, TimeSpan.Zero).ToLocalTime().ToString("g", culture);

        Assert.Equal(
            [("Version", "0.1.0"), ("Core", "0.1.0, protocol 11"), ("Build", $"commit 0123456789, built {built}, with uncommitted changes")],
            AboutText.Rows("0.1.0", core, release, culture));

        var development = new ReleaseFolder(false, null, null, null);
        Assert.Equal(("Core", "not reachable"), AboutText.Rows("0.1.0", null, development, culture)[1]);
        Assert.Equal("Development build (no release.json next to CabinetOS.exe)", AboutText.Rows("0.1.0", core, development, culture)[2].Value);
        Assert.StartsWith("LICENSE and THIRD-PARTY-NOTICES.md come with a release", AboutText.MissingDocuments(development));
        Assert.Equal("release.json next to CabinetOS.exe could not be read", AboutText.Rows("0.1.0", core, new ReleaseFolder(true, null, null, null), culture)[2].Value);
        Assert.Equal("LICENSE or THIRD-PARTY-NOTICES.md is missing next to CabinetOS.exe.", AboutText.MissingDocuments(new ReleaseFolder(true, null, null, null)));
    }
}
