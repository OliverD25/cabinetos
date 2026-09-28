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
    public void Keys_outside_the_grammar_are_refused(string text) => Assert.False(KeySequence.TryParse(text, out _));

    [Theory]
    [InlineData("ctrl+shift+p", new[] { "Ctrl+Shift+P" })]
    [InlineData("ctrl+backquote", new[] { "Ctrl+`" })]
    [InlineData("f5", new[] { "F5" })]
    [InlineData("ctrl+k ctrl+s", new[] { "Ctrl+K", "Ctrl+S" })]
    [InlineData("shift+pageup", new[] { "Shift+PageUp" })]
    [InlineData("escape", new[] { "Esc" })]
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
    public void Virtual_keys_map_to_grammar_names(int virtualKey, string name) => Assert.Equal(name, KeyNames.FromVirtualKey(virtualKey));

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
