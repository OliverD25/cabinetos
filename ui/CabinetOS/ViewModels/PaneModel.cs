using System.Diagnostics;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Tabs;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CabinetOS.ViewModels;

/// <summary>
/// How long one navigation took, for the "listing shown" log line; <paramref name="Kept"/> when a tab
/// came back with the listing it kept (<see cref="ParkedListing"/>) and the core was not asked.
/// </summary>
public sealed record NavigationTiming(string RequestId, string Path, int Entries, ulong CoreUs, long StartedTicks, long RepliedTicks, bool Kept = false);

/// <summary>One entry of a pane, as a command needs it.</summary>
public sealed record PaneEntry(int Index, string Name, string Path, bool IsFolder, ulong Size);

/// <summary>
/// What a pane in search mode shows (docs/ui.md, "Search"): the title
/// ("Search: budget · 12 hits · index · 1.2 ms"), what was searched, the note
/// under the title, the "whole volume" box, and the hits (null while the
/// first answer is on its way).
/// </summary>
public sealed record PaneSearch(string Header, string Scope, string Note, bool WholeVolume, SearchRows? Rows);

/// <summary>
/// One file pane: its folder, its listing in shared memory, the selection and
/// the history. The core lists, sorts and watches; the pane only asks and shows.
/// </summary>
public sealed class PaneModel : ObservableObject, IRowDetails
{
    private const string Target = "cabinetos_ui::pane";

    private readonly ICoreChannel _core;
    private readonly EntryDetailsCache _details = new();
    private readonly ExtensionDetails _known;
    private readonly Services.IconCache _icons;
    private bool _detailsUnavailable;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private ListingView? _view;
    private ulong _listingId;
    private int _navigation;
    private string _path = "";
    private ListingRows? _rows;
    private bool _isActive;
    private bool _isDual = true;
    private string? _message;
    private string? _expectedName;
    private TaskCompletionSource<int>? _expectedListed;
    private PaneSearch? _search;
    private SortSpec? _sort;
    private SortSpec _defaultSort = new(PaneSort.Name, false);
    private bool _isLocked;
    private readonly PaneFind _find;
    // The filter the rows were made with, so a change of it makes new rows.
    private IReadOnlyList<int>? _rowsVisible;
    // The listing of the tab that went behind last, kept for a while so a switch back does not list again.
    private readonly ParkedListing _kept;
    private CancellationTokenSource? _keptTimer;
    // The listing of the tab going behind while the next one is listed: kept once the pane lets go of it.
    private ParkedListing.Entry? _keepOnLeave;

    /// <summary>
    /// Creates pane <paramref name="index"/>, asking <paramref name="core"/> for
    /// its listings; <paramref name="known"/> and <paramref name="icons"/> are
    /// shared by both panes.
    /// </summary>
    internal PaneModel(int index, ICoreChannel core, ExtensionDetails known, Services.IconCache icons)
    {
        Index = index;
        _core = core;
        _known = known;
        _icons = icons;
        KnownDetails = new GuessedDetails(known, icons);
        Selection.Changed += () =>
        {
            OnPropertyChanged(nameof(Selection));
            OnPropertyChanged(nameof(FocusName));
        };
        SearchSelection.Changed += () => OnPropertyChanged(nameof(Selection));
        _find = new PaneFind(core, Selection);
        _find.Changed += OnFindChanged;
        _kept = new ParkedListing(core, () => Environment.TickCount64,
            ParkedListing.LifetimeFrom(Environment.GetEnvironmentVariable(ParkedListing.LifetimeEnv)));
    }

    /// <summary>
    /// The find widget of the tab in front (Ctrl+F; docs/ui.md, "Find in
    /// pane"): its text, and the rows of the listing it shows. The other
    /// pane has its own.
    /// </summary>
    public PaneFind Find => _find;

    /// <summary>Filters the listing by <paramref name="text"/> (the find widget's box); an empty text shows every row.</summary>
    public Task<bool> SetFindTextAsync(string text, string? requestId = null)
    {
        if (!_find.IsOpen)
        {
            _find.Open(text);
        }
        return _view is { } view
            ? _find.SetQueryAsync(text, _listingId, view.Generation, view.Count, requestId)
            : Task.FromResult(false);
    }

    /// <summary>Asks the core again for the find text's rows, after the listing changed under it.</summary>
    public Task RefindAsync() =>
        _find.Query is { } query && _view is { } view
            ? _find.SetQueryAsync(query, _listingId, view.Generation, view.Count)
            : Task.CompletedTask;

    // The find's rows changed: the list shows them (a new object, so the view resets its repeater).
    private void OnFindChanged()
    {
        if (_view is { } view && !ReferenceEquals(_rowsVisible, Selection.Visible))
        {
            _rowsVisible = Selection.Visible;
            Rows = new ListingRows(view, this, _rowsVisible);
        }
        OnPropertyChanged(nameof(Find));
    }

    /// <summary>
    /// The search results the pane shows instead of its folder, or null. The
    /// listing stays underneath and comes back when the search is left.
    /// </summary>
    public PaneSearch? Search
    {
        get => _search;
        set
        {
            var newRows = !ReferenceEquals(value?.Rows, _search?.Rows);
            _search = value;
            if (newRows)
            {
                SearchSelection.Reset(value?.Rows?.Count ?? 0, 0);
            }
            OnPropertyChanged();
        }
    }

    /// <summary>The focus and selection among the hits (the listing keeps its own).</summary>
    public SelectionModel SearchSelection { get; } = new();

