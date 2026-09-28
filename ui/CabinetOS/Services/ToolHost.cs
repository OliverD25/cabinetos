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
/// new host for every open; the page cannot navigate there. Messages go
/// both ways as JSON strings; what the page asks for is checked here.
/// </summary>
internal sealed class ToolHost
{
    private const string Target = "cabinetos_ui::tools";

    private readonly WebViewHost _page;
    private int _serial;
    private string? _fileHost;
    private bool _ready;

    /// <summary>A host for <paramref name="tool"/> whose WebView2 goes into <paramref name="frame"/>.</summary>
    public ToolHost(InstalledTool tool, Border frame, string hostScript)
    {
        Tool = tool;
        _page = new WebViewHost(frame, $"tool-{tool.Manifest.Id}") { HostScript = hostScript };
        _page.MapFolder(PageHost, tool.Folder, CoreWebView2HostResourceAccessKind.Deny);
        _page.MessageReceived += OnMessage;
        _page.Failed += reason =>
        {
            _ready = false;
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
    public string? FilePath { get; private set; }

    /// <summary>Whether the page said <c>ready</c> and has the file.</summary>
    public bool IsReady => _ready;

    /// <summary>The page's process ended (the text says which).</summary>
    public event Action<string>? Failed;

    /// <summary>The page asked for a command: (command ID, arguments). The window checks and runs it.</summary>
    public event Action<string, JsonElement?>? CommandRequested;

    /// <summary>The host's key script passed on one of the window's shortcuts.</summary>
    public event Action<string>? KeyPressed;

    /// <summary>What the page gets as <c>context</c> when it is ready.</summary>
    public Func<string>? Context { get; set; }

    /// <summary>
    /// Shows <paramref name="path"/>: its folder is served on a new host,
    /// and the page gets <c>open</c> (at once, or when it says <c>ready</c>).
    /// False when WebView2 could not start.
    /// </summary>
    public async Task<bool> OpenAsync(string path)
    {
        FilePath = path;
        var folder = Path.GetDirectoryName(path) ?? path;
        if (_fileHost is { } previous)
        {
            _page.UnmapFolder(previous);
        }
        _fileHost = ToolFileUrls.Host(Tool.Manifest.Id, ++_serial, WebViewHost.Domain);
        _page.MapFolder(_fileHost, folder, CoreWebView2HostResourceAccessKind.Allow, navigable: false);
        if (_page.View is null)
        {
            _ready = false;
            return await _page.StartAsync(EntryUri());
        }
        if (_ready)
        {
            SendOpen();
        }
        return true;
    }

    /// <summary>Sends <c>context</c> (the active pane's folder and selection) if the page is ready.</summary>
    public void Send(string message)
    {
        if (_ready)
        {
            _page.Post(message);
        }
    }

    /// <summary>Sends the host's key script its keys; it listens from the page's first line.</summary>
    public void SendPassKeys(string message) => _page.Post(message);

    /// <summary>Brings the page back after <see cref="Failed"/>; it gets the same file again.</summary>
    public Task<bool> ReloadAsync()
    {
        _ready = false;
        return _page.RestartAsync();
    }

    /// <summary>Ends the page and its browser process.</summary>
    public void Close()
    {
        _ready = false;
        _page.Close();
    }

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
                _ready = true;
                if (Context?.Invoke() is { } context)
                {
                    _page.Post(context);
                }
                SendOpen();
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

    private void SendOpen()
    {
        if (FilePath is { } path && _fileHost is { } host)
        {
            _page.Post(ToolMessages.Open(path, ToolFileUrls.Url(host, path)));
        }
    }
}
