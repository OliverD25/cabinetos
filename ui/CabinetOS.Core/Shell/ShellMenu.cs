using System.Text.Json;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Settings;
using CabinetOS.Core.Updates;

namespace CabinetOS.Core.Shell;

/// <summary>
/// One entry of the top row's hamburger menu: the command it runs, its title and its first key;
/// <see cref="Dot"/> marks the entry of an update that waits for a restart.
/// </summary>
public sealed record ShellMenuItem(string CommandId, string Title, string? Keys, bool Dot = false);

/// <summary>
/// A preference row of the hamburger menu (Phase 23, the settings-three-ways skill): a toggle with its state in
/// <see cref="Checked"/>, or a submenu of <see cref="Choices"/> of which the current one is checked. A toggle runs its
/// command; so does a choice, and the rows of a submenu run theirs. <see cref="Args"/> are what the command is given: a pick-list
/// command takes the value of the row as <c>{"value": ...}</c> and writes without its list.
/// </summary>
public sealed record ShellPreference(string CommandId, string Title, bool? Checked = null, IReadOnlyList<ShellPreference>? Choices = null, string? Keys = null, JsonElement? Args = null);

/// <summary>
/// The top row's hamburger menu (the creator's SHELL_REDESIGN.md §1): the
/// common commands, with their titles and keys as the registry has them now,
/// so a rebinding shows at once. The registry has no mark for "in this
/// menu", so the menu lists these eight; a command the registry does not
/// list (an older core) is left out.
/// </summary>
public static class ShellMenu
{
    /// <summary>New Tab, New Folder, Find in Pane, Go to Path…, Toggle Sidebar, Marketplace, Keyboard Shortcuts, Check for Updates.</summary>
    public static readonly IReadOnlyList<string> CommandIds =
    [
        "tab.new",
        "file.newFolder",
        "search.focus",
        "go.toPath",
        "view.toggleSidebar",
        "marketplace.browse",
        "keys.open",
        "update.check",
    ];

    /// <summary>The command that takes Check for Updates' place while an update waits (Phase 17).</summary>
    public const string RestartToUpdate = "update.apply";

    /// <summary>The row whose submenu has the three layouts.</summary>
    public const string LayoutTitle = "Layout";

    /// <summary>The checkable row for <c>panes.showHidden</c>.</summary>
    public const string ShowHiddenTitle = "Show Hidden Files";

    /// <summary>The checkable row for <c>ui.sidebarAutoReveal</c>.</summary>
    public const string FollowTitle = "Follow the Active Pane";

