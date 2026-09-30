namespace CabinetOS.Core.Tabs;

/// <summary>
/// One pane's tabs, left to right, and the one in front (docs/ui.md,
/// "Tabs"). It is the tab state the window owns and reports (Phase 12); it
/// knows no listing and no window, so its rules are tested without either.
/// A pane always keeps one folder tab: the last one cannot be closed, even
/// while a tool tab is open beside it.
/// </summary>
public sealed class TabStrip
{
    private readonly List<PaneTab> _tabs;
    private int _active;

    /// <summary>A pane with one tab, in front.</summary>
    public TabStrip(PaneTab first) => _tabs = [first];

    /// <summary>A pane with these tabs (at least one folder tab among them) and the tab at <paramref name="active"/> in front.</summary>
    public TabStrip(IEnumerable<PaneTab> tabs, int active)
    {
        _tabs = [.. tabs];
        if (_tabs.Count == 0 || _tabs.All(t => t.IsTool))
        {
            throw new ArgumentException("a pane needs a folder tab", nameof(tabs));
        }
        _active = Math.Clamp(active, 0, _tabs.Count - 1);
    }

    /// <summary>Raised after every change of the tabs, the tab in front, or a tab's lock or folder.</summary>
    public event Action? Changed;

    /// <summary>The tabs, left to right.</summary>
    public IReadOnlyList<PaneTab> Tabs => _tabs;

    /// <summary>How many tabs.</summary>
    public int Count => _tabs.Count;

    /// <summary>Where the tab in front is.</summary>
    public int ActiveIndex => _active;

    /// <summary>The tab in front.</summary>
    public PaneTab Active => _tabs[_active];

    /// <summary>
    /// Whether the tab strip shows: always, since the Phase 16 shell (the
    /// creator's SHELL_REDESIGN.md §2 puts it at the top of every pane, and a
    /// pane always has a tab). Before, it showed from the second tab on.
    /// </summary>
    public bool ShowsRow => _tabs.Count > 0;

    /// <summary>How many of the tabs show a folder.</summary>
    public int FolderCount => _tabs.Count(t => !t.IsTool);

    /// <summary>Where <paramref name="tab"/> is, or -1.</summary>
    public int IndexOf(PaneTab tab) => _tabs.IndexOf(tab);

    /// <summary>The first tab that shows a file in the tool <paramref name="toolId"/>, or -1.</summary>
    public int IndexOfTool(string toolId) => _tabs.FindIndex(t => t.Tool == toolId);

    /// <summary>
    /// Adds <paramref name="tab"/> right of the tab in front (or at
    /// <paramref name="at"/>) and, unless told otherwise, brings it to the
    /// front. Returns where it is.
    /// </summary>
    public int Add(PaneTab tab, bool activate = true, int? at = null)
    {
        var index = at is { } position ? Math.Clamp(position, 0, _tabs.Count) : _active + 1;
        _tabs.Insert(index, tab);
        if (activate)
        {
            _active = index;
        }
        else if (index <= _active)
        {
            _active++;
        }
        Changed?.Invoke();
        return index;
    }

    /// <summary>Whether the tab at <paramref name="index"/> may be closed: the pane's last folder tab may not.</summary>
    public bool CanClose(int index) =>
        (uint)index < (uint)_tabs.Count && (_tabs[index].IsTool || FolderCount > 1);

    /// <summary>
    /// Takes the tab at <paramref name="index"/> out and returns it; null when
    /// it may not be closed (<see cref="CanClose"/>). When it was in front,
    /// the tab that was right of it comes to the front, else the one left of it.
    /// </summary>
    public PaneTab? Remove(int index)
    {
        if (!CanClose(index))
        {
            return null;
        }
        var tab = _tabs[index];
        _tabs.RemoveAt(index);
        if (index < _active)
        {
            _active--;
        }
        else if (index == _active)
        {
            _active = Math.Min(index, _tabs.Count - 1);
        }
        Changed?.Invoke();
        return tab;
    }

    /// <summary>Brings the tab at <paramref name="index"/> to the front. False when it is not a tab or is in front already.</summary>
    public bool Select(int index)
    {
        if ((uint)index >= (uint)_tabs.Count || index == _active)
        {
            return false;
        }
        _active = index;
        Changed?.Invoke();
        return true;
    }

    /// <summary>The index of the tab right of the one in front; the first after the last.</summary>
    public int NextIndex() => (_active + 1) % _tabs.Count;

    /// <summary>The index of the tab left of the one in front; the last before the first.</summary>
    public int PreviousIndex() => (_active + _tabs.Count - 1) % _tabs.Count;

    /// <summary>Locks or unlocks the tab at <paramref name="index"/>; a tool tab has no lock. Returns whether it is locked now.</summary>
    public bool ToggleLock(int index)
    {
        if ((uint)index >= (uint)_tabs.Count || _tabs[index].IsTool)
        {
            return false;
        }
        _tabs[index].Locked = !_tabs[index].Locked;
        Changed?.Invoke();
        return _tabs[index].Locked;
    }

    /// <summary>
    /// Moves the tab at <paramref name="index"/> of <paramref name="from"/> to
    /// the other pane's strip, right of its front tab, and brings it to the
    /// front there. Returns the tab; null when it may not go: the pane's last
    /// folder tab stays, and a tab with a tool stays with its pane's tool host.
    /// </summary>
    public static PaneTab? Move(TabStrip from, int index, TabStrip to)
    {
        if ((uint)index >= (uint)from._tabs.Count || from._tabs[index].IsTool || !from.CanClose(index))
        {
            return null;
        }
        var tab = from.Remove(index)!;
        to.Add(tab);
        return tab;
    }

    /// <summary>Tells the listeners that something a tab holds changed (its folder), without the tabs changing.</summary>
    public void Touch() => Changed?.Invoke();

    /// <summary>
    /// Takes every tool tab out, as when the pane goes away (single-pane mode
    /// closes the right pane's tools). Returns whether the tab in front was
    /// one of them: the folder tab nearest to it is then in front.
    /// </summary>
    public bool RemoveToolTabs()
    {
        var frontWasTool = Active.IsTool;
        var front = Active;
        var before = _tabs.Take(_active).Count(t => !t.IsTool);
        if (_tabs.RemoveAll(t => t.IsTool) == 0)
        {
            return false;
        }
        _active = frontWasTool ? Math.Clamp(before, 0, _tabs.Count - 1) : _tabs.IndexOf(front);
        Changed?.Invoke();
        return frontWasTool;
    }

    /// <summary>
    /// The folder tabs as <c>ui.tabs</c> holds them: tool tabs are not saved
    /// (a tool needs its process and its file); when one is in front, the
    /// folder tab before it is.
    /// </summary>
    public PaneTabsConfig ToConfig()
    {
        var items = new List<TabItemConfig>();
        var active = 0;
        for (var i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].IsTool)
            {
                continue;
            }
            if (i <= _active)
            {
                active = items.Count;
            }
            items.Add(new TabItemConfig(_tabs[i].Path, _tabs[i].Locked));
        }
        return new PaneTabsConfig(items, active);
    }
}
