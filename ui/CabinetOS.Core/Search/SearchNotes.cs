namespace CabinetOS.Core.Search;

/// <summary>
/// What the window says when a search did not cover everything (<c>complete: false</c>, docs/ipc.md,
/// "search"): the Search view and Quick Open say it the same way, so the reason is kept here.
/// </summary>
public static class SearchNotes
{
    /// <summary>The core's walk limits as text; they are <c>WALK_LIMITS</c> in <c>core/crates/cabinetos-core/src/search.rs</c>.</summary>
    public const string WalkLimits = "2 s or 200,000 entries";

    /// <summary>Why an answer from <paramref name="source"/> that is not complete stopped: "the search stopped at its limit of …".</summary>
    public static string Reason(string source) =>
        source == Protocol.FileSearchResultsReply.FromWalk
            ? $"the search stopped at its limit of {WalkLimits}"
            : "a volume is still being indexed";
}
