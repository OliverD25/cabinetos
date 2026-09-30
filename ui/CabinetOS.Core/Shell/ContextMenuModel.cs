using System.Buffers;
using System.Text.Json;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Shell;

/// <summary>What a right-click (or Shift+F10) opened the menu on.</summary>
public enum MenuTargetKind
{
    /// <summary>The pane's empty space: the folder itself.</summary>
    Background,

    /// <summary>One file.</summary>
    File,

    /// <summary>One folder.</summary>
    Folder,

    /// <summary>A row inside a selection of several rows.</summary>
    MultiSelect,
}

/// <summary>One row of a target in <c>contextMenu</c>: a command (with an optional extensions filter) or a divider.</summary>
public sealed record MenuItemConfig(string? Command, IReadOnlyList<string>? Extensions = null, bool Separator = false)
{
    /// <summary>A divider.</summary>
    public static MenuItemConfig Divider { get; } = new(null, null, true);
}

/// <summary>One target of <c>contextMenu</c>: the icon row's command IDs and the rows.</summary>
public sealed record MenuTargetConfig(IReadOnlyList<string> QuickActions, IReadOnlyList<MenuItemConfig> Items);

/// <summary>
/// <c>contextMenu</c> of cabinetos.json (docs/config.md, "The context menu"), as the core sends it
/// in <c>config</c>. The core fills in the defaults, which are the menus of Phase 5; the window
/// keeps the same defaults for a core that does not know the key yet.
/// </summary>
public sealed record ContextMenuConfig(
    bool ShellMenu,
    MenuTargetConfig Background,
    MenuTargetConfig File,
    MenuTargetConfig Folder,
    MenuTargetConfig MultiSelect)
{
    private static readonly IReadOnlyList<string> RowQuickActions = ["edit.cut", "edit.copy", "edit.paste", "file.rename", "file.delete"];

    private static readonly IReadOnlyList<MenuItemConfig> RowItems =
        [new("pane.openSelected"), new("file.openInOtherPane"), new("file.copyToOtherPane"), new("terminal.new")];

    /// <summary>The defaults of the core (cabinetos-config's menu.rs): today's menus.</summary>
    public static ContextMenuConfig Defaults { get; } = new(
        false,
        new([], [new("edit.paste"), new("file.newFolder"), new("sidebar.pin")]),
        new(RowQuickActions, RowItems),
        new(RowQuickActions, RowItems),
        new(RowQuickActions, RowItems));

    /// <summary>The target's lists.</summary>
    public MenuTargetConfig For(MenuTargetKind kind) => kind switch
    {
        MenuTargetKind.Background => Background,
        MenuTargetKind.File => File,
        MenuTargetKind.Folder => Folder,
        _ => MultiSelect,
    };

    /// <summary>Reads <c>contextMenu</c> from the <c>config</c> reply; what is missing or of the wrong kind keeps its default.</summary>
    public static ContextMenuConfig FromConfig(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty("contextMenu", out var menu) || menu.ValueKind != JsonValueKind.Object)
        {
            return Defaults;
        }
        var shell = menu.TryGetProperty("shellMenu", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new ContextMenuConfig(
            shell,
            Target(menu, "background", Defaults.Background),
            Target(menu, "file", Defaults.File),
            Target(menu, "folder", Defaults.Folder),
            Target(menu, "multiSelect", Defaults.MultiSelect));
    }

    private static MenuTargetConfig Target(JsonElement menu, string name, MenuTargetConfig fallback)
    {
        if (!menu.TryGetProperty(name, out var target) || target.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }
        var quick = target.TryGetProperty("quickActions", out var ids) && ids.ValueKind == JsonValueKind.Array
            ? [.. ids.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.String).Select(id => id.GetString()!)]
            : fallback.QuickActions;
        var items = target.TryGetProperty("items", out var rows) && rows.ValueKind == JsonValueKind.Array
            ? [.. rows.EnumerateArray().Where(row => row.ValueKind == JsonValueKind.Object).Select(Item)]
            : fallback.Items;
        return new MenuTargetConfig(quick, items);
    }

    private static MenuItemConfig Item(JsonElement row)
    {
        if (row.TryGetProperty("separator", out var separator) && separator.ValueKind == JsonValueKind.True)
        {
            return MenuItemConfig.Divider;
        }
        var command = row.TryGetProperty("command", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        IReadOnlyList<string>? extensions = row.TryGetProperty("extensions", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : null;
        return new MenuItemConfig(command, extensions);
    }
}

/// <summary>What an entry of the menu is.</summary>
public enum ContextMenuEntryKind
{
    /// <summary>An icon of the row at the top.</summary>
    QuickAction,

    /// <summary>A command in the list.</summary>
    Item,

    /// <summary>A divider line.</summary>
    Separator,

    /// <summary>A group's label, such as "From plugins".</summary>
    Header,
}

/// <summary>
/// One entry of the built menu. <see cref="Glyph"/> is the Segoe Fluent icon (null: none),
/// <see cref="Keys"/> the first binding as the menu shows it. <see cref="Args"/> are the command's
/// arguments; <see cref="WantsTargets"/> asks the window for <c>{"path", "paths"}</c> when the entry
/// runs (a plugin's command), so a menu over 100,000 marked rows lists no paths while it opens.
/// </summary>
public sealed record ContextMenuEntry(
    ContextMenuEntryKind Kind,
    string Title = "",
    string? Glyph = null,
    string? CommandId = null,
    JsonElement? Args = null,
    string? Keys = null,
    bool IsEnabled = true,
    string? Badge = null,
    string? Tooltip = null,
    bool WantsTargets = false)
{
    /// <summary>A divider.</summary>
    public static ContextMenuEntry Divider { get; } = new(ContextMenuEntryKind.Separator);
}

/// <summary>The built menu: the icon row, the list, and the command IDs of the file that no command has.</summary>
public sealed record ContextMenuView(IReadOnlyList<ContextMenuEntry> QuickActions, IReadOnlyList<ContextMenuEntry> Items, IReadOnlyList<string> Unknown)
{
    /// <summary>The list's titles, "|" between them (for the log and the checks).</summary>
    public string DescribeItems() => string.Join("|", Items.Where(i => i.Kind == ContextMenuEntryKind.Item).Select(i => i.Title));

    /// <summary>The icon row's titles, "|" between them.</summary>
    public string DescribeQuickActions() => string.Join("|", QuickActions.Select(i => i.Title));
}

/// <summary>The extensions of the selected rows, lower case with their dot, and whether a folder is among them.</summary>
public sealed record SelectionExtensions(IReadOnlySet<string> Extensions, bool AnyFolder, bool AnyWithout);

/// <summary>
/// What the menu is opened on and what the window knows about the moment (nothing is read from the
/// disk): the target, its path, the pane's folder, and the states that enable or hide an entry.
/// <see cref="Selection"/> is asked for only when an entry has an extensions filter and several
/// rows are selected.
/// </summary>
public sealed record ContextMenuFacts(
    MenuTargetKind Kind,
    string PanePath,
    string? EntryPath = null,
    bool EntryIsFolder = false,
    int SelectedCount = 0,
    bool ClipboardEmpty = true,
    bool DualPane = true,
    bool RenameAvailable = true,
    bool OpenAvailable = true,
    bool CreateDirectoryAvailable = true,
    bool PanePinned = false,
    Func<SelectionExtensions>? Selection = null);

/// <summary>
/// The right-click menu (Phase 18, docs/ui.md "The context menu"), built from <c>contextMenu</c>
/// at every opening, so a saved edit of the file shows at the next right-click. The order: the
/// target's items in the file's order; after a divider the plugins' commands of the file pane
/// (rows only, with the plugin's name); after a divider Properties; then "Edit Menu…". A command
/// ID no command has (a plugin that is off) is left out and reported in
/// <see cref="ContextMenuView.Unknown"/>. Titles and icons of today's entries are the menu's own;
/// any other command shows its registry title and no icon unless the map below has one.
/// </summary>
public static class ContextMenuModel
{
    /// <summary>The command "Edit Menu…" runs.</summary>
    public const string EditMenu = "menu.edit";

    /// <summary>The command of Properties, which the window adds last.</summary>
    public const string Properties = "file.properties";

    /// <summary>Segoe Fluent glyphs of the commands the window's menus show (the Phase 5 menu, the hamburger).</summary>
    private static readonly IReadOnlyDictionary<string, string> Glyphs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["edit.cut"] = "",
        ["edit.copy"] = "",
        ["edit.paste"] = "",
        ["file.rename"] = "",
        ["file.delete"] = "",
        ["pane.openSelected"] = "",
        ["file.openInOtherPane"] = "",
        ["file.copyToOtherPane"] = "",
        ["terminal.new"] = "",
        ["file.newFolder"] = "",
        ["sidebar.pin"] = "",
        [Properties] = "",
        [EditMenu] = "",
        ["tab.new"] = "",
        ["search.focus"] = "",
        ["go.toPath"] = "",
        ["view.toggleSidebar"] = "",
        ["marketplace.browse"] = "",
        ["keys.open"] = "",
        ["update.check"] = "",
        ["update.apply"] = "",
    };

    /// <summary>The menu's own titles for today's entries, which read better in a menu than the palette's.</summary>
    private static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["edit.cut"] = "Cut",
        ["edit.copy"] = "Copy",
        ["edit.paste"] = "Paste",
        ["file.rename"] = "Rename",
        ["file.delete"] = "Delete",
        ["pane.openSelected"] = "Open",
        ["file.openInOtherPane"] = "Open in other pane",
        ["file.copyToOtherPane"] = "Copy to other pane",
        ["terminal.new"] = "Open in Terminal",
        ["file.newFolder"] = "New folder",
        ["sidebar.pin"] = "Pin this folder to the sidebar",
        [Properties] = "Properties",
        [EditMenu] = "Edit Menu…",
    };

    /// <summary>The Segoe Fluent glyph of a command in the window's menus, or null.</summary>
    public static string? GlyphOf(string commandId) => Glyphs.GetValueOrDefault(commandId);

    /// <summary>
    /// Builds the menu for <paramref name="facts"/> from the target's lists in
    /// <paramref name="config"/>, the registry's <paramref name="commands"/> and
    /// <paramref name="keysOf"/> (a command's first binding as the menu shows it).
    /// </summary>
    public static ContextMenuView Build(ContextMenuConfig config, ContextMenuFacts facts, IReadOnlyList<CommandInfo> commands, Func<string, string?> keysOf)
    {
        var byId = new Dictionary<string, CommandInfo>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            byId.TryAdd(command.Id, command);
        }
        var unknown = new List<string>();
        var target = config.For(facts.Kind);
        var row = facts.Kind != MenuTargetKind.Background;

        var quick = new List<ContextMenuEntry>();
        foreach (var id in target.QuickActions)
        {
            if (!byId.TryGetValue(id, out var command))
            {
                unknown.Add(id);
                continue;
            }
            if (Hidden(id, facts))
            {
                continue;
            }
            var title = TitleOf(command);
            var tooltipTitle = id == "file.delete" ? "Delete to the Recycle Bin" : title;
            quick.Add(new ContextMenuEntry(ContextMenuEntryKind.QuickAction, title, GlyphOf(id), id, ArgsOf(id, facts),
                IsEnabled: Enabled(command, facts), Tooltip: keysOf(id) is { } keys ? $"{tooltipTitle} ({keys})" : tooltipTitle,
                WantsTargets: command.Source.Kind == "plugin"));
        }

        var items = new List<ContextMenuEntry>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        SelectionExtensions? selection = null;
        foreach (var item in target.Items)
        {
            if (item.Separator)
            {
                items.Add(ContextMenuEntry.Divider);
                continue;
            }
            // Properties and "Edit Menu…" have their own places at the end.
            if (item.Command is not { Length: > 0 } id || id is Properties or EditMenu)
            {
                continue;
            }
            if (!byId.TryGetValue(id, out var command))
            {
                unknown.Add(id);
                continue;
            }
            if (Hidden(id, facts) || (item.Extensions is { } extensions && !Matches(extensions, facts, ref selection)))
            {
                continue;
            }
            listed.Add(id);
            items.Add(new ContextMenuEntry(ContextMenuEntryKind.Item, TitleOf(command), GlyphOf(id), id, ArgsOf(id, facts),
                ShowsKeys(id) ? keysOf(id) : null, Enabled(command, facts), Badge(command), WantsTargets: command.Source.Kind == "plugin"));
        }
        items = Tidy(items);

        if (row)
        {
            var plugins = commands.Where(c => c.Source.Kind == "plugin" && c.When == Keys.KeyContexts.FilesView && !listed.Contains(c.Id)).ToList();
            if (plugins.Count > 0)
            {
                items.Add(ContextMenuEntry.Divider);
                items.Add(new ContextMenuEntry(ContextMenuEntryKind.Header, "From plugins"));
                items.AddRange(plugins.Select(p => new ContextMenuEntry(ContextMenuEntryKind.Item, p.Title, CommandId: p.Id,
                    Keys: keysOf(p.Id), Badge: Badge(p), WantsTargets: true)));
            }
        }
        if (items.Count > 0)
        {
            items.Add(ContextMenuEntry.Divider);
        }
        // The folder's Properties has no keys: Alt+Enter is the focused row's.
        items.Add(new ContextMenuEntry(ContextMenuEntryKind.Item, "Properties", GlyphOf(Properties), Properties,
            row ? null : Json(("scope", "folder")), row ? keysOf(Properties) : null));
        if (byId.ContainsKey(EditMenu))
        {
            items.Add(new ContextMenuEntry(ContextMenuEntryKind.Item, "Edit Menu…", GlyphOf(EditMenu), EditMenu, Keys: keysOf(EditMenu)));
        }
        return new ContextMenuView(quick, items, unknown);
    }

    /// <summary>
    /// The extension of a name as a menu filter compares it: from the last dot on, lower case;
    /// empty when there is none. The name is a file's, not its folder's.
    /// </summary>
    public static string ExtensionOf(ReadOnlySpan<char> name)
    {
        var slash = name.LastIndexOfAny('\\', '/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }
        var dot = name.LastIndexOf('.');
        return dot < 0 || dot == name.Length - 1 ? "" : name[dot..].ToString().ToLowerInvariant();
    }

    private static string TitleOf(CommandInfo command) => Titles.GetValueOrDefault(command.Id) ?? command.Title;

    private static string? Badge(CommandInfo command) =>
        command.Source.Kind == "plugin" ? command.Source.Name ?? command.Source.Id ?? "plugin" : null;

    // "Open in Terminal" shows no keys: Ctrl+` toggles the terminal pane, which is not a new shell here.
    private static bool ShowsKeys(string id) => id != "terminal.new";

    private static bool Hidden(string id, ContextMenuFacts facts) =>
        id == "sidebar.pin" && (facts.PanePath.Length == 0 || facts.PanePinned);

    private static bool Enabled(CommandInfo command, ContextMenuFacts facts) => command.Id switch
    {
        "edit.paste" => !facts.ClipboardEmpty,
        "file.rename" => facts.RenameAvailable && facts.SelectedCount <= 1 && facts.EntryPath is not null,
        "pane.openSelected" => facts.EntryPath is not null && (facts.EntryIsFolder || facts.OpenAvailable),
        "file.openInOtherPane" => facts.EntryPath is not null && facts.EntryIsFolder,
        "file.copyToOtherPane" => facts.DualPane && facts.EntryPath is not null,
        "file.newFolder" => facts.CreateDirectoryAvailable,
        "edit.cut" or "edit.copy" or "file.delete" => facts.EntryPath is not null,
        // The core fills {path} with the focused entry: a menu without one has nothing to give.
        _ when command.Source.Kind == "program" => facts.EntryPath is not null,
        _ => true,
    };

    private static JsonElement? ArgsOf(string id, ContextMenuFacts facts) => id switch
    {
        // A new shell in the row's folder: a folder's own, a file's folder, or the pane's.
        "terminal.new" => Json(("cwd", facts.EntryIsFolder && facts.EntryPath is { } folder ? folder : facts.PanePath)),
        "sidebar.pin" => Json(("path", facts.PanePath)),
        _ => null,
    };

    private static bool Matches(IReadOnlyList<string> extensions, ContextMenuFacts facts, ref SelectionExtensions? selection)
    {
        var wanted = new HashSet<string>(extensions.Select(e => e.ToLowerInvariant()), StringComparer.Ordinal);
        switch (facts.Kind)
        {
            case MenuTargetKind.File when facts.EntryPath is { } path:
                return wanted.Contains(ExtensionOf(path));
            case MenuTargetKind.MultiSelect when facts.Selection is { } read:
                selection ??= read();
                return !selection.AnyFolder && !selection.AnyWithout && selection.Extensions.Count > 0 && selection.Extensions.All(wanted.Contains);
            default:
                // A folder, or the pane's space, is not a file with an extension.
                return false;
        }
    }

    // No divider first, last, or next to another.
    private static List<ContextMenuEntry> Tidy(List<ContextMenuEntry> items)
    {
        var tidy = new List<ContextMenuEntry>(items.Count);
        foreach (var item in items)
        {
            if (item.Kind == ContextMenuEntryKind.Separator && (tidy.Count == 0 || tidy[^1].Kind == ContextMenuEntryKind.Separator))
            {
                continue;
            }
            tidy.Add(item);
        }
        if (tidy.Count > 0 && tidy[^1].Kind == ContextMenuEntryKind.Separator)
        {
            tidy.RemoveAt(tidy.Count - 1);
        }
        return tidy;
    }

    private static JsonElement Json(params (string Name, string Value)[] fields)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in fields)
            {
                writer.WriteString(name, value);
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
