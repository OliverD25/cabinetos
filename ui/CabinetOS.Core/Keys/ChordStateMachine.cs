namespace CabinetOS.Core.Keys;

/// <summary>What the key state machine decided about one key press.</summary>
public abstract record KeyOutcome
{
    /// <summary>Run this command; the key is consumed.</summary>
    public sealed record Run(string Command, KeySequence Keys) : KeyOutcome;

    /// <summary>The first half of a chord; waiting for the second. The key is consumed.</summary>
    public sealed record Pending(KeyCombo First) : KeyOutcome;

    /// <summary>A chord's second half that completes nothing: nothing runs, the key is consumed.</summary>
    public sealed record NotBound(KeyCombo First, KeyCombo Second) : KeyOutcome;

    /// <summary>No binding: the key goes on to whatever has focus.</summary>
    public sealed record PassThrough : KeyOutcome;
}

/// <summary>
/// The UI's key state machine (docs/keybindings.md, "Chords"; brief §7). It
/// knows no key codes and no clock of its own, so it is tested without a window.
/// </summary>
/// <remarks>
/// Which bindings apply: one with a <c>when</c> applies while that context
/// holds, one without applies everywhere, and when both match the same keys
/// the one with the <c>when</c> wins. While a text box has focus
/// (<c>textInput</c>), text input takes precedence: a binding without
/// <c>when</c> then applies only when it belongs to the Immutable System Tier
/// (the palette, the way out of an overlay, the shortcut editor), so typing
/// is never swallowed by an ordinary shortcut.
/// </remarks>
public sealed class ChordStateMachine(Func<long> nowMilliseconds)
{
    private static readonly KeyOutcome.PassThrough PassThroughOutcome = new();

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

    /// <summary>Handles one combination pressed while <paramref name="contexts"/> hold.</summary>
    public KeyOutcome OnKey(KeyCombo combo, IReadOnlySet<string> contexts)
    {
        ExpireIfDue();
        if (_pending is { } first)
        {
            ClearPending();
            var chord = Best(combo, contexts, b => b.Keys.IsChord && b.Keys.First == first && b.Keys.Second == combo);
            return chord is null ? new KeyOutcome.NotBound(first, combo) : new KeyOutcome.Run(chord.Command, chord.Keys);
        }

        if (_keymap.Bindings.Any(b => b.Keys.IsChord && b.Keys.First == combo && Applies(b, contexts)))
        {
            _pending = combo;
            _pendingSince = nowMilliseconds();
            PendingChanged?.Invoke();
            return new KeyOutcome.Pending(combo);
        }

        var single = Best(combo, contexts, b => !b.Keys.IsChord && b.Keys.First == combo);
        return single is null ? PassThroughOutcome : new KeyOutcome.Run(single.Command, single.Keys);
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
        if (binding.When is not null)
        {
            return contexts.Contains(binding.When);
        }
        return !contexts.Contains(KeyContexts.TextInput) || _keymap.Immutable.Contains(binding.Command);
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
