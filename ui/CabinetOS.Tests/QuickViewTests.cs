using CabinetOS.Core.Keys;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.QuickView;

namespace CabinetOS.Tests;

/// <summary>
/// Quick View's logic without a window (ADR 0023, item W1): the matcher over the core's table, the token state
/// machine, the 120 ms rest after a move, the timeouts, the count of a viewer's stops, the pool of alive viewers, and
/// the key routing of decision 3.
/// </summary>
public class QuickViewTests
{
    private static readonly QuickViewer Fixture = new("quickview-fixture", "Quick View Fixture", "1.0.0", @"C:\tools\quickview-fixture", "quickview.html");
    private static readonly QuickViewer Photos = new("photo-pro", "Photo Pro", "2.0.0", @"C:\tools\photo-pro", "index.html");

    private static QuickViewTable Table() => new(
        [Fixture, Photos],
        [
            new QuickViewKind("*.svg", [], Off: true),
            new QuickViewKind("readme", [Fixture.Id]),
            new QuickViewKind("*.tar.gz", [Photos.Id]),
            new QuickViewKind("*.png", [Fixture.Id, Photos.Id]),
            new QuickViewKind("*.qvtest", [Fixture.Id]),
            new QuickViewKind("*.gz", [Fixture.Id]),
        ]);

    private static QuickViewFile File(string name, bool folder = false) => new($@"C:\data\{name}", name, folder, 1234, new DateTime(2026, 9, 30, 14, 2, 11, DateTimeKind.Utc), 2, 10);

    private sealed class Clock
    {
        public long Now { get; set; } = 1_000_000;
    }

    private static (QuickViewSession Session, Clock Clock) NewSession()
    {
        var clock = new Clock();
        return (new QuickViewSession(() => clock.Now), clock);
    }

    [Fact]
    public void The_table_takes_the_first_kind_that_matches_and_its_first_viewer()
    {
        var table = Table();
        var png = table.Match("Shot.PNG");
        Assert.Equal((Fixture, "*.png", false), (png.Viewer, png.Claim, png.Off));
        Assert.Equal([Fixture, Photos], png.Choices);
        Assert.True(png.ShowsChooser);

        // The order of the table decides: *.tar.gz comes before *.gz; a whole name matches itself without case.
        Assert.Equal(Photos, table.Match("backup.tar.gz").Viewer);
        Assert.Equal([Photos, Fixture], table.Match("backup.tar.gz").Choices);
        Assert.Equal(Fixture, table.Match("README").Viewer);
        Assert.Null(table.Match("README.md").Viewer);
        // "*.png" needs a name before the extension.
        Assert.Equal(QuickViewMatch.Nothing, table.Match(".png"));
        Assert.Equal(QuickViewMatch.Nothing, table.Match("notes.xyz"));
    }

    [Fact]
    public void A_kind_turned_off_has_no_viewer_and_its_chooser_lists_every_viewer()
    {
        var svg = Table().Match("logo.svg");
        Assert.Equal((null, "*.svg", true), (svg.Viewer, svg.Claim, svg.Off));
        Assert.Equal([Fixture, Photos], svg.Choices);
        Assert.True(svg.ShowsChooser);
        Assert.False(Table().Match("a.qvtest").ShowsChooser);
    }

    [Fact]
    public void A_viewer_off_for_the_session_gives_way_to_the_next_of_its_kind()
    {
        Assert.Equal(Photos, Table().Match("a.png", id => id == Fixture.Id).Viewer);
        var off = Table().Match("a.qvtest", id => id == Fixture.Id);
        Assert.Equal(((QuickViewer?)null, Fixture), (off.Viewer, off.OffViewer));
    }

    [Fact]
    public void Space_loads_the_viewer_at_once_and_a_move_waits_120_ms_for_the_arrows_to_rest()
    {
        var (session, clock) = NewSession();
        var first = session.Show(File("a.qvtest"), Table().Match("a.qvtest"), rest: false, keyAt: clock.Now);
        Assert.Equal(QuickViewDue.StartViewer, session.Tick());

        var second = session.Show(File("b.qvtest"), Table().Match("b.qvtest"), rest: true, keyAt: clock.Now);
        Assert.Equal(first + 1, second);
        Assert.Equal(QuickViewDue.Nothing, session.Tick());
        Assert.Equal(120, session.NextDueIn());
        clock.Now += 119;
        Assert.Equal(QuickViewDue.Nothing, session.Tick());
        // A third move inside the 120 ms starts the wait again.
        session.Show(File("c.qvtest"), Table().Match("c.qvtest"), rest: true, keyAt: clock.Now);
        clock.Now += 100;
        Assert.Equal(QuickViewDue.Nothing, session.Tick());
        clock.Now += 20;
        Assert.Equal(QuickViewDue.StartViewer, session.Tick());
    }

