using System.Runtime.InteropServices;
using System.Text.Json;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Prompts;
using CabinetOS.Core.Settings;
using CabinetOS.Services;

namespace CabinetOS;

// The editor and the log level, the last two settings that were reachable from the file only (the settings-three-ways skill,
// gap 7): files.editor and logging.level. Each is a command in the palette, a submenu of the top row's menu and a key of
// cabinetos.json. The editor's choices are Windows' default, the user's programs (the list the palette and the context menu
// start programs from) and a program picked from disk; the core finds and starts the editor (edit_path), the window only
// writes the key.
public sealed partial class MainWindow
{
    // The programs entry of the configuration the core last sent: the editor's choices besides Windows' default.
    private IReadOnlyList<EditorProgram> _editorPrograms = [];

    // The snapshot aid's answer to the file dialog of "Choose…" (settings-do:editor-file|<path>): a dialog cannot be driven by a test.
    private string? _editorFileAnswer;

    private void ApplyPreferenceConfig(JsonElement config) => _editorPrograms = EditorChoices.ProgramsFrom(config);

    private void RegisterEditorCommands()
    {
        _router.RegisterUiHandler("preferences.chooseEditor", ChooseEditorAsync);
        _router.RegisterUiHandler("diagnostics.chooseLogLevel", ChooseLogLevelAsync);
    }

    // preferences.chooseEditor: "Preferences: Choose Editor" lists the choices; the menu's rows give one of {"default": true},
    // {"program": name}, {"choose": true} (the file dialog) or {"keep": true} (the editor set by hand, which stays) and skip the list.
    private async Task ChooseEditorAsync(CommandInvocation invocation)
    {
        var args = invocation.Args;
        if (CommandArgs.Bool(args, "default"))
        {
            await WriteEditorAsync(null, "F4 opens files with Windows' own editor for the file's type, else Notepad.");
            return;
        }
        if (CommandArgs.Bool(args, "keep"))
        {
            ShowNotice($"{_settings.EditorLabel} is the editor already.");
            return;
        }
        if (CommandArgs.Bool(args, "choose"))
        {
            await ChooseEditorFileAsync();
            return;
        }
        if (CommandArgs.Text(args, "program") is { } name)
        {
            if (_editorPrograms.FirstOrDefault(p => p.Name == name) is not { } program)
            {
                ShowNotice($"No program named {name} is in the programs list of cabinetos.json.", isError: true);
                return;
            }
            await WriteEditorAsync(EditorSetting.Of(program.Command, program.EditorArgs), $"F4 opens files with {program.Title}.");
            return;
        }
        var choices = EditorChoices.Choices(_settings.Editor, _editorPrograms);
        var rows = choices.Select(choice => new PromptRow(choice.Title, (choice.Checked ? "current · " : "") + choice.Detail,
            Sticky: choice.Kind == EditorChoiceKind.Choose)).ToList();
        var row = await PickRowAsync("Editor", rows, choices.ToList().FindIndex(choice => choice.Checked),
            "Enter makes it the program F4 opens a file with.");
        if (row is null || rows.FindIndex(r => ReferenceEquals(r, row)) is not (>= 0 and var index))
        {
            return;
        }
        await ChooseEditorAsync(new CommandInvocation(invocation.CommandId, choices[index].Args, invocation.RequestId, invocation.Trigger));
    }

    // "Choose…": a file dialog for a program (.exe); the program becomes the editor, with no arguments of its own.
    private async Task ChooseEditorFileAsync()
    {
        string? path;
        if (_editorFileAnswer is { } answer)
        {
            _editorFileAnswer = null;
            path = answer.Length > 0 ? answer : null;
        }
        else
        {
            path = await PickProgramFileAsync();
        }
        if (path is not null)
        {
            await WriteEditorAsync(EditorSetting.Of(path, []), $"F4 opens files with {Path.GetFileName(path)}.");
        }
    }

    private async Task<string?> PickProgramFileAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
        {
            Diag.Warn(SettingsTarget, "the file dialog could not open", new LogField("error", error.Message));
            ShowNotice("The file dialog could not open. Set files.editor in cabinetos.json instead.", isError: true);
            return null;
        }
        finally
        {
            FocusActivePane();
        }
    }

    // files.editor is written whole through set_value: the program and its arguments, or null for Windows' own edit verb.
    private async Task WriteEditorAsync(EditorSetting? editor, string said)
    {
        var value = editor is null
            ? JsonSerializer.SerializeToElement<object?>(null)
            : JsonSerializer.SerializeToElement(new { command = editor.Command, args = editor.Args });
        var refusal = await _settingsWriter.SetOrRefusalAsync("files.editor", value);
        if (refusal is null)
        {
            Diag.Info(SettingsTarget, "editor chosen", new LogField("command", editor?.Command ?? ""));
            ShowNotice(said);
            return;
        }
        ShowNotice($"The editor could not be changed: {refusal}.", isError: true);
    }

    // logging.level: "Diagnostics: Log Level" and the menu's level rows (which give {"value": level} and skip the list).
    private async Task ChooseLogLevelAsync(CommandInvocation invocation)
    {
        var current = LogLevels.Normalize(_settings.LogLevel);
        var chosen = CommandArgs.Text(invocation.Args, "value");
        if (chosen is null)
        {
            var rows = LogLevels.All.Select(level => new PromptRow(level, LogLevels.DetailOf(level))).ToList();
            var row = await PickRowAsync("Log level", rows, rows.FindIndex(r => r.Title == current),
                "Enter sets the least important level the core writes to its log.");
            if (row is null)
            {
                return;
            }
            chosen = row.Title;
        }
        if (chosen == current)
        {
            ShowNotice($"The log level is {chosen} already.");
            return;
        }
        await ChooseSettingAsync("logging.level", chosen, level => $"The core logs from {level} up.", "The log level");
    }
}
