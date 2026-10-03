using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Shell;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace CabinetOS.Views;

/// <summary>
/// The right-click menu's edit mode (Phase 18, step 2; docs/ui.md, "Editing the menu"): "Edit Menu…"
/// turns the menu into this surface, where the menu was and as wide, until Done, Cancel or Esc. A
/// click outside does nothing. The target's rows can be dragged, moved with Alt+Up and Alt+Down,
/// taken out with their X or Delete, and added to with "Add Command…" (Insert) and "Add separator";
/// the icon row and what the window adds after the rows stay as they are, greyed.
/// <see cref="ContextMenuEditModel"/> holds every rule; this class only draws it and takes the
/// pointer and the keys. The window writes nothing: "Done" hands the model to <see cref="Save"/>.
/// It is part of the window, not a flyout, so a click outside can be swallowed, the palette's
/// prompt ("Add Command…") can open over it, and the snapshot aid can draw it.
/// </summary>
public sealed partial class ContextMenuEditor : UserControl
{
    private const string Target = "cabinetos_ui::context_menu";

    // The menu's own width when it is wider; each row here also has a handle and an X.
    private const double MinPanelWidth = 280;

    // A press that moves less than this is a click, not a drag.
    private const double DragThreshold = 4;

    private readonly Storyboard _entrance;
    private readonly List<ContentControl> _rows = [];
    private ContextMenuEditModel? _model;
    private Drag? _drag;
    private bool _saving;
    private bool _adding;

    /// <summary>A row being dragged: where it started, where the pointer went down, and whether it moved yet.</summary>
    private sealed class Drag(int from, double startY)
    {
        public int From { get; } = from;

        public double StartY { get; } = startY;

        public bool Moving { get; set; }

        public int To { get; set; } = from;
    }