    [Fact]
    public void A_late_report_with_an_old_token_is_dropped()
    {
        var (session, clock) = NewSession();
        var old = session.Show(File("a.qvtest"), Table().Match("a.qvtest"), rest: false, keyAt: clock.Now);
        Assert.True(session.OnLoadStarted(old));
        Assert.True(session.OnReady(old));
        var current = session.Show(File("b.qvtest"), Table().Match("b.qvtest"), rest: false, keyAt: clock.Now);

        Assert.False(session.OnShown(old, "4032 × 3024"));
        Assert.False(session.OnFailed(old, "unsupported", "no"));
        Assert.False(session.OnReady(old));
        Assert.False(session.OnKeysGranted(old, ["left"]));
        Assert.False(session.OnThumbnail(old, picture: true, null));
        Assert.Equal((QuickViewPicture.Card, ViewerPhase.Resting), (session.Picture, session.Phase));

        Assert.True(session.OnLoadStarted(current));
        Assert.True(session.OnReady(current));
        Assert.True(session.OnShown(current, "fixture"));
        Assert.Equal((QuickViewPicture.Page, ViewerPhase.Shown, "fixture"), (session.Picture, session.Phase, session.Details));
    }

    [Fact]
    public void The_card_comes_first_then_the_thumbnail_then_the_page()
    {
        var (session, clock) = NewSession();
        var token = session.Show(File("a.png"), Table().Match("a.png"), rest: false, keyAt: clock.Now);
        Assert.Equal(("loading", QuickViewPicture.Card), (session.StateName(), session.Picture));
        clock.Now += 8;
        session.OnRendered(token, QuickViewMoment.Card);
        Assert.True(session.OnThumbnail(token, picture: true, null));
        clock.Now += 30;
        session.OnRendered(token, QuickViewMoment.Thumbnail);
        Assert.Equal(QuickViewPicture.Thumbnail, session.Picture);
        session.OnLoadStarted(token);
        session.OnReady(token);
        Assert.Null(session.TakeLogLine());
        Assert.True(session.OnShown(token, "640 × 480"));
        clock.Now += 300;
        session.OnRendered(token, QuickViewMoment.Page);

        var line = session.TakeLogLine();
        Assert.NotNull(line);
        Assert.Equal((8L, 38L, 338L, "quickview-fixture", true), (line.CardMs, line.ThumbnailMs, line.FullMs, line.Viewer, line.Cold));
        Assert.Null(session.TakeLogLine());

        // The second file of the same viewer is warm.
        session.Show(File("b.png"), Table().Match("b.png"), rest: true, keyAt: clock.Now);
        Assert.False(session.Cold);
    }

    [Fact]
    public void A_page_that_reports_first_keeps_the_thumbnail_from_being_drawn()
    {
        var (session, clock) = NewSession();
        var token = session.Show(File("a.png"), Table().Match("a.png"), rest: false, keyAt: clock.Now);
        session.OnLoadStarted(token);
        session.OnReady(token);
        session.OnShown(token, null);
        Assert.False(session.OnThumbnail(token, picture: true, null));
        Assert.Equal(QuickViewPicture.Page, session.Picture);
        Assert.Equal("after-page", session.Timing.Thumbnail);
    }

