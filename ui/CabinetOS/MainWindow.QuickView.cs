using System.Diagnostics;
using System.Text.Json;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Prompts;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.QuickView;
using CabinetOS.Core.Tools;
using CabinetOS.Services;
using CabinetOS.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace CabinetOS;

// Quick View (Phase 25, ADR 0023; docs/ui.md, "Quick View"): Space opens a floating panel on the cursor row; the icon card
// first, the shell's thumbnail from the core, then a viewer's full view (a Tool Extension page). The keyboard stays in the
// pane's list the whole time: the arrows walk the folder and the panel follows the cursor, Space or Esc closes it, Enter
// opens the file as before. The window reads no file for it (Prime Directive 1) and never waits on a page (Article 1).
public sealed partial class MainWindow
{
    private const string QuickViewTarget = "cabinetos_ui::quickview";

    private readonly Stopwatch _quickViewClock = Stopwatch.StartNew();
    private readonly QuickViewPool _quickViewPool = new();
    private readonly Dictionary<string, QuickViewHost> _quickViewHosts = new(StringComparer.Ordinal);
    private readonly List<(long Token, QuickViewMoment What)> _quickViewFrames = [];
    private QuickViewSession _quickView = null!;
    private QuickViewTable _quickViewTable = QuickViewTable.Empty;
    private DispatcherQueueTimer _quickViewTimer = null!;
    private IconCache _quickViewIcons = null!;
    private QuickViewHost? _quickViewPage;

    // The pane whose cursor the panel follows, the listing index it shows, and the direction of the last move (read ahead).
    private int _quickViewPane = -1;
    private int _quickViewIndex = -1;
    private int _quickViewDirection = 1;

    // The trace of the command run that opened the panel: the "quick view shown" lines of that panel carry it (Article 12).
    private string? _quickViewTrace;

    // When the window last handled a key (the zero of the panel's times), on _quickViewClock.
    private long _quickViewKeyAt;

    // The thumbnail on screen, for quickview-show.
    private QuickViewThumbnail? _quickViewThumbnail;

    // The requests to the core that are out for the panel (thumbnail, offer): until:quickview-idle waits for none.
    private int _quickViewAsking;

    // One drawing per token in flight; a second waits for the first (ADR 0023, decision 3).
    private bool _quickViewDrawing;
    private (QuickViewHost Host, long Token, int Width, int Height)? _quickViewNextDrawing;

    private void SetUpQuickView()
    {
        _quickView = new QuickViewSession(() => _quickViewClock.ElapsedMilliseconds)
        {
            Limits = QuickViewLimits.Parse(Environment.GetEnvironmentVariable("CABINETOS_QUICKVIEW_LIMITS")),
        };
        _quickViewTimer = DispatcherQueue.CreateTimer();
        _quickViewTimer.IsRepeating = false;
        _quickViewTimer.Tick += (_, _) => OnQuickViewTimer();
        // The card's icon at 48 px; until it comes, the row's icon the window holds.
        _quickViewIcons = new IconCache(_session) { Size = 48 };
        _quickViewIcons.Loaded += key =>
        {
            if (_quickView.IsOpen && CursorDetail() is { IconKey: var shown } && shown == key)
            {
                QuickViewView.SetIcon(_quickViewIcons.Get(key));
            }
        };
        QuickViewView.OpenClicked += () => _ = _router.ExecuteAsync("pane.openSelected", trigger: "mouse");
        QuickViewView.CloseClicked += () => CloseQuickView("close button");
        QuickViewView.InstallClicked += () => _ = InstallQuickViewOfferAsync(null);
        QuickViewView.ViewerChosen += id => _ = ChooseQuickViewerAsync(id, null, "panel");
        QuickViewView.MenuClosed += FocusQuickViewList;
        _market.Changed += OnQuickViewInstallProgress;
        FocusManager.GotFocus += OnQuickViewFocus;
        RootGrid.ActualThemeChanged += (_, _) => SendQuickViewTheme();
        _themes.Applied += _ => SendQuickViewTheme();
        // Enter opens the row as it does in the pane, and the panel closes first (ADR 0023, decision 6).
        _router.Executing += invocation =>
        {
            if (invocation.CommandId == "pane.openSelected")
            {
                CloseQuickView("enter");
            }
        };
    }

    private void RegisterQuickViewCommands()
    {
        _router.RegisterUiHandler("quickView.toggle", ToggleQuickViewAsync);
        _router.RegisterUiHandler("quickView.chooseViewer", ChooseViewerFromPaletteAsync);
        _router.RegisterUiHandler("quickView.installViewer", InstallViewerFromCommandAsync);
    }

    // The table of viewers, once after hello, and again when the core restarts.
    private async Task LoadQuickViewTableAsync()
    {
        try
        {
            if (await _session.RequestAsync(new QuickViewTableRequest()) is QuickViewTableReply table)
            {
                ApplyQuickViewTable(new QuickViewTable(table.Viewers, table.Kinds));
            }
        }
        catch (IOException error)
        {
            Diag.Info(QuickViewTarget, "the Quick View table could not be read", new LogField("error", error.Message));
        }
    }

