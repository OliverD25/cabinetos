using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.QuickView;

/// <summary>What the panel knows of the file it shows: the listing's facts, read as the row is drawn (no file is read).</summary>
public sealed record QuickViewFile(string Path, string Name, bool IsFolder, ulong Size, DateTime Modified, int Position, int Count)
{
    /// <summary>The extension in lower case with its dot, or an empty text (ADR 0023, <c>quickview-show</c>'s <c>extension</c>).</summary>
    public string Extension
    {
        get
        {
            var dot = Name.LastIndexOf('.');
            return IsFolder || dot <= 0 ? "" : Name[dot..].ToLowerInvariant();
        }
    }
}

/// <summary>What fills the panel's content: the icon card, the shell's thumbnail, or the viewer's page.</summary>
public enum QuickViewPicture
{
    /// <summary>The row's icon, the name, the type, size and date: on screen at the next frame, with no request.</summary>
    Card,

    /// <summary>The shell's thumbnail, from the core.</summary>
    Thumbnail,

    /// <summary>The viewer's page, after it reported <c>quickview-shown</c>.</summary>
    Page,
}

/// <summary>Where the viewer of the shown file is (ADR 0023, decisions 2.3, 3 and 7).</summary>
public enum ViewerPhase
{
    /// <summary>No viewer: a folder, a kind no viewer claims, a kind set to "no viewer", or a viewer off for the session.</summary>
    None,

    /// <summary>Waiting for the arrow keys to rest (120 ms after the last move) before the page loads.</summary>
    Resting,

    /// <summary>The page loads; it has not said <c>ready</c> yet (3 s).</summary>
    Starting,

    /// <summary>The page said <c>ready</c> and has the file; no report yet (5 s: "Still loading…"; 30 s: failed).</summary>
    Loading,

    /// <summary>The page painted its full view.</summary>
    Shown,

    /// <summary>The page cannot show the file, did not start, or did not finish.</summary>
    Failed,

    /// <summary>The page's process ended or stopped responding.</summary>
    Stopped,

    /// <summary>The viewer stopped three times within a minute: off until the window restarts.</summary>
    Off,
}

/// <summary>Where the install offer is, when no viewer claims the shown file (ADR 0023, decision 5).</summary>
public enum OfferPhase
{
    /// <summary>No offer: a viewer claims the file, the kind is off, it is a folder, or the catalogue cannot be read.</summary>
    None,

    /// <summary>The core is asked which item would show it.</summary>
    Asking,

    /// <summary>An item to install.</summary>
    Item,

    /// <summary>No item of the catalogue claims the file.</summary>
    NoItem,

    /// <summary>The item installs.</summary>
    Installing,

    /// <summary>The install failed.</summary>
    InstallFailed,
}

/// <summary>What the window does when <see cref="QuickViewSession.Tick"/> says a moment has come.</summary>
public enum QuickViewDue
{
    /// <summary>Nothing.</summary>
    Nothing,

    /// <summary>The arrows rested: load the viewer's page for the current token now.</summary>
    StartViewer,

    /// <summary>A timeout changed what the panel says: draw it again.</summary>
    Redraw,
}

/// <summary>
/// The panel's logic, without XAML (ADR 0023, item W1): the token of each file, what the content shows, where the
/// viewer is, the 120 ms rest after a move, the timeouts, the count of a viewer's stops, the granted keys, and the
/// times of the <c>quick view shown</c> log line. The window feeds it what happens and draws what it says; the
/// clock is a parameter, so the tests run it without a window and without waiting.
/// </summary>
public sealed class QuickViewSession(Func<long> nowMilliseconds)
{
    /// <summary>The viewer's page loads this long after the last move of the cursor (Space opens with no wait).</summary>
    public const int RestMs = 120;

    /// <summary>A page that has not said <c>ready</c> this long after its load started "did not start".</summary>
    public const int ReadyMs = 3_000;

    /// <summary>A page that has said <c>ready</c> but not reported this long after shows "Still loading…".</summary>
    public const int StillLoadingMs = 5_000;

    /// <summary>A page that has not reported this long after its load started "did not finish".</summary>
    public const int GiveUpMs = 30_000;