    /// <summary>Creates the edit surface, hidden.</summary>
    public ContextMenuEditor()
    {
        InitializeComponent();
        _entrance = (Storyboard)Resources["Entrance"];
        Scrim.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Scrim))
            {
                e.Handled = true;
                FocusRow(FocusState.Programmatic);
            }
        };
        ShellMenuCheck.Checked += OnShellMenuBox;
        ShellMenuCheck.Unchecked += OnShellMenuBox;
        AddButton.Click += (_, _) => _ = AddCommandAsync();
        SeparatorButton.Click += (_, _) => AddSeparator();
        OpenFileButton.Click += (_, _) => OpenFile?.Invoke();
        CancelButton.Click += (_, _) => Cancel();
        DoneButton.Click += (_, _) => _ = DoneAsync();
    }

    /// <summary>Writes the edited list (the core's <c>set_value</c>); true when it was written.</summary>
    public Func<ContextMenuEditModel, Task<bool>>? Save { get; set; }

    /// <summary>Asks for a command to add (the palette's prompt); its ID, or null when none was chosen.</summary>
    public Func<ContextMenuEditModel, Task<string?>>? PickCommand { get; set; }

    /// <summary>"Open the file": the window leaves the edit mode and opens cabinetos.json.</summary>
    public Action? OpenFile { get; set; }

    /// <summary>Raised when the edit mode ends, before the surface collapses: whether the list was saved.</summary>
    public event Action<bool>? Closed;

    /// <summary>The last row was pressed: the window runs <c>menu.toggleShellMenu</c>, which writes <c>contextMenu.shellMenu</c>.</summary>
    public event Action? ShellMenuToggled;

    private bool _shellMenuOn;
    private bool _syncingBox;

    // A click, the Space key or assistive technology changed the box: the box shows what the file says, so it is put back at once
    // and the window is asked to change the setting; the file's change (the core's config_changed) moves the box.
    private void OnShellMenuBox(object sender, RoutedEventArgs e)
    {
        if (_syncingBox || (ShellMenuCheck.IsChecked == true) == _shellMenuOn)
        {
            return;
        }
        _syncingBox = true;
        ShellMenuCheck.IsChecked = _shellMenuOn;
        _syncingBox = false;
        ShellMenuToggled?.Invoke();
    }

    /// <summary>Whether the last row's box is checked now (the snapshot aid's log).</summary>
    public bool ShellMenuChecked => ShellMenuCheck.IsChecked == true;

    /// <summary>Whether <c>contextMenu.shellMenu</c> is on: the last row's check follows the file.</summary>
    public bool ShellMenuOn
    {
        get => _shellMenuOn;
        set
        {
            _shellMenuOn = value;
            _syncingBox = true;
            ShellMenuCheck.IsChecked = value;
            _syncingBox = false;
        }
    }

    /// <summary>Whether the edit mode is on screen.</summary>
    public bool IsOpen => _model is not null;

    /// <summary>Whether nothing of the edit mode is in flight: no "Add Command…" prompt is open for it and no save is out (the snapshot aid's <c>until:menu-edit-idle</c>).</summary>
    public bool IsIdle => !_adding && !_saving;

    /// <summary>
    /// Whether the keyboard is on the row the model says has the focus (or on "Add Command…" when it has none), or the edit
    /// mode is closed: the snapshot aid's <c>menu-edit-key</c> waits for it before the next key.
    /// </summary>
    public bool FocusSettled => _model is not { } model || IsFocusWithin(model.Focus >= 0 && model.Focus < _rows.Count ? _rows[model.Focus] : AddButton);

    /// <summary>The list being edited, while open.</summary>
    public ContextMenuEditModel? Model => _model;

    /// <summary>The rows' titles ("-" a divider, "|" between them) while open, else empty: for the snapshot aid's log.</summary>
    public string Describe() => _model?.Describe() ?? "";

    /// <summary>Whether the keyboard is inside the surface.</summary>
    public bool HasKeyboard => IsFocusWithin(Panel);

    /// <summary>
    /// Shows <paramref name="model"/> where the menu was (<paramref name="menu"/>, the window's
    /// coordinates), else where a menu would open for <paramref name="at"/> (<see cref="MenuPlacement"/>:
    /// the corner at the point, above or to its left when it would not fit). <paramref name="around"/>
    /// is the menu built without the target's rows: its icon row, and what follows the rows.
    /// </summary>
    public void Show(ContextMenuEditModel model, ContextMenuView around, Rect? menu, Point at, bool fromKeyboard)
    {
        _model = model;
        _drag = null;
        _saving = false;
        DoneButton.IsEnabled = true;
        HeaderText.Text = $"Editing: {model.TargetName}";
        BuildStrip(around.QuickActions);
        BuildRows();
        BuildTail(around.Items);
        Visibility = Visibility.Visible;

        var window = XamlRoot?.Size ?? new Size(ActualWidth, ActualHeight);
        var width = Math.Min(Math.Max(MinPanelWidth, menu?.Width ?? 0), Math.Max(MinPanelWidth, window.Width - 8));
        Panel.Width = width;
        Panel.MaxHeight = Math.Max(200, window.Height - 16);
        Panel.Measure(new Size(width, Panel.MaxHeight));
        double x, y;
        if (menu is { } place)
        {
            x = Math.Max(4, Math.Min(place.X, window.Width - width - 4));
            y = Math.Max(4, Math.Min(place.Y, window.Height - Panel.DesiredSize.Height - 8));
        }
        else
        {
            // Where the menu would have opened: the corner at the point, above or to the left of it when it does not fit.
            (x, y) = MenuPlacement.Corner(at.X, at.Y, width, Panel.DesiredSize.Height, window.Width, window.Height);
        }
        Canvas.SetLeft(Panel, x);
        Canvas.SetTop(Panel, y);
        _entrance.Begin();
        FocusRow(fromKeyboard ? FocusState.Keyboard : FocusState.Programmatic);
    }

    /// <summary>Leaves the edit mode without saving (Cancel, Esc).</summary>
    public void Cancel()
    {
        if (IsOpen)
        {
            Finish(saved: false);
        }
    }

    /// <summary>"Done": saves the list when it changed and closes; a refused save keeps the edit mode open.</summary>
    public async Task DoneAsync()
    {
        if (_model is not { } model || _saving)
        {
            return;
        }
        if (!model.IsChanged)
        {
            Finish(saved: false);
            return;
        }
        _saving = true;
        DoneButton.IsEnabled = false;
        var saved = Save is not null && await Save(model);
        _saving = false;
        if (!ReferenceEquals(_model, model))
        {
            return;
        }
        if (saved)
        {
            Finish(saved: true);
            return;
        }
        DoneButton.IsEnabled = true;
        FocusRow(FocusState.Programmatic);
    }

    /// <summary>
    /// The snapshot aid's drag: the row titled <paramref name="from"/> dropped on the row titled
    /// <paramref name="onto"/>, through the same steps the pointer takes. Whether both rows were there.
    /// </summary>
    public bool DragForSnapshot(string from, string onto)
    {
        if (_model is not { } model || _rows.Count == 0)
        {
            return false;
        }
        // The rows were built by the last change and are measured at the next frame, which a busy machine draws late: a row with no
        // height yet makes the drag move nothing.
        Rows.UpdateLayout();
        var titles = model.Rows.Select(r => r.Title).ToList();
        var (start, end) = (titles.IndexOf(from), titles.IndexOf(onto));
        if (start < 0 || end < 0)
        {
            return false;
        }
        _drag = new Drag(start, 0);
        DragTo(start, (end - start) * _rows[0].ActualHeight);
        EndDrag(commit: true);
        return true;
    }

    /// <summary>Puts the keyboard back on the focused row (or "Add Command…" when there is none).</summary>
    public void TakeKeyboard() => FocusRow(FocusState.Keyboard);

    /// <summary>
    /// A key while the edit mode is open (the window passes every key here, as a dialog holds the
    /// keyboard): true when the edit mode took it. Tab, Enter and Space go on to the focused button.
    /// </summary>
    public bool HandleKey(KeyCombo combo)
    {
        if (_model is not { } model)
        {
            return false;
        }
        var inRows = IsFocusWithin(Rows);
        switch (ContextMenuEditModel.ActionFor(combo))
        {
            case MenuEditAction.Cancel:
                Cancel();
                return true;
            case MenuEditAction.Save:
                _ = DoneAsync();
                return true;
            case MenuEditAction.AddCommand:
                _ = AddCommandAsync();
                return true;
            case MenuEditAction.FocusUp or MenuEditAction.FocusDown when inRows:
                model.MoveFocus(combo.Key == "up" ? -1 : 1);
                FocusRow(FocusState.Keyboard);
                return true;
            case MenuEditAction.MoveUp or MenuEditAction.MoveDown when inRows:
                var title = FocusedTitle();
                if (model.MoveFocused(combo.Key == "up" ? -1 : 1))
                {
                    Changed("move", title);
                }
                FocusRow(FocusState.Keyboard);
                return true;
            case MenuEditAction.Remove when inRows:
                RemoveAt(model.Focus);
                return true;
            default:
                return false;
        }
    }

    private void Finish(bool saved)
    {
        _model = null;
        _drag = null;
        // The window gives the pane its keyboard first: a collapsing surface with the focus would hand it on.
        Closed?.Invoke(saved);
        Visibility = Visibility.Collapsed;
        Rows.Children.Clear();
        _rows.Clear();
        OpenToolTips.Close(XamlRoot);
    }

    private async Task AddCommandAsync()
    {
        if (_model is not { } model || PickCommand is null)
        {
            return;
        }
        string? id;
        _adding = true;
        try
        {
            id = await PickCommand(model);
        }
        finally
        {
            _adding = false;
        }
        if (!ReferenceEquals(_model, model))
        {
            return;
        }
        if (id is not null)
        {
            model.InsertCommand(id);
            Changed("add", model.Rows[model.Focus].Title);
        }
        FocusRow(FocusState.Keyboard);
    }

    private void AddSeparator()
    {
        if (_model is not { } model)
        {
            return;
        }
        model.InsertSeparator();
        Changed("separator", "Separator");
        FocusRow(FocusState.Keyboard);
    }

    private void RemoveAt(int index)
    {
        if (_model is not { } model || index < 0 || index >= model.Items.Count)
        {
            return;
        }
        var title = model.Rows[index].Title;
        if (model.Remove(index))
        {
            Changed("remove", title);
        }
        FocusRow(FocusState.Keyboard);
    }

    private string FocusedTitle() => _model is { Focus: >= 0 } model ? model.Rows[model.Focus].Title : "";

    // Every change redraws the rows (a menu has a handful) and says what happened in the log.
    private void Changed(string step, string title)
    {
        BuildRows();
        Diag.Info(Target, "menu edit step", new LogField("step", step), new LogField("row", title), new LogField("rows", Describe()));
    }

    private void FocusRow(FocusState state)
    {
        if (_model is { Focus: >= 0 } model && model.Focus < _rows.Count)
        {
            var row = _rows[model.Focus];
            row.Focus(state);
            row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
        else
        {
            AddButton.Focus(state);
        }
        MarkFocus();
    }

    private bool IsFocusWithin(DependencyObject container)
    {
        var focused = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
        for (var element = focused; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element == container)
            {
                return true;
            }
        }
        return false;
    }

    private void BuildStrip(IReadOnlyList<ContextMenuEntry> quickActions)
    {
        Strip.Children.Clear();
        StripBorder.Visibility = quickActions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var entry in quickActions)
        {
            var icon = new Border
            {
                Width = 36,
                Height = WindowMetrics.Current.MenuRowHeight,
                Child = new FontIcon { Glyph = entry.Glyph ?? "", FontSize = 14, Foreground = ThemeResources.Brush("CbTextSecondaryBrush") },
            };
            ToolTipService.SetToolTip(icon, entry.Tooltip ?? entry.Title);
            AutomationProperties.SetName(icon, entry.Title);
            Strip.Children.Add(icon);
        }
    }

    private void BuildRows()
    {
        Rows.Children.Clear();
        _rows.Clear();
        if (_model is not { } model)
        {
            return;
        }
        var rows = model.Rows;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = RowView(rows[i], i);
            _rows.Add(row);
            Rows.Children.Add(row);
        }
        if (rows.Count == 0)
        {
            Rows.Children.Add(new TextBlock
            {
                Text = "No rows. Add Command… puts one here.",
                Padding = new Thickness(10, 6, 10, 6),
                FontSize = 12,
                Foreground = ThemeResources.Brush("CbHintTextBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        MarkFocus();
    }

    // A row as the menu shows it, with a drag handle at the left and the red X at the right.
    private ContentControl RowView(MenuEditRow row, int index)
    {
        var m = WindowMetrics.Current;
        var root = new Grid { CornerRadius = WindowMetrics.Corners(m.RadiusControl), Background = new SolidColorBrush(Colors.Transparent) };
        root.Children.Add(new Rectangle
        {
            Width = m.SelectionBarWidth,
            Height = Math.Clamp(m.MenuRowHeight - 4, 0, 16),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = ThemeResources.Brush("CbAccentBrush"),
            RadiusX = m.SelectionBarWidth / 2,
            RadiusY = m.SelectionBarWidth / 2,
            Visibility = Visibility.Collapsed,
        });
        var content = new Grid { Padding = new Thickness(6, 0, 2, 0), ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(new FontIcon
        {
            Glyph = "",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeResources.Brush("CbTextTertiaryBrush"),
        });
        if (row.IsSeparator)
        {
            var line = new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Center, Fill = ThemeResources.Brush("CbDividerBrush") };
            Grid.SetColumn(line, 1);
            Grid.SetColumnSpan(line, 3);
            content.Children.Add(line);
        }
        else
        {
            var icon = Icon(row);
            Grid.SetColumn(icon, 1);
            content.Children.Add(icon);
            var title = new TextBlock
            {
                Text = row.Badge is { } badge ? $"{row.Title} ({badge})" : row.Title,
                FontSize = m.FontSize,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeResources.Brush(row.Known ? "CbTextPrimaryBrush" : "CbTextDisabledBrush"),
            };
            Grid.SetColumn(title, 2);
            content.Children.Add(title);
            if ((row.Detail ?? row.Keys) is { Length: > 0 } detail)
            {
                var side = new TextBlock
                {
                    Text = detail,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ThemeResources.Brush("CbHintTextBrush"),
                };
                if (row.Detail is null)
                {
                    side.FontFamily = (FontFamily)ThemeResources.Get("CbMonoFont")!;
                }
                Grid.SetColumn(side, 3);
                content.Children.Add(side);
            }
        }
        var remove = new Button
        {
            Width = 28,
            Height = 24,
            MinWidth = 0,
            Style = (Style)ThemeResources.Get("CbSubtleButtonStyle")!,
            Content = new FontIcon { Glyph = "", FontSize = 10, Foreground = ThemeResources.Brush("CbErrorTextBrush") },
        };
        AutomationProperties.SetName(remove, $"Remove {row.Title}");
        ToolTipService.SetToolTip(remove, "Remove (Delete)");
        remove.Click += (_, _) => RemoveAt(index);
        Grid.SetColumn(remove, 4);
        content.Children.Add(remove);
        root.Children.Add(content);

        var host = new ContentControl
        {
            Content = root,
            Height = m.MenuRowHeight,
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            RenderTransform = new TranslateTransform(),
        };
        AutomationProperties.SetName(host, row.Title);
        host.GotFocus += (_, _) =>
        {
            // XAML raises this a frame late on a busy machine. A row that a rebuild has replaced since (a move, an add)
            // must not take the focus back to the index it had: the next Delete took out the row that was there then.
            if (index >= _rows.Count || !ReferenceEquals(_rows[index], host))
            {
                return;
            }
            _model?.SetFocus(index);
            MarkFocus();
        };
        host.PointerEntered += (_, _) =>
        {
            if (_drag is null && _model?.Focus != index)
            {
                root.Background = ThemeResources.Brush("CbMenuHoverFillBrush");
            }
        };
        host.PointerExited += (_, _) => MarkFocus();
        host.PointerPressed += (_, e) => OnRowPressed(host, index, e);
        host.PointerMoved += (_, e) => OnRowMoved(index, e);
        // Windows ends the capture when the button goes up, and the drop rebuilds the rows: whichever of the two events
        // comes first ends the drag, a drop when the button is up, a cancel when the capture was taken away.
        host.PointerReleased += (_, _) => EndDrag(commit: true);
        host.PointerCaptureLost += (_, e) =>
        {
            if (_drag is not null)
            {
                EndDrag(commit: !e.GetCurrentPoint(null).Properties.IsLeftButtonPressed);
            }
        };
        return host;
    }

    private static FrameworkElement Icon(MenuEditRow row)
    {
        if (row.Glyph is null && row.Badge is { } badge)
        {
            return new Border
            {
                Width = 14,
                Height = 14,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(FileContextMenu.PluginColor(badge)),
            };
        }
        return new FontIcon { Glyph = row.Glyph ?? "", FontSize = 14, Width = 16, VerticalAlignment = VerticalAlignment.Center };
    }

    private void MarkFocus()
    {
        var focus = _model?.Focus ?? -1;
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Content is Grid { Children: [Rectangle pill, ..] } root)
            {
                var focused = i == focus;
                root.Background = focused ? ThemeResources.Brush("CbSelectedFillBrush") : new SolidColorBrush(Colors.Transparent);
                pill.Visibility = focused ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void OnRowPressed(ContentControl host, int index, PointerRoutedEventArgs e)
    {
        if (_model is null || !e.GetCurrentPoint(host).Properties.IsLeftButtonPressed)
        {
            return;
        }
        e.Handled = true;
        _model.SetFocus(index);
        host.Focus(FocusState.Pointer);
        MarkFocus();
        if (host.CapturePointer(e.Pointer))
        {
            _drag = new Drag(index, e.GetCurrentPoint(Rows).Position.Y);
        }
    }

    private void OnRowMoved(int index, PointerRoutedEventArgs e)
    {
        if (_drag is { } drag && drag.From == index)
        {
            DragTo(index, e.GetCurrentPoint(Rows).Position.Y - drag.StartY);
        }
    }

    // The dragged row follows the pointer; the rows it passes make room, so the drop place shows before the release.
    private void DragTo(int index, double dy)
    {
        if (_drag is not { } drag || drag.From != index || _rows.Count == 0)
        {
            return;
        }
        if (!drag.Moving && Math.Abs(dy) < DragThreshold)
        {
            return;
        }
        drag.Moving = true;
        var height = Math.Max(1, _rows[0].ActualHeight);
        var last = _rows.Count - 1;
        drag.To = Math.Clamp(drag.From + (int)Math.Round(dy / height), 0, last);
        for (var i = 0; i < _rows.Count; i++)
        {
            var shift = i == drag.From ? Math.Clamp(dy, -drag.From * height, (last - drag.From) * height)
                : drag.From < i && i <= drag.To ? -height
                : drag.To <= i && i < drag.From ? height
                : 0;
            ((TranslateTransform)_rows[i].RenderTransform).Y = shift;
            Canvas.SetZIndex(_rows[i], i == drag.From ? 1 : 0);
        }
    }

    private void EndDrag(bool commit)
    {
        if (_drag is not { } drag)
        {
            return;
        }
        _drag = null;
        foreach (var row in _rows)
        {
            ((TranslateTransform)row.RenderTransform).Y = 0;
            Canvas.SetZIndex(row, 0);
        }
        if (commit && drag.Moving && _model is { } model)
        {
            var title = model.Rows[drag.From].Title;
            if (model.Move(drag.From, drag.To))
            {
                Changed("drag", title);
            }
            FocusRow(FocusState.Programmatic);
        }
    }

    private void BuildTail(IReadOnlyList<ContextMenuEntry> items)
    {
        Tail.Children.Clear();
        var m = WindowMetrics.Current;
        var greyed = ThemeResources.Brush("CbTextDisabledBrush");
        if (items.Count > 0 && items[0].Kind != ContextMenuEntryKind.Separator)
        {
            Tail.Children.Add(Divider());
        }
        foreach (var entry in items)
        {
            switch (entry.Kind)
            {
                case ContextMenuEntryKind.Separator:
                    Tail.Children.Add(Divider());
                    break;
                case ContextMenuEntryKind.Header:
                    Tail.Children.Add(new TextBlock
                    {
                        Text = entry.Title.ToUpperInvariant(),
                        Padding = new Thickness(10, 6, 10, 2),
                        Style = (Style)ThemeResources.Get("CbSectionLabelStyle")!,
                        FontSize = 10,
                        Opacity = 0.6,
                    });
                    break;
                default:
                    var row = new Grid { Height = m.MenuRowHeight, Padding = new Thickness(26, 0, 10, 0), ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(new FontIcon { Glyph = entry.Glyph ?? "", FontSize = 14, Width = 16, Foreground = greyed, VerticalAlignment = VerticalAlignment.Center });
                    var title = new TextBlock
                    {
                        Text = entry.Badge is { } badge ? $"{entry.Title} ({badge})" : entry.Title,
                        FontSize = m.FontSize,
                        Foreground = greyed,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    Grid.SetColumn(title, 1);
                    row.Children.Add(title);
                    if (!string.IsNullOrEmpty(entry.Keys))
                    {
                        var keys = new TextBlock
                        {
                            Text = entry.Keys,
                            FontSize = 11,
                            FontFamily = (FontFamily)ThemeResources.Get("CbMonoFont")!,
                            Foreground = greyed,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        Grid.SetColumn(keys, 2);
                        row.Children.Add(keys);
                    }
                    ToolTipService.SetToolTip(row, "The window adds this row to every menu; it is not edited here.");
                    Tail.Children.Add(row);
                    break;
            }
        }
    }

    private static Rectangle Divider() => new()
    {
        Height = 1,
        Margin = new Thickness(8, 4, 8, 4),
        Fill = ThemeResources.Brush("CbDividerBrush"),
    };
}
