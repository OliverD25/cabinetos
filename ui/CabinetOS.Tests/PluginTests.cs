using CabinetOS.Core.Plugins;
using CabinetOS.Core.Protocol;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>The plugin list, the permissions review dialog, and the notices when a plugin's state changes.</summary>
public class PluginTests
{
    private static PluginInfo Hello(PluginState state, bool granted = false, params string[] commands) => new(
        "hello", "Hello", "0.1.0", "CabinetOS", "The sample Core Plugin.", state,
        [
            new CapabilityInfo("cmd:register", "low", granted, "Adds the Say Hello command to the palette."),
            new CapabilityInfo("events:emit", "low", granted, "Tells the window each time it said hello."),
        ],
        commands);

    private static readonly PluginState NeedsReview = new(PluginState.NeedsReview, ["cmd:register", "events:emit"]);

    [Fact]
    public void The_review_shows_every_capability_with_its_level_and_reason()
    {
        var review = new PermissionReview(Hello(NeedsReview));

        Assert.Equal("Hello by CabinetOS", review.Subtitle);
        Assert.Equal("Trust CabinetOS for future updates", review.TrustText);
        Assert.Equal("This plugin runs in a WebAssembly sandbox. It can only do what you allow here.", PermissionReview.Sandbox);
        Assert.Equal(["cmd:register", "events:emit"], review.Rows.Select(r => r.Name));
        Assert.All(review.Rows, row => Assert.Equal(("LOW", "#6CCB5F"), (row.LevelText, row.Color)));
        Assert.Equal("Adds the Say Hello command to the palette.", review.Rows[0].Detail);
        Assert.Equal(["cmd:register", "events:emit"], review.ToGrant);
    }

    [Fact]
    public async Task Allow_grants_what_is_missing_and_says_why_when_the_core_refuses()
    {
        var review = new PermissionReview(Hello(NeedsReview));
        var core = new FakeChannel(_ => new OkReply());
        Assert.True(await review.AllowAsync(core));
        var grant = Assert.IsType<GrantCapabilitiesRequest>(Assert.Single(core.Requests));
        Assert.Equal("hello", grant.PluginId);
        Assert.Equal(["cmd:register", "events:emit"], grant.Capabilities);
        Assert.Null(review.Error);

        var refused = new PermissionReview(Hello(NeedsReview));
        Assert.False(await refused.AllowAsync(new FakeChannel(_ => new ErrorReply(ErrorCodes.ConfigError, "cabinetos.json line 3: expected `,`"))));
        Assert.Equal("cabinetos.json line 3: expected `,`", refused.Error);

        var gone = new PermissionReview(Hello(NeedsReview));
        Assert.False(await gone.AllowAsync(new FakeChannel(_ => new ErrorReply(ErrorCodes.NoSuchPlugin, "no such plugin"))));
        Assert.Equal("Hello is not installed any more.", gone.Error);
    }

    [Fact]
    public void Without_a_missing_list_the_review_grants_what_is_not_granted()
    {
        var plugin = Hello(new PluginState(PluginState.NeedsReview)) with
        {
            Capabilities =
            [
                new CapabilityInfo("cmd:register", "low", true, "Adds a command."),
                new CapabilityInfo("fs:read", "medium", false, "Reads its folder.", [@"%TEMP%\reader"]),
            ],
        };
        var review = new PermissionReview(plugin);
        Assert.Equal(["fs:read"], review.ToGrant);
        Assert.Equal(("MEDIUM", "#F2C063"), (review.Rows[1].LevelText, review.Rows[1].Color));
        Assert.Equal(@"Reads its folder. (%TEMP%\reader)", review.Rows[1].Detail);
    }

    [Fact]
    public void The_list_says_what_each_plugin_does_and_offers_review_or_reload()
    {
        var active = new PluginRow(Hello(new PluginState(PluginState.Active), true, "hello.say"));
        Assert.Equal(("Hello 0.1.0", "Active: 1 command", false, false), (active.Title, active.StateText, active.CanReview, active.CanReload));

        var waiting = new PluginRow(Hello(NeedsReview));
        Assert.Equal("Waits for your review: cmd:register, events:emit", waiting.StateText);
        Assert.True(waiting.CanReview);

        var crashed = new PluginRow(Hello(new PluginState(PluginState.Crashed, Message: "wasm trap: unreachable")));
        Assert.StartsWith("Crashed: wasm trap", crashed.StateText);
        Assert.True(crashed.CanReload);
        Assert.True(crashed.IsProblem);

        var failed = new PluginRow(Hello(new PluginState(PluginState.Failed, Message: "asks for net, which is never granted")));
        Assert.Equal("Cannot start: asks for net, which is never granted", failed.StateText);
        Assert.True(failed.CanReload);
        Assert.False(failed.CanReview);
    }

    [Fact]
    public void A_plugin_that_newly_waits_for_review_is_put_forward_once_per_session()
    {
        var watch = new PluginWatch();
        // At start, a waiting plugin is known, not pushed in front of the user.
        var start = watch.Update([Hello(NeedsReview)]);
        Assert.Empty(start.ToReview);

        Assert.Empty(watch.Update([Hello(new PluginState(PluginState.Loading))]).ToReview);
        var again = watch.Update([Hello(NeedsReview)]);
        Assert.Equal("hello", Assert.Single(again.ToReview).Id);

        watch.Update([Hello(new PluginState(PluginState.Loading))]);
        Assert.Empty(watch.Update([Hello(NeedsReview)]).ToReview);
    }

    [Fact]
    public void Becoming_active_and_crashing_are_announced_once()
    {
        var watch = new PluginWatch();
        watch.Update([Hello(new PluginState(PluginState.Loading))]);

        var active = watch.Update([Hello(new PluginState(PluginState.Active), true, "hello.say")]);
        Assert.Equal("hello", Assert.Single(active.BecameActive).Id);
        Assert.Empty(watch.Update([Hello(new PluginState(PluginState.Active), true, "hello.say")]).BecameActive);

        var crashed = watch.Update([Hello(new PluginState(PluginState.Crashed, Message: "trap"))]);
        Assert.Equal("hello", Assert.Single(crashed.Crashed).Id);
    }

    [Fact]
    public void A_plugin_s_tile_is_its_initials_on_a_color_that_stays()
    {
        var tile = PluginTile.For(Hello(NeedsReview));
        Assert.Equal("He", tile.Text);
        Assert.Equal(tile, PluginTile.For(Hello(new PluginState(PluginState.Active))));
        Assert.Equal("GL", PluginTile.For(Hello(NeedsReview) with { Id = "gitlens", Name = "Git Lens" }).Text);
    }
}
