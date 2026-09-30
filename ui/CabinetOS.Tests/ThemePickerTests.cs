using System.Collections.Concurrent;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Themes;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// The theme picker's model: the core's themes, the highlight, applying one through <c>set_value ui.theme</c>,
/// and the live preview of the highlighted theme through <c>get_theme</c>.
/// </summary>
public class ThemePickerTests
{
    private static readonly ThemeInfo[] Themes =
    [
        new("default", "Default", "CabinetOS", "1.1.0", ColorTheme.System),
        new("nord", "Nord", "CabinetOS", "1.0.0", ColorTheme.Dark, "#88C0D0", new MicaTint("#2E3440", 0.88)),
        new("paper", "Paper", "Someone", "0.1.0", ColorTheme.Light, "#0F6CBD"),
    ];

    private static FakeChannel Core(Func<SetValueRequest, CoreReply>? setValue = null, Func<GetThemeRequest, CoreReply>? getTheme = null) =>
        new(request => request switch
        {
            ListThemesRequest => new ThemesReply(Themes),
            SetValueRequest set => setValue?.Invoke(set) ?? new OkReply(),
            GetThemeRequest get => getTheme?.Invoke(get) ?? new ThemeReply(Whole(get.ThemeId!)),
            _ => new ErrorReply(ErrorCodes.UnknownRequest, request.Type),
        });

    // Any whole theme will do: the picker hands on what get_theme answered, by ID.
    private static ColorTheme Whole(string id) => ThemeTests.Shipped("nord") with { Id = id };

