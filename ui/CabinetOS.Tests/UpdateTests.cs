using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Shell;
using CabinetOS.Core.Updates;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// In-app updates in the window (Phase 17; docs/ui.md, "Updates"): the release notes as the
/// dialog renders them, the dialog, the pill, the snooze rule, the dot in the menu and in About,
/// and the update messages on the wire.
/// </summary>
public class UpdateTests
{
    private const string Base = "https://github.com/OliverD25/cabinetos/blob/v0.2.0/";

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static UpdateRelease Release(string version = "0.2.0") => new(
        1,
        "stable",
        version,
        "2026-10-01",
        new UpdateZip($"https://github.com/OliverD25/cabinetos/releases/download/v{version}/CabinetOS-{version}-win-x64.zip", new string('a', 64), 80_000_000),
        new UpdateNotes($"https://oliverd25.github.io/cabinetos-marketplace/update/stable/notes-{version}.md"),
        new UpdateRequires("2.5", "10.0"));

    private static UpdateStatus Status(string state, string? notes = "### Added\n\n- Updates from inside the app.", ulong? snoozedUntil = null) => new(
        state,
        "0.1.0",
        "stable",
        Latest: Release(),
        NotesUrl: Release().Notes.Url,
        Notes: notes,
        SnoozedUntilMs: snoozedUntil,
        InstallDir: @"C:\Users\me\AppData\Local\Programs\CabinetOS");

    // ----- The release notes -----

