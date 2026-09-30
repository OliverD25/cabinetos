using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Shell;

/// <summary>One file or folder Quick Open lists: its name, and the folder it is in as the row shows it (under the workspace).</summary>
public sealed record QuickOpenRow(string Path, string Name, string Folder, bool IsFolder);

/// <summary>
/// Quick Open (Ctrl+P; docs/ui.md, "Quick Open"; the creator's
/// SHELL_REDESIGN.md §3): the files and folders of the workspace whose names
/// match the text. The core finds and ranks them (<c>search</c>, from its
/// index or a walk); this model asks, drops answers that came too late, and
/// keeps the highlighted row. A <c>&gt;</c> typed first switches to the
/// commands (<see cref="PaletteInput"/>), which the window does.
/// </summary>
public sealed class QuickOpenModel(ICoreChannel core)
{
    /// <summary>At most this many rows are asked for.</summary>
    public const int Limit = 50;

    private const string Target = "cabinetos_ui::quick_open";

    private int _asked;

    /// <summary>Raised after the rows, the highlight or the state changed.</summary>
    public event Action? Changed;

    /// <summary>Whether Quick Open is shown.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The folder searched: the workspace (the repository that holds the active folder, else that folder).</summary>
    public string Root { get; private set; } = "";

    /// <summary>The text as typed.</summary>
    public string Query { get; private set; } = "";

    /// <summary>The rows, best first.</summary>
    public IReadOnlyList<QuickOpenRow> Rows { get; private set; } = [];

    /// <summary>The highlighted row's index, or -1.</summary>
    public int Highlight { get; private set; } = -1;

    /// <summary>Why the last search failed, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>The highlighted row, or null.</summary>
    public QuickOpenRow? Highlighted => Highlight >= 0 && Highlight < Rows.Count ? Rows[Highlight] : null;

    /// <summary>What the count beside the box says: "12 results", an error, or nothing while nothing is typed.</summary>
    public string CountText =>
        Error ?? (PaletteInput.FileQuery(Query) is null ? "" : Rows.Count == 1 ? "1 result" : $"{Rows.Count} results");

    /// <summary>Opens Quick Open on <paramref name="root"/> with nothing typed.</summary>
    public void Open(string root)
    {
        _asked++;
        IsOpen = true;
        Root = root;
        Query = "";
        Rows = [];
        Highlight = -1;
        Error = null;
        Changed?.Invoke();
    }

    /// <summary>Closes Quick Open; an answer on its way is dropped.</summary>
    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }
        _asked++;
        IsOpen = false;
        Rows = [];
        Highlight = -1;
        Changed?.Invoke();
    }

    /// <summary>
    /// Asks the core for the names that match <paramref name="text"/> under
    /// <see cref="Root"/> and shows them, the best one highlighted. Returns
    /// false when a newer text or a close made the answer useless.
    /// </summary>
    public async Task<bool> SearchAsync(string text, string? requestId = null)
    {
        Query = text;
        var asked = ++_asked;
        if (PaletteInput.FileQuery(text) is not { } query)
        {
            Rows = [];
            Highlight = -1;
            Error = null;
            Changed?.Invoke();
            return true;
        }
        CoreReply reply;
        try
        {
            reply = await core.RequestAsync(new SearchRequest(query) { Limit = Limit, Root = Root.Length > 0 ? Root : null, Id = requestId ?? "" });
        }
        catch (IOException error)
        {
            reply = new ErrorReply(ErrorCodes.Io, error.Message);
        }
        if (asked != _asked || !IsOpen)
        {
            return false;
        }
        switch (reply)
        {
            case FileSearchResultsReply results:
                Rows = [.. results.Hits.Select(hit => Row(hit, Root))];
                Error = null;
                Diag.Debug(Target, "quick open results", new LogField("rows", Rows.Count), new LogField("source", results.Source),
                    new LogField("took_us", results.TookUs));
                break;
            case ErrorReply error:
                Rows = [];
                Error = error.Message;
                break;
            default:
                Rows = [];
                Error = "The core gave an answer Quick Open does not know.";
                break;
        }
        Highlight = Rows.Count > 0 ? 0 : -1;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Moves the highlight by <paramref name="delta"/> rows, held to the rows there are.</summary>
    public void MoveHighlight(int delta)
    {
        if (Rows.Count == 0)
        {
            return;
        }
        SetHighlight(Math.Clamp(Highlight + delta, 0, Rows.Count - 1));
    }

    /// <summary>Puts the highlight on row <paramref name="index"/> (the pointer over a row).</summary>
    public void SetHighlight(int index)
    {
        if (index < 0 || index >= Rows.Count || index == Highlight)
        {
            return;
        }
        Highlight = index;
        Changed?.Invoke();
    }

    /// <summary>A hit as a row: its name, and its folder from the workspace's own name down.</summary>
    public static QuickOpenRow Row(FileHit hit, string? root)
    {
        var parent = DisplayFormat.Parent(hit.Path);
        return new QuickOpenRow(hit.Path, DisplayFormat.FolderName(hit.Path),
            parent is null ? "" : DisplayFormat.FolderUnder(parent, string.IsNullOrEmpty(root) ? null : root), hit.IsFolder);
    }
}
