using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CabinetOS.Core.Sidebar;

/// <summary>The sub-folders of one folder, by name, in the order the pane would show them; or why the folder could not be read.</summary>
public sealed record FolderListing(IReadOnlyList<string> Names, string? Error = null)
{
    /// <summary>A folder that could not be read (no access, gone).</summary>
    public static FolderListing Failed(string error) => new([], error);
}

/// <summary>
/// Where the tree gets a folder's sub-folders. The window's source asks the
/// core (<c>list_directory</c>, one request per call); tests use a fake one.
/// A cancelled token means the node was collapsed: the request is abandoned.
/// </summary>
public interface IFolderSource
{
    /// <summary>The sub-folders of <paramref name="path"/>; the hidden ones are left out as <c>panes.showHidden</c> says.</summary>
    Task<FolderListing> ListAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// The sub-folders of <paramref name="path"/> with the hidden ones too, whatever <c>panes.showHidden</c> says. The tree asks
    /// only for a folder that the active pane is in and that <see cref="ListAsync"/> did not list.
    /// </summary>
    Task<FolderListing> ListIncludingHiddenAsync(string path, CancellationToken cancellationToken);
}

/// <summary>One folder of the tree: a row of the Explorer view.</summary>
public sealed class FolderNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isLoading;
    private bool _isCurrent;
    private bool _isCursor;
    private bool _hasChildren = true;
    private bool _isHidden;
    private string? _error;

    internal FolderNode(string path, string name, int depth, FolderNode? parent)
    {
        Path = path;
        Name = name;
        Depth = depth;
        Parent = parent;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The folder's path; a drive's root ends with a backslash, any other folder does not.</summary>
    public string Path { get; }

    /// <summary>What the row says.</summary>
    public string Name { get; }

    /// <summary>The nesting: 0 for a drive.</summary>
    public int Depth { get; }

    /// <summary>The folder above, or null for a drive.</summary>
    public FolderNode? Parent { get; }

    /// <summary>The sub-folders read last, or null before the first read.</summary>
    internal List<FolderNode>? Children { get; set; }

    internal CancellationTokenSource? LoadCancellation { get; set; }

    internal Task? LoadTask { get; set; }

    /// <summary>Whether the sub-folders show under the row.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        internal set
        {
            if (Set(ref _isExpanded, value))
            {
                Raise(nameof(ChevronGlyph));
            }
        }
    }

    /// <summary>Whether a request for the sub-folders is on its way.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        internal set => Set(ref _isLoading, value);
    }

    /// <summary>Whether this is the folder the active pane shows (the row's accent pill).</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set => Set(ref _isCurrent, value);
    }

    /// <summary>Whether the keyboard's cursor is on this row.</summary>
    public bool IsCursor
    {
        get => _isCursor;
        internal set => Set(ref _isCursor, value);
    }

    /// <summary>False once a read found no sub-folders (or the folder could not be read): the row then has no chevron.</summary>
    public bool HasChildren
    {
        get => _hasChildren;
        internal set => Set(ref _hasChildren, value);
    }

    /// <summary>
    /// Whether the folder is hidden and the tree shows it only because the active pane is in it or below it
    /// (<c>panes.showHidden</c> is off): the row is drawn dim.
    /// </summary>
    public bool IsHidden
    {
        get => _isHidden;
        internal set => Set(ref _isHidden, value);
    }

    /// <summary>Why the folder could not be read, or null.</summary>
    public string? Error
    {
        get => _error;
        internal set => Set(ref _error, value);
    }

    /// <summary>The chevron's glyph in Segoe Fluent Icons: right when collapsed, down when open.</summary>
    public string ChevronGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The rows of the tree: an observable list that can take or drop a whole run of rows with one change event,
/// so a folder with thousands of sub-folders opens with one update of the list and not thousands.
/// </summary>
public sealed class FolderRows : ObservableCollection<FolderNode>
{
    /// <summary>Puts <paramref name="nodes"/> at <paramref name="index"/> and reports it once.</summary>
    public void InsertRange(int index, IReadOnlyList<FolderNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return;
        }
        CheckReentrancy();
        ((List<FolderNode>)Items).InsertRange(index, nodes);
        Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, nodes.ToList(), index));
    }

    /// <summary>Drops <paramref name="count"/> rows from <paramref name="index"/> and reports it once.</summary>
    public void RemoveRange(int index, int count)
    {
        if (count <= 0)
        {
            return;
        }
        CheckReentrancy();
        var removed = ((List<FolderNode>)Items).GetRange(index, count);
        ((List<FolderNode>)Items).RemoveRange(index, count);
        Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
    }

    private void Raise(NotifyCollectionChangedEventArgs change)
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(change);
    }
}

