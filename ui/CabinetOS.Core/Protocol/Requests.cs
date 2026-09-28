using System.Text.Json;
using System.Text.Json.Serialization;

namespace CabinetOS.Core.Protocol;

/// <summary>
/// A request from the UI to the core (docs/ipc.md, "Requests and replies").
/// On the wire it is one flat JSON object: <c>id</c>, <c>type</c>, then the
/// request's own fields. The Rust types in <c>cabinetos-protocol</c> are the
/// source of truth; <c>sdk/protocol/request.schema.json</c> checks this mirror.
/// </summary>
public abstract class CoreRequest
{
    private protected CoreRequest(string type) => Type = type;

    /// <summary>The request's ULID; the reply carries the same one.</summary>
    [JsonPropertyOrder(-2)]
    public string Id { get; set; } = "";

    /// <summary>The request type, for example <c>list_directory</c>.</summary>
    [JsonPropertyOrder(-1)]
    public string Type { get; }
}

/// <summary>Asks whether the core is alive; the reply is <c>pong</c>.</summary>
public sealed class PingRequest() : CoreRequest("ping");

/// <summary>Asks the core to exit; the reply is <c>ok</c>.</summary>
public sealed class ShutdownRequest() : CoreRequest("shutdown");

/// <summary>Introduces the client; the reply is <c>welcome</c>.</summary>
public sealed class HelloRequest(uint clientPid, string clientName) : CoreRequest("hello")
{
    /// <summary>This process, which the core checks against the pipe.</summary>
    public uint ClientPid { get; } = clientPid;

    /// <summary>A name for the core's log.</summary>
    public string ClientName { get; } = clientName;
}

/// <summary>The order of a listing; the core sorts, never the UI.</summary>
public sealed record SortSpec(string Key, bool Descending);

/// <summary>Lists a directory into shared memory; the reply is <c>listing_opened</c>.</summary>
public sealed class ListDirectoryRequest(string path) : CoreRequest("list_directory")
{
    /// <summary>The directory.</summary>
    public string Path { get; } = path;

    /// <summary>Hidden and system entries too; absent: <c>panes.showHidden</c> decides.</summary>
    public bool? IncludeHidden { get; init; }

    /// <summary>The order; absent: <c>panes.sort</c> decides.</summary>
    public SortSpec? Sort { get; init; }

    /// <summary>Keep watching and send <c>listing_refreshed</c> events.</summary>
    public bool Watch { get; init; }
}

/// <summary>Ends a listing; the reply is <c>ok</c>.</summary>
public sealed class CloseListingRequest(ulong listingId) : CoreRequest("close_listing")
{
    /// <summary>The listing, from <c>listing_opened</c>.</summary>
    public ulong ListingId { get; } = listingId;
}

/// <summary>Asks for the volume and disk of a path; the reply is <c>volume_info</c>.</summary>
public sealed class VolumeInfoRequest(string path) : CoreRequest("volume_info")
{
    /// <summary>Any path on the volume.</summary>
    public string Path { get; } = path;
}

/// <summary>
/// Asks for every volume. Not in protocol version 7: until the core adds it,
/// the reply is <c>error</c> with <c>unknown_request</c> (Phase 5, Part D).
/// </summary>
public sealed class ListVolumesRequest() : CoreRequest("list_volumes");

/// <summary>Asks for the configuration in effect; the reply is <c>config</c>.</summary>
public sealed class GetConfigRequest() : CoreRequest("get_config");

/// <summary>Asks for the compiled keymap; the reply is <c>keymap</c>.</summary>
public sealed class GetKeymapRequest() : CoreRequest("get_keymap");

/// <summary>Asks for every command; the reply is <c>commands</c>.</summary>
public sealed class ListCommandsRequest() : CoreRequest("list_commands");

/// <summary>Ranks the commands for the palette; the reply is <c>search_results</c>.</summary>
public sealed class SearchCommandsRequest(string query, uint limit) : CoreRequest("search_commands")
{
    /// <summary>What the user typed.</summary>
    public string Query { get; } = query;

    /// <summary>At most this many hits.</summary>
    public uint Limit { get; } = limit;
}

/// <summary>Runs a command; the reply is <c>command_result</c> or <c>command_routed</c>.</summary>
public sealed class ExecuteCommandRequest(string command) : CoreRequest("execute_command")
{
    /// <summary>The command's ID.</summary>
    public string Command { get; } = command;

    /// <summary>Arguments, if the command takes any.</summary>
    public JsonElement? Args { get; init; }
}

/// <summary>Binds a command to new keys; the reply is <c>keymap</c>.</summary>
public sealed class SetKeybindingRequest(string command, string keys) : CoreRequest("set_keybinding")
{
    /// <summary>The command's ID.</summary>
    public string Command { get; } = command;

    /// <summary>The keys in the grammar of docs/keybindings.md; empty for none.</summary>
    public string Keys { get; } = keys;
}

/// <summary>Gives a command its default keys back; the reply is <c>keymap</c>.</summary>
public sealed class ResetKeybindingRequest(string command) : CoreRequest("reset_keybinding")
{
    /// <summary>The command's ID.</summary>
    public string Command { get; } = command;
}
