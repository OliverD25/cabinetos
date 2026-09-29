namespace CabinetOS.Core.Presentation;

/// <summary>Where the WebView2 pages (the terminal, the Tool Extensions) keep their user data.</summary>
public static class WebViewData
{
    /// <summary>The variable that moves the folder, so tests and live checks leave the real one alone.</summary>
    public const string DirEnv = "CABINETOS_WEBVIEW2_DIR";

    /// <summary>
    /// The folder above every host's data folder: <c>CABINETOS_WEBVIEW2_DIR</c>
    /// when it is set and not blank (made absolute), else
    /// <c>%LOCALAPPDATA%\CabinetOS\WebView2</c>.
    /// </summary>
    public static string Root(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var fromEnv = environment(DirEnv);
        return string.IsNullOrWhiteSpace(fromEnv)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CabinetOS", "WebView2")
            : Path.GetFullPath(fromEnv.Trim());
    }
}
