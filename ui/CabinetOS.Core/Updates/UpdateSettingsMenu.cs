namespace CabinetOS.Core.Updates;

/// <summary>
/// One row of the update settings' menu: a check row for a toggle, or a channel's row, checked while it is the channel in
/// effect. <see cref="Value"/> is what the command is given as its <c>value</c> (the channel's name); null for a toggle.
/// </summary>
public sealed record UpdateSettingRow(string Title, string CommandId, bool Checked, string? Value = null);

/// <summary>
/// The update settings as a menu (the settings-three-ways skill, gap 6): <c>update.check</c>, <c>update.autoInstall</c> and
/// <c>update.channel</c>. The status bar's update pill has them in its flyout, and the top row's menu in its "Update
/// Settings" submenu; both are drawn from these rows, so they say the same. Apart from the views so it is tested on its own.
/// </summary>
public static class UpdateSettingsMenu
{
    /// <summary>The submenu's title in the top row's menu.</summary>
    public const string Title = "Update Settings";

    /// <summary>The check row of <c>update.check</c>.</summary>
    public const string CheckTitle = "Check Automatically";

    /// <summary>The check row of <c>update.autoInstall</c>.</summary>
    public const string AutoInstallTitle = "Install Automatically";

    /// <summary>The channels, in the order the menu and the pick list list them: the values of <c>update.channel</c>.</summary>
    public static IReadOnlyList<string> Channels { get; } = ["stable", "preview"];

    /// <summary>The channel the window shows for a value of <c>update.channel</c>: a value that is neither shows <c>stable</c>.</summary>
    public static string NormalizeChannel(string? channel) => channel == "preview" ? "preview" : "stable";

    /// <summary>What a row or a notice calls <paramref name="channel"/>.</summary>
    public static string ChannelTitle(string channel) => NormalizeChannel(channel) == "preview" ? "Preview Channel" : "Stable Channel";

    /// <summary>The rows: the two toggles, then one row for each channel with the one in effect checked.</summary>
    public static IReadOnlyList<UpdateSettingRow> Rows(bool check, bool autoInstall, string? channel)
    {
        var current = NormalizeChannel(channel);
        return
        [
            new UpdateSettingRow(CheckTitle, "update.toggleCheck", check),
            new UpdateSettingRow(AutoInstallTitle, "update.toggleAutoInstall", autoInstall),
            .. Channels.Select(name => new UpdateSettingRow(ChannelTitle(name), "update.chooseChannel", name == current, name)),
        ];
    }

    /// <summary>The rows as the window's log tells them: "|" between rows, " [x]" or " [ ]" after each title.</summary>
    public static string Describe(IEnumerable<UpdateSettingRow> rows) =>
        string.Join("|", rows.Select(row => row.Title + (row.Checked ? " [x]" : " [ ]")));
}
