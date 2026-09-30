using System.Globalization;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Shell;
using CabinetOS.Services;
using CabinetOS.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;

namespace CabinetOS;

// The shell of Phase 16 (the creator's SHELL_REDESIGN.md; docs/ui.md, "The top row", "The breadcrumb row"): one top
// row that is also the window's drag area, and in each pane a breadcrumb row with Back, Forward and Up. What they
// decide without a window is in CabinetOS.Core.Shell, with tests.
public sealed partial class MainWindow
{
    private const string ShellTarget = "cabinetos_ui::shell";

    // Windows draws the caption buttons this high (TitleBarHeightOption.Standard): the top row is never lower.
    private const double CaptionButtonsHeight = 32;

    private PaneCrumbs[] _crumbViews = null!;
    private CommandCenterPlace _centerPlace = new(false, 0, 0);
    private string _dragRegions = "";

    // The workspace until workspaces exist (docs/ui.md, "The top row"): the repository that holds the active folder, for
    // the pill's branch and Quick Open's folder. The core finds it (workspace_info) whenever the active folder changes.
    private WorkspaceInfoReply? _workspace;
    private string _workspaceFor = "";
    private int _workspaceAsked;

    private void SetUpShell()
    {
        _crumbViews = [LeftCrumbs, RightCrumbs];
        for (var i = 0; i < _crumbViews.Length; i++)
        {
            _crumbViews[i].PaneIndex = i;
            _crumbViews[i].RunCommand = (id, args, trigger) => _router.ExecuteAsync(id, args, trigger);
        }
        MenuButton.Click += (_, _) => _ = _router.ExecuteAsync("menu.show", trigger: "button");
        WorkspacePill.Click += (_, _) => ShowWorkspaceMenu(fromKeyboard: false);
        CommandCenter.Click += (_, _) => _ = _router.ExecuteAsync("quickOpen.show", trigger: "button");
        SettingsButton.Click += (_, _) => _ = _router.ExecuteAsync("settings.open", trigger: "button");
        _palette.Opened += UpdatePaletteButton;
        _palette.Closed += UpdatePaletteButton;
        // The top row's layout: the command center between its clusters, and the drag area around its controls.
        TopRowGrid.SizeChanged += (_, _) => LayOutTopRow();
        TopLeft.SizeChanged += (_, _) => LayOutTopRow();
        TopRight.SizeChanged += (_, _) => LayOutTopRow();
        CommandCenterFrame.SizeChanged += (_, _) => UpdateDragRegions();
        ShowBranch();
    }

    // ----- The top row -----

    /// <summary>
    /// Places the command center (<see cref="TopRowLayout"/>): centred in the
    /// window, clamp(200px, 34%, 380px) wide, never over the clusters beside
    /// it, hidden below 640 px of window width. Then the drag area follows.
    /// </summary>
    private void LayOutTopRow()
    {
        var width = RootGrid.ActualWidth;
        var leftEnd = TopLeft.ActualWidth + TopLeft.Margin.Left;
        var rightStart = TopRowGrid.ActualWidth - TopRight.ActualWidth;
        var place = TopRowLayout.Place(width, leftEnd, rightStart);
        if (place != _centerPlace)
        {
            _centerPlace = place;
            CommandCenterFrame.Visibility = place.Visible ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(CommandCenterFrame, place.Left);
            CommandCenterFrame.Width = Math.Max(0, place.Width);
        }
        Canvas.SetTop(CommandCenterFrame, Math.Max(0, (TopRowGrid.ActualHeight - CommandCenterFrame.Height) / 2));
        UpdateDragRegions();
    }

    // Windows' caption buttons take the right end of the row: RightInset in physical pixels, once the window shows.
    private void FitCaptionSpace()
    {
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        var inset = AppWindow.TitleBar.RightInset / scale;
        CaptionSpace.Width = inset > 0 ? inset : 3 * WindowMetrics.Current.CaptionButtonWidth;
    }

