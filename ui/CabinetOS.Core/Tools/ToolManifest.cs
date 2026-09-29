using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CabinetOS.Core.Tools;

/// <summary>A <c>tool.json</c> that cannot be used, with the file and the problem.</summary>
public sealed class ToolManifestException(string message) : Exception(message);

/// <summary>
/// A Tool Extension's manifest, <c>tool.json</c> (docs/tool-extensions.md):
/// its ID, name, version, author, description, the page it starts from, the
/// file names it opens, where it goes, and whether it also has a page in the
/// sidebar (<c>"sidebar": true</c>, optional). Read strictly, like
/// <c>plugin.json</c>: an unknown key, a missing key or a bad value stops the
/// tool with a message that names the file and the problem.
/// </summary>
public sealed partial record ToolManifest(
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    string Entry,
    IReadOnlyList<string> Accepts,
    string Placement,
    bool Sidebar = false)
{
    /// <summary>A tool that opens as a pane's editor tab (design view D).</summary>
    public const string InPane = "pane";

    /// <summary>A tool for the Tool Dock; this version opens it in a pane too.</summary>
    public const string InDock = "dock";

    private static readonly FrozenSet<string> Keys = FrozenSet.ToFrozenSet(
        ["id", "name", "version", "author", "description", "entry", "accepts", "placement", "sidebar"], StringComparer.Ordinal);

    /// <summary>
    /// Reads <paramref name="json"/>, the <c>tool.json</c> of the folder named
    /// <paramref name="folderName"/>; <paramref name="file"/> names it in messages.
    /// </summary>
    public static ToolManifest Parse(string json, string folderName, string file = "tool.json")
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException error)
        {
            throw new ToolManifestException($"{file}: not JSON: {error.Message}");
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ToolManifestException($"{file}: expected an object");
            }
            foreach (var property in root.EnumerateObject())
            {
                if (!Keys.Contains(property.Name))
                {
                    throw new ToolManifestException($"{file}: unknown field `{property.Name}`, expected one of {string.Join(", ", Keys.Order().Select(k => $"`{k}`"))}");
                }
            }
            var id = Text(root, "id", file);
            if (!IdPattern().IsMatch(id))
            {
                throw new ToolManifestException($"{file}: `id` must be 1 to 63 lower case letters, digits and `-`, starting with a letter: {id}");
            }
            if (!string.Equals(id, folderName, StringComparison.Ordinal))
            {
                throw new ToolManifestException($"{file}: `id` {id} must be the folder's name, {folderName}");
            }
            // The fields in the documented order, so the first problem named is the first one in the file.
            var name = Text(root, "name", file);
            var version = Text(root, "version", file);
            if (!VersionPattern().IsMatch(version))
            {
                throw new ToolManifestException($"{file}: `version` must be major.minor.patch, numbers only: {version}");
            }
            var author = Text(root, "author", file);
            var description = Text(root, "description", file);
            var entry = Text(root, "entry", file);
            if (!IsRelativePage(entry))
            {
                throw new ToolManifestException($"{file}: `entry` must be an .html file inside the tool's folder: {entry}");
            }
            if (!root.TryGetProperty("accepts", out var acceptsElement) || acceptsElement.ValueKind != JsonValueKind.Array)
            {
                throw new ToolManifestException($"{file}: missing field `accepts` (a list of file name patterns such as `*.md`)");
            }
            var accepts = new List<string>();
            foreach (var pattern in acceptsElement.EnumerateArray())
            {
                if (pattern.ValueKind != JsonValueKind.String || pattern.GetString() is not { Length: > 0 } text || !IsNamePattern(text))
                {
                    throw new ToolManifestException($"{file}: each of `accepts` must be `*.ext` or a file name, without a folder");
                }
                accepts.Add(text);
            }
            var placement = Text(root, "placement", file);
            if (placement is not (InPane or InDock))
            {
                throw new ToolManifestException($"{file}: `placement` must be `pane` or `dock`: {placement}");
            }
            var sidebar = false;
            if (root.TryGetProperty("sidebar", out var sidebarElement))
            {
                if (sidebarElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new ToolManifestException($"{file}: `sidebar` must be true or false");
                }
                sidebar = sidebarElement.GetBoolean();
            }
            return new ToolManifest(id, name, version, author, description, entry, accepts, placement, sidebar);
        }
    }

    /// <summary>Whether the tool opens a file named <paramref name="fileName"/> (without case).</summary>
    public bool AcceptsFile(string fileName) => Accepts.Any(pattern => pattern.StartsWith("*.", StringComparison.Ordinal)
        ? fileName.Length > pattern.Length - 1 && fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
        : string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase));

    private static string Text(JsonElement root, string name, string file)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            throw new ToolManifestException($"{file}: missing field `{name}`");
        }
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Trim().Length == 0)
        {
            throw new ToolManifestException($"{file}: `{name}` must be text, not empty");
        }
        return text;
    }

    // Inside the folder: relative, forward or back slashes, no "..", no drive, ending in .html or .htm.
    private static bool IsRelativePage(string entry) =>
        !Path.IsPathRooted(entry)
        && !entry.Contains(':', StringComparison.Ordinal)
        && !entry.Split('/', '\\').Any(part => part is "" or "." or "..")
        && (entry.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".htm", StringComparison.OrdinalIgnoreCase));

    private static bool IsNamePattern(string pattern)
    {
        if (pattern.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            return false;
        }
        var star = pattern.IndexOf('*', StringComparison.Ordinal);
        // "*.md" or "README": one leading "*." at most, and something after it.
        return star < 0 || (star == 0 && pattern.Length > 2 && pattern[1] == '.' && pattern.IndexOf('*', 1) < 0);
    }

    // A DNS label, since the ID becomes the tool's virtual host name.
    [GeneratedRegex("^[a-z][a-z0-9-]{0,62}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex VersionPattern();
}

/// <summary>One installed tool: its manifest and its folder.</summary>
public sealed record InstalledTool(ToolManifest Manifest, string Folder)
{
    /// <summary>The page to load.</summary>
    public string EntryPath => Path.Combine(Folder, Manifest.Entry);
}

/// <summary>
/// The tools found in the tools folders, read once at start
/// (docs/tool-extensions.md, "Where tools live"). A folder given first wins
/// over the same ID in a later one, so <c>--tools-dir</c> overrides an
/// installed copy while a tool is being written.
/// </summary>
public sealed class ToolCatalog
{
    /// <summary>No tools.</summary>
    public static readonly ToolCatalog Empty = new([], []);

    private ToolCatalog(IReadOnlyList<InstalledTool> tools, IReadOnlyList<string> problems)
    {
        Tools = tools;
        Problems = problems;
    }

    /// <summary>The usable tools, in the order found.</summary>
    public IReadOnlyList<InstalledTool> Tools { get; }

    /// <summary>Why a folder is not a usable tool, one line each, for the log.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>
    /// Reads every <c>&lt;root&gt;\&lt;id&gt;\tool.json</c>. Roots that do not
    /// exist are skipped. This is file I/O in the UI process, so it runs on a
    /// background thread, once (docs/ui.md, "Tool Extensions").
    /// </summary>
    public static ToolCatalog Load(IEnumerable<string> roots)
    {
        var tools = new List<InstalledTool>();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }
            foreach (var folder in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                var manifestPath = Path.Combine(folder, "tool.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }
                try
                {
                    var manifest = ToolManifest.Parse(File.ReadAllText(manifestPath), Path.GetFileName(folder), manifestPath);
                    var tool = new InstalledTool(manifest, folder);
                    if (!File.Exists(tool.EntryPath))
                    {
                        problems.Add($"{manifestPath}: the entry page {manifest.Entry} is missing");
                    }
                    else if (seen.Add(manifest.Id))
                    {
                        tools.Add(tool);
                    }
                    else
                    {
                        problems.Add($"{manifestPath}: a tool with the ID {manifest.Id} was found first in another folder");
                    }
                }
                catch (ToolManifestException error)
                {
                    problems.Add(error.Message);
                }
                catch (IOException error)
                {
                    problems.Add($"{manifestPath}: {error.Message}");
                }
                catch (UnauthorizedAccessException error)
                {
                    problems.Add($"{manifestPath}: {error.Message}");
                }
            }
        }
        return new ToolCatalog(tools, problems);
    }

    /// <summary>The first tool that opens <paramref name="fileName"/>, or null.</summary>
    public InstalledTool? ForFile(string fileName) => Tools.FirstOrDefault(t => t.Manifest.AcceptsFile(fileName));
}
