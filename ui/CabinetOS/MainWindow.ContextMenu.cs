using System.Diagnostics;
using System.Text.Json;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Platform;
using CabinetOS.Core.Prompts;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Shell;
using CabinetOS.Services;
using CabinetOS.ViewModels;
using CabinetOS.Views;
using Windows.Foundation;

namespace CabinetOS;

// The right-click menu of Phase 18 (docs/ui.md, "The context menu"): built from contextMenu of cabinetos.json at
// every opening (ContextMenuModel, tested without XAML), drawn as WinUI's CommandBarFlyout; its edit mode ("Edit
// Menu…", ContextMenuEditModel), which the core saves (set_value); and Windows' own menu, which the core builds
// (shell_menu) when contextMenu.shellMenu is on. The window reads and writes no file for any of them (brief §1).
public sealed partial class MainWindow
{
    private const string MenuTarget = "cabinetos_ui::context_menu";

    // Windows' menu of more files than this is not asked for: the shell's handlers slow down with every item, and the
    // core gives up after 3 s. The window's state carries at most as many marked paths.
    private const int MaxWindowsMenuPaths = 1000;

    private readonly ContextMenuFlyout _contextMenu = new();
    private readonly ShellMenuFlyout _windowsMenu = new();
    private readonly HashSet<string> _menuWarned = new(StringComparer.Ordinal);
    private ContextMenuConfig _menuConfig = ContextMenuConfig.Defaults;

    // The pane the open menu is about, and what it was opened on: a plugin's entry gets that pane's paths when it runs.
    private (int Pane, ContextMenuFacts Facts)? _menuFor;

    // Where the open menu was asked for (the window's coordinates) and whether from the keyboard: its edit mode opens there.
    private (Point At, bool Keyboard)? _menuAt;

    // A Windows menu that answers after a newer gesture is not shown.
    private int _windowsMenuAsked;

    private void SetUpContextMenu()
    {
        foreach (var view in _paneViews)
        {
            view.ShellMenuRequested += OnShellMenuRequested;
        }
        _contextMenu.Closed += () =>
        {
            Diag.Info(MenuTarget, "context menu closed");
            FocusActivePane();
        };
        _contextMenu.Opened += () => Diag.Info(MenuTarget, "context menu opened");
        _contextMenu.Run = RunMenuEntry;
        _windowsMenu.Closed += () =>
        {
            Diag.Info(MenuTarget, "windows menu closed");
            FocusActivePane();
        };
        _windowsMenu.Invoke = (menu, item) => _ = InvokeWindowsMenuItemAsync(menu, item);
        _router.RegisterUiHandler(ContextMenuModel.EditMenu, EditMenu);
        MenuEditorView.Save = SaveMenuEditAsync;
        MenuEditorView.PickCommand = PickMenuCommandAsync;
        MenuEditorView.OpenFile = () =>
        {
            // The file and the edit mode must not both change the list: the edit ends unsaved first.
            MenuEditorView.Cancel();
            _ = _router.ExecuteAsync("settings.open", trigger: "menu");
        };
        MenuEditorView.Closed += saved =>
        {
            Diag.Info(MenuTarget, "menu edit closed", new LogField("saved", saved));
            FocusActivePane();
        };
        _router.RegisterUiHandler("menu.showShell", _ =>
        {
            ApplyCursorKeys();
            OnShellMenuRequested(_paneViews[_active], Active.FocusIndex, null);
        });
        _router.Executing += invocation =>
        {
            if (!invocation.CommandId.StartsWith("program.", StringComparison.Ordinal))
            {
                return;
            }
            // The core fills {path} and {selection} from the state it stores: it must be this moment's, not the one
            // the 50 ms timer would send. The request goes out before execute_command, on the same pipe.
            ApplyCursorKeys();
            _stateTimer.Stop();
            _ = SendWindowStateAsync();
            // The program starts from the core, a background process: let it come to the front.
            if (_session.CoreProcessId is { } corePid)
            {
                WindowsPlatform.AllowForeground(corePid);
            }
        };
    }

