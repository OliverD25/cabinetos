namespace CabinetOS.Core.Prompts;

/// <summary>
/// What a prompt in the palette's frame shows: a label before the box
/// ("Select", "Go to"), its kind, its rows, the text it starts with, a
/// placeholder, a check box when <paramref name="Option"/> names one, and a
/// hint line under the rows.
/// </summary>
public sealed record PromptRequest(
    string Label,
    PromptKind Kind,
    IReadOnlyList<PromptRow> Rows,
    string Text = "",
    string Placeholder = "",
    string? Option = null,
    bool OptionChecked = false,
    string Hint = "");

/// <summary>
/// What the user chose: the text in the box, the highlighted row of a pick
/// list (null for a text prompt), and the check box.
/// </summary>
public sealed record PromptResult(string Text, PromptRow? Row, bool OptionChecked);