    /// <summary>A viewer that stops this many times within <see cref="StopWindowMs"/> is off for the session.</summary>
    public const int StopsToOff = 3;

    /// <summary>The window in which <see cref="StopsToOff"/> stops turn a viewer off.</summary>
    public const int StopWindowMs = 60_000;

    private readonly Dictionary<string, List<long>> _stops = new(StringComparer.Ordinal);
    private readonly HashSet<string> _off = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);
    private long _phaseSince;
    private long _readyAt;
    private long _keyAt;

    /// <summary>The token of the file shown: it grows with every file the panel shows in this window.</summary>
    public long Token { get; private set; }

    /// <summary>Whether the panel is open.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The file shown, or null.</summary>
    public QuickViewFile? File { get; private set; }

    /// <summary>The viewer of the shown file, or null.</summary>
    public QuickViewer? Viewer { get; private set; }

    /// <summary>The viewer that would show the file but is off for the session, or null.</summary>
    public QuickViewer? OffViewer { get; private set; }

    /// <summary>The pattern of the viewer's kinds that matched, or the kind turned off.</summary>
    public string? Claim { get; private set; }

    /// <summary>What the table said about the shown file.</summary>
    public QuickViewMatch Match { get; private set; } = QuickViewMatch.Nothing;

    /// <summary>What fills the content.</summary>
    public QuickViewPicture Picture { get; private set; }

    /// <summary>Where the viewer is.</summary>
    public ViewerPhase Phase { get; private set; }

    /// <summary>Whether a page that said <c>ready</c> has been loading longer than <see cref="StillLoadingMs"/>.</summary>
    public bool StillLoading { get; private set; }

    /// <summary>Why the viewer failed: a page's <c>reason</c>, or <c>not-started</c>, <c>not-finished</c>.</summary>
    public string? FailReason { get; private set; }

    /// <summary>The page's own text of a failure, at most 200 characters.</summary>
    public string? FailMessage { get; private set; }

    /// <summary>The <c>details</c> of <c>quickview-shown</c> for the bottom line, or null.</summary>
    public string? Details { get; private set; }

    /// <summary>The page keys the window granted for the current token.</summary>
    public IReadOnlyList<string> GrantedKeys { get; private set; } = [];

    /// <summary>Whether the current file's viewer loads for the first time in this window (its browser process starts).</summary>
    public bool Cold { get; private set; }

    /// <summary>Where the install offer is.</summary>
    public OfferPhase Offer { get; private set; }

    /// <summary>The item offered, while <see cref="Offer"/> is <see cref="OfferPhase.Item"/>, installing or failed.</summary>
    public QuickViewOfferItem? OfferItem { get; private set; }

    /// <summary>How far the install has come, 0 to 100.</summary>
    public int InstallPercent { get; private set; }

    /// <summary>Why the install failed.</summary>
    public string? InstallError { get; private set; }

    /// <summary>The times of the shown file, for the log line.</summary>
    public QuickViewTiming Timing { get; private set; } = new();

    /// <summary>
    /// Shows <paramref name="file"/>, with what the table said about it, and returns its token. <paramref name="rest"/>:
    /// the cursor moved (an arrow, PageDown, quick search), so the viewer waits <see cref="RestMs"/>; false for Space,
    /// which loads at once. <paramref name="keyAt"/> is the moment the window handled the key, the zero of the times.
    /// A line of the file before it that has not been written yet comes back from <see cref="TakeLogLine"/> first.
    /// </summary>
    public long Show(QuickViewFile file, QuickViewMatch match, bool rest, long keyAt)
    {
        EndFile("skipped");
        IsOpen = true;
        Token++;
        File = file;
        Match = match;
        Claim = match.Claim;
        Viewer = file.IsFolder || match.Viewer is not { } viewer || _off.Contains(viewer.Id) ? null : match.Viewer;
        OffViewer = file.IsFolder || Viewer is not null ? null
            : match.Viewer is { } offered && _off.Contains(offered.Id) ? offered : match.OffViewer;
        Picture = QuickViewPicture.Card;
        StillLoading = false;
        FailReason = null;
        FailMessage = null;
        Details = null;
        GrantedKeys = [];
        OfferItem = null;
        InstallPercent = 0;
        InstallError = null;
        _keyAt = keyAt;
        Cold = Viewer is { } shown && !_loaded.Contains(shown.Id);
        Timing = new QuickViewTiming { Token = Token, Viewer = Viewer?.Id, Cold = Cold };
        Offer = !file.IsFolder && Viewer is null && OffViewer is null && !match.Off ? OfferPhase.Asking : OfferPhase.None;
        if (Viewer is null)
        {
            // A viewer that is off for the session says so; a file without one simply has none.
            Phase = OffViewer is not null ? ViewerPhase.Off : ViewerPhase.None;
            Timing.Full = Phase == ViewerPhase.Off ? "off" : "none";
        }
        else
        {
            Phase = ViewerPhase.Resting;
        }
        _phaseSince = rest ? nowMilliseconds() : nowMilliseconds() - RestMs;
        return Token;
    }

    /// <summary>The panel closed: a line not written yet ends as skipped.</summary>
    public void Close()
    {
        EndFile("skipped");
        IsOpen = false;
        File = null;
        Viewer = null;
        OffViewer = null;
        Phase = ViewerPhase.None;
        Offer = OfferPhase.None;
        GrantedKeys = [];
    }

    /// <summary>
    /// What is due now: the viewer's load once the arrows rested, or a timeout that changes what the panel says.
    /// The window calls it from a timer (<see cref="NextDueIn"/>) and right after <see cref="Show"/>.
    /// </summary>
    public QuickViewDue Tick()
    {
        var now = nowMilliseconds();
        switch (Phase)
        {
            case ViewerPhase.Resting when now - _phaseSince >= RestMs:
                return QuickViewDue.StartViewer;
            case ViewerPhase.Starting when now - _phaseSince >= GiveUpMs:
            case ViewerPhase.Loading when now - _phaseSince >= GiveUpMs:
                Fail("not-finished", null);
                return QuickViewDue.Redraw;
            case ViewerPhase.Starting when now - _phaseSince >= ReadyMs:
                // A ready that still comes is taken (OnReady): this only says so meanwhile.
                Fail("not-started", null);
                return QuickViewDue.Redraw;
            case ViewerPhase.Loading when !StillLoading && now - _readyAt >= StillLoadingMs:
                StillLoading = true;
                return QuickViewDue.Redraw;
            case ViewerPhase.Failed when FailReason == "not-started" && now - _phaseSince >= GiveUpMs:
                FailReason = "not-finished";
                Timing.Full = "failed:not-finished";
                return QuickViewDue.Redraw;
            default:
                return QuickViewDue.Nothing;
        }
    }

    /// <summary>Milliseconds until <see cref="Tick"/> has something to do, or null when nothing waits.</summary>
    public long? NextDueIn()
    {
        var now = nowMilliseconds();
        long? due = Phase switch
        {
            ViewerPhase.Resting => _phaseSince + RestMs,
            ViewerPhase.Starting => _phaseSince + ReadyMs,
            ViewerPhase.Loading when !StillLoading => Math.Min(_readyAt + StillLoadingMs, _phaseSince + GiveUpMs),
            ViewerPhase.Loading => _phaseSince + GiveUpMs,
            ViewerPhase.Failed when FailReason == "not-started" => _phaseSince + GiveUpMs,
            _ => null,
        };
        return due is { } at ? Math.Max(0, at - now) : null;
    }

    /// <summary>The window started the page's load for <paramref name="token"/>. False for an old token.</summary>
    public bool OnLoadStarted(long token)
    {
        if (token != Token || Phase != ViewerPhase.Resting || Viewer is not { } viewer)
        {
            return false;
        }
        _loaded.Add(viewer.Id);
        Phase = ViewerPhase.Starting;
        _phaseSince = nowMilliseconds();
        return true;
    }

    /// <summary>The page could not be loaded at all (WebView2 did not start, a folder it refused): a failure with that text.</summary>
    public bool OnLoadFailed(long token, string message)
    {
        if (token != Token || Viewer is null)
        {
            return false;
        }
        Fail("other", message);
        return true;
    }

    /// <summary>
    /// The page said <c>ready</c>: the window sends <c>quickview-show</c> when this is true. A ready after the
    /// "did not start" line is still taken, and the page gets the file.
    /// </summary>
    public bool OnReady(long token)
    {
        var lateReady = Phase == ViewerPhase.Failed && FailReason == "not-started";
        if (token != Token || (Phase != ViewerPhase.Starting && !lateReady))
        {
            return false;
        }
        Phase = ViewerPhase.Loading;
        FailReason = null;
        FailMessage = null;
        Timing.Full = null;
        _readyAt = nowMilliseconds();
        return true;
    }

    /// <summary>
    /// <c>quickview-shown</c>: the page replaces the card or the thumbnail. Taken for the current token in any phase
    /// but stopped and off, so a page that reports after "did not finish" is still shown. False for an old token.
    /// </summary>
    public bool OnShown(long token, string? details)
    {
        if (token != Token || Viewer is null || Phase is ViewerPhase.Stopped or ViewerPhase.Off or ViewerPhase.Shown or ViewerPhase.None)
        {
            return false;
        }
        Phase = ViewerPhase.Shown;
        StillLoading = false;
        FailReason = null;
        FailMessage = null;
        Details = details;
        Picture = QuickViewPicture.Page;
        Timing.Full = "pending-frame";
        return true;
    }

    /// <summary><c>quickview-failed</c>: the thumbnail or the card stays, and the panel says why. False for an old token.</summary>
    public bool OnFailed(long token, string reason, string message)
    {
        if (token != Token || Viewer is null || Phase is not (ViewerPhase.Starting or ViewerPhase.Loading))
        {
            return false;
        }
        Fail(reason, message);
        return true;
    }

    /// <summary>
    /// The page of <paramref name="viewerId"/> stopped (its process ended or hung). Counts the stop; the third within a
    /// minute turns the viewer off for the session. True when the shown file's viewer was that one.
    /// </summary>
    public bool OnStopped(string viewerId)
    {
        var now = nowMilliseconds();
        if (!_stops.TryGetValue(viewerId, out var times))
        {
            _stops[viewerId] = times = [];
        }
        times.Add(now);
        times.RemoveAll(t => now - t > StopWindowMs);
        if (times.Count >= StopsToOff)
        {
            _off.Add(viewerId);
        }
        if (Viewer?.Id != viewerId)
        {
            return false;
        }
        Phase = _off.Contains(viewerId) ? ViewerPhase.Off : ViewerPhase.Stopped;
        StillLoading = false;
        if (Picture == QuickViewPicture.Page)
        {
            Picture = Timing.ThumbnailShown ? QuickViewPicture.Thumbnail : QuickViewPicture.Card;
        }
        GrantedKeys = [];
        Timing.Full ??= Phase == ViewerPhase.Off ? "off" : "stopped";
        if (Timing.Full == "pending-frame")
        {
            Timing.Full = "stopped";
        }
        return true;
    }

    /// <summary>Whether <paramref name="viewerId"/> is off for the rest of the window's session.</summary>
    public bool IsOff(string viewerId) => _off.Contains(viewerId);

    /// <summary>The keys granted to the page of <paramref name="token"/>: what <see cref="QuickViewKeys.Grant"/> allowed. False for an old token.</summary>
    public bool OnKeysGranted(long token, IReadOnlyList<string> keys)
    {
        if (token != Token || Viewer is null || Phase is ViewerPhase.Stopped or ViewerPhase.Off)
        {
            return false;
        }
        GrantedKeys = keys;
        return true;
    }

    /// <summary>The thumbnail of <paramref name="token"/> came, with a picture or none. False when it is too late to matter.</summary>
    public bool OnThumbnail(long token, bool picture, string? reason)
    {
        if (token != Token || !IsOpen)
        {
            return false;
        }
        if (!picture)
        {
            Timing.Thumbnail ??= "none" + (reason is { Length: > 0 } && reason != ThumbnailReasons.None ? ":" + reason : "");
            return false;
        }
        if (Picture != QuickViewPicture.Card)
        {
            // The page came first: it replaces whichever of the two shows, and the thumbnail is not drawn.
            Timing.Thumbnail ??= "after-page";
            return false;
        }
        Picture = QuickViewPicture.Thumbnail;
        Timing.Thumbnail = "pending-frame";
        return true;
    }

    /// <summary>The frame after a change of <paramref name="what"/> was rendered: its time from the key goes into the line.</summary>
    public void OnRendered(long token, QuickViewMoment what)
    {
        if (token != Token)
        {
            return;
        }
        var ms = nowMilliseconds() - _keyAt;
        switch (what)
        {
            case QuickViewMoment.Card:
                Timing.CardMs ??= ms;
                break;
            case QuickViewMoment.Thumbnail when Timing.Thumbnail == "pending-frame":
                Timing.ThumbnailMs = ms;
                Timing.Thumbnail = null;
                Timing.ThumbnailShown = true;
                break;
            case QuickViewMoment.Page when Timing.Full == "pending-frame":
                Timing.FullMs = ms;
                Timing.Full = null;
                break;
        }
    }

    /// <summary>The offer's answer: an item, or none and why.</summary>
    public bool OnOffer(long token, QuickViewOfferItem? item, string? reason)
    {
        if (token != Token || Offer != OfferPhase.Asking)
        {
            return false;
        }
        OfferItem = item;
        Offer = item is not null ? OfferPhase.Item : reason == OfferReasons.Offline ? OfferPhase.None : OfferPhase.NoItem;
        return true;
    }

    /// <summary>The offered item started to install (the button or <c>quickView.installViewer</c>).</summary>
    public bool OnInstallStarted()
    {
        if (Offer is not (OfferPhase.Item or OfferPhase.InstallFailed) || OfferItem is null)
        {
            return false;
        }
        Offer = OfferPhase.Installing;
        InstallPercent = 0;
        InstallError = null;
        return true;
    }

    /// <summary>The install's progress.</summary>
    public bool OnInstallProgress(string itemId, ulong bytes, ulong total)
    {
        if (Offer != OfferPhase.Installing || OfferItem?.Id != itemId || total == 0)
        {
            return false;
        }
        var percent = (int)Math.Min(100, bytes * 100 / total);
        if (percent == InstallPercent)
        {
            return false;
        }
        InstallPercent = percent;
        return true;
    }

    /// <summary>The install ended: a failure shows its message and "Try again"; a success waits for the new table.</summary>
    public bool OnInstallEnded(string itemId, bool ok, string? message)
    {
        if (Offer != OfferPhase.Installing || OfferItem?.Id != itemId)
        {
            return false;
        }
        if (!ok)
        {
            Offer = OfferPhase.InstallFailed;
            InstallError = message;
        }
        return true;
    }

    /// <summary>
    /// The line to write now for the file shown, or null: once its outcome is known (the full view's frame, a failure,
    /// a stop, or no viewer and the thumbnail's answer), or for the file before it that the user left first (skipped).
    /// Each token's line comes once.
    /// </summary>
    public QuickViewTiming? TakeLogLine()
    {
        if (_ended.Count > 0)
        {
            return _ended.Dequeue();
        }
        if (Timing.Written || Timing.CardMs is null)
        {
            return null;
        }
        var fullKnown = Timing.FullMs is not null || Timing.Full is not (null or "pending-frame");
        var thumbnailKnown = Timing.ThumbnailMs is not null || Timing.Thumbnail is not (null or "pending-frame");
        if (!fullKnown || (Viewer is null && !thumbnailKnown))
        {
            return null;
        }
        Timing.Written = true;
        return Timing;
    }

    private readonly Queue<QuickViewTiming> _ended = new();

    private void EndFile(string outcome)
    {
        if (!IsOpen || Timing.Written || Timing.Token == 0)
        {
            return;
        }
        if (Timing.FullMs is null && Timing.Full is null or "pending-frame")
        {
            Timing.Full = outcome;
        }
        if (Timing.ThumbnailMs is null && Timing.Thumbnail is null or "pending-frame")
        {
            Timing.Thumbnail = outcome;
        }
        Timing.Written = true;
        _ended.Enqueue(Timing);
    }

    private void Fail(string reason, string? message)
    {
        Phase = ViewerPhase.Failed;
        StillLoading = false;
        FailReason = reason;
        FailMessage = message;
        GrantedKeys = [];
        // The phase keeps the moment the load started: a "did not start" still turns into "did not finish" at 30 s.
        Timing.Full = "failed:" + reason;
    }

    /// <summary>
    /// The line under the thumbnail, in the panel's words (ADR 0023, decisions 3 and 7), or null when there is
    /// nothing to say.
    /// </summary>
    public string? StatusText()
    {
        var name = Viewer?.Name ?? OffViewer?.Name ?? "The viewer";
        return Phase switch
        {
            ViewerPhase.Loading when StillLoading => "Still loading…",
            ViewerPhase.Failed when FailReason == "not-started" => $"{name} did not start.",
            ViewerPhase.Failed when FailReason == "not-finished" => $"{name} did not finish.",
            ViewerPhase.Failed => string.IsNullOrWhiteSpace(FailMessage) ? $"{name} cannot show this file." : $"{name} cannot show this file. {FailMessage}",
            ViewerPhase.Stopped => $"{name} stopped.",
            ViewerPhase.Off => $"{name} stopped {StopsToOff} times. It is off until CabinetOS restarts.",
            _ => null,
        };
    }

    /// <summary>The offer bar's line, or null when it shows none.</summary>
    public string? OfferText()
    {
        var kind = File?.Extension is { Length: > 0 } extension ? $"{extension} files" : File is { } file ? file.Name : "this file";
        return Offer switch
        {
            OfferPhase.Item => $"No viewer for {kind} is installed.",
            OfferPhase.NoItem => $"No viewer for {kind}.",
            OfferPhase.Installing => $"Installing {OfferItem?.Name}… {InstallPercent} %",
            OfferPhase.InstallFailed => $"The install failed: {InstallError?.TrimEnd('.')}.",
            _ => null,
        };
    }

    /// <summary>One word for the log and the tests: what the panel shows now.</summary>
    public string StateName() => !IsOpen ? "closed" : Phase switch
    {
        ViewerPhase.Shown => "shown",
        ViewerPhase.Failed => "failed",
        ViewerPhase.Stopped => "stopped",
        ViewerPhase.Off => "off",
        ViewerPhase.Resting or ViewerPhase.Starting or ViewerPhase.Loading => "loading",
        _ when Offer is not OfferPhase.None => "offer",
        _ => Picture == QuickViewPicture.Thumbnail ? "thumbnail" : "card",
    };
}

