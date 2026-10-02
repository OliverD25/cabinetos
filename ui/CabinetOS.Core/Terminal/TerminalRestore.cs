using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Terminal;

/// <summary>What the first show of the dock after a restart does about the terminal's sessions.</summary>
public enum RestoreKind
{
    /// <summary>Nothing: the dock opens as it always does, and Ctrl+` starts a session.</summary>
    Nothing,

    /// <summary>The core still runs sessions the window does not show: the window shows those.</summary>
    Adopt,

    /// <summary>The saved sessions are started again as fresh shells.</summary>
    Restore,
}

/// <summary>What to do, and why in a word for the log (the "terminal restore" line).</summary>
public sealed record RestoreDecision(RestoreKind Kind, string Reason);

/// <summary>Where a saved value was replaced by another when the session came back: <paramref name="What"/> is <c>profile</c>, <c>folder</c> or <c>mode</c>.</summary>
public sealed record Fallback(string What, string From, string To);

/// <summary>
/// One session to start again. <paramref name="Index"/> is its place in the saved layout, which the saved front tabs
/// name; <paramref name="Fallbacks"/> are the values that could not be kept.
/// </summary>
public sealed record RestoreStep(int Index, string Profile, string Folder, int Pane, TerminalMode Mode, IReadOnlyList<Fallback> Fallbacks);

/// <summary>
/// The rules of the restoration (docs/terminal.md, "Restoring the tabs"): whether to restore at all, which sessions to
/// start from the saved layout and the profiles that exist now, what to try again when the core refuses one, and which
/// tabs come to the front. A pure class, as <see cref="TerminalSummoning"/> and <see cref="TerminalSplitLayout"/> are: the
/// window asks the core for everything that touches a disk (whether a folder is still there is the core's answer to the
/// open), and feeds the answers in.
/// </summary>
public static class TerminalRestore
{
    /// <summary>The most sessions the core keeps at once (docs/terminal.md, "Limits"); a longer saved list is cut here.</summary>
    public const int MaxSessions = 32;

    /// <summary>
    /// What the first show of the dock does. A session the window shows already means nothing is restored; sessions the core
    /// still runs (the core stayed, only the window restarted) win over the saved layout and are shown, whatever
    /// <c>terminal.restore</c> says; else the saved sessions come back when the setting allows and there are any.
    /// </summary>
    public static RestoreDecision Decide(bool restoreSetting, int windowSessions, int coreSessions, TerminalLayout saved)
    {
        if (windowSessions > 0)
        {
            return new(RestoreKind.Nothing, "a session is shown already");
        }
        if (coreSessions > 0)
        {
            return new(RestoreKind.Adopt, "the core still runs sessions");
        }
        if (!restoreSetting)
        {
            return new(RestoreKind.Nothing, "terminal.restore is false");
        }
        return saved.IsEmpty ? new(RestoreKind.Nothing, "no sessions were saved") : new(RestoreKind.Restore, "saved sessions");
    }

    /// <summary>
    /// The sessions to start, in the saved order: a profile that no longer exists becomes <paramref name="defaultProfile"/>,
    /// a session saved without a folder starts in <paramref name="home"/>, and no more than <see cref="MaxSessions"/> start.
    /// </summary>
    public static IReadOnlyList<RestoreStep> Plan(TerminalLayout saved, IReadOnlyCollection<string> profiles, string defaultProfile, string home)
    {
        var steps = new List<RestoreStep>();
        for (var index = 0; index < Math.Min(saved.Items.Count, MaxSessions); index++)
        {
            var item = saved.Items[index];
            var fallbacks = new List<Fallback>();
            var profile = item.Profile;
            if (!profiles.Contains(profile))
            {
                fallbacks.Add(new Fallback("profile", profile, defaultProfile));
                profile = defaultProfile;
            }
            steps.Add(new RestoreStep(index, profile, string.IsNullOrWhiteSpace(item.Folder) ? home : item.Folder, item.Pane == 1 ? 1 : 0, item.Mode, fallbacks));
        }
        return steps;
    }

    /// <summary>
    /// The step to try after the core refused <paramref name="step"/> with <paramref name="errorCode"/>, or null for a session
    /// that is skipped. A refusal <c>not_linkable</c> (the profile cannot be linked now) starts it locked; a refusal
    /// <c>spawn_failed</c> in a folder other than <paramref name="home"/> (the core says so when the folder is gone) starts it
    /// in <paramref name="home"/>. Each retry changes what the other cannot, so asking again until null ends after two.
    /// </summary>
    public static RestoreStep? Retry(RestoreStep step, string errorCode, string home)
    {
        if (errorCode == ErrorCodes.NotLinkable && step.Mode == TerminalMode.Linked)
        {
            return step with { Mode = TerminalMode.Locked, Fallbacks = [.. step.Fallbacks, new Fallback("mode", "linked", "locked")] };
        }
        if (errorCode == ErrorCodes.SpawnFailed && !string.Equals(step.Folder, home, StringComparison.OrdinalIgnoreCase))
        {
            return step with { Folder = home, Fallbacks = [.. step.Fallbacks, new Fallback("folder", step.Folder, home)] };
        }
        return null;
    }

    /// <summary>
    /// The saved indexes of the tabs to show, in the order to show them, once the sessions in <paramref name="started"/>
    /// (their saved index and pane, in the order they started) are running: each pane's own front tab (the saved one, else its
    /// last session that started), then the tab in front (the saved one, else the last session that started), so the split dock
    /// shows each pane's tab in its half and the one view ends on the saved front tab. Empty when none started.
    /// </summary>
    public static IReadOnlyList<int> ShowOrder(TerminalLayout saved, IReadOnlyList<(int Index, int Pane)> started)
    {
        if (started.Count == 0)
        {
            return [];
        }
        int? FrontOf(int pane)
        {
            if (saved.ShownIn(pane) is { } wanted && started.Any(s => s.Index == wanted && s.Pane == pane))
            {
                return wanted;
            }
            var own = started.Where(s => s.Pane == pane).ToList();
            return own.Count > 0 ? own[^1].Index : null;
        }
        var order = new List<int>();
        foreach (var pane in new[] { 0, 1 })
        {
            if (FrontOf(pane) is { } shown)
            {
                order.Add(shown);
            }
        }
        order.Add(saved.Front is { } front && started.Any(s => s.Index == front) ? front : started[^1].Index);
        return order;
    }
}
