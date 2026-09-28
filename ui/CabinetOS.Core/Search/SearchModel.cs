using System.Globalization;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Terminal;

namespace CabinetOS.Core.Search;

/// <summary>Where a search is.</summary>
public enum SearchPhase
{
    /// <summary>Nothing typed: the pane shows its folder.</summary>
    Idle,

    /// <summary>Typed; waiting for the user to stop (150 ms) or for the core's answer.</summary>
    Searching,

    /// <summary>The core answered with hits.</summary>
    Done,

    /// <summary>The core refused, or could not be asked.</summary>
    Failed,
}

/// <summary>One search as it was sent: what was typed, and the folder it was limited to (null: every volume).</summary>
public sealed record SearchQuery(string Text, string? Root);

/// <summary>
/// The command bar's search (docs/ui.md, "Search"): what the user typed goes
/// to the core as <c>search</c> once 150 ms pass without another key, limited
/// to the active pane's folder unless "whole volume" is on. The core finds and
/// ranks; the model only keeps the latest answer and says what it means. An
/// answer to an older request, or one that comes after the search was left,
/// is dropped.
/// </summary>
public sealed class SearchModel(ICoreChannel core, Func<long> nowMilliseconds)
{
    /// <summary>At most this many hits are asked for.</summary>
    public const int Limit = 100;

    /// <summary>The wait after the last key.</summary>
    public const int DelayMilliseconds = 150;

    private const string Target = "cabinetos_ui::search";

    private readonly Debouncer<SearchQuery> _typed = new(nowMilliseconds, DelayMilliseconds);
    private int _sent;
    private string? _folder;

    /// <summary>The phase, the texts or the hits changed.</summary>
    public event Action? Changed;

    /// <summary>What the user typed, trimmed.</summary>
    public string Text { get; private set; } = "";

    /// <summary>Whether the search covers every volume instead of the pane's folder.</summary>
    public bool WholeVolume { get; private set; }

    /// <summary>The phase.</summary>
    public SearchPhase Phase { get; private set; }

    /// <summary>The query the shown answer is for.</summary>
    public SearchQuery? Shown { get; private set; }

    /// <summary>The shown answer.</summary>
    public FileSearchResultsReply? Results { get; private set; }

    /// <summary>Why the search failed.</summary>
    public string? Error { get; private set; }

    /// <summary>Whether the pane shows search results (something is typed).</summary>
    public bool IsActive => Phase != SearchPhase.Idle;

    /// <summary>
    /// The user typed <paramref name="text"/> in a pane showing <paramref name="folder"/>;
    /// the search goes out once <see cref="DelayMilliseconds"/> pass without another change.
    /// </summary>
    public void SetText(string text, string? folder)
    {
        _folder = folder;
        Text = text.Trim();
        if (Text.Length == 0)
        {
            Clear();
            return;
        }
        Phase = Results is null ? SearchPhase.Searching : Phase;
        _typed.Set(Query());
        Changed?.Invoke();
    }

    /// <summary>Turns "whole volume" on or off; the search waits to run again (the window sends it at once).</summary>
    public void SetWholeVolume(bool wholeVolume)
    {
        if (WholeVolume == wholeVolume)
        {
            return;
        }
        WholeVolume = wholeVolume;
        if (Text.Length > 0)
        {
            _typed.Set(Query());
            Changed?.Invoke();
        }
    }

    /// <summary>How long until the typed text is due; -1 when nothing waits.</summary>
    public int MillisecondsUntilDue() => _typed.MillisecondsUntilDue();

