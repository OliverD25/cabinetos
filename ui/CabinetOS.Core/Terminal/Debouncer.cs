namespace CabinetOS.Core.Terminal;

/// <summary>
/// The last value set, once <paramref name="delayMilliseconds"/> passed
/// without a new one: the search field (150 ms) waits for the user to stop.
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
