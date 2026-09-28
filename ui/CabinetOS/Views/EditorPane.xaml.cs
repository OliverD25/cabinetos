using System.Text.Json;
using CabinetOS.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CabinetOS.Views;

/// <summary>
/// A pane's editor tab (design view D): the file's glyph and name with
/// close, the tool's name beside a green dot, "Open in Terminal", and the
/// Tool Extension's page below. Close returns the pane to its folder. When
/// the tool's process ends, the pane says so, with Reload.
/// </summary>
public sealed partial class EditorPane : UserControl
{
    private string _folder = "";

    /// <summary>Creates the editor, hidden.</summary>
    public EditorPane()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Run("editor.close", CommandArgs.Object(("pane", PaneIndex)));
        TerminalButton.Click += (_, _) => Run("terminal.new", CommandArgs.With("cwd", _folder));
        ReloadButton.Click += (_, _) => Run("editor.reload", CommandArgs.Object(("pane", PaneIndex)));
    }

    /// <summary>Which pane this editor covers: 0 left, 1 right.</summary>
    public int PaneIndex { get; set; }

    /// <summary>Runs a command through the window's router: (command, args, trigger).</summary>
    public Func<string, JsonElement?, string, Task>? RunCommand { get; set; }

    /// <summary>Where the tool's WebView2 goes.</summary>
    internal Border Frame => ToolFrame;

    /// <summary>Whether the editor is shown.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Whether the tool's page has the keyboard.</summary>
    public bool HasFocus =>
        XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused && IsInside(focused, this);

    /// <summary>Shows <paramref name="path"/> in the tool named <paramref name="provider"/>.</summary>
    public void Show(string path, string provider)
    {
        var name = Path.GetFileName(path);
        FileNameText.Text = name;
        ToolTipService.SetToolTip(FileNameText, path);
        FileIcon.Foreground = FileRow.IconBrushFor(name);
        ProviderText.Text = provider;
        _folder = Path.GetDirectoryName(path) ?? path;
        HideStopped();
        Visibility = Visibility.Visible;
    }

    /// <summary>Hides the editor.</summary>
    public void Hide()
    {
        HideStopped();
        Visibility = Visibility.Collapsed;
    }

    /// <summary>The tool's page stopped: says so, with Reload.</summary>
    public void ShowStopped(string reason)
    {
        StoppedReason.Text = $"Its page failed: {reason}. The rest of the window goes on.";
        StoppedPanel.Visibility = Visibility.Visible;
        ToolFrame.Visibility = Visibility.Collapsed;
    }

    /// <summary>The page runs again.</summary>
    public void HideStopped()
    {
        StoppedPanel.Visibility = Visibility.Collapsed;
        ToolFrame.Visibility = Visibility.Visible;
    }

    /// <summary>Gives the tool's page the keyboard.</summary>
    public bool FocusPage() => ToolFrame.Child is Control page && page.Focus(FocusState.Programmatic);

    private void Run(string command, JsonElement? args) => _ = RunCommand?.Invoke(command, args, "button");

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current == ancestor)
            {
                return true;
            }
        }
        return false;
    }
}