    /// <summary>
    /// Sends the typed text when it is due (or at once with <paramref name="now"/>,
    /// for Enter) and shows the answer if it is still the latest request.
    /// Returns false when nothing was due.
    /// </summary>
    public async Task<bool> SearchIfDueAsync(bool now = false)
    {
        SearchQuery query;
        if (now && _typed.HasPending)
        {
            _typed.Cancel();
            query = Query();
        }
        else if (!_typed.TryTake(out query))
        {
            return false;
        }
        var request = ++_sent;
        if (Shown is null)
        {
            // The first answer: "searching…". Later ones replace the shown hits without a flicker.
            Phase = SearchPhase.Searching;
            Changed?.Invoke();
        }
        CoreReply reply;
        try
        {
            reply = await core.RequestAsync(new SearchRequest(query.Text) { Limit = Limit, Root = query.Root });
        }
        catch (IOException error)
        {
            reply = new ErrorReply(ErrorCodes.Io, error.Message);
        }
        if (request != _sent)
        {
            // A newer search went out meanwhile, or the search was left.
            return true;
        }
        switch (reply)
        {
            case FileSearchResultsReply results:
                Shown = query;
                Results = results;
                Error = null;
                Phase = SearchPhase.Done;
                Diag.Debug(Target, "search results", new LogField("hits", results.Hits.Count), new LogField("source", results.Source),
                    new LogField("took_us", results.TookUs), new LogField("complete", results.Complete));
                break;
            case ErrorReply error:
                Shown = query;
                Results = null;
                Error = Describe(error, query);
                Phase = SearchPhase.Failed;
                break;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Leaves the search: the pane shows its folder again, and answers still on their way are dropped.</summary>
    public void Clear()
    {
        _typed.Cancel();
        _sent++;
        Text = "";
        Shown = null;
        Results = null;
        Error = null;
        Phase = SearchPhase.Idle;
        Changed?.Invoke();
    }

    /// <summary>The results' title: <c>Search: budget · 12 hits · index · 1.2 ms</c>.</summary>
    public string Header
    {
        get
        {
            var text = Shown?.Text ?? Text;
            return Phase switch
            {
                SearchPhase.Idle => "",
                SearchPhase.Done when Results is { } results => $"Search: {text} · {Hits(results.Hits.Count)} · {results.Source} · {Took(results.TookUs)}",
                SearchPhase.Failed => $"Search: {text}",
                _ => $"Search: {Text} · searching…",
            };
        }
    }

    /// <summary>What was searched, for the header's right side.</summary>
    public string Scope => (Shown ?? Query()).Root is { } root ? root : "every indexed volume";

    /// <summary>
    /// Whether the answer covers everything, and when the core walked the
    /// folders itself, that the index is not running.
    /// </summary>
    public string Note
    {
        get
        {
            if (Phase == SearchPhase.Failed)
            {
                return Error ?? "";
            }
            if (Phase != SearchPhase.Done || Results is not { } results)
            {
                return "";
            }
            var walked = results.Source == FileSearchResultsReply.FromWalk;
            var note = results.Complete
                ? $"Complete: all of {Scope} was searched."
                : walked
                    ? "Incomplete: the search stopped at its limit of 2 s or 20,000 entries."
                    : "Incomplete: a volume is still being indexed.";
            if (results.Hits.Count >= Limit)
            {
                note += $" Only the first {Limit} hits are shown; type more to narrow them.";
            }
            if (walked)
            {
                note += " The index is not running, so the core walked the folders itself; to search whole volumes at once, start the indexer (docs/indexer.md, \"Running it\").";
            }
            return note;
        }
    }

    /// <summary>"1 hit", "12 hits".</summary>
    public static string Hits(int count) => count == 1 ? "1 hit" : string.Create(CultureInfo.InvariantCulture, $"{count:N0} hits");

    /// <summary>The core's time: "0.8 ms", "12 ms", "2.1 s".</summary>
    public static string Took(ulong microseconds) => microseconds switch
    {
        < 10_000 => string.Create(CultureInfo.InvariantCulture, $"{microseconds / 1000.0:0.0} ms"),
        < 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{microseconds / 1000} ms"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{microseconds / 1_000_000.0:0.0} s"),
    };

    private SearchQuery Query() => new(Text, WholeVolume ? null : _folder);

    private static string Describe(ErrorReply error, SearchQuery query) => error.Code switch
    {
        ErrorCodes.NotFound => $"The folder {query.Root} does not exist any more.",
        ErrorCodes.UnknownRequest => "This core cannot search yet.",
        _ => $"The search failed: {error.Message}",
    };
}