/// <summary>Which change of the panel a rendered frame ends the time of.</summary>
public enum QuickViewMoment
{
    /// <summary>The icon card.</summary>
    Card,

    /// <summary>The thumbnail.</summary>
    Thumbnail,

    /// <summary>The page made visible after <c>quickview-shown</c>.</summary>
    Page,
}

/// <summary>
/// The times of one file shown, from the key press to the rendered frame (ADR 0023, decision 4.5), for the line
/// <c>quick view shown</c>. A time that has none says why: <c>none</c>, <c>skipped</c>, <c>failed:&lt;reason&gt;</c>.
/// </summary>
public sealed class QuickViewTiming
{
    /// <summary>The file's token.</summary>
    public long Token { get; init; }

    /// <summary>The viewer, or null.</summary>
    public string? Viewer { get; init; }

    /// <summary>The first load of that viewer in this window.</summary>
    public bool Cold { get; init; }

    /// <summary>The icon card on screen, from the key press.</summary>
    public long? CardMs { get; set; }

    /// <summary>The thumbnail on screen, from the key press.</summary>
    public long? ThumbnailMs { get; set; }

    /// <summary>Why there is no thumbnail time: <c>none</c> (with the core's reason), <c>after-page</c>, <c>skipped</c>.</summary>
    public string? Thumbnail { get; set; }

    /// <summary>Whether the thumbnail was drawn.</summary>
    public bool ThumbnailShown { get; set; }

    /// <summary>The full view on screen, from the key press.</summary>
    public long? FullMs { get; set; }

    /// <summary>Why there is no full-view time: <c>none</c>, <c>off</c>, <c>failed:&lt;reason&gt;</c>, <c>stopped</c>, <c>skipped</c>.</summary>
    public string? Full { get; set; }

    /// <summary>The line was written.</summary>
    public bool Written { get; set; }
}