    [Fact]
    public void A_file_no_viewer_claims_asks_for_the_offer_and_logs_once_the_thumbnail_answered()
    {
        var (session, clock) = NewSession();
        var token = session.Show(File("notes.xyz"), Table().Match("notes.xyz"), rest: false, keyAt: clock.Now);
        Assert.Equal((ViewerPhase.None, OfferPhase.Asking), (session.Phase, session.Offer));
        Assert.Equal(QuickViewDue.Nothing, session.Tick());
        session.OnRendered(token, QuickViewMoment.Card);
        Assert.Null(session.TakeLogLine());
        Assert.False(session.OnThumbnail(token, picture: false, ThumbnailReasons.None));
        var line = session.TakeLogLine();
        Assert.Equal(("none", "none", (string?)null), (line?.Thumbnail, line?.Full, line?.Viewer));

        Assert.True(session.OnOffer(token, null, OfferReasons.NoItem));
        Assert.Equal(("offer", "No viewer for .xyz files."), (session.StateName(), session.OfferText()));
    }

    [Fact]
    public void The_offer_installs_with_progress_and_a_failure_offers_to_try_again()
    {
        var (session, clock) = NewSession();
        var token = session.Show(File("report.pdf"), Table().Match("report.pdf"), rest: false, keyAt: clock.Now);
        var item = new QuickViewOfferItem("document-viewer", "Document Viewer", "1.0.0", 1_240_000, new MarketAuthor("CabinetOS"), "Shows PDF.");
        Assert.True(session.OnOffer(token, item, null));
        Assert.Equal("No viewer for .pdf files is installed.", session.OfferText());
        Assert.True(session.OnInstallStarted());
        Assert.True(session.OnInstallProgress("document-viewer", 558_000, 1_240_000));
        Assert.Equal("Installing Document Viewer… 45 %", session.OfferText());
        Assert.True(session.OnInstallEnded("document-viewer", ok: false, "the download was cut off."));
        Assert.Equal((OfferPhase.InstallFailed, "The install failed: the download was cut off."), (session.Offer, session.OfferText()));
        Assert.True(session.OnInstallStarted());
    }

    [Fact]
    public void An_offline_catalogue_shows_no_offer_and_a_folder_asks_for_none()
    {
        var (session, clock) = NewSession();
        var token = session.Show(File("notes.xyz"), Table().Match("notes.xyz"), rest: false, keyAt: clock.Now);
        session.OnOffer(token, null, OfferReasons.Offline);
        Assert.Equal(OfferPhase.None, session.Offer);
        Assert.Null(session.OfferText());

        session.Show(File("photos", folder: true), Table().Match("photos"), rest: false, keyAt: clock.Now);
        Assert.Equal((OfferPhase.None, ViewerPhase.None, (QuickViewer?)null), (session.Offer, session.Phase, session.Viewer));
    }

    [Fact]
    public void A_page_without_ready_did_not_start_and_a_ready_that_still_comes_is_taken()
    {
        var (session, clock) = NewSession();
        var token = session.Show(File("a.qvtest"), Table().Match("a.qvtest"), rest: false, keyAt: clock.Now);
        session.OnLoadStarted(token);
        Assert.Equal(3000, session.NextDueIn());
        clock.Now += 3000;
        Assert.Equal(QuickViewDue.Redraw, session.Tick());
        Assert.Equal(("failed", "Quick View Fixture did not start."), (session.StateName(), session.StatusText()));
        Assert.True(session.OnReady(token));
        Assert.Equal(ViewerPhase.Loading, session.Phase);
        Assert.Null(session.StatusText());
    }

    [Fact]
    public void A_page_that_hangs_says_still_loading_at_5_s_and_did_not_finish_at_30_s_and_a_late_shown_still_shows()
    {
        var (session, clock) = NewSession();
        var token = session.Show(File("hang.qvtest"), Table().Match("hang.qvtest"), rest: false, keyAt: clock.Now);
        session.OnLoadStarted(token);
        clock.Now += 200;
        session.OnReady(token);
        clock.Now += 4999;
        Assert.Equal(QuickViewDue.Nothing, session.Tick());
        clock.Now += 1;
        Assert.Equal(QuickViewDue.Redraw, session.Tick());
        Assert.Equal("Still loading…", session.StatusText());
        clock.Now = 1_000_000 + 30_000;
        Assert.Equal(QuickViewDue.Redraw, session.Tick());
        Assert.Equal(("failed", "Quick View Fixture did not finish."), (session.StateName(), session.StatusText()));
        Assert.Equal("failed:not-finished", session.Timing.Full);
        Assert.True(session.OnShown(token, null));
        Assert.Equal(QuickViewPicture.Page, session.Picture);
    }

