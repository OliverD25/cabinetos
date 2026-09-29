using System.Runtime.InteropServices;
using CabinetOS.Core.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace CabinetOS.Services;

/// <summary>
/// One WebView2 for a page the window hosts: the terminal page, or a Tool
/// Extension (Constitution Article 11). Each host has a user-data folder of
/// its own, so each runs its own browser process: when one crashes, the
/// window and the other hosts go on (<see cref="Failed"/>, then
/// <see cref="RestartAsync"/>). Pages come only from folders mapped to
/// virtual hosts under <c>cabinetos.example</c>; every other navigation and
/// request, http and https included, is refused, so a page cannot reach the
/// network. Developer tools exist only in Debug builds.
/// </summary>
internal sealed class WebViewHost
{
    /// <summary>The domain of every virtual host (<c>.example</c> is reserved: it never resolves).</summary>
    public const string Domain = "cabinetos.example";

    private const string Target = "cabinetos_ui::webview";

    private readonly Border _frame;
    private readonly string _name;
    private readonly Dictionary<string, (string Folder, CoreWebView2HostResourceAccessKind Access, bool Navigable)> _mappings = new(StringComparer.OrdinalIgnoreCase);
    private WebView2? _view;
    private CoreWebView2? _core;
    private CoreWebView2Environment? _environment;
    private Uri? _start;
    private bool _browserGone;

    /// <summary>A host that puts its WebView2 into <paramref name="frame"/>; <paramref name="name"/> names its data folder and its log lines.</summary>
    public WebViewHost(Border frame, string name)
    {
        _frame = frame;
        _name = name;
    }

    /// <summary>A string the page posted (<c>chrome.webview.postMessage</c>), on the UI thread.</summary>
    public event Action<string>? MessageReceived;

    /// <summary>The page stopped: its renderer or its browser process ended. The text says which.</summary>
    public event Action<string>? Failed;

    /// <summary>The WebView2 control, once started.</summary>
    public WebView2? View => _view;

    /// <summary>Whether a page runs (started and not failed).</summary>
    public bool IsRunning { get; private set; }

    /// <summary>The browser process of this host, or 0; the crash-isolation check ends it.</summary>
    public int BrowserProcessId => _core is { } core ? (int)core.BrowserProcessId : 0;

    /// <summary>
    /// The user-data folder: <c>%LOCALAPPDATA%\CabinetOS\WebView2\&lt;name&gt;</c>, or
    /// <c>&lt;CABINETOS_WEBVIEW2_DIR&gt;\&lt;name&gt;</c> when that variable is set, so
    /// tests and live checks leave the real folder alone.
    /// </summary>
    public string DataFolder => Path.Combine(CabinetOS.Core.Presentation.WebViewData.Root(), _name);

    /// <summary>
    /// Whether the page is drawn dark or light (<c>prefers-color-scheme</c>);
    /// set before <see cref="StartAsync"/>, or any time for a running page.
    /// </summary>
    public CoreWebView2PreferredColorScheme ColorScheme
    {
        get => _colorScheme;
        set
        {
            _colorScheme = value;
            if (_core is { } core)
            {
                core.Profile.PreferredColorScheme = value;
            }
        }
    }

    private CoreWebView2PreferredColorScheme _colorScheme = CoreWebView2PreferredColorScheme.Auto;

    /// <summary>A script of the window's that runs in every document the page loads, before its own scripts.</summary>
    public string? HostScript { get; set; }

    /// <summary>
    /// Serves <paramref name="folder"/> at <c>https://&lt;host&gt;/</c>; kept
    /// across restarts. A host that is not <paramref name="navigable"/> only
    /// answers requests (a tool's file folder): the page cannot go there.
    /// </summary>
    public void MapFolder(string host, string folder, CoreWebView2HostResourceAccessKind access, bool navigable = true)
    {
        // First, so a folder WebView2 refuses is not kept for the next start.
        _core?.SetVirtualHostNameToFolderMapping(host, folder, access);
        _mappings[host] = (folder, access, navigable);
    }

    /// <summary>Stops serving a folder.</summary>
    public void UnmapFolder(string host)
    {
        if (_mappings.Remove(host))
        {
            _core?.ClearVirtualHostNameToFolderMapping(host);
        }
    }

