using System.Globalization;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Themes;

namespace CabinetOS.Core.Plugins;

/// <summary>The capability levels of docs/plugins.md and their colours.</summary>
public static class CapabilityLevels
{
    /// <summary>
    /// The colours in use: the design's (low green, medium yellow, high red;
    /// docs/design/README.md, "Permission levels") until a theme sets its own
    /// (<c>permissionLow</c>, <c>permissionMedium</c>, <c>permissionHigh</c>).
    /// </summary>
    public static LevelColors Current { get; set; } = LevelColors.Design;

    /// <summary>The dot's colour for <paramref name="level"/>, as <c>#RRGGBB</c>.</summary>
    public static string Color(string level)
    {
        var color = Current.For(level);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}

/// <summary>One capability as the list and the review dialog show it.</summary>
public sealed record CapabilityRow(string Name, string Level, string Reason, string? Roots, bool Granted)
{
    /// <summary>"LOW", "MEDIUM", "HIGH".</summary>
    public string LevelText => Level.ToUpperInvariant();

    /// <summary>The level's color.</summary>
    public string Color => CapabilityLevels.Color(Level);

    /// <summary>The reason, and for file access the folders it covers.</summary>
    public string Detail => Roots is { Length: > 0 } roots ? $"{Reason} ({roots})" : Reason;

    /// <summary>The row of a capability of the core's list.</summary>
    public static CapabilityRow From(CapabilityInfo capability) =>
        new(capability.Name, capability.Level, capability.Reason,
            capability.Roots is { Count: > 0 } roots ? string.Join("; ", roots) : null, capability.Granted);
}

/// <summary>
/// A plugin's tile: its first letters on a color taken from its ID, until
/// plugins bring icons of their own (docs/design/README.md, "Assets").
/// </summary>
public sealed record PluginTile(string Text, string Color)
{
    private static readonly string[] Colors = ["#F0906C", "#F2C063", "#C9B6FF", "#60CDFF", "#8AD2B8", "#F5A8C9"];

    /// <summary>The tile for <paramref name="plugin"/>.</summary>
    public static PluginTile For(PluginInfo plugin) => For(plugin.Id, plugin.Name);

    /// <summary>The tile of the extension <paramref name="id"/> named <paramref name="name"/>.</summary>
    public static PluginTile For(string id, string name)
    {
        var words = name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        var text = words.Length switch
        {
            0 => "?",
            1 => words[0][..Math.Min(2, words[0].Length)],
            _ => string.Concat(words[0][0], words[1][0]),
        };
        // A stable pick: the same plugin keeps its color from one start to the next.
        var hash = 0u;
        foreach (var c in id)
        {
            hash = (hash * 31) + c;
        }
        return new PluginTile(char.ToUpperInvariant(text[0]) + text[1..], Colors[hash % (uint)Colors.Length]);
    }
}

/// <summary>One plugin in the list of <c>plugins.list</c>.</summary>
public sealed record PluginRow(PluginInfo Plugin)
{
    /// <summary>"Hello 0.1.0".</summary>
    public string Title => $"{Plugin.Name} {Plugin.Version}";

    /// <summary>What the plugin is doing, in words.</summary>
    public string StateText => Plugin.State switch
    {
        { Type: PluginState.Active } => Plugin.Commands.Count switch
        {
            0 => "Active",
            1 => "Active: 1 command",
            var n => string.Create(CultureInfo.InvariantCulture, $"Active: {n} commands"),
        },
        { Type: PluginState.Loading } => "Starting",
        { Type: PluginState.Disabled } => "Turned off",
        { Type: PluginState.NeedsReview, Missing: { Count: > 0 } missing } => $"Waits for your review: {string.Join(", ", missing)}",
        { Type: PluginState.NeedsReview } => "Waits for your review",
        { Type: PluginState.Failed, Message: { } message } => $"Cannot start: {message}",
        { Type: PluginState.Crashed, Message: { } message, AtMs: { } at } =>
            $"Crashed at {DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime():HH:mm:ss}: {message}",
        { Type: PluginState.Crashed, Message: { } message } => $"Crashed: {message}",
        var other => other.Type,
    };

    /// <summary>Whether the state is a problem to show in red.</summary>
    public bool IsProblem => Plugin.State.Type is PluginState.Failed or PluginState.Crashed;

    /// <summary>What it asks for.</summary>
    public IReadOnlyList<CapabilityRow> Capabilities { get; } = Plugin.Capabilities.Select(CapabilityRow.From).ToList();

    /// <summary>Whether "Review permissions" is offered: it waits for grants.</summary>
    public bool CanReview => Plugin.State.Type == PluginState.NeedsReview;

    /// <summary>Whether "Reload" is offered: it crashed, or could not start (a fixed manifest is read again).</summary>
    public bool CanReload => Plugin.State.Type is PluginState.Crashed or PluginState.Failed;

    /// <summary>The tile.</summary>
    public PluginTile Tile => PluginTile.For(Plugin);
}

/// <summary>
/// The permissions review dialog (design view C, docs/ui.md, "Plugins"):
/// every capability the plugin asks for, with its level and reason. "Allow
/// and install" grants those not granted yet (<c>grant_capabilities</c>);
/// the core then starts the plugin. "Trust {author}" waits for the
/// marketplace, which brings publisher identities.
/// </summary>
public sealed class PermissionReview(PluginInfo plugin)
{
    /// <summary>The dialog's sentence under the title.</summary>
    public const string Sandbox = "This plugin runs in a WebAssembly sandbox. It can only do what you allow here.";

