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

// The shell (the creator's SHELL_REDESIGN.md, v2 of 2026-10-01; docs/ui.md, "The top row", "The pane's rows"): one quiet
// top row that is also the window's drag area, and in each pane a toolbar row and a path row. What they decide without a
// window is in CabinetOS.Core.Shell, with tests.
public sealed partial class MainWindow
{
    private const string ShellTarget = "cabinetos_ui::shell";

    // Windows draws the caption buttons this high (TitleBarHeightOption.Standard): the top row is never lower.
    private const double CaptionButtonsHeight = 32;

    // The workspace's name until workspaces exist (docs/ui.md, "The sidebar header").
    private const string WorkspaceTitle = "Default";

    private PaneCrumbs[] _crumbViews = null!;
    private string _dragRegions = "";

    // The title's parts: the app's name at weight 600, the dot at 35 %, the folder at 60 %.
    private readonly Microsoft.UI.Xaml.Documents.Run _titleApp = new() { Text = TopRowLayout.AppName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly Microsoft.UI.Xaml.Documents.Run _titleSeparator = new();
    private readonly Microsoft.UI.Xaml.Documents.Run _titleFolder = new();

    // The workspace until workspaces exist (docs/ui.md, "The sidebar header"): the repository that holds the active folder,
    // for its branch and Quick Open's folder. The core finds it (workspace_info) whenever the active folder changes.
    private WorkspaceInfoReply? _workspace;
    private string _workspaceFor = "";
    private int _workspaceAsked;

    // The questions to the core that are out (workspace_info): the snapshot aid's until:workspace waits for none.
    private int _workspaceAsking;

    private void SetUpShell()
    {
        _crumbViews = [LeftCrumbs, RightCrumbs];
        for (var i = 0; i < _crumbViews.Length; i++)
        {
            _crumbViews[i].PaneIndex = i;
            _crumbViews[i].RunCommand = (id, args, trigger) => _router.ExecuteAsync(id, args, trigger);
        }
        MenuButton.Click += (_, _) => _ = _router.ExecuteAsync("menu.show", trigger: "button");
        WorkspaceHeader.Click += (_, _) => ShowWorkspaceMenu(fromKeyboard: false);
        QuickOpenChip.Click += (_, _) => _ = _router.ExecuteAsync("quickOpen.show", trigger: "button");
        SettingsButton.Click += (_, _) => _ = _router.ExecuteAsync("settings.open", trigger: "button");
        _palette.Opened += UpdatePaletteButton;
        _palette.Closed += UpdatePaletteButton;
        _titleApp.Foreground = ThemeResources.Brush("CbRowTextBrush");
        _titleSeparator.Foreground = ThemeResources.Brush("CbCrumbSeparatorBrush");
        _titleFolder.Foreground = ThemeResources.Brush("CbTitleFolderBrush");
        TitleText.Inlines.Add(_titleApp);
        TitleText.Inlines.Add(_titleSeparator);
        TitleText.Inlines.Add(_titleFolder);
        // The chip and the workspace row say the keys their commands have now, so a rebinding shows there too.
        _router.CommandsChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            ShowQuickOpenKeys();
            ShowBranch();
        });
        // Each toolbar's free space follows the volumes the sidebar's drives come from (list_volumes, and its changes).
        _sidebar.DrivesChanged += UpdatePaneDrives;
        // The top row's layout: the title up to the chip, and the drag area around its controls.
        TopRowGrid.SizeChanged += (_, _) => LayOutTopRow();
        TopRight.SizeChanged += (_, _) => LayOutTopRow();
        ShowBranch();
        UpdateTitle();
    }

    // ----- The top row -----

    /// <summary>
    /// The title takes the room up to the Quick Open chip and no more
    /// (<see cref="TopRowLayout.TitleRoom"/>): a long folder name ends with an
    /// ellipsis, and the chip never hides. Then the drag area follows.
    /// </summary>
    private void LayOutTopRow()
    {
        var titleLeft = TopLeft.Padding.Left + MenuButton.ActualWidth + AppTile.ActualWidth + (2 * TopLeft.Spacing);
        var chipLeft = TopRowGrid.ActualWidth - TopRight.ActualWidth;
        TitleText.MaxWidth = TopRowLayout.TitleRoom(titleLeft, chipLeft);
        UpdateDragRegions();
    }

    /// <summary>"CabinetOS · folder": the title follows the active pane's front tab (a folder's name, a tool tab's file).</summary>
    private void UpdateTitle()
    {
        if (_strips is null)
        {
            return;
        }
        var strip = _strips[_dual ? _active : 0];
        var name = strip.Count > 0 ? strip.Active.Title : "";
        var title = TopRowLayout.Title(name);
        _titleSeparator.Text = title.Length > TopRowLayout.AppName.Length ? TopRowLayout.Separator : "";
        _titleFolder.Text = title.Length > TopRowLayout.AppName.Length ? name : "";
    }

    // The chip's key: Quick Open's first binding as the registry has it now, Ctrl+P until the registry is read.
    private void ShowQuickOpenKeys()
    {
        var keys = KeysOf("quickOpen.show") ?? "Ctrl+P";
        QuickOpenKeys.Text = keys;
        ToolTipService.SetToolTip(QuickOpenChip, $"Quick Open ({keys}) · type > for commands");
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
    /// menu, the Quick Open chip or a view button reaches it.
    /// </summary>
    private void UpdateDragRegions()
    {
        if (RootGrid.XamlRoot is not { } root || TopBar.ActualWidth <= 0)
        {
            return;
        }
        var scale = root.RasterizationScale;
        var rects = new List<RectInt32>();
        foreach (var element in new FrameworkElement[] { MenuButton, QuickOpenChipFrame, DualButton, TerminalButton, MarketplaceButton, PaletteButton, SettingsButton })
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
        // Ctrl+K W keeps its binding and opens the workspace dropdown, so the keyboard reaches it (Article 7).
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
    /// The workspace dropdown, on Ctrl+K W (workspace.switch) and the palette:
    /// the workspaces (only "Default" until workspaces exist, with its
    /// branch; picking it goes to its root in the left pane's tab in front),
    /// a divider, and "Open folder as workspace…", which says that workspaces
    /// arrive in a later version.
    /// </summary>
    private void ShowWorkspaceMenu(bool fromKeyboard)
    {
        if (FileMenu.IsOpen)
        {
            FileMenu.Close();
            return;
        }
        var root = WorkspaceRoot();
        var items = new List<MenuEntry>
        {
            new(MenuEntryKind.Item, WorkspaceTitle, "\uE73E", root.Length > 0 ? "go.toPath" : null,
                root.Length > 0 ? CommandArgs.Object(("path", root), ("pane", 0)) : null, Keys: _workspace?.Branch,
                Tooltip: root.Length > 0 ? root : "The only workspace until workspaces arrive"),
            MenuEntry.Separator,
            new(MenuEntryKind.Item, "Open folder as workspace…", "\uE8DA", "workspace.openFolder"),
        };
        var (at, width) = WorkspaceMenuPlace();
        FileMenu.Show(at, [], items, fromKeyboard, WindowMetrics.Current.DropdownRowHeight, width);
        Diag.Info(ShellTarget, "workspace menu shown", new LogField("branch", _workspace?.Branch ?? ""), new LogField("root", root),
            new LogField("left", Math.Round(at.X, 1)), new LogField("top", Math.Round(at.Y, 1)), new LogField("width", width is { } w ? Math.Round(w, 1) : null));
    }

    // The least width of the workspace dropdown: the design's min-width, for a narrow sidebar.
    private const double WorkspaceMenuMinWidth = 220;

    // Where the workspace dropdown opens: under the sidebar's workspace row at its full width (at least 220 px); while the
    // sidebar is hidden (Ctrl+B), under the top row at the panes' left edge, at the menu's own width.
    private (Point At, double? Width) WorkspaceMenuPlace()
    {
        if (SidebarHost.Visibility == Visibility.Visible && WorkspaceHeaderFrame.ActualWidth > 0)
        {
            var below = WorkspaceHeaderFrame.TransformToVisual(null).TransformPoint(new Point(0, WorkspaceHeaderFrame.ActualHeight));
            return (below, Math.Max(WorkspaceMenuMinWidth, WorkspaceHeaderFrame.ActualWidth));
        }
        var left = MainColumn.TransformToVisual(null).TransformPoint(new Point(0, 0)).X;
        return (new Point(left, TopBar.ActualHeight), null);
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

    // ----- The workspace and its branch -----

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
    /// the window none, brief §1) and shows its branch in the sidebar's workspace
    /// row. Runs when the active folder changes and when the window comes to the
    /// front, so a branch switched in a terminal shows. An older core, or a lost
    /// pipe, leaves the row without a branch.
    /// </summary>
    private async Task UpdateWorkspaceAsync()
    {
        var folder = Active.Path;
        var asked = ++_workspaceAsked;
        WorkspaceInfoReply? workspace = null;
        if (folder.Length > 0 && !_unavailable.Contains("workspace_info"))
        {
            _workspaceAsking++;
            try
            {
                switch (await _session.RequestAsync(new WorkspaceInfoRequest(folder)))
                {
                    case WorkspaceInfoReply reply:
                        workspace = reply;
                        break;
                    case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                        _unavailable.Add("workspace_info");
                        Diag.Info(ShellTarget, "the core does not answer workspace_info; the workspace row shows no branch");
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
            finally
            {
                _workspaceAsking--;
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
            Diag.Info(ShellTarget, "workspace shows a branch", new LogField("root", workspace?.Root ?? folder),
                new LogField("branch", workspace?.Branch ?? ""));
        }
    }

    // The sidebar's workspace row: the workspace's name, and the branch in the mono font when the active folder is in a
    // repository on one.
    private void ShowBranch()
    {
        var branch = _workspace?.Branch;
        WorkspaceName.Text = WorkspaceTitle;
        WorkspaceBranch.Text = branch ?? "";
        WorkspaceBranch.Visibility = branch is null ? Visibility.Collapsed : Visibility.Visible;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(WorkspaceHeader,
            branch is null ? $"Workspace {WorkspaceTitle}" : $"Workspace {WorkspaceTitle}, branch {branch}");
        var keys = KeysOf("workspace.switch") is { } key ? $" ({key})" : "";
        ToolTipService.SetToolTip(WorkspaceHeader, _workspace is { Branch: not null } workspace
            ? $"{WorkspaceTitle} · {workspace.Root}{Environment.NewLine}Switch workspace{keys}"
            : $"Switch workspace{keys}");
    }

    // ----- The pane's rows: the toolbar row and the path row -----

    /// <summary>
    /// Each pane's path row shows its folder, and its toolbar the drive of that
    /// folder; the active pane's toolbar has the fill of its tab in front.
    /// </summary>
    private void UpdateCrumbs()
    {
        if (_crumbViews is null)
        {
            return;
        }
        for (var i = 0; i < _crumbViews.Length; i++)
        {
            _crumbViews[i].Show(TabFolder(i), _dual);
            _crumbViews[i].IsActivePane = _dual ? i == _active : i == 0;
        }
        UpdatePaneDrives();
    }

    /// <summary>
    /// Each toolbar's drive chip and free space: the letter of the pane's
    /// folder, and that drive's free space as the core's last
    /// <c>list_volumes</c> said it. The window reads nothing from the disk.
    /// </summary>
    private void UpdatePaneDrives()
    {
        if (_crumbViews is null)
        {
            return;
        }
        for (var i = 0; i < _crumbViews.Length; i++)
        {
            var folder = TabFolder(i);
            _crumbViews[i].SetDrive(PaneRows.DriveLabel(folder), PaneRows.FreeSpace(_sidebar.Volumes, folder));
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
            new("title", TitleText.Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().Aggregate("", (text, run) => text + run.Text)),
            new("title_trimmed", TitleText.IsTextTrimmed),
            new("title_right", Math.Round(LeftOf(TitleText) + TitleText.ActualWidth, 1)),
            new("chip_left", Math.Round(LeftOf(QuickOpenChipFrame), 1)),
            new("chip_right", Math.Round(LeftOf(QuickOpenChipFrame) + QuickOpenChipFrame.ActualWidth, 1)),
            new("chip_height", Math.Round(QuickOpenChipFrame.ActualHeight, 1)),
            new("chip_visible", QuickOpenChipFrame.Visibility == Visibility.Visible && QuickOpenChipFrame.ActualWidth > 0),
            new("chip_keys", QuickOpenKeys.Text),
            new("right_cluster_start", Math.Round(TopRowGrid.ActualWidth - TopRight.ActualWidth, 1)),
            new("right_cluster_end", Math.Round(LeftOf(SettingsButton) + SettingsButton.ActualWidth, 1)),
            new("caption_start", Math.Round(LeftOf(CaptionSpace), 1)),
            new("workspace", WorkspaceTitle),
            new("workspace_header", WorkspaceHeaderFrame.Visibility == Visibility.Visible && WorkspaceHeaderFrame.ActualWidth > 0 && SidebarHost.Visibility == Visibility.Visible),
            new("workspace_header_height", Math.Round(WorkspaceHeaderFrame.ActualHeight, 1)),
            new("workspace_header_width", Math.Round(WorkspaceHeaderFrame.ActualWidth, 1)),
            new("workspace_header_left", Math.Round(LeftOf(WorkspaceHeaderFrame), 1)),
            new("workspace_header_bottom", Math.Round(BottomOf(WorkspaceHeaderFrame), 1)),
            new("workspace_header_branch", WorkspaceBranch.Visibility == Visibility.Visible ? WorkspaceBranch.Text : ""),
            new("sidebar_shown", SidebarHost.Visibility == Visibility.Visible),
            new("workspace_root", WorkspaceRoot()),
            new("quick_open", _quickOpen.IsOpen),
            new("quick_open_rows", string.Join("|", _quickOpen.Rows.Take(10).Select(r => r.Folder.Length > 0 ? $"{r.Name} ({r.Folder})" : r.Name))),
            new("quick_open_highlight", _quickOpen.Highlight),
            new("palette_open", _palette.IsOpen),
            new("prompt_open", PromptView.IsOpen),
            new("menu", FileMenu.Describe()),
            new("context_menu", _contextMenu.DescribeItems()),
            new("context_quick", _contextMenu.DescribeQuickActions()),
            new("context_menu_on_screen", _contextMenu.IsOnScreen),
            new("menu_edit", MenuEditorView.Describe()),
            new("menu_edit_target", MenuEditorView.Model?.TargetName ?? ""),
            new("windows_menu", _windowsMenu.Describe()),
            new("branch", _workspace?.Branch ?? ""),
            new("active_pane", _active),
            new("update_pill", UpdatePill.Visibility == Visibility.Visible ? UpdatePillText.Text : ""),
            new("update_dot", MenuUpdateDot.Visibility == Visibility.Visible),
            new("update_notice", UpdateNotice.Visibility == Visibility.Visible ? UpdateNoticeText.Text : ""),
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
            var (toolbarRow, pathRow) = _crumbViews[i].RowHeights;
            fields.Add(new($"pane{i}_toolbar_row", Math.Round(toolbarRow, 1)));
            fields.Add(new($"pane{i}_path_row", Math.Round(pathRow, 1)));
            fields.Add(new($"pane{i}_width", Math.Round(_crumbViews[i].ActualWidth, 1)));
            fields.Add(new($"pane{i}_toolbar_items", _crumbViews[i].ToolbarItems()));
            fields.Add(new($"pane{i}_crumbs_fit", _crumbViews[i].CrumbsFit));
            fields.Add(new($"pane{i}_filter", _crumbViews[i].FilterLabelText));
            fields.Add(new($"pane{i}_find_button_open", _crumbViews[i].FindShownOpen));
            fields.Add(new($"pane{i}_drive", _crumbViews[i].DriveShown.Drive));
            fields.Add(new($"pane{i}_free", _crumbViews[i].DriveShown.Free));
            fields.Add(new($"pane{i}_toolbar_fill", BrushText(_crumbViews[i].ToolbarFillKey)));
            fields.Add(new($"pane{i}_tab_fill", BrushText(_tabViews[i].FrontFillKey)));
            // Where the find widget hangs: from the toolbar row's bottom edge, at the pane's right end.
            fields.Add(new($"pane{i}_toolbar_bottom", Math.Round(TopOf(_crumbViews[i]) + toolbarRow, 1)));
            fields.Add(new($"pane{i}_find_top", _findViews[i].IsOpen ? Math.Round(TopOf(_findViews[i]), 1) : 0));
            fields.Add(new($"pane{i}_find_right_gap", _findViews[i].IsOpen
                ? Math.Round(LeftOf(_crumbViews[i]) + _crumbViews[i].ActualWidth - (LeftOf(_findViews[i]) + _findViews[i].ActualWidth), 1)
                : 0));
            fields.Add(new($"pane{i}_tab_look", _tabViews[i].DescribeLook()));
            // The listings the pane holds: one per column in the column view (ADR 0016), so a dropped column's is seen to go.
            fields.Add(new($"pane{i}_listings", ListingCount(i)));
            fields.Add(new($"pane{i}_columns", _columnViews[i]?.Count ?? 0));
        }
        Diag.Info("cabinetos_ui::snapshot", "shell state", [.. fields]);
    }

    // A theme brush as the log names it: its key and its colour, "CbFrontTabFillBrush #17FFFFFF".
    private static string BrushText(string key) =>
        ThemeResources.Brush(key) is Microsoft.UI.Xaml.Media.SolidColorBrush solid ? $"{key} {solid.Color}" : key;

    // An element's left edge in the window, for the snapshot aid's log.
    private static double LeftOf(FrameworkElement element) =>
        element.ActualWidth > 0 ? element.TransformToVisual(null).TransformPoint(new Point(0, 0)).X : 0;

    // An element's top edge in the window, for the snapshot aid's log.
    private static double TopOf(FrameworkElement element) =>
        element.ActualHeight > 0 ? element.TransformToVisual(null).TransformPoint(new Point(0, 0)).Y : 0;

    // An element's bottom edge in the window, for the snapshot aid's log.
    private static double BottomOf(FrameworkElement element) =>
        element.ActualHeight > 0 ? element.TransformToVisual(null).TransformPoint(new Point(0, element.ActualHeight)).Y : 0;
}
