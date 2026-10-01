using System.Text.Json;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Tabs;
using CabinetOS.Core.Terminal;
using CabinetOS.Core.Tools;
using CabinetOS.Services;
using CabinetOS.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace CabinetOS;

// Tool Extensions: web pages that open files in a pane's editor tab (docs/tool-extensions.md, docs/ui.md "Tool Extensions").
public sealed partial class MainWindow
{
    private const string ToolsTarget = "cabinetos_ui::tools";

    private readonly ToolHost?[] _toolHosts = new ToolHost?[2];
    private EditorPane[] _editorViews = null!;
    private DispatcherQueueTimer _contextTimer = null!;
    private ToolCatalog _tools = ToolCatalog.Empty;
    private Task _toolsLoading = Task.CompletedTask;
    private IReadOnlyDictionary<string, string> _toolKeys = new Dictionary<string, string>();

    // What a tool's page in the sidebar passes back: the ways out and the tab keys of every tool page, and the keys that change
    // the sidebar's view.
    private IReadOnlyDictionary<string, string> _sidebarPageKeys = new Dictionary<string, string>();

    // Ctrl+Shift+P from a tool's page (a pane's tab, the sidebar): the way the keyboard goes back to it when the palette
    // closes, as it goes back to the terminal (_paletteFromTerminal). Null when the palette came from elsewhere.
    private Action? _paletteBackToPage;

