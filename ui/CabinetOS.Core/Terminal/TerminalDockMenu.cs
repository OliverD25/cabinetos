namespace CabinetOS.Core.Terminal;

/// <summary>What a row of the dock's chevron menu is.</summary>
public enum DockMenuKind
{
    /// <summary>A shell of <c>terminal.profiles</c>: a click starts a terminal with it.</summary>
    Profile,

    /// <summary>A thin line between the shells and the settings.</summary>
    Separator,

    /// <summary>A setting's command; <see cref="DockMenuRow.Checked"/> says whether a toggle is on.</summary>
    Command,
}

/// <summary>One row of the dock's chevron menu.</summary>
public sealed record DockMenuRow(DockMenuKind Kind, string Title, string? CommandId = null, string? Profile = null, bool? Checked = null);

/// <summary>
/// The rows of the dock's chevron menu ("Other shells"): the shells, and under a line the terminal's three defaults
/// (the settings-three-ways skill, gap 5): a row that opens the default-profile picker, and a check row each for
/// <c>terminal.restore</c> and <c>terminal.defaultMode</c>. Apart from the view so it is tested on its own.
/// </summary>
public static class TerminalDockMenu
{
    /// <summary>The row that opens the picker of <c>terminal.defaultProfile</c>.</summary>
    public const string DefaultProfileTitle = "Default Profile…";

    /// <summary>The check row of <c>terminal.restore</c>.</summary>
    public const string RestoreTitle = "Restore Tabs on Start";

    /// <summary>The check row of <c>terminal.defaultMode</c>: checked while new terminals start linked.</summary>
    public const string LinkedTitle = "New Terminals Start Linked";

    /// <summary>The menu's rows; the default shell says "(default)".</summary>
    public static IReadOnlyList<DockMenuRow> Rows(TerminalProfiles profiles, bool restoreTabs, bool startsLinked) =>
    [
        .. profiles.Names.Select(name => new DockMenuRow(DockMenuKind.Profile, name == profiles.DefaultProfile ? $"{name} (default)" : name, Profile: name)),
        new DockMenuRow(DockMenuKind.Separator, ""),
        new DockMenuRow(DockMenuKind.Command, DefaultProfileTitle, "terminal.chooseDefaultProfile"),
        new DockMenuRow(DockMenuKind.Command, RestoreTitle, "terminal.toggleRestore", Checked: restoreTabs),
        new DockMenuRow(DockMenuKind.Command, LinkedTitle, "terminal.toggleDefaultMode", Checked: startsLinked),
    ];

    /// <summary>The rows as the window's log tells them: "|" between rows, "-" for a line, " [x]" or " [ ]" for a check row.</summary>
    public static string Describe(IEnumerable<DockMenuRow> rows) => string.Join("|", rows.Select(row => row.Kind == DockMenuKind.Separator
        ? "-"
        : row.Title + (row.Checked switch { true => " [x]", false => " [ ]", _ => "" })));
}
