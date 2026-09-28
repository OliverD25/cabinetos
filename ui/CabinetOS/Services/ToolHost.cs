using System.Text.Json;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Tools;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace CabinetOS.Services;

/// <summary>
/// One running Tool Extension (docs/tool-extensions.md): its page in a
/// WebView2 of its own, served from <c>https://&lt;id&gt;.tool.cabinetos.example/</c>.
/// The file it shows is read from the file's folder, mapped read-only to a
/// new host for every open (<see cref="ToolFileSession"/>, which also loads
/// the page again for each open); the page cannot navigate there. Messages
/// go both ways as JSON strings; what the page asks for is checked here.
/// </summary>
internal sealed class ToolHost : IToolPage
{
    private const string Target = "cabinetos_ui::tools";

    private readonly WebViewHost _page;
    private readonly ToolFileSession _files;

    /// <summary>A host for <paramref name="tool"/> whose WebView2 goes into <paramref name="frame"/>.</summary>
    public ToolHost(InstalledTool tool, Border frame, string hostScript)
    {
        Tool = tool;
        _files = new ToolFileSession(tool.Manifest.Id, WebViewHost.Domain, this);
        _page = new WebViewHost(frame, $"tool-{tool.Manifest.Id}") { HostScript = hostScript };
        _page.MapFolder(PageHost, tool.Folder, CoreWebView2HostResourceAccessKind.Deny);
        _page.MessageReceived += OnMessage;
        _page.Failed += reason =>
        {
            _files.OnStopped();
            Failed?.Invoke(reason);
        };
    }

    /// <summary>The tool.</summary>
    public InstalledTool Tool { get; }

    /// <summary>The page's WebView2.</summary>
    public WebViewHost Page => _page;

    /// <summary>The tool page's virtual host.</summary>
    public string PageHost => $"{Tool.Manifest.Id}.tool.{WebViewHost.Domain}";

    /// <summary>The file on screen.</summary>
    public string? FilePath => _files.FilePath;

    /// <summary>Whether the page said <c>ready</c> and has the file.</summary>
    public bool IsReady => _files.IsReady;

    /// <summary>Why the last open failed (a path too long for WebView2, a folder it refused), or null.</summary>
    public string? Problem => _files.Problem;

    /// <summary>The page's process ended (the text says which).</summary>
    public event Action<string>? Failed;

    /// <summary>The page asked for a command: (command ID, arguments). The window checks and runs it.</summary>
    public event Action<string, JsonElement?>? CommandRequested;

    /// <summary>The host's key script passed on one of the window's shortcuts.</summary>
    public event Action<string>? KeyPressed;

    /// <summary>What the page gets as <c>context</c> when it is ready.</summary>
    public Func<string>? Context { get; set; }

    /// <summary>
    /// Shows <paramref name="path"/>: its folder is served on a new host, the
    /// page loads (again), and it gets <c>open</c> when it says <c>ready</c>.
    /// False when WebView2 could not start, or could not serve the folder
    /// (<see cref="Problem"/>).
    /// </summary>
    public Task<bool> OpenAsync(string path) => _files.OpenAsync(path);

    /// <summary>Sends <c>context</c> (the active pane's folder and selection) if the page is ready.</summary>
    public void Send(string message)
    {
        if (_files.IsReady)
        {
            _page.Post(message);
        }
    }

    /// <summary>Sends the host's key script its keys; it listens from the page's first line.</summary>
    public void SendPassKeys(string message) => _page.Post(message);

    /// <summary>Brings the page back after <see cref="Failed"/>; it gets the same file again.</summary>
    public Task<bool> ReloadAsync() => _files.ReloadAsync();

    /// <summary>Ends the page and its browser process.</summary>
    public void Close()
    {
        _files.OnStopped();
        _page.Close();
    }

    void IToolPage.MapFolder(string host, string folder) =>
        _page.MapFolder(host, folder, CoreWebView2HostResourceAccessKind.Allow, navigable: false);

    void IToolPage.UnmapFolder(string host) => _page.UnmapFolder(host);

    Task<bool> IToolPage.LoadAsync() => _page.View is null ? _page.StartAsync(EntryUri()) : _page.RestartAsync();

    void IToolPage.Post(string message) => _page.Post(message);

    private Uri EntryUri() =>
        new($"https://{PageHost}/{string.Join('/', Tool.Manifest.Entry.Split('/', '\\').Select(Uri.EscapeDataString))}");

    private void OnMessage(string json)
    {
        if (ToolMessages.Parse(json) is not { } message)
        {
            Diag.Debug(Target, "a tool message was dropped", new LogField("tool", Tool.Manifest.Id));
            return;
        }
        switch (message.Type)
        {
            case "ready":
                _files.OnReady(Context?.Invoke());
                Diag.Info(Target, "tool ready", new LogField("tool", Tool.Manifest.Id), new LogField("path", FilePath));
                break;
            case "command":
                CommandRequested?.Invoke(message.CommandId!, message.Args);
                break;
            case "key":
                KeyPressed?.Invoke(message.Keys!);
                break;
        }
    }

}