    private void SetUpTools()
    {
        _editorViews = [LeftEditor, RightEditor];
        for (var i = 0; i < _editorViews.Length; i++)
        {
            _editorViews[i].PaneIndex = i;
            _editorViews[i].RunCommand = (id, args, trigger) => _router.ExecuteAsync(id, args, trigger);
        }
        // The page gets the active pane's folder and selection, at most every 100 ms.
        _contextTimer = DispatcherQueue.CreateTimer();
        _contextTimer.IsRepeating = false;
        _contextTimer.Interval = TimeSpan.FromMilliseconds(100);
        _contextTimer.Tick += (_, _) => SendToolContext();
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            foreach (var host in AllToolHosts())
            {
                host.Page.ColorScheme = ToolColorScheme();
            }
        };
        _toolsLoading = LoadToolsAsync();
    }

    private void RegisterToolCommands()
    {
        _router.RegisterUiHandler("editor.openMarkdownPreview", OpenMarkdownPreviewAsync);
        _router.RegisterUiHandler("editor.close", invocation => CloseEditorTabAsync(EditorPaneOf(invocation.Args), invocation.RequestId));
        _router.RegisterUiHandler("editor.reload", invocation => ReloadEditorAsync(EditorPaneOf(invocation.Args)));
    }

    private void ApplyToolKeys(Keymap keymap)
    {
        _toolKeys = TerminalKeys.PassKeys(keymap, context: null, paneWays: TerminalKeys.TabWays);
        _sidebarPageKeys = TerminalKeys.PassKeys(keymap, context: null, TerminalKeys.SidebarPageWays, TerminalKeys.TabWays);
        foreach (var host in AllToolHosts())
        {
            host.SendPassKeys(ToolMessages.PassKeys(KeysOfHost(host).Keys));
        }
    }

    private IReadOnlyDictionary<string, string> KeysOfHost(ToolHost host) =>
        _sidebarPages.Values.Any(p => p.Host == host) ? _sidebarPageKeys : _toolKeys;

    // Tools are read once, at start, on a background thread: reading tool.json is file I/O in the
    // UI process, kept small and off the UI thread (docs/ui.md, "Tool Extensions").
    private async Task LoadToolsAsync()
    {
        var roots = ToolRoots();
        var catalog = await Task.Run(() => ToolCatalog.Load(roots));
        _tools = catalog;
        foreach (var problem in catalog.Problems)
        {
            Diag.Warn(ToolsTarget, "a tool cannot be used", new LogField("problem", problem));
        }
        Diag.Info(ToolsTarget, "tools found", new LogField("count", catalog.Tools.Count),
            new LogField("tools", string.Join(",", catalog.Tools.Select(t => $"{t.Manifest.Id}@{t.Folder}"))),
            new LogField("roots", string.Join(";", roots)));
        OnToolsLoadedForRail();
    }

    // --tools-dir (or CABINETOS_TOOLS_DIR) first, so a tool being written wins over an installed copy.
    private List<string> ToolRoots()
    {
        var roots = new List<string>();
        if ((_toolsDir ?? Environment.GetEnvironmentVariable("CABINETOS_TOOLS_DIR")) is { Length: > 0 } development)
        {
            roots.Add(Path.GetFullPath(development));
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CabinetOS", "tools"));
        return roots;
    }

    /// <summary>The tool that opens <paramref name="name"/>, once the tools are read.</summary>
    private async Task<InstalledTool?> ToolForAsync(string name)
    {
        await _toolsLoading;
        return _tools.ForFile(name);
    }

    private async Task OpenMarkdownPreviewAsync(CommandInvocation invocation)
    {
        var path = CommandArgs.Text(invocation.Args, "path");
        if (path is null && Active.Search is null && Active.EntryAt(Active.FocusIndex) is { IsFolder: false } entry)
        {
            path = entry.Path;
        }
        else if (path is null && Active.FocusedHit is { Hit.IsFolder: false } hit)
        {
            path = hit.Hit.Path;
        }
        if (path is null)
        {
            ShowNotice("Select a Markdown file first.");
            return;
        }
        await _toolsLoading;
        var name = Path.GetFileName(path);
        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            ShowNotice($"{name} is not a Markdown file.");
            return;
        }
        if (_tools.ForFile(name) is not { } tool)
        {
            // Article 10: viewers are opt-in; nothing is built in.
            ShowNotice("No Markdown tool is installed. Tools are opt-in: see docs/tool-extensions.md.");
            return;
        }
        await OpenInToolAsync(tool, path);
    }

    /// <summary>
    /// Opens <paramref name="path"/> in <paramref name="tool"/>, as a tab of the
    /// pane's row: where the tool is open already (its tab shows the next
    /// file), else in the other pane (two panes) or this one (one).
    /// </summary>
    private async Task OpenInToolAsync(InstalledTool tool, string path)
    {
        if (tool.Manifest.Placement == ToolManifest.InDock)
        {
            Diag.Info(ToolsTarget, "dock tools arrive later; opening in a pane", new LogField("tool", tool.Manifest.Id));
        }
        var pane = Array.FindIndex(_strips, s => s.IndexOfTool(tool.Manifest.Id) >= 0);
        if (pane < 0)
        {
            pane = _dual ? 1 - _active : _active;
        }
        var strip = _strips[pane];
        var covered = _paneViews[pane].FocusState != FocusState.Unfocused || _editorViews[pane].HasFocus || pane == _active && !_dual;
        var index = strip.IndexOfTool(tool.Manifest.Id);
        if (index >= 0)
        {
            strip.Tabs[index].Path = path;
            strip.Select(index);
            strip.Touch();
        }
        else
        {
            strip.Add(PaneTab.ForTool(path, tool.Manifest.Id, tool.Manifest.Name));
        }
        // Each open loads the tool's page again, the same file too (ToolFileSession).
        await QueueShow(pane, giveKeys: covered, reopenTool: true);
    }

    // The tab in front of the pane shows a tool: its host is started (or reused, when it is the same tool), the editor
    // covers the pane's list, and the file is opened unless the page shows it already. False when the tool cannot show it.
    private async Task<bool> ShowToolTabAsync(int pane, PaneTab tab, bool giveKeys, bool reopen)
    {
        var tool = _tools.Tools.FirstOrDefault(t => t.Manifest.Id == tab.Tool);
        if (tool is null)
        {
            Diag.Warn(ToolsTarget, "a tab shows a tool that is not installed", new LogField("tool", tab.Tool), new LogField("path", tab.Path));
            ShowNotice($"The tool {tab.Tool} is not installed.", isError: true);
            return false;
        }
        var host = _toolHosts[pane];
        if (host is not null && host.Tool.Manifest.Id != tool.Manifest.Id)
        {
            // One tool process per pane: the other tool's tabs stay, and open it again when they come to the front.
            EndToolHost(pane);
            host = null;
        }
        if (host is null)
        {
            host = CreateToolHost(tool, pane);
            _toolHosts[pane] = host;
        }
        _editorViews[pane].Show(tab.Path, tool.Manifest.Name);
        _paneViews[pane].Visibility = Visibility.Collapsed;
        if (reopen || !host.IsReady || host.FilePath != tab.Path)
        {
            if (!await host.OpenAsync(tab.Path))
            {
                if (host.Problem is { } problem)
                {
                    Diag.Warn(ToolsTarget, "a tool could not open a file", new LogField("tool", tool.Manifest.Id), new LogField("path", tab.Path),
                        new LogField("problem", problem));
                }
                ShowNotice(host.Problem is { } why
                    ? $"{tool.Manifest.Name} cannot show {Path.GetFileName(tab.Path)}: {why}."
                    : $"{tool.Manifest.Name} could not start: WebView2 did not load it.", isError: true);
                EndToolHost(pane);
                return false;
            }
            Diag.Info(ToolsTarget, "file opened in a tool", new LogField("tool", tool.Manifest.Id), new LogField("path", tab.Path), new LogField("pane", pane));
        }
        if (giveKeys)
        {
            // The pane that had the keyboard is covered by the editor: the page takes it.
            MainColumn.UpdateLayout();
            FocusEditorPage(pane);
        }
        ScheduleToolContext();
        return true;
    }

    private ToolHost CreateToolHost(InstalledTool tool, int pane)
    {
        var host = NewToolHost(tool, _editorViews[pane].Frame, dataName: null, pane);
        host.Failed += reason =>
        {
            Diag.Info(ToolsTarget, "tool stopped; the pane says so", new LogField("tool", tool.Manifest.Id), new LogField("pane", pane),
                new LogField("reason", reason));
            _editorViews[pane].ShowStopped(reason);
        };
        return host;
    }

    // A host for a tool's page in any frame (a pane's editor, or the sidebar): the window's keys, the panes' context and
    // the theme go to it, and what it asks for is checked in RunToolCommand. A page in a pane's tab names its pane: the tab
    // keys it passes back are about that pane's tabs, and the active pane need not be the one whose page has the keyboard.
    private ToolHost NewToolHost(InstalledTool tool, Border frame, string? dataName, int? pane = null)
    {
        var sidebar = dataName is not null;
        var host = new ToolHost(tool, frame, ToolKeyScript.Build((sidebar ? _sidebarPageKeys : _toolKeys).Keys), dataName)
        {
            Context = ToolContext,
        };
        host.Page.ColorScheme = ToolColorScheme();
        host.CommandRequested += (id, args) => RunToolCommand(tool, host, id, args);
        host.KeyPressed += keys =>
        {
            if ((sidebar ? _sidebarPageKeys : _toolKeys).TryGetValue(keys, out var command))
            {
                _ = _router.ExecuteAsync(command, PageKeyArguments(command, keys, pane), trigger: "key");
            }
        };
        return host;
    }

    // A tool may move around and open things, never change files or grant rights (ToolMessages.MayRun); besides that the
    // commands of a plugin its page follows, which is how a plugin's own page (the agent's chat) runs the plugin's commands.
    private void RunToolCommand(InstalledTool tool, ToolHost host, string commandId, JsonElement? args)
    {
        if (!ToolMessages.MayRun(commandId, _router.Find(commandId)?.Source, host.Subscriptions.Wants))
        {
            Diag.Warn(ToolsTarget, "a tool asked for a command it may not run", new LogField("tool", tool.Manifest.Id),
                new LogField("command", commandId));
            ShowNotice($"{tool.Manifest.Name} asked to run {commandId}, which tools may not run.", isError: true);
            return;
        }
        _ = _router.ExecuteAsync(commandId, args, $"tool:{tool.Manifest.Id}");
    }

    // The pane's tool process ends and its editor goes; the tool's tabs stay and open it again when they come to the front.
    private void EndToolHost(int pane)
    {
        if (pane is < 0 or > 1 || _toolHosts[pane] is not { } host)
        {
            return;
        }
        host.Close();
        _toolHosts[pane] = null;
        _editorViews[pane].Hide();
        Diag.Info(ToolsTarget, "tool closed", new LogField("tool", host.Tool.Manifest.Id), new LogField("pane", pane));
    }

    // The pane loses its tool tabs and its tool (single-pane mode closes the right pane's); the folder tab in front shows.
    private void CloseToolTabs(int pane, bool focusPane)
    {
        if (pane is < 0 or > 1)
        {
            return;
        }
        var hadFocus = _editorViews[pane].HasFocus;
        _strips[pane].RemoveToolTabs();
        EndToolHost(pane);
        _ = QueueShow(pane, giveKeys: focusPane || hadFocus);
    }

    // editor.close: the tool tab in front, else the pane's first tool tab, goes.
    private Task CloseEditorTabAsync(int pane, string? requestId)
    {
        if (pane is < 0 or > 1)
        {
            return Task.CompletedTask;
        }
        var strip = _strips[pane];
        var index = strip.Active.IsTool ? strip.ActiveIndex : strip.Tabs.ToList().FindIndex(t => t.IsTool);
        return index < 0 ? Task.CompletedTask : CloseTabAsync(pane, index, requestId);
    }

    private async Task ReloadEditorAsync(int pane)
    {
        if (pane is < 0 or > 1 || _toolHosts[pane] is not { } host)
        {
            return;
        }
        _editorViews[pane].HideStopped();
        if (!await host.ReloadAsync())
        {
            _editorViews[pane].ShowStopped("WebView2 did not start it again");
        }
    }

    // The pane of an editor command: its "pane" argument, else the editor that has the keyboard, else the one open.
    private int EditorPaneOf(JsonElement? args)
    {
        if (CommandArgs.Number(args, "pane") is { } pane and <= 1)
        {
            return (int)pane;
        }
        var focused = Array.FindIndex(_editorViews, v => v.HasFocus);
        return focused >= 0 ? focused : Array.FindIndex(_strips, strip => strip.Tabs.Any(t => t.IsTool));
    }

    private void ScheduleToolContext()
    {
        if (_toolHosts.Any(h => h is not null) || _sidebarPages.Count > 0)
        {
            _contextTimer.Stop();
            _contextTimer.Start();
        }
    }

    private void SendToolContext()
    {
        var context = ToolContext();
        foreach (var host in AllToolHosts())
        {
            host.Send(context);
        }
    }

    private string ToolContext() => ToolMessages.Context(
        Active.Search is null ? Active.Targets().Select(t => t.Path).ToList() : [],
        Active.Path.Length > 0 ? Active.Path : null);

    // The keyboard back to the active pane, or to the tool page that covers it (one pane shown);
    // while the marketplace covers the panes, back to the marketplace.
    private void FocusActivePane()
    {
        if (MarketView.IsOpen)
        {
            MarketView.FocusSearch();
            return;
        }
        FocusPaneOrEditor();
    }

    private void FocusPaneOrEditor()
    {
        if (_editorViews is { } editors && editors[_active].IsOpen && FocusEditorPage(_active))
        {
            return;
        }
        _paneViews[_active].Focus(FocusState.Programmatic);
    }

    // The way back to the tool page that has the keyboard now, or null when none has it. A page that is gone or hidden
    // by then (its tab closed, its pane or the sidebar shut) sends the keyboard to the active pane instead.
    private Action? WayBackToToolPage()
    {
        if (RootGrid.XamlRoot is not { } root || FocusManager.GetFocusedElement(root) is not WebView2 view)
        {
            return null;
        }
        if (Array.FindIndex(_editorViews, v => v.PageView == view) is var pane and >= 0)
        {
            return () =>
            {
                if (!_editorViews[pane].IsOpen || !FocusEditorPage(pane))
                {
                    FocusActivePane();
                }
            };
        }
        if (_sidebarPages.FirstOrDefault(p => p.Value.Host.Page.View == view).Key is { } id)
        {
            return () =>
            {
                if (_railLayout && _sidebarOpen && _sidebarView == id)
                {
                    FocusSidebarPage(id);
                }
                else
                {
                    FocusActivePane();
                }
            };
        }
        return null;
    }

    // The editor's page takes the keyboard, and the window checks that Windows sends the keys there (PageKeyboard).
    private bool FocusEditorPage(int pane) =>
        GiveKeysToPage(_editorViews[pane].PageView, $"tool:{_toolHosts[pane]?.Tool.Manifest.Id}", () => _editorViews[pane].IsOpen);

    private CoreWebView2PreferredColorScheme ToolColorScheme() =>
        RootGrid.ActualTheme == ElementTheme.Light ? CoreWebView2PreferredColorScheme.Light : CoreWebView2PreferredColorScheme.Dark;
}
