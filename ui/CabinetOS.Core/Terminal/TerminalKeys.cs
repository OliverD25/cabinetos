using CabinetOS.Core.Keys;

namespace CabinetOS.Core.Terminal;

/// <summary>
/// Which keys pressed in the terminal still reach the window. A shell and the
/// programs in it need nearly every key (Esc for vim, Ctrl+K to cut a line,
/// Tab to complete), so the terminal is like a text box, only stricter: the
/// page passes on a single combination only when it is bound with
/// <c>when: terminalFocus</c>, or bound (in any context) to
/// <c>palette.show</c> or <c>view.toggleTerminal</c>, the ways back out.
/// Chords are never passed on: their first half is a shell key.
/// </summary>
public static class TerminalKeys
{
    /// <summary>
    /// The commands the window runs for keys pressed in a web page, by
    /// combination (<c>ctrl+shift+p</c>): in the terminal (<paramref name="context"/>
    /// <c>terminalFocus</c>, whose own bindings count too), or in a Tool
    /// Extension (null: the ways out only).
    /// </summary>
    public static IReadOnlyDictionary<string, string> PassKeys(Keymap keymap, string? context = KeyContexts.TerminalFocus)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var binding in keymap.Bindings)
        {
            if (binding.Keys.IsChord)
            {
                continue;
            }
            var combo = binding.Keys.First.ToString();
            if (context is not null && binding.When == context)
            {
                // The more specific binding wins, as everywhere (keybindings.md, "Contexts").
                keys[combo] = binding.Command;
            }
            else if (binding.When is null && binding.Command is "palette.show" or "view.toggleTerminal")
            {
                keys.TryAdd(combo, binding.Command);
            }
        }
        return keys;
    }
}
