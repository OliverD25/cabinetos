namespace CabinetOS.Core.Prompts;

/// <summary>What a prompt in the palette's frame asks for.</summary>
public enum PromptKind
{
    /// <summary>Text, such as a pattern: its rows are earlier answers that go into the box.</summary>
    Text,

    /// <summary>One of its rows, such as a pinned folder: typing narrows them.</summary>
    Pick,
}

/// <summary>
/// One row of a prompt: what it says, a second text in the tertiary colour
/// (a folder's path), its Segoe Fluent Icons glyph, and a row that stays
/// shown and last whatever is typed (<paramref name="Sticky"/>, "Pin this folder").
/// </summary>
public sealed record PromptRow(string Title, string Detail = "", string Glyph = "", bool Sticky = false);

/// <summary>
/// The rows of a prompt and the highlighted one: the part of the palette's
/// frame the window's prompts share (the pattern box, the pinned folders),
/// without the view, so it is tested on its own.
/// </summary>
public sealed class PromptList
{
    private readonly PromptKind _kind;
    private readonly IReadOnlyList<PromptRow> _rows;

    /// <summary>A list of <paramref name="rows"/> for a prompt of <paramref name="kind"/>.</summary>
    public PromptList(PromptKind kind, IReadOnlyList<PromptRow> rows)
    {
        _kind = kind;
        _rows = rows;
        Shown = rows;
        Highlight = kind == PromptKind.Pick && rows.Count > 0 ? 0 : -1;
    }

    /// <summary>The rows shown now: in a pick list the ones the text finds, the sticky ones last.</summary>
    public IReadOnlyList<PromptRow> Shown { get; private set; }

    /// <summary>
    /// The highlighted row's index in <see cref="Shown"/>, or -1. A pick list
    /// starts on its first row; a text list on none, so Enter takes the text.
    /// </summary>
    public int Highlight { get; private set; }

    /// <summary>The highlighted row, or null.</summary>
    public PromptRow? Highlighted => Highlight >= 0 && Highlight < Shown.Count ? Shown[Highlight] : null;

    /// <summary>The text changed: a pick list shows what it finds, case ignored, in titles and details.</summary>
    public void SetText(string text)
    {
        if (_kind != PromptKind.Pick)
        {
            return;
        }
        var wanted = text.Trim();
        Shown = wanted.Length == 0
            ? _rows
            : [.. _rows.Where(r => !r.Sticky && (r.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                    || r.Detail.Contains(wanted, StringComparison.OrdinalIgnoreCase))),
               .. _rows.Where(r => r.Sticky)];
        Highlight = Shown.Count > 0 ? 0 : -1;
    }

    /// <summary>
    /// Moves the highlight by <paramref name="delta"/> rows, within the list.
    /// Returns the text a text list puts in the box (the row's title), else null.
    /// </summary>
    public string? Move(int delta)
    {
        if (Shown.Count == 0)
        {
            return null;
        }
        Highlight = Math.Clamp((Highlight < 0 && delta > 0 ? -1 : Highlight) + delta, 0, Shown.Count - 1);
        return _kind == PromptKind.Text ? Shown[Highlight].Title : null;
    }

    /// <summary>Puts the highlight on row <paramref name="index"/> of <see cref="Shown"/> (the pointer).</summary>
    public void SetHighlight(int index)
    {
        if (index >= 0 && index < Shown.Count)
        {
            Highlight = index;
        }
    }
}
