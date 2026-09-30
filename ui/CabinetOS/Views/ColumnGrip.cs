using CabinetOS.Core.Presentation;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace CabinetOS.Views;

/// <summary>
/// The grip at one divider of a file list's column headers (docs/ui.md,
/// "Column widths"): 8 px wide, centred on the divider, with the resize
/// cursor. A 1 px line shows while the pointer is over it or drags it. A
/// drag reports how far the pointer went from where it was pressed, as
/// <see cref="SplitterBar"/> does, and the pane turns that into widths; a
/// double-click asks for a fit. The snapshot aid drags it through the same
/// three steps (<see cref="BeginDrag"/>, <see cref="DragBy"/>, <see cref="EndDrag"/>).
/// </summary>
public sealed partial class ColumnGrip : Grid
{
    /// <summary>The grip's width, centred on the divider.</summary>
    public const double HitWidth = 8;

    private readonly Rectangle _line = new() { Width = 1, Opacity = 0, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center };
    private double _startX;
    private bool _dragging;
    private bool _over;

    /// <summary>Creates a grip; <see cref="Divider"/> says which one it is.</summary>
    public ColumnGrip()
    {
        Width = HitWidth;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Children.Add(_line);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        PointerEntered += (_, _) =>
        {
            _over = true;
            ShowLine();
        };
        PointerExited += (_, _) =>
        {
            _over = false;
            ShowLine();
        };
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, e) => EndPointerDrag(e.Pointer);
        PointerCaptureLost += (_, e) => EndPointerDrag(e.Pointer);
        DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            FitRequested?.Invoke(this);
        };
    }

    /// <summary>Which divider the grip sits on.</summary>
    public ColumnDivider Divider { get; set; }

    /// <summary>A drag started.</summary>
    public event Action<ColumnGrip>? DragStarted;

    /// <summary>The pointer moved this far (DIPs, right is positive) since the drag started.</summary>
    public event Action<ColumnGrip, double>? Dragged;

    /// <summary>A drag ended: the button was released, or the pointer was lost.</summary>
    public event Action<ColumnGrip>? DragCompleted;

    /// <summary>A double-click on the grip: fit the column at its left.</summary>
    public event Action<ColumnGrip>? FitRequested;

    /// <summary>Starts a drag (the pointer was pressed on the grip).</summary>
    public void BeginDrag()
    {
        _dragging = true;
        ShowLine();
        DragStarted?.Invoke(this);
    }

    /// <summary>The drag went <paramref name="dx"/> pixels from where it started.</summary>
    public void DragBy(double dx)
    {
        if (_dragging)
        {
            Dragged?.Invoke(this, dx);
        }
    }

    /// <summary>Ends a drag (the button was released).</summary>
    public void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }
        _dragging = false;
        ShowLine();
        DragCompleted?.Invoke(this);
    }

    // The theme's divider colour when it shows, so a theme applied since is taken.
    private void ShowLine()
    {
        var shown = _over || _dragging;
        if (shown)
        {
            _line.Fill = ThemeResources.Brush("CbDividerBrush");
        }
        _line.Opacity = shown ? 1 : 0;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CapturePointer(e.Pointer))
        {
            return;
        }
        _startX = e.GetCurrentPoint(null).Position.X;
        BeginDrag();
        e.Handled = true;
    }

    // Measured against the window, not the grip: the grip itself moves while it is dragged.
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            DragBy(e.GetCurrentPoint(null).Position.X - _startX);
            e.Handled = true;
        }
    }

    private void EndPointerDrag(Pointer pointer)
    {
        if (_dragging)
        {
            ReleasePointerCapture(pointer);
            EndDrag();
        }
    }
}
