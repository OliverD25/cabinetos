using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CabinetOS.Core.Protocol;

/// <summary>A reply from the core, matched to its request by <c>id</c>.</summary>
public abstract record CoreReply;

/// <summary>
/// A message that hands the UI a shared-memory section: <c>listing_opened</c>
/// and <c>listing_refreshed</c>. The pipe client wraps the handle as soon as
/// the message arrives, so a handle nobody takes is still closed.
/// </summary>
public interface ICarriesSection
{
    /// <summary>The handle's value, already valid in this process.</summary>
    ulong SectionHandle { get; }

    /// <summary>The bytes of the section that hold the listing.</summary>
    ulong SectionSize { get; }

    /// <summary>The owned handle, until someone takes it.</summary>
    SafeHandle? Section { get; set; }
}

/// <summary>Ownership transfer for <see cref="ICarriesSection"/>.</summary>
public static class SectionCarrierExtensions
{
    /// <summary>Takes ownership of the section handle; later calls return null.</summary>
    public static SafeHandle? TakeSection(this ICarriesSection carrier)
    {
        var section = carrier.Section;
        carrier.Section = null;
        return section;
    }
}

/// <summary>Reply to <c>ping</c>.</summary>
public sealed record PongReply(uint ProtocolVersion, string CoreVersion) : CoreReply;

/// <summary>The request succeeded and returns nothing.</summary>
public sealed record OkReply : CoreReply;

/// <summary>The request failed; <see cref="Code"/> is one of <see cref="ErrorCodes"/>.</summary>
public sealed record ErrorReply(string Code, string Message) : CoreReply;

/// <summary>Reply to <c>hello</c>.</summary>
public sealed record WelcomeReply(uint ProtocolVersion, string CoreVersion) : CoreReply;

/// <summary>Reply to <c>list_directory</c>: the listing is complete in shared memory.</summary>
public sealed record ListingOpenedReply(
    ulong ListingId,
    ulong SectionHandle,
    ulong SectionSize,
    uint EntryCount,
    uint Generation,
    ulong ElapsedUs) : CoreReply, ICarriesSection
{
    /// <inheritdoc/>
    [JsonIgnore]
    public SafeHandle? Section { get; set; }
}

/// <summary>The physical disk under a volume; every field is best effort.</summary>
public sealed record DiskIdentity(uint DeviceNumber, string BusType, bool? SeekPenalty, string? MediaType);

/// <summary>One volume, as <c>volume_info</c> describes it.</summary>
public sealed record VolumeDetails(
    string? DriveLetter,
    string VolumeGuidPath,
    string Filesystem,
    string Label,
    ulong TotalBytes,
    ulong FreeBytes,
    DiskIdentity? Disk);

/// <summary>Reply to <c>volume_info</c>.</summary>
public sealed record VolumeInfoReply(
    string? DriveLetter,
    string VolumeGuidPath,
    string Filesystem,
    string Label,
    ulong TotalBytes,
    ulong FreeBytes,
    DiskIdentity? Disk) : CoreReply
{
    /// <summary>The same fields as a <see cref="VolumeDetails"/>.</summary>
    public VolumeDetails ToDetails() => new(DriveLetter, VolumeGuidPath, Filesystem, Label, TotalBytes, FreeBytes, Disk);
}

/// <summary>Reply to <c>list_volumes</c> (not in protocol version 7 yet).</summary>
public sealed record VolumesReply(IReadOnlyList<VolumeDetails> Volumes) : CoreReply;

/// <summary>Reply to <c>get_config</c>: the whole configuration, defaults included.</summary>
public sealed record ConfigReply(string Path, JsonElement Config) : CoreReply;

/// <summary>One binding of the compiled keymap.</summary>
public sealed record KeymapBinding(string Keys, string Command, string? When);

/// <summary>The compiled keymap, as <c>keymap_changed</c> carries it.</summary>
public sealed record KeymapData(uint ChordWindowMs, IReadOnlyList<KeymapBinding> Bindings, IReadOnlyList<string> Immutable);

/// <summary>Reply to <c>get_keymap</c>, <c>set_keybinding</c> and <c>reset_keybinding</c>.</summary>
public sealed record KeymapReply(uint ChordWindowMs, IReadOnlyList<KeymapBinding> Bindings, IReadOnlyList<string> Immutable) : CoreReply
{
    /// <summary>The same keymap as a <see cref="KeymapData"/>.</summary>
    public KeymapData ToData() => new(ChordWindowMs, Bindings, Immutable);
}

/// <summary>Who provides a command: <c>core</c>, or <c>plugin</c> with its ID and name.</summary>
public sealed record CommandSource(string Kind, string? Id, string? Name);

/// <summary>A command, as the palette shows it.</summary>
public sealed record CommandInfo(
    string Id,
    string Category,
    string Title,
    IReadOnlyList<string> Keys,
    IReadOnlyList<string> DefaultKeys,
    CommandSource Source,
    string Target,
    string? When,
    bool Immutable);

/// <summary>Reply to <c>list_commands</c>, in registry order.</summary>
public sealed record CommandsReply(IReadOnlyList<CommandInfo> Commands) : CoreReply;

/// <summary>One command that matches a palette search.</summary>
public sealed record SearchHit(string Id, int Score);

/// <summary>Reply to <c>search_commands</c>, best first.</summary>
public sealed record SearchResultsReply(IReadOnlyList<SearchHit> Hits) : CoreReply;

/// <summary>Reply to <c>execute_command</c> for a command the UI runs.</summary>
public sealed record CommandRoutedReply(string Target) : CoreReply;

/// <summary>Reply to <c>execute_command</c> for a command the core ran.</summary>
public sealed record CommandResultReply(JsonElement Result) : CoreReply;

/// <summary>The error codes of docs/ipc.md that the UI acts on.</summary>
public static class ErrorCodes
{
    public const string UnknownRequest = "unknown_request";
    public const string ProtocolError = "protocol_error";
    public const string NotFound = "not_found";
    public const string AccessDenied = "access_denied";
    public const string InvalidPath = "invalid_path";
    public const string NoSuchListing = "no_such_listing";
    public const string UnknownCommand = "unknown_command";
    public const string NotImplemented = "not_implemented";
    public const string InvalidKeys = "invalid_keys";
    public const string KeybindingConflict = "keybinding_conflict";
    public const string ImmutableBinding = "immutable_binding";
    public const string ConfigError = "config_error";
}
