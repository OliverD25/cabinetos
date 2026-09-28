using System.Collections.Frozen;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Keys;

/// <summary>The <c>when</c> contexts the UI decides (docs/keybindings.md, "Contexts").</summary>
public static class KeyContexts
{
    /// <summary>A file pane has focus.</summary>
    public const string FilesView = "filesView";

    /// <summary>The command palette is open.</summary>
    public const string PaletteOpen = "paletteOpen";

    /// <summary>A text box has focus.</summary>
    public const string TextInput = "textInput";

    /// <summary>The terminal has focus (no terminal pane yet).</summary>
    public const string TerminalFocus = "terminalFocus";
}

/// <summary>One binding of the compiled keymap, with its keys parsed.</summary>
public sealed record Binding(KeySequence Keys, string Command, string? When);

/// <summary>The keymap the core compiled (<c>keymap</c> reply or <c>keymap_changed</c>).</summary>
public sealed class Keymap
{
    /// <summary>A keymap with no bindings, used until the core answers.</summary>
    public static readonly Keymap Empty = new(1000, [], FrozenSet<string>.Empty);

    /// <summary>Creates a keymap from parsed bindings.</summary>
    public Keymap(int chordWindowMs, IReadOnlyList<Binding> bindings, IReadOnlySet<string> immutable)
    {
        ChordWindowMs = chordWindowMs;
        Bindings = bindings;
        Immutable = immutable;
    }

    /// <summary>How long the second half of a chord may take, in milliseconds.</summary>
    public int ChordWindowMs { get; }

    /// <summary>Every binding in effect, grouped by command in registry order.</summary>
    public IReadOnlyList<Binding> Bindings { get; }

    /// <summary>The commands of the Immutable System Tier.</summary>
    public IReadOnlySet<string> Immutable { get; }

    /// <summary>
    /// Parses the core's keymap. The core sends only the normal form, so a
    /// binding that does not parse is a bug on one side: it is skipped and logged.
    /// </summary>
    public static Keymap From(KeymapData data)
    {
        var bindings = new List<Binding>(data.Bindings.Count);
        foreach (var binding in data.Bindings)
        {
            if (KeySequence.TryParse(binding.Keys, out var keys))
            {
                bindings.Add(new Binding(keys, binding.Command, binding.When));
            }
            else
            {
                Diag.Warn("cabinetos_ui::keys", "a binding from the core does not parse; skipped",
                    new LogField("keys", binding.Keys), new LogField("command", binding.Command));
            }
        }
        return new Keymap((int)data.ChordWindowMs, bindings, data.Immutable.ToFrozenSet(StringComparer.Ordinal));
    }

    /// <summary>The first binding of <paramref name="command"/>, if it has any.</summary>
    public Binding? FirstFor(string command) => Bindings.FirstOrDefault(b => b.Command == command);
}
