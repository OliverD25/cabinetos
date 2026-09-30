using CabinetOS.Core.Keys;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Shell;

/// <summary>
/// One entry of the top row's hamburger menu: the command it runs, its title and its first key;
/// <see cref="Dot"/> marks the entry of an update that waits for a restart.
/// </summary>
public sealed record ShellMenuItem(string CommandId, string Title, string? Keys, bool Dot = false);

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
