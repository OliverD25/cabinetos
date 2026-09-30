using CabinetOS.Core.Keys;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Tests;

/// <summary>The key state machine of docs/keybindings.md, "Chords" and "Contexts".</summary>
public class ChordStateMachineTests
{
    private static readonly HashSet<string> Nothing = [];
    private static readonly HashSet<string> Files = [KeyContexts.FilesView];
    private static readonly HashSet<string> Typing = [KeyContexts.TextInput];
    private static readonly HashSet<string> Palette = [KeyContexts.PaletteOpen, KeyContexts.TextInput];

    private long _now;
    private readonly ChordStateMachine _keys;

    public ChordStateMachineTests()
    {
        _keys = new ChordStateMachine(() => _now);
        _keys.SetKeymap(Keymap.From(new KeymapData(1000,
        [
            new KeymapBinding("ctrl+shift+p", "palette.show", null),
            new KeymapBinding("escape", "overlay.close", null),
            new KeymapBinding("ctrl+k ctrl+s", "keys.open", null),
            new KeymapBinding("ctrl+b", "view.toggleSidebar", null),
            new KeymapBinding("tab", "view.focusOtherPane", KeyContexts.FilesView),
            new KeymapBinding("f5", "file.copyToOtherPane", KeyContexts.FilesView),
            new KeymapBinding("ctrl+k ctrl+w", "workspace.switch", null),
            new KeymapBinding("f2", "test.everywhere", null),
            new KeymapBinding("f2", "test.inPalette", KeyContexts.PaletteOpen),
        ],
        ["palette.show", "overlay.close", "keys.open"])));
    }

    private static KeyCombo Combo(string text) =>
        KeyCombo.TryParse(text, out var combo) ? combo.Value : throw new ArgumentException(text);

    private KeyOutcome Press(string text, HashSet<string>? contexts = null) => _keys.OnKey(Combo(text), contexts ?? Nothing);

    [Fact]
    public void A_single_binding_runs_at_once()
    {
        var outcome = Assert.IsType<KeyOutcome.Run>(Press("ctrl+b"));
        Assert.Equal("view.toggleSidebar", outcome.Command);
        Assert.Null(_keys.PendingFirst);
    }

    [Fact]
    public void A_key_nobody_bound_goes_on_to_the_focused_control()
    {
        Assert.IsType<KeyOutcome.PassThrough>(Press("ctrl+j"));
        Assert.IsType<KeyOutcome.PassThrough>(Press("down", Files));
    }

