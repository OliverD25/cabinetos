namespace CabinetOS.Core.Sidebar;

/// <summary>What a button of the activity rail stands for.</summary>
public enum RailKind
{
    /// <summary>The Explorer view: pinned folders, drives and the folder tree.</summary>
    Explorer,

    /// <summary>The Search view.</summary>
    Search,

    /// <summary>The marketplace, which takes the main column's place.</summary>
    Marketplace,

    /// <summary>The terminal panel.</summary>
    Terminal,

    /// <summary>The sidebar page of a Tool Extension (<c>"sidebar": true</c> in its <c>tool.json</c>).</summary>
    Tool,
}

/// <summary>A Tool Extension with a sidebar page: its ID and the name its button and tooltip show.</summary>
public sealed record RailTool(string Id, string Name);

/// <summary>
/// One button of the rail (docs/design/README.md, "Activity rail"). The ID is
/// what <c>ui.rail</c>, <c>ui.sidebarView</c> and a plugin's badge event name:
/// <c>explorer</c>, <c>search</c>, <c>marketplace</c>, <c>terminal</c>, or a
/// tool's ID.
/// </summary>
public sealed record RailButton(string Id, RailKind Kind, string Title, string Glyph, string Initials)
{
    /// <summary>Whether the button shows a view in the sidebar (the others act on the window).</summary>
    public bool ShowsInSidebar => Kind is RailKind.Explorer or RailKind.Search or RailKind.Tool;

    /// <summary>The tool's ID for a <see cref="RailKind.Tool"/> button; otherwise null.</summary>
    public string? ToolId => Kind == RailKind.Tool ? Id : null;
}

/// <summary>What a click on a rail button asks the window to do.</summary>
public enum RailAction
{
    /// <summary>Show the button's view in the sidebar (open the sidebar if it is closed).</summary>
    ShowView,

    /// <summary>The button's view is the one shown: close the sidebar.</summary>
    CloseSidebar,

    /// <summary>Open the marketplace, or close it.</summary>
    ToggleMarketplace,

    /// <summary>Show the terminal panel, or hide it.</summary>
    ToggleTerminal,
}

/// <summary>A click's decision. <see cref="LeaveMarketplace"/>: the marketplace is open and a view button brings the panes back.</summary>
public sealed record RailClick(RailAction Action, string? View = null, bool LeaveMarketplace = false);

/// <summary>
/// The activity rail's buttons, their order and their badges. The order is
/// the saved one (<c>ui.rail</c>) with what it does not name at the end in
/// the default order; the default is Explorer, Search, Marketplace,
/// Terminal, then one button per tool with a sidebar page. The model knows
/// no window: it decides, and the window acts.
/// </summary>
public sealed class RailModel
{
    /// <summary>The Explorer view's ID.</summary>
    public const string Explorer = "explorer";

    /// <summary>The Search view's ID.</summary>
    public const string Search = "search";

    /// <summary>The marketplace button's ID.</summary>
    public const string Marketplace = "marketplace";

    /// <summary>The terminal button's ID.</summary>
    public const string Terminal = "terminal";

    /// <summary>The most badges kept; a plugin cannot fill memory by naming views.</summary>
    public const int MaxBadges = 64;

    private static readonly string[] BuiltIn = [Explorer, Search, Marketplace, Terminal];

    private readonly Dictionary<string, RailButton> _buttons = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly Dictionary<string, string> _badges = new(StringComparer.Ordinal);
    private List<string> _defaults = [];
    private List<string> _arranged = [];

    /// <summary>Builds the rail for <paramref name="tools"/> (the tools with a sidebar page, in the order found) and the saved order.</summary>
    public RailModel(IReadOnlyList<RailTool> tools, IReadOnlyList<string> savedOrder)
    {
        _arranged = [.. savedOrder];
        Build(tools);
    }

    /// <summary>The badges or the order changed.</summary>
    public event Action? Changed;

    /// <summary>The buttons, top to bottom.</summary>
    public IReadOnlyList<RailButton> Buttons => _order.Select(id => _buttons[id]).ToList();

    /// <summary>The button IDs, top to bottom: what <c>ui.rail</c> holds after the user moved one.</summary>
    public IReadOnlyList<string> Order => _order;

    /// <summary>Whether the order is the default one (then <c>ui.rail</c> stays empty).</summary>
    public bool IsDefaultOrder => _order.SequenceEqual(_defaults, StringComparer.Ordinal);

    /// <summary>The badges: a view's ID and <c>dot</c> or <c>spinner</c>.</summary>
    public IReadOnlyDictionary<string, string> Badges => _badges;

    /// <summary>The button with this ID, or null.</summary>
    public RailButton? Find(string id) => _buttons.GetValueOrDefault(id);

    /// <summary>Whether <paramref name="id"/> names a view the sidebar can show.</summary>
    public bool IsSidebarView(string id) => Find(id)?.ShowsInSidebar == true;

