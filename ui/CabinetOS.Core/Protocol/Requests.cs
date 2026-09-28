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
/// Asks for every volume (protocol version 8); the reply is <c>volumes</c>.
/// An older core answers <c>unknown_request</c>, and the Drives section stays hidden.
/// </summary>
public sealed class ListVolumesRequest() : CoreRequest("list_volumes");

/// <summary>Asks for the configuration in effect; the reply is <c>config</c>.</summary>
public sealed class GetConfigRequest() : CoreRequest("get_config");

/// <summary>Asks for one setting in effect (version 8); the reply is <c>value</c>.</summary>
public sealed class GetValueRequest(string path) : CoreRequest("get_value")
{
    /// <summary>The setting as a dotted path, for example <c>ui.dualPane</c>.</summary>
    public string Path { get; } = path;
}

/// <summary>
/// Changes one setting (version 8); the reply is <c>ok</c>, and every client
/// that said hello then gets <c>config_changed</c>, this one too.
/// </summary>
public sealed class SetValueRequest(string path, JsonElement value) : CoreRequest("set_value")
{
    /// <summary>The setting as a dotted path, for example <c>ui.lastPaths</c>.</summary>
    public string Path { get; } = path;

    /// <summary>Its new value, in the file's own format.</summary>
    public JsonElement Value { get; } = value;
}

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

/// <summary>What a job does: <c>copy</c>, <c>move</c>, or <c>delete</c> (docs/jobs.md).</summary>
/// <param name="Type">The kind's tag.</param>
/// <param name="Permanent">For a delete: for good instead of to the Recycle Bin.</param>
public sealed record JobKind(string Type, bool? Permanent = null)
{
    /// <summary>A copy into the destination folder.</summary>
    public static readonly JobKind Copy = new("copy");

    /// <summary>A move into the destination folder.</summary>
    public static readonly JobKind Move = new("move");

    /// <summary>A delete, to the Recycle Bin or for good.</summary>
    public static JobKind Delete(bool permanent) => new("delete", permanent);

    /// <summary>Whether this is a delete for good.</summary>
    [JsonIgnore]
    public bool IsPermanentDelete => Type == "delete" && Permanent == true;
}

/// <summary>The options of <c>start_job</c>; a field left null takes the core's default.</summary>
public sealed record JobOptions(string? OnConflict = null, string? CopyLinks = null, bool? Verify = null, bool? PreserveTimestamps = null);

/// <summary>Starts a copy, move or delete; the reply is <c>job_started</c>, the work comes as events.</summary>
public sealed class StartJobRequest(JobKind kind, IReadOnlyList<string> sources) : CoreRequest("start_job")
{
    /// <summary>Copy, move or delete.</summary>
    public JobKind Kind { get; } = kind;

    /// <summary>Absolute paths of the files and folders.</summary>
    public IReadOnlyList<string> Sources { get; } = sources;

    /// <summary>The folder a copy or move goes into; none for a delete.</summary>
    public string? Destination { get; init; }

    /// <summary>Conflict policy and the rest; absent: the core's defaults (<c>on_conflict: ask</c>).</summary>
    public JobOptions? Options { get; init; }
}

/// <summary>Asks for every job the core knows; the reply is <c>jobs</c>.</summary>
public sealed class ListJobsRequest() : CoreRequest("list_jobs");

/// <summary>The actions of <c>job_control</c>.</summary>
public static class JobActions
{
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Cancel = "cancel";
}

/// <summary>Pauses, resumes or cancels a job; the reply is <c>ok</c>.</summary>
public sealed class JobControlRequest(ulong jobId, string action) : CoreRequest("job_control")
{
    /// <summary>The job, from <c>job_started</c>.</summary>
    public ulong JobId { get; } = jobId;

    /// <summary>One of <see cref="JobActions"/>.</summary>
    public string Action { get; } = action;
}

/// <summary>A decision for a file that waits on a conflict (docs/jobs.md, "Conflicts").</summary>
/// <param name="Type"><c>overwrite</c>, <c>skip</c>, <c>rename</c>, <c>retry</c>, <c>delete_permanently</c> or <c>cancel_job</c>.</param>
/// <param name="NewName">For <c>rename</c>: the new name; absent, the core picks <c>name (2).ext</c>.</param>
public sealed record Resolution(string Type, string? NewName = null)
{
    public const string OverwriteType = "overwrite";
    public const string SkipType = "skip";
    public const string RenameType = "rename";
    public const string RetryType = "retry";
    public const string DeletePermanentlyType = "delete_permanently";
    public const string CancelJobType = "cancel_job";
}

/// <summary>Decides a conflict; the reply is <c>ok</c>.</summary>
public sealed class ResolveConflictRequest(ulong jobId, ulong conflictId, Resolution resolution) : CoreRequest("resolve_conflict")
{
    /// <summary>The job, from <c>job_conflict</c>.</summary>
    public ulong JobId { get; } = jobId;

    /// <summary>The conflict, from <c>job_conflict</c>.</summary>
    public ulong ConflictId { get; } = conflictId;

    /// <summary>The decision.</summary>
    public Resolution Resolution { get; } = resolution;

    /// <summary>Also the job's other conflicts of this kind, waiting and still to come.</summary>
    public bool ApplyToSameKind { get; init; }
}

/// <summary>
/// Opens a file with its default program (protocol version 8, "open_path");
/// the reply is <c>ok</c>. Built against the shape the core agreed on; an
/// older core answers <c>unknown_request</c> and the UI stops offering it.
/// </summary>
public sealed class OpenPathRequest(string path) : CoreRequest("open_path")
{
    /// <summary>The file, as an absolute path.</summary>
    public string Path { get; } = path;
}

/// <summary>Creates one folder (version 8, "create_directory"); the reply is <c>ok</c>.</summary>
public sealed class CreateDirectoryRequest(string path) : CoreRequest("create_directory")
{
    /// <summary>The new folder, as an absolute path.</summary>
    public string Path { get; } = path;
}

/// <summary>Renames one entry in its folder (version 8, "rename"); the reply is <c>ok</c>.</summary>
public sealed class RenameRequest(string path, string newName) : CoreRequest("rename")
{
    /// <summary>The entry, as an absolute path.</summary>
    public string Path { get; } = path;

    /// <summary>The new name, without a folder part.</summary>
    public string NewName { get; } = newName;
}