    // A new table (an install, an uninstall, a choice in quickView.viewers): a file on screen whose viewer changes is shown again.
    private void ApplyQuickViewTable(QuickViewTable table)
    {
        _quickViewTable = table;
        Diag.Info(QuickViewTarget, "quick view table", new LogField("viewers", string.Join(",", table.Viewers.Select(v => v.Id))),
            new LogField("kinds", table.Kinds.Count));
        foreach (var (id, host) in _quickViewHosts.ToList())
        {
            // A viewer that is gone, or moved to another folder, starts afresh.
            if (table.Viewer(id) is not { } viewer || viewer != host.Viewer)
            {
                CloseQuickViewHost(host);
            }
        }
        if (!_quickView.IsOpen || _quickView.File is not { } file || _quickViewPane < 0)
        {
            return;
        }
        var match = file.IsFolder ? QuickViewMatch.Nothing : table.Match(file.Name, _quickView.IsOff);
        if (match.Viewer?.Id != _quickView.Viewer?.Id || match.Off != _quickView.Match.Off || match.Choices.Count != _quickView.Match.Choices.Count)
        {
            if (_panes[_quickViewPane].EntryAt(_quickViewIndex) is { } entry)
            {
                ShowQuickViewEntry(entry, rest: false, _quickViewClock.ElapsedMilliseconds);
            }
        }
    }

    // ----- Opening, following and closing -----

    private Task ToggleQuickViewAsync(CommandInvocation invocation)
    {
        if (_quickView.IsOpen)
        {
            CloseQuickView("toggle");
            return Task.CompletedTask;
        }
        if (Active.Search is not null)
        {
            ShowNotice("Quick View shows the rows of a folder; these are search results.");
            return Task.CompletedTask;
        }
        if (Active.EntryAt(Active.FocusIndex) is not { } entry)
        {
            // An empty folder, or no cursor row.
            return Task.CompletedTask;
        }
        var keyAt = invocation.Trigger == "key" && _quickViewClock.ElapsedMilliseconds - _quickViewKeyAt < 1000 ? _quickViewKeyAt : _quickViewClock.ElapsedMilliseconds;
        CloseOtherOverlays(Overlay.QuickView);
        _quickViewPane = _active;
        _quickViewTrace = invocation.RequestId;
        _quickViewDirection = 1;
        QuickViewView.Compact = _compact is not null;
        QuickViewView.Open();
        Diag.Info(QuickViewTarget, "quick view opened", new LogField("pane", _active), new LogField("trigger", invocation.Trigger));
        ShowQuickViewEntry(entry, rest: false, keyAt);
        return Task.CompletedTask;
    }

    private void CloseQuickView(string why)
    {
        if (!_quickView.IsOpen)
        {
            return;
        }
        _quickView.Close();
        WriteQuickViewLines();
        _quickViewTimer.Stop();
        _quickViewFrames.Clear();
        foreach (var host in _quickViewHosts.Values)
        {
            _ = host.SleepAsync();
        }
        _quickViewPage = null;
        _quickViewThumbnail = null;
        QuickViewView.Close();
        Diag.Info(QuickViewTarget, "quick view closed", new LogField("why", why));
        _quickViewPane = -1;
        _quickViewIndex = -1;
    }

    // The pane's cursor moved (an arrow, PageDown, Home, quick search, a click on a row, the next row after a Delete): the
    // panel shows the file under it, and its viewer waits for the keys to rest.
    private void FollowQuickViewCursor()
    {
        if (!_quickView.IsOpen || _quickViewPane < 0)
        {
            return;
        }
        var pane = _panes[_quickViewPane];
        if (pane.Search is not null)
        {
            CloseQuickView("search");
            return;
        }
        if (pane.EntryAt(pane.FocusIndex) is not { } entry)
        {
            return;
        }
        if (_quickView.File is { } shown && string.Equals(shown.Path, entry.Path, StringComparison.OrdinalIgnoreCase))
        {
            _quickViewIndex = entry.Index;
            return;
        }
        var before = _quickViewIndex >= 0 ? pane.Selection.PositionOf(_quickViewIndex) : -1;
        _quickViewDirection = pane.Selection.PositionOf(entry.Index) >= before ? 1 : -1;
        var now = _quickViewClock.ElapsedMilliseconds;
        ShowQuickViewEntry(entry, rest: true, now - _quickViewKeyAt < 500 ? _quickViewKeyAt : now);
    }

    // The pane's folder changed: the panel closes, except in the column view, where it follows the keyboard's column.
    private void OnQuickViewFolderChanged(PaneModel pane)
    {
        if (!_quickView.IsOpen || _quickViewPane < 0 || pane != _panes[_quickViewPane])
        {
            return;
        }
        if (_columnViews[_quickViewPane] is not null)
        {
            FollowQuickViewCursor();
            return;
        }
        CloseQuickView("folder changed");
    }

