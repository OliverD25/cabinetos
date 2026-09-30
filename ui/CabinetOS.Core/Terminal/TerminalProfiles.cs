using System.Text.Json;

namespace CabinetOS.Core.Terminal;

/// <summary>
/// The shells the terminal can start, from <c>terminal.profiles</c> and
/// <c>terminal.defaultProfile</c> in the <c>config</c> reply
/// (docs/terminal.md, "Profiles"). The core starts them; the window only
/// offers the names, and knows which of them the folder sync leaves alone
/// (<c>followsPane: false</c>, a program that is not a shell).
/// </summary>
public sealed record TerminalProfiles(string DefaultProfile, IReadOnlyList<string> Names, IReadOnlySet<string> NotFollowing)
{
    /// <summary>The documented defaults, until the core answers.</summary>
    public static readonly TerminalProfiles Defaults = new("pwsh", ["pwsh", "cmd", "wsl", "claude"],
        new HashSet<string>(["claude"], StringComparer.Ordinal));

    /// <summary>
    /// Whether the window types a change-directory line into the profile's
    /// session when the active pane changes folder. A name that is not known follows.
    /// </summary>
    public bool FollowsPane(string name) => !NotFollowing.Contains(name);

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
        var notFollowing = new HashSet<string>(StringComparer.Ordinal);
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
                    // Only a real false stops the sync; a missing or wrong-typed key is the default, true.
                    if (profile.TryGetProperty("followsPane", out var follows) && follows.ValueKind == JsonValueKind.False)
                    {
                        notFollowing.Add(text);
                    }
                }
            }
        }
        var defaultProfile = terminal.TryGetProperty("defaultProfile", out var chosen) && chosen.ValueKind == JsonValueKind.String
            ? chosen.GetString() ?? Defaults.DefaultProfile
            : Defaults.DefaultProfile;
        return names.Count > 0
            ? new TerminalProfiles(defaultProfile, names, notFollowing)
            : new TerminalProfiles(defaultProfile, Defaults.Names, Defaults.NotFollowing);
    }
}