    /// <summary>
    /// The preferences the menu shows after Toggle Sidebar (the settings-three-ways skill): "Layout" with a submenu of the
    /// three layouts, the current one checked, then "Show Hidden Files" and "Follow the Active Pane", each checked while its
    /// setting is on. A row whose command the registry does not list (an older core) is left out.
    /// </summary>
    public static IReadOnlyList<ShellPreference> Preferences(IEnumerable<CommandInfo> commands, string layout, bool showHidden, bool followActivePane)
    {
        var byId = new Dictionary<string, CommandInfo>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            byId.TryAdd(command.Id, command);
        }
        string? KeysOf(string id) => byId.TryGetValue(id, out var command) ? FirstKeys(command.Keys) : null;
        var rows = new List<ShellPreference>();
        var current = Layouts.Normalize(layout);
        var choices = Layouts.All.Where(l => byId.ContainsKey(Layouts.CommandOf(l)))
            .Select(l => new ShellPreference(Layouts.CommandOf(l), Layouts.TitleOf(l), l == current)).ToList();
        if (choices.Count > 0)
        {
            // The row's own key is the one that goes to the next layout.
            rows.Add(new ShellPreference("", LayoutTitle, Choices: choices, Keys: KeysOf("view.cycleLayout")));
        }
        if (byId.ContainsKey("view.toggleHiddenFiles"))
        {
            rows.Add(new ShellPreference("view.toggleHiddenFiles", ShowHiddenTitle, showHidden, Keys: KeysOf("view.toggleHiddenFiles")));
        }
        if (byId.ContainsKey("sidebar.toggleFollow"))
        {
            rows.Add(new ShellPreference("sidebar.toggleFollow", FollowTitle, followActivePane, Keys: KeysOf("sidebar.toggleFollow")));
        }
        return rows;
    }

    /// <summary>The row whose submenu has the editor's choices (<c>files.editor</c>).</summary>
    public const string EditorTitle = "Editor";

    /// <summary>The row whose submenu has the levels of <c>logging.level</c>.</summary>
    public const string LogLevelTitle = "Log Level";

    /// <summary>
    /// The settings the menu shows after the preference rows (the settings-three-ways skill, gaps 6 and 7), each a row with a
    /// submenu: "Editor" (<see cref="EditorChoices"/>: Windows' default, the programs of <paramref name="programs"/>, Choose…),
    /// "Update Settings" (<see cref="UpdateSettingsMenu"/>) and "Log Level" (<see cref="LogLevels"/>); the one in effect is checked.
    /// A row whose command the registry does not list (an older core) is left out.
    /// </summary>
    public static IReadOnlyList<ShellPreference> MoreSettings(IEnumerable<CommandInfo> commands, UiSettings settings, IReadOnlyList<EditorProgram>? programs = null)
    {
        var ids = commands.Select(command => command.Id).ToHashSet(StringComparer.Ordinal);
        var rows = new List<ShellPreference>();
        if (ids.Contains("preferences.chooseEditor"))
        {
            var editor = EditorChoices.Choices(settings.Editor, programs ?? [])
                .Select(choice => new ShellPreference("preferences.chooseEditor", choice.Title, choice.Checked, Args: choice.Args)).ToList();
            rows.Add(new ShellPreference("", EditorTitle, Choices: editor));
        }
        var update = UpdateSettingsMenu.Rows(settings.UpdateCheck, settings.UpdateAutoInstall, settings.UpdateChannel)
            .Where(row => ids.Contains(row.CommandId))
            .Select(row => new ShellPreference(row.CommandId, row.Title, row.Checked, Args: row.Value is null ? null : ValueArgs(row.Value)))
            .ToList();
        if (update.Count > 0)
        {
            rows.Add(new ShellPreference("", UpdateSettingsMenu.Title, Choices: update));
        }
        if (ids.Contains("diagnostics.chooseLogLevel"))
        {
            var current = LogLevels.Normalize(settings.LogLevel);
            var levels = LogLevels.All
                .Select(level => new ShellPreference("diagnostics.chooseLogLevel", LogLevels.TitleOf(level), level == current, Args: ValueArgs(level))).ToList();
            rows.Add(new ShellPreference("", LogLevelTitle, Choices: levels));
        }
        return rows;
    }

    /// <summary>The arguments <c>{"value": value}</c> of a pick-list command.</summary>
    public static JsonElement ValueArgs(string value) => JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["value"] = value });

    /// <summary>
    /// The menu's entries from the registry's commands, in the menu's order. While an update
    /// waits (<paramref name="waitingUpdate"/> names its version), Check for Updates gives its
    /// place to "Restart to Update (version)" with a dot, as the menu button carries one.
    /// </summary>
    public static IReadOnlyList<ShellMenuItem> Build(IEnumerable<CommandInfo> commands, string? waitingUpdate = null)
    {
        var byId = new Dictionary<string, CommandInfo>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            byId.TryAdd(command.Id, command);
        }
        return [.. CommandIds.Where(byId.ContainsKey).Select(id =>
            id == "update.check" && waitingUpdate is not null && byId.TryGetValue(RestartToUpdate, out var restart)
                ? new ShellMenuItem(restart.Id, $"{restart.Title} ({waitingUpdate})", FirstKeys(restart.Keys), Dot: true)
                : new ShellMenuItem(id, byId[id].Title, FirstKeys(byId[id].Keys)))];
    }

    /// <summary>A command's first binding as menus show it ("Ctrl+T", "Ctrl+K Ctrl+S"), or null.</summary>
    public static string? FirstKeys(IReadOnlyList<string> keys) =>
        keys is [var first, ..] && KeySequence.TryParse(first, out var sequence) ? string.Join(' ', sequence.DisplayParts()) : null;
}