/// <summary>
/// The Explorer view's folder tree (docs/ui.md, "The activity rail and the
/// sidebar"). It is lazy: a folder's sub-folders are read when its row is
/// expanded, one request per expanded row, and the request is abandoned when
/// the row collapses before the answer. <see cref="Rows"/> holds the visible
/// rows in order, so a list can show it as it is and follow its changes.
/// The model follows the active pane on <see cref="RevealAsync"/>: it expands
/// the path down to the folder and marks it, and leaves every other row as
/// it was. A folder on that path that the source left out because it is hidden
/// is read anyway and shown as a hidden row (<see cref="FolderNode.IsHidden"/>);
/// no other hidden folder shows, and a hidden row goes when the pane leaves its
/// path. It runs on the UI thread; nothing here waits for the core.
/// </summary>
public sealed class FolderTreeModel(IFolderSource source)
{
    private readonly List<FolderNode> _roots = [];
    private FolderNode? _current;
    private FolderNode? _cursor;

    // The path of the last reveal: the folders on it show even when they are hidden.
    private string? _followed;

    // Whether a row may be hidden: false lets a reveal skip the look for hidden rows to drop (every folder change goes through it).
    private bool _mayHaveHidden;

    /// <summary>The rows that show, top to bottom; changes are reported one row at a time.</summary>
    public FolderRows Rows { get; } = [];

    /// <summary>How many requests were sent so far.</summary>
    public int Requests { get; private set; }

    /// <summary>The row for the folder the active pane shows, or null.</summary>
    public FolderNode? Current => _current;

    /// <summary>The row the keyboard is on, or null.</summary>
    public FolderNode? Cursor => _cursor;

    /// <summary>The drives, in order.</summary>
    public IReadOnlyList<FolderNode> Roots => _roots;

