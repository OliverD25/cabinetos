using System.Collections.Frozen;

namespace CabinetOS.Core.Keys;

/// <summary>What the key state machine decided about one key press.</summary>
public abstract record KeyOutcome
{
    /// <summary>Run this command; the key is consumed.</summary>
    public sealed record Run(string Command, KeySequence Keys) : KeyOutcome;

    /// <summary>The first half of a chord; waiting for the second. The key is consumed.</summary>
    public sealed record Pending(KeyCombo First) : KeyOutcome;

    /// <summary>
    /// A chord's second half that completes nothing here: nothing runs, the key is consumed.
    /// <paramref name="Elsewhere"/> is the binding of these two keys that does not apply here
    /// (its context does not hold, or a text box has the keyboard), or null when there is none.
    /// </summary>
    public sealed record NotBound(KeyCombo First, KeyCombo Second, Binding? Elsewhere = null) : KeyOutcome;

    /// <summary>No binding: the key goes on to whatever has focus.</summary>
    public sealed record PassThrough : KeyOutcome;

    /// <summary>
    /// Windows repeats a key held down, and <paramref name="Command"/> runs once per press
    /// (<see cref="ChordStateMachine.RepeatingCommands"/>): nothing runs, the key is consumed.
    /// </summary>
    public sealed record Held(string Command) : KeyOutcome;
}

/// <summary>What the status bar says about a chord's second half that ran nothing (docs/keybindings.md, "Chords").</summary>
public static class ChordNotice
{
    /// <summary>
    /// The notice for <paramref name="outcome"/>: where a chord bound in another context works,
    /// so a chord that is bound is never reported as not bound.
    /// </summary>
    public static string Text(KeyOutcome.NotBound outcome)
    {
        var keys = $"{outcome.First.ToDisplay()} {outcome.Second.ToDisplay()}";
        if (outcome.Elsewhere is not { } binding)
        {
            return $"{keys} is not bound to a command.";
        }
        return binding.When switch
        {
            // No context of its own: a text box had the keyboard and kept the chord's first key, which types there.
            null => $"{keys} does not work while you type in a box. Esc leaves the box.",
            KeyContexts.FilesView => $"{keys} works only in a file list.",
            KeyContexts.PaletteOpen => $"{keys} works only in the command palette.",
            KeyContexts.TerminalFocus => $"{keys} works only in the terminal.",
            var context => $"{keys} works only where {context} holds.",
        };
    }
}

/// <summary>
/// Which keys a text box keeps while it has the keyboard (docs/keybindings.md, "Contexts"): the keys that type
/// or edit there. Any other key types nothing in a box, so its binding runs.
/// </summary>
public static class TextInputKeys
{
    // The keys a box edits with while Ctrl is held, and with Shift too (which selects as it goes): select all,
    // copy (Ctrl+Insert as well), paste, cut, undo, redo, a word back or forward, the start and the end.
    private static readonly FrozenSet<string> CtrlEditing = FrozenSet.ToFrozenSet(
        ["a", "c", "v", "x", "z", "y", "insert", "backspace", "delete", "left", "right", "home", "end"]);

    /// <summary>Whether a text box keeps <paramref name="combo"/>: true for a key that types or edits there.</summary>
    public static bool StaysWithBox(KeyCombo combo)
    {
        if (combo.Key.Length > 1 && combo.Key[0] == 'f' && char.IsAsciiDigit(combo.Key[1]))
        {
            return false;
        }
        if ((combo.Modifiers & KeyModifiers.Win) != 0)
        {
            return false;
        }
        var ctrl = (combo.Modifiers & KeyModifiers.Ctrl) != 0;
        var alt = (combo.Modifiers & KeyModifiers.Alt) != 0;
        return (ctrl, alt) switch
        {
            // Alone or with Shift every key types or edits: characters, Space, Enter, Esc, Backspace, Delete, Insert,
            // Home, End, PageUp, PageDown, the arrows, Tab.
            (false, false) => true,
            // Ctrl and Alt together are AltGr, which types characters on many layouts.
            (true, true) => KeyNames.IsCharacter(combo.Key),
            (true, false) => CtrlEditing.Contains(combo.Key),
            // Alt with the keypad's digits types a character by its code, and the grammar names those digits as the row's.
            (false, true) => combo.Key is [>= '0' and <= '9'],
        };
    }
}

/// <summary>
/// The UI's key state machine (docs/keybindings.md, "Chords"; brief §7). It
/// knows no key codes and no clock of its own, so it is tested without a window.
/// </summary>
/// <remarks>
/// Which bindings apply: one with a <c>when</c> applies while that context
/// holds, one without applies everywhere, and when both match the same keys
/// the one with the <c>when</c> wins. While a text box has focus
/// (<c>textInput</c>), the box keeps the keys that type or edit
/// (<see cref="TextInputKeys"/>), so typing is never swallowed by a shortcut:
/// a binding without <c>when</c>, or one of <c>filesView</c> (a pane's own
/// boxes hold both contexts), applies there only to a key that types nothing,
/// and a chord is judged by its first half. The Immutable System Tier applies
/// everywhere.
/// </remarks>
public sealed class ChordStateMachine(Func<long> nowMilliseconds)
{
    private static readonly KeyOutcome.PassThrough PassThroughOutcome = new();

