using System.Text.Json;
using CabinetOS.Core.Keys;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CabinetOS.Views;

/// <summary>
/// The function-key bar (chrome <c>fkeyBar</c>; docs/ui.md, "Metrics and
/// chrome"): Total Commander's row of keys between the panes and the status
/// bar. Each button runs the command its key runs, through the window's
/// router; the keys stay as the keymap has them, and the bar only shows them.
/// It is never a Tab stop and never takes the keyboard, so a click acts on
/// the active pane's selection as the key would.
/// </summary>
public sealed partial class FunctionKeyBar : UserControl
{
    /// <summary>The handout's keys, in its order: the key, its short label, and the command the key runs.</summary>
    public static readonly IReadOnlyList<(string Key, string Label, string Command)> Entries =
    [
        ("f3", "View", "file.view"),
        ("f4", "Edit", "file.edit"),
        ("f5", "Copy", "file.copyToOtherPane"),
        ("f6", "Move", "file.moveToOtherPane"),
        ("f7", "Mkdir", "file.newFolder"),
        ("f8", "Delete", "file.delete"),
        ("alt+f1", "Drv", "go.chooseDriveLeft"),
    ];

    private readonly List<(Button Button, TextBlock Key, TextBlock Label)> _buttons = [];
    private Keymap _keymap = Keymap.Empty;

    /// <summary>Creates the bar, hidden until a theme turns it on.</summary>
    public FunctionKeyBar()
    {
        InitializeComponent();
        var style = (Style)Resources["FunctionKeyStyle"];
        for (var i = 0; i < Entries.Count; i++)
        {
            var (_, label, command) = Entries[i];
            Keys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var key = new TextBlock
            {
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(WindowMetrics.FiguresFontFamily),
                Foreground = ThemeResources.Brush("CbAccentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var text = new TextBlock
            {
                Text = label,
                Foreground = ThemeResources.Brush("CbFkeyLabelBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // The key keeps its width; a label too long for a narrow window ends in "…".
            var content = new Grid { ColumnSpacing = 4 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.Children.Add(key);
            Grid.SetColumn(text, 1);
            content.Children.Add(text);
            var button = new Button { Style = style, Content = content };
            button.Click += (_, _) => _ = RunCommand?.Invoke(command, null, "fkeyBar");
            Grid.SetColumn(button, i);
            Keys.Children.Add(button);
            _buttons.Add((button, key, text));
        }
        ShowKeys();
        ApplyMetrics();
    }

    /// <summary>Runs a command by ID through the window's router: (command, arguments, trigger).</summary>
    public Func<string, JsonElement?, string, Task>? RunCommand { get; set; }

    /// <summary>
    /// Takes the keymap: each button shows its handout key while the command
    /// is still bound to it, else the command's first key, else no key at all.
    /// </summary>
    public void SetKeymap(Keymap keymap)
    {
        _keymap = keymap;
        ShowKeys();
    }

    /// <summary>Shows or hides the bar (chrome <c>fkeyBar</c>) and sizes it with the window's metrics.</summary>
    public void ApplyMetrics()
    {
        var m = WindowMetrics.Current;
        Visibility = WindowMetrics.Chrome.FkeyBar ? Visibility.Visible : Visibility.Collapsed;
        Frame.Height = m.FkeyBarHeight;
        Keys.ColumnSpacing = m.FkeyBarGap;
        foreach (var (button, key, label) in _buttons)
        {
            button.CornerRadius = WindowMetrics.Corners(m.FkeyButtonRadius);
            key.FontSize = label.FontSize = m.FkeyBarFontSize;
        }
    }

    /// <summary>The snapshot aid's check: each key's accessible name, its width, and whether its label is cut short.</summary>
    internal IEnumerable<(string Name, double Width, bool Trimmed)> Measure() =>
        _buttons.Select(b => (AutomationProperties.GetName(b.Button), b.Button.ActualWidth, b.Label.IsTextTrimmed || b.Key.IsTextTrimmed));

    private void ShowKeys()
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            var (handout, label, command) = Entries[i];
            var (button, key, _) = _buttons[i];
            var own = KeySequence.TryParse(handout, out var sequence) ? sequence : null;
            var bound = _keymap.Bindings.Count == 0
                ? own
                : _keymap.Bindings.Where(b => b.Command == command).Select(b => b.Keys).OrderBy(k => k == own ? 0 : 1).FirstOrDefault();
            var shown = bound is null ? "" : string.Join(" ", bound.DisplayParts());
            key.Text = shown;
            key.Visibility = shown.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var name = shown.Length > 0 ? $"{shown} {label}" : label;
            AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, shown.Length > 0 ? $"{label} ({shown})" : label);
        }
    }
}