    // An open picker on "default" with previews on and no delay; the IDs it asked the window to paint (null: the theme in effect).
    private static async Task<(ThemePickerModel Picker, ConcurrentQueue<string?> Painted)> OpenAsync(ICoreChannel core)
    {
        var picker = new ThemePickerModel(core) { PreviewDelay = TimeSpan.Zero };
        var painted = new ConcurrentQueue<string?>();
        picker.Preview += theme => painted.Enqueue(theme?.Id);
        picker.BeginPreviews();
        Assert.True(await picker.LoadAsync("default"));
        return (picker, painted);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 10)
        {
            Assert.True(waited < 5000, "waited 5 s");
            await Task.Delay(10);
        }
    }

    // A core whose get_theme answers wait until the test gives them, in any order.
    private sealed class HeldCore : ICoreChannel
    {
        public List<(string Id, TaskCompletionSource<CoreReply> Reply)> Held { get; } = [];

        public Task<CoreReply> RequestAsync(CoreRequest request, CancellationToken cancellationToken = default)
        {
            if (request is not GetThemeRequest get)
            {
                return Task.FromResult<CoreReply>(new ThemesReply(Themes));
            }
            var reply = new TaskCompletionSource<CoreReply>();
            Held.Add((get.ThemeId!, reply));
            return reply.Task;
        }

        public void Answer(int index) => Held[index].Reply.SetResult(new ThemeReply(Whole(Held[index].Id)));
    }

    [Fact]
    public async Task Opening_lists_the_themes_in_the_core_order_with_the_current_one_marked_and_highlighted()
    {
        var core = Core();
        var picker = new ThemePickerModel(core);

        Assert.True(await picker.LoadAsync("nord"));

        Assert.Equal(["default", "nord", "paper"], picker.Rows.Select(r => r.Info.Id));
        Assert.Equal([false, true, false], picker.Rows.Select(r => r.IsCurrent));
        Assert.Equal(1, picker.Highlight);
        Assert.Null(picker.Error);
        // The swatches' tints come with the list (protocol 11): one request, no theme file read.
        Assert.Equal([null, "#2E3440", null], picker.Rows.Select(r => r.Tint));
        Assert.IsType<ListThemesRequest>(Assert.Single(core.Requests));
    }

    [Fact]
    public async Task Without_a_current_theme_the_first_row_is_highlighted()
    {
        var picker = new ThemePickerModel(Core());
        Assert.True(await picker.LoadAsync(null));
        Assert.Equal(0, picker.Highlight);
        Assert.DoesNotContain(picker.Rows, r => r.IsCurrent);
    }

    [Fact]
    public async Task Up_and_down_stay_on_the_list_and_each_move_redraws_once()
    {
        var picker = new ThemePickerModel(Core());
        await picker.LoadAsync("default");
        var redraws = 0;
        picker.Changed += () => redraws++;

        picker.Move(-1);
        Assert.Equal((0, 0), (picker.Highlight, redraws));
        picker.Move(1);
        Assert.Equal((1, 1), (picker.Highlight, redraws));
        picker.Move(10);
        Assert.Equal((2, 2), (picker.Highlight, redraws));
        picker.SetHighlight(7);
        picker.SetHighlight(-1);
        Assert.Equal((2, 2), (picker.Highlight, redraws));
        picker.SetHighlight(0);
        Assert.Equal((0, 3), (picker.Highlight, redraws));
    }

    [Fact]
    public async Task Enter_applies_the_highlighted_theme_and_a_click_applies_its_row_through_set_value()
    {
        var core = Core();
        var picker = new ThemePickerModel(core);
        await picker.LoadAsync("default");

        picker.Move(1);
        Assert.True(await picker.ApplyAsync(requestId: "01J0000000000000000000000A"));
        var byKey = core.Requests.OfType<SetValueRequest>().Single();
        Assert.Equal(("ui.theme", "nord", "01J0000000000000000000000A"), (byKey.Path, byKey.Value.GetString(), byKey.Id));

        Assert.True(await picker.ApplyAsync(2));
        Assert.Equal("paper", core.Requests.OfType<SetValueRequest>().Last().Value.GetString());
        Assert.False(picker.IsApplying);
    }

    [Fact]
    public async Task A_theme_the_core_refuses_keeps_the_picker_open_with_the_core_message()
    {
        var picker = new ThemePickerModel(Core(_ => new ErrorReply(ErrorCodes.NoSuchTheme, "no theme paper: paper.json line 2: expected `,`")));
        await picker.LoadAsync("default");

        Assert.False(await picker.ApplyAsync(2));
        Assert.Equal("no theme paper: paper.json line 2: expected `,`", picker.Error);
        Assert.False(await picker.ApplyAsync(9));
    }

    [Fact]
    public async Task A_core_before_protocol_10_says_it_has_no_themes()
    {
        var picker = new ThemePickerModel(new FakeChannel(request => new ErrorReply(ErrorCodes.UnknownRequest, request.Type)));

        Assert.False(await picker.LoadAsync("default"));
        Assert.Empty(picker.Rows);
        Assert.Equal(-1, picker.Highlight);
        Assert.Equal("This core has no themes yet (list_themes).", picker.Error);
        Assert.False(await picker.ApplyAsync());
    }

    [Fact]
    public async Task Moving_the_highlight_previews_the_theme_get_theme_answers_and_writes_nothing()
    {
        var core = Core();
        var (picker, painted) = await OpenAsync(core);
        // Opening lands on the theme in effect: nothing to fetch.
        Assert.Empty(core.Requests.OfType<GetThemeRequest>());

        picker.Move(1);
        Assert.Equal(["nord"], painted);
        Assert.True(picker.IsPreviewShown);
        picker.SetHighlight(2);
        Assert.Equal(["nord", "paper"], painted);

        Assert.Equal(["nord", "paper"], core.Requests.OfType<GetThemeRequest>().Select(r => r.ThemeId));
        Assert.Empty(core.Requests.OfType<SetValueRequest>());
    }

    [Fact]
    public async Task Highlighting_the_theme_in_effect_paints_it_again_without_a_request()
    {
        var core = Core();
        var (picker, painted) = await OpenAsync(core);

        picker.Move(1);
        picker.Move(-1);

        Assert.Equal(["nord", null], painted);
        Assert.False(picker.IsPreviewShown);
        Assert.Single(core.Requests.OfType<GetThemeRequest>());
    }

    [Fact]
    public async Task A_reply_for_a_row_the_highlight_left_is_dropped_and_the_later_one_is_painted()
    {
        var core = new HeldCore();
        var (picker, painted) = await OpenAsync(core);

        picker.Move(1);
        picker.Move(1);
        Assert.Equal(["nord", "paper"], core.Held.Select(h => h.Id));
        core.Answer(1);
        await UntilAsync(() => !painted.IsEmpty);
        core.Answer(0);
        await Task.Delay(100);

        Assert.Equal(["paper"], painted);
    }

    [Fact]
    public async Task Closing_with_restore_paints_the_theme_in_effect_once_and_only_after_a_preview()
    {
        var (unmoved, nothing) = await OpenAsync(Core());
        unmoved.EndPreviews(restore: true);
        Assert.Empty(nothing);

        var (picker, painted) = await OpenAsync(Core());
        picker.Move(1);
        picker.EndPreviews(restore: true);
        picker.EndPreviews(restore: true);
        Assert.Equal(["nord", null], painted);

        // A reply that comes after the close is dropped, and the closed picker previews no more.
        var core = new HeldCore();
        var (late, latePainted) = await OpenAsync(core);
        late.Move(1);
        late.EndPreviews(restore: true);
        core.Answer(0);
        await Task.Delay(100);
        late.Move(1);
        Assert.Empty(latePainted);
        Assert.Single(core.Held);
        Assert.False(late.IsPreviewShown);
    }

    [Fact]
    public async Task Closing_after_an_apply_leaves_the_preview_on_screen()
    {
        var (picker, painted) = await OpenAsync(Core());
        picker.Move(1);
        Assert.True(await picker.ApplyAsync());

        picker.EndPreviews(restore: false);

        Assert.Equal(["nord"], painted);
        Assert.False(picker.IsPreviewShown);
    }

    [Fact]
    public async Task Choosing_the_theme_in_effect_before_its_repaint_was_due_paints_it_back()
    {
        // The core sends no theme_changed for the theme it has, so nothing else would end the preview.
        var core = Core();
        var picker = new ThemePickerModel(core) { PreviewDelay = TimeSpan.FromMilliseconds(40) };
        var painted = new ConcurrentQueue<string?>();
        picker.Preview += theme => painted.Enqueue(theme?.Id);
        picker.BeginPreviews();
        await picker.LoadAsync("default");
        picker.Move(1);
        await UntilAsync(() => !painted.IsEmpty);

        picker.Move(-1);
        Assert.True(await picker.ApplyAsync());
        picker.EndPreviews(restore: false);
        await Task.Delay(200);

        Assert.Equal(["nord", null], painted);
    }

    [Fact]
    public async Task Quick_moves_fetch_only_the_row_the_highlight_rests_on()
    {
        var core = Core();
        var picker = new ThemePickerModel(core) { PreviewDelay = TimeSpan.FromMilliseconds(40) };
        var painted = new ConcurrentQueue<string?>();
        picker.Preview += theme => painted.Enqueue(theme?.Id);
        picker.BeginPreviews();
        await picker.LoadAsync("default");

        picker.Move(1);
        picker.Move(1);
        picker.Move(-1);
        await UntilAsync(() => !painted.IsEmpty);
        await Task.Delay(200);

        Assert.Equal("nord", Assert.Single(core.Requests.OfType<GetThemeRequest>()).ThemeId);
        Assert.Equal(["nord"], painted);
    }

    [Fact]
    public async Task A_theme_changed_moves_the_check_and_leaves_the_highlight()
    {
        var (picker, painted) = await OpenAsync(Core());
        picker.Move(1);
        var redraws = 0;
        picker.Changed += () => redraws++;

        picker.MarkCurrent("nord");

        Assert.Equal([false, true, false], picker.Rows.Select(r => r.IsCurrent));
        Assert.Equal((1, 1), (picker.Highlight, redraws));
        // Nord is the theme in effect now: Esc has nothing to paint back.
        Assert.False(picker.IsPreviewShown);
        picker.EndPreviews(restore: true);
        Assert.Equal(["nord"], painted);
    }

    [Fact]
    public async Task A_theme_the_core_cannot_send_leaves_the_screen_and_the_footer_as_they_are()
    {
        var picker = (await OpenAsync(Core(getTheme: get => new ErrorReply(ErrorCodes.NoSuchTheme, $"no theme {get.ThemeId}")))).Picker;
        var painted = 0;
        picker.Preview += _ => painted++;

        picker.Move(1);

        Assert.Equal(0, painted);
        Assert.False(picker.IsPreviewShown);
        Assert.Null(picker.Error);
    }
}
