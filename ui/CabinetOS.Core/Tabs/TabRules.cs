namespace CabinetOS.Core.Tabs;

/// <summary>The rules of tabs that need no tab: small, so the window and its tests share one.</summary>
public static class TabRules
{
    /// <summary>
    /// Whether going to <paramref name="target"/> from a tab that shows
    /// <paramref name="currentFolder"/> opens a new tab instead: the tab is
    /// locked and the folder differs (Total Commander's rule for a locked tab).
    /// </summary>
    public static bool OpensInNewTab(bool locked, string currentFolder, string target) =>
        locked && currentFolder.Length > 0 && !SameFolder(currentFolder, target);

    /// <summary>Whether two folder paths name the same folder: case and a closing backslash do not tell them apart.</summary>
    public static bool SameFolder(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
