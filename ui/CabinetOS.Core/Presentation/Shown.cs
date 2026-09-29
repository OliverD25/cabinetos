namespace CabinetOS.Core.Presentation;

/// <summary>
/// The value one of a row's elements shows now, so the row sets a value only
/// when it differs (docs/ui.md, "Scrolling"). WinUI lays a text out again
/// even when it is set to the same string; skipping equal values saved a
/// quarter of the UI thread's work while scrolling 100,000 entries.
/// </summary>
public struct Shown<T>
{
    private T _value;
    private bool _known;

    /// <summary>Takes <paramref name="value"/>: true when it differs from the value shown (the caller then sets it).</summary>
    public bool Take(T value)
    {
        if (_known && EqualityComparer<T>.Default.Equals(_value, value))
        {
            return false;
        }
        _value = value;
        _known = true;
        return true;
    }

    /// <summary>Forgets the value shown: the next <see cref="Take"/> is true (the element was changed another way).</summary>
    public void Forget() => _known = false;
}
