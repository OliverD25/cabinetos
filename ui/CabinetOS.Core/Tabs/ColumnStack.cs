namespace CabinetOS.Core.Tabs;

/// <summary>A column the column view let go: where it was (0 is the first column), its folder, and what it held.</summary>
public sealed record DroppedColumn<T>(int Column, string Folder, T? Item)
    where T : class;

/// <summary>
/// The columns of a pane's column view (ADR 0016; docs/ui.md, "The column
/// view"), without a window or a listing: the folders from the first column
/// to the deepest, what each column holds (its listing), and the column the
/// keyboard is in. Opening a folder from the keyboard column drops the
/// columns to its right and adds one; Left and Right move the keyboard and
/// drop nothing. What a change drops comes back to the caller, deepest
/// first, so it can release it. The deepest column's folder is the tab's path.
/// </summary>
public sealed class ColumnStack<T>
    where T : class
{
    private readonly List<(string Folder, T? Item)> _columns;

    /// <summary>The column view of <paramref name="folder"/>: one column, with the keyboard.</summary>
    public ColumnStack(string folder, T? item = null) => _columns = [(folder, item)];

    /// <summary>How many columns are shown.</summary>
    public int Count => _columns.Count;

    /// <summary>The column the keyboard is in, from 0.</summary>
    public int Keyboard { get; private set; }

    /// <summary>The last column's folder: the tab's path, the breadcrumb row's and the saved tab's.</summary>
    public string Deepest => _columns[^1].Folder;

    /// <summary>The folder of the column the keyboard is in: the commands act on its rows.</summary>
    public string KeyboardFolder => _columns[Keyboard].Folder;

    /// <summary>The folders, from the first column to the deepest.</summary>
    public IReadOnlyList<string> Folders => [.. _columns.Select(c => c.Folder)];

    /// <summary>Column <paramref name="column"/>'s folder.</summary>
    public string FolderAt(int column) => _columns[column].Folder;

    /// <summary>What column <paramref name="column"/> holds, or null.</summary>
    public T? ItemAt(int column) => _columns[column].Item;

    /// <summary>Puts <paramref name="item"/> in column <paramref name="column"/>; returns what it held.</summary>
    public T? SetItem(int column, T? item)
    {
        var old = _columns[column].Item;
        _columns[column] = (_columns[column].Folder, item);
        return old;
    }

    /// <summary>The first column that shows <paramref name="folder"/>, or -1.</summary>
    public int IndexOf(string folder) => _columns.FindIndex(c => TabRules.SameFolder(c.Folder, folder));

    /// <summary>
    /// Opens <paramref name="folder"/> from a row of the keyboard column: the
    /// columns to its right go, <paramref name="folder"/> becomes the column
    /// right of it, and the keyboard moves there. Returns what went.
    /// </summary>
    public IReadOnlyList<DroppedColumn<T>> Open(string folder, T? item = null)
    {
        var dropped = DropAfter(Keyboard);
        _columns.Add((folder, item));
        Keyboard = _columns.Count - 1;
        return dropped;
    }

    /// <summary>Left: the keyboard goes to the parent column; the columns stay. False in the first column.</summary>
    public bool MoveLeft() => MoveTo(Keyboard - 1);

    /// <summary>Right: the keyboard goes to the column on the right, if one is shown; the columns stay.</summary>
    public bool MoveRight() => MoveTo(Keyboard + 1);

    /// <summary>The keyboard goes to column <paramref name="column"/> (a click on one of its rows). False when it is there or there is no such column.</summary>
    public bool MoveTo(int column)
    {
        if ((uint)column >= (uint)_columns.Count || column == Keyboard)
        {
            return false;
        }
        Keyboard = column;
        return true;
    }

    /// <summary>
    /// The parent of the first column becomes the first column (Backspace in
    /// the first column lists the parent, as in the list); the keyboard goes
    /// there and the other columns stay.
    /// </summary>
    public void Prepend(string folder, T? item = null)
    {
        _columns.Insert(0, (folder, item));
        Keyboard = 0;
    }

    /// <summary>
    /// Drops the columns right of <paramref name="column"/>; a keyboard that
    /// was in one of them goes to <paramref name="column"/>. Returns what
    /// went, deepest first.
    /// </summary>
    public IReadOnlyList<DroppedColumn<T>> DropAfter(int column)
    {
        column = Math.Clamp(column, 0, _columns.Count - 1);
        var dropped = new List<DroppedColumn<T>>();
        for (var i = _columns.Count - 1; i > column; i--)
        {
            dropped.Add(new DroppedColumn<T>(i, _columns[i].Folder, _columns[i].Item));
        }
        _columns.RemoveRange(column + 1, _columns.Count - column - 1);
        Keyboard = Math.Min(Keyboard, column);
        return dropped;
    }

    /// <summary>
    /// Starts over with <paramref name="folder"/> as the only column, with the
    /// keyboard (the pane went to a folder by another way: a crumb, Back, the
    /// sidebar). Returns every column that went, deepest first.
    /// </summary>
    public IReadOnlyList<DroppedColumn<T>> Reset(string folder, T? item = null)
    {
        var dropped = new List<DroppedColumn<T>>();
        for (var i = _columns.Count - 1; i >= 0; i--)
        {
            dropped.Add(new DroppedColumn<T>(i, _columns[i].Folder, _columns[i].Item));
        }
        _columns.Clear();
        _columns.Add((folder, item));
        Keyboard = 0;
        return dropped;
    }
}
