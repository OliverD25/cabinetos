using CabinetOS.Core.Keys;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Shell;

/// <summary>One entry of the top row's hamburger menu: the command it runs, its title and its first key.</summary>
public sealed record ShellMenuItem(string CommandId, string Title, string? Keys);

/// <summary>
/// The top row's hamburger menu (the creator's SHELL_REDESIGN.md §1): the
/// common commands, with their titles and keys as the registry has them now,
/// so a rebinding shows at once. The registry has no mark for "in this
/// menu", so the menu lists these seven; a command the registry does not
/// list (an older core) is left out.
/// </summary>
public static class ShellMenu
{
    /// <summary>New Tab, New Folder, Find in Pane, Go to Path…, Toggle Sidebar, Marketplace, Keyboard Shortcuts.</summary>
    public static readonly IReadOnlyList<string> CommandIds =
    [
        "tab.new",
        "file.newFolder",
        "search.focus",
        "go.toPath",
        "view.toggleSidebar",
        "marketplace.browse",
        "keys.open",
    ];

    /// <summary>The menu's entries from the registry's commands, in the menu's order.</summary>
    public static IReadOnlyList<ShellMenuItem> Build(IEnumerable<CommandInfo> commands)
    {
        var byId = new Dictionary<string, CommandInfo>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            byId.TryAdd(command.Id, command);
        }
        return [.. CommandIds.Where(byId.ContainsKey).Select(id => new ShellMenuItem(id, byId[id].Title, FirstKeys(byId[id].Keys)))];
    }

    /// <summary>A command's first binding as menus show it ("Ctrl+T", "Ctrl+K Ctrl+S"), or null.</summary>
    public static string? FirstKeys(IReadOnlyList<string> keys) =>
        keys is [var first, ..] && KeySequence.TryParse(first, out var sequence) ? string.Join(' ', sequence.DisplayParts()) : null;
}
