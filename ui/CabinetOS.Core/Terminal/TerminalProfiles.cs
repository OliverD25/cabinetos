using System.Text.Json;

namespace CabinetOS.Core.Terminal;

/// <summary>
/// The shells the terminal can start, from <c>terminal.profiles</c> and
/// <c>terminal.defaultProfile</c> in the <c>config</c> reply
/// (docs/terminal.md, "Profiles"). The core starts them and says whether a
/// session may be linked; the window only offers the names.
/// </summary>
public sealed record TerminalProfiles(string DefaultProfile, IReadOnlyList<string> Names)
{
    /// <summary>The documented defaults, until the core answers.</summary>
    public static readonly TerminalProfiles Defaults = new("pwsh", ["pwsh", "cmd", "wsl", "claude"]);

    /// <summary>Reads the profiles; anything missing keeps the defaults.</summary>
    public static TerminalProfiles FromConfig(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("terminal", out var terminal)
            || terminal.ValueKind != JsonValueKind.Object)
        {
            return Defaults;
        }
        var names = new List<string>();
        if (terminal.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
        {
            foreach (var profile in profiles.EnumerateArray())
            {
                if (profile.ValueKind == JsonValueKind.Object
                    && profile.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && name.GetString() is { Length: > 0 } text)
                {
                    names.Add(text);
                }
            }
        }
        var defaultProfile = terminal.TryGetProperty("defaultProfile", out var chosen) && chosen.ValueKind == JsonValueKind.String
            ? chosen.GetString() ?? Defaults.DefaultProfile
            : Defaults.DefaultProfile;
        return new TerminalProfiles(defaultProfile, names.Count > 0 ? names : Defaults.Names);
    }
}
