namespace CabinetOS.Core.Listing;

/// <summary>
/// The marks a pane had before a file command or an unmark cleared them, by
/// name, for Restore Selection (Num /, as in Total Commander). Names, not
/// indexes: the listing a copy, a move or a refresh leaves behind is another
/// one, in which the same names may sit elsewhere or be gone.
/// </summary>
public sealed class MarkMemory
{
    /// <summary>The remembered names, in the order they were listed.</summary>
    public IReadOnlyList<string> Names { get; private set; } = [];

    /// <summary>Whether nothing was remembered yet.</summary>
    public bool IsEmpty => Names.Count == 0;

    /// <summary>
    /// Whether <paramref name="selection"/> holds marks worth remembering: any
    /// selected row, except, in the Windows style, the focused row selected
    /// alone (that style selects the row the keyboard is on; that is not a mark).
    /// </summary>
    public static bool HasMarks(SelectionModel selection) => selection.HasMarks;

    /// <summary>Remembers the marked names of <paramref name="view"/>, when there are marks; else keeps the last ones.</summary>
    public void Remember(ListingView view, SelectionModel selection)
    {
        if (HasMarks(selection))
        {
            Names = [.. selection.Selected.Select(view.Name)];
        }
    }

    /// <summary>
    /// Marks the remembered names that <paramref name="view"/> has, and only
    /// them. Returns how many it found.
    /// </summary>
    public int Restore(ListingView view, SelectionModel selection)
    {
        var found = view.IndexesOfNames(Names);
        selection.Restore(view.Count, found, selection.Focus, selection.Anchor);
        return found.Count;
    }
}
