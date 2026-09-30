using CabinetOS.Core.Keys;

namespace CabinetOS.Tests;

/// <summary>The key grammar of docs/keybindings.md, as the UI reads and shows it.</summary>
public class KeyTests
{
    [Theory]
    [InlineData("ctrl+shift+p", "ctrl+shift+p")]
    [InlineData("Shift+Ctrl+P", "ctrl+shift+p")]
    [InlineData("win+alt+shift+ctrl+x", "ctrl+shift+alt+win+x")]
    [InlineData("Ctrl+K  Ctrl+S", "ctrl+k ctrl+s")]
    [InlineData("control+`", "ctrl+backquote")]
    [InlineData("meta+esc", "win+escape")]
    [InlineData("pgdn", "pagedown")]
    [InlineData("ctrl+=", "ctrl+equal")]
    [InlineData("F24", "f24")]
    [InlineData("tab", "tab")]
    [InlineData("numpadadd", "numpadadd")]
    [InlineData("Ctrl+NumPad_Subtract", "ctrl+numpadsubtract")]
    [InlineData("alt+numpad_add", "alt+numpadadd")]
    [InlineData("numpad_multiply", "numpadmultiply")]
    [InlineData("numpad_divide", "numpaddivide")]
    [InlineData("numpad_decimal", "numpaddecimal")]
    public void Keys_are_read_in_any_case_and_order_and_normalized(string text, string normal)
    {
        Assert.True(KeySequence.TryParse(text, out var keys));
        Assert.Equal(normal, keys.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ctrl+nope")]
    [InlineData("ctrl+ctrl+a")]
    [InlineData("ctrl+")]
    [InlineData("ctrl")]
    [InlineData("a+b")]
    [InlineData("f0")]
    [InlineData("f25")]
    [InlineData("ctrl+k ctrl+s ctrl+x")]
    [InlineData("numpad0")]
    [InlineData("numpad_0")]
    public void Keys_outside_the_grammar_are_refused(string text) => Assert.False(KeySequence.TryParse(text, out _));

    [Theory]
    [InlineData("ctrl+shift+p", new[] { "Ctrl+Shift+P" })]
    [InlineData("ctrl+backquote", new[] { "Ctrl+`" })]
    [InlineData("f5", new[] { "F5" })]
    [InlineData("ctrl+k ctrl+s", new[] { "Ctrl+K", "Ctrl+S" })]
    [InlineData("shift+pageup", new[] { "Shift+PageUp" })]
    [InlineData("escape", new[] { "Esc" })]
    [InlineData("numpadadd", new[] { "Num +" })]
    [InlineData("ctrl+numpadsubtract", new[] { "Ctrl+Num -" })]
    [InlineData("numpadmultiply", new[] { "Num *" })]
    [InlineData("numpaddivide", new[] { "Num /" })]
    [InlineData("alt+numpaddecimal", new[] { "Alt+Num ." })]
    public void Keycaps_show_one_part_per_combination(string text, string[] parts)
    {
        Assert.True(KeySequence.TryParse(text, out var keys));
        Assert.Equal(parts, keys.DisplayParts());
    }

    [Theory]
    [InlineData(0x41, "a")]
    [InlineData(0x5A, "z")]
    [InlineData(0x30, "0")]
    [InlineData(0x69, "9")]
    [InlineData(0x70, "f1")]
    [InlineData(0x87, "f24")]
    [InlineData(0xC0, "backquote")]
    [InlineData(0x21, "pageup")]
    [InlineData(0x0D, "enter")]
    [InlineData(0x1B, "escape")]
    [InlineData(0xBB, "equal")]
    [InlineData(0xDE, "quote")]
    [InlineData(0x6B, "numpadadd")]
    [InlineData(0x6D, "numpadsubtract")]
    [InlineData(0x6A, "numpadmultiply")]
    [InlineData(0x6F, "numpaddivide")]
    [InlineData(0x6E, "numpaddecimal")]
    // The keypad's digits are the plain digits (Num Lock on).
    [InlineData(0x60, "0")]
    [InlineData(0x65, "5")]
    public void Virtual_keys_map_to_grammar_names(int virtualKey, string name) => Assert.Equal(name, KeyNames.FromVirtualKey(virtualKey));

    [Theory]
    [InlineData("t", 0x54)]
    [InlineData("backquote", 0xC0)]
    [InlineData("tab", 0x09)]
    [InlineData("f10", 0x79)]
    [InlineData("numpaddivide", 0x6F)]
    // The main keyboard's digit, not the keypad's.
    [InlineData("1", 0x31)]
    public void A_key_name_gives_back_the_virtual_key_that_makes_it(string name, int virtualKey)
    {
        Assert.Equal(virtualKey, KeyNames.VirtualKeyFor(name));
        Assert.Equal(name, KeyNames.FromVirtualKey(virtualKey));
    }

    [Fact]
    public void A_name_the_grammar_does_not_have_gives_no_virtual_key() => Assert.Null(KeyNames.VirtualKeyFor("nope"));

    [Fact]
    public void A_keypad_key_makes_a_combination_the_keymap_can_hold()
    {
        Assert.Equal("ctrl+numpadadd", KeyNames.ComboFor(0x6B, KeyModifiers.Ctrl).ToString());
        Assert.Equal("Alt+Num -", KeyNames.ComboFor(0x6D, KeyModifiers.Alt)!.Value.ToDisplay());
        Assert.Equal("numpaddivide", KeyNames.VirtualKeyNames[0x6F]);
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    [InlineData(0x12)]
    [InlineData(0x5B)]
    [InlineData(0xA3)]
    public void Modifier_keys_are_not_keys(int virtualKey)
    {
        Assert.True(KeyNames.IsModifier(virtualKey));
        Assert.Null(KeyNames.FromVirtualKey(virtualKey));
    }

    [Fact]
    public void Keys_without_a_grammar_name_map_to_nothing()
    {
        Assert.Null(KeyNames.FromVirtualKey(0x2C));
        Assert.Null(KeyNames.FromVirtualKey(0xAD));
        Assert.Null(KeyNames.ComboFor(0x2C, KeyModifiers.Ctrl));
        Assert.Equal("ctrl+shift+p", KeyNames.ComboFor(0x50, KeyModifiers.Ctrl | KeyModifiers.Shift).ToString());
    }

    [Fact]
    public void A_palette_row_shows_the_first_binding_and_counts_the_others()
    {
        var several = BindingSummary.Of(["ctrl+k v", "no+such+keys", "f2", "ctrl+shift+m"]);
        Assert.Equal("ctrl+k v", several.First!.ToString());
        Assert.Equal((2, "+2"), (several.Others, several.MoreText));
        Assert.Equal("Ctrl+K then V, F2, Ctrl+Shift+M", several.All);

        var one = BindingSummary.Of(["f5"]);
        Assert.Equal((0, null, "F5"), (one.Others, one.MoreText, one.All));

        var none = BindingSummary.Of([]);
        Assert.Null(none.First);
        Assert.Null(none.MoreText);
    }
}