    [Fact]
    public void A_chord_waits_for_its_second_half_and_then_runs()
    {
        var changes = 0;
        _keys.PendingChanged += () => changes++;

        var pending = Assert.IsType<KeyOutcome.Pending>(Press("ctrl+k"));
        Assert.Equal(Combo("ctrl+k"), pending.First);
        Assert.Equal(Combo("ctrl+k"), _keys.PendingFirst);

        _now += 999;
        var run = Assert.IsType<KeyOutcome.Run>(Press("ctrl+w"));
        Assert.Equal("workspace.switch", run.Command);
        Assert.Equal("ctrl+k ctrl+w", run.Keys.ToString());
        Assert.Null(_keys.PendingFirst);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void The_second_half_may_come_exactly_at_the_end_of_the_window()
    {
        Press("ctrl+k");
        _now += 1000;
        Assert.Equal("keys.open", Assert.IsType<KeyOutcome.Run>(Press("ctrl+s")).Command);
    }

    [Fact]
    public void A_chord_times_out_after_the_window_and_nothing_runs()
    {
        Press("ctrl+k");
        _now += 1001;
        Assert.True(_keys.ExpireIfDue());
        Assert.Null(_keys.PendingFirst);
        // Back to idle: ctrl+w alone is no binding.
        Assert.IsType<KeyOutcome.PassThrough>(Press("ctrl+w"));
    }

    [Fact]
    public void A_late_second_half_is_read_from_idle_even_without_the_timer()
    {
        Press("ctrl+k");
        _now += 5000;
        Assert.IsType<KeyOutcome.PassThrough>(Press("ctrl+s"));
    }

    [Fact]
    public void A_chord_prefix_is_not_a_binding_of_its_own_and_a_wrong_second_key_runs_nothing()
    {
        Assert.IsType<KeyOutcome.Pending>(Press("ctrl+k"));
        var notBound = Assert.IsType<KeyOutcome.NotBound>(Press("x"));
        Assert.Equal((Combo("ctrl+k"), Combo("x")), (notBound.First, notBound.Second));
        Assert.Null(_keys.PendingFirst);
    }

    [Fact]
    public void A_binding_whose_context_holds_wins_over_one_without()
    {
        Assert.Equal("test.inPalette", Assert.IsType<KeyOutcome.Run>(Press("f2", [KeyContexts.PaletteOpen])).Command);
        Assert.Equal("test.everywhere", Assert.IsType<KeyOutcome.Run>(Press("f2")).Command);
    }

    [Fact]
    public void A_chord_prefix_pressed_twice_runs_nothing_and_leaves_no_wait()
    {
        Assert.IsType<KeyOutcome.Pending>(Press("ctrl+k"));
        var twice = Assert.IsType<KeyOutcome.NotBound>(Press("ctrl+k"));
        Assert.Equal((Combo("ctrl+k"), Combo("ctrl+k")), (twice.First, twice.Second));
        Assert.Null(_keys.PendingFirst);
        // The next press starts over from idle.
        Assert.IsType<KeyOutcome.Pending>(Press("ctrl+k"));
        Assert.Equal("keys.open", Assert.IsType<KeyOutcome.Run>(Press("ctrl+s")).Command);
    }

    [Fact]
    public void A_first_half_held_down_keeps_the_wait_while_windows_repeats_it()
    {
        Assert.IsType<KeyOutcome.Pending>(Press("ctrl+k"));
        // Windows repeats a held key after its delay (250 to 1000 ms): the repeats are the same press.
        for (var i = 0; i < 3; i++)
        {
            _now += 40;
            Assert.Equal(Combo("ctrl+k"), Assert.IsType<KeyOutcome.Pending>(_keys.OnKey(Combo("ctrl+k"), Nothing, repeat: true)).First);
        }
        Assert.Equal("workspace.switch", Assert.IsType<KeyOutcome.Run>(Press("ctrl+w")).Command);
        Assert.Null(_keys.PendingFirst);
    }

    [Fact]
    public void The_wait_counts_from_the_last_repeat_of_a_first_half_held_down()
    {
        var changes = 0;
        _keys.PendingChanged += () => changes++;
        Press("ctrl+k");
        _now += 900;
        _keys.OnKey(Combo("ctrl+k"), Nothing, repeat: true);
        _now += 900;
        Assert.False(_keys.ExpireIfDue());
        Assert.Equal("keys.open", Assert.IsType<KeyOutcome.Run>(Press("ctrl+s")).Command);
        // Each repeat restarts the wait, so the window's timer and the status bar start again too.
        Assert.Equal(3, changes);
    }

    [Fact]
    public void A_repeated_key_that_is_not_the_waiting_first_half_still_ends_the_wait()
    {
        Press("ctrl+k");
        Assert.IsType<KeyOutcome.NotBound>(_keys.OnKey(Combo("x"), Nothing, repeat: true));
        Assert.Null(_keys.PendingFirst);
    }

    [Fact]
    public void A_chord_that_timed_out_can_be_started_again_and_its_window_counts_from_the_new_press()
    {
        Press("ctrl+k");
        _now += 1001;
        Assert.True(_keys.ExpireIfDue());

        Assert.IsType<KeyOutcome.Pending>(Press("ctrl+k"));
        _now += 1000;
        Assert.Equal("workspace.switch", Assert.IsType<KeyOutcome.Run>(Press("ctrl+w")).Command);

        // Late again, without the timer: the prefix pressed anew still starts a fresh wait.
        Press("ctrl+k");
        _now += 2000;
        Assert.IsType<KeyOutcome.Pending>(Press("ctrl+k"));
        Assert.Equal("keys.open", Assert.IsType<KeyOutcome.Run>(Press("ctrl+s")).Command);
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    [InlineData(0x12)]
    [InlineData(0x5B)]
    [InlineData(0x5C)]
    [InlineData(0xA0)]
    [InlineData(0xA3)]
    [InlineData(0xA5)]
    public void A_modifier_pressed_alone_makes_no_combination_so_a_chord_keeps_waiting(int modifierKey)
    {
        Assert.Null(KeyNames.ComboFor(modifierKey, KeyModifiers.Ctrl));
        Assert.IsType<KeyOutcome.Pending>(Press("ctrl+k"));
        // The window feeds the machine only what ComboFor returns: Ctrl pressed again between the halves is not a key.
        if (KeyNames.ComboFor(modifierKey, KeyModifiers.Ctrl) is { } combo)
        {
            _keys.OnKey(combo, Nothing);
        }
        Assert.Equal(Combo("ctrl+k"), _keys.PendingFirst);
        Assert.Equal("keys.open", Assert.IsType<KeyOutcome.Run>(_keys.OnKey(KeyNames.ComboFor(0x53, KeyModifiers.Ctrl)!.Value, Nothing)).Command);
    }

    [Fact]
    public void A_binding_with_a_context_applies_only_while_it_holds()
    {
        Assert.Equal("view.focusOtherPane", Assert.IsType<KeyOutcome.Run>(Press("tab", Files)).Command);
        Assert.IsType<KeyOutcome.PassThrough>(Press("tab"));
        Assert.IsType<KeyOutcome.PassThrough>(Press("f5", Typing));
    }

    [Fact]
    public void Text_input_takes_precedence_except_for_the_immutable_tier()
    {
        Assert.IsType<KeyOutcome.PassThrough>(Press("ctrl+b", Typing));
        Assert.IsType<KeyOutcome.PassThrough>(Press("f2", Typing));
        Assert.Equal("overlay.close", Assert.IsType<KeyOutcome.Run>(Press("escape", Typing)).Command);
        Assert.Equal("palette.show", Assert.IsType<KeyOutcome.Run>(Press("ctrl+shift+p", Palette)).Command);
        Assert.Equal("test.inPalette", Assert.IsType<KeyOutcome.Run>(Press("f2", Palette)).Command);

        Assert.IsType<KeyOutcome.Pending>(Press("ctrl+k", Typing));
        Assert.Equal("keys.open", Assert.IsType<KeyOutcome.Run>(Press("ctrl+s", Typing)).Command);
        Assert.IsType<KeyOutcome.Pending>(Press("ctrl+k", Typing));
        Assert.IsType<KeyOutcome.NotBound>(Press("ctrl+w", Typing));
    }

    [Fact]
    public void A_new_keymap_ends_a_wait()
    {
        Press("ctrl+k");
        _keys.SetKeymap(Keymap.Empty);
        Assert.Null(_keys.PendingFirst);
        Assert.IsType<KeyOutcome.PassThrough>(Press("ctrl+k"));
    }

    [Fact]
    public void A_binding_that_does_not_parse_is_skipped()
    {
        var keymap = Keymap.From(new KeymapData(1000,
            [new KeymapBinding("ctrl+nope", "broken", null), new KeymapBinding("f9", "fine", null)], []));
        Assert.Equal(["fine"], keymap.Bindings.Select(b => b.Command));
    }
}