    [Fact]
    public void A_failure_keeps_the_picture_and_says_why()
    {
        var (session, clock) = NewSession();
        var token = session.Show(File("bad.qvtest"), Table().Match("bad.qvtest"), rest: false, keyAt: clock.Now);
        session.OnLoadStarted(token);
        session.OnReady(token);
        Assert.True(session.OnFailed(token, "unsupported", "The fixture was told to fail."));
        Assert.Equal(QuickViewPicture.Card, session.Picture);
        Assert.Equal("Quick View Fixture cannot show this file. The fixture was told to fail.", session.StatusText());
        session.OnRendered(token, QuickViewMoment.Card);
        Assert.Equal("failed:unsupported", session.TakeLogLine()?.Full);
    }

    [Fact]
    public void Three_stops_within_60_s_turn_the_viewer_off_for_the_session()
    {
        var (session, clock) = NewSession();
        session.Show(File("a.qvtest"), Table().Match("a.qvtest"), rest: false, keyAt: clock.Now);
        Assert.True(session.OnStopped(Fixture.Id));
        Assert.Equal("Quick View Fixture stopped.", session.StatusText());
        clock.Now += 20_000;
        session.Show(File("b.qvtest"), Table().Match("b.qvtest"), rest: false, keyAt: clock.Now);
        session.OnStopped(Fixture.Id);
        clock.Now += 20_000;
        session.Show(File("c.qvtest"), Table().Match("c.qvtest"), rest: false, keyAt: clock.Now);
        Assert.True(session.OnStopped(Fixture.Id));
        Assert.Equal(("off", "Quick View Fixture stopped 3 times. It is off until CabinetOS restarts."), (session.StateName(), session.StatusText()));
        Assert.True(session.IsOff(Fixture.Id));

        session.Show(File("d.qvtest"), Table().Match("d.qvtest", session.IsOff), rest: false, keyAt: clock.Now);
        Assert.Equal((ViewerPhase.Off, (QuickViewer?)null, OfferPhase.None), (session.Phase, session.Viewer, session.Offer));
        Assert.Equal(QuickViewDue.Nothing, session.Tick());
        Assert.StartsWith("Quick View Fixture stopped 3 times.", session.StatusText());
        // A kind with another viewer goes on with that one.
        session.Show(File("e.png"), Table().Match("e.png", session.IsOff), rest: false, keyAt: clock.Now);
        Assert.Equal(Photos, session.Viewer);
    }

    [Fact]
    public void Stops_further_apart_than_60_s_do_not_turn_the_viewer_off()
    {
        var (session, clock) = NewSession();
        for (var i = 0; i < 5; i++)
        {
            session.Show(File($"{i}.qvtest"), Table().Match($"{i}.qvtest"), rest: false, keyAt: clock.Now);
            session.OnStopped(Fixture.Id);
            clock.Now += 31_000;
        }
        Assert.False(session.IsOff(Fixture.Id));
    }

    [Fact]
    public void Moving_on_before_the_outcome_logs_the_file_as_skipped()
    {
        var (session, clock) = NewSession();
        var token = session.Show(File("slow.qvtest"), Table().Match("slow.qvtest"), rest: false, keyAt: clock.Now);
        session.OnRendered(token, QuickViewMoment.Card);
        session.Show(File("b.qvtest"), Table().Match("b.qvtest"), rest: true, keyAt: clock.Now);
        var skipped = session.TakeLogLine();
        Assert.Equal((token, "skipped", "skipped"), (skipped?.Token, skipped?.Full, skipped?.Thumbnail));
        session.Close();
        Assert.Equal((token + 1, "skipped"), (session.TakeLogLine()?.Token, session.Timing.Full));
        Assert.Equal("closed", session.StateName());
    }

    [Fact]
    public void Only_three_viewers_stay_alive_and_the_one_used_longest_ago_goes()
    {
        var pool = new QuickViewPool();
        Assert.Null(pool.Use("a"));
        Assert.Null(pool.Use("b"));
        Assert.Null(pool.Use("c"));
        Assert.Null(pool.Use("a"));
        Assert.Equal("b", pool.Use("d"));
        Assert.Equal(["d", "a", "c"], pool.Alive);
    }

