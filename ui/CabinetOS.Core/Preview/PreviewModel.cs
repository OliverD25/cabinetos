using CabinetOS.Core.Listing;

namespace CabinetOS.Core.Preview;

/// <summary>
/// One row of a preview as the pane shows it (docs/ui.md, "The preview pane"):
/// what applying it does, the item, and where it goes. The item keeps its
/// folder apart from its name, since the rows of one preview may come from
/// several folders.
/// </summary>
public sealed record PreviewLine(PreviewChange Change, string Folder, string Name, string Target)
{
    /// <summary>The verb in front of the row: "Rename", "Move", "Copy", "Delete", "Create".</summary>
    public string Verb => Change switch
    {
        PreviewChange.Rename => "Rename",
        PreviewChange.Move => "Move",
        PreviewChange.Copy => "Copy",
        PreviewChange.Delete => "Delete",
        PreviewChange.Create => "Create",
        _ => "Change",
    };

    /// <summary>Whether the row puts something into the Recycle Bin: the pane tints it red.</summary>
    public bool IsDelete => Change == PreviewChange.Delete;

    /// <summary>Whether the row has a target to show in the accent colour.</summary>
    public bool HasTarget => Target.Length > 0;

    /// <summary>The row as one line of text, for the screen reader and the log.</summary>
    public string Description => HasTarget ? $"{Verb} {Folder}{Name} to {Target}" : $"{Verb} {Folder}{Name}";

    /// <summary>
    /// The line for row <paramref name="index"/> of a preview listing. A
    /// rename's target shows as the new name only, since its row already
    /// shows the folder; a move or a copy shows the folder it goes into.
    /// </summary>
    public static PreviewLine From(ListingView view, int index)
    {
        var path = view.Name(index);
        var (folder, name) = Split(path);
        var change = view.Change(index);
        var target = view.Target(index);
        if (change == PreviewChange.Rename)
        {
            target = Split(target).Name;
        }
        else if (change is PreviewChange.Delete or PreviewChange.Create)
        {
            target = "";
        }
        return new PreviewLine(change, folder, name, target);
    }

    /// <summary>
    /// Splits a path into its folder (with the closing backslash) and its
    /// name; a path that ends with a backslash (a folder to create) keeps the
    /// backslash on its name.
    /// </summary>
    public static (string Folder, string Name) Split(string path)
    {
        var body = path.TrimEnd('\\');
        var cut = body.LastIndexOf('\\');
        if (cut < 0)
        {
            return ("", path);
        }
        return (path[..(cut + 1)], path[(cut + 1)..]);
    }
}

/// <summary>A preview the core keeps for the window: its ID, its title and its rows.</summary>
public sealed class PreviewSession(string id, string title, IReadOnlyList<PreviewLine> lines)
{
    /// <summary>The text of the bar's hint (Article 7: the keys are shown where they act).</summary>
    public const string Hint = "Enter applies · Esc cancels";

    /// <summary>The preview's ID, as <c>preview_apply</c> and <c>preview_cancel</c> name it.</summary>
    public string Id { get; } = id;

    /// <summary>What the preview is about.</summary>
    public string Title { get; } = title;

    /// <summary>The rows, in the order applying them runs.</summary>
    public IReadOnlyList<PreviewLine> Lines { get; } = lines;

    /// <summary>"3 changes" or "1 change".</summary>
    public string Count => Lines.Count == 1 ? "1 change" : $"{Lines.Count:N0} changes";

    /// <summary>Reads every row of <paramref name="view"/>, a preview listing.</summary>
    public static PreviewSession From(string id, string title, ListingView view)
    {
        var lines = new List<PreviewLine>(view.Count);
        for (var i = 0; i < view.Count; i++)
        {
            lines.Add(PreviewLine.From(view, i));
        }
        return new PreviewSession(id, title, lines);
    }
}

/// <summary>What a key does while a preview is shown.</summary>
public enum PreviewKeyAction
{
    /// <summary>The key is not the preview's: the keymap and the pane see it as always.</summary>
    None,

    /// <summary>Enter: <c>preview_apply</c>.</summary>
    Apply,

    /// <summary>Esc: <c>preview_cancel</c>.</summary>
    Cancel,
}

/// <summary>
/// The preview's two keys. The pane handles them before the keymap, so Enter
/// does not open the row under the other pane's cursor and Esc does not close
/// something else while changes wait for an answer.
/// </summary>
public static class PreviewKeys
{
    /// <summary>The action for a key named as the keymap names it (<c>enter</c>, <c>escape</c>, <c>ctrl+enter</c>).</summary>
    public static PreviewKeyAction For(string combo) => combo switch
    {
        "enter" => PreviewKeyAction.Apply,
        "escape" => PreviewKeyAction.Cancel,
        _ => PreviewKeyAction.None,
    };
}
