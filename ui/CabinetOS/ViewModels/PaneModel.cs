using System.Diagnostics;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CabinetOS.ViewModels;

/// <summary>How long one navigation took, for the "listing shown" log line.</summary>
public sealed record NavigationTiming(string RequestId, string Path, int Entries, ulong CoreUs, long StartedTicks, long RepliedTicks);

/// <summary>One entry of a pane, as a command needs it.</summary>
public sealed record PaneEntry(int Index, string Name, string Path, bool IsFolder, ulong Size);

/// <summary>
/// One file pane: its folder, its listing in shared memory, the selection and
/// the history. The core lists, sorts and watches; the pane only asks and shows.
/// </summary>
public sealed class PaneModel : ObservableObject
{
    private const string Target = "cabinetos_ui::pane";

    private readonly ICoreChannel _core;
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

    /// <summary>Creates pane <paramref name="index"/>, asking <paramref name="core"/> for its listings.</summary>
    public PaneModel(int index, ICoreChannel core)
    {
        Index = index;
        _core = core;
        Selection.Changed += () =>
        {
            OnPropertyChanged(nameof(Selection));
            OnPropertyChanged(nameof(FocusName));
        };
    }

    /// <summary>Raised when something the user should read goes to the status bar.</summary>
    public event Action<string>? Notice;

    /// <summary>0 for the left pane, 1 for the right.</summary>
    public int Index { get; }

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

    /// <summary>Whether there is a folder to go back to.</summary>
    public bool CanGoBack => _back.Count > 0;

    /// <summary>Whether there is a folder to go forward to.</summary>
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Whether the folder has a parent.</summary>
    public bool CanGoUp => DisplayFormat.Parent(Path) is not null;

    /// <summary>The last navigation's timing, until the view logs its first paint.</summary>
    public NavigationTiming? PendingTiming { get; set; }

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
    /// How many rows are selected, and the bytes of the selected files: the
    /// status bar's "12 selected, 1.4 MB". Folders count as none; the listing
    /// has no size for them. Nothing is read from the disk.
    /// </summary>
    public (int Count, ulong FileBytes, bool AnyFile) SelectionSize()
    {
        if (_view is not { } view)
        {
            return (0, 0, false);
        }
        ulong bytes = 0;
        var anyFile = false;
        foreach (var index in Selection.SelectedUnordered)
        {
            if (!view.IsFolder(index))
            {
                bytes += view.Size(index);
                anyFile = true;
            }
        }
        return (Selection.SelectedCount, bytes, anyFile);
    }

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
    public async Task<bool> NavigateAsync(string path, string? requestId = null, NavigationKind kind = NavigationKind.New, string? selectName = null)
    {
        var navigation = ++_navigation;
        var started = Stopwatch.GetTimestamp();
        var request = new ListDirectoryRequest(path) { Watch = true, Id = requestId ?? "" };
        CoreReply reply;
        try
        {
            reply = await _core.RequestAsync(request);
        }
        catch (IOException error)
        {
            Fail(path, error.Message);
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
            Fail(path, Describe(refused));
            return false;
        }
        if (reply is not ListingOpenedReply opened || opened.TakeSection() is not { } section)
        {
            Fail(path, $"unexpected reply {reply.GetType().Name}");
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
            Fail(path, failure.Message);
            return false;
        }

        var previousPath = Path;
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
        _view = view;
        _listingId = opened.ListingId;
        PendingTiming = new NavigationTiming(request.Id, path, view.Count, opened.ElapsedUs, started, Stopwatch.GetTimestamp());
        Path = path;
        Message = view.Count == 0 ? "This folder is empty." : null;
        Rows = new ListingRows(view);
        var select = selectName is null ? -1 : view.IndexOfName(selectName);
        Selection.Reset(view.Count, select >= 0 ? select : 0);
        RaiseHistoryChanged();
        oldView?.Dispose();
        if (oldListing != 0)
        {
            CloseListing(oldListing);
        }
        return true;
    }

    /// <summary>Lists the same folder again, keeping the focused entry.</summary>
    public Task<bool> ReloadAsync(string? requestId = null) =>
        NavigateAsync(Path, requestId, NavigationKind.Reload, FocusName);

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
        var selectedIds = Selection.SelectedUnordered.Select(old.Id).ToList();
        var previousFocus = Selection.Focus;

        _view = view;
        Message = view.Count == 0 ? "This folder is empty." : null;
        Rows = new ListingRows(view);

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
            var indexOf = IndexById(view, selectedIds.Count > 1);
            var focus = focusId is { } id ? indexOf(id) : -1;
            var anchor = anchorId is { } anchorEntry ? indexOf(anchorEntry) : -1;
            Selection.Restore(view.Count, selectedIds.Select(indexOf).Where(i => i >= 0), focus >= 0 ? focus : previousFocus, anchor);
        }
        Diag.Debug(Target, "listing refreshed", new LogField("listing_id", _listingId),
            new LogField("generation", refreshed.Generation), new LogField("entries", view.Count), new LogField("reason", refreshed.Reason));
        old.Dispose();
        return true;
    }

    /// <summary>
    /// The watched folder is gone (<c>listing_lost</c>): goes to the nearest
    /// parent the core can list.
    /// </summary>
    public async Task ApplyLostAsync(ListingLostEvent lost)
    {
        if (lost.ListingId != _listingId)
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
    /// could name another pane's new listing and must not be closed.
    /// </summary>
    public void ForgetListing() => _listingId = 0;

    /// <summary>Releases the listing, at shutdown.</summary>
    public void Release()
    {
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

    private void Fail(string path, string why)
    {
        Diag.Info(Target, "cannot list a folder", new LogField("path", path), new LogField("error", why));
        if (_view is null)
        {
            Path = path;
            Message = $"Cannot open this folder: {why}";
            Rows = null;
            Selection.Reset(0, 0);
        }
        else
        {
            Notice?.Invoke($"Cannot open {path}: {why}");
        }
    }

    private static string Describe(ErrorReply error) => error.Code switch
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