    private static Keymap KeymapOf(params (string Keys, string Command, string? When)[] bindings) => new(1000,
        [.. bindings.Select(b => { Assert.True(KeySequence.TryParse(b.Keys, out var keys)); return new Binding(keys, b.Command, b.When); })],
        new HashSet<string> { "overlay.close", "palette.show" });

    private static Keymap Defaults() => KeymapOf(
        ("space", "quickView.toggle", KeyContexts.FilesView),
        ("escape", "overlay.close", null),
        ("enter", "pane.openSelected", KeyContexts.FilesView),
        ("shift+space", "edit.toggleSelectionInPlace", KeyContexts.FilesView),
        ("ctrl+k ctrl+t", "preferences.selectColorTheme", null),
        ("f5", "file.copy", KeyContexts.FilesView),
        ("j", "terminal.something", KeyContexts.TerminalFocus));

    [Fact]
    public void Space_Esc_Enter_Up_Down_Tab_and_Ctrl_Alt_Win_keys_are_never_granted()
    {
        string[] never = ["space", "escape", "enter", "up", "down", "tab", "ctrl+left", "alt+k", "win+l", "ctrl+shift+a", "shift+up", "f5", "numpadadd", "equal"];
        Assert.Empty(QuickViewKeys.Grant(never, KeymapOf()));
        Assert.All(never, key => Assert.False(QuickViewKeys.IsAskable(key), key));
    }

    [Fact]
    public void A_key_bound_in_filesView_or_everywhere_or_starting_a_chord_is_never_granted()
    {
        var keymap = KeymapOf(("k", "custom.one", KeyContexts.FilesView), ("l", "custom.two", null), ("ctrl+k ctrl+t", "x", null), ("m n", "chord.m", KeyContexts.FilesView), ("j", "t", KeyContexts.TerminalFocus));
        Assert.Equal(["j", "shift+k", "right"], QuickViewKeys.Grant(["k", "l", "m", "j", "shift+k", "right", "right"], keymap));
    }

    [Fact]
    public void The_fixed_set_is_granted_alone_or_with_Shift_and_plus_is_the_grammars_equal()
    {
        string[] asked = ["left", "right", "pageup", "pagedown", "home", "end", "a", "z", "0", "9", "plus", "minus", "comma", "period", "shift+left", "shift+plus"];
        Assert.Equal(asked, QuickViewKeys.Grant(asked, Defaults()));
        Assert.Equal(new KeyCombo(KeyModifiers.None, "equal"), QuickViewKeys.ToCombo("plus"));
        Assert.Equal(new KeyCombo(KeyModifiers.Shift, "equal"), QuickViewKeys.ToCombo("shift+plus"));

        // A press of "=" reaches a page that asked for plus by its own name.
        Assert.Equal("plus", QuickViewKeys.PressedKey(new KeyCombo(KeyModifiers.None, "equal"), ["plus"], Defaults()));
        Assert.Equal("shift+plus", QuickViewKeys.PressedKey(new KeyCombo(KeyModifiers.Shift, "equal"), ["plus", "shift+plus"], Defaults()));
        Assert.Null(QuickViewKeys.PressedKey(new KeyCombo(KeyModifiers.None, "left"), ["right"], Defaults()));
    }

    [Fact]
    public void A_binding_added_while_the_panel_is_open_wins_at_the_next_press()
    {
        var granted = QuickViewKeys.Grant(["k"], Defaults());
        Assert.Equal(["k"], granted);
        Assert.Equal("k", QuickViewKeys.PressedKey(new KeyCombo(KeyModifiers.None, "k"), granted, Defaults()));
        var later = KeymapOf(("k", "custom.pause", KeyContexts.FilesView));
        Assert.Null(QuickViewKeys.PressedKey(new KeyCombo(KeyModifiers.None, "k"), granted, later));
    }

    [Fact]
    public void A_file_s_extension_is_lower_case_with_its_dot_and_empty_without_one()
    {
        Assert.Equal(".jpg", File("IMG_0412.JPG").Extension);
        Assert.Equal(".gz", File("a.tar.gz").Extension);
        Assert.Equal("", File("README").Extension);
        Assert.Equal("", File(".gitignore").Extension);
        Assert.Equal("", File("photos.d", folder: true).Extension);
    }
}
