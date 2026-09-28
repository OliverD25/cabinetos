using CabinetOS.Core.Ipc;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Search;

namespace CabinetOS.Tests;

/// <summary>The search field's model: the wait, the request, stale answers, and what the pane says.</summary>
public class SearchTests
{
    private long _now = 1000;
    private readonly HeldChannel _core = new();
    private readonly SearchModel _search;

    public SearchTests() => _search = new SearchModel(_core, () => _now);

    private static FileSearchResultsReply Results(string source, bool complete, params string[] paths) =>
        new(paths.Select(p => new FileHit(p, "file")).ToList(), source, 1210, complete);

    [Fact]
    public async Task Typing_waits_150_ms_then_asks_for_100_hits_under_the_pane_s_folder()
    {
        _search.SetText("bud", @"C:\Users\me");
        _now += 100;
        _search.SetText(" budget ", @"C:\Users\me");
        Assert.True(_search.IsActive);
        Assert.Equal("Search: budget · searching…", _search.Header);

        _now += 149;
        Assert.False(await _search.SearchIfDueAsync());
        Assert.Empty(_core.Sent);

        _now += 1;
        var search = _search.SearchIfDueAsync();
        var request = Assert.IsType<SearchRequest>(Assert.Single(_core.Sent));
        Assert.Equal(("budget", 100u, @"C:\Users\me"), (request.Query, request.Limit, request.Root));

        _core.Answer(0, Results("index", true, @"C:\Users\me\Budget-2026.xlsx"));
        Assert.True(await search);
        Assert.Equal(SearchPhase.Done, _search.Phase);
        Assert.Equal("Search: budget · 1 hit · index · 1.2 ms", _search.Header);
        Assert.Equal(@"Complete: all of C:\Users\me was searched.", _search.Note);
    }

    [Fact]
    public async Task Whole_volume_drops_the_root()
    {
        _search.SetText("budget", @"C:\Users\me");
        _search.SetWholeVolume(true);
        // The window sends it at once, as for Enter.
        var search = _search.SearchIfDueAsync(now: true);
        Assert.Null(Assert.IsType<SearchRequest>(Assert.Single(_core.Sent)).Root);
        _core.Answer(0, Results("index", true));
        await search;
        Assert.Equal("every indexed volume", _search.Scope);
        Assert.Equal("Search: budget · 0 hits · index · 1.2 ms", _search.Header);
    }

    [Fact]
    public async Task Enter_searches_at_once()
    {
        _search.SetText("budget", @"C:\");
        var search = _search.SearchIfDueAsync(now: true);
        Assert.Single(_core.Sent);
        _core.Answer(0, Results("index", true));
        Assert.True(await search);
        Assert.Equal(-1, _search.MillisecondsUntilDue());
    }

    [Fact]
    public async Task An_answer_to_an_older_request_is_dropped()
    {
        _search.SetText("bud", @"C:\");
        _now += 150;
        var first = _search.SearchIfDueAsync();
        _search.SetText("budget", @"C:\");
        _now += 150;
        var second = _search.SearchIfDueAsync();

        _core.Answer(1, Results("index", true, @"C:\budget.txt"));
        await second;
        _core.Answer(0, Results("index", true, @"C:\bud.txt", @"C:\budget.txt"));
        await first;

        Assert.Equal("budget", _search.Shown!.Text);
        Assert.Equal(@"C:\budget.txt", Assert.Single(_search.Results!.Hits).Path);
    }

    [Fact]
    public async Task Leaving_the_search_drops_the_answer_on_its_way()
    {
        _search.SetText("budget", @"C:\");
        _now += 150;
        var search = _search.SearchIfDueAsync();
        _search.Clear();
        _core.Answer(0, Results("index", true, @"C:\budget.txt"));
        await search;

        Assert.Equal(SearchPhase.Idle, _search.Phase);
        Assert.False(_search.IsActive);
        Assert.Null(_search.Results);
        Assert.Equal("", _search.Header);
    }

    [Fact]
    public async Task A_walk_says_the_index_is_not_running_and_a_limit_says_it_is_incomplete()
    {
        _search.SetText("a", @"D:\photos");
        _now += 150;
        var search = _search.SearchIfDueAsync();
        _core.Answer(0, Results("walk", false, Enumerable.Range(0, 100).Select(i => $@"D:\photos\a{i}.jpg").ToArray()));
        await search;

        Assert.Equal("Search: a · 100 hits · walk · 1.2 ms", _search.Header);
        Assert.StartsWith("Incomplete: the search stopped at its limit of 2 s or 20,000 entries.", _search.Note);
        Assert.Contains("Only the first 100 hits are shown", _search.Note);
        Assert.Contains("The index is not running", _search.Note);
        Assert.Contains("docs/indexer.md", _search.Note);
    }

    [Fact]
    public async Task A_folder_that_is_gone_is_said_in_the_note()
    {
        _search.SetText("x", @"C:\gone");
        _now += 150;
        var search = _search.SearchIfDueAsync();
        _core.Answer(0, new ErrorReply(ErrorCodes.NotFound, "not found"));
        await search;

        Assert.Equal(SearchPhase.Failed, _search.Phase);
        Assert.Equal("Search: x", _search.Header);
        Assert.Equal(@"The folder C:\gone does not exist any more.", _search.Note);
    }

    [Theory]
    [InlineData(800UL, "0.8 ms")]
    [InlineData(1210UL, "1.2 ms")]
    [InlineData(12_345UL, "12 ms")]
    [InlineData(2_100_000UL, "2.1 s")]
    public void Times_read_like_the_status_bar(ulong microseconds, string text) =>
        Assert.Equal(text, SearchModel.Took(microseconds));

    [Fact]
    public void Hits_are_counted_in_words() =>
        Assert.Equal(("1 hit", "0 hits", "1,000 hits"), (SearchModel.Hits(1), SearchModel.Hits(0), SearchModel.Hits(1000)));

    /// <summary>A core whose answers the test gives, in any order.</summary>
    private sealed class HeldChannel : ICoreChannel
    {
        private readonly List<TaskCompletionSource<CoreReply>> _replies = [];

        public List<CoreRequest> Sent { get; } = [];

        public Task<CoreReply> RequestAsync(CoreRequest request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            var reply = new TaskCompletionSource<CoreReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _replies.Add(reply);
            return reply.Task;
        }

        public void Answer(int index, CoreReply reply) => _replies[index].SetResult(reply);
    }
}