    /// <summary>The selection of what the pane shows: the hits in search mode, else the listing.</summary>
    public SelectionModel CurrentSelection => _search is null ? Selection : SearchSelection;

    /// <summary>How many rows the pane shows: hits in search mode, else the entries a find filter leaves (all without one).</summary>
    public int ShownCount => _search is null ? Selection.ShownCount : _search.Rows?.Count ?? 0;

    /// <summary>The focused hit in search mode, or null.</summary>
    public SearchRowItem? FocusedHit =>
        _search?.Rows is { } rows && (uint)SearchSelection.Focus < (uint)rows.Count ? rows[SearchSelection.Focus] : null;

    /// <summary>Type names and icons by extension only: for rows that are not entries of this listing (search hits).</summary>
    public IRowDetails KnownDetails { get; }

    /// <summary>Raised when something the user should read goes to the status bar.</summary>
    public event Action<string>? Notice;

    /// <summary>Raised with (first row, count) when type names and icon keys arrived for rows.</summary>
    public event Action<int, int>? DetailsArrived;

    /// <summary>
    /// Raised when a listing is on show: a folder opened, or a watched one listed again. The second
    /// argument is the navigation's request ID, or null for a refresh. Folder sizes follow it
    /// (<c>panes.folderSizes</c>).
    /// </summary>
    public event Action<PaneModel, string?>? Listed;

    /// <inheritdoc/>
    public EntryDetail? Detail(int index, ReadOnlySpan<char> name, bool isFolder) =>
        _details.Get(index) ?? _known.Guess(name, isFolder);

    /// <inheritdoc/>
    public Microsoft.UI.Xaml.Media.ImageSource? Icon(string key) => _icons.Get(key);

    /// <summary>
    /// Asks the core for the type names and icons of rows <paramref name="first"/>
    /// to <paramref name="last"/>, a page at a time, each page once per section
    /// (<c>describe_entries</c>, protocol 9).
    /// </summary>
    public void EnsureDetails(int first, int last)
    {
        if (_detailsUnavailable || _view is not { } view || _listingId == 0)
        {
            return;
        }
        foreach (var (from, count) in _details.TakePagesToRequest(first, last, view.Count))
        {
            _ = FetchDetailsAsync(from, count);
        }
    }

    /// <summary>
    /// The same for the rows shown at <paramref name="firstPosition"/> to
    /// <paramref name="lastPosition"/>: under a find filter they are not
    /// neighbours in the listing, so each asks for its own page (once).
    /// </summary>
    public void EnsureShownDetails(int firstPosition, int lastPosition)
    {
        if (!Selection.IsFiltered)
        {
            EnsureDetails(firstPosition, lastPosition);
            return;
        }
        for (var position = firstPosition; position <= lastPosition; position++)
        {
            var index = Selection.IndexAt(position);
            EnsureDetails(index, index);
        }
    }

    private async Task FetchDetailsAsync(uint from, uint count)
    {
        var listing = _details.ListingId;
        var generation = _details.Generation;
        CoreReply reply;
        try
        {
            reply = await _core.RequestAsync(new DescribeEntriesRequest(listing, from, count));
        }
        catch (IOException)
        {
            ForgetIfCurrent();
            return;
        }
        var started = FrameParts.Start();
        switch (reply)
        {
            case EntryDetailsReply details when _details.Apply(details) && _view is { } view:
                for (var i = 0; i < details.Details.Count; i++)
                {
                    var index = (int)details.From + i;
                    _known.Learn(view.NameSpan(index), view.IsFolder(index), details.Details[i]);
                }
                FrameParts.Stop(FramePart.Details, started);
                started = 0;
                DetailsArrived?.Invoke((int)details.From, details.Details.Count);
                break;
            case EntryDetailsReply:
                // For a section that has been replaced meanwhile; the new one asks again.
                break;
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                // A core before protocol 9: the built-in type text and glyphs stay.
                _detailsUnavailable = true;
                break;
            case ErrorReply { Code: ErrorCodes.NoSuchListing }:
                break;
            default:
                ForgetIfCurrent();
                break;
        }
        FrameParts.Stop(FramePart.Details, started);

        void ForgetIfCurrent()
        {
            if (_details.ListingId == listing && _details.Generation == generation)
            {
                _details.Forget(from);
            }
        }
    }

    /// <summary>0 for the left pane, 1 for the right; the two change places when the panes are swapped.</summary>
    public int Index { get; internal set; }

    /// <summary>The folder shown.</summary>
    public string Path
    {
        get => _path;
        private set
        {
            if (SetProperty(ref _path, value))
            {
                OnPropertyChanged(nameof(FolderName));
                OnPropertyChanged(nameof(CanGoUp));
            }
        }
    }

    /// <summary>The folder's own name, for the pane header.</summary>
    public string FolderName => DisplayFormat.FolderName(Path);

    /// <summary>The rows; a new object for each listing, so the view resets its repeater.</summary>
    public ListingRows? Rows
    {
        get => _rows;
        private set
        {
            if (SetProperty(ref _rows, value))
            {
                OnPropertyChanged(nameof(Count));
            }
        }
    }

    /// <summary>Entries in the listing.</summary>
    public int Count => _rows?.Count ?? 0;

    /// <summary>The focus, the anchor and the selected rows.</summary>
    public SelectionModel Selection { get; } = new();

    /// <summary>The focused row (the keyboard's row), or -1.</summary>
    public int FocusIndex => Selection.Focus;

    /// <summary>The focused entry's name.</summary>
    public string? FocusName => _view is { } view && Selection.Focus >= 0 ? view.Name(Selection.Focus) : null;

