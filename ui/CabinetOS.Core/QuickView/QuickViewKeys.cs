using CabinetOS.Core.Keys;

namespace CabinetOS.Core.QuickView;

/// <summary>
/// The keys a viewer page may ask for while the panel shows a file (ADR 0023, decision 3), and the rule that decides
/// which it gets. The keyboard stays in the pane's list: the window hears every key and sends a page only the keys it
/// granted, and only when no binding of the keymap wants them. So the panel always wins.
/// </summary>
/// <remarks>
/// A page names keys in the core's key grammar, with one name of its own: <c>plus</c>, which ADR 0023 lists and the
/// grammar does not have. The window maps it to <c>equal</c>, the key Windows calls VK_OEM_PLUS, which carries the
/// plus sign on US and German keyboards; <c>shift+plus</c> is <c>shift+equal</c> (the "+" a US keyboard types). A press
/// reaches the page by the name it asked for.
/// </remarks>
public static class QuickViewKeys
{
    /// <summary>The page's name for the key the grammar calls <see cref="PlusKey"/>.</summary>
    public const string PlusName = "plus";

    /// <summary>The grammar's name of the key that carries the plus sign (VK_OEM_PLUS).</summary>
    public const string PlusKey = "equal";

    /// <summary>The most keys one page may hold at a time.</summary>
    public const int MaxKeys = 64;

    private static readonly HashSet<string> NamedKeys = new(StringComparer.Ordinal)
    {
        "left", "right", "pageup", "pagedown", "home", "end", PlusName, "minus", "comma", "period",
    };

    /// <summary>
    /// Whether a page may ask for <paramref name="pageKey"/>: a key of the fixed set (Left, Right, PageUp, PageDown,
    /// Home, End, a letter, a digit of the top row, plus, minus, comma, period), alone or with Shift, in lower case.
    /// Space, Esc, Enter, Up, Down, Tab and every combination with Ctrl, Alt or Win never are.
    /// </summary>
    public static bool IsAskable(string pageKey)
    {
        var key = pageKey.StartsWith("shift+", StringComparison.Ordinal) ? pageKey["shift+".Length..] : pageKey;
        return NamedKeys.Contains(key) || (key.Length == 1 && (key[0] is >= 'a' and <= 'z' || key[0] is >= '0' and <= '9'));
    }

    /// <summary>The combination a page key means, or null when a page may not ask for it.</summary>
    public static KeyCombo? ToCombo(string pageKey)
    {
        if (!IsAskable(pageKey))
        {
            return null;
        }
        var shift = pageKey.StartsWith("shift+", StringComparison.Ordinal);
        var key = shift ? pageKey["shift+".Length..] : pageKey;
        return new KeyCombo(shift ? KeyModifiers.Shift : KeyModifiers.None, key == PlusName ? PlusKey : key);
    }

    /// <summary>
    /// The keys of <paramref name="asked"/> the window grants: each one a page may ask for, that the keymap does not
    /// bind in <c>filesView</c> or everywhere and that starts no chord bound in either. In the order asked, each once,
    /// at most <see cref="MaxKeys"/>.
    /// </summary>
    public static IReadOnlyList<string> Grant(IEnumerable<string> asked, Keymap keymap)
    {
        var granted = new List<string>();
        foreach (var key in asked)
        {
            if (granted.Count < MaxKeys && !granted.Contains(key) && ToCombo(key) is { } combo && !Bound(combo, keymap))
            {
                granted.Add(key);
            }
        }
        return granted;
    }

    /// <summary>
    /// The page key of <paramref name="granted"/> that <paramref name="combo"/> presses, or null. The grant is checked
    /// again at each press, so a binding the user adds while the panel is open wins at once.
    /// </summary>
    public static string? PressedKey(KeyCombo combo, IReadOnlyList<string> granted, Keymap keymap)
    {
        if (Bound(combo, keymap))
        {
            return null;
        }
        foreach (var key in granted)
        {
            if (ToCombo(key) == combo)
            {
                return key;
            }
        }
        return null;
    }

    private static bool Bound(KeyCombo combo, Keymap keymap) =>
        keymap.Bindings.Any(b => b.When is null or KeyContexts.FilesView && b.Keys.First == combo);
}