    /// <summary>Takes a saved order (the file was edited, or read at start); IDs it does not know are dropped.</summary>
    public void SetOrder(IReadOnlyList<string> savedOrder)
    {
        var before = _order.ToArray();
        _arranged = [.. savedOrder];
        Compose();
        if (!before.SequenceEqual(_order, StringComparer.Ordinal))
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The tools with a sidebar page were read (the window reads them once,
    /// after it started). The buttons, the order the user arranged and the
    /// badges stay; a tool's button appears where that order puts it.
    /// </summary>
    public void SetTools(IReadOnlyList<RailTool> tools)
    {
        Build(tools);
        Changed?.Invoke();
    }

    /// <summary>Moves a button up (<paramref name="delta"/> below zero) or down; false when it cannot move that way.</summary>
    public bool Move(string id, int delta)
    {
        var from = _order.IndexOf(id);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= _order.Count || delta == 0)
        {
            return false;
        }
        _order.RemoveAt(from);
        _order.Insert(to, id);
        _arranged = [.. _order];
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Sets or clears a view's badge (the plugin event <c>badge</c>). Returns
    /// whether anything changed.
    /// </summary>
    public bool SetBadge(string view, string? kind)
    {
        if (kind is null)
        {
            if (!_badges.Remove(view))
            {
                return false;
            }
        }
        else if (_badges.TryGetValue(view, out var current))
        {
            if (current == kind)
            {
                return false;
            }
            _badges[view] = kind;
        }
        else if (_badges.Count >= MaxBadges)
        {
            return false;
        }
        else
        {
            _badges[view] = kind;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>The badge of a button: <c>dot</c>, <c>spinner</c> or null.</summary>
    public string? BadgeOf(string id) => _badges.GetValueOrDefault(id);

    /// <summary>
    /// What a click on <paramref name="button"/> does: the button of the view
    /// on show closes the sidebar, any other view button shows its view, and
    /// the two buttons of the window's own toggle theirs.
    /// </summary>
    public static RailClick Click(RailButton button, bool sidebarOpen, string shownView, bool marketplaceOpen)
    {
        switch (button.Kind)
        {
            case RailKind.Marketplace:
                return new RailClick(RailAction.ToggleMarketplace);
            case RailKind.Terminal:
                return new RailClick(RailAction.ToggleTerminal);
        }
        if (marketplaceOpen)
        {
            // The design's Explorer button brings the panes back and opens the sidebar.
            return new RailClick(RailAction.ShowView, button.Id, LeaveMarketplace: true);
        }
        return sidebarOpen && shownView == button.Id
            ? new RailClick(RailAction.CloseSidebar)
            : new RailClick(RailAction.ShowView, button.Id);
    }

    /// <summary>Whether the button wears the accent pill: its view is on show, its panel is open.</summary>
    public static bool IsActive(RailButton button, bool sidebarOpen, string shownView, bool marketplaceOpen, bool terminalOpen) => button.Kind switch
    {
        RailKind.Marketplace => marketplaceOpen,
        RailKind.Terminal => terminalOpen,
        _ => sidebarOpen && !marketplaceOpen && shownView == button.Id,
    };

    /// <summary>Two letters for a button that has no glyph: the first letters of the first two words, or of the one word.</summary>
    public static string InitialsOf(string name)
    {
        var words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()))
            .Where(word => word.Length > 0)
            .ToList();
        var initials = words.Count switch
        {
            0 => "?",
            1 => words[0][..Math.Min(2, words[0].Length)],
            _ => $"{words[0][0]}{words[1][0]}",
        };
        return initials.ToUpperInvariant();
    }

    private void Build(IReadOnlyList<RailTool> tools)
    {
        _buttons.Clear();
        _buttons[Explorer] = new RailButton(Explorer, RailKind.Explorer, "Explorer", "\uE8B7", "EX");
        _buttons[Search] = new RailButton(Search, RailKind.Search, "Search", "\uE721", "SE");
        _buttons[Marketplace] = new RailButton(Marketplace, RailKind.Marketplace, "Extensions", "\uE719", "MK");
        _buttons[Terminal] = new RailButton(Terminal, RailKind.Terminal, "Terminal", "\uE756", ">_");
        _defaults = [.. BuiltIn];
        foreach (var tool in tools)
        {
            // A tool named like a built-in view would answer to its ID: it keeps to the pane.
            if (!_buttons.ContainsKey(tool.Id))
            {
                _buttons[tool.Id] = new RailButton(tool.Id, RailKind.Tool, tool.Name, "", InitialsOf(tool.Name));
                _defaults.Add(tool.Id);
            }
        }
        Compose();
    }

    private void Compose()
    {
        _order.Clear();
        foreach (var id in _arranged)
        {
            if (_buttons.ContainsKey(id) && !_order.Contains(id))
            {
                _order.Add(id);
            }
        }
        foreach (var id in _defaults)
        {
            if (!_order.Contains(id))
            {
                _order.Add(id);
            }
        }
    }
}