    /// <summary>
    /// Creates the WebView2 and opens <paramref name="start"/>. False when
    /// WebView2 could not start (no WebView2 Runtime, or a broken data folder).
    /// </summary>
    public async Task<bool> StartAsync(Uri start)
    {
        _start = start;
        var view = new WebView2
        {
            // The page draws on the window's own fill (the terminal's rgba(0,0,0,.35)).
            DefaultBackgroundColor = Colors.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _frame.Child = view;
        _view = view;
        try
        {
            _environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, DataFolder, new CoreWebView2EnvironmentOptions());
            await view.EnsureCoreWebView2Async(_environment);
        }
        catch (Exception error) when (error is COMException or FileNotFoundException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            Diag.Error(Target, "WebView2 could not start", new LogField("host", _name), new LogField("error", error.Message));
            _frame.Child = null;
            _view = null;
            return false;
        }
        if (_view != view)
        {
            // Closed while starting.
            return false;
        }
        var core = view.CoreWebView2;
        _core = core;
        _browserGone = false;
        Configure(core);
        try
        {
            foreach (var (host, (folder, access, _)) in _mappings)
            {
                core.SetVirtualHostNameToFolderMapping(host, folder, access);
            }
        }
        catch (Exception error) when (error is COMException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Back to not started, so the caller can let go of the folder and start again.
            Diag.Error(Target, "WebView2 refused a folder", new LogField("host", _name), new LogField("error", error.Message));
            Close();
            throw;
        }
        core.Profile.PreferredColorScheme = _colorScheme;
        if (HostScript is { } script)
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
        }
        IsRunning = true;
        core.Navigate(start.AbsoluteUri);
        Diag.Info(Target, "WebView2 started", new LogField("host", _name), new LogField("browser_pid", core.BrowserProcessId),
            new LogField("url", start.AbsoluteUri));
        return true;
    }

    /// <summary>Sends the page a string (<c>chrome.webview</c>'s <c>message</c> event); dropped while it is not running.</summary>
    public void Post(string json)
    {
        if (!IsRunning || _core is not { } core)
        {
            return;
        }
        Diag.HeavyPageMessage(_name, "to_page", json);
        try
        {
            core.PostWebMessageAsString(json);
        }
        catch (COMException error)
        {
            Diag.Debug(Target, "a message to the page was lost", new LogField("host", _name), new LogField("error", error.Message));
        }
    }

    /// <summary>
    /// Brings the page back after <see cref="Failed"/>: a new renderer
    /// (reload), or, when the browser process ended, a new WebView2.
    /// </summary>
    public async Task<bool> RestartAsync()
    {
        if (_start is not { } start)
        {
            return false;
        }
        if (_browserGone || _core is null)
        {
            Close();
            return await StartAsync(start);
        }
        IsRunning = true;
        _core.Navigate(start.AbsoluteUri);
        return true;
    }

    /// <summary>Takes the page's pixels as PNG (the snapshot aid). False when there is no page.</summary>
    public async Task<bool> CaptureAsync(IRandomAccessStream stream)
    {
        if (!IsRunning || _core is not { } core)
        {
            return false;
        }
        try
        {
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            return true;
        }
        catch (COMException error)
        {
            Diag.Info(Target, "the page could not be captured", new LogField("host", _name), new LogField("error", error.Message));
            return false;
        }
    }

    /// <summary>Closes the WebView2 and its browser process.</summary>
    public void Close()
    {
        IsRunning = false;
        var view = _view;
        _view = null;
        _core = null;
        _environment = null;
        _frame.Child = null;
        view?.Close();
    }

    private void Configure(CoreWebView2 core)
    {
        var settings = core.Settings;
#if DEBUG
        // F12 goes to the page with the other browser keys; the right-click menu's Inspect opens the tools.
        settings.AreDevToolsEnabled = true;
        settings.AreDefaultContextMenusEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
#endif
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        // Ctrl+F, Ctrl+P, F5 and the like go to the page (the shell), not to the browser.
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsWebMessageEnabled = true;

        core.NavigationStarting += (_, e) =>
        {
            if (!IsAllowed(e.Uri, navigation: true))
            {
                e.Cancel = true;
                Diag.Info(Target, "a navigation was blocked", new LogField("host", _name), new LogField("url", e.Uri));
            }
        };
        core.FrameNavigationStarting += (_, e) =>
        {
            if (!IsAllowed(e.Uri, navigation: true))
            {
                e.Cancel = true;
                Diag.Info(Target, "a frame navigation was blocked", new LogField("host", _name), new LogField("url", e.Uri));
            }
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            Diag.Info(Target, "a new window was blocked", new LogField("host", _name), new LogField("url", e.Uri));
        };
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        // Requests a page makes (scripts, images, fetch) go through the same check as navigations.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!IsAllowed(e.Request.Uri, navigation: false) && _environment is { } environment)
            {
                e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                Diag.Info(Target, "a request was blocked", new LogField("host", _name), new LogField("url", e.Request.Uri));
            }
        };
        core.WebMessageReceived += (_, e) =>
        {
            string text;
            try
            {
                text = e.TryGetWebMessageAsString();
            }
            catch (ArgumentException)
            {
                // Only strings are part of the protocol.
                return;
            }
            Diag.HeavyPageMessage(_name, "from_page", text);
            MessageReceived?.Invoke(text);
        };
        core.ProcessFailed += (_, e) => OnProcessFailed(e);
    }

    private bool IsAllowed(string uri, bool navigation)
    {
        if (uri == "about:blank")
        {
            return true;
        }
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            && parsed.Scheme == Uri.UriSchemeHttps
            && _mappings.TryGetValue(parsed.Host, out var mapping)
            && (!navigation || mapping.Navigable);
    }

    private void OnProcessFailed(CoreWebView2ProcessFailedEventArgs e)
    {
        Diag.Warn(Target, "a WebView2 process failed", new LogField("host", _name), new LogField("kind", e.ProcessFailedKind.ToString()),
            new LogField("reason", e.Reason.ToString()), new LogField("exit_code", e.ExitCode), new LogField("process", e.ProcessDescription));
        string what;
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                _browserGone = true;
                what = "its browser process ended";
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                what = "its page process ended";
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                what = "its page stopped responding";
                break;
            default:
                // The GPU and utility processes come back by themselves.
                return;
        }
        IsRunning = false;
        Failed?.Invoke(what);
    }
}
