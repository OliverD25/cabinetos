using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Sidebar;
using CabinetOS.Core.Tools;
using CabinetOS.Services;
using CabinetOS.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CabinetOS;

// The sidebar pages of Tool Extensions with "sidebar": true (docs/tool-extensions.md, "The sidebar page"): each one a
// page in a WebView2 of its own, started when its button is first pressed. The one hidden last stays awake, the ones
// hidden before it are suspended (WarmPages), and a suspended page wakes when its button is pressed again.
public sealed partial class MainWindow
{
    // One sidebar page: the frame the WebView2 sits in, the tool's host, and the "stopped" cover.
    private sealed class SidebarPage(InstalledTool tool, Border frame, Border cover, ToolHost host)
    {
        public InstalledTool Tool { get; } = tool;

        public Border Frame { get; } = frame;

        public Border Cover { get; } = cover;

        public ToolHost Host { get; } = host;

        public bool Started { get; set; }

        public bool Stopped { get; set; }

        public bool WantsKeys { get; set; }
    }

    private readonly Dictionary<string, SidebarPage> _sidebarPages = new(StringComparer.Ordinal);
    private readonly WarmPages _warmPages = new();

    // Every tool page the window hosts: the panes' and the sidebar's (the keys, the context and the theme go to all).
    private IEnumerable<ToolHost> AllToolHosts() => _toolHosts.OfType<ToolHost>().Concat(_sidebarPages.Values.Select(p => p.Host));

    // Shows the page of tool <paramref name="toolId"/> in the sidebar, or none. A page that never started starts;
    // one that stopped is started again; one that was suspended wakes.
    private void ShowSidebarPage(string? toolId)
    {
        SidebarPage? shown = null;
        if (toolId is not null && !_sidebarPages.TryGetValue(toolId, out shown))
        {
            shown = CreateSidebarPage(toolId);
        }
        foreach (var (id, page) in _sidebarPages)
        {
            var visible = id == toolId;
            page.Frame.Visibility = visible && !page.Stopped ? Visibility.Visible : Visibility.Collapsed;
            page.Cover.Visibility = visible && page.Stopped ? Visibility.Visible : Visibility.Collapsed;
        }
        ToolViews.Visibility = shown is null ? Visibility.Collapsed : Visibility.Visible;
        ApplyWarmth(_warmPages.Show(shown?.Tool.Manifest.Id));
        if (shown is { Started: false, Stopped: false })
        {
            _ = StartSidebarPageAsync(shown);
        }
    }

    private SidebarPage CreateSidebarPage(string toolId)
    {
        var tool = _tools.Tools.First(t => t.Manifest.Id == toolId);
        var frame = new Border();
        var reload = new Button { Content = "Reload", HorizontalAlignment = HorizontalAlignment.Center };
        var cover = new Border
        {
            Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 8,
                Padding = new Thickness(16),
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{tool.Manifest.Name} stopped",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = ThemeResources.Brush("CbTextPrimaryBrush"),
                    },
                    reload,
                },
            },
        };
        ToolViews.Children.Add(frame);
        ToolViews.Children.Add(cover);
        var host = NewToolHost(tool, frame, $"sidebar-{toolId}");
        var page = new SidebarPage(tool, frame, cover, host);
        host.Failed += reason =>
        {
            Diag.Info(ToolsTarget, "a sidebar page stopped", new LogField("tool", toolId), new LogField("reason", reason));
            page.Stopped = true;
            page.Started = false;
            _warmPages.Forget(toolId);
            ShowNotice($"{tool.Manifest.Name} stopped: {reason}.", isError: true);
            UpdateSidebarChrome();
        };
        reload.Click += (_, _) => _ = ReloadSidebarPageAsync(page);
        _sidebarPages[toolId] = page;
        return page;
    }

    private async Task StartSidebarPageAsync(SidebarPage page)
    {
        page.Started = true;
        var id = page.Tool.Manifest.Id;
        if (!await page.Host.StartViewAsync())
        {
            page.Started = false;
            Diag.Warn(ToolsTarget, "a sidebar page could not start", new LogField("tool", id));
            ShowNotice($"{page.Tool.Manifest.Name} could not start: WebView2 did not load it.", isError: true);
            page.Stopped = true;
            UpdateSidebarChrome();
            return;
        }
        Diag.Info(ToolsTarget, "a sidebar page started", new LogField("tool", id));
        if (page.WantsKeys && _sidebarView == id)
        {
            FocusSidebarPage(id);
        }
    }

    private async Task ReloadSidebarPageAsync(SidebarPage page)
    {
        page.Stopped = false;
        page.Started = true;
        UpdateSidebarChrome();
        if (!await page.Host.ReloadAsync())
        {
            page.Started = false;
            page.Stopped = true;
            ShowNotice($"{page.Tool.Manifest.Name} could not start again: WebView2 did not load it.", isError: true);
            UpdateSidebarChrome();
        }
    }

    // The tool's page takes the keyboard (never WebView2.Focus itself: the window hands the keys over and checks they arrive).
    // A page that has not started yet takes it as soon as it has.
    private void FocusSidebarPage(string id)
    {
        if (!_sidebarPages.TryGetValue(id, out var page))
        {
            return;
        }
        if (page.Host.Page.View is not { } view)
        {
            page.WantsKeys = true;
            return;
        }
        page.WantsKeys = false;
        GiveKeysToPage(view, $"sidebar:{id}", () => _railLayout && _sidebarOpen && _sidebarView == id);
    }

    // The steps WarmPages gives: wake a suspended page first, then put the pages hidden before the warm one to sleep.
    private void ApplyWarmth(IReadOnlyList<PageStep> steps)
    {
        foreach (var step in steps)
        {
            if (!_sidebarPages.TryGetValue(step.Page, out var page))
            {
                continue;
            }
            if (step.Change == PageChange.Resume)
            {
                page.Host.Page.Resume();
                // What the page missed while it slept: the panes' folder and selection.
                ScheduleToolContext();
            }
            else
            {
                _ = SuspendSidebarPageAsync(page);
            }
        }
    }

    // WebView2 suspends only a page that is hidden: the frame was collapsed first, and layout is given a moment to hide the control.
    private async Task SuspendSidebarPageAsync(SidebarPage page)
    {
        await Task.Delay(300);
        if (page.Frame.Visibility == Visibility.Visible)
        {
            // Shown again meanwhile.
            return;
        }
        await page.Host.Page.TrySuspendAsync();
    }
}