    // The keyboard stays in the pane's list (ADR 0023, decision 6). A click that puts it in the page, or a control of the
    // panel, gives it back to the list at once (the click itself still reaches the page); the viewer button's list (a
    // popup) keeps it until it closes. Anywhere else (Tab, a click in the other pane, a tool tab) closes the panel.
    private void OnQuickViewFocus(object? sender, FocusManagerGotFocusEventArgs e)
    {
        if (!_quickView.IsOpen || _quickViewPane < 0 || e.NewFocusedElement is not DependencyObject element)
        {
            return;
        }
        if (IsWithin(element, _paneViews[_quickViewPane]))
        {
            return;
        }
        if (IsWithin(element, QuickViewView))
        {
            // After the focus event, not inside it: the list takes the keyboard on the next turn.
            Diag.Info(QuickViewTarget, "the keyboard went into the panel; back to the list", new LogField("element", element.GetType().Name));
            DispatcherQueue.TryEnqueue(FocusQuickViewList);
            return;
        }
        if (!IsWithinContent(element))
        {
            // A popup of the panel: the viewer button's list.
            return;
        }
        if (IsAroundContent(element))
        {
            // The focus fell to the window's root (an element that had it went away): no user moved it; back to the list.
            Diag.Info(QuickViewTarget, "the keyboard fell to the window's root; back to the list");
            DispatcherQueue.TryEnqueue(FocusQuickViewList);
            return;
        }
        Diag.Info(QuickViewTarget, "the keyboard left the list", new LogField("element", element.GetType().Name),
            new LogField("name", (element as FrameworkElement)?.Name ?? ""));
        CloseQuickView("the keyboard left the list");
    }

    private static bool IsWithin(DependencyObject element, DependencyObject container)
    {
        for (var at = element; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (at == container)
            {
                return true;
            }
        }
        return false;
    }

    private void FocusQuickViewList()
    {
        if (_quickView.IsOpen && _quickViewPane >= 0)
        {
            _paneViews[_quickViewPane].Focus(FocusState.Programmatic);
        }
    }

    // ----- One file -----

    private void ShowQuickViewEntry(PaneEntry entry, bool rest, long keyAt)
    {
        var pane = _panes[_quickViewPane];
        var view = pane.View;
        var position = Math.Max(0, pane.Selection.PositionOf(entry.Index));
        var count = pane.Selection.ShownCount;
        var modified = view is not null && entry.Index < view.Count ? view.Modified(entry.Index) : default;
        var file = new QuickViewFile(entry.Path, entry.Name, entry.IsFolder, entry.Size, modified, position, count);
        var match = entry.IsFolder ? QuickViewMatch.Nothing : _quickViewTable.Match(entry.Name, _quickView.IsOff);
        var token = _quickView.Show(file, match, rest, keyAt);
        WriteQuickViewLines();
        _quickViewIndex = entry.Index;
        _quickViewThumbnail = null;
        _quickViewNextDrawing = null;

        // The icon card: from what the window holds, so something of the file is on screen at the next frame.
        var detail = pane.KnownDetails.Detail(entry.Index, entry.Name.AsSpan(), entry.IsFolder);
        var icon = detail is { IconKey.Length: > 0 } ? _quickViewIcons.Get(detail.IconKey) ?? pane.KnownDetails.Icon(detail.IconKey) : null;
        var type = detail?.TypeName is { Length: > 0 } typeName ? typeName : entry.IsFolder ? "File folder" : DisplayFormat.TypeText(entry.Name, CabinetOS.Core.Listing.EntryKind.File, false);
        QuickViewView.SetTitle(entry.Name, $"{position + 1} of {count}", icon);
        QuickViewView.SetCard(icon, entry.Name, type, CardFacts(file));
        QuickViewView.SetThumbnail(null, 0, 0, 1);
        if (_quickViewPage is { } previous && previous.Viewer.Id != _quickView.Viewer?.Id)
        {
            // Another viewer (or none) shows this file: the page before goes to about:blank and sleeps.
            _ = previous.SleepAsync();
            _quickViewPage = null;
        }
        else if (_quickViewPage is { } same)
        {
            same.Frame.Opacity = 0;
            same.Frame.IsHitTestVisible = false;
        }
        RenderQuickView();
        MarkQuickViewFrame(token, QuickViewMoment.Card);

        _ = LoadQuickViewThumbnailAsync(token, file);
        ReadQuickViewAhead(pane, position);
        if (file.IsFolder)
        {
            // The folder card shows its size, measured at once as Calculate Folder Size measures it (ADR 0023, decision 4.6).
            _ = MeasureFoldersAsync([file.Path], new CommandInvocation("quickView.toggle", null, _quickViewTrace ?? Ulid.NewId(), "quickview"));
        }
        if (_quickView.Offer == OfferPhase.Asking)
        {
            _ = AskQuickViewOfferAsync(token, file.Name);
        }
        OnQuickViewTimer();
    }

    private EntryDetail? CursorDetail() =>
        _quickViewPane >= 0 && _panes[_quickViewPane].EntryAt(_quickViewIndex) is { } entry
            ? _panes[_quickViewPane].KnownDetails.Detail(entry.Index, entry.Name.AsSpan(), entry.IsFolder)
            : null;

