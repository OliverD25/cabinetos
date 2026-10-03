using System.Text.Json;

namespace CabinetOS.Core.Settings;

/// <summary>
/// The levels of <c>logging.level</c> the core accepts (docs/config.md), most detailed first, and what a pick list or a menu
/// says of each (the settings-three-ways skill, gap 7). The window writes the level with <c>set_value</c>; the core's own
/// words are the answer when it does not take one.
/// </summary>
public static class LogLevels
{
    /// <summary>The level in effect when the file says none.</summary>
    public const string Default = "info";

    /// <summary>The levels, in the order the pick list and the menu list them.</summary>
    public static IReadOnlyList<string> All { get; } = ["trace", "debug", "info", "warn", "error"];

    /// <summary>The level the window shows for a value of <c>logging.level</c>: one the core does not know shows <c>info</c>.</summary>
    public static string Normalize(string? level) => level is not null && All.Contains(level) ? level : Default;

    /// <summary>What a menu row calls <paramref name="level"/>.</summary>
    public static string TitleOf(string level) => Normalize(level) switch
    {
        "trace" => "Trace",
        "debug" => "Debug",
        "warn" => "Warn",
        "error" => "Error",
        _ => "Info",
    };

    /// <summary>The second text of the pick list's row: what the level writes (the core's own words for it).</summary>
    public static string DetailOf(string level) => Normalize(level) switch
    {
        "trace" => "Everything",
        "debug" => "Details for developers",
        "warn" => "Problems the core recovered from",
        "error" => "Failures",
        _ => "Normal operation",
    };
}

/// <summary>
/// A program of the user's <c>programs</c> list (docs/config.md, "Programs"): the command <c>program.&lt;name&gt;</c>. The picker
/// of <c>files.editor</c> offers each one, since a program that opens a file is the editor most users want.
/// </summary>
/// <param name="Name">The key of the entry; the command is <c>program.&lt;name&gt;</c>.</param>
/// <param name="Title">What the menu and the palette show; the name when the entry has none.</param>
/// <param name="Command">A full path, or a program name found on the <c>PATH</c>.</param>
/// <param name="Args">The entry's arguments, with <c>{path}</c>, <c>{selection}</c> and <c>{cwd}</c> as written.</param>
public sealed record EditorProgram(string Name, string Title, string Command, IReadOnlyList<string> Args)
{
    /// <summary>
    /// The arguments <c>files.editor</c> gets: those of the entry without a token. <c>files.editor</c> adds the file's path as the
    /// last argument itself, so <c>{selection}</c> and its kind would be wrong there; <c>--wait</c> stays.
    /// </summary>
    public IReadOnlyList<string> EditorArgs => [.. Args.Where(arg => !EditorChoices.HasToken(arg))];
}

/// <summary>
/// <c>files.editor</c> as the window holds it: the program and its arguments, or none for Windows' own edit verb. The arguments
/// are one text, joined by a line break, so two readings of the same file are equal (a record compares lists by reference).
/// </summary>
public sealed record EditorSetting(string Command, string ArgsText)
{
    /// <summary>The arguments.</summary>
    public IReadOnlyList<string> Args => ArgsText.Length == 0 ? [] : ArgsText.Split('\n');

    /// <summary>The setting of a program and its arguments.</summary>
    public static EditorSetting Of(string command, IEnumerable<string> args) => new(command, string.Join('\n', args));
}

/// <summary>
/// The editor's choices (the settings-three-ways skill, gap 7): "Windows' default", each program of <c>programs</c>, and a
/// program picked from disk. Apart from the window so it is tested on its own.
/// </summary>
public static class EditorChoices
{
    /// <summary>The label of <c>files.editor</c> being <c>null</c>.</summary>
    public const string WindowsDefault = "Windows' default";

    /// <summary>The pick list's last row, which opens a file dialog for a program.</summary>
    public const string ChooseTitle = "Choose…";

    /// <summary>The second text of the "Windows' default" row.</summary>
    public const string WindowsDefaultDetail = "The edit verb of the file's type, else Notepad";

    /// <summary>The second text of the "Choose…" row.</summary>
    public const string ChooseDetail = "Pick a program (.exe) on this PC";

    /// <summary>Whether an argument of a program has a token the core fills in (<c>{path}</c>, <c>{selection}</c>, <c>{cwd}</c>).</summary>
    public static bool HasToken(string arg) =>
        arg.Contains("{path}", StringComparison.Ordinal) || arg.Contains("{selection}", StringComparison.Ordinal) || arg.Contains("{cwd}", StringComparison.Ordinal);