    /// <summary>Why "Trust …" cannot be ticked yet.</summary>
    public const string TrustNotYet = "Publisher identities come with the marketplace.";

    /// <summary>The plugin under review.</summary>
    public PluginInfo Plugin { get; } = plugin;

    /// <summary>The marketplace item this review installs ("Allow and install"), or null for an installed plugin.</summary>
    public MarketItem? Install { get; private init; }

    /// <summary>
    /// The review of a plugin before it is installed from the marketplace:
    /// what the index says it asks for, which the core checks against the
    /// plugin's own manifest before installing (trust rule 3). Allowing
    /// installs it and grants all of them.
    /// </summary>
    public static PermissionReview ForInstall(MarketItem item)
    {
        var capabilities = (item.Capabilities ?? [])
            .Select(c => new CapabilityInfo(c.Name, c.Level ?? "unknown", false, c.Reason, c.Roots))
            .ToList();
        var plugin = new PluginInfo(item.Id, item.Name, item.Version, item.Author.Name, item.Description,
            new PluginState(PluginState.NeedsReview, capabilities.Select(c => c.Name).ToList()), capabilities, []);
        return new PermissionReview(plugin) { Install = item };
    }

    /// <summary>"Hello by CabinetOS".</summary>
    public string Subtitle => $"{Plugin.Name} by {Plugin.Author}";

    /// <summary>"Trust CabinetOS for future updates".</summary>
    public string TrustText => $"Trust {Plugin.Author} for future updates";

    /// <summary>Every capability it asks for, in the manifest's order.</summary>
    public IReadOnlyList<CapabilityRow> Rows { get; } = plugin.Capabilities.Select(CapabilityRow.From).ToList();

    /// <summary>What "Allow" grants: what the core says is missing, else what is not granted.</summary>
    public IReadOnlyList<string> ToGrant { get; } = plugin.State.Missing is { Count: > 0 } missing
        ? missing
        : plugin.Capabilities.Where(c => !c.Granted).Select(c => c.Name).ToList();

    /// <summary>The tile.</summary>
    public PluginTile Tile => PluginTile.For(Plugin);

    /// <summary>Why the grant failed, shown in the dialog.</summary>
    public string? Error { get; private set; }

    /// <summary>Whether a grant is on its way (the buttons wait).</summary>
    public bool IsBusy { get; private set; }

    /// <summary>
    /// Grants the missing capabilities. True once the core wrote them (the
    /// plugin is then starting); false with <see cref="Error"/> set.
    /// </summary>
    public async Task<bool> AllowAsync(ICoreChannel core, string? requestId = null)
    {
        if (IsBusy)
        {
            return false;
        }
        IsBusy = true;
        Error = null;
        try
        {
            var reply = await core.RequestAsync(new GrantCapabilitiesRequest(Plugin.Id, ToGrant) { Id = requestId ?? "" });
            switch (reply)
            {
                case OkReply:
                    return true;
                case ErrorReply error:
                    Error = error.Code == ErrorCodes.NoSuchPlugin ? $"{Plugin.Name} is not installed any more." : error.Message;
                    return false;
                default:
                    Error = $"Unexpected reply {reply.GetType().Name}.";
                    return false;
            }
        }
        catch (IOException error)
        {
            Error = error.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>What changed between two looks at the plugins, for the status bar and the review dialog.</summary>
public sealed record PluginChanges(IReadOnlyList<PluginInfo> BecameActive, IReadOnlyList<PluginInfo> ToReview, IReadOnlyList<PluginInfo> Crashed);

/// <summary>
/// Remembers the plugins' states, so the window can say when one became
/// active or crashed, and open the review dialog for a plugin that newly
/// waits for grants: once per plugin per session, so Cancel is respected
/// until the window starts again (the list offers the review any time).
/// </summary>
public sealed class PluginWatch
{
    private readonly Dictionary<string, string> _states = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reviewed = new(StringComparer.Ordinal);
    private bool _first = true;

    /// <summary>The plugins as last seen.</summary>
    public IReadOnlyList<PluginInfo> Plugins { get; private set; } = [];

    /// <summary>Whether no list was taken yet (since the start, or since the core started again).</summary>
    public bool IsFresh => _first;

    /// <summary>
    /// Takes a new list. The first list only sets what is known: plugins that
    /// wait for review at start are announced, not put in front of the user.
    /// </summary>
    public PluginChanges Update(IReadOnlyList<PluginInfo> plugins)
    {
        var active = new List<PluginInfo>();
        var review = new List<PluginInfo>();
        var crashed = new List<PluginInfo>();
        foreach (var plugin in plugins)
        {
            var before = _states.GetValueOrDefault(plugin.Id);
            var now = plugin.State.Type;
            _states[plugin.Id] = now;
            if (_first || before == now)
            {
                continue;
            }
            switch (now)
            {
                case PluginState.Active:
                    active.Add(plugin);
                    break;
                case PluginState.NeedsReview when _reviewed.Add(plugin.Id):
                    review.Add(plugin);
                    break;
                case PluginState.Crashed:
                    crashed.Add(plugin);
                    break;
            }
        }
        _first = false;
        Plugins = plugins;
        return new PluginChanges(active, review, crashed);
    }

    /// <summary>The review was shown for <paramref name="pluginId"/> (from the list): no automatic one after.</summary>
    public void MarkReviewed(string pluginId) => _reviewed.Add(pluginId);

    /// <summary>The core started again: its plugins start over, and so does what is known.</summary>
    public void Reset()
    {
        _states.Clear();
        _first = true;
    }
}
