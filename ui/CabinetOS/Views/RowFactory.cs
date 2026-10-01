using System.Diagnostics;
using CabinetOS.Core.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CabinetOS.Views;

/// <summary>
/// A pane's rows for its <see cref="ItemsRepeater"/>: rows the repeater lets
/// go wait here and are handed out again, and rows are made ahead while the
/// window is idle (<see cref="MakeAhead"/>). Without them the first PageDown
/// after a folder opened made a page of rows from the template, a frame of
/// 60 to 77 ms (docs/ui.md, "Scrolling").
/// </summary>
internal sealed class RowFactory(DataTemplate template, DispatcherQueue queue) : IElementFactory
{
    // Rows made in one idle moment: a few milliseconds, so input and frames go first.
    private const double SliceMs = 4;

    private readonly Stack<UIElement> _free = new();
    private int _ahead;
    private int _wanted;
    private bool _making;

    // Set when the window closes: a step still waiting in the dispatcher then runs while XAML is taken down, and
    // DataTemplate.LoadContent fails with an access violation (0xC0000005) that ends the process with a crash report.
    // On a busy machine the idle steps have not finished when the window closes, so every run of a test beside a load saw it.
    private static volatile bool _windowClosing;

    /// <summary>The window is closing: no row is made ahead from now on.</summary>
    public static void WindowClosing() => _windowClosing = true;

    /// <summary>Rows made so far, by the repeater's need and ahead of it.</summary>
    public int Made { get; private set; }

    /// <summary>
    /// Makes rows until <paramref name="rows"/> were made ahead in all, a few
    /// in each idle moment of the window (the dispatcher's low priority).
    /// </summary>
    public void MakeAhead(int rows)
    {
        _wanted = Math.Max(_wanted, rows);
        if (_ahead < _wanted && !_making && !_windowClosing)
        {
            _making = queue.TryEnqueue(DispatcherQueuePriority.Low, Step);
        }
    }

    private void Step()
    {
        if (_windowClosing)
        {
            _making = false;
            return;
        }
        var started = Stopwatch.GetTimestamp();
        while (_ahead < _wanted && Stopwatch.GetElapsedTime(started).TotalMilliseconds < SliceMs)
        {
            _free.Push(Make());
            _ahead++;
        }
        _making = _ahead < _wanted && queue.TryEnqueue(DispatcherQueuePriority.Low, Step);
        if (!_making)
        {
            Diag.Debug("cabinetos_ui::pane", "rows made ahead", new LogField("rows", _ahead), new LogField("made", Made));
        }
    }

    /// <inheritdoc/>
    public UIElement GetElement(ElementFactoryGetArgs args)
    {
        var element = _free.Count > 0 ? _free.Pop() : Make();
        if (element is FrameworkElement row)
        {
            row.DataContext = args.Data;
        }
        return element;
    }

    /// <inheritdoc/>
    public void RecycleElement(ElementFactoryRecycleArgs args) => _free.Push(args.Element);

    private UIElement Make()
    {
        Made++;
        return (UIElement)template.LoadContent();
    }
}
