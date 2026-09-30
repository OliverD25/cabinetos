using System.Globalization;
using CabinetOS.Core.Keys;

namespace CabinetOS.Core.Diagnostics;

/// <summary>One key press as heavy mode logs it: the message, the key's name (none for typed text) and the modifiers held.</summary>
public readonly record struct KeyLogEntry(string Message, string? Key, string Modifiers);

/// <summary>
/// How heavy mode logs a key press (docs/diagnostics.md, "Heavy mode"): by the key's name and
/// the modifiers held, but never the text typed into a box. A key that types a character while a
/// text box has the keyboard is logged as <c>text input</c> without the character, so the log
/// says the user typed, and how often, not what.
/// </summary>
public static class KeyLog
{
    /// <summary>The message of a key press that is logged by name.</summary>
    public const string KeyMessage = "key pressed";

    /// <summary>The message of a key that types a character into a text box.</summary>
    public const string TextMessage = "text input";

    /// <summary>What to log for the key <paramref name="virtualKey"/> pressed with <paramref name="modifiers"/>.</summary>
    /// <param name="virtualKey">The Windows virtual-key code.</param>
    /// <param name="modifiers">The modifiers held.</param>
    /// <param name="inTextBox">Whether a text box (a name, an address, the palette's field) has the keyboard.</param>
    public static KeyLogEntry Describe(int virtualKey, KeyModifiers modifiers, bool inTextBox)
    {
        var held = ModifierText(modifiers);
        return inTextBox && TypesCharacter(virtualKey, modifiers)
            ? new KeyLogEntry(TextMessage, null, held)
            : new KeyLogEntry(KeyMessage, NameOf(virtualKey), held);
    }

    /// <summary>
    /// Whether the key types a character: a letter, a digit, the space, a punctuation key or a
    /// keypad operator, with no Windows key held and neither Ctrl nor Alt alone (that is a
    /// shortcut). Ctrl and Alt together are AltGr, which many layouts type letters with.
    /// </summary>
    public static bool TypesCharacter(int virtualKey, KeyModifiers modifiers)
    {
        if (KeyNames.IsModifier(virtualKey) || (modifiers & KeyModifiers.Win) != 0)
        {
            return false;
        }
        var ctrl = (modifiers & KeyModifiers.Ctrl) != 0;
        var alt = (modifiers & KeyModifiers.Alt) != 0;
        if (ctrl != alt)
        {
            return false;
        }
        return KeyNames.FromVirtualKey(virtualKey) is { } name && KeyNames.IsCharacter(name);
    }

    /// <summary>The key's name in the grammar of docs/keybindings.md, <c>shift</c> and its kin for modifiers, or <c>vk_XX</c> for a key without a name.</summary>
    public static string NameOf(int virtualKey) => virtualKey switch
    {
        0x10 or 0xA0 or 0xA1 => "shift",
        0x11 or 0xA2 or 0xA3 => "ctrl",
        0x12 or 0xA4 or 0xA5 => "alt",
        0x5B or 0x5C => "win",
        _ => KeyNames.FromVirtualKey(virtualKey) ?? string.Create(CultureInfo.InvariantCulture, $"vk_{virtualKey:X2}"),
    };

    /// <summary>The modifiers held, as <c>ctrl+shift</c> (in the grammar's order), or an empty text.</summary>
    public static string ModifierText(KeyModifiers modifiers)
    {
        var parts = new List<string>(4);
        if ((modifiers & KeyModifiers.Ctrl) != 0)
        {
            parts.Add("ctrl");
        }
        if ((modifiers & KeyModifiers.Shift) != 0)
        {
            parts.Add("shift");
        }
        if ((modifiers & KeyModifiers.Alt) != 0)
        {
            parts.Add("alt");
        }
        if ((modifiers & KeyModifiers.Win) != 0)
        {
            parts.Add("win");
        }
        return string.Join('+', parts);
    }
}
