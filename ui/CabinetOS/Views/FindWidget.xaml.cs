using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace CabinetOS.Views;

/// <summary>
/// The find widget of a pane's tab (Phase 16; docs/ui.md, "Find in pane").
/// It shows the text and the match count and reports what the user does;
/// the window asks the core for the matches (<c>PaneFind</c>) and filters
/// the pane. Esc reaches the window first, which closes it (<c>overlay.close</c>).
/// </summary>
public sealed partial class FindWidget : UserControl
{
    // The text the window put in the box: TextBox raises TextChanged later, so the change it causes is known by its text.
    private string? _setText;

    /// <summary>Creates the widget, hidden.</summary>
    public FindWidget()
    {
        InitializeComponent();
        Input.TextChanged += (_, _) =>
        {
            var set = _setText;
            _setText = null;
            if (IsOpen && set != Input.Text)
            {
                QueryChanged?.Invoke(Input.Text);
            }
        };
        Input.KeyDown += OnInputKeyDown;
        Input.Loaded += (_, _) => HideClearButton();
        CloseButton.Click += (_, _) => CloseRequested?.Invoke();
        ApplyMetrics();
    }

    /// <summary>Raised with the text as it is typed.</summary>
    public event Action<string>? QueryChanged;

    /// <summary>Enter in the box: the first match is to be selected; the widget stays.</summary>
    public event Action? FirstMatchRequested;

    /// <summary>Down in the box: the keyboard goes to the list.</summary>
    public event Action? ListRequested;

    /// <summary>The close button (Esc goes through the window's <c>overlay.close</c>).</summary>
    public event Action? CloseRequested;

    /// <summary>Whether the widget is shown.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>The text in the box.</summary>
    public string Query => Input.Text;

    /// <summary>The folder the box's placeholder names.</summary>
    public string Folder { get; private set; } = "";

    /// <summary>Whether the box has the keyboard.</summary>
    public bool HasFocus => FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && IsWithin(focused);

    /// <summary>Shows the widget with <paramref name="query"/> for the folder named <paramref name="folder"/>.</summary>
    public void Show(string query, string folder)
    {
        SetText(query);
        Input.SelectionStart = query.Length;
        Folder = folder;
        Input.PlaceholderText = folder.Length > 0 ? $"Find in {folder}" : "Find";
        Visibility = Visibility.Visible;
    }

    /// <summary>Hides the widget; the text goes with it.</summary>
    public void Hide()
    {
        SetText("");
        Folder = "";
        CountText.Text = "";
        Visibility = Visibility.Collapsed;
    }

    private void SetText(string text)
    {
        if (Input.Text != text)
        {
            _setText = text;
            Input.Text = text;
        }
    }

    /// <summary>Gives the box the keyboard with its text selected.</summary>
    public void FocusInput()
    {
        Input.UpdateLayout();
        if (!Input.Focus(FocusState.Keyboard))
        {
            DispatcherQueue.TryEnqueue(() => Input.Focus(FocusState.Keyboard));
        }
        Input.SelectAll();
    }

    /// <summary>Types <paramref name="text"/> into the box as the user would (the snapshot aid's <c>find:</c> step).</summary>
    public void Type(string text)
    {
        Input.Text = text;
        Input.SelectionStart = text.Length;
    }

    /// <summary>Shows the match count: "n of m", or nothing while nothing is typed.</summary>
    public void SetCount(string text) => CountText.Text = text;

    /// <summary>The count as shown, for the snapshot aid's log.</summary>
    public string Count => CountText.Text;

    /// <summary>Enter as the box takes it (the snapshot aid's <c>find-key:enter</c>).</summary>
    public void PressEnter() => FirstMatchRequested?.Invoke();

    /// <summary>Lays the widget out with the window's sizes now: its corners follow radiusControl, its box the breadcrumb row.</summary>
    public void ApplyMetrics()
    {
        var m = WindowMetrics.Current;
        var radius = m.RadiusControl;
        Frame.CornerRadius = new CornerRadius(0, 0, radius, radius);
        Input.Height = Math.Max(18, m.BreadcrumbRowHeight - 4);
        Input.CornerRadius = WindowMetrics.Corners(radius);
        Input.Padding = new Thickness(6, WindowMetrics.TextTop(Input.Height, 12), 6, 0);
        CloseButton.CornerRadius = WindowMetrics.Corners(radius);
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                FirstMatchRequested?.Invoke();
                break;
            case VirtualKey.Down:
                e.Handled = true;
                ListRequested?.Invoke();
                break;
        }
    }

    // WinUI's box shows a clear button (×) of its own while it has the keyboard: beside the widget's close × that
    // makes two crosses, so the box's is taken away (as the pane's rename box does).
    private void HideClearButton()
    {
        Input.ApplyTemplate();
        if (FindPart(Input, "DeleteButton") is Button clear)
        {
            clear.MaxWidth = 0;
            clear.IsTabStop = false;
            clear.IsHitTestVisible = false;
        }
    }

    private static DependencyObject? FindPart(DependencyObject parent, string name)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { Name: var childName } && childName == name)
            {
                return child;
            }
            if (FindPart(child, name) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private bool IsWithin(DependencyObject element)
    {
        for (var current = element; current is not null; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
        {
            if (current == this)
            {
                return true;
            }
        }
        return false;
    }
}