    /// <summary>Whether this pane has the focus (or is the only one).</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                OnPropertyChanged(nameof(ShowsActiveStroke));
                OnPropertyChanged(nameof(HasBrightTitle));
            }
        }
    }

    /// <summary>Whether two panes are shown; in single mode the pane looks active always.</summary>
    public bool IsDual
    {
        get => _isDual;
        set
        {
            if (SetProperty(ref _isDual, value))
            {
                OnPropertyChanged(nameof(ShowsActiveStroke));
                OnPropertyChanged(nameof(HasBrightTitle));
            }
        }
    }

    /// <summary>The brighter stroke of the design's active pane (dual mode only).</summary>
    public bool ShowsActiveStroke => _isActive && _isDual;

    /// <summary>The white title of the design's active pane.</summary>
    public bool HasBrightTitle => _isActive || !_isDual;

    /// <summary>Text in the pane body instead of rows: empty folder, or why it could not be listed.</summary>
    public string? Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    /// <summary>The listing's ID in the core, for its events.</summary>
    public ulong ListingId => _listingId;

    /// <summary>The listing on screen.</summary>
    public ListingView? View => _view;

    /// <summary>Whether there is a folder to go back to (a locked tab stays where it is).</summary>
    public bool CanGoBack => _back.Count > 0 && !_isLocked;

    /// <summary>Whether there is a folder to go forward to (a locked tab stays where it is).</summary>
    public bool CanGoForward => _forward.Count > 0 && !_isLocked;

    /// <summary>
    /// Whether the tab in front is locked (Phase 12): going into another
    /// folder then opens a new tab (<see cref="LockedNavigation"/>), and Back
    /// and Forward do nothing.
    /// </summary>
    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (SetProperty(ref _isLocked, value))
            {
                OnPropertyChanged(nameof(CanGoBack));
                OnPropertyChanged(nameof(CanGoForward));
            }
        }
    }

    /// <summary>
    /// What a locked tab does instead of listing another folder: (folder,
    /// request ID, name to select) go to a new tab. Returns whether the
    /// folder was shown there.
    /// </summary>
    public Func<string, string?, string?, Task<bool>>? LockedNavigation { get; set; }

    /// <summary>Whether the folder has a parent.</summary>
    public bool CanGoUp => DisplayFormat.Parent(Path) is not null;

    /// <summary>The last navigation's timing, until the view logs its first paint.</summary>
    public NavigationTiming? PendingTiming { get; set; }

    /// <summary>
    /// The pane's own order (Ctrl+F3 to Ctrl+F6), sent with every listing of
    /// this pane; null leaves it to <c>panes.sort</c>.
    /// </summary>
    public SortSpec? Sort => _sort;

    /// <summary><c>panes.sort</c>, the order of a pane without its own.</summary>
    public SortSpec DefaultSort
    {
        get => _defaultSort;
        set
        {
            if (SetProperty(ref _defaultSort, value))
            {
                OnPropertyChanged(nameof(EffectiveSort));
            }
        }
    }

    /// <summary>The order the listing is in: the pane's own, else <c>panes.sort</c>.</summary>
    public SortSpec EffectiveSort => _sort ?? _defaultSort;

    /// <summary>Lists the folder again in <paramref name="sort"/>, keeping the focused and the marked entries.</summary>
    public async Task<bool> SortAsync(SortSpec sort, string? requestId = null)
    {
        var previous = _sort;
        _sort = sort;
        OnPropertyChanged(nameof(EffectiveSort));
        if (await RefreshAsync(requestId))
        {
            return true;
        }
        _sort = previous;
        OnPropertyChanged(nameof(EffectiveSort));
        return false;
    }

    /// <summary>Entry <paramref name="index"/> of the listing, or null.</summary>
    public PaneEntry? EntryAt(int index)
    {
        if (_view is not { } view || (uint)index >= (uint)view.Count)
        {
            return null;
        }
        var name = view.Name(index);
        return new PaneEntry(index, name, DisplayFormat.Join(Path, name), view.IsFolder(index), view.Size(index));
    }

    /// <summary>What a command acts on: the selected entries, or the focused one (listing order).</summary>
    public IReadOnlyList<PaneEntry> Targets() =>
        Selection.Targets().Select(EntryAt).OfType<PaneEntry>().ToList();

    /// <summary>
    /// How many rows are selected, and the bytes of the selected files and
    /// measured folders: the status bar's "12 selected, 1.4 MB". A folder
    /// counts only once it was measured (Space, Shift+Alt+Enter); the listing
    /// has no size for it. Nothing is read from the disk.
    /// </summary>
    public (int Count, ulong Bytes, bool AnySize) SelectionSize()
    {
        if (_view is not { } view)
        {
            return (0, 0, false);
        }
        ulong bytes = 0;
        var anySize = false;
        foreach (var index in Selection.SelectedUnordered)
        {
            if (!view.IsFolder(index))
            {
                bytes += view.Size(index);
                anySize = true;
            }
            else if (!Sizes.IsEmpty && Sizes.Get(view.NameSpan(index)) is { } measured)
            {
                bytes += measured.Bytes;
                anySize = true;
            }
        }
        return (Selection.SelectedCount, bytes, anySize);
    }

    /// <summary>
    /// The full paths of the marked rows in listing order, at most
    /// <paramref name="limit"/> of them (the window's state message): only
    /// real marks, not the cursor row the Windows style selects.
    /// </summary>
    public IReadOnlyList<string> MarkedPaths(int limit)
    {
        if (_view is not { } view || !Selection.HasMarks)
        {
            return [];
        }
        return [.. Selection.SelectedUnordered.Where(index => (uint)index < (uint)view.Count).Order().Take(limit).Select(index => DisplayFormat.Join(Path, view.Name(index)))];
    }

    /// <summary>How many rows <see cref="MarkedPaths"/> would list with no limit: the true count behind a cut list (the window's state message).</summary>
    public int MarkedCount() =>
        _view is { } view && Selection.HasMarks ? Selection.SelectedUnordered.Count(index => (uint)index < (uint)view.Count) : 0;

    /// <summary>
    /// A name for a new entry that the listing does not have yet:
    /// <paramref name="baseName"/>, then "<paramref name="baseName"/> (2)", …
    /// </summary>
    public string FreeName(string baseName)
    {
        if (_view is not { } view || view.IndexOfName(baseName) < 0)
        {
            return baseName;
        }
        for (var n = 2; ; n++)
        {
            var candidate = $"{baseName} ({n})";
            if (view.IndexOfName(candidate) < 0)
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Selects <paramref name="name"/> once a listing has it: at once when the
    /// one on screen does, else at the next refresh from the core's watcher.
    /// Returns the row, or -1 when it did not show up in time.
    /// </summary>
    public async Task<int> SelectWhenListedAsync(string name, TimeSpan timeout)
    {
        var index = _view?.IndexOfName(name) ?? -1;
        if (index >= 0)
        {
            Selection.Reset(Count, index);
            return index;
        }
        ExpectName(name);
        var listed = _expectedListed!.Task;
        if (await Task.WhenAny(listed, Task.Delay(timeout)) == listed)
        {
            return listed.Result;
        }
        if (_expectedListed?.Task == listed)
        {
            ForgetExpectedName();
        }
        return -1;
    }

    /// <summary>
    /// The next refresh that lists <paramref name="name"/> selects it: after a
    /// rename, the entry keeps its ID on NTFS, but not on file systems without
    /// file IDs (the ID is then a hash of the name), so the name decides.
    /// </summary>
    public void ExpectName(string name)
    {
        _expectedListed?.TrySetResult(-1);
        _expectedName = name;
        _expectedListed = new TaskCompletionSource<int>();
    }

    /// <summary>Stops waiting for a name (the rename failed).</summary>
    public void ForgetExpectedName()
    {
        _expectedListed?.TrySetResult(-1);
        _expectedName = null;
        _expectedListed = null;
    }

    /// <summary>
    /// Lists <paramref name="path"/> and shows it. <paramref name="requestId"/>
    /// is the command's ID, so the key press and the core's work share one ID
    /// in both logs (Article 12).
    /// </summary>
    public async Task<bool> NavigateAsync(string path, string? requestId = null, NavigationKind kind = NavigationKind.New, string? selectName = null, bool notify = true)
    {
        if (kind == NavigationKind.New && LockedNavigation is { } redirect && TabRules.OpensInNewTab(_isLocked, _path, path))
        {
            return await redirect(path, requestId, selectName);
        }
        var navigation = ++_navigation;
        var started = Stopwatch.GetTimestamp();
        var request = new ListDirectoryRequest(path) { Watch = true, Id = requestId ?? "", Sort = _sort };
        CoreReply reply;
        try
        {
            reply = await _core.RequestAsync(request);
        }
        catch (IOException error)
        {
            Fail(path, error.Message, notify);
            return false;
        }

        if (navigation != _navigation)
        {
            // A newer navigation started meanwhile; this listing is no longer wanted.
            if (reply is ListingOpenedReply stale)
            {
                stale.TakeSection()?.Dispose();
                CloseListing(stale.ListingId);
            }
            return false;
        }
        if (reply is ErrorReply refused)
        {
            Fail(path, Describe(refused), notify);
            return false;
        }
        if (reply is not ListingOpenedReply opened || opened.TakeSection() is not { } section)
        {
            Fail(path, $"unexpected reply {reply.GetType().Name}", notify);
            return false;
        }

        ListingView view;
        try
        {
            view = ListingView.Open(section, opened.SectionSize);
        }
        catch (Exception failure) when (failure is InvalidDataException or IOException)
        {
            CloseListing(opened.ListingId);
            Fail(path, failure.Message, notify);
            return false;
        }

        var previousPath = Path;
        var samePlace = string.Equals(previousPath, path, StringComparison.OrdinalIgnoreCase);
        switch (kind)
        {
            case NavigationKind.New when previousPath.Length > 0 && !string.Equals(previousPath, path, StringComparison.OrdinalIgnoreCase):
                _back.Push(previousPath);
                _forward.Clear();
                break;
            case NavigationKind.Back:
                _forward.Push(previousPath);
                break;
            case NavigationKind.Forward:
                _back.Push(previousPath);
                break;
        }

        var oldView = _view;
        var oldListing = _listingId;
        if (!Sizes.IsEmpty && !string.Equals(previousPath, path, StringComparison.OrdinalIgnoreCase))
        {
            ForgetSizes();
        }
        _view = view;
        _listingId = opened.ListingId;
        _details.Reset(opened.ListingId, view.Generation);
        PendingTiming = new NavigationTiming(request.Id, path, view.Count, opened.ElapsedUs, started, Stopwatch.GetTimestamp());
        Path = path;
        Drives.Remember(path);
        Message = view.Count == 0 ? "This folder is empty." : null;
        _rowsVisible = null;
        Rows = new ListingRows(view, this);
        var select = selectName is null ? -1 : view.IndexOfName(selectName);
        Selection.Reset(view.Count, select >= 0 ? select : 0);
        // The find belongs to the folder: another folder closes it, the same one (a reload) asks again.
        if (_find.IsOpen)
        {
            if (samePlace)
            {
                _ = RefindAsync();
            }
            else
            {
                _find.Close();
            }
        }
        RaiseHistoryChanged();
        var keep = _keepOnLeave;
        _keepOnLeave = null;
        LetGo(oldView, oldListing, keep);
        Navigated?.Invoke(this, kind, samePlace);
        Listed?.Invoke(this, requestId);
        return true;
    }

    /// <summary>
    /// Raised when the pane listed a folder by any way but the column view's
    /// own moves (a crumb, Back, the sidebar, a tab, a reload): the kind of
    /// navigation, and whether the folder is the one it showed. A column view
    /// starts over from another folder (ADR 0016).
    /// </summary>
    public event Action<PaneModel, NavigationKind, bool>? Navigated;

    /// <summary>
    /// The column view's keyboard moves to another column (ADR 0016): the
    /// pane's listing, with its cursor and marks, goes into a column of its
    /// own, which is returned, and <paramref name="incoming"/>'s listing
    /// becomes the pane's, with its cursor and marks, without asking the core
    /// again. The history stays: the columns are the path. Returns null when
    /// the pane had no listing.
    /// </summary>
    internal ColumnListing? SwapListing(ColumnListing incoming)
    {
        ForgetExpectedName();
        if (_find.IsOpen)
        {
            // The find belongs to the folder, as another folder closes it in the list.
            _find.Close();
        }
        if (!Sizes.IsEmpty)
        {
            ForgetSizes();
        }
        var outgoing = _view is { } view
            ? new ColumnListing(Path, view, _listingId, KnownDetails, Selection.Style, Selection.Focus, Selection.Anchor, [.. Selection.AllSelected])
            : null;
        var focus = incoming.Selection.Focus;
        var anchor = incoming.Selection.Anchor;
        var selected = incoming.Selection.AllSelected.ToList();
        var (newView, listingId) = incoming.HandOver();
        _view = newView;
        _listingId = listingId;
        _details.Reset(listingId, newView.Generation);
        Path = incoming.Path;
        Drives.Remember(incoming.Path);
        Message = newView.Count == 0 ? "This folder is empty." : null;
        _rowsVisible = null;
        Rows = new ListingRows(newView, this);
        Selection.Restore(newView.Count, selected, focus, anchor);
        return outgoing;
    }

    /// <summary>Lists the same folder again, keeping the focused entry.</summary>
    public Task<bool> ReloadAsync(string? requestId = null) =>
        NavigateAsync(Path, requestId, NavigationKind.Reload, FocusName);

    /// <summary>
    /// Lists the same folder again (<c>view.refresh</c>, Ctrl+R), keeping the
    /// focused entry and the marked ones, found again by name.
    /// </summary>
    public async Task<bool> RefreshAsync(string? requestId = null)
    {
        var marked = AllSelectedNames();
        if (!await ReloadAsync(requestId))
        {
            return false;
        }
        if (marked.Count > 0 && _view is { } view)
        {
            Selection.Restore(view.Count, view.IndexesOfNames(marked), Selection.Focus, Selection.Anchor);
        }
        return true;
    }

    /// <summary>The names of the selected entries, in listing order.</summary>
    public IReadOnlyList<string> SelectedNames() =>
        _view is { } view ? Selection.Selected.Select(view.Name).ToList() : [];

    /// <summary>The names of every selected entry, the ones a find filter hides too, in listing order.</summary>
    public IReadOnlyList<string> AllSelectedNames() =>
        _view is { } view ? Selection.AllSelected.Order().Select(view.Name).ToList() : [];

    /// <summary>The marks a file command or an unmark cleared last, for Restore Selection (Num /).</summary>
    public MarkMemory SavedMarks { get; } = new();

    /// <summary>The folder this pane last showed on each drive, where the drive list (Alt+F1, Alt+F2) goes.</summary>
    public DriveMemory Drives { get; } = new();

    /// <summary>The measured sizes of this folder's folders (Space, Shift+Alt+Enter), until the pane leaves it.</summary>
    public FolderSizes Sizes { get; } = new();

    /// <inheritdoc/>
    public FolderSize? MeasuredSize(ReadOnlySpan<char> name) => Sizes.Get(name);

    /// <summary>
    /// Asks the core to count <paramref name="paths"/> (<c>measure_paths</c>);
    /// the Size column shows the running totals the events bring. Returns the
    /// core's reply (an error, or <c>measure_started</c>), or null when every
    /// path is being counted already.
    /// </summary>
    public async Task<CoreReply?> MeasureAsync(IReadOnlyList<string> paths, string? requestId = null)
    {
        var wanted = Sizes.NotCounting(paths);
        if (wanted.Count == 0)
        {
            return null;
        }
        var asked = _path;
        var reply = await _core.RequestAsync(new MeasurePathsRequest(wanted) { Id = requestId ?? "" });
        // Right after the reply, before any of its events: the client hands the UI a reply first.
        if (reply is MeasureStartedReply started)
        {
            if (!string.Equals(_path, asked, StringComparison.OrdinalIgnoreCase))
            {
                // The pane left the folder while the core started the count: these names mean nothing here.
                CancelMeasures([started.MeasureId], "left the folder");
                return reply;
            }
            Sizes.Start(started.MeasureId, wanted);
            OnPropertyChanged(nameof(Sizes));
        }
        return reply;
    }

    /// <summary>
    /// Stops every measure still counting and keeps the sizes shown (<c>panes.folderSizes</c> turned
    /// off): the core ends each with <c>measure_finished</c>, which drops the folders it did not
    /// finish. Returns how many it stopped.
    /// </summary>
    public int CancelMeasures(string why)
    {
        var running = Sizes.RunningIds;
        CancelMeasures(running, why);
        return running.Count;
    }

    /// <summary>A measure's progress or end; false when the measure is not this pane's.</summary>
    public bool ApplyMeasure(CoreEvent coreEvent)
    {
        var mine = coreEvent switch
        {
            MeasureProgressEvent progress => Sizes.Apply(progress),
            MeasureFinishedEvent finished => Sizes.Apply(finished),
            _ => false,
        };
        if (mine)
        {
            OnPropertyChanged(nameof(Sizes));
            if (coreEvent is MeasureFinishedEvent done)
            {
                Diag.Info(Target, "folder sizes counted", new LogField("pane", Index), new LogField("folders", done.Results.Count),
                    new LogField("bytes", done.Results.Aggregate(0UL, (sum, result) => sum + result.Bytes)), new LogField("cancelled", done.Cancelled));
            }
        }
        return mine;
    }

    // The folder changed: its sizes mean nothing here, and a count still running is stopped.
    private void ForgetSizes()
    {
        CancelMeasures(Sizes.Clear(), "left the folder");
        OnPropertyChanged(nameof(Sizes));
    }

    private void CancelMeasures(IReadOnlyList<ulong> running, string why)
    {
        if (running.Count == 0)
        {
            return;
        }
        Diag.Info(Target, "folder sizes cancelled", new LogField("pane", Index), new LogField("measures", running.Count), new LogField("why", why));
        foreach (var measureId in running)
        {
            _ = CancelMeasureAsync(measureId);
        }
    }

    private async Task CancelMeasureAsync(ulong measureId)
    {
        try
        {
            await _core.RequestAsync(new CancelMeasureRequest(measureId));
        }
        catch (IOException error)
        {
            Diag.Debug(Target, "cannot cancel a measure", new LogField("measure_id", measureId), new LogField("error", error.Message));
        }
    }

    /// <summary>Remembers the marks before a file command or an unmark changes them.</summary>
    public void RememberMarks()
    {
        if (_view is { } view && _search is null)
        {
            SavedMarks.Remember(view, Selection);
        }
    }

    /// <summary>
    /// Parks the pane's own state in <paramref name="tab"/>, when another tab
    /// takes the pane over (docs/ui.md, "Tabs"): its order, its history, the
    /// cursor row and the marks (by name, as Restore Selection keeps them).
    /// </summary>
    public void CaptureInto(PaneTab tab)
    {
        tab.Sort = _sort;
        tab.Back = [.. _back];
        tab.Forward = [.. _forward];
        tab.CursorName = FocusName;
        // The marks a find filter hides are marks too.
        var hiddenMarks = Selection.IsFiltered && Selection.AllSelected.Any(index => !Selection.IsShown(index));
        tab.MarkedNames = Selection.HasMarks || hiddenMarks ? AllSelectedNames() : [];
        tab.FindQuery = _find.Query;
    }

    /// <summary>
    /// Shows <paramref name="tab"/>: its history and order come back, and its
    /// folder is listed with the cursor row and the marks found again by
    /// name. A folder that is gone falls back to its parents, then to
    /// <paramref name="fallbacks"/>; only the first failure is reported.
    /// </summary>
    public async Task<bool> RestoreAsync(PaneTab tab, IEnumerable<string> fallbacks, string? requestId = null, PaneTab? leaving = null)
    {
        // The listing of the tab going behind, as the pane has it now (before the find closes and the order changes).
        var outgoing = leaving is not null && _view is { } current && _listingId != 0
            ? new ParkedListing.Entry(leaving, Path, EffectiveSort, _listingId, current, Selection.Focus, Selection.Anchor, [.. Selection.AllSelected])
            : null;
        // The find of the tab that was in front is its own; this tab's comes back once its folder is listed.
        if (_find.IsOpen)
        {
            _find.Close();
        }
        _back.Clear();
        _forward.Clear();
        foreach (var folder in tab.Back.Reverse())
        {
            _back.Push(folder);
        }
        foreach (var folder in tab.Forward.Reverse())
        {
            _forward.Push(folder);
        }
        _sort = tab.Sort;
        OnPropertyChanged(nameof(EffectiveSort));
        IsLocked = tab.Locked;
        var candidates = new List<string> { tab.Path };
        for (var parent = DisplayFormat.Parent(tab.Path); parent is not null; parent = DisplayFormat.Parent(parent))
        {
            candidates.Add(parent);
        }
        candidates.AddRange(fallbacks);
        // A tab in the column view keeps no listing (ADR 0016), so only a list tab can find its own here.
        if (tab.Mode == TabMode.Files && _kept.TakeBack(tab, EffectiveSort) is { } kept)
        {
            ShowKept(kept, outgoing, requestId);
            if (tab.FindQuery is { } keptQuery)
            {
                _find.Open(keptQuery);
                await RefindAsync();
            }
            return true;
        }
        _keepOnLeave = outgoing;
        try
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                if (!await NavigateAsync(candidates[i], i == 0 ? requestId : null, NavigationKind.Reload, i == 0 ? tab.CursorName : null, notify: i == 0))
                {
                    continue;
                }
                if (i == 0 && tab.MarkedNames.Count > 0 && _view is { } view)
                {
                    Selection.Restore(view.Count, view.IndexesOfNames(tab.MarkedNames), Selection.Focus, Selection.Anchor);
                }
                if (i == 0 && tab.FindQuery is { } query)
                {
                    _find.Open(query);
                    await RefindAsync();
                }
                return true;
            }
        }
        finally
        {
            _keepOnLeave = null;
        }
        RaiseHistoryChanged();
        return false;
    }

    // A tab back in front with the listing it kept: the rows are bound to it again, with the cursor and the marks
    // it had, and the core is not asked. The listing of the tab that went behind is kept in its place.
    private void ShowKept(ParkedListing.Entry kept, ParkedListing.Entry? outgoing, string? requestId)
    {
        // A navigation that answers later is no longer wanted, as after a new listing.
        ++_navigation;
        var started = Stopwatch.GetTimestamp();
        var oldView = _view;
        var oldListing = _listingId;
        var samePlace = string.Equals(Path, kept.Path, StringComparison.OrdinalIgnoreCase);
        if (!Sizes.IsEmpty && !samePlace)
        {
            ForgetSizes();
        }
        _view = kept.View;
        _listingId = kept.ListingId;
        _details.Reset(kept.ListingId, kept.View.Generation);
        PendingTiming = new NavigationTiming(requestId ?? Ulid.NewId(), kept.Path, kept.View.Count, 0, started, started, Kept: true);
        Path = kept.Path;
        Drives.Remember(kept.Path);
        Message = kept.View.Count == 0 ? "This folder is empty." : null;
        _rowsVisible = null;
        Rows = new ListingRows(kept.View, this);
        Selection.Restore(kept.View.Count, kept.Selected, kept.Focus, kept.Anchor);
        RaiseHistoryChanged();
        LetGo(oldView, oldListing, outgoing);
        Navigated?.Invoke(this, NavigationKind.Reload, samePlace);
        Listed?.Invoke(this, requestId);
    }

    // The listing the pane showed before: kept for the tab that went behind while it still is that tab's, else closed.
    private void LetGo(ListingView? view, ulong listingId, ParkedListing.Entry? keep)
    {
        if (keep is not null && view is not null && ReferenceEquals(keep.View, view) && keep.ListingId == listingId)
        {
            _kept.Park(keep);
            _keptTimer?.Cancel();
            _keptTimer?.Dispose();
            _keptTimer = new CancellationTokenSource();
            _ = ExpireKeptAsync(_keptTimer.Token);
            return;
        }
        view?.Dispose();
        if (listingId != 0)
        {
            CloseListing(listingId);
        }
    }

    // Lets the kept listing go when its time is up; a newer one cancels this wait and starts its own.
    private async Task ExpireKeptAsync(CancellationToken cancellationToken)
    {
        while (_kept.DueInMs is { } due)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, due)), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            _kept.ReleaseIfExpired();
        }
    }

    /// <summary>
    /// Lets the listing kept for a tab behind go when that tab is not among
    /// <paramref name="tabs"/> any more (closed, or moved to the other pane).
    /// </summary>
    public void ReleaseKeptUnlessAmong(IEnumerable<PaneTab> tabs) => _kept.ReleaseUnlessAmong(tabs);

    /// <summary>Goes to the parent folder and selects the folder it came from.</summary>
    public Task GoUpAsync(string requestId) =>
        DisplayFormat.Parent(Path) is { } parent
            ? NavigateAsync(parent, requestId, NavigationKind.New, DisplayFormat.FolderName(Path))
            : Task.CompletedTask;

    /// <summary>Goes back in this pane's history.</summary>
    public Task GoBackAsync(string requestId) =>
        _back.TryPop(out var path) ? NavigateAsync(path, requestId, NavigationKind.Back, DisplayFormat.FolderName(Path)) : Task.CompletedTask;

    /// <summary>Goes forward in this pane's history.</summary>
    public Task GoForwardAsync(string requestId) =>
        _forward.TryPop(out var path) ? NavigateAsync(path, requestId, NavigationKind.Forward) : Task.CompletedTask;

    /// <summary>
    /// Shows a refreshed listing of a watched folder (<c>listing_refreshed</c>).
    /// The selection follows its entries by ID, and a name the pane waits for
    /// (a new folder, a rename) is selected when it shows up. Returns false
    /// when the event is not for this pane's listing.
    /// </summary>
    public bool ApplyRefresh(ListingRefreshedEvent refreshed)
    {
        // A kept listing whose folder changed goes; its tab lists the folder again when it comes back.
        if (_kept.Release(refreshed.ListingId, "its folder changed"))
        {
            refreshed.TakeSection()?.Dispose();
            return true;
        }
        if (refreshed.ListingId != _listingId || _view is not { } old || refreshed.Generation <= old.Generation)
        {
            return false;
        }
        if (refreshed.TakeSection() is not { } section)
        {
            return true;
        }
        ListingView view;
        try
        {
            view = ListingView.Open(section, refreshed.SectionSize);
        }
        catch (Exception failure) when (failure is InvalidDataException or IOException)
        {
            Diag.Warn(Target, "a refreshed listing could not be read", new LogField("listing_id", _listingId), new LogField("error", failure.Message));
            return true;
        }

        var focusId = Selection.Focus >= 0 ? old.Id(Selection.Focus) : (ulong?)null;
        var anchorId = Selection.Anchor >= 0 ? old.Id(Selection.Anchor) : (ulong?)null;
        // The rows a find filter hides are selected too; so are the ones it shows.
        var selectedIds = Selection.AllSelected.Select(old.Id).ToList();
        var shownIds = Selection.Visible?.Select(old.Id).ToList();
        var previousFocus = Selection.Focus;

        _view = view;
        _details.Reset(_listingId, view.Generation);
        Message = view.Count == 0 ? "This folder is empty." : null;
        _rowsVisible = null;
        Rows = new ListingRows(view, this);

        var expected = _expectedName is { } name ? view.IndexOfName(name) : -1;
        if (expected >= 0)
        {
            Selection.Reset(view.Count, expected);
            var listed = _expectedListed;
            _expectedName = null;
            _expectedListed = null;
            listed?.TrySetResult(expected);
        }
        else
        {
            var indexOf = IndexById(view, selectedIds.Count > 1 || shownIds is { Count: > 1 });
            var focus = focusId is { } id ? indexOf(id) : -1;
            var anchor = anchorId is { } anchorEntry ? indexOf(anchorEntry) : -1;
            Selection.Restore(view.Count, selectedIds.Select(indexOf).Where(i => i >= 0), focus >= 0 ? focus : previousFocus, anchor);
            if (shownIds is not null)
            {
                // The rows the find showed, found again by ID until the core answers for the new listing.
                Selection.SetVisible(shownIds.Select(indexOf).Where(i => i >= 0));
                _rowsVisible = Selection.Visible;
                Rows = new ListingRows(view, this, _rowsVisible);
            }
        }
        if (_find.IsFiltering || _find.IsOpen)
        {
            _ = RefindAsync();
        }
        Diag.Debug(Target, "listing refreshed", new LogField("listing_id", _listingId),
            new LogField("generation", refreshed.Generation), new LogField("entries", view.Count), new LogField("reason", refreshed.Reason));
        old.Dispose();
        Listed?.Invoke(this, null);
        return true;
    }

    /// <summary>
    /// The watched folder is gone (<c>listing_lost</c>): goes to the nearest
    /// parent the core can list.
    /// </summary>
    public async Task ApplyLostAsync(ListingLostEvent lost)
    {
        if (_kept.Release(lost.ListingId, "its folder is gone") || lost.ListingId != _listingId)
        {
            return;
        }
        Notice?.Invoke($"{Path} is no longer available: {lost.Message}");
        CloseListing(_listingId);
        _listingId = 0;
        for (var parent = DisplayFormat.Parent(Path); parent is not null; parent = DisplayFormat.Parent(parent))
        {
            if (await NavigateAsync(parent, kind: NavigationKind.Reload))
            {
                return;
            }
        }
    }

    /// <summary>
    /// The core was started again: its listing IDs start over, so the old ID
    /// could name another pane's new listing and must not be closed. The new
    /// core may know <c>describe_entries</c> even if the old one did not.
    /// </summary>
    public void ForgetListing()
    {
        _kept.Forget("the core started again");
        _listingId = 0;
        _detailsUnavailable = false;
        // The dead core's counts never finish: without this the rows say "counting" for good, and the
        // folder-sizes setting skips them as counted. No cancel: the core that counted is gone.
        Sizes.Clear();
    }

    /// <summary>Releases the listing, at shutdown.</summary>
    public void Release()
    {
        _kept.Forget("the window closes");
        _keptTimer?.Cancel();
        _view?.Dispose();
        _view = null;
        _listingId = 0;
    }

    // A dictionary pays off only when many entries must be found again; one is found by a scan.
    private static Func<ulong, int> IndexById(ListingView view, bool many)
    {
        if (!many)
        {
            return view.IndexOfId;
        }
        var map = new Dictionary<ulong, int>(view.Count);
        for (var i = 0; i < view.Count; i++)
        {
            map.TryAdd(view.Id(i), i);
        }
        return id => map.TryGetValue(id, out var index) ? index : -1;
    }

    private void Fail(string path, string why, bool notify = true)
    {
        Diag.Info(Target, "cannot list a folder", new LogField("path", path), new LogField("error", why));
        if (_view is null)
        {
            Path = path;
            Message = $"Cannot open this folder: {why}";
            Rows = null;
            Selection.Reset(0, 0);
        }
        else if (notify)
        {
            Notice?.Invoke($"Cannot open {path}: {why}");
        }
    }

    /// <summary>Why the core could not list a folder, in the words the pane shows.</summary>
    internal static string Describe(ErrorReply error) => error.Code switch
    {
        ErrorCodes.NotFound => "it does not exist.",
        ErrorCodes.AccessDenied => "access is denied.",
        ErrorCodes.InvalidPath => "it is not a folder.",
        _ => error.Message,
    };

    private void CloseListing(ulong listingId) => _ = CloseListingAsync(listingId);

    private async Task CloseListingAsync(ulong listingId)
    {
        try
        {
            await _core.RequestAsync(new CloseListingRequest(listingId));
        }
        catch (IOException error)
        {
            Diag.Debug(Target, "cannot close a listing", new LogField("listing_id", listingId), new LogField("error", error.Message));
        }
    }

    private void RaiseHistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }
}

/// <summary>What the core said about an extension, and its icons, for rows without a listing index.</summary>
internal sealed class GuessedDetails(ExtensionDetails known, Services.IconCache icons) : IRowDetails
{
    /// <inheritdoc/>
    public EntryDetail? Detail(int index, ReadOnlySpan<char> name, bool isFolder) => known.Guess(name, isFolder);

    /// <inheritdoc/>
    public Microsoft.UI.Xaml.Media.ImageSource? Icon(string key) => icons.Get(key);
}

/// <summary>How a navigation relates to the history.</summary>
public enum NavigationKind
{
    /// <summary>A new folder: the old one goes on the back stack.</summary>
    New,

    /// <summary>Back in the history.</summary>
    Back,

    /// <summary>Forward in the history.</summary>
    Forward,

    /// <summary>The same place again; the history stays.</summary>
    Reload,
}
