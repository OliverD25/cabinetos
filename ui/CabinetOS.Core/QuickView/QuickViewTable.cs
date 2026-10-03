using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.QuickView;

/// <summary>
/// What the table says about one file's name: the viewer to use (null for none), the pattern that matched, the
/// viewers the panel's chooser lists, whether the user set the kind to "no viewer", and the viewer that would show it
/// but is off for the window's session (three stops in a minute) when no other viewer of the kind is left.
/// </summary>
public sealed record QuickViewMatch(QuickViewer? Viewer, string? Claim, IReadOnlyList<QuickViewer> Choices, bool Off, QuickViewer? OffViewer = null)
{
    /// <summary>No kind of the table matches the name.</summary>
    public static readonly QuickViewMatch Nothing = new(null, null, [], false);

    /// <summary>
    /// Whether the panel shows its viewer button: two or more viewers to choose from, or a kind the user turned off
    /// (the button is then the way back to a viewer).
    /// </summary>
    public bool ShowsChooser => Choices.Count >= 2 || (Off && Choices.Count >= 1);
}

/// <summary>
/// The Quick View table as the core built it (<c>quick_view_table</c>, ADR 0023, decision 1.3), and the lookup the
/// window makes in it for each file. The window reads no file for it: the core reads the tools' manifests, and the
/// window only matches a name against at most a few hundred patterns (Prime Directive 1).
/// </summary>
public sealed class QuickViewTable
{
    /// <summary>No viewers, until the core answers.</summary>
    public static readonly QuickViewTable Empty = new([], []);

    private readonly Dictionary<string, QuickViewer> _byId;

    /// <summary>A table of <paramref name="viewers"/> and <paramref name="kinds"/> in the order to try them.</summary>
    public QuickViewTable(IReadOnlyList<QuickViewer> viewers, IReadOnlyList<QuickViewKind> kinds)
    {
        Viewers = viewers;
        Kinds = kinds;
        _byId = viewers.GroupBy(v => v.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    /// <summary>Every viewer, the one installed first first.</summary>
    public IReadOnlyList<QuickViewer> Viewers { get; }

    /// <summary>The kinds, in the order to try them: the first whose pattern matches decides.</summary>
    public IReadOnlyList<QuickViewKind> Kinds { get; }

    /// <summary>The viewer with this ID, or null.</summary>
    public QuickViewer? Viewer(string id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// The viewer for a file named <paramref name="name"/>: the first viewer of the first kind that matches, skipping
    /// viewers that <paramref name="isOff"/> says are off for this window's session (three stops in a minute) and
    /// viewers the table does not list. The chooser lists the viewers of every kind that matches, in the table's
    /// order; for a kind the user turned off the core does not say who claims it, so the chooser then lists every
    /// viewer.
    /// </summary>
    public QuickViewMatch Match(string name, Func<string, bool>? isOff = null)
    {
        QuickViewKind? first = null;
        var choices = new List<QuickViewer>();
        foreach (var kind in Kinds)
        {
            if (!Matches(kind.Pattern, name))
            {
                continue;
            }
            first ??= kind;
            foreach (var id in kind.Viewers)
            {
                if (_byId.TryGetValue(id, out var viewer) && !choices.Contains(viewer))
                {
                    choices.Add(viewer);
                }
            }
        }
        if (first is null)
        {
            return QuickViewMatch.Nothing;
        }
        if (first.Off)
        {
            return new QuickViewMatch(null, first.Pattern, choices.Count > 0 ? choices : Viewers, Off: true);
        }
        var listed = first.Viewers.Select(Viewer).OfType<QuickViewer>().ToList();
        var chosen = listed.FirstOrDefault(v => isOff?.Invoke(v.Id) != true);
        return new QuickViewMatch(chosen, first.Pattern, choices, Off: false, OffViewer: chosen is null ? listed.FirstOrDefault() : null);
    }

    /// <summary>
    /// Whether <paramref name="name"/> is of the kind <paramref name="pattern"/>, without case: <c>*.ext</c> needs a
    /// name before the extension, a whole name matches itself. The rule of <c>accepts</c> and of the core.
    /// </summary>
    public static bool Matches(string pattern, string name) => pattern.StartsWith('*')
        ? name.Length > pattern.Length - 1 && name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
        : string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
}
