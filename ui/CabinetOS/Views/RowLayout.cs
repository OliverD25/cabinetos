using CabinetOS.Core.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace CabinetOS.Views;

/// <summary>
/// The pane's row layout: WinUI's <see cref="StackLayout"/>, with its measure
/// and arrange passes timed for the frame table while frame statistics are on
/// (<see cref="FrameParts"/>; docs/ui.md, "Scrolling"). A layout of fixed-height
/// rows that made the page ahead in the idle frame was measured against it
/// and cost more: WinUI draws rows only when they come into view.
/// </summary>
public sealed partial class RowLayout : StackLayout
{
    /// <inheritdoc/>
    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var started = FrameParts.Start();
        var size = base.MeasureOverride(context, availableSize);
        FrameParts.Stop(FramePart.Measure, started);
        return size;
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        var started = FrameParts.Start();
        var size = base.ArrangeOverride(context, finalSize);
        FrameParts.Stop(FramePart.Arrange, started);
        return size;
    }
}
