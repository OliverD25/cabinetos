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
/// 50 to 65 ms (docs/ui.md, "Scrolling").
/// </summary>
internal sealed class RowFactory(DataTemplate template, DispatcherQueue queue) : IElementFactory
{
    // Rows made in one idle moment: a few milliseconds, so input and frames go first.
    private const double SliceMs = 4;

    private readonly Stack<UIElement> _free = new();
    private int _ahead;
    private int _wanted;
    private bool _making;

    /// <summary>Rows made so far, by the repeater's need and ahead of it.</summary>
    public int Made { get; private set; }

    /// <summary>
    /// Makes rows until <paramref name="rows"/> were made ahead in all, a few
    /// in each idle moment of the window (the dispatcher's low priority).
    /// </summary>
    public void MakeAhead(int rows)
    {
        _wanted = Math.Max(_wanted, rows);
        if (_ahead < _wanted && !_making)
        {
            _making = queue.TryEnqueue(DispatcherQueuePriority.Low, Step);
        }
    }

    private void Step()
    {
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
