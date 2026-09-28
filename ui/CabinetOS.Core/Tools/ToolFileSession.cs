namespace CabinetOS.Core.Tools;

/// <summary>What a tool's file session asks of the page's WebView2 (the window's <c>WebViewHost</c> does it).</summary>
public interface IToolPage
{
    /// <summary>Serves <paramref name="folder"/> read-only on <paramref name="host"/>; the page may request from it, never navigate there.</summary>
    void MapFolder(string host, string folder);

    /// <summary>Stops serving the folder of <paramref name="host"/>.</summary>
    void UnmapFolder(string host);

    /// <summary>Starts the page, or loads it again when it runs; the page then says <c>ready</c>. False when WebView2 could not start.</summary>
    Task<bool> LoadAsync();

    /// <summary>Sends the page a message.</summary>
    void Post(string message);
}

/// <summary>
/// The file a Tool Extension shows, and how it reaches the page
/// (docs/tool-extensions.md, "Messages"): each open serves the file's folder
/// on a new host (<see cref="ToolFileUrls"/>) and unmaps the one before, and
/// the page gets <c>open</c> once it says <c>ready</c>. A page that is
/// already loaded cannot fetch from a host mapped after it loaded: the live
/// check of 2026-09-28 got "Failed to fetch" when a shown file was opened
/// again (Ctrl+K V). So every open loads the page again, and it asks for the
/// file with a new <c>ready</c>.
/// </summary>
public sealed class ToolFileSession(string toolId, string domain, IToolPage page)
{
    /// <summary>
    /// The longest file path WebView2 reads from a mapped folder (MAX_PATH less
    /// its end). On 2026-09-29 it showed a 255-character path, failed to fetch
    /// a 272-character one from a folder it served, and refused a 333-character
    /// folder, with Windows' long paths turned on.
    /// </summary>
    public const int LongestPath = 259;

    private int _serial;

    /// <summary>The file on screen, or null.</summary>
    public string? FilePath { get; private set; }

    /// <summary>The host that serves its folder, or null.</summary>
    public string? Host { get; private set; }

    /// <summary>Whether the page said <c>ready</c> since it last loaded.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Why the last open failed, to follow "cannot show {name}: ", or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// Shows <paramref name="path"/>: its folder on a new host, then the page
    /// loads. False when WebView2 could not start, cannot read a path that
    /// long, or could not serve the folder (<see cref="Problem"/> says why).
    /// </summary>
    public async Task<bool> OpenAsync(string path)
    {
        FilePath = path;
        Problem = null;
        IsReady = false;
        if (Host is { } previous)
        {
            page.UnmapFolder(previous);
            Host = null;
        }
        if (path.Length > LongestPath)
        {
            // Not offered at all: the page would only say "Failed to fetch".
            Problem = $"its path has {path.Length} characters, and WebView2 reads none longer than {LongestPath}";
            return false;
        }
        var host = ToolFileUrls.Host(toolId, ++_serial, domain);
        var mapped = false;
        try
        {
            page.MapFolder(host, Path.GetDirectoryName(path) ?? path);
            mapped = true;
            Host = host;
            // A WebView2 that has not started yet serves its folders while it starts, so a refusal can come from here too.
            return await page.LoadAsync();
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // A folder WebView2 refuses (one gone since the listing, say) ends the open, not a blank tool.
            if (mapped)
            {
                page.UnmapFolder(host);
            }
            Host = null;
            Problem = $"WebView2 cannot serve its folder ({error.Message.TrimEnd('.', ' ')})";
            return false;
        }
    }

    /// <summary>The page said <c>ready</c>: it gets <paramref name="context"/> (if any), then the file.</summary>
    public void OnReady(string? context)
    {
        IsReady = true;
        if (context is not null)
        {
            page.Post(context);
        }
        if (FilePath is { } path && Host is { } host)
        {
            page.Post(ToolMessages.Open(path, ToolFileUrls.Url(host, path)));
        }
    }

    /// <summary>Loads the page again after it stopped (a crash); it gets the same file when it says <c>ready</c>.</summary>
    public Task<bool> ReloadAsync()
    {
        IsReady = false;
        return page.LoadAsync();
    }

    /// <summary>The page stopped or was closed: nothing reaches it until it says <c>ready</c> again.</summary>
    public void OnStopped() => IsReady = false;
}
