namespace CabinetOS.Core.Sidebar;

/// <summary>What a web page of the sidebar is told when the shown view changes.</summary>
public enum PageChange
{
    /// <summary>The page was suspended: it wakes (<c>CoreWebView2.Resume</c>).</summary>
    Resume,

    /// <summary>The page is hidden and goes to sleep (<c>CoreWebView2.TrySuspendAsync</c>).</summary>
    Suspend,
}

/// <summary>One page and what happens to it.</summary>
public readonly record struct PageStep(string Page, PageChange Change);

/// <summary>
/// Which hidden web page of the sidebar stays awake. The page hidden last is
/// kept warm (it comes back at once); every page hidden before it is
/// suspended, which frees its memory and its processor time. Native views
/// (Explorer, Search) are never suspended: they are kept alive as they are
/// (docs/ui.md, "The activity rail and the sidebar").
/// </summary>
public sealed class WarmPages
{
    private readonly HashSet<string> _suspended = new(StringComparer.Ordinal);
    private string? _shown;
    private string? _warm;

    /// <summary>The page on show, or null when a native view is or the sidebar is closed.</summary>
    public string? Shown => _shown;

    /// <summary>The page kept warm, or null.</summary>
    public string? Warm => _warm;

    /// <summary>Whether <paramref name="page"/> is suspended now.</summary>
    public bool IsSuspended(string page) => _suspended.Contains(page);

    /// <summary>
    /// Page <paramref name="page"/> comes on show, or <c>null</c> when no page
    /// does. Returns the changes to make, the wake-up first.
    /// </summary>
    public IReadOnlyList<PageStep> Show(string? page)
    {
        var steps = new List<PageStep>();
        if (page == _shown)
        {
            return steps;
        }
        var hidden = _shown;
        _shown = page;
        if (page is not null && _suspended.Remove(page))
        {
            steps.Add(new PageStep(page, PageChange.Resume));
        }
        if (page is not null && page == _warm)
        {
            _warm = null;
        }
        if (hidden is not null)
        {
            if (_warm is { } older && older != hidden)
            {
                _suspended.Add(older);
                steps.Add(new PageStep(older, PageChange.Suspend));
            }
            _warm = hidden;
        }
        return steps;
    }

    /// <summary>A page was closed or stopped: it needs no waking and is not the warm one.</summary>
    public void Forget(string page)
    {
        _suspended.Remove(page);
        if (_warm == page)
        {
            _warm = null;
        }
        if (_shown == page)
        {
            _shown = null;
        }
    }
}
