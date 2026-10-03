using CabinetOS.Core.Updates;

namespace CabinetOS.Core.Settings;

/// <summary>
/// The window's layouts, the values of <c>ui.layout</c>, and what the commands, the hamburger menu and the palette say of
/// them (Phase 23, the settings-three-ways skill): one command for each value and one that goes to the next. The layout is
/// written with <c>set_value</c>, so the file and the window follow whichever way changed it.
/// </summary>
public static class Layouts
{
    /// <summary>The terminal under the panes, the sidebar beside them (the default).</summary>
    public const string Classic = "classic";

    /// <summary>The terminal on the right of the panes.</summary>
    public const string Right = "right";

    /// <summary>The activity rail with the sidebar's views and the terminal under the panes.</summary>
    public const string Rail = "rail";

    /// <summary>The layouts, in the order <c>view.cycleLayout</c> goes through them.</summary>
    public static IReadOnlyList<string> All { get; } = [Classic, Right, Rail];

    /// <summary>The layout a <c>view.layout*</c> command chooses; null for any other command.</summary>
    public static string? FromCommand(string commandId) => commandId switch
    {
        "view.layoutClassic" => Classic,
        "view.layoutRight" => Right,
        "view.layoutRail" => Rail,
        _ => null,
    };

    /// <summary>The command that chooses <paramref name="layout"/>.</summary>
    public static string CommandOf(string layout) => Normalize(layout) switch
    {
        Right => "view.layoutRight",
        Rail => "view.layoutRail",
        _ => "view.layoutClassic",
    };

    /// <summary>
    /// The layout the window shows for a value of <c>ui.layout</c>: a value that is none of the three (a hand edit that
    /// slipped past the schema) shows the classic layout.
    /// </summary>
    public static string Normalize(string? layout) => layout is Right or Rail ? layout : Classic;

    /// <summary>The layout after <paramref name="current"/> (<c>view.cycleLayout</c>): classic, right, rail, then classic again.</summary>
    public static string Next(string? current) => Normalize(current) switch
    {
        Classic => Right,
        Right => Rail,
        _ => Classic,
    };

    /// <summary>What a menu row or a palette title calls <paramref name="layout"/>.</summary>
    public static string TitleOf(string layout) => Normalize(layout) switch
    {
        Right => "Terminal on the Right",
        Rail => "Activity Rail",
        _ => "Classic Layout",
    };
}

/// <summary>
/// What the palette's row of a command says of the setting it changes (the settings-three-ways skill: "a boolean gets a
/// toggle command whose row shows the current state"): "current" on the layout in effect, "on" or "off" on a toggle, the value in
/// effect on a picker's row, "linked" or "locked" on the terminal's mode.
/// </summary>
public static class SettingStates
{
    /// <summary>The mark of the layout in effect.</summary>
    public const string Current = "current";

    /// <summary>The mark of a toggle that is on.</summary>
    public const string On = "on";

    /// <summary>The mark of a toggle that is off.</summary>
    public const string Off = "off";

    /// <summary>The mark of <c>terminal.defaultMode</c> when new terminals start linked to their pane.</summary>
    public const string Linked = "linked";

    /// <summary>The mark of <c>terminal.defaultMode</c> when new terminals start locked.</summary>
    public const string Locked = "locked";

    /// <summary>
    /// The mark for <paramref name="commandId"/>'s palette row given the settings now (<paramref name="shellMenu"/> is
    /// <c>contextMenu.shellMenu</c>), or null for a command that has none.
    /// </summary>
    public static string? Of(string commandId, UiSettings settings, bool shellMenu) => commandId switch
    {
        "view.layoutClassic" or "view.layoutRight" or "view.layoutRail" =>
            Layouts.FromCommand(commandId) == Layouts.Normalize(settings.Layout) ? Current : null,
        "view.toggleHiddenFiles" => OnOff(settings.ShowHidden),
        "sidebar.toggleFollow" => OnOff(settings.SidebarAutoReveal),
        "menu.toggleShellMenu" => OnOff(shellMenu),
        // The terminal's defaults (gap 5): the picker's row names the profile in effect.
        "terminal.chooseDefaultProfile" => settings.TerminalDefaultProfile,
        "terminal.toggleRestore" => OnOff(settings.TerminalRestore),
        "terminal.toggleDefaultMode" => settings.TerminalStartsLinked ? Linked : Locked,
        // The update settings (gap 6): the toggles say on or off, the picker's row says the channel in effect.
        "update.toggleCheck" => OnOff(settings.UpdateCheck),
        "update.toggleAutoInstall" => OnOff(settings.UpdateAutoInstall),
        "update.chooseChannel" => UpdateSettingsMenu.NormalizeChannel(settings.UpdateChannel),
        _ => null,
    };

    private static string OnOff(bool on) => on ? On : Off;
}
