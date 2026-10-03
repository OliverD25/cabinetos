using System.Text.Json;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Prompts;
using CabinetOS.Core.Settings;
using CabinetOS.Core.Updates;
using CabinetOS.Services;

namespace CabinetOS;

// Gaps 5 to 7 of the settings-three-ways audit (the creator's rule of 2026-10-03): the terminal's defaults, the update settings,
// the editor and the log level. As for the first four settings (MainWindow.ThreeWays.cs), each is a command in the palette, a
// control in the window and a key of cabinetos.json; the command and the control write the key through set_value and the window
// follows the key the core announces (config_changed), so a change made any of the three ways is the same change.
// A command that chooses one of several values opens a pick list (the prompt in the palette's frame) and takes {"value": ...}
// as its argument to skip the list, which is how the menus' rows run it.
public sealed partial class MainWindow
{
    private void RegisterPreferenceCommands()
    {
        _router.RegisterUiHandler("terminal.chooseDefaultProfile", ChooseDefaultProfileAsync);
        _router.RegisterUiHandler("terminal.toggleRestore", _ => ToggleSettingAsync("terminal.restore", !_settings.TerminalRestore,
            on => on ? "Terminal tabs come back after a restart." : "Terminal tabs do not come back after a restart.", "Restoring terminal tabs"));
        _router.RegisterUiHandler("terminal.toggleDefaultMode", _ => ChooseSettingAsync("terminal.defaultMode",
            _settings.TerminalStartsLinked ? "locked" : "linked",
            mode => mode == "linked" ? "New terminals start linked to their pane." : "New terminals start locked.", "The terminal's starting mode"));
        RegisterUpdateSettingsCommands();
        RegisterEditorCommands();
    }

    // A text setting is written through set_value, as a toggle is; the core's own words say why it did not take it
    // (terminal.defaultProfile names a profile that must exist).
    private async Task ChooseSettingAsync(string key, string value, Func<string, string> said, string name)
    {
        var refusal = await _settingsWriter.SetOrRefusalAsync(key, JsonSerializer.SerializeToElement(value));
        if (refusal is null)
        {
            Diag.Info(SettingsTarget, "setting chosen", new LogField("key", key), new LogField("value", value));
            ShowNotice(said(value));
            return;
        }
        ShowNotice($"{name} could not be changed: {refusal}.", isError: true);
    }

    // The pick list in the palette's frame: the value in effect starts highlighted; null when the list is cancelled.
    private async Task<PromptRow?> PickRowAsync(string label, IReadOnlyList<PromptRow> rows, int current, string hint)
    {
        var answer = PromptView.ShowAsync(new PromptRequest(label, PromptKind.Pick, rows, Placeholder: "Type to narrow the list", Hint: hint));
        if (current > 0)
        {
            PromptView.Highlight(current);
        }
        return (await answer)?.Row;
    }

    // terminal.defaultProfile: "Terminal: Default Profile" and the chevron menu's "Default Profile…". The profiles are the
    // names of terminal.profiles; a new terminal that names none starts with the one chosen.
    private async Task ChooseDefaultProfileAsync(CommandInvocation invocation)
    {
        var profiles = _terminal.Profiles;
        var chosen = CommandArgs.Text(invocation.Args, "value");
        if (chosen is null)
        {
            var rows = profiles.Names.Select(name => new PromptRow(name, name == profiles.DefaultProfile ? "default" : "")).ToList();
            var row = await PickRowAsync("Default shell", rows, rows.FindIndex(r => r.Detail.Length > 0),
                "Enter makes it the shell a new terminal starts with.");
            if (row is null)
            {
                return;
            }
            chosen = row.Title;
        }
        if (chosen == profiles.DefaultProfile)
        {
            ShowNotice($"{chosen} is the default shell already.");
            return;
        }
        await ChooseSettingAsync("terminal.defaultProfile", chosen, name => $"New terminals start with {name}.", "The default shell");
    }

    // The snapshot aid's until:setting:<name>=<value> for these settings (SettingIs hands the names it does not know on).
    private bool PreferenceIs(string name, string value)
    {
        var on = value == "on";
        return name switch
        {
            "default-profile" => _settings.TerminalDefaultProfile == value,
            "restore" => _settings.TerminalRestore == on,
            "default-mode" => _settings.TerminalStartsLinked == (value == "linked"),
            "update-check" => _settings.UpdateCheck == on,
            "auto-install" => _settings.UpdateAutoInstall == on,
            "channel" => UpdateSettingsMenu.NormalizeChannel(_settings.UpdateChannel) == value,
            // editor=default: files.editor is null; editor=<command>: that program; editor-label=<text>: what the palette's row says.
            "editor" => value == "default" ? _settings.Editor is null : _settings.Editor?.Command == value,
            "editor-label" => _settings.EditorLabel == value,
            "log-level" => LogLevels.Normalize(_settings.LogLevel) == value,
            _ => true,
        };
    }

    // settings-do:<what>|<argument>: a press the snapshot aid makes where a click cannot reach.
    // update-flyout|<row title>: the update pill's flyout, which is shown only while an update runs.
    // editor-file|<path>: the answer of the next file dialog of the editor's "Choose…", which a test cannot drive.
    private void RunSettingsStep(string argument)
    {
        var parts = argument.Split('|', 2);
        var done = parts is [var what, var value] && what switch
        {
            "update-flyout" => PressUpdateFlyoutRow(value),
            "editor-file" => SetEditorFileAnswer(value),
            _ => false,
        };
        if (!done)
        {
            Diag.Info("cabinetos_ui::snapshot", "no such settings step", new LogField("step", argument));
        }
    }

    private bool SetEditorFileAnswer(string path)
    {
        _editorFileAnswer = path;
        return true;
    }

    // settings-state:<label>: what the controls of these settings show, in the log ("settings state"), for the checks that read it.
    private void LogSettingsState(string label)
    {
        Diag.Info("cabinetos_ui::snapshot", "settings state",
            new LogField("label", label),
            new LogField("dock_menu", Dock.DescribeProfilesMenu()),
            new LogField("default_profile", _settings.TerminalDefaultProfile),
            new LogField("restore", _settings.TerminalRestore),
            new LogField("default_mode", _settings.TerminalStartsLinked ? "linked" : "locked"),
            new LogField("update_check", _settings.UpdateCheck),
            new LogField("update_auto_install", _settings.UpdateAutoInstall),
            new LogField("update_channel", _settings.UpdateChannel),
            new LogField("update_flyout", _updateFlyoutText),
            new LogField("editor_label", _settings.EditorLabel),
            new LogField("editor_command", _settings.Editor?.Command ?? ""),
            new LogField("editor_args", _settings.Editor?.ArgsText.Replace('\n', ' ') ?? ""),
            new LogField("editor_choices", EditorChoices.Describe(EditorChoices.Choices(_settings.Editor, _editorPrograms))),
            new LogField("log_level", LogLevels.Normalize(_settings.LogLevel)),
            new LogField("menu", FileMenu.Describe()),
            new LogField("palette_states", string.Join("|", _palette.Rows.Select(r => r.StateText is { } state ? $"{r.Info.Id}={state}" : "").Where(s => s.Length > 0))),
            new LogField("prompt_open", PromptView.IsOpen),
            new LogField("prompt_rows", PromptView.IsOpen ? PromptView.DescribeRows() : ""));
    }
}
