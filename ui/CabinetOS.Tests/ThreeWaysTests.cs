using System.Text.Json;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Settings;
using CabinetOS.Core.Shell;
using CabinetOS.Core.Terminal;

namespace CabinetOS.Tests;

/// <summary>
/// The four settings that are reachable three ways (Phase 23, the settings-three-ways skill): the layouts the commands
/// choose, the marks the palette's rows carry, and the preference rows of the hamburger menu. The window's controls follow
/// the file in the window tests (ThreeWaysEndToEndTests).
/// </summary>
public class ThreeWaysTests
{
    private static CommandInfo Command(string id, string title, params string[] keys) =>
        new(id, "Any", title, keys, keys, new CommandSource("core", null, null), "ui", null, false);

    private static readonly CommandInfo[] Registry =
    [
        Command("view.layoutClassic", "Classic Layout"),
        Command("view.layoutRight", "Terminal on the Right"),
        Command("view.layoutRail", "Activity Rail"),
        Command("view.cycleLayout", "Next Layout", "ctrl+k ctrl+l"),
        Command("view.toggleHiddenFiles", "Toggle Hidden Files", "ctrl+k ctrl+h"),
        Command("sidebar.toggleFollow", "Follow the Active Pane"),
        Command("menu.toggleShellMenu", "Toggle Windows' Shell Menu"),
    ];

    [Theory]
    [InlineData("view.layoutClassic", "classic")]
    [InlineData("view.layoutRight", "right")]
    [InlineData("view.layoutRail", "rail")]
    public void Each_layout_command_chooses_its_layout_and_the_layout_names_its_command(string command, string layout)
    {
        Assert.Equal(layout, Layouts.FromCommand(command));
        Assert.Equal(command, Layouts.CommandOf(layout));
    }

    [Fact]
    public void Another_command_chooses_no_layout()
    {
        Assert.Null(Layouts.FromCommand("view.cycleLayout"));
        Assert.Null(Layouts.FromCommand("view.toggleTerminal"));
    }

    [Fact]
    public void Next_Layout_goes_classic_right_rail_and_round_again()
    {
        Assert.Equal(["classic", "right", "rail"], Layouts.All);
        Assert.Equal("right", Layouts.Next("classic"));
        Assert.Equal("rail", Layouts.Next("right"));
        Assert.Equal("classic", Layouts.Next("rail"));
        // A value the window shows as the classic layout goes on from there; so does none.
        Assert.Equal("right", Layouts.Next("sideways"));
        Assert.Equal("right", Layouts.Next(null));
    }

    [Fact]
    public void A_layout_value_that_is_none_of_the_three_is_the_classic_layout()
    {
        Assert.Equal(("classic", "classic", "right", "rail"), (Layouts.Normalize(null), Layouts.Normalize("tabs"), Layouts.Normalize("right"), Layouts.Normalize("rail")));
        Assert.Equal(["Classic Layout", "Terminal on the Right", "Activity Rail"], Layouts.All.Select(Layouts.TitleOf));
    }

    [Theory]
    [InlineData("rail", "view.layoutRail", "current")]
    [InlineData("rail", "view.layoutClassic", null)]
    [InlineData("right", "view.layoutRight", "current")]
    [InlineData("classic", "view.layoutClassic", "current")]
    // A hand edit that slipped past the schema shows the classic layout, and the palette says so.
    [InlineData("odd", "view.layoutClassic", "current")]
    public void The_palette_marks_the_layout_in_effect(string layout, string command, string? mark)
    {
        Assert.Equal(mark, SettingStates.Of(command, UiSettings.Defaults with { Layout = layout }, shellMenu: false));
    }

    [Fact]
    public void The_palette_says_whether_a_toggle_is_on()
    {
        var off = UiSettings.Defaults with { ShowHidden = false, SidebarAutoReveal = false };
        var on = UiSettings.Defaults with { ShowHidden = true, SidebarAutoReveal = true };
        Assert.Equal(("off", "off", "off"), (SettingStates.Of("view.toggleHiddenFiles", off, false), SettingStates.Of("sidebar.toggleFollow", off, false),
            SettingStates.Of("menu.toggleShellMenu", off, false)));
        Assert.Equal(("on", "on", "on"), (SettingStates.Of("view.toggleHiddenFiles", on, true), SettingStates.Of("sidebar.toggleFollow", on, true),
            SettingStates.Of("menu.toggleShellMenu", on, true)));
        // A command that changes no setting has no mark.
        Assert.Null(SettingStates.Of("view.cycleLayout", on, true));
        Assert.Null(SettingStates.Of("tab.new", on, true));
    }

    [Fact]
    public void The_hamburger_shows_Layout_with_the_current_layout_checked_and_the_two_toggles_with_their_state()
    {
        var rows = ShellMenu.Preferences(Registry, "rail", showHidden: true, followActivePane: false);

        Assert.Equal(["Layout", "Show Hidden Files", "Follow the Active Pane"], rows.Select(r => r.Title));
        var layout = rows[0];
        Assert.Equal(["Classic Layout", "Terminal on the Right", "Activity Rail"], layout.Choices!.Select(c => c.Title));
        Assert.Equal(["view.layoutClassic", "view.layoutRight", "view.layoutRail"], layout.Choices!.Select(c => c.CommandId));
        Assert.Equal([false, false, true], layout.Choices!.Select(c => c.Checked == true));
        // The Layout row has the key that goes to the next layout, as the toggle has its own.
        Assert.Equal(("Ctrl+K Ctrl+L", null), (layout.Keys, layout.Checked));
        Assert.Equal(("view.toggleHiddenFiles", true, "Ctrl+K Ctrl+H"), (rows[1].CommandId, rows[1].Checked, rows[1].Keys));
        Assert.Equal(("sidebar.toggleFollow", false, null), (rows[2].CommandId, rows[2].Checked, rows[2].Keys));
    }

