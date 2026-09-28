using System.Text.Json;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Terminal;
using CabinetOS.Core.Tools;
using CabinetOS.Services;
using CabinetOS.Views;
using Microsoft.UI.Xaml;
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
            foreach (var host in _toolHosts.OfType<ToolHost>())
            {
                host.Page.ColorScheme = ToolColorScheme();
            }
        };
        _toolsLoading = LoadToolsAsync();
    }

    private void RegisterToolCommands()
    {
        _router.RegisterUiHandler("editor.openMarkdownPreview", OpenMarkdownPreviewAsync);
        _router.RegisterUiHandler("editor.close", invocation => CloseEditor(EditorPaneOf(invocation.Args), focusPane: true));
        _router.RegisterUiHandler("editor.reload", invocation => ReloadEditorAsync(EditorPaneOf(invocation.Args)));
    }

    private void ApplyToolKeys(Keymap keymap)
    {
        _toolKeys = TerminalKeys.PassKeys(keymap, context: null);
        foreach (var host in _toolHosts.OfType<ToolHost>())
        {
            host.SendPassKeys(ToolMessages.PassKeys(_toolKeys.Keys));
        }
    }

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
    /// Opens <paramref name="path"/> in <paramref name="tool"/>: where the tool
    /// is open already, else in the other pane (two panes) or this one (one).
    /// </summary>
    private async Task OpenInToolAsync(InstalledTool tool, string path)
    {
        if (tool.Manifest.Placement == ToolManifest.InDock)
        {
            Diag.Info(ToolsTarget, "dock tools arrive later; opening in a pane", new LogField("tool", tool.Manifest.Id));
        }
        var pane = Array.FindIndex(_toolHosts, h => h?.Tool.Manifest.Id == tool.Manifest.Id);
        if (pane < 0)
        {
            pane = _dual ? 1 - _active : _active;
            if (_toolHosts[pane] is not null)
            {
                // Another tool had that pane: one editor per pane.
                CloseEditor(pane, focusPane: false);
            }
        }
        var host = _toolHosts[pane];
        if (host is null)
        {
            host = CreateToolHost(tool, pane);
            _toolHosts[pane] = host;
        }
        var covered = _paneViews[pane].FocusState != FocusState.Unfocused || pane == _active && !_dual;
        _editorViews[pane].Show(path, tool.Manifest.Name);
        _paneViews[pane].Visibility = Visibility.Collapsed;
        if (!await host.OpenAsync(path))
        {
            if (host.Problem is { } problem)
            {
                Diag.Warn(ToolsTarget, "a tool could not open a file", new LogField("tool", tool.Manifest.Id), new LogField("path", path),
                    new LogField("problem", problem));
            }
            ShowNotice(host.Problem is { } why
                ? $"{tool.Manifest.Name} cannot show {Path.GetFileName(path)}: {why}."
                : $"{tool.Manifest.Name} could not start: WebView2 did not load it.", isError: true);
            CloseEditor(pane, focusPane: covered);
            return;
        }
        Diag.Info(ToolsTarget, "file opened in a tool", new LogField("tool", tool.Manifest.Id), new LogField("path", path), new LogField("pane", pane));
        if (covered)
        {
            // The pane that had the keyboard is covered by the editor: the page takes it.
            MainColumn.UpdateLayout();
            _editorViews[pane].FocusPage();
        }
        ScheduleToolContext();
    }

    private ToolHost CreateToolHost(InstalledTool tool, int pane)
    {
        var host = new ToolHost(tool, _editorViews[pane].Frame, ToolKeyScript.Build(_toolKeys.Keys))
        {
            Context = ToolContext,
        };
        host.Page.ColorScheme = ToolColorScheme();
        host.Failed += reason =>
        {
            Diag.Info(ToolsTarget, "tool stopped; the pane says so", new LogField("tool", tool.Manifest.Id), new LogField("pane", pane),
                new LogField("reason", reason));
            _editorViews[pane].ShowStopped(reason);
        };
        host.CommandRequested += (id, args) => RunToolCommand(tool, id, args);
        host.KeyPressed += keys =>
        {
            if (_toolKeys.TryGetValue(keys, out var command))
            {
                _ = _router.ExecuteAsync(command, trigger: "key");
            }
        };
        return host;
    }

    // A tool may move around and open things, never change files or grant rights (ToolMessages.MayRun).
    private void RunToolCommand(InstalledTool tool, string commandId, JsonElement? args)
    {
        if (!ToolMessages.MayRun(commandId))
        {
            Diag.Warn(ToolsTarget, "a tool asked for a command it may not run", new LogField("tool", tool.Manifest.Id),
                new LogField("command", commandId));
            ShowNotice($"{tool.Manifest.Name} asked to run {commandId}, which tools may not run.", isError: true);
            return;
        }
        _ = _router.ExecuteAsync(commandId, args, $"tool:{tool.Manifest.Id}");
    }

    private void CloseEditor(int pane, bool focusPane)
    {
        if (pane is < 0 or > 1 || _toolHosts[pane] is not { } host)
        {
            return;
        }
        var hadFocus = _editorViews[pane].HasFocus;
        host.Close();
        _toolHosts[pane] = null;
        _editorViews[pane].Hide();
        _paneViews[pane].Visibility = Visibility.Visible;
        if (focusPane || hadFocus)
        {
            _paneViews[pane].Focus(FocusState.Programmatic);
        }
        Diag.Info(ToolsTarget, "tool closed", new LogField("tool", host.Tool.Manifest.Id), new LogField("pane", pane));
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
        return focused >= 0 ? focused : Array.FindIndex(_toolHosts, h => h is not null);
    }

    private void ScheduleToolContext()
    {
        if (_toolHosts.Any(h => h is not null))
        {
            _contextTimer.Stop();
            _contextTimer.Start();
        }
    }

    private void SendToolContext()
    {
        var context = ToolContext();
        foreach (var host in _toolHosts.OfType<ToolHost>())
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
        if (_editorViews is { } editors && editors[_active].IsOpen && editors[_active].FocusPage())
        {
            return;
        }
        _paneViews[_active].Focus(FocusState.Programmatic);
    }

    private CoreWebView2PreferredColorScheme ToolColorScheme() =>
        RootGrid.ActualTheme == ElementTheme.Light ? CoreWebView2PreferredColorScheme.Light : CoreWebView2PreferredColorScheme.Dark;
}
