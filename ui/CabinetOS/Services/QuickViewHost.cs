using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace CabinetOS.Services;

/// <summary>
/// One viewer's page in the Quick View panel (ADR 0023, item W4): a WebView2 in its tool's user-data folder
/// (<c>tool-&lt;id&gt;</c>, so it shares a browser process with the same tool's pane page and no other tool), with
/// every rule of a tool page (no network, no downloads, no new windows, no permissions). Each file loads the page
/// again on a new host <c>f&lt;n&gt;.&lt;id&gt;.cabinetos.example</c> that serves the file's folder read-only, and
/// the host before is cleared first, so a page never reads an earlier folder; WebView2's browser process reads the
/// file, never the window. The window never waits on the page: every call here posts or starts something.
/// </summary>
internal sealed class QuickViewHost
{
    private const string Target = "cabinetos_ui::quickview";

    private readonly WebViewHost _page;
    private int _serial;
    // The core keeps its drawings (render_image) in cache\render\<id>\image.png: cabinetos-core's cache_dir() is
    // CABINETOS_CACHE_DIR, else %LOCALAPPDATA%\CabinetOS\cache, and the window, which starts the core, has the same environment.
    private static readonly string RenderRoot = Path.Combine(
        Environment.GetEnvironmentVariable("CABINETOS_CACHE_DIR") is { Length: > 0 } cache
            ? cache
            : Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local ? local : Path.GetTempPath(), "CabinetOS", "cache"),
        "render");

    private int _renderSerial;
    private string? _fileHost;
    private string? _renderHost;
    private bool _renderHostIsRoot;

    /// <summary>A host for <paramref name="viewer"/> whose WebView2 goes into a frame of its own in <paramref name="layer"/>.</summary>
    public QuickViewHost(QuickViewer viewer, Grid layer)
    {
        Viewer = viewer;
        Frame = new Border { Opacity = 0, IsHitTestVisible = false };
        layer.Children.Add(Frame);
        _page = new WebViewHost(Frame, $"tool-{viewer.Id}") { BrowserArguments = WebViewHost.ToolBrowserArguments };
        _page.MapFolder(PageHost, viewer.Dir, CoreWebView2HostResourceAccessKind.Deny);
        _page.MessageReceived += OnMessage;
        _page.Failed += reason => Failed?.Invoke(reason);
    }

    /// <summary>The viewer.</summary>
    public QuickViewer Viewer { get; }

    /// <summary>The frame the WebView2 is drawn in, over the card and the thumbnail.</summary>
    public Border Frame { get; }

    /// <summary>The page's WebView2.</summary>
    public WebViewHost Page => _page;

    /// <summary>The viewer page's own host.</summary>
    public string PageHost => $"{Viewer.Id}.tool.{WebViewHost.Domain}";

    /// <summary>Whether the page's browser started once (a later file of this viewer is a warm load).</summary>
    public bool Started => _page.View is not null;

    /// <summary>Where the page reads the file it loads now, set before the load starts; null when none.</summary>
    public string? FileUrl { get; private set; }

    /// <summary>A message of the page, on the UI thread: <c>ready</c>, a report, or a refused command.</summary>
    public event Action<QuickViewPageMessage>? MessageReceived;

    /// <summary>The page's process ended or stopped responding (the text says which).</summary>
    public event Action<string>? Failed;

    /// <summary>
    /// Loads the page for <paramref name="path"/>: the file's folder on a new host, then the viewer's entry. Returns
    /// the URL the page reads the file from, or null with <paramref name="problem"/> saying why it could not load
    /// (a path longer than WebView2 reads, a folder it refused, WebView2 that did not start).
    /// </summary>
    public async Task<(string? Url, string? Problem)> LoadAsync(string path)
    {
        ClearHosts();
        FileUrl = null;
        if (path.Length > ToolFileSession.LongestPath)
        {
            return (null, $"its path has {path.Length} characters, and WebView2 reads none longer than {ToolFileSession.LongestPath}");
        }
        Frame.Visibility = Visibility.Visible;
        var host = ToolFileUrls.Host(Viewer.Id, ++_serial, WebViewHost.Domain);
        try
        {
            _page.MapFolder(host, Path.GetDirectoryName(path) ?? path, CoreWebView2HostResourceAccessKind.Allow, navigable: false);
            _fileHost = host;
            MapRenderRoot();
            // Before the load: the page's ready can come before this method's caller runs again.
            FileUrl = ToolFileUrls.Url(host, path);
            var entry = EntryUri();
            var loaded = _page.View is null ? await StartAsync(entry)
                : _page.IsRunning ? _page.Navigate(entry)
                : await _page.RestartAsync();
            if (!loaded)
            {
                FileUrl = null;
            }
            return loaded ? (FileUrl, null) : (null, "WebView2 did not load it");
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ClearHosts();
            return (null, $"WebView2 cannot serve its folder ({error.Message.TrimEnd('.', ' ')})");
        }
    }

    // The page never takes the keyboard (ADR 0023, decision 6): a click still reaches it, so a video's controls work by mouse.
    private async Task<bool> StartAsync(Uri entry)
    {
        var started = await _page.StartAsync(entry);
        if (_page.View is { } view)
        {
            view.IsTabStop = false;
            view.AllowFocusOnInteraction = false;
        }
        return started;
    }

    // The folder of the core's drawings goes onto a host of its own before the page loads: WebView2 says that a mapping set
    // after a page loaded may not reach it, and a mapping made when the drawing is ready (below) did not in the Omen laptop's
    // live check of 2026-10-04 (the page's request for the drawing failed, so the Image Viewer reported "damaged"). A drawing
    // is then a file under that host, whenever the core makes it. When the folder cannot be mapped now, MapRender maps the
    // drawing's folder when it is ready, as before.
    private void MapRenderRoot()
    {
        var host = ToolFileUrls.RenderHost(Viewer.Id, ++_renderSerial, WebViewHost.Domain);
        try
        {
            Directory.CreateDirectory(RenderRoot);
            _page.MapFolder(host, RenderRoot, CoreWebView2HostResourceAccessKind.Allow, navigable: false);
            _renderHost = host;
            _renderHostIsRoot = true;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Diag.Info(Target, "the folder of drawings could not be served before the load", new LogField("viewer", Viewer.Id), new LogField("error", error.Message));
        }
    }

    /// <summary>
    /// Where the page reads the drawing in <paramref name="folder"/> (<c>render_image</c>): <c>image.png</c> under the
    /// host that serves the core's folder of drawings, or, for a drawing somewhere else, on a host mapped now. Null when
    /// the folder cannot be served.
    /// </summary>
    public string? MapRender(string folder)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(folder);
        if (_renderHostIsRoot && _renderHost is { } served
            && string.Equals(Path.GetDirectoryName(trimmed), Path.TrimEndingDirectorySeparator(RenderRoot), StringComparison.OrdinalIgnoreCase))
        {
            return $"https://{served}/{Uri.EscapeDataString(Path.GetFileName(trimmed))}/image.png";
        }
        if (_renderHost is { } previous)
        {
            _page.UnmapFolder(previous);
            _renderHost = null;
            _renderHostIsRoot = false;
        }
        var host = ToolFileUrls.RenderHost(Viewer.Id, ++_renderSerial, WebViewHost.Domain);
        try
        {
            _page.MapFolder(host, folder, CoreWebView2HostResourceAccessKind.Allow, navigable: false);
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Diag.Info(Target, "a drawing's folder could not be served", new LogField("viewer", Viewer.Id), new LogField("error", error.Message));
            return null;
        }
        _renderHost = host;
        return $"https://{host}/image.png";
    }

    /// <summary>Sends the page a message; dropped while it does not run.</summary>
    public void Post(string json) => _page.Post(json);

    /// <summary>
    /// The panel closed, or another viewer shows the next file: the page goes to <c>about:blank</c> at once (a video
    /// stops and its decoder is freed), its file hosts are cleared, the frame is hidden and the page is suspended.
    /// </summary>
    /// <param name="idle">Asked after the wait: false when the panel came back for this viewer meanwhile.</param>
    public async Task SleepAsync(Func<bool> idle)
    {
        ClearHosts();
        _page.NavigateToBlank();
        Frame.Opacity = 0;
        Frame.IsHitTestVisible = false;
        Frame.Visibility = Visibility.Collapsed;
        // WebView2 refuses to suspend while the navigation to about:blank still runs ("not in the correct state"). A
        // panel opened again within the wait keeps the page awake: its load comes only after the 120 ms rest, and a
        // suspend just before it held a warm thumbnail's frame back to 138 ms in the laptop's live check of 2026-10-03.
        await Task.Delay(400);
        if (Frame.Visibility == Visibility.Collapsed && FileUrl is null && idle())
        {
            await _page.TrySuspendAsync();
        }
    }

    /// <summary>Closes the WebView2 and its browser process (unless the same tool's pane page shares it).</summary>
    public void Close()
    {
        // Not ClearHosts: after a crash the browser is gone and clearing a mapping there throws; the whole WebView2 goes.
        FileUrl = null;
        _fileHost = null;
        _renderHost = null;
        _renderHostIsRoot = false;
        _page.Close();
        if (Frame.Parent is Panel layer)
        {
            layer.Children.Remove(Frame);
        }
    }

    private void ClearHosts()
    {
        FileUrl = null;
        if (_fileHost is { } file)
        {
            _page.UnmapFolder(file);
            _fileHost = null;
        }
        if (_renderHost is { } render)
        {
            _page.UnmapFolder(render);
            _renderHost = null;
            _renderHostIsRoot = false;
        }
    }

    private Uri EntryUri() =>
        new($"https://{PageHost}/{string.Join('/', Viewer.Entry.Split('/', '\\').Select(Uri.EscapeDataString))}");

    private void OnMessage(string json)
    {
        if (QuickViewMessages.Parse(json) is not { } message)
        {
            Diag.Debug(Target, "a viewer page's message was dropped", new LogField("viewer", Viewer.Id));
            return;
        }
        if (message.Type == QuickViewPageMessage.Refused)
        {
            // A page in the panel runs no commands and follows no plugins (ADR 0023, decision 3).
            Diag.Warn(Target, "a viewer page asked for something a page in the panel may not do; refused",
                new LogField("trigger", $"quickview:{Viewer.Id}"), new LogField("type", message.RefusedType));
            return;
        }
        MessageReceived?.Invoke(message);
    }
}