    [Fact]
    public void A_layout_the_file_names_oddly_checks_the_classic_one_and_an_older_core_leaves_rows_out()
    {
        var odd = ShellMenu.Preferences(Registry, "odd", showHidden: false, followActivePane: true);
        Assert.Equal([true, false, false], odd[0].Choices!.Select(c => c.Checked == true));

        // A core without the new commands: no row of them.
        Assert.Empty(ShellMenu.Preferences([Command("tab.new", "New Tab")], "classic", false, true));
        var onlyHidden = ShellMenu.Preferences(Registry.Where(c => c.Id is "view.toggleHiddenFiles" or "view.layoutRail"), "classic", false, true);
        Assert.Equal(["Layout", "Show Hidden Files"], onlyHidden.Select(r => r.Title));
        Assert.Equal(["Activity Rail"], onlyHidden[0].Choices!.Select(c => c.Title));
    }

    // ----- Gap 5: the terminal's defaults -----

    [Fact]
    public void The_terminal_defaults_are_read_from_the_configuration_and_default_to_pwsh_locked_and_restoring()
    {
        var none = UiSettings.FromConfig(JsonDocument.Parse("""{"terminal": {}}""").RootElement);
        Assert.Equal(("pwsh", false, true), (none.TerminalDefaultProfile, none.TerminalStartsLinked, none.TerminalRestore));
        var set = UiSettings.FromConfig(JsonDocument.Parse("""{"terminal": {"defaultProfile": "cmd", "defaultMode": "linked", "restore": false}}""").RootElement);
        Assert.Equal(("cmd", true, false), (set.TerminalDefaultProfile, set.TerminalStartsLinked, set.TerminalRestore));
    }

    [Fact]
    public void The_palette_names_the_default_profile_and_says_whether_the_toggles_are_on()
    {
        var restoring = UiSettings.Defaults with { TerminalDefaultProfile = "wsl", TerminalRestore = true, TerminalStartsLinked = false };
        var other = UiSettings.Defaults with { TerminalDefaultProfile = "cmd", TerminalRestore = false, TerminalStartsLinked = true };
        Assert.Equal(("wsl", "on", "locked"), (SettingStates.Of("terminal.chooseDefaultProfile", restoring, false),
            SettingStates.Of("terminal.toggleRestore", restoring, false), SettingStates.Of("terminal.toggleDefaultMode", restoring, false)));
        Assert.Equal(("cmd", "off", "linked"), (SettingStates.Of("terminal.chooseDefaultProfile", other, false),
            SettingStates.Of("terminal.toggleRestore", other, false), SettingStates.Of("terminal.toggleDefaultMode", other, false)));
        // The per-session switch is no setting: its row has no mark.
        Assert.Null(SettingStates.Of("terminal.setMode", other, false));
    }

    [Fact]
    public void The_dock_s_chevron_menu_lists_the_shells_then_the_default_profile_row_and_the_two_check_rows()
    {
        var profiles = new TerminalProfiles("cmd", ["pwsh", "cmd", "wsl"]);
        var rows = TerminalDockMenu.Rows(profiles, restoreTabs: true, startsLinked: false);

        Assert.Equal(["pwsh", "cmd (default)", "wsl"], rows.Where(r => r.Kind == DockMenuKind.Profile).Select(r => r.Title));
        Assert.Equal(["pwsh", "cmd", "wsl"], rows.Where(r => r.Kind == DockMenuKind.Profile).Select(r => r.Profile));
        Assert.Equal(DockMenuKind.Separator, rows[3].Kind);
        Assert.Equal(("Default Profile…", "terminal.chooseDefaultProfile", (bool?)null), (rows[4].Title, rows[4].CommandId, rows[4].Checked));
        Assert.Equal(("Restore Tabs on Start", "terminal.toggleRestore", (bool?)true), (rows[5].Title, rows[5].CommandId, rows[5].Checked));
        Assert.Equal(("New Terminals Start Linked", "terminal.toggleDefaultMode", (bool?)false), (rows[6].Title, rows[6].CommandId, rows[6].Checked));
        Assert.Equal("pwsh|cmd (default)|wsl|-|Default Profile…|Restore Tabs on Start [x]|New Terminals Start Linked [ ]", TerminalDockMenu.Describe(rows));
    }

    [Fact]
    public void The_chevron_menu_follows_the_two_toggles_and_a_single_shell_still_has_its_settings()
    {
        var one = new TerminalProfiles("pwsh", ["pwsh"]);
        var rows = TerminalDockMenu.Rows(one, restoreTabs: false, startsLinked: true);
        Assert.Equal("pwsh (default)|-|Default Profile…|Restore Tabs on Start [ ]|New Terminals Start Linked [x]", TerminalDockMenu.Describe(rows));
    }
}