    private static string Dot(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    private string CardFacts(QuickViewFile file)
    {
        if (!file.IsFolder)
        {
            return Dot(DisplayFormat.Size(file.Size, false), file.Modified == default ? null : DisplayFormat.Modified(file.Modified, DateTime.Now));
        }
        var measured = _quickViewPane >= 0 ? _panes[_quickViewPane].MeasuredSize(file.Name.AsSpan()) : null;
        return measured is null ? "Measuring…"
            : Dot(DisplayFormat.Bytes(measured.Bytes), $"{measured.Files:N0} files", $"{measured.Folders:N0} folders", measured.Done ? null : "counting…");
    }

    // A measured folder's size grows on the folder card.
    private void UpdateQuickViewFolderSize()
    {
        if (_quickView.IsOpen && _quickView.File is { IsFolder: true } folder)
        {
            QuickViewView.SetCardFacts(CardFacts(folder));
            QuickViewView.SetFacts(CardFacts(folder));
        }
    }

    // Draws what the session says: the picture, the line under it, the offer bar, the viewer button and the bottom line.
    private void RenderQuickView()
    {
        if (!_quickView.IsOpen || _quickView.File is not { } file)
        {
            return;
        }
        QuickViewView.ShowPicture(_quickView.Picture, _quickViewPage?.Frame);
        QuickViewView.SetStatus(_quickView.StatusText());
        var item = _quickView.OfferItem;
        var button = _quickView.Offer switch
        {
            OfferPhase.Item => $"Install {item?.Name}",
            OfferPhase.InstallFailed => "Try again",
            _ => null,
        };
        var note = _quickView.Offer == OfferPhase.Item && item is not null ? $"Free · {DisplayFormat.Size(item.Size, false)} · from the CabinetOS marketplace" : null;
        QuickViewView.SetOffer(_quickView.OfferText(), button, note);
        var match = _quickView.Match;
        QuickViewView.SetViewerChoices(match.Choices, match.Off ? null : _quickView.Viewer?.Id ?? match.OffViewer?.Id, !file.IsFolder && match.ShowsChooser);
        QuickViewView.SetFacts(file.IsFolder ? CardFacts(file) : Dot(CardFacts(file), _quickView.Details));
    }

    // ----- Times: the frame after each change -----

    // The frame after a change ends its time (ADR 0023, decision 4.5): CompositionTarget.Rendering after the card is set, the
    // thumbnail is drawn, or the page is made visible.
    private void MarkQuickViewFrame(long token, QuickViewMoment what)
    {
        if (_quickViewFrames.Count == 0)
        {
            CompositionTarget.Rendering += OnQuickViewFrame;
        }
        _quickViewFrames.Add((token, what));
    }

    private void OnQuickViewFrame(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnQuickViewFrame;
        var frames = _quickViewFrames.ToList();
        _quickViewFrames.Clear();
        foreach (var (token, what) in frames)
        {
            _quickView.OnRendered(token, what);
        }
        WriteQuickViewLines();
    }

    // One line per file shown (docs/diagnostics.md, "quick view shown"): the times the live check reads.
    private void WriteQuickViewLines()
    {
        while (_quickView.TakeLogLine() is { } line)
        {
            var fields = new List<LogField>
            {
                new("token", line.Token),
                new("kind", line.Kind),
                new("viewer", line.Viewer ?? "none"),
                line.CardMs is { } card ? new("card_ms", card) : new("card", "skipped"),
                line.ThumbnailMs is { } thumbnail ? new("thumbnail_ms", thumbnail) : new("thumbnail", line.Thumbnail ?? "none"),
                line.FullMs is { } full ? new("full_ms", full) : new("full", line.Full ?? "none"),
                new("cold", line.Cold),
            };
            Diag.Log(LogLevel.Info, QuickViewTarget, "quick view shown", fields: fields, traceId: _quickViewTrace);
        }
    }

    // ----- The thumbnail -----

    private async Task LoadQuickViewThumbnailAsync(long token, QuickViewFile file)
    {
        _quickViewAsking++;
        try
        {
            var reply = await _session.RequestAsync(new GetThumbnailRequest(file.Path, 256));
            if (reply is ThumbnailReply { PngBase64: { Length: > 0 } png, Width: { } width, Height: { } height })
            {
                var (stream, _) = await IconBytes.DecodeAsync(png);
                using (stream)
                {
                    if (token != _quickView.Token || !_quickView.IsOpen)
                    {
                        return;
                    }
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    if (!_quickView.OnThumbnail(token, picture: true, null))
                    {
                        WriteQuickViewLines();
                        return;
                    }
                    _quickViewThumbnail = new QuickViewThumbnail("data:image/png;base64," + png, (int)width, (int)height);
                    QuickViewView.SetThumbnail(bitmap, (int)width, (int)height, RootGrid.XamlRoot?.RasterizationScale ?? 1);
                    RenderQuickView();
                    MarkQuickViewFrame(token, QuickViewMoment.Thumbnail);
                }
                return;
            }
            var reason = reply switch
            {
                ThumbnailReply none => none.Reason ?? ThumbnailReasons.None,
                ErrorReply error => error.Code,
                _ => "unexpected",
            };
            _quickView.OnThumbnail(token, picture: false, reason);
            WriteQuickViewLines();
        }
        catch (Exception error) when (error is IOException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            _quickView.OnThumbnail(token, picture: false, "error");
            WriteQuickViewLines();
        }
        finally
        {
            _quickViewAsking--;
        }
    }

    // The files next to the one shown, the one in the direction of the last move first (ADR 0023, decision 4.3): the core
    // puts them in its cache, so walking on is a memory read.
    private void ReadQuickViewAhead(PaneModel pane, int position)
    {
        foreach (var step in new[] { _quickViewDirection, -_quickViewDirection })
        {
            var at = position + step;
            if (at < 0 || at >= pane.Selection.ShownCount || pane.EntryAt(pane.Selection.IndexAt(at)) is not { } next)
            {
                continue;
            }
            _ = AskAheadAsync(next.Path);
        }

        async Task AskAheadAsync(string path)
        {
            try
            {
                await _session.RequestAsync(new GetThumbnailRequest(path, 256) { Ahead = true });
            }
            catch (IOException)
            {
                // A read ahead is a hint; the file asks again when it is shown.
            }
        }
    }

    // ----- The viewer's page -----

    private void OnQuickViewTimer()
    {
        _quickViewTimer.Stop();
        if (!_quickView.IsOpen)
        {
            return;
        }
        switch (_quickView.Tick())
        {
            case QuickViewDue.StartViewer:
                _ = StartQuickViewerAsync(_quickView.Token);
                break;
            case QuickViewDue.Redraw:
                RenderQuickView();
                WriteQuickViewLines();
                break;
        }
        if (_quickView.NextDueIn() is { } due)
        {
            _quickViewTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, due));
            _quickViewTimer.Start();
        }
    }

    private QuickViewHost QuickViewHostFor(QuickViewer viewer)
    {
        if (_quickViewHosts.TryGetValue(viewer.Id, out var host))
        {
            _quickViewPool.Use(viewer.Id);
            return host;
        }
        if (_quickViewPool.Use(viewer.Id) is { } oldest && _quickViewHosts.TryGetValue(oldest, out var closing))
        {
            Diag.Info(QuickViewTarget, "a fourth viewer is needed; the one used longest ago closes", new LogField("viewer", oldest));
            CloseQuickViewHost(closing);
        }
        host = new QuickViewHost(viewer, QuickViewView.Pages);
        host.Page.ColorScheme = ToolColorScheme();
        host.MessageReceived += message => OnQuickViewMessage(host, message);
        host.Failed += reason => OnQuickViewerStopped(host, reason);
        _quickViewHosts[viewer.Id] = host;
        return host;
    }

    private void CloseQuickViewHost(QuickViewHost host)
    {
        host.Close();
        _quickViewHosts.Remove(host.Viewer.Id);
        _quickViewPool.Remove(host.Viewer.Id);
        if (_quickViewPage == host)
        {
            _quickViewPage = null;
        }
    }

    private async Task StartQuickViewerAsync(long token)
    {
        if (_quickView.Viewer is not { } viewer || _quickView.File is not { } file || !_quickView.OnLoadStarted(token))
        {
            return;
        }
        var host = QuickViewHostFor(viewer);
        _quickViewPage = host;
        host.Frame.Opacity = 0;
        host.Frame.IsHitTestVisible = false;
        Diag.Info(QuickViewTarget, "a viewer loads a file", new LogField("viewer", viewer.Id), new LogField("token", token),
            new LogField("cold", _quickView.Cold), new LogField("extension", file.Extension));
        var (url, problem) = await host.LoadAsync(file.Path);
        if (token != _quickView.Token)
        {
            return;
        }
        if (url is null)
        {
            Diag.Warn(QuickViewTarget, "a viewer could not load a file", new LogField("viewer", viewer.Id), new LogField("problem", problem));
            _quickView.OnLoadFailed(token, problem is { Length: > 0 } ? char.ToUpperInvariant(problem[0]) + problem[1..] + "." : "WebView2 did not load it.");
            RenderQuickView();
            WriteQuickViewLines();
            return;
        }
        OnQuickViewTimer();
    }

    private void OnQuickViewMessage(QuickViewHost host, QuickViewPageMessage message)
    {
        if (host != _quickViewPage || !_quickView.IsOpen || _quickView.File is not { } file)
        {
            return;
        }
        var token = _quickView.Token;
        switch (message.Type)
        {
            case "ready":
                if (_quickView.OnReady(token) && host.FileUrl is { } url)
                {
                    var size = QuickViewView.PageSize;
                    host.Post(QuickViewMessages.Show(token, file.Path, url, file.Name, file.Extension, _quickView.Claim ?? "", file.Size, file.Modified,
                        _quickViewThumbnail, QuickViewLook(), size.Width, size.Height, RootGrid.XamlRoot?.RasterizationScale ?? 1));
                    Diag.Info(QuickViewTarget, "a viewer is ready; the file was sent", new LogField("viewer", host.Viewer.Id), new LogField("token", token));
                }
                break;
            case "quickview-shown":
                if (_quickView.OnShown(message.Token, message.Details))
                {
                    GrantQuickViewKeys(host, token, message.Keys);
                    QuickViewView.ShowPicture(QuickViewPicture.Page, host.Frame);
                    MarkQuickViewFrame(token, QuickViewMoment.Page);
                }
                break;
            case "quickview-failed":
                if (_quickView.OnFailed(message.Token, message.Reason!, message.Message!))
                {
                    Diag.Info(QuickViewTarget, "a viewer cannot show a file", new LogField("viewer", host.Viewer.Id), new LogField("reason", message.Reason));
                }
                break;
            case "quickview-keys" when message.Token == token:
                GrantQuickViewKeys(host, token, message.Keys);
                break;
            case "quickview-render" when message.Token == token:
                _ = DrawForQuickViewPageAsync(host, token, message.Width, message.Height);
                break;
            case "quickview-system-preview" when message.Token == token:
                // Windows' preview handlers are not hosted in this version (ADR 0023, decision 7): the page hears so at once.
                host.Post(QuickViewMessages.SystemPreviewFailed(token, "Windows' preview handlers are not available in Quick View yet."));
                break;
        }
        RenderQuickView();
        WriteQuickViewLines();
        OnQuickViewTimer();
    }

    private void GrantQuickViewKeys(QuickViewHost host, long token, IReadOnlyList<string>? asked)
    {
        if (asked is null)
        {
            return;
        }
        var granted = QuickViewKeys.Grant(asked, _keys.Keymap);
        if (_quickView.OnKeysGranted(token, granted))
        {
            host.Post(QuickViewMessages.KeysGranted(token, granted));
            Diag.Info(QuickViewTarget, "keys granted to a viewer", new LogField("viewer", host.Viewer.Id), new LogField("asked", string.Join(",", asked)),
                new LogField("granted", string.Join(",", granted)));
        }
    }

    // A key no binding wants and the page was granted goes to the page (ADR 0023, decision 3, step 3). Called for a key the
    // keymap passed through while the keyboard is in the panel's pane.
    private bool SendQuickViewKey(KeyCombo combo, bool repeat)
    {
        if (!_quickView.IsOpen || _quickViewPage is not { } host || _quickView.GrantedKeys.Count == 0
            || RootGrid.XamlRoot is not { } root || FocusManager.GetFocusedElement(root) is not DependencyObject focused
            || !IsWithin(focused, _paneViews[_quickViewPane])
            || QuickViewKeys.PressedKey(combo, _quickView.GrantedKeys, _keys.Keymap) is not { } key)
        {
            return false;
        }
        host.Post(QuickViewMessages.Key(_quickView.Token, key, repeat));
        Diag.Info(QuickViewTarget, "a key went to the viewer", new LogField("key", key), new LogField("repeat", repeat));
        return true;
    }

    private async Task DrawForQuickViewPageAsync(QuickViewHost host, long token, int width, int height)
    {
        if (_quickViewDrawing)
        {
            _quickViewNextDrawing = (host, token, width, height);
            return;
        }
        if (_quickView.File is not { } file)
        {
            return;
        }
        _quickViewDrawing = true;
        try
        {
            var reply = await _session.RequestAsync(new RenderImageRequest(file.Path, (uint)Math.Min(QuickViewMessages.MaxRenderSize, Math.Max(width, height))));
            if (token != _quickView.Token || host != _quickViewPage)
            {
                return;
            }
            switch (reply)
            {
                case RenderedImageReply drawn when host.MapRender(drawn.Folder) is { } url:
                    host.Post(QuickViewMessages.Rendered(token, url, (int)drawn.Width, (int)drawn.Height));
                    break;
                case RenderedImageReply:
                    host.Post(QuickViewMessages.RenderFailed(token, "The drawing could not be served to the page."));
                    break;
                case ErrorReply error:
                    host.Post(QuickViewMessages.RenderFailed(token, error.Message));
                    break;
            }
        }
        catch (IOException error)
        {
            host.Post(QuickViewMessages.RenderFailed(token, error.Message));
        }
        finally
        {
            _quickViewDrawing = false;
            if (_quickViewNextDrawing is { } next && next.Token == _quickView.Token)
            {
                _quickViewNextDrawing = null;
                _ = DrawForQuickViewPageAsync(next.Host, next.Token, next.Width, next.Height);
            }
        }
    }

    // The page's process ended or hung: the panel keeps the thumbnail and says so; the WebView2 is made anew at the next file.
    private void OnQuickViewerStopped(QuickViewHost host, string reason)
    {
        Diag.Warn(QuickViewTarget, "a viewer stopped", new LogField("viewer", host.Viewer.Id), new LogField("reason", reason));
        CloseQuickViewHost(host);
        if (_quickView.OnStopped(host.Viewer.Id))
        {
            RenderQuickView();
            WriteQuickViewLines();
        }
        if (_quickView.IsOff(host.Viewer.Id))
        {
            Diag.Warn(QuickViewTarget, "a viewer stopped three times in a minute; it is off until the window restarts", new LogField("viewer", host.Viewer.Id));
        }
    }

    private QuickViewLook QuickViewLook() => QuickViewTheme.From(_themes.Current, RootGrid.ActualTheme == ElementTheme.Light);

    private void SendQuickViewTheme()
    {
        foreach (var host in _quickViewHosts.Values)
        {
            host.Page.ColorScheme = ToolColorScheme();
        }
        if (_quickView is { IsOpen: true } && _quickViewPage is { } page && _quickView.Phase is ViewerPhase.Loading or ViewerPhase.Shown)
        {
            page.Post(QuickViewMessages.Theme(_quickView.Token, QuickViewLook()));
        }
    }

    // ----- The install offer -----

    private async Task AskQuickViewOfferAsync(long token, string name)
    {
        _quickViewAsking++;
        try
        {
            var reply = await _session.RequestAsync(new QuickViewOfferRequest(name));
            var (item, reason) = reply switch
            {
                QuickViewOfferReply offer => (offer.Item, offer.Reason),
                _ => (null, OfferReasons.Offline),
            };
            if (_quickView.OnOffer(token, item, reason))
            {
                Diag.Info(QuickViewTarget, "quick view offer", new LogField("item", item?.Id), new LogField("reason", reason));
                RenderQuickView();
            }
        }
        catch (IOException)
        {
            _quickView.OnOffer(token, null, OfferReasons.Offline);
            RenderQuickView();
        }
        finally
        {
            _quickViewAsking--;
        }
    }

    // The offer's one click: the marketplace's own install (install_extension); the new table then shows the file in the viewer.
    private async Task InstallQuickViewOfferAsync(string? requestId)
    {
        if (_quickView.OfferItem is not { } item || !_quickView.OnInstallStarted())
        {
            return;
        }
        Diag.Info(QuickViewTarget, "the offered viewer installs", new LogField("item", item.Id));
        RenderQuickView();
        var outcome = await _market.InstallAsync(item.Id, requestId);
        if (_quickView.OnInstallEnded(item.Id, outcome.Ok, outcome.Error))
        {
            RenderQuickView();
        }
        Diag.Info(QuickViewTarget, "the offered viewer's install ended", new LogField("item", item.Id), new LogField("ok", outcome.Ok),
            new LogField("error", outcome.Error));
    }

    private void OnQuickViewInstallProgress()
    {
        if (_quickView is { IsOpen: true, Offer: OfferPhase.Installing, OfferItem: { } item } && _market.InstallOf(item.Id) is { } progress
            && _quickView.OnInstallProgress(item.Id, progress.Bytes, progress.Total))
        {
            RenderQuickView();
        }
    }

    // quickView.installViewer: the panel's offer when it shows one; from the palette the panel is closed, so the cursor file's.
    private async Task InstallViewerFromCommandAsync(CommandInvocation invocation)
    {
        if (_quickView.IsOpen && _quickView.Offer is OfferPhase.Item or OfferPhase.InstallFailed)
        {
            await InstallQuickViewOfferAsync(invocation.RequestId);
            return;
        }
        if (Active.Search is not null || Active.EntryAt(Active.FocusIndex) is not { IsFolder: false } entry)
        {
            ShowNotice("Put the cursor on a file first: Quick View offers the viewer for its kind.");
            return;
        }
        if (_quickViewTable.Match(entry.Name).Viewer is { } installed)
        {
            ShowNotice($"{installed.Name} shows {entry.Name} already: press Space.");
            return;
        }
        QuickViewOfferReply? offer;
        try
        {
            offer = await _session.RequestAsync(new QuickViewOfferRequest(entry.Name)) as QuickViewOfferReply;
        }
        catch (IOException error)
        {
            ShowNotice(error.Message, isError: true);
            return;
        }
        if (offer?.Item is not { } item)
        {
            ShowNotice(offer?.Reason == OfferReasons.NoItem
                ? $"No viewer for {(DisplayFormat.Extension(entry.Name) is { Length: > 0 } ext ? $".{ext} files" : entry.Name)}."
                : "The marketplace cannot be reached, so no viewer can be offered.");
            return;
        }
        ShowNotice($"Installing {item.Name}…");
        var outcome = await _market.InstallAsync(item.Id, invocation.RequestId);
        ShowNotice(outcome.Ok ? $"{item.Name} is installed: Space shows {entry.Name} in it." : $"The install failed: {outcome.Error?.TrimEnd('.')}.", isError: !outcome.Ok);
    }

    // ----- The viewer choice (quickView.viewers, three ways: the panel, the palette, the file) -----

    private async Task ChooseViewerFromPaletteAsync(CommandInvocation invocation)
    {
        if (Active.Search is not null || Active.EntryAt(Active.FocusIndex) is not { IsFolder: false } entry)
        {
            ShowNotice("Put the cursor on a file first: Quick View chooses the viewer of its kind.");
            return;
        }
        var match = _quickViewTable.Match(entry.Name);
        if (match.Claim is not { } pattern || match.Choices.Count == 0)
        {
            ShowNotice($"No installed viewer claims {entry.Name}. Viewers are opt-in: find one in the marketplace (Ctrl+Shift+X).");
            return;
        }
        var current = match.Off ? QuickViewChoice.NoViewer : match.Viewer?.Id;
        var rows = match.Choices.Select(v => new PromptRow(v.Name, v.Id, v.Id == current ? "" : "")).ToList();
        rows.Add(new PromptRow("No viewer (thumbnail only)", QuickViewChoice.NoViewer, match.Off ? "" : ""));
        var answer = await PromptView.ShowAsync(new PromptRequest($"Viewer for {pattern}", PromptKind.Pick, rows,
            Placeholder: "Type to narrow the list",
            Hint: "Quick View uses it for every file of this kind; it is saved as quickView.viewers in cabinetos.json."));
        if (answer?.Row is { } row)
        {
            await ChooseQuickViewerAsync(row.Detail, pattern, "palette");
        }
    }

    // Writes the choice into quickView.viewers (the whole object: a key such as *.pdf has a dot); the core's new table follows.
    private async Task ChooseQuickViewerAsync(string viewer, string? pattern, string trigger)
    {
        pattern ??= _quickView.Claim;
        if (pattern is null)
        {
            return;
        }
        JsonElement? current = null;
        try
        {
            if (await _session.RequestAsync(new GetValueRequest(QuickViewChoice.Setting)) is ValueReply value)
            {
                current = value.Value;
            }
        }
        catch (IOException error)
        {
            ShowNotice(error.Message, isError: true);
            return;
        }
        var refusal = await _settingsWriter.SetOrRefusalAsync(QuickViewChoice.Setting, QuickViewChoice.With(current, pattern, viewer));
        Diag.Info(QuickViewTarget, "quick view viewer chosen", new LogField("pattern", pattern), new LogField("viewer", viewer),
            new LogField("trigger", trigger), new LogField("refused", refusal));
        if (refusal is not null)
        {
            ShowNotice(refusal, isError: true);
        }
        FocusQuickViewList();
    }

    // ----- The snapshot aid -----

    // quickview:<label>: what the panel shows, for the end-to-end tests.
    private void LogQuickViewState(string label)
    {
        var look = QuickViewView.Describe();
        var (width, height) = QuickViewView.CardSize;
        Diag.Info("cabinetos_ui::snapshot", "quick view state", new LogField("label", label), new LogField("state", _quickView.StateName()),
            new LogField("open", QuickViewView.IsOpen), new LogField("file", _quickView.File?.Name), new LogField("viewer", _quickView.Viewer?.Id ?? ""),
            new LogField("token", _quickView.Token), new LogField("picture", _quickView.Picture.ToString().ToLowerInvariant()),
            new LogField("position", _quickView.File is { } shownFile ? $"{shownFile.Position + 1} of {shownFile.Count}" : ""),
            new LogField("line", look.Status), new LogField("offer", look.Offer), new LogField("button", look.Button),
            new LogField("chooser", look.Viewer), new LogField("facts", look.Facts), new LogField("card", look.Card),
            new LogField("keys", string.Join(",", _quickView.GrantedKeys)), new LogField("asking", _quickViewAsking),
            new LogField("alive", string.Join(",", _quickViewPool.Alive)), new LogField("overlays", OpenOverlayNames()),
            new LogField("marked", Active.MarkedCount()), new LogField("pane_path", Active.Path),
            new LogField("width", Math.Round(width, 1)), new LogField("height", Math.Round(height, 1)),
            new LogField("area_width", Math.Round(QuickViewView.ActualWidth, 1)), new LogField("area_height", Math.Round(QuickViewView.ActualHeight, 1)),
            new LogField("focus_in_list", _quickViewPane >= 0 && RootGrid.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject f && IsWithin(f, _paneViews[_quickViewPane])));
    }

    // until:quickview-…: the conditions the end-to-end tests wait for.
    private bool QuickViewConditionMet(string condition) => condition switch
    {
        "quickview-shown" => _quickView.IsOpen && _quickView.Phase == ViewerPhase.Shown && _quickView.Timing.FullMs is not null,
        "quickview-thumbnail" => _quickView.IsOpen && _quickView.Timing.ThumbnailMs is not null,
        "quickview-closed" => !_quickView.IsOpen,
        "quickview-idle" => _quickView.IsOpen && _quickViewAsking == 0 && _quickView.Offer != OfferPhase.Asking
            && _quickView.Phase is not (ViewerPhase.Resting or ViewerPhase.Starting) && _quickView.Timing.CardMs is not null,
        _ when condition.StartsWith("quickview-state:", StringComparison.Ordinal) => _quickView.StateName() == condition["quickview-state:".Length..],
        _ when condition.StartsWith("quickview-viewer:", StringComparison.Ordinal) => _quickViewTable.Viewer(condition["quickview-viewer:".Length..]) is not null,
        _ => true,
    };

    // crash:quickview:<id>: ends that viewer's browser process, as a crash would.
    private QuickViewHost? QuickViewHostToCrash(string which) =>
        which.StartsWith("quickview:", StringComparison.Ordinal) && _quickViewHosts.TryGetValue(which["quickview:".Length..], out var host) ? host : null;
}