    /// <summary>
    /// The sections of CHANGELOG.md, newest first, each by its name ("Unreleased", "0.1.0") and without the heading
    /// links at the end of the file, as build/release.ps1 cuts one for notes-&lt;version&gt;.md.
    /// </summary>
    private static List<(string Name, string Text)> Sections()
    {
        var changelog = File.ReadAllText(Path.Combine(Repo.Root, "CHANGELOG.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        return [.. Regex.Matches(changelog, @"(?ms)^## \[(Unreleased|\d+\.\d+\.\d+[^\]]*)\][^\n]*\n(.*?)(?=^## \[|\z)")
            .Select(m => (m.Groups[1].Value,
                Regex.Replace(m.Groups[2].Value.Trim(), @"(?m)^\[(Unreleased|\d+\.\d+\.\d+[^\]]*)\]:[ \t]+\S+[ \t]*$", "").Trim()))];
    }

    /// <summary>
    /// The notes the next release would publish, and those of 0.1.0: every heading, item, link and code span of each
    /// renders. The newest section with content is Unreleased while it holds anything, else the newest version's.
    /// </summary>
    [Fact]
    public void The_changelog_s_own_section_renders_with_every_heading_item_link_and_code_span()
    {
        var sections = Sections();
        var newest = sections.FirstOrDefault(s => s.Text.Length > 0);
        Assert.True(newest.Text is not null, "CHANGELOG.md has no section with content");
        var first = sections.Single(s => s.Name == "0.1.0");
        foreach (var (name, section) in new[] { newest, first }.Distinct())
        {
            var blocks = ReleaseNotes.Parse(section, ReleaseNotes.BaseFor(name == "Unreleased" ? "0.1.0" : name));
            var spans = blocks.SelectMany(b => b.Spans).ToList();
            var lines = section.Split('\n');

            // Each "### " line is a level-3 heading, each "- " line one item of the outer list, in order.
            Assert.Equal(lines.Where(l => l.StartsWith("### ", StringComparison.Ordinal)).Select(l => l[4..]),
                blocks.Where(b => b.Kind == NoteBlockKind.Heading).Select(b => b.PlainText));
            Assert.All(blocks.Where(b => b.Kind == NoteBlockKind.Heading), b => Assert.Equal(3, b.Level));
            Assert.Equal(lines.Count(l => l.StartsWith("- ", StringComparison.Ordinal)),
                blocks.Count(b => b.Kind == NoteBlockKind.ListItem && b.Level == 0 && b.Marker == "•"));
            Assert.All(spans.Where(s => s.Kind == NoteSpanKind.Link), s => Assert.StartsWith("https://", s.Url));
            // No markup is left in the text: a backtick only where the section escapes one ("Ctrl+\`"), and the heading
            // links at the end of the file are no block.
            var text = spans.Where(s => s.Kind is NoteSpanKind.Text or NoteSpanKind.Bold).ToList();
            Assert.Equal(Regex.Count(section, @"\\`"), text.Sum(s => s.Text.Count(c => c == '`')));
            Assert.All(text, s =>
            {
                Assert.DoesNotContain("](", s.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("**", s.Text, StringComparison.Ordinal);
            });
            Assert.DoesNotContain(blocks, b => b.PlainText.StartsWith("[Unreleased]", StringComparison.Ordinal));
            if (name != "0.1.0")
            {
                continue;
            }
            // 0.1.0 opens with a paragraph whose relative link points into the repository at the version's tag.
            Assert.Equal(NoteBlockKind.Paragraph, blocks[0].Kind);
            Assert.Contains(spans, s => s is { Kind: NoteSpanKind.Link, Text: "docs/release.md", Url: "https://github.com/OliverD25/cabinetos/blob/v0.1.0/docs/release.md" });
            Assert.Contains(spans, s => s is { Kind: NoteSpanKind.Code, Text: "cabinetos.json" });
            Assert.Contains(spans, s => s is { Kind: NoteSpanKind.Code, Text: "followsPane: false" });
        }
    }

    [Fact]
    public void Headings_lists_bold_links_and_code_spans_become_blocks_and_spans()
    {
        var blocks = ReleaseNotes.Parse("""
            ## 0.2.0 - 2026-10-01

            An update **from inside** the app, see [the guide](docs/release.md "Release").

            ### Added
            - `cabinetos-cli update` checks and downloads,
              and applies.
              - nested under it
                * and deeper
            - back *out* <https://example.org/a>
            1. first
            2) second

            ---
            ```
            cabinetos-cli update --json
              indented
            ```
            [0.2.0]: https://github.com/OliverD25/cabinetos/releases/tag/v0.2.0
            """, Base);

        Assert.Equal(
            [
                (NoteBlockKind.Heading, 2, "", "0.2.0 - 2026-10-01"),
                (NoteBlockKind.Paragraph, 0, "", "An update from inside the app, see the guide."),
                (NoteBlockKind.Heading, 3, "", "Added"),
                (NoteBlockKind.ListItem, 0, "•", "cabinetos-cli update checks and downloads, and applies."),
                (NoteBlockKind.ListItem, 1, "•", "nested under it"),
                (NoteBlockKind.ListItem, 2, "•", "and deeper"),
                (NoteBlockKind.ListItem, 0, "•", "back out https://example.org/a"),
                (NoteBlockKind.ListItem, 0, "1.", "first"),
                (NoteBlockKind.ListItem, 0, "2.", "second"),
                (NoteBlockKind.Code, 0, "", "cabinetos-cli update --json\n  indented"),
            ],
            blocks.Select(b => (b.Kind, b.Level, b.Marker, b.PlainText)));

        Assert.Equal(
            [
                new NoteSpan(NoteSpanKind.Text, "An update "),
                new NoteSpan(NoteSpanKind.Bold, "from inside"),
                new NoteSpan(NoteSpanKind.Text, " the app, see "),
                new NoteSpan(NoteSpanKind.Link, "the guide", Base + "docs/release.md"),
                new NoteSpan(NoteSpanKind.Text, "."),
            ],
            blocks[1].Spans);
        Assert.Equal(new NoteSpan(NoteSpanKind.Code, "cabinetos-cli update"), blocks[3].Spans[0]);
        Assert.Equal(
            [
                new NoteSpan(NoteSpanKind.Text, "back "),
                new NoteSpan(NoteSpanKind.Italic, "out"),
                new NoteSpan(NoteSpanKind.Text, " "),
                new NoteSpan(NoteSpanKind.Link, "https://example.org/a", "https://example.org/a"),
            ],
            blocks[6].Spans);
    }

    [Theory]
    [InlineData("[page](https://example.org/x?a=1)", "https://example.org/x?a=1")]
    [InlineData("[doc](docs/ui.md#updates)", Base + "docs/ui.md#updates")]
    [InlineData("[up](../README.md)", "https://github.com/OliverD25/cabinetos/blob/README.md")]
    [InlineData("[anchor](#added)", null)]
    [InlineData("[script](javascript:alert(1))", null)]
    [InlineData("[file](file:///C:/Windows/notepad.exe)", null)]
    [InlineData("[mail](mailto:someone@example.org)", null)]
    [InlineData("[drive](C:\\Windows\\notepad.exe)", null)]
    public void A_link_opens_only_a_web_page_and_relative_ones_resolve_against_the_tag(string markdown, string? url)
    {
        var span = Assert.Single(ReleaseNotes.Inlines(markdown, Base));
        if (url is null)
        {
            // Shown as its text, not clickable.
            Assert.Equal(NoteSpanKind.Text, span.Kind);
            Assert.Null(span.Url);
        }
        else
        {
            Assert.Equal((NoteSpanKind.Link, url), (span.Kind, span.Url));
        }
    }

    [Fact]
    public void Underscores_escapes_and_unclosed_marks_stay_as_written()
    {
        static string Plain(string markdown) => string.Concat(ReleaseNotes.Inlines(markdown, Base).Select(s => $"{s.Kind}:{s.Text}|"));

        Assert.Equal("Text:update_status and __init__ stay|", Plain("update_status and __init__ stay"));
        Assert.Equal("Text:a *literal* star|", Plain(@"a \*literal\* star"));
        Assert.Equal("Text:2 * 3 and **open|", Plain("2 * 3 and **open"));
        Assert.Equal("Text:a `tick and [x] and [y](|", Plain("a `tick and [x] and [y]("));
        Assert.Equal("Code:a ` b|", Plain("`` a ` b ``"));
        Assert.Equal("Bold:bold |Code:code|", Plain("**bold `code`**"));
    }

    // ----- The dialog -----

    [Fact]
    public void A_downloaded_version_s_dialog_has_its_notes_Restart_now_and_Later_which_snoozes()
    {
        var view = UpdateText.Dialog(Status(UpdatePhases.Downloaded))!;

        Assert.Equal(("0.2.0", "CabinetOS 0.2.0 is ready"), (view.Version, view.Title));
        Assert.Equal("You have 0.1.0. Published 2026-10-01 on the stable channel.", view.Subtitle);
        Assert.Equal(["Added", "Updates from inside the app."], view.Notes.Select(b => b.PlainText));
        Assert.Null(view.NotesMissing);
        Assert.Equal(("Restart now", "Later", true), (view.RestartText, view.CloseText, view.SnoozesOnClose));
        Assert.Equal("https://github.com/OliverD25/cabinetos/blob/v0.2.0/CHANGELOG.md", view.ChangelogUrl);
    }

    [Fact]
    public void Before_the_download_the_dialog_only_shows_the_notes_and_closing_snoozes_nothing()
    {
        var available = UpdateText.Dialog(Status(UpdatePhases.Available))!;
        Assert.Equal(("CabinetOS 0.2.0 is available", null, "Close", false), (available.Title, available.RestartText, available.CloseText, available.SnoozesOnClose));

        // Swapped in already (by the command line or another window): a restart runs it, and nothing is left to snooze.
        var ready = UpdateText.Dialog(Status(UpdatePhases.Ready) with { Installed = "0.2.0" })!;
        Assert.Equal(("CabinetOS 0.2.0 is ready", "Restart now", false), (ready.Title, ready.RestartText, ready.SnoozesOnClose));
    }

    [Fact]
    public void Notes_the_core_could_not_read_and_a_rollback_s_version_point_to_the_changelog()
    {
        var unread = UpdateText.Dialog(Status(UpdatePhases.Downloaded, notes: null))!;
        Assert.Empty(unread.Notes);
        Assert.Equal("The release notes could not be read.", unread.NotesMissing);
        Assert.Equal("https://oliverd25.github.io/cabinetos-marketplace/update/stable/notes-0.2.0.md", unread.NotesUrl);

        var rolledBack = UpdateText.Dialog(Status(UpdatePhases.Ready) with { Installed = "0.1.5" })!;
        Assert.Equal(("0.1.5", "CabinetOS 0.1.5 is ready"), (rolledBack.Version, rolledBack.Title));
        Assert.Empty(rolledBack.Notes);
        Assert.Equal("The release notes of 0.1.5 are in the changelog.", rolledBack.NotesMissing);
        Assert.Equal("https://github.com/OliverD25/cabinetos/blob/v0.1.5/CHANGELOG.md", rolledBack.ChangelogUrl);

        Assert.Null(UpdateText.Dialog(new UpdateStatus(UpdatePhases.UpToDate, "0.1.0", "stable")));
        Assert.Null(UpdateText.Dialog(null));
    }

    // ----- The snooze rule -----

    [Fact]
    public void The_dialog_opens_by_itself_once_per_downloaded_version_and_not_while_snoozed()
    {
        var now = (ulong)Now.ToUnixTimeMilliseconds();

        Assert.True(UpdateText.OpensDialog(Status(UpdatePhases.Downloaded), Now, shownFor: null));
        // Later a day ago: the snooze is over.
        Assert.True(UpdateText.OpensDialog(Status(UpdatePhases.Downloaded, snoozedUntil: now - 1), Now, null));
        Assert.True(UpdateText.OpensDialog(Status(UpdatePhases.Downloaded, snoozedUntil: now), Now, null));
        Assert.False(UpdateText.OpensDialog(Status(UpdatePhases.Downloaded, snoozedUntil: now + 1), Now, null));
        // Once per version in a run; a newer version opens it again.
        Assert.False(UpdateText.OpensDialog(Status(UpdatePhases.Downloaded), Now, shownFor: "0.2.0"));
        Assert.True(UpdateText.OpensDialog(Status(UpdatePhases.Downloaded), Now, shownFor: "0.1.9"));
        // Only a finished download: not while it runs, not after a swap, not without a release.
        foreach (var state in new[] { UpdatePhases.Available, UpdatePhases.Downloading, UpdatePhases.Ready, UpdatePhases.Failed, UpdatePhases.UpToDate })
        {
            Assert.False(UpdateText.OpensDialog(Status(state), Now, null), state);
        }
        Assert.False(UpdateText.OpensDialog(Status(UpdatePhases.Downloaded) with { Latest = null }, Now, null));
        Assert.False(UpdateText.OpensDialog(null, Now, null));
    }

    [Fact]
    public void The_model_remembers_the_shown_version_and_keeps_progress_only_while_downloading()
    {
        var model = new UpdateModel { AutoInstall = false };
        model.Apply(Status(UpdatePhases.Downloaded));
        Assert.True(model.OpensDialog(Now));
        model.MarkDialogShown("0.2.0");
        Assert.False(model.OpensDialog(Now));

        model.Apply(Status(UpdatePhases.Downloading));
        model.Apply(new UpdateProgressEvent("0.2.0", 10, 80_000_000, 5));
        Assert.NotNull(model.Progress);
        model.Apply(Status(UpdatePhases.Downloaded));
        Assert.Null(model.Progress);
        Assert.Equal("0.2.0", model.WaitingVersion);

        model.Forget();
        Assert.Null(model.Status);
        Assert.False(model.Pill().Visible);
    }

    // ----- update.autoInstall and the notice (ADR 0018) -----

    [Fact]
    public void Auto_install_is_on_unless_the_configuration_says_false()
    {
        static bool Read(string json) => UpdateText.AutoInstallFrom(System.Text.Json.JsonDocument.Parse(json).RootElement);

        Assert.True(Read("{}"));
        Assert.True(Read("""{"update":{"check":false}}"""));
        Assert.True(Read("""{"update":{"autoInstall":true}}"""));
        Assert.False(Read("""{"update":{"autoInstall":false}}"""));
        Assert.True(Read("""{"update":{"autoInstall":"no"}}"""), "not a boolean: the default");
        Assert.True(Read("""{"update":true}"""));
    }

    [Fact]
    public void With_auto_install_the_dialog_never_opens_by_itself()
    {
        var model = new UpdateModel();
        Assert.True(model.AutoInstall, "the default until the configuration is read");
        model.Apply(Status(UpdatePhases.Downloaded));
        Assert.False(model.OpensDialog(Now));
        model.AutoInstall = false;
        Assert.True(model.OpensDialog(Now));
    }

    [Fact]
    public void A_version_swapped_in_by_itself_shows_the_notice_until_Later_and_then_the_pill()
    {
        var invariant = CultureInfo.InvariantCulture;
        var model = new UpdateModel();
        model.Apply(Status(UpdatePhases.Downloading));
        Assert.Null(model.Notice);
        model.Apply(Status(UpdatePhases.Applying));
        Assert.Null(model.Notice);
        Assert.Equal("Update · installing", model.Pill(invariant).Text);

        var ready = Status(UpdatePhases.Ready) with { Installed = "0.2.0", Previous = "0.1.0" };
        model.Apply(ready);
        var notice = Assert.IsType<UpdateNoticeView>(model.Notice);
        Assert.Equal(("CabinetOS 0.2.0 is installed; restart to use it", false, "0.2.0"), (notice.Text, notice.Failed, notice.Version));
        Assert.Equal("CabinetOS 0.1.0 runs until the restart. Click for the release notes.", notice.ToolTip);
        Assert.False(model.Pill(invariant).Visible, "the notice holds Restart now");
        Assert.Equal("0.2.0", model.WaitingVersion);

        // Later: the session runs on, the pill offers the restart, and the same state brings no notice back.
        model.CloseNotice();
        Assert.Null(model.Notice);
        Assert.Equal("Update ready \u00b7 Restart", model.Pill(invariant).Text);
        model.Apply(ready);
        Assert.Null(model.Notice);
        Assert.Equal("0.2.0", model.WaitingVersion);
        // Another version in place later (a second update in a long session) is a new notice.
        model.Apply(ready with { Installed = "0.3.0" });
        Assert.Equal("CabinetOS 0.3.0 is installed; restart to use it", model.Notice?.Text);

        // A rollback to the version that runs leaves nothing to restart into.
        Assert.Null(UpdateText.Notice(UpdatePhases.Applying, ready with { Installed = "0.1.0" }, null));
        // A swap made by cabinetos-cli update apply or another window: the same notice, also at a window's start.
        Assert.Equal("installed 0.2.0", UpdateText.Notice(null, ready, null)?.Key);
    }

    [Fact]
    public void A_failed_swap_says_so_in_the_notice_and_a_failed_check_does_not()
    {
        var model = new UpdateModel();
        model.Apply(Status(UpdatePhases.Applying));
        var failed = Status(UpdatePhases.Failed) with { Message = @"cannot move C:\x\cabinetos-core.exe; nothing was changed" };
        model.Apply(failed);
        var notice = Assert.IsType<UpdateNoticeView>(model.Notice);
        Assert.Equal(("CabinetOS 0.2.0 could not be installed; 0.1.0 keeps running", true), (notice.Text, notice.Failed));
        Assert.Equal(@"cannot move C:\x\cabinetos-core.exe; nothing was changed", notice.ToolTip);
        Assert.False(model.Pill().Visible);
        // The same state again (update_status after the event) keeps it; the next step clears it.
        model.Apply(failed);
        Assert.Same(notice, model.Notice);
        model.Apply(Status(UpdatePhases.Checking));
        Assert.Null(model.Notice);
        model.Apply(failed with { Message = "offline" });
        Assert.Null(model.Notice);

        // Close works as Later does.
        model.Apply(Status(UpdatePhases.Applying));
        model.Apply(failed);
        model.CloseNotice();
        model.Apply(failed);
        Assert.Null(model.Notice);
        model.Forget();
        Assert.Null(model.Notice);
    }

    // ----- The pill, the dot and About -----

    [Fact]
    public void The_pill_shows_percent_and_speed_while_downloading_and_Update_ready_after()
    {
        var invariant = CultureInfo.InvariantCulture;
        var downloading = Status(UpdatePhases.Downloading);

        var start = UpdateText.Pill(downloading, null, invariant);
        Assert.Equal((true, "Update · 0%", true, false), (start.Visible, start.Text, start.ShowsTrack, start.Restarts));

        var half = UpdateText.Pill(downloading, new UpdateProgressEvent("0.2.0", 36_000_000, 80_000_000, 4_404_019), invariant);
        Assert.Equal("Update · 45% · 4.2 MB/s", half.Text);
        Assert.Equal(0.45, half.Fraction, 3);
        Assert.Equal("Downloading CabinetOS 0.2.0: 34.3 MB of 76.3 MB", half.ToolTip);
        // A progress of another version (a download before it) is not this one's.
        Assert.Equal("Update · 0%", UpdateText.Pill(downloading, new UpdateProgressEvent("0.1.9", 1, 2, 3), invariant).Text);

        var ready = UpdateText.Pill(Status(UpdatePhases.Downloaded), null, invariant);
        Assert.Equal((true, "Update ready · Restart", false, true), (ready.Visible, ready.Text, ready.ShowsTrack, ready.Restarts));
        Assert.Equal("CabinetOS 0.2.0 is ready. Click to restart into it.", ready.ToolTip);
        Assert.True(UpdateText.Pill(Status(UpdatePhases.Ready) with { Installed = "0.2.0" }, null, invariant).Restarts);
        Assert.False(UpdateText.Pill(Status(UpdatePhases.Applying), null, invariant).Restarts);

        foreach (var state in new[] { UpdatePhases.NotUpdatable, UpdatePhases.Unchecked, UpdatePhases.UpToDate, UpdatePhases.Checking, UpdatePhases.Available, UpdatePhases.Failed })
        {
            Assert.False(UpdateText.Pill(Status(state), null, invariant).Visible, state);
        }
        Assert.False(UpdateText.Pill(null, null, invariant).Visible);
    }

    [Fact]
    public void The_dot_shows_while_a_version_waits_for_a_restart()
    {
        Assert.Equal("0.2.0", UpdateText.WaitingVersion(Status(UpdatePhases.Downloaded)));
        Assert.Equal("0.2.0", UpdateText.WaitingVersion(Status(UpdatePhases.Ready) with { Installed = "0.2.0" }));
        Assert.Equal("0.1.5", UpdateText.WaitingVersion(Status(UpdatePhases.Ready) with { Installed = "0.1.5" }));
        Assert.Null(UpdateText.WaitingVersion(Status(UpdatePhases.Downloading)));
        Assert.Null(UpdateText.WaitingVersion(Status(UpdatePhases.Available)));
        Assert.Null(UpdateText.WaitingVersion(null));
    }

    [Fact]
    public void About_says_where_the_updater_is()
    {
        var invariant = CultureInfo.InvariantCulture;
        var nowLocal = new DateTime(2026, 10, 1, 14, 0, 0, DateTimeKind.Local);
        UpdateAboutRow Row(UpdateStatus? status, UpdateProgressEvent? progress = null) => UpdateText.AboutRow(status, progress, nowLocal, invariant);

        Assert.Equal(new UpdateAboutRow("0.2.0 is ready: restart to run it", true), Row(Status(UpdatePhases.Downloaded)));
        Assert.Equal(new UpdateAboutRow("Downloading 0.2.0: 45% · 4.2 MB/s", false),
            Row(Status(UpdatePhases.Downloading), new UpdateProgressEvent("0.2.0", 36_000_000, 80_000_000, 4_404_019)));
        Assert.Equal("Not checked yet (stable channel)", Row(new UpdateStatus(UpdatePhases.Unchecked, "0.1.0", "stable")).Text);
        Assert.Equal("a development build: it runs from the source and never updates itself",
            Row(new UpdateStatus(UpdatePhases.NotUpdatable, "0.1.0", "stable", Reason: "a development build: it runs from the source and never updates itself")).Text);
        Assert.StartsWith("Up to date on the preview channel, checked ",
            Row(new UpdateStatus(UpdatePhases.UpToDate, "0.1.0", "preview", CheckedAtMs: (ulong)Now.ToUnixTimeMilliseconds())).Text, StringComparison.Ordinal);
        Assert.Equal("The last step failed: the hash does not match", Row(Status(UpdatePhases.Failed) with { Message = "the hash does not match" }).Text);
        Assert.Equal(new UpdateAboutRow("Not reported by this core", false), Row(null));
    }

    [Fact]
    public void An_explicit_check_says_what_it_found_unless_a_download_follows()
    {
        Assert.Equal("CabinetOS 0.1.0 is the newest version on the stable channel.", UpdateText.CheckedNotice(Status(UpdatePhases.UpToDate)));
        Assert.Equal("The update check failed: offline", UpdateText.CheckedNotice(Status(UpdatePhases.Failed) with { Message = "offline" }));
        Assert.Null(UpdateText.CheckedNotice(Status(UpdatePhases.Available)));
    }

    [Fact]
    public void The_hamburger_offers_Check_for_Updates_and_Restart_to_Update_with_a_dot_while_one_waits()
    {
        static CommandInfo Command(string id, string title) =>
            new(id, "Update", title, [], [], new CommandSource("core", null, null), "ui", null, false);
        var registry = new[] { Command("tab.new", "New Tab"), Command("update.check", "Check for Updates"), Command("update.apply", "Restart to Update") };

        var plain = ShellMenu.Build(registry);
        Assert.Equal(("update.check", "Check for Updates", false), (plain[^1].CommandId, plain[^1].Title, plain[^1].Dot));
        var waiting = ShellMenu.Build(registry, waitingUpdate: "0.2.0");
        Assert.Equal(["tab.new", "update.apply"], waiting.Select(m => m.CommandId));
        Assert.Equal(("Restart to Update (0.2.0)", true), (waiting[^1].Title, waiting[^1].Dot));
        // A core without update.apply keeps Check for Updates in its place.
        Assert.Equal("update.check", ShellMenu.Build(registry.Where(c => c.Id != "update.apply"), "0.2.0")[^1].CommandId);
    }

    // ----- The wire -----

    [Fact]
    public void The_update_messages_decode_and_the_state_s_fields_stand_next_to_id_and_type()
    {
        var state = MessageCodec.Decode(Encoding.UTF8.GetBytes(UpdateStateJson("update_state")));
        Assert.False(state.IsEvent);
        var status = Assert.IsType<UpdateStateReply>(state.Body).Status;
        Assert.Equal((UpdatePhases.Downloaded, "0.1.0", "stable", "0.2.0", 80_000_000UL, "2.5"),
            (status.State, status.Current, status.Channel, status.Latest!.Version, status.Latest.Zip.Size, status.Latest.Requires!.WindowsAppRuntime));
        Assert.Equal(("### Added\n\n- Updates.", 1790000000000UL, "0.0.9", @"C:\Users\me\AppData\Local\Programs\CabinetOS"),
            (status.Notes, status.CheckedAtMs, status.Previous, status.InstallDir));
        Assert.Null(status.SnoozedUntilMs);

        var changed = MessageCodec.Decode(Encoding.UTF8.GetBytes(UpdateStateJson("update_state_changed")));
        Assert.True(changed.IsEvent);
        Assert.Equal(status, Assert.IsType<UpdateStateChangedEvent>(changed.Body).Status);

        var progress = MessageCodec.Decode("""{"type":"update_progress","version":"0.2.0","bytes":4194304,"total":80123456,"bytes_per_second":2097152}"""u8);
        Assert.True(progress.IsEvent);
        Assert.Equal(new UpdateProgressEvent("0.2.0", 4194304, 80123456, 2097152), progress.Body);

        // Written back, the status's fields stand flat, without the reply's own.
        var written = Encoding.UTF8.GetString(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new UpdateStateReply(new UpdateStatus(UpdatePhases.NotUpdatable, "0.1.0", "stable", Reason: "a development build")),
            ProtocolJson.Default.UpdateStateReply));
        Assert.Equal("""{"state":"not_updatable","current":"0.1.0","channel":"stable","reason":"a development build"}""", written);
    }

    internal static string UpdateStateJson(string type) =>
        $$$"""{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"{{{type}}}","state":"downloaded","current":"0.1.0","channel":"stable","latest":{"schemaVersion":1,"channel":"stable","version":"0.2.0","published":"2026-10-01","zip":{"url":"https://github.com/OliverD25/cabinetos/releases/download/v0.2.0/CabinetOS-0.2.0-win-x64.zip","sha256":"{{{new string('b', 64)}}}","size":80000000},"notes":{"url":"https://oliverd25.github.io/cabinetos-marketplace/update/stable/notes-0.2.0.md"},"requires":{"windowsAppRuntime":"2.5","dotnet":"10.0"}},"notes_url":"https://oliverd25.github.io/cabinetos-marketplace/update/stable/notes-0.2.0.md","notes":"### Added\n\n- Updates.","checked_at_ms":1790000000000,"previous":"0.0.9","install_dir":"C:\\Users\\me\\AppData\\Local\\Programs\\CabinetOS"}""";
}
