using System.Text.Json;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Themes;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>The theme picker's model: the core's themes, the highlight, and applying one through <c>set_value ui.theme</c>.</summary>
public class ThemePickerTests
{
    private static readonly ThemeInfo[] Themes =
    [
        new("default", "Default", "CabinetOS", "1.0.0", ColorTheme.Dark),
        new("nord", "Nord", "CabinetOS", "1.0.0", ColorTheme.Dark, "#88C0D0"),
        new("paper", "Paper", "Someone", "0.1.0", ColorTheme.Light, "#0F6CBD"),
    ];

    private static ColorTheme Shipped(string id) =>
        JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(Repo.Root, "sdk", "themes", id + ".json")), ProtocolJson.Default.ColorTheme)!;

    // A core with three themes; "paper" has no readable file, so its swatch stays plain.
    private static FakeChannel Core(Func<SetValueRequest, CoreReply>? setValue = null) => new(request => request switch
    {
        ListThemesRequest => new ThemesReply(Themes),
        GetThemeRequest { ThemeId: "paper" } => new ErrorReply(ErrorCodes.ConfigError, "paper.json line 2: expected `,`"),
        GetThemeRequest { ThemeId: var id } => new ThemeReply(Shipped(id!)),
        SetValueRequest set => setValue?.Invoke(set) ?? new OkReply(),
        _ => new ErrorReply(ErrorCodes.UnknownRequest, request.Type),
    });

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
        // The swatches: the tint from each theme's file; plain Mica, or a file that cannot be read, has none.
        Assert.Equal([null, "#2E3440", null], picker.Rows.Select(r => r.Tint));
        Assert.IsType<ListThemesRequest>(core.Requests[0]);
        Assert.Equal(["default", "nord", "paper"], core.Requests.OfType<GetThemeRequest>().Select(r => r.ThemeId).Order());
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
}
