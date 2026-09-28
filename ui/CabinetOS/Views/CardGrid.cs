using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace CabinetOS.Views;

/// <summary>
/// The marketplace's card grid, as CSS's <c>repeat(auto-fill,
/// minmax(230px, 1fr))</c>: as many columns as fit at the minimum width,
/// sharing the width equally; each row as tall as its tallest card, and every
/// card of a row stretched to it.
/// </summary>
public sealed partial class CardGrid : Panel
{
    /// <summary>The narrowest a column may get.</summary>
    public double MinItemWidth { get; set; } = 230;

    /// <summary>The gap between columns and between rows.</summary>
    public double Spacing { get; set; } = 10;

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, width, itemWidth) = Columns(availableSize.Width);
        var height = 0.0;
        foreach (var row in Rows(columns))
        {
            var rowHeight = 0.0;
            foreach (var child in row)
            {
                child.Measure(new Size(itemWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            height += (height > 0 ? Spacing : 0) + rowHeight;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, _, itemWidth) = Columns(finalSize.Width);
        var top = 0.0;
        foreach (var row in Rows(columns))
        {
            var rowHeight = row.Max(child => child.DesiredSize.Height);
            for (var i = 0; i < row.Count; i++)
            {
                row[i].Arrange(new Rect(i * (itemWidth + Spacing), top, itemWidth, rowHeight));
            }
            top += rowHeight + Spacing;
        }
        return finalSize;
    }

    private (int Columns, double Width, double ItemWidth) Columns(double available)
    {
        var width = double.IsInfinity(available) ? MinItemWidth : available;
        var columns = Math.Max(1, (int)Math.Floor((width + Spacing) / (MinItemWidth + Spacing)));
        return (columns, width, Math.Max(0, (width - (Spacing * (columns - 1))) / columns));
    }

    private IEnumerable<List<UIElement>> Rows(int columns)
    {
        for (var start = 0; start < Children.Count; start += columns)
        {
            var row = new List<UIElement>(columns);
            for (var i = start; i < Math.Min(start + columns, Children.Count); i++)
            {
                row.Add(Children[i]);
            }
            yield return row;
        }
    }
}
