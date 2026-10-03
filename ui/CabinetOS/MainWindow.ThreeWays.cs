using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Settings;
using CabinetOS.Core.Shell;
using CabinetOS.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CabinetOS;

// Four settings that were reachable from the file only (Phase 23, the settings-three-ways skill; the creator's rule of
// 2026-10-03): ui.layout, panes.showHidden, ui.sidebarAutoReveal and contextMenu.shellMenu. Each is a command in the
// palette, a control in the window, and a key of cabinetos.json. The command and the control both write the key through
// set_value; the window shows the key the core announces (config_changed), so a change made any of the three ways is
// the same change, and the window keeps no second copy.
public sealed partial class MainWindow
{
    private const string SettingsTarget = "cabinetos_ui::settings";

    // The layout a command asked for and the file has not shown yet: a second Next Layout before config_changed goes one step on.
    private string? _layoutWanted;

    private void SetUpThreeWays()
    {
        HiddenPill.Click += (_, _) => _ = _router.ExecuteAsync("view.toggleHiddenFiles", trigger: "button");
        SidebarView.Tree.FollowToggled += () => _ = _router.ExecuteAsync("sidebar.toggleFollow", trigger: "button");
        MenuEditorView.ShellMenuToggled += () => _ = _router.ExecuteAsync("menu.toggleShellMenu", trigger: "button");
        // The palette's rows say which layout is in effect and whether a toggle is on.
        _palette.StateOf = info => SettingStates.Of(info.Id, _settings, _menuConfig.ShellMenu);
        SetUpUpdateSettings();
    }

    // The controls of gaps 5 to 7 that are drawn from the configuration: the update pill's flyout. Each read of the
    // configuration comes here, from ApplySettings, so an edit of the file moves their checks.
    private void UpdatePreferenceControls() => UpdateUpdateSettingsFlyout();

    private void RegisterThreeWaysCommands()
    {
        _router.RegisterUiHandler("view.layoutClassic", _ => SetLayoutAsync(Layouts.Classic));
        _router.RegisterUiHandler("view.layoutRight", _ => SetLayoutAsync(Layouts.Right));
        _router.RegisterUiHandler("view.layoutRail", _ => SetLayoutAsync(Layouts.Rail));
        _router.RegisterUiHandler("view.cycleLayout", _ => SetLayoutAsync(Layouts.Next(_layoutWanted ?? _settings.Layout)));
        _router.RegisterUiHandler("view.toggleHiddenFiles", _ => ToggleSettingAsync("panes.showHidden", !_settings.ShowHidden,
            on => on ? "Hidden files are shown." : "Hidden files are not shown.", "Hidden files"));
        _router.RegisterUiHandler("sidebar.toggleFollow", _ => ToggleSettingAsync("ui.sidebarAutoReveal", !_settings.SidebarAutoReveal,
            on => on ? "The Explorer follows the active pane." : "The Explorer stays where it is.", "The Explorer view"));
        _router.RegisterUiHandler("menu.toggleShellMenu", _ => ToggleSettingAsync("contextMenu.shellMenu", !_menuConfig.ShellMenu,
            on => on ? "Shift+right-click shows Windows' own menu." : "Shift+right-click shows the plain menu.", "Windows' own menu"));
        RegisterPreferenceCommands();
    }

    // view.layout*, view.cycleLayout: the layout is written with set_value; the window follows config_changed as for a hand edit.
    private async Task SetLayoutAsync(string layout)
    {
        if (Layouts.Normalize(_layoutWanted ?? _settings.Layout) == layout)
        {
            ShowNotice($"{Layouts.TitleOf(layout)} is the layout already.");
            return;
        }
        _layoutWanted = layout;
        if (await _settingsWriter.SetAsync("ui.layout", layout))
        {
            Diag.Info(SettingsTarget, "layout chosen", new LogField("layout", layout));
            ShowNotice($"Layout: {Layouts.TitleOf(layout)}.");
            return;
        }
        _layoutWanted = null;
        ShowNotice("The layout could not be changed: the core did not take the setting.", isError: true);
    }

    // A toggle of the same kind: the key is flipped through set_value and the window follows config_changed.
    private async Task ToggleSettingAsync(string key, bool value, Func<bool, string> said, string name)
    {
        if (await _settingsWriter.SetAsync(key, value))
        {
            Diag.Info(SettingsTarget, "setting toggled", new LogField("key", key), new LogField("value", value));
            ShowNotice(said(value));
            return;
        }
        ShowNotice($"{name} could not be switched: the core did not take the setting.", isError: true);
    }

    // The snapshot aid's until:setting:<name>=<value>: whether the window holds that value of the setting now.
    private bool SettingIs(string nameAndValue)
    {
        var parts = nameAndValue.Split('=', 2);
        if (parts.Length != 2)
        {
            return true;
        }
        var on = parts[1] == "on";
        return parts[0] switch
        {
            "layout" => Layouts.Normalize(_settings.Layout) == parts[1],
            "hidden" => _settings.ShowHidden == on,
            "follow" => _settings.SidebarAutoReveal == on,
            "shell-menu" => _menuConfig.ShellMenu == on,
            _ => PreferenceIs(parts[0], parts[1]),
        };
    }

    // The status bar says "hidden" while hidden files are listed, and a click on it turns them off.
    private void UpdateHiddenPill()
    {
        HiddenPill.Visibility = _settings.ShowHidden ? Visibility.Visible : Visibility.Collapsed;
    }

    // The hamburger's preference rows (after Toggle Sidebar): Layout with its three, and the two toggles with their checks,
    // then the settings of gaps 6 and 7 (ShellMenu.MoreSettings): Update Settings, Log Level and the editor.
    private IEnumerable<MenuEntry> PreferenceEntries() =>
        ShellMenu.Preferences(_router.Commands, _settings.Layout, _settings.ShowHidden, _settings.SidebarAutoReveal)
            .Concat(ShellMenu.MoreSettings(_router.Commands, _settings, _editorPrograms)).Select(row =>
            row.Choices is { } choices
                ? new MenuEntry(MenuEntryKind.Item, row.Title, "", Keys: row.Keys,
                    Children: [.. choices.Select(choice => new MenuEntry(MenuEntryKind.Item, choice.Title, null, choice.CommandId, Args: choice.Args, Checked: choice.Checked))])
                : new MenuEntry(MenuEntryKind.Item, row.Title, null, row.CommandId, Args: row.Args, Keys: row.Keys, Checked: row.Checked));
}