    /// <summary><c>files.editor</c> of the core's configuration; null when it is null, missing or has no command.</summary>
    public static EditorSetting? EditorFrom(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object
            || !files.TryGetProperty("editor", out var editor) || editor.ValueKind != JsonValueKind.Object
            || !editor.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String
            || command.GetString() is not { Length: > 0 } text)
        {
            return null;
        }
        var args = editor.TryGetProperty("args", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)
            : [];
        return EditorSetting.Of(text, args);
    }

    /// <summary>The <c>programs</c> of the core's configuration; an entry without a name or a command is left out.</summary>
    public static IReadOnlyList<EditorProgram> ProgramsFrom(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty("programs", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var programs = new List<EditorProgram>();
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() is not { Length: > 0 } nameText
                || !entry.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String || command.GetString() is not { Length: > 0 } commandText)
            {
                continue;
            }
            var title = entry.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String && titleValue.GetString() is { Length: > 0 } titleText
                ? titleText
                : nameText;
            var args = entry.TryGetProperty("args", out var argList) && argList.ValueKind == JsonValueKind.Array
                ? argList.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList()
                : [];
            programs.Add(new EditorProgram(nameText, title, commandText, args));
        }
        return programs;
    }

    /// <summary>The program whose command and arguments are the editor in effect, if the editor is one of them.</summary>
    public static EditorProgram? ProgramOf(EditorSetting? editor, IEnumerable<EditorProgram> programs) =>
        editor is null
            ? null
            : programs.FirstOrDefault(program => string.Equals(program.Command, editor.Command, StringComparison.OrdinalIgnoreCase)
                && program.EditorArgs.SequenceEqual(editor.Args));

    /// <summary>
    /// What the palette's row and the menu call the editor in effect: "Windows' default", the title of the program of
    /// <c>programs</c> it is, or the program's file name without its extension ("notepad++").
    /// </summary>
    public static string Label(EditorSetting? editor, IEnumerable<EditorProgram> programs)
    {
        if (editor is null)
        {
            return WindowsDefault;
        }
        if (ProgramOf(editor, programs) is { } program)
        {
            return program.Title;
        }
        var name = Path.GetFileNameWithoutExtension(editor.Command.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : editor.Command;
    }

    /// <summary>The label of <c>files.editor</c> in <paramref name="config"/> (the core's configuration).</summary>
    public static string LabelFrom(JsonElement config) => Label(EditorFrom(config), ProgramsFrom(config));

    /// <summary>
    /// The choices of the pick list and of the menu, in order: "Windows' default", the editor in effect when it is none of the
    /// programs (set by hand in the file), each program of <c>programs</c>, and "Choose…". The one in effect is checked.
    /// </summary>
    public static IReadOnlyList<EditorChoice> Choices(EditorSetting? editor, IReadOnlyList<EditorProgram> programs)
    {
        var current = ProgramOf(editor, programs);
        var rows = new List<EditorChoice>
        {
            new(EditorChoiceKind.WindowsDefault, WindowsDefault, WindowsDefaultDetail, Args("default", true), editor is null),
        };
        if (editor is not null && current is null)
        {
            rows.Add(new EditorChoice(EditorChoiceKind.Other, Label(editor, programs), editor.Command, Args("keep", true), true));
        }
        rows.AddRange(programs.Select(program =>
            new EditorChoice(EditorChoiceKind.Program, program.Title, program.Command, Args("program", program.Name), ReferenceEquals(program, current))));
        rows.Add(new EditorChoice(EditorChoiceKind.Choose, ChooseTitle, ChooseDetail, Args("choose", true), false));
        return rows;
    }

    /// <summary>The rows as the window's log tells them: "|" between rows, " [x]" or " [ ]" after each title.</summary>
    public static string Describe(IEnumerable<EditorChoice> choices) =>
        string.Join("|", choices.Select(choice => choice.Title + (choice.Checked ? " [x]" : " [ ]")));

    private static JsonElement Args(string name, object value) => JsonSerializer.SerializeToElement(new Dictionary<string, object> { [name] = value });
}

/// <summary>What a choice of the editor does.</summary>
public enum EditorChoiceKind
{
    /// <summary>Writes <c>files.editor</c> as <c>null</c>.</summary>
    WindowsDefault,

    /// <summary>An editor set by hand in the file that is none of <c>programs</c>; choosing it changes nothing.</summary>
    Other,

    /// <summary>A program of <c>programs</c>: its command, and its arguments without the tokens, become the editor.</summary>
    Program,

    /// <summary>Opens a file dialog for a program on disk.</summary>
    Choose,
}

/// <summary>
/// One choice of the editor: the pick list's row and the menu's row. <see cref="Args"/> are what the command
/// <c>preferences.chooseEditor</c> is given for it (<c>{"default": true}</c>, <c>{"program": "code"}</c>,
/// <c>{"keep": true}</c>, <c>{"choose": true}</c>), which is how a menu row runs it without the list.
/// </summary>
public sealed record EditorChoice(EditorChoiceKind Kind, string Title, string Detail, JsonElement Args, bool Checked);
