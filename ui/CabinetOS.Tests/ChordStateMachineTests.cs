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
            new KeymapBinding("ctrl+k v", "editor.openMarkdownPreview", KeyContexts.FilesView),
            new KeymapBinding("ctrl+tab", "tab.next", KeyContexts.FilesView),
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
    public void A_second_half_that_completes_a_chord_bound_elsewhere_says_where_it_works()
    {
        // In a text box that is not the pane's (a name typed in place): the chord is bound in filesView.
        Press("ctrl+k", Typing);
        var typing = Assert.IsType<KeyOutcome.NotBound>(Press("v", Typing));
        Assert.Equal("editor.openMarkdownPreview", typing.Elsewhere?.Command);
        Assert.Equal("Ctrl+K V works only in a file list.", ChordNotice.Text(typing));

        // A chord without a context misses only where a box keeps its first key, which types there.
        var boxKept = new KeyOutcome.NotBound(Combo("g"), Combo("g"), new Binding(new KeySequence(Combo("g"), Combo("g")), "test.top", null));
        Assert.Equal("G G does not work while you type in a box. Esc leaves the box.", ChordNotice.Text(boxKept));

        // Outside a file list.
        Press("ctrl+k");
        var outside = Assert.IsType<KeyOutcome.NotBound>(Press("v"));
        Assert.Equal("editor.openMarkdownPreview", outside.Elsewhere?.Command);
        Assert.Equal("Ctrl+K V works only in a file list.", ChordNotice.Text(outside));

        Press("ctrl+k");
        var nowhere = Assert.IsType<KeyOutcome.NotBound>(Press("x"));
        Assert.Null(nowhere.Elsewhere);
        Assert.Equal("Ctrl+K X is not bound to a command.", ChordNotice.Text(nowhere));
    }

    [Fact]
    public void Esc_during_a_chord_s_wait_ends_the_wait_and_runs_nothing()
    {
        var changes = 0;
        _keys.PendingChanged += () => changes++;
        Press("ctrl+k");
        var cancelled = Assert.IsType<KeyOutcome.Cancelled>(Press("escape"));
        Assert.Equal(Combo("ctrl+k"), cancelled.First);
        Assert.Null(_keys.PendingFirst);
        Assert.Equal(2, changes);
        // From idle, Esc is the way out of an overlay again.
        Assert.Equal("overlay.close", Assert.IsType<KeyOutcome.Run>(Press("escape")).Command);
    }

    [Fact]
    public void The_palette_s_key_during_a_chord_s_wait_ends_the_wait_and_opens_the_palette()
    {
        Press("ctrl+k", Typing);
        var run = Assert.IsType<KeyOutcome.Run>(Press("ctrl+shift+p", Typing));
        Assert.Equal(("palette.show", "ctrl+shift+p"), (run.Command, run.Keys.ToString()));
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
    public void A_key_held_down_runs_its_command_once_unless_the_command_is_meant_to_repeat()
    {
        // The first press runs; Windows' repeats (WasKeyDown) of a toggle run nothing and go nowhere else.
        Assert.Equal("palette.show", Assert.IsType<KeyOutcome.Run>(Press("ctrl+shift+p")).Command);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("palette.show", Assert.IsType<KeyOutcome.Held>(_keys.OnKey(Combo("ctrl+shift+p"), Palette, repeat: true)).Command);
            Assert.Equal("view.toggleSidebar", Assert.IsType<KeyOutcome.Held>(_keys.OnKey(Combo("ctrl+b"), Nothing, repeat: true)).Command);
            Assert.Equal("tab.next", Assert.IsType<KeyOutcome.Run>(_keys.OnKey(Combo("ctrl+tab"), Files, repeat: true)).Command);
        }
        // A key nobody bound repeats as it always did: a letter typed, an arrow in the list.
        Assert.IsType<KeyOutcome.PassThrough>(_keys.OnKey(Combo("down"), Files, repeat: true));
        Assert.IsType<KeyOutcome.PassThrough>(_keys.OnKey(Combo("x"), Typing, repeat: true));
    }

    [Fact]
    public void The_commands_that_repeat_are_moving_through_tabs_rows_and_folders()
    {
        Assert.Equal(new[] { "edit.toggleSelection", "go.back", "go.forward", "go.up", "tab.next", "tab.previous" },
            ChordStateMachine.RepeatingCommands.Order(StringComparer.Ordinal));
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
    public void In_a_text_box_a_key_that_types_nothing_runs_and_the_immutable_tier_works_as_everywhere()
    {
        Assert.Equal("view.toggleSidebar", Assert.IsType<KeyOutcome.Run>(Press("ctrl+b", Typing)).Command);
        Assert.Equal("test.everywhere", Assert.IsType<KeyOutcome.Run>(Press("f2", Typing)).Command);
        Assert.Equal("overlay.close", Assert.IsType<KeyOutcome.Run>(Press("escape", Typing)).Command);
        Assert.Equal("palette.show", Assert.IsType<KeyOutcome.Run>(Press("ctrl+shift+p", Palette)).Command);
        Assert.Equal("test.inPalette", Assert.IsType<KeyOutcome.Run>(Press("f2", Palette)).Command);

        Assert.IsType<KeyOutcome.Pending>(Press("ctrl+k", Typing));
        Assert.Equal("keys.open", Assert.IsType<KeyOutcome.Run>(Press("ctrl+s", Typing)).Command);
        Assert.IsType<KeyOutcome.Pending>(Press("ctrl+k", Typing));
        Assert.Equal("workspace.switch", Assert.IsType<KeyOutcome.Run>(Press("ctrl+w", Typing)).Command);
    }

    [Fact]
    public void The_pane_s_own_boxes_run_the_pane_s_keys_that_type_nothing_and_other_boxes_do_not()
    {
        HashSet<string> paneBox = [KeyContexts.TextInput, KeyContexts.FilesView];
        Assert.Equal("file.copyToOtherPane", Assert.IsType<KeyOutcome.Run>(Press("f5", paneBox)).Command);
        // Tab moves on from the box, as in any box; in the list it is the pane's.
        Assert.IsType<KeyOutcome.PassThrough>(Press("tab", paneBox));
        Assert.IsType<KeyOutcome.Pending>(Press("ctrl+k", paneBox));
        Assert.Equal("editor.openMarkdownPreview", Assert.IsType<KeyOutcome.Run>(Press("v", paneBox)).Command);

        // A name typed in place, the palette's field: F5 stays with the box, and the box types no F5, so it passes on.
        Assert.IsType<KeyOutcome.PassThrough>(Press("f5", Typing));
        Assert.IsType<KeyOutcome.PassThrough>(Press("f5", Palette));
    }

    // docs/keybindings.md, "Contexts": which keys a text box keeps (stays) and which run their binding.
    [Theory]
    [InlineData("f5", false)]
    [InlineData("shift+f8", false)]
    [InlineData("ctrl+f3", false)]
    [InlineData("alt+f1", false)]
    [InlineData("f24", false)]
    [InlineData("ctrl+t", false)]
    [InlineData("ctrl+w", false)]
    [InlineData("ctrl+b", false)]
    [InlineData("ctrl+l", false)]
    [InlineData("ctrl+1", false)]
    [InlineData("ctrl+tab", false)]
    [InlineData("ctrl+shift+tab", false)]
    [InlineData("ctrl+enter", false)]
    [InlineData("ctrl+up", false)]
    [InlineData("ctrl+pagedown", false)]
    [InlineData("ctrl+space", false)]
    [InlineData("ctrl+numpadsubtract", false)]
    [InlineData("ctrl+backquote", false)]
    [InlineData("ctrl+shift+e", false)]
    [InlineData("alt+enter", false)]
    [InlineData("alt+left", false)]
    [InlineData("alt+up", false)]
    [InlineData("alt+backspace", false)]
    [InlineData("shift+alt+l", false)]
    [InlineData("ctrl+alt+up", false)]
    [InlineData("win+e", false)]
    [InlineData("a", true)]
    [InlineData("shift+a", true)]
    [InlineData("5", true)]
    [InlineData("shift+5", true)]
    [InlineData("space", true)]
    [InlineData("quote", true)]
    [InlineData("numpadadd", true)]
    [InlineData("enter", true)]
    [InlineData("shift+enter", true)]
    [InlineData("escape", true)]
    [InlineData("backspace", true)]
    [InlineData("delete", true)]
    [InlineData("shift+delete", true)]
    [InlineData("insert", true)]
    [InlineData("shift+insert", true)]
    [InlineData("home", true)]
    [InlineData("end", true)]
    [InlineData("pageup", true)]
    [InlineData("left", true)]
    [InlineData("shift+right", true)]
    [InlineData("up", true)]
    [InlineData("down", true)]
    [InlineData("tab", true)]
    [InlineData("shift+tab", true)]
    [InlineData("ctrl+a", true)]
    [InlineData("ctrl+c", true)]
    [InlineData("ctrl+v", true)]
    [InlineData("ctrl+x", true)]
    [InlineData("ctrl+z", true)]
    [InlineData("ctrl+y", true)]
    [InlineData("ctrl+insert", true)]
    [InlineData("ctrl+backspace", true)]
    [InlineData("ctrl+delete", true)]
    [InlineData("ctrl+left", true)]
    [InlineData("ctrl+right", true)]
    [InlineData("ctrl+home", true)]
    [InlineData("ctrl+end", true)]
    [InlineData("ctrl+shift+left", true)]
    [InlineData("ctrl+shift+right", true)]
    [InlineData("ctrl+shift+home", true)]
    [InlineData("ctrl+shift+end", true)]
    [InlineData("ctrl+shift+z", true)]
    [InlineData("ctrl+shift+c", true)]
    [InlineData("ctrl+shift+x", true)]
    [InlineData("ctrl+alt+p", true)]
    [InlineData("ctrl+alt+quote", true)]
    [InlineData("ctrl+alt+shift+4", true)]
    [InlineData("alt+1", true)]
    public void A_text_box_keeps_the_keys_that_type_or_edit_and_a_key_that_types_nothing_runs(string keys, bool stays)
    {
        Assert.Equal(stays, TextInputKeys.StaysWithBox(Combo(keys)));
        foreach (var when in new string?[] { null, KeyContexts.FilesView })
        {
            var machine = new ChordStateMachine(() => 0);
            machine.SetKeymap(Keymap.From(new KeymapData(1000, [new KeymapBinding(keys, "test.command", when)], [])));
            var outcome = machine.OnKey(Combo(keys), new HashSet<string> { KeyContexts.TextInput, KeyContexts.FilesView });
            if (stays)
            {
                Assert.IsType<KeyOutcome.PassThrough>(outcome);
            }
            else
            {
                Assert.Equal("test.command", Assert.IsType<KeyOutcome.Run>(outcome).Command);
            }
        }
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
