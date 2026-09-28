using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace CabinetOS.Views;

/// <summary>
/// The 8 px gap between the panes and the Tool Dock, which also moves it: a
/// drag reports how far the pointer went from where it was pressed, and the
/// window turns that into the dock's size. A thin accent line shows while
/// the pointer is over it.
/// </summary>
public sealed partial class SplitterBar : Grid
{
    private readonly Rectangle _line = new() { Opacity = 0, IsHitTestVisible = false };
    private Orientation _orientation = Orientation.Horizontal;
    private double _startOffset;
    private bool _dragging;

    /// <summary>Creates a splitter; <see cref="Orientation"/> says which way it lies.</summary>
    public SplitterBar()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Children.Add(_line);
        ApplyOrientation();
        PointerEntered += (_, _) => _line.Opacity = 1;
        PointerExited += (_, _) => _line.Opacity = _dragging ? 1 : 0;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, e) => EndDrag(e.Pointer);
        PointerCaptureLost += (_, e) => EndDrag(e.Pointer);
    }

    /// <summary>A drag started.</summary>
    public event Action? DragStarted;

    /// <summary>The pointer moved this far (DIPs, down or right is positive) since the drag started.</summary>
    public event Action<double>? Dragged;

    /// <summary>A drag ended: the button was released, or the pointer was lost.</summary>
    public event Action? DragCompleted;

    /// <summary><c>Horizontal</c>: a bar under the panes that moves up and down; <c>Vertical</c>: one beside them.</summary>
    public Orientation Orientation
    {
        get => _orientation;
        set
        {
            _orientation = value;
            ApplyOrientation();
        }
    }

    private void ApplyOrientation()
    {
        var horizontal = _orientation == Orientation.Horizontal;
        ProtectedCursor = InputSystemCursor.Create(horizontal ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast);
        _line.Fill = ThemeResources.Brush("CbAccentBrush");
        _line.Height = horizontal ? 2 : double.NaN;
        _line.Width = horizontal ? double.NaN : 2;
        _line.HorizontalAlignment = horizontal ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        _line.VerticalAlignment = horizontal ? VerticalAlignment.Center : VerticalAlignment.Stretch;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CapturePointer(e.Pointer))
        {
            return;
        }
        _dragging = true;
        _startOffset = Offset(e);
        DragStarted?.Invoke();
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            Dragged?.Invoke(Offset(e) - _startOffset);
            e.Handled = true;
        }
    }

    private void EndDrag(Pointer pointer)
    {
        if (!_dragging)
        {
            return;
        }
        _dragging = false;
        ReleasePointerCapture(pointer);
        _line.Opacity = 0;
        DragCompleted?.Invoke();
    }

    // Measured against the window, not the bar: the bar itself moves while it is dragged.
    private double Offset(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(null).Position;
        return _orientation == Orientation.Horizontal ? point.Y : point.X;
    }
}