    // contextMenu of the configuration the core sent (at start, and after each config_changed).
    private void ApplyMenuConfig(JsonElement config)
    {
        _menuConfig = ContextMenuConfig.FromConfig(config);
        // An ID left out once is logged again after a change: a plugin may have come, or the file been fixed.
        _menuWarned.Clear();
    }

    private void OnContextMenuRequested(FilePane view, int index, Point? at)
    {
        var paneIndex = Array.IndexOf(_paneViews, view);
        if (paneIndex < 0)
        {
            return;
        }
        _windowsMenuAsked++;
        SetActive(paneIndex);
        var pane = _panes[paneIndex];
        var position = at ?? view.RowAnchor(index);
        EndAddressEdit();
        FileMenu.Close();
        _windowsMenu.Close();
        var started = Stopwatch.GetTimestamp();
        var facts = MenuFacts(pane, index);
        var menu = ContextMenuModel.Build(_menuConfig, facts, _router.Commands, KeysOf);
        foreach (var id in menu.Unknown.Where(_menuWarned.Add))
        {
            Diag.Warn(MenuTarget, "context menu entry left out: no command has this ID", new LogField("command", id));
        }
        _menuFor = (paneIndex, facts);
        _menuAt = (position, at is null);
        _contextMenu.Show(view, position, menu);
        Diag.Info(MenuTarget, "context menu shown",
            new LogField("target", facts.Kind.ToString()),
            new LogField("quick_actions", menu.QuickActions.Count),
            new LogField("items", menu.Items.Count(i => i.Kind == ContextMenuEntryKind.Item)),
            new LogField("keyboard", at is null),
            new LogField("build_ms", Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 2)));
    }

    // "Edit Menu…" (menu.edit): the menu it was chosen from turns into its edit mode, in its place (docs/ui.md, "Editing
    // the menu"). From the palette or a key there is no open menu: the focused row's menu is edited, where Shift+F10
    // would open it.
    private void EditMenu(CommandInvocation invocation)
    {
        int paneIndex;
        ContextMenuFacts facts;
        Rect? bounds = null;
        Point at;
        bool keyboard;
        if (invocation.Trigger == "menu" && _menuFor is var (menuPane, menuFacts) && _menuAt is var (menuAt, menuKeyboard))
        {
            (paneIndex, facts, at, keyboard) = (menuPane, menuFacts, menuAt, menuKeyboard);
            bounds = _contextMenu.ChosenBounds;
        }
        else
        {
            ApplyCursorKeys();
            paneIndex = _active;
            var index = Active.FocusIndex;
            facts = MenuFacts(Active, index);
            at = _paneViews[paneIndex].RowAnchor(index);
            keyboard = true;
        }
        var started = Stopwatch.GetTimestamp();
        EndAddressEdit();
        FileMenu.Close();
        _windowsMenu.Close();
        _contextMenu.Close();
        var commands = _router.Commands;
        var model = new ContextMenuEditModel(facts.Kind, _menuConfig.For(facts.Kind).Items, commands, KeysOf);
        MenuEditorView.Show(model, ContextMenuEditModel.Fixed(_menuConfig, facts, commands, KeysOf), bounds, at, keyboard);
        Diag.Info(MenuTarget, "menu edit shown",
            new LogField("target", facts.Kind.ToString()),
            new LogField("pane", paneIndex),
            new LogField("rows", model.Items.Count),
            new LogField("in_menu_place", bounds is not null),
            new LogField("keyboard", keyboard),
            new LogField("build_ms", Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 2)));
    }

    // "Done": the list goes to the core (set_value contextMenu.<target>.items), which writes the file and announces
    // config_changed; the next opening shows the new menu. A refusal keeps the edit mode open with a notice.
    private async Task<bool> SaveMenuEditAsync(ContextMenuEditModel model)
    {
        var request = new SetValueRequest(model.SettingPath, model.ToValue());
        switch (await RequestSafelyAsync(request))
        {
            case OkReply:
                Diag.Info(MenuTarget, "menu edit saved", new LogField("path", model.SettingPath), new LogField("rows", model.Describe()));
                ShowNotice($"{model.TargetName} saved.");
                return true;
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                ShowNotice("This core cannot change settings (set_value): the menu was not saved.", isError: true);
                return false;
            case ErrorReply error:
                Diag.Request(LogLevel.Warn, request.Id, MenuTarget, "menu edit refused",
                    new LogField("path", model.SettingPath), new LogField("code", error.Code), new LogField("error", error.Message));
                ShowNotice($"The menu was not saved: {error.Message}", isError: true);
                return false;
            default:
                // No answer: RequestSafelyAsync showed why.
                return false;
        }
    }

    // "Add Command…" (Insert): the commands that may sit in a file pane's menu, in the palette's frame; typing narrows them.
    private async Task<string?> PickMenuCommandAsync(ContextMenuEditModel model)
    {
        var rows = model.Addable().Select(c => new PromptRow(ContextMenuEditModel.TitleOf(c), c.Id, ContextMenuModel.GlyphOf(c.Id) ?? "")).ToList();
        var answer = await PromptView.ShowAsync(new PromptRequest("Add", PromptKind.Pick, rows,
            Placeholder: "Type to narrow the commands",
            Hint: $"Enter adds the command to the {model.TargetName.ToLowerInvariant()}, after the focused row; Esc goes back to it."));
        return answer?.Row?.Detail;
    }

    // What the window knows about the target now; the selection's extensions only if a row asks for them.
    private ContextMenuFacts MenuFacts(PaneModel pane, int index)
    {
        var entry = index >= 0 ? pane.EntryAt(index) : null;
        var selected = pane.Selection.SelectedCount;
        var kind = entry is null ? MenuTargetKind.Background
            : selected > 1 && pane.Selection.IsSelected(index) ? MenuTargetKind.MultiSelect
            : entry.IsFolder ? MenuTargetKind.Folder
            : MenuTargetKind.File;
        return new ContextMenuFacts(
            kind,
            pane.Path,
            entry?.Path,
            entry?.IsFolder ?? false,
            selected,
            _clipboard.IsEmpty,
            _dual,
            RenameAvailable: !_unavailable.Contains("rename"),
            OpenAvailable: !_unavailable.Contains("open_path"),
            CreateDirectoryAvailable: !_unavailable.Contains("create_directory"),
            PanePinned: pane.Path.Length > 0 && _sidebar.IsPinned(pane.Path),
            Selection: () => SelectionExtensionsOf(pane));
    }

    // The selected rows' extensions, from the names the listing holds; a new extension is the only allocation, so
    // 100,000 selected files cost one pass over their names. A folder or a name without one ends it: no filter matches.
    private static SelectionExtensions SelectionExtensionsOf(PaneModel pane)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pane.View is not { } listing)
        {
            return new(found, false, false);
        }
        var lookup = found.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var index in pane.Selection.SelectedUnordered)
        {
            if ((uint)index >= (uint)listing.Count)
            {
                continue;
            }
            if (listing.IsFolder(index))
            {
                return new(found, true, false);
            }
            var name = listing.NameSpan(index);
            var dot = name.LastIndexOf('.');
            if (dot < 0 || dot == name.Length - 1)
            {
                return new(found, false, true);
            }
            if (!lookup.Contains(name[dot..]))
            {
                found.Add(name[dot..].ToString());
            }
        }
        return new(found.Select(e => e.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal), false, false);
    }

    private void RunMenuEntry(ContextMenuEntry entry)
    {
        if (entry.CommandId is not { } id)
        {
            return;
        }
        var args = entry.Args;
        if (entry.WantsTargets && _menuFor is var (paneIndex, facts))
        {
            // Plugins cannot read the selection yet (docs/plugins.md): the menu hands them the paths, read only now.
            var pane = _panes[paneIndex];
            List<string> paths = facts.Kind == MenuTargetKind.Background ? [pane.Path] : [.. pane.Targets().Select(t => t.Path)];
            args = CommandArgs.Object(("path", facts.EntryPath ?? pane.Path), ("paths", paths));
        }
        _ = _router.ExecuteAsync(id, args, "menu");
    }

    // Shift+right-click and menu.showShell: Windows' own menu when contextMenu.shellMenu is on, else what the plain
    // gesture opens. The core builds it; the window only waits for the reply, at most about 3 s.
    private void OnShellMenuRequested(FilePane view, int index, Point? at)
    {
        var paneIndex = Array.IndexOf(_paneViews, view);
        if (paneIndex < 0 || _panes[paneIndex].Search is not null)
        {
            return;
        }
        if (!_menuConfig.ShellMenu || _unavailable.Contains("shell_menu"))
        {
            OnContextMenuRequested(view, index, at);
            return;
        }
        SetActive(paneIndex);
        var pane = _panes[paneIndex];
        EndAddressEdit();
        FileMenu.Close();
        _contextMenu.Close();
        _windowsMenu.Close();
        var entry = index >= 0 ? pane.EntryAt(index) : null;
        List<string> paths = entry is null
            ? (pane.Path.Length > 0 ? [pane.Path] : [])
            : pane.Selection.SelectedCount > 1 && pane.Selection.IsSelected(index)
                ? [.. pane.Selection.SelectedUnordered.Take(MaxWindowsMenuPaths + 1).Order().Select(i => pane.EntryAt(i)?.Path).OfType<string>()]
                : [entry.Path];
        if (paths.Count == 0)
        {
            return;
        }
        if (paths.Count > MaxWindowsMenuPaths)
        {
            ShowNotice($"Windows' menu takes at most {MaxWindowsMenuPaths:N0} files; select fewer.", isError: true);
            return;
        }
        _ = ShowWindowsMenuAsync(view, index, at, paths);
    }

    private async Task ShowWindowsMenuAsync(FilePane view, int index, Point? at, List<string> paths)
    {
        var asked = ++_windowsMenuAsked;
        var started = Stopwatch.GetTimestamp();
        var reply = await RequestSafelyAsync(new ShellMenuRequest(paths));
        if (asked != _windowsMenuAsked || _closing)
        {
            return;
        }
        switch (reply)
        {
            case ShellMenuReply menu:
                var replied = Stopwatch.GetTimestamp();
                _windowsMenu.Show(view, at ?? view.RowAnchor(index), menu);
                Diag.Info(MenuTarget, "windows menu shown", new LogField("paths", paths.Count), new LogField("items", menu.Items.Count),
                    new LogField("reply_ms", Math.Round(Stopwatch.GetElapsedTime(started, replied).TotalMilliseconds, 1)),
                    new LogField("build_ms", Math.Round(Stopwatch.GetElapsedTime(replied).TotalMilliseconds, 2)));
                break;
            case ErrorReply { Code: ErrorCodes.ShellMenuOff }:
                // The file turned it off since the window read it: the plain menu, as the setting says.
                OnContextMenuRequested(view, index, at);
                break;
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                _unavailable.Add("shell_menu");
                Diag.Info(MenuTarget, "the core does not answer shell_menu; Shift+right-click opens the context menu");
                OnContextMenuRequested(view, index, at);
                break;
            case ErrorReply error:
                ShowNotice($"Windows' menu: {error.Message}", isError: true);
                break;
        }
    }

    private async Task InvokeWindowsMenuItemAsync(ulong menu, uint item)
    {
        // The item runs in the core, a background process: a window it opens may come to the front.
        if (_session.CoreProcessId is { } corePid)
        {
            WindowsPlatform.AllowForeground(corePid);
        }
        switch (await RequestSafelyAsync(new ShellMenuInvokeRequest(menu, item)))
        {
            case OkReply:
                Diag.Info(MenuTarget, "windows menu item run", new LogField("menu", menu), new LogField("item", item));
                break;
            case ErrorReply error:
                ShowNotice($"Windows' menu: {error.Message}", isError: true);
                break;
        }
    }

    // The snapshot aid's menu steps: "*" is the pane's empty space; "" the focused row; else a row by name, selected first.
    private int MenuRowForStep(string argument)
    {
        var row = argument == "*" ? -1 : argument.Length == 0 ? Active.FocusIndex : Active.View?.IndexOfName(argument) ?? -1;
        if (row >= 0 && !Active.Selection.IsSelected(row))
        {
            Active.Selection.MoveTo(row, Core.Listing.SelectMode.Single);
        }
        return row;
    }
}
