using System.Net;
using System.Text.RegularExpressions;

namespace CabinetOS.Tests;

/// <summary>Every bold label in docs/ui-map.md is in the source, so an agent never names a button the app does not have.</summary>
public partial class UiMapTests
{
    private static readonly string[] SourceExtensions = [".cs", ".xaml", ".rs", ".json", ".toml", ".ps1", ".js", ".html"];
    private static readonly string[] SkippedFolders = ["bin", "obj", "target", "node_modules", ".git", "CabinetOS.Tests", "TestResults"];

    [Fact]
    public void Every_label_in_the_ui_map_is_in_the_code()
    {
        var root = RepoRoot();
        var source = ReadSource(root);
        var missing = new List<string>();
        var number = 0;
        foreach (var line in File.ReadLines(Path.Combine(root, "docs", "ui-map.md")))
        {
            number++;
            if (line.StartsWith("Updated:", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (Match match in Bold().Matches(line))
            {
                if (line[(match.Index + match.Length)..].StartsWith(" (not ours)", StringComparison.Ordinal))
                {
                    continue;
                }
                var parts = Wildcard().Split(Normalize(match.Groups[1].Value)).Select(Regex.Escape);
                if (!Regex.IsMatch(source, string.Join(".+?", parts), RegexOptions.Singleline))
                {
                    missing.Add($"docs/ui-map.md:{number}: {match.Groups[1].Value}");
                }
            }
        }

        Assert.True(missing.Count == 0, "Labels in the ui map that the code does not have:\n" + string.Join("\n", missing));
    }

    private static string Normalize(string text) => text.Replace("...", "\u2026", StringComparison.Ordinal);

    private static string ReadSource(string root)
    {
        var text = new System.Text.StringBuilder();
        foreach (var top in new[] { "ui", "core", "sdk" })
        {
            foreach (var file in Walk(Path.Combine(root, top)))
            {
                var content = File.ReadAllText(file);
                text.Append(Normalize(file.EndsWith(".xaml", StringComparison.Ordinal) ? WebUtility.HtmlDecode(content) : content)).Append('\n');
            }
        }
        return text.ToString();
    }

    private static IEnumerable<string> Walk(string folder)
    {
        if (!Directory.Exists(folder))
        {
            yield break;
        }
        foreach (var file in Directory.EnumerateFiles(folder).Where(f => SourceExtensions.Contains(Path.GetExtension(f))))
        {
            yield return file;
        }
        foreach (var child in Directory.EnumerateDirectories(folder).Where(d => !SkippedFolders.Contains(Path.GetFileName(d))))
        {
            foreach (var file in Walk(child))
            {
                yield return file;
            }
        }
    }

    private static string RepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "docs", "ui-map.md")))
            {
                return folder.FullName;
            }
        }
        throw new InvalidOperationException("docs/ui-map.md was not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Wildcard();
}
