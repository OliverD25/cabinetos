namespace CabinetOS.Core.QuickView;

/// <summary>
/// Which viewers keep their WebView2 alive between files (ADR 0023, decision 7): at most <see cref="MaxAlive"/>; the
/// one used longest ago is closed when another is needed. The window holds the hosts; this keeps their order.
/// </summary>
public sealed class QuickViewPool
{
    /// <summary>The most viewers whose WebView2 stays alive.</summary>
    public const int MaxAlive = 3;

    private readonly List<string> _used = [];

    /// <summary>The alive viewers, the one used last first.</summary>
    public IReadOnlyList<string> Alive => _used;

    /// <summary>Marks <paramref name="viewerId"/> as used now; returns the viewer to close to make room, or null.</summary>
    public string? Use(string viewerId)
    {
        _used.Remove(viewerId);
        _used.Insert(0, viewerId);
        if (_used.Count <= MaxAlive)
        {
            return null;
        }
        var oldest = _used[^1];
        _used.RemoveAt(_used.Count - 1);
        return oldest;
    }

    /// <summary>The viewer's WebView2 was closed (its process ended, or the window closes).</summary>
    public void Remove(string viewerId) => _used.Remove(viewerId);
}
