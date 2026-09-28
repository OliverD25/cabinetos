using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Files;

/// <summary>Whether the clipboard's paths were copied or cut.</summary>
public enum ClipboardMode
{
    /// <summary>Paste copies them.</summary>
    Copy,

    /// <summary>Paste moves them, once.</summary>
    Cut,
}

/// <summary>A paste: the job it starts.</summary>
public sealed record PastePlan(JobKind Kind, IReadOnlyList<string> Sources, string Destination);

/// <summary>
/// The in-app clipboard of paths (Ctrl+X, Ctrl+C, Ctrl+V). Pasting starts a
/// copy or move job in the core. Windows' clipboard gets the same paths as
/// text, one per line, so they can be pasted into a terminal or an editor;
/// when something else replaces that text, the in-app clipboard empties too,
/// so a paste never brings back files the user no longer has "on the clipboard".
/// </summary>
public sealed class FileClipboard
{
    /// <summary>Raised after every change.</summary>
    public event Action? Changed;

    /// <summary>The paths, in the order they were selected.</summary>
    public IReadOnlyList<string> Paths { get; private set; } = [];

    /// <summary>Copied or cut.</summary>
    public ClipboardMode Mode { get; private set; }

    /// <summary>Whether a paste has anything to do.</summary>
    public bool IsEmpty => Paths.Count == 0;

    /// <summary>The text Windows' clipboard holds for these paths: one per line.</summary>
    public string Text => string.Join("\r\n", Paths);

    /// <summary>Takes <paramref name="paths"/>; returns the text for Windows' clipboard.</summary>
    public string Set(IReadOnlyList<string> paths, ClipboardMode mode)
    {
        Paths = [.. paths];
        Mode = mode;
        Changed?.Invoke();
        return Text;
    }

    /// <summary>Empties the clipboard.</summary>
    public void Clear()
    {
        if (IsEmpty)
        {
            return;
        }
        Paths = [];
        Changed?.Invoke();
    }

    /// <summary>
    /// Windows' clipboard changed: keeps the paths only while it still holds
    /// their text. Returns whether they were kept.
    /// </summary>
    public bool KeepIfSystemTextIs(string? systemText)
    {
        if (IsEmpty || string.Equals(systemText, Text, StringComparison.Ordinal))
        {
            return !IsEmpty;
        }
        Clear();
        return false;
    }

    /// <summary>The job a paste into <paramref name="destination"/> starts, or null when empty.</summary>
    public PastePlan? Plan(string destination) =>
        IsEmpty ? null : new PastePlan(Mode == ClipboardMode.Cut ? JobKind.Move : JobKind.Copy, Paths, destination);

    /// <summary>After a paste started its job: cut paths move once, so they leave the clipboard.</summary>
    public void OnPasted()
    {
        if (Mode == ClipboardMode.Cut)
        {
            Clear();
        }
    }
}