    /// <summary>
    /// The commands a key held down runs again on each of Windows' repeats (docs/keybindings.md, "Keys held
    /// down"): the next and the previous tab, Insert (it marks the row and moves the cursor down), and Back,
    /// Forward and Up, which move through folders as the arrows move through rows. Any other command runs once
    /// per press, so a toggle held a moment too long does not flicker.
    /// </summary>
    public static readonly IReadOnlySet<string> RepeatingCommands = FrozenSet.ToFrozenSet(
        ["tab.next", "tab.previous", "edit.toggleSelection", "go.back", "go.forward", "go.up"], StringComparer.Ordinal);

    private Keymap _keymap = Keymap.Empty;
    private KeyCombo? _pending;
    private long _pendingSince;

    /// <summary>Raised when a chord starts waiting or stops waiting.</summary>
    public event Action? PendingChanged;

    /// <summary>The first half of the chord being waited for, if any.</summary>
    public KeyCombo? PendingFirst => _pending;

    /// <summary>The keymap in use.</summary>
    public Keymap Keymap => _keymap;

    /// <summary>Uses a new keymap and drops any wait.</summary>
    public void SetKeymap(Keymap keymap)
    {
        _keymap = keymap;
        ClearPending();
    }

    /// <summary>
    /// Handles one combination pressed while <paramref name="contexts"/> hold.
    /// <paramref name="repeat"/> says Windows repeats a key held down.
    /// </summary>
    public KeyOutcome OnKey(KeyCombo combo, IReadOnlySet<string> contexts, bool repeat = false)
    {
        ExpireIfDue();
        if (_pending is { } first)
        {
            if (repeat && combo == first)
            {
                // The first half is still held: the same press, not the second half. The wait starts again.
                _pendingSince = nowMilliseconds();
                PendingChanged?.Invoke();
                return new KeyOutcome.Pending(first);
            }
            ClearPending();
            var chord = Best(combo, contexts, b => b.Keys.IsChord && b.Keys.First == first && b.Keys.Second == combo);
            return chord is null
                ? new KeyOutcome.NotBound(first, combo, _keymap.Bindings.FirstOrDefault(b => b.Keys.IsChord && b.Keys.First == first && b.Keys.Second == combo))
                : new KeyOutcome.Run(chord.Command, chord.Keys);
        }

        if (_keymap.Bindings.Any(b => b.Keys.IsChord && b.Keys.First == combo && Applies(b, contexts)))
        {
            _pending = combo;
            _pendingSince = nowMilliseconds();
            PendingChanged?.Invoke();
            return new KeyOutcome.Pending(combo);
        }

        var single = Best(combo, contexts, b => !b.Keys.IsChord && b.Keys.First == combo);
        if (single is null)
        {
            return PassThroughOutcome;
        }
        return repeat && !RepeatingCommands.Contains(single.Command)
            ? new KeyOutcome.Held(single.Command)
            : new KeyOutcome.Run(single.Command, single.Keys);
    }

    /// <summary>
    /// Ends a wait whose window has passed; the window's timer calls this.
    /// Returns whether a wait ended.
    /// </summary>
    public bool ExpireIfDue()
    {
        if (_pending is null || nowMilliseconds() - _pendingSince <= _keymap.ChordWindowMs)
        {
            return false;
        }
        ClearPending();
        return true;
    }

    /// <summary>Drops any wait, for example when the window loses focus.</summary>
    public void Reset() => ClearPending();

    private Binding? Best(KeyCombo combo, IReadOnlySet<string> contexts, Func<Binding, bool> matches)
    {
        Binding? everywhere = null;
        foreach (var binding in _keymap.Bindings)
        {
            if (!matches(binding) || !Applies(binding, contexts))
            {
                continue;
            }
            if (binding.When is not null)
            {
                return binding;
            }
            everywhere ??= binding;
        }
        return everywhere;
    }

    private bool Applies(Binding binding, IReadOnlySet<string> contexts)
    {
        if (binding.When is not null && !contexts.Contains(binding.When))
        {
            return false;
        }
        if (!contexts.Contains(KeyContexts.TextInput) || _keymap.Immutable.Contains(binding.Command))
        {
            return true;
        }
        // The box keeps its typing and editing keys from the bindings without a context and from the pane's (its find box
        // and its address box are the pane's too). A binding of a context that holds only around a box (the palette's F2)
        // is meant for it. A chord goes by its first half: once that has started the wait, the second belongs to the chord.
        return binding.When is not (null or KeyContexts.FilesView) || !TextInputKeys.StaysWithBox(binding.Keys.First);
    }

    private void ClearPending()
    {
        if (_pending is null)
        {
            return;
        }
        _pending = null;
        PendingChanged?.Invoke();
    }
}