    /// <summary>
    /// Sets the drives the tree starts from. A drive that was there keeps its
    /// state; one that went is dropped with everything under it.
    /// </summary>
    public void SetRoots(IReadOnlyList<(string Path, string Name)> drives)
    {
        var old = _roots.ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);
        var roots = new List<FolderNode>();
        foreach (var (path, name) in drives)
        {
            var normal = Normalize(path);
            if (roots.Any(r => SamePath(r.Path, normal)))
            {
                continue;
            }
            roots.Add(old.GetValueOrDefault(normal) ?? new FolderNode(normal, name, 0, null));
        }
        foreach (var gone in old.Values.Where(o => !roots.Contains(o)))
        {
            CancelLoads(gone);
            if (_current is { } current && IsInside(current, gone))
            {
                SetCurrent(null);
            }
            if (_cursor is { } cursor && IsInside(cursor, gone))
            {
                SetCursor(null);
            }
        }
        _roots.Clear();
        _roots.AddRange(roots);
        Rows.Clear();
        var shown = new List<FolderNode>();
        foreach (var root in _roots)
        {
            shown.Add(root);
            shown.AddRange(VisibleBelow(root));
        }
        Rows.InsertRange(0, shown);
    }

    /// <summary>The row of a folder that is in the tree now (its parents expanded or not), or null.</summary>
    public FolderNode? Find(string path)
    {
        var normal = Normalize(path);
        foreach (var root in _roots)
        {
            var found = Find(root, normal);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Expands <paramref name="node"/>: its known sub-folders show at once and
    /// the folder is read again (one request), so the rows follow the disk.
    /// The task ends when the answer is in, or the row was collapsed.
    /// </summary>
    public Task ExpandAsync(FolderNode node)
    {
        if (node.IsExpanded)
        {
            return node.LoadTask ?? Task.CompletedTask;
        }
        node.IsExpanded = true;
        if (node.Children is not null && Rows.IndexOf(node) >= 0)
        {
            InsertBelow(node);
        }
        node.LoadTask = LoadAsync(node);
        return node.LoadTask;
    }

    /// <summary>Collapses <paramref name="node"/>: its rows go, and requests still on their way for it or for rows under it are abandoned.</summary>
    public void Collapse(FolderNode node)
    {
        if (!node.IsExpanded)
        {
            return;
        }
        node.IsExpanded = false;
        CancelLoads(node);
        var index = Rows.IndexOf(node);
        if (index >= 0)
        {
            RemoveBelow(node, index);
        }
        if (_cursor is { } cursor && cursor != node && IsInside(cursor, node))
        {
            SetCursor(node);
        }
    }

    /// <summary>Expands the row, or collapses it.</summary>
    public Task ToggleAsync(FolderNode node)
    {
        if (node.IsExpanded)
        {
            Collapse(node);
            return Task.CompletedTask;
        }
        return ExpandAsync(node);
    }

    /// <summary>
    /// Shows <paramref name="path"/> in the tree: expands the folders on the way
    /// (reading each one that was not read yet), marks the row as the current
    /// one and returns it. Rows beside the path are not touched, except the
    /// hidden folders that the tree showed for an earlier path, which go. A
    /// folder that is hidden shows while it is on the path. A folder that
    /// is not on the disk yet (created after the last read) is looked for by
    /// reading its parent once more; when it is still not there the deepest
    /// row on the way is marked. Null when no drive holds the path.
    /// </summary>
    public async Task<FolderNode?> RevealAsync(string path, CancellationToken cancellationToken = default)
    {
        var normal = Normalize(path);
        _followed = normal;
        DropHiddenOffPath();
        var root = _roots.FirstOrDefault(r => IsUnder(r.Path, normal));
        if (root is null)
        {
            SetCurrent(null);
            return null;
        }
        var node = root;
        var rest = normal.Length > root.Path.Length ? normal[root.Path.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries) : [];
        foreach (var segment in rest)
        {
            if (!node.IsExpanded)
            {
                await ExpandAsync(node);
            }
            else if (node.LoadTask is { IsCompleted: false } loading)
            {
                await loading;
            }
            if (!CanGoOn(node, cancellationToken, out var deepest))
            {
                return deepest ? Mark(node) : null;
            }
            var child = FindChild(node, segment);
            if (child is null)
            {
                node.LoadTask = LoadAsync(node);
                await node.LoadTask;
                if (!CanGoOn(node, cancellationToken, out deepest))
                {
                    return deepest ? Mark(node) : null;
                }
                child = FindChild(node, segment);
            }
            if (child is null)
            {
                break;
            }
            node = child;
        }
        return Mark(node);
    }

    private FolderNode Mark(FolderNode node)
    {
        SetCurrent(node);
        return node;
    }

    // Whether a reveal can go down from a node. It cannot when a newer reveal took over or the row was
    // collapsed meanwhile (nothing is marked), or when the folder could not be read (then the node is the
    // deepest row on the way and is marked: deepest is true).
    private static bool CanGoOn(FolderNode node, CancellationToken cancellationToken, out bool deepest)
    {
        deepest = false;
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        if (node.IsExpanded)
        {
            return true;
        }
        deepest = node.Error is not null;
        return false;
    }

    /// <summary>Marks <paramref name="node"/> as the current folder (or none).</summary>
    public void SetCurrent(FolderNode? node)
    {
        if (_current == node)
        {
            return;
        }
        if (_current is not null)
        {
            _current.IsCurrent = false;
        }
        _current = node;
        if (node is not null)
        {
            node.IsCurrent = true;
        }
    }

    /// <summary>Puts the keyboard's cursor on <paramref name="node"/> (or nowhere).</summary>
    public void SetCursor(FolderNode? node)
    {
        if (_cursor == node)
        {
            return;
        }
        if (_cursor is not null)
        {
            _cursor.IsCursor = false;
        }
        _cursor = node;
        if (node is not null)
        {
            node.IsCursor = true;
        }
    }

    /// <summary>Moves the cursor <paramref name="delta"/> rows down (up when negative); it stops at the ends.</summary>
    public void MoveCursor(int delta)
    {
        if (Rows.Count == 0)
        {
            return;
        }
        var index = _cursor is null ? -1 : Rows.IndexOf(_cursor);
        var target = index < 0 ? (delta > 0 ? 0 : Rows.Count - 1) : Math.Clamp(index + delta, 0, Rows.Count - 1);
        SetCursor(Rows[target]);
    }

    /// <summary>Puts the cursor on the first row.</summary>
    public void CursorToStart() => SetCursor(Rows.Count > 0 ? Rows[0] : null);

    /// <summary>Puts the cursor on the last row.</summary>
    public void CursorToEnd() => SetCursor(Rows.Count > 0 ? Rows[^1] : null);

    /// <summary>
    /// Right arrow: expands the cursor row; when it is open already, the cursor
    /// goes to its first sub-folder.
    /// </summary>
    public async Task CursorRightAsync()
    {
        if (_cursor is not { } node)
        {
            return;
        }
        if (!node.IsExpanded)
        {
            if (node.HasChildren)
            {
                await ExpandAsync(node);
            }
            return;
        }
        if (node.Children is { Count: > 0 } children)
        {
            SetCursor(children[0]);
        }
    }

    /// <summary>Left arrow: collapses the cursor row; when it is closed already, the cursor goes to the folder above.</summary>
    public void CursorLeft()
    {
        if (_cursor is not { } node)
        {
            return;
        }
        if (node.IsExpanded)
        {
            Collapse(node);
        }
        else if (node.Parent is { } parent)
        {
            SetCursor(parent);
        }
    }

    // ----- Reading a folder -----

    private async Task LoadAsync(FolderNode node)
    {
        node.LoadCancellation?.Cancel();
        var cancellation = node.LoadCancellation = new CancellationTokenSource();
        node.IsLoading = true;
        Requests++;
        FolderListing? listing;
        IReadOnlyList<string>? names = null;
        string? hiddenName = null;
        try
        {
            listing = await source.ListAsync(node.Path, cancellation.Token);
            if (listing.Error is null)
            {
                names = listing.Names;
                if (await ReadHiddenOnPathAsync(node, listing.Names, cancellation.Token) is { } hidden)
                {
                    var withHidden = new List<string>(listing.Names);
                    withHidden.Insert(Math.Min(hidden.Position, withHidden.Count), hidden.Name);
                    names = withHidden;
                    hiddenName = hidden.Name;
                }
            }
        }
        catch (OperationCanceledException)
        {
            listing = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            listing = FolderListing.Failed(error.Message);
        }
        if (cancellation.IsCancellationRequested || !ReferenceEquals(node.LoadCancellation, cancellation))
        {
            // Collapsed, or read again meanwhile: whoever did that owns the state now.
            return;
        }
        node.LoadCancellation = null;
        node.IsLoading = false;
        if (listing is null)
        {
            return;
        }
        if (listing.Error is { } error2)
        {
            node.Error = error2;
            node.HasChildren = false;
            if (node.IsExpanded)
            {
                Collapse(node);
            }
            return;
        }
        node.Error = null;
        Merge(node, names ?? listing.Names, hiddenName);
    }

    // A hidden folder of a read and the place the listing has it: after this many of the names that were read.
    private readonly record struct HiddenFolder(string Name, int Position);

    // The active pane's path may go through a folder of this node that the read left out because it is hidden. The folder is then
    // read again with the hidden ones too. Returns the folder and where the listing has it among the names that were read, or null
    // when the path does not go through a missing folder, the folder is not there, or the pane has gone elsewhere.
    private async Task<HiddenFolder?> ReadHiddenOnPathAsync(FolderNode node, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        if (NextOnPath(node) is not { } wanted || names.Any(n => SameName(n, wanted)))
        {
            return null;
        }
        Requests++;
        FolderListing all;
        try
        {
            all = await source.ListIncludingHiddenAsync(node.Path, cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (all.Error is not null || cancellationToken.IsCancellationRequested
            || NextOnPath(node) is not { } still || !SameName(still, wanted))
        {
            return null;
        }
        var found = all.Names.FirstOrDefault(n => SameName(n, wanted));
        if (found is null)
        {
            return null;
        }
        var read = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        return new HiddenFolder(found, all.Names.TakeWhile(n => !SameName(n, found)).Count(read.Contains));
    }

    // The read names become the node's sub-folders: a row that was there keeps its state (open, read),
    // a new folder gets a row, a folder that is gone loses its row. The visible rows under the node follow.
    // The folder named hiddenName is hidden and shows only because the pane's path goes through it.
    private void Merge(FolderNode node, IReadOnlyList<string> names, string? hiddenName)
    {
        var existing = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in node.Children ?? [])
        {
            existing[child.Name] = child;
        }
        var children = new List<FolderNode>(names.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (!seen.Add(name))
            {
                continue;
            }
            var child = existing.GetValueOrDefault(name) ?? new FolderNode(Combine(node.Path, name), name, node.Depth + 1, node);
            child.IsHidden = hiddenName is not null && SameName(name, hiddenName);
            _mayHaveHidden |= child.IsHidden;
            children.Add(child);
        }
        ReplaceChildren(node, children);
    }

    // Gives a node its sub-folders: a folder that is gone takes the marks with it (the cursor goes to the node), and the visible
    // rows under the node follow.
    private void ReplaceChildren(FolderNode node, List<FolderNode> children)
    {
        foreach (var gone in (node.Children ?? []).Where(e => !children.Contains(e)))
        {
            CancelLoads(gone);
            if (_current is { } current && IsInside(current, gone))
            {
                SetCurrent(null);
            }
            if (_cursor is { } cursor && IsInside(cursor, gone))
            {
                SetCursor(node);
            }
        }
        var index = Rows.IndexOf(node);
        var shown = index >= 0 && node.IsExpanded;
        if (shown)
        {
            RemoveBelow(node, index);
        }
        node.Children = children;
        node.HasChildren = children.Count > 0;
        if (shown)
        {
            InsertBelow(node);
        }
    }

    // A hidden folder shows only while the active pane is in it or below it. The next reveal drops the hidden rows that are not on
    // its path, and everything under them.
    private void DropHiddenOffPath()
    {
        if (!_mayHaveHidden)
        {
            return;
        }
        // Set again below for each hidden row that stays.
        _mayHaveHidden = false;
        foreach (var root in _roots)
        {
            DropHiddenBelow(root);
        }
    }

    private void DropHiddenBelow(FolderNode node)
    {
        if (node.Children is not { } children)
        {
            return;
        }
        List<FolderNode>? kept = null;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.IsHidden && !(_followed is { } path && IsUnder(child.Path, path)))
            {
                // The first row to go: the rows before it stay.
                kept ??= children.Take(i).ToList();
                continue;
            }
            _mayHaveHidden |= child.IsHidden;
            kept?.Add(child);
        }
        if (kept is not null)
        {
            ReplaceChildren(node, kept);
            children = kept;
        }
        foreach (var child in children)
        {
            DropHiddenBelow(child);
        }
    }

    // ----- Rows -----

    private void InsertBelow(FolderNode node) => Rows.InsertRange(Rows.IndexOf(node) + 1, VisibleBelow(node).ToList());

    private void RemoveBelow(FolderNode node, int index)
    {
        var end = index + 1;
        while (end < Rows.Count && Rows[end].Depth > node.Depth)
        {
            end++;
        }
        Rows.RemoveRange(index + 1, end - index - 1);
    }

    // The rows under a node that shows, in order: its sub-folders, and under each open one its own.
    private static IEnumerable<FolderNode> VisibleBelow(FolderNode node)
    {
        if (!node.IsExpanded || node.Children is null)
        {
            yield break;
        }
        foreach (var child in node.Children)
        {
            yield return child;
            foreach (var deeper in VisibleBelow(child))
            {
                yield return deeper;
            }
        }
    }

    // Abandons the requests still on their way for a node and everything under it.
    private static void CancelLoads(FolderNode node)
    {
        if (node.LoadCancellation is { } cancellation)
        {
            cancellation.Cancel();
            node.LoadCancellation = null;
            node.IsLoading = false;
        }
        foreach (var child in node.Children ?? [])
        {
            CancelLoads(child);
        }
    }

    // ----- Paths -----

    /// <summary>A path in the tree's form: backslashes, no closing one except on a drive's root ("C:\").</summary>
    public static string Normalize(string path)
    {
        var text = path.Replace('/', '\\');
        if (text.Length == 2 && text[1] == ':')
        {
            return text + "\\";
        }
        return text.Length > 3 ? text.TrimEnd('\\') : text;
    }

    private static string Combine(string parent, string name) => parent.EndsWith('\\') ? parent + name : parent + "\\" + name;

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // The name of the folder under a node that the followed path goes through; null when the path does not go through the node or
    // ends at it.
    private string? NextOnPath(FolderNode node)
    {
        if (_followed is not { } path || SamePath(node.Path, path) || !IsUnder(node.Path, path))
        {
            return null;
        }
        var below = path[(node.Path.EndsWith('\\') ? node.Path.Length : node.Path.Length + 1)..];
        var end = below.IndexOf('\\');
        var name = end < 0 ? below : below[..end];
        return name.Length > 0 ? name : null;
    }

    private static bool IsUnder(string root, string path) =>
        SamePath(root, path) || path.StartsWith(root.EndsWith('\\') ? root : root + "\\", StringComparison.OrdinalIgnoreCase);

    private static FolderNode? FindChild(FolderNode node, string name) =>
        node.Children?.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private static FolderNode? Find(FolderNode node, string path)
    {
        if (SamePath(node.Path, path))
        {
            return node;
        }
        if (!IsUnder(node.Path, path))
        {
            return null;
        }
        foreach (var child in node.Children ?? [])
        {
            if (Find(child, path) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static bool IsInside(FolderNode node, FolderNode ancestor)
    {
        for (var up = node; up is not null; up = up.Parent)
        {
            if (up == ancestor)
            {
                return true;
            }
        }
        return false;
    }
}
