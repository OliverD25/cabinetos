namespace CabinetOS.Core.Terminal;

/// <summary>Whether the window types a change-directory command into the shell, and why not.</summary>
public enum CwdSyncDecision
{
    /// <summary>Sync: send <c>terminal_sync_cwd</c>.</summary>
    Sync,

    /// <summary>A command line is half typed: a <c>cd</c> would land in the middle of it.</summary>
    SkipTyping,

    /// <summary>A full-screen program runs (the alternate screen: vim, less): it would get the line.</summary>
    SkipFullScreen,

    /// <summary>The shell is there already.</summary>
    SkipSameFolder,

    /// <summary>The shell has exited.</summary>
    SkipNotRunning,
}

/// <summary>
/// What the user has done in one session that matters for following the
/// active pane (docs/terminal.md, "Following the active pane": text already
/// on the prompt line stays in front of the typed command, and a program that
/// runs in the shell receives the line instead).
/// </summary>
public sealed class TypingTracker
{
    /// <summary>Whether keys went in since the last Enter or Ctrl+C: a line is being typed.</summary>
    public bool LinePending { get; private set; }

    /// <summary>Whether the terminal shows the alternate screen (a full-screen program).</summary>
    public bool FullScreen { get; set; }

    /// <summary>Notes keys the user sent to the shell.</summary>
    public void OnInput(string data)
    {
        // Focus reports (ESC [ I, ESC [ O) come from the terminal, not from the user.
        if (data.Length == 0 || data is "\u001b[I" or "\u001b[O")
        {
            return;
        }
        var end = data.LastIndexOfAny(['\r', '\u0003']);
        LinePending = end < data.Length - 1;
    }

    /// <summary>A synced <c>cd</c> ended with Enter: the line is empty again.</summary>
    public void OnSynced() => LinePending = false;

    /// <summary>
    /// The window typed paths at the prompt (Ctrl+P, <c>terminal_type_paths</c>):
    /// the line holds text now, for the user to go on typing, and a <c>cd</c>
    /// must not land in the middle of it.
    /// </summary>
    public void OnPathsTyped() => LinePending = true;
}

/// <summary>The rule for following the active pane, on its own so it is tested without a shell.</summary>
public static class CwdSyncRule
{
    /// <summary>Decides for <paramref name="folder"/>, the active pane's folder after the wait.</summary>
    public static CwdSyncDecision Decide(string folder, string? lastSynced, bool running, TypingTracker typing)
    {
        if (!running)
        {
            return CwdSyncDecision.SkipNotRunning;
        }
        if (string.Equals(folder.TrimEnd('\\'), lastSynced?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return CwdSyncDecision.SkipSameFolder;
        }
        if (typing.FullScreen)
        {
            return CwdSyncDecision.SkipFullScreen;
        }
        return typing.LinePending ? CwdSyncDecision.SkipTyping : CwdSyncDecision.Sync;
    }
}

/// <summary>
/// The last value set, once <paramref name="delayMilliseconds"/> passed
/// without a new one: the terminal's folder sync (300 ms) and the search
/// field (150 ms) wait for the user to stop.
/// </summary>
public sealed class Debouncer<T>(Func<long> nowMilliseconds, int delayMilliseconds)
    where T : class
{
    private T? _pending;
    private long _since;

    /// <summary>Whether a value waits.</summary>
    public bool HasPending => _pending is not null;

    /// <summary>A new value; the wait starts again.</summary>
    public void Set(T value)
    {
        _pending = value;
        _since = nowMilliseconds();
    }

    /// <summary>Drops the waiting value.</summary>
    public void Cancel() => _pending = null;

    /// <summary>How long until the value is due; 0 when due, -1 when nothing waits.</summary>
    public int MillisecondsUntilDue()
    {
        if (_pending is null)
        {
            return -1;
        }
        var wait = _since + delayMilliseconds - nowMilliseconds();
        return wait <= 0 ? 0 : (int)wait;
    }

    /// <summary>The value, when it is due; it is taken.</summary>
    public bool TryTake(out T value)
    {
        if (_pending is { } pending && MillisecondsUntilDue() == 0)
        {
            value = pending;
            _pending = null;
            return true;
        }
        value = null!;
        return false;
    }
}
