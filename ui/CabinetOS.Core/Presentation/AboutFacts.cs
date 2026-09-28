using System.Globalization;
using System.Text.Json;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Presentation;

/// <summary>What <c>release.json</c> says about a release build (build/release.ps1 writes it next to CabinetOS.exe).</summary>
public sealed record ReleaseFacts(string? Version, string? Commit, DateTimeOffset? BuiltUtc, bool UncommittedChanges)
{
    /// <summary>The file's name, next to CabinetOS.exe.</summary>
    public const string FileName = "release.json";

    /// <summary>Reads the file's text; null when it is not a JSON object.</summary>
    public static ReleaseFacts? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            string? Text(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
            DateTimeOffset? built = Text("builtUtc") is { } stamp
                && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed
                    : null;
            var dirty = root.TryGetProperty("uncommittedChanges", out var flag) && flag.ValueKind == JsonValueKind.True;
            return new ReleaseFacts(Text("version"), Text("commit"), built, dirty);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// What sits next to CabinetOS.exe: <c>release.json</c> (absent in a
/// development build, unreadable when damaged), <c>LICENSE</c> and
/// <c>THIRD-PARTY-NOTICES.md</c>, each null when missing.
/// </summary>
public sealed record ReleaseFolder(bool HasReleaseFile, ReleaseFacts? Facts, string? LicensePath, string? NoticesPath)
{
    /// <summary>
    /// Looks at <paramref name="folder"/>. File I/O in the window's process:
    /// call it off the UI thread (docs/ui.md, "About"); it reads one small file.
    /// </summary>
    public static ReleaseFolder Read(string folder)
    {
        var release = Path.Combine(folder, ReleaseFacts.FileName);
        ReleaseFacts? facts = null;
        var present = File.Exists(release);
        if (present)
        {
            try
            {
                facts = ReleaseFacts.Parse(File.ReadAllText(release));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Present but unreadable: the view says so (Facts stays null).
            }
        }
        string? Beside(string name) => Path.Combine(folder, name) is var path && File.Exists(path) ? path : null;
        return new ReleaseFolder(present, facts, Beside("LICENSE"), Beside("THIRD-PARTY-NOTICES.md"));
    }
}

/// <summary>The About view's lines (docs/ui.md, "About"), each a label and its value.</summary>
public static class AboutText
{
    /// <summary>The rows: the version (the window's), the core's version and protocol, the build.</summary>
    public static IReadOnlyList<(string Label, string Value)> Rows(string windowVersion, PongReply? core, ReleaseFolder release, CultureInfo? culture = null) =>
    [
        ("Version", windowVersion),
        ("Core", core is null ? "not reachable" : $"{core.CoreVersion}, protocol {core.ProtocolVersion}"),
        ("Build", Build(release, culture ?? CultureInfo.CurrentCulture)),
    ];

    /// <summary>What the links line says when a file is not there.</summary>
    public static string MissingDocuments(ReleaseFolder release) => release.HasReleaseFile
        ? "LICENSE or THIRD-PARTY-NOTICES.md is missing next to CabinetOS.exe."
        : "LICENSE and THIRD-PARTY-NOTICES.md come with a release; this development build has none next to it.";

    private static string Build(ReleaseFolder release, CultureInfo culture)
    {
        if (!release.HasReleaseFile)
        {
            return "Development build (no release.json next to CabinetOS.exe)";
        }
        if (release.Facts is not { } facts)
        {
            return "release.json next to CabinetOS.exe could not be read";
        }
        var parts = new List<string>();
        if (facts.Commit is { } commit)
        {
            parts.Add($"commit {commit[..Math.Min(10, commit.Length)]}");
        }
        if (facts.BuiltUtc is { } built)
        {
            parts.Add($"built {built.ToLocalTime().ToString("g", culture)}");
        }
        if (facts.UncommittedChanges)
        {
            parts.Add("with uncommitted changes");
        }
        return parts.Count > 0 ? string.Join(", ", parts) : "release.json names no commit or time";
    }
}
