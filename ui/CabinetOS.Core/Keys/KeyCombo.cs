using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace CabinetOS.Core.Keys;

/// <summary>Modifier keys, as bits in the normalized order ctrl, shift, alt, win.</summary>
[Flags]
public enum KeyModifiers : byte
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    Win = 8,
}

/// <summary>
/// One combination: modifiers held while one key is pressed, in the grammar of
/// docs/keybindings.md ("Writing keys"). <see cref="Key"/> is a key name in
/// normal form: <c>a</c>–<c>z</c>, <c>0</c>–<c>9</c>, <c>f1</c>–<c>f24</c>, or a named key.
/// </summary>
public readonly record struct KeyCombo(KeyModifiers Modifiers, string Key)
{
    /// <summary>Parses <c>ctrl+shift+p</c> in any case or order, aliases included.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out KeyCombo? combo)
    {
        combo = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var modifiers = KeyModifiers.None;
        string? key = null;
        var parts = text.Trim().ToLowerInvariant().Split('+');
        foreach (var part in parts)
        {
            if (part.Length == 0)
            {
                return false;
            }
            var modifier = part switch
            {
                "ctrl" or "control" => KeyModifiers.Ctrl,
                "shift" => KeyModifiers.Shift,
                "alt" => KeyModifiers.Alt,
                "win" or "meta" => KeyModifiers.Win,
                _ => KeyModifiers.None,
            };
            if (modifier != KeyModifiers.None)
            {
                if ((modifiers & modifier) != 0)
                {
                    return false;
                }
                modifiers |= modifier;
                continue;
            }
            if (key is not null || KeyNames.Normalize(part) is not { } normalized)
            {
                return false;
            }
            key = normalized;
        }
        if (key is null)
        {
            return false;
        }
        combo = new KeyCombo(modifiers, key);
        return true;
    }

    /// <summary>The normal form: <c>ctrl+shift+alt+win+key</c>, lower case.</summary>
    public override string ToString()
    {
        var text = new StringBuilder();
        if ((Modifiers & KeyModifiers.Ctrl) != 0) text.Append("ctrl+");
        if ((Modifiers & KeyModifiers.Shift) != 0) text.Append("shift+");
        if ((Modifiers & KeyModifiers.Alt) != 0) text.Append("alt+");
        if ((Modifiers & KeyModifiers.Win) != 0) text.Append("win+");
        return text.Append(Key).ToString();
    }

    /// <summary>What a keycap shows: <c>Ctrl+Shift+P</c>, <c>Ctrl+`</c>, <c>F5</c>.</summary>
    public string ToDisplay()
    {
        var text = new StringBuilder();
        if ((Modifiers & KeyModifiers.Ctrl) != 0) text.Append("Ctrl+");
        if ((Modifiers & KeyModifiers.Shift) != 0) text.Append("Shift+");
        if ((Modifiers & KeyModifiers.Alt) != 0) text.Append("Alt+");
        if ((Modifiers & KeyModifiers.Win) != 0) text.Append("Win+");
        return text.Append(KeyNames.Display(Key)).ToString();
    }
}

/// <summary>
/// A binding's keys: one combination, or a chord of two pressed one after the
/// other (<c>ctrl+k ctrl+s</c>).
/// </summary>
public sealed record KeySequence(KeyCombo First, KeyCombo? Second)
{
    /// <summary>Whether this is a two-step chord.</summary>
    public bool IsChord => Second is not null;

    /// <summary>Parses one or two combinations separated by spaces.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out KeySequence? sequence)
    {
        sequence = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || !KeyCombo.TryParse(parts[0], out var first))
        {
            return false;
        }
        KeyCombo? second = null;
        if (parts.Length == 2)
        {
            if (!KeyCombo.TryParse(parts[1], out var parsed))
            {
                return false;
            }
            second = parsed;
        }
        sequence = new KeySequence(first.Value, second);
        return true;
    }

    /// <summary>The normal form, one space between the two combinations of a chord.</summary>
    public override string ToString() => Second is { } second ? $"{First} {second}" : First.ToString();

    /// <summary>One keycap text per combination: <c>["Ctrl+K", "Ctrl+S"]</c>.</summary>
    public IReadOnlyList<string> DisplayParts() =>
        Second is { } second ? [First.ToDisplay(), second.ToDisplay()] : [First.ToDisplay()];
}