    /// <summary>
    /// The top row is the window's drag area (SetTitleBar), except over its
    /// controls: those rectangles pass the pointer through, so a click on the
    /// menu, the pill, the command center or a view button reaches it.
    /// </summary>
    private void UpdateDragRegions()
    {
        if (RootGrid.XamlRoot is not { } root || TopBar.ActualWidth <= 0)
        {
            return;
        }
        var scale = root.RasterizationScale;
        var rects = new List<RectInt32>();
        foreach (var element in new FrameworkElement[] { MenuButton, WorkspacePillFrame, CommandCenterFrame, DualButton, TerminalButton, MarketplaceButton, PaletteButton, SettingsButton })
        {
            if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0)
            {
                continue;
            }
            var bounds = element.TransformToVisual(null).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            rects.Add(new RectInt32(
                (int)Math.Floor(bounds.X * scale),
                (int)Math.Floor(bounds.Y * scale),
                (int)Math.Ceiling(bounds.Width * scale),
                (int)Math.Ceiling(bounds.Height * scale)));
        }
        var text = string.Join(";", rects.Select(r => $"{r.X},{r.Y},{r.Width},{r.Height}"));
        if (text == _dragRegions)
        {
            return;
        }
        _dragRegions = text;
        try
        {
            InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, [.. rects]);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Diag.Warn(ShellTarget, "the top row's controls could not be taken out of the drag area", new LogField("error", error.Message));
        }
    }

    // The view buttons show what is on: the accent for dual panes, the terminal, the marketplace, the palette.
    private void UpdateDualButton() => DualIcon.Foreground = ThemeResources.Brush(_dual ? "CbAccentBrush" : "CbTextSecondaryBrush");

    private void UpdatePaletteButton() => PaletteIcon.Foreground = ThemeResources.Brush(_palette.IsOpen ? "CbAccentBrush" : "CbTextSecondaryBrush");

    private void RegisterShellCommands()
    {
        _router.RegisterUiHandler("menu.show", invocation => ShowShellMenu(fromKeyboard: invocation.Trigger is not ("button" or "mouse")));
        _router.RegisterUiHandler("settings.open", invocation => OpenSettingsAsync(invocation.RequestId));
        // Ctrl+K W keeps its binding and now opens the pill's dropdown, so the keyboard reaches it (Article 7).
        _router.RegisterUiHandler("workspace.switch", invocation => ShowWorkspaceMenu(fromKeyboard: invocation.Trigger is not ("button" or "mouse")));
        _router.RegisterLocal("workspace.openFolder", _ =>
            ShowNotice("Workspaces arrive in a later version. Until then the workspace is the repository that holds the active folder."));
    }

    // ----- The hamburger menu and the workspace dropdown -----

    // Glyphs for the hamburger's commands (Segoe Fluent Icons); a command without one shows none.
    private static string? MenuGlyph(string commandId) => commandId switch
    {
        "tab.new" => "\uE710",
        "file.newFolder" => "\uE8F4",
        "search.focus" => "\uE721",
        "go.toPath" => "\uE8AD",
        "view.toggleSidebar" => "\uE89F",
        "marketplace.browse" => "\uE719",
        "keys.open" => "\uE765",
        "update.check" => "\uE895",
        "update.apply" => "\uE777",
        _ => null,
    };

    /// <summary>
    /// The hamburger (SHELL_REDESIGN.md §1): the common commands with their
    /// titles and keys as the registry has them now (ShellMenu), in the
    /// acrylic menu under the button. A click outside or Esc closes it.
    /// </summary>
    private void ShowShellMenu(bool fromKeyboard)
    {
        if (FileMenu.IsOpen)
        {
            FileMenu.Close();
            return;
        }
        // While an update waits, Check for Updates gives its place to Restart to Update with a dot (Phase 17).
        var items = ShellMenu.Build(_router.Commands, _update.WaitingVersion)
            .Select(item => new MenuEntry(MenuEntryKind.Item, item.Title, MenuGlyph(item.CommandId), item.CommandId, Keys: item.Keys, Dot: item.Dot))
            .ToList();
        FileMenu.Show(Below(MenuButton), [], items, fromKeyboard, WindowMetrics.Current.DropdownRowHeight);
        Diag.Info(ShellTarget, "menu shown", new LogField("items", items.Count));
    }

    /// <summary>
    /// The workspace pill's dropdown, also on Ctrl+K W (workspace.switch):
    /// the workspaces (only "Default" until workspaces exist, with its
    /// branch), a divider, and "Open folder as workspace…", which says that
    /// workspaces arrive in a later version.
    /// </summary>
    private void ShowWorkspaceMenu(bool fromKeyboard)
    {
        if (FileMenu.IsOpen)
        {
            FileMenu.Close();
            return;
        }
        var items = new List<MenuEntry>
        {
            new(MenuEntryKind.Item, WorkspaceName.Text, "\uE73E", Keys: _workspace?.Branch,
                Tooltip: _workspace is { Branch: not null } workspace ? workspace.Root : "The only workspace until workspaces arrive"),
            MenuEntry.Separator,
            new(MenuEntryKind.Item, "Open folder as workspace…", "\uE8DA", "workspace.openFolder"),
        };
        FileMenu.Show(Below(WorkspacePillFrame), [], items, fromKeyboard, WindowMetrics.Current.DropdownRowHeight);
        Diag.Info(ShellTarget, "workspace menu shown", new LogField("branch", _workspace?.Branch ?? ""));
    }

    // The point under a top-row control's left edge, in the window's coordinates: where its dropdown opens.
    private static Point Below(FrameworkElement trigger) =>
        trigger.TransformToVisual(null).TransformPoint(new Point(0, trigger.ActualHeight + 4));

    /// <summary>
    /// Settings (Ctrl+,; SHELL_REDESIGN.md §5): cabinetos.json opens for
    /// editing with the program F4 uses (files.editor, the type's edit verb,
    /// or Notepad; the core picks). The core says where the file is.
    /// </summary>
    private async Task OpenSettingsAsync(string requestId)
    {
        switch (await RequestSafelyAsync(new GetConfigRequest { Id = requestId }))
        {
            case ConfigReply { Path.Length: > 0 } config:
                await EditPathAsync(config.Path, "cabinetos.json", requestId);
                break;
            case ErrorReply error:
                ShowNotice($"Cannot find cabinetos.json: {error.Message}", isError: true);
                break;
        }
    }

    // ----- The workspace pill -----

    /// <summary>The folder Quick Open searches: the repository that holds the active folder, else the active folder.</summary>
    private string WorkspaceRoot()
    {
        var folder = Active.Path;
        return _workspace is { } workspace && string.Equals(_workspaceFor, folder, StringComparison.OrdinalIgnoreCase)
            ? workspace.Root
            : folder;
    }

    /// <summary>
    /// Asks the core which repository holds the active folder
    /// (<c>workspace_info</c>: the core reads the repository's small files,
    /// the window none, brief §1) and shows its branch in the pill. Runs when
    /// the active folder changes and when the window comes to the front, so a
    /// branch switched in a terminal shows. An older core, or a lost pipe,
    /// leaves the pill without a branch.
    /// </summary>
    private async Task UpdateWorkspaceAsync()
    {
        var folder = Active.Path;
        var asked = ++_workspaceAsked;
        WorkspaceInfoReply? workspace = null;
        if (folder.Length > 0 && !_unavailable.Contains("workspace_info"))
        {
            try
            {
                switch (await _session.RequestAsync(new WorkspaceInfoRequest(folder)))
                {
                    case WorkspaceInfoReply reply:
                        workspace = reply;
                        break;
                    case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                        _unavailable.Add("workspace_info");
                        Diag.Info(ShellTarget, "the core does not answer workspace_info; the pill shows no branch");
                        break;
                    case ErrorReply error:
                        Diag.Debug(ShellTarget, "workspace_info refused", new LogField("code", error.Code));
                        break;
                }
            }
            catch (IOException error)
            {
                Diag.Debug(ShellTarget, "cannot ask for the workspace", new LogField("error", error.Message));
            }
        }
        if (asked != _workspaceAsked)
        {
            return;
        }
        var changed = workspace != _workspace;
        _workspaceFor = folder;
        _workspace = workspace;
        if (changed)
        {
            ShowBranch();
            Diag.Info(ShellTarget, "workspace pill shows a branch", new LogField("root", workspace?.Root ?? folder),
                new LogField("branch", workspace?.Branch ?? ""));
        }
    }

    // The pill: the workspace's name, and the branch in the mono font when the active folder is in a repository on one.
    private void ShowBranch()
    {
        var branch = _workspace?.Branch;
        WorkspaceBranch.Text = branch ?? "";
        WorkspaceBranch.Visibility = branch is null ? Visibility.Collapsed : Visibility.Visible;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(WorkspacePill,
            branch is null ? $"Workspace {WorkspaceName.Text}" : $"Workspace {WorkspaceName.Text}, branch {branch}");
        ToolTipService.SetToolTip(WorkspacePill, _workspace is { Branch: not null } workspace ? $"{WorkspaceName.Text} · {workspace.Root}" : WorkspaceName.Text);
    }

    // ----- The breadcrumb rows -----

    /// <summary>Each pane's breadcrumb row shows its folder; the active pane's row is tinted.</summary>
    private void UpdateCrumbs()
    {
        if (_crumbViews is null)
        {
            return;
        }
        for (var i = 0; i < _crumbViews.Length; i++)
        {
            _crumbViews[i].Show(_panes[i].Path, _dual);
            _crumbViews[i].IsActivePane = _dual ? i == _active : i == 0;
        }
    }

    /// <summary>Each pane's Back, Forward and Up: Forward without a forward entry is drawn at 30 % (NavState).</summary>
    private void UpdateNavigationButtons()
    {
        if (_crumbViews is null)
        {
            return;
        }
        for (var i = 0; i < _crumbViews.Length; i++)
        {
            var pane = _panes[i];
            _crumbViews[i].SetNav(new NavState(pane.CanGoBack, pane.CanGoForward, pane.CanGoUp));
        }
    }

    // The pane a command names ({"pane": 0 or 1}, the breadcrumb row's buttons), else the active one.
    private int PaneOf(Core.Commands.CommandInvocation invocation) =>
        CommandArgs.Number(invocation.Args, "pane") is { } named and <= 1 ? (int)named : _active;

    /// <summary>Ctrl+L: the active pane's breadcrumb row becomes a text box with its path.</summary>
    private void BeginAddressEdit(int pane)
    {
        if (pane != _active)
        {
            SetActive(pane);
        }
        _crumbViews[pane].BeginEdit(_panes[pane].Path);
    }

    /// <summary>Every breadcrumb row shows its segments again.</summary>
    private void EndAddressEdit()
    {
        if (_crumbViews is null)
        {
            return;
        }
        foreach (var crumbs in _crumbViews)
        {
            crumbs.EndEdit();
        }
    }

    private bool IsEditingAddress => _crumbViews is not null && _crumbViews.Any(c => c.IsEditing);

    // ----- The snapshot aid -----

    // shell:<label>: what the top row and each pane's rows show, in the log ("shell state"), for the checks that read it.
    private void LogShellState(string label)
    {
        var fields = new List<LogField>
        {
            new("label", label),
            new("window", string.Create(CultureInfo.InvariantCulture, $"{RootGrid.ActualWidth:0}x{RootGrid.ActualHeight:0}")),
            new("top_row", Math.Round(TopBar.ActualHeight, 1)),
            new("command_center", _centerPlace.Visible ? string.Create(CultureInfo.InvariantCulture, $"{_centerPlace.Left:0}+{_centerPlace.Width:0}") : "hidden"),
            new("left_cluster_end", Math.Round(TopLeft.ActualWidth, 1)),
            new("right_cluster_start", Math.Round(TopRowGrid.ActualWidth - TopRight.ActualWidth, 1)),
            new("workspace", WorkspaceName.Text),
            new("workspace_root", WorkspaceRoot()),
            new("quick_open", _quickOpen.IsOpen),
            new("quick_open_rows", string.Join("|", _quickOpen.Rows.Take(10).Select(r => r.Folder.Length > 0 ? $"{r.Name} ({r.Folder})" : r.Name))),
            new("quick_open_highlight", _quickOpen.Highlight),
            new("palette_open", _palette.IsOpen),
            new("menu", FileMenu.Describe()),
            new("context_menu", _contextMenu.DescribeItems()),
            new("context_quick", _contextMenu.DescribeQuickActions()),
            new("context_menu_on_screen", _contextMenu.IsOnScreen),
            new("menu_edit", MenuEditorView.Describe()),
            new("menu_edit_target", MenuEditorView.Model?.TargetName ?? ""),
            new("windows_menu", _windowsMenu.Describe()),
            new("branch", WorkspaceBranch.Visibility == Visibility.Visible ? WorkspaceBranch.Text : ""),
            new("active_pane", _active),
            new("update_pill", UpdatePill.Visibility == Visibility.Visible ? UpdatePillText.Text : ""),
            new("update_dot", MenuUpdateDot.Visibility == Visibility.Visible),
        };
        for (var i = 0; i < _panes.Length; i++)
        {
            var pane = _panes[i];
            fields.Add(new($"pane{i}_path", pane.Path));
            fields.Add(new($"pane{i}_crumbs", _crumbViews[i].Text));
            fields.Add(new($"pane{i}_nav", _crumbViews[i].NavText));
            fields.Add(new($"pane{i}_editing", _crumbViews[i].IsEditing));
            fields.Add(new($"pane{i}_shown", pane.ShownCount));
            fields.Add(new($"pane{i}_count", pane.Count));
            fields.Add(new($"pane{i}_selected", string.Join("|", pane.SelectedNames().Take(20))));
            fields.Add(new($"pane{i}_all_selected", string.Join("|", pane.AllSelectedNames().Take(20))));
            fields.Add(new($"pane{i}_cursor", pane.FocusName ?? ""));
            fields.Add(new($"pane{i}_scroll", Math.Round(_paneViews[i].ScrollOffset, 1)));
            fields.Add(new($"pane{i}_find", pane.Find.Query ?? ""));
            fields.Add(new($"pane{i}_find_open", _findViews[i].IsOpen));
            fields.Add(new($"pane{i}_find_count", _findViews[i].Count));
            fields.Add(new($"pane{i}_tabs", _tabViews[i].Describe()));
            fields.Add(new($"pane{i}_tab_row", Math.Round(_tabViews[i].RowHeight, 1)));
            fields.Add(new($"pane{i}_crumb_row", Math.Round(_crumbViews[i].ActualHeight, 1)));
        }
        Diag.Info("cabinetos_ui::snapshot", "shell state", [.. fields]);
    }
}
