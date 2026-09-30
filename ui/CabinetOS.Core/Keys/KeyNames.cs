using System.Collections.Frozen;
using System.Globalization;

namespace CabinetOS.Core.Keys;

/// <summary>
/// Key names of the grammar (docs/keybindings.md) and the Windows virtual-key
/// codes that produce them.
/// </summary>
public static class KeyNames
{
    private static readonly FrozenSet<string> Named = FrozenSet.ToFrozenSet(
    [
        "escape", "enter", "tab", "space", "backspace", "delete", "insert", "home", "end",
        "pageup", "pagedown", "up", "down", "left", "right", "backquote", "comma", "period",
        "slash", "minus", "equal", "bracketleft", "bracketright", "backslash", "semicolon", "quote",
        "numpadadd", "numpadsubtract", "numpadmultiply", "numpaddivide", "numpaddecimal",
    ]);

    private static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>
    {
        ["esc"] = "escape",
        ["return"] = "enter",
        ["del"] = "delete",
        ["ins"] = "insert",
        ["pgup"] = "pageup",
        ["pgdn"] = "pagedown",
        ["`"] = "backquote",
        [","] = "comma",
        ["."] = "period",
        ["/"] = "slash",
        ["-"] = "minus",
        ["="] = "equal",
        ["["] = "bracketleft",
        ["]"] = "bracketright",
        ["\\"] = "backslash",
        [";"] = "semicolon",
        ["'"] = "quote",
        // VS Code's names of the keypad's operators.
        ["numpad_add"] = "numpadadd",
        ["numpad_subtract"] = "numpadsubtract",
        ["numpad_multiply"] = "numpadmultiply",
        ["numpad_divide"] = "numpaddivide",
        ["numpad_decimal"] = "numpaddecimal",
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, string> DisplayNames = new Dictionary<string, string>
    {
        ["escape"] = "Esc",
        ["enter"] = "Enter",
        ["tab"] = "Tab",
        ["space"] = "Space",
        ["backspace"] = "Backspace",
        ["delete"] = "Delete",
        ["insert"] = "Insert",
        ["home"] = "Home",
        ["end"] = "End",
        ["pageup"] = "PageUp",
        ["pagedown"] = "PageDown",
        ["up"] = "Up",
        ["down"] = "Down",
        ["left"] = "Left",
        ["right"] = "Right",
        ["backquote"] = "`",
        ["comma"] = ",",
        ["period"] = ".",
        ["slash"] = "/",
        ["minus"] = "-",
        ["equal"] = "=",
        ["bracketleft"] = "[",
        ["bracketright"] = "]",
        ["backslash"] = "\\",
        ["semicolon"] = ";",
        ["quote"] = "'",
        ["numpadadd"] = "Num +",
        ["numpadsubtract"] = "Num -",
        ["numpadmultiply"] = "Num *",
        ["numpaddivide"] = "Num /",
        ["numpaddecimal"] = "Num .",
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<int, string> VirtualKeys = BuildVirtualKeys();

    /// <summary>The normal form of a key name, or null when it is not one.</summary>
    public static string? Normalize(string name)
    {
        var lower = name.ToLowerInvariant();
        if (Aliases.TryGetValue(lower, out var canonical))
        {
            lower = canonical;
        }
        if (lower.Length == 1 && (lower[0] is >= 'a' and <= 'z' || lower[0] is >= '0' and <= '9'))
        {
            return lower;
        }
        if (lower.Length is 2 or 3 && lower[0] == 'f' && lower[1] != '0'
            && int.TryParse(lower.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number is >= 1 and <= 24)
        {
            return lower;
        }
        return Named.Contains(lower) ? lower : null;
    }

    /// <summary>What a keycap shows for a normalized key name: <c>A</c>, <c>F5</c>, <c>PageUp</c>, <c>`</c>.</summary>
    public static string Display(string key) =>
        DisplayNames.TryGetValue(key, out var display) ? display : key.ToUpperInvariant();

    /// <summary>
    /// The key name for a Windows virtual-key code, or null for a key the
    /// grammar has no name for (modifiers included).
    /// </summary>
    public static string? FromVirtualKey(int virtualKey) =>
        VirtualKeys.TryGetValue(virtualKey, out var name) ? name : null;

    /// <summary>
    /// The virtual-key code that makes the key name <paramref name="name"/>
    /// (a digit's is the main keyboard's, not the keypad's), or null for a
    /// name the grammar does not have.
    /// </summary>
    public static int? VirtualKeyFor(string name) =>
        VirtualKeys.Where(pair => pair.Value == name).Select(pair => (int?)pair.Key).Min();

    /// <summary>Every virtual-key code the grammar names, and its name (for the pages' key scripts).</summary>
    public static IReadOnlyDictionary<int, string> VirtualKeyNames => VirtualKeys;

    /// <summary>
    /// Whether the key named <paramref name="name"/> types a character when pressed alone or with Shift:
    /// a letter, a digit, the space, a punctuation key or a keypad operator.
    /// </summary>
    public static bool IsCharacter(string name) =>
        name.Length == 1 || name is "space" or "backquote" or "comma" or "period" or "slash" or "minus" or "equal"
            or "bracketleft" or "bracketright" or "backslash" or "semicolon" or "quote"
            or "numpadadd" or "numpadsubtract" or "numpadmultiply" or "numpaddivide" or "numpaddecimal";

    /// <summary>Whether the virtual key is Ctrl, Shift, Alt or Win (either side).</summary>
    public static bool IsModifier(int virtualKey) =>
        virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    /// <summary>
    /// The combination a key press makes, or null for Ctrl, Shift, Alt or Win
    /// pressed alone and for a key the grammar has no name for. Such presses
    /// never reach the key state machine, so a chord keeps waiting while a
    /// modifier is pressed again between its two halves.
    /// </summary>
    public static KeyCombo? ComboFor(int virtualKey, KeyModifiers modifiers) =>
        IsModifier(virtualKey) || FromVirtualKey(virtualKey) is not { } name ? null : new KeyCombo(modifiers, name);

    private static FrozenDictionary<int, string> BuildVirtualKeys()
    {
        var keys = new Dictionary<int, string>
        {
            [0x08] = "backspace",
            [0x09] = "tab",
            [0x0D] = "enter",
            [0x1B] = "escape",
            [0x20] = "space",
            [0x21] = "pageup",
            [0x22] = "pagedown",
            [0x23] = "end",
            [0x24] = "home",
            [0x25] = "left",
            [0x26] = "up",
            [0x27] = "right",
            [0x28] = "down",
            [0x2D] = "insert",
            [0x2E] = "delete",
            // The keypad's operators (the same codes with Num Lock on or off).
            [0x6A] = "numpadmultiply",
            [0x6B] = "numpadadd",
            [0x6D] = "numpadsubtract",
            [0x6E] = "numpaddecimal",
            [0x6F] = "numpaddivide",
            [0xBA] = "semicolon",
            [0xBB] = "equal",
            [0xBC] = "comma",
            [0xBD] = "minus",
            [0xBE] = "period",
            [0xBF] = "slash",
            [0xC0] = "backquote",
            [0xDB] = "bracketleft",
            [0xDC] = "backslash",
            [0xDD] = "bracketright",
            [0xDE] = "quote",
        };
        for (var digit = 0; digit <= 9; digit++)
        {
            keys[0x30 + digit] = digit.ToString(CultureInfo.InvariantCulture);
            // The grammar has no separate numpad digits.
            keys[0x60 + digit] = digit.ToString(CultureInfo.InvariantCulture);
        }
        for (var letter = 0; letter < 26; letter++)
        {
            keys[0x41 + letter] = ((char)('a' + letter)).ToString();
        }
        for (var function = 1; function <= 24; function++)
        {
            keys[0x70 + function - 1] = "f" + function.ToString(CultureInfo.InvariantCulture);
        }
        return keys.ToFrozenDictionary();
    }
}
