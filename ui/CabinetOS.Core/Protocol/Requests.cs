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
    [JsonPropertyOrder(-3)]
    public string Id { get; set; } = "";

    /// <summary>
    /// The user action this request belongs to (docs/diagnostics.md, "Trace
    /// ids"): the command run's ULID. Null leaves it out, and the core takes
    /// the request as an action of its own. <see cref="Ipc.CoreClient"/>
    /// fills it from <see cref="Diagnostics.Diag.CurrentTrace"/>.
    /// </summary>
    [JsonPropertyOrder(-2)]
    public string? Trace { get; set; }

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

/// <summary>
/// Asks for the type names and icon keys of a range of a listing (protocol
/// version 9); the reply is <c>entry_details</c>.
/// </summary>
public sealed class DescribeEntriesRequest(ulong listingId, uint from, uint count) : CoreRequest("describe_entries")
{
    /// <summary>The listing, from <c>listing_opened</c>.</summary>
    public ulong ListingId { get; } = listingId;

    /// <summary>The first entry, in section order.</summary>
    public uint From { get; } = from;

    /// <summary>How many entries, at most 512.</summary>
    public uint Count { get; } = count;
}

/// <summary>Asks for an icon as a PNG (version 9); the reply is <c>icon</c>.</summary>
public sealed class GetIconRequest(string key, uint size) : CoreRequest("get_icon")
{
    /// <summary>The icon key from <c>entry_details</c>.</summary>
    public string Key { get; } = key;

    /// <summary>16, 24, 32 or 48 pixels.</summary>
    public uint Size { get; } = size;
}

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

/// <summary>
/// Creates one empty file (version 12, New Text File); the reply is <c>ok</c>.
/// It never opens or replaces a file: a taken name is <c>already_exists</c>.
/// </summary>
public sealed class CreateFileRequest(string path) : CoreRequest("create_file")
{
    /// <summary>The new file, as an absolute path.</summary>
    public string Path { get; } = path;
}

/// <summary>
/// Opens a file for editing, never running it (version 12, F4): the reply is
/// <c>ok</c>. The core picks <c>files.editor</c>, else the type's edit verb, else Notepad.
/// </summary>
public sealed class EditPathRequest(string path) : CoreRequest("edit_path")
{
    /// <summary>The file, as an absolute path.</summary>
    public string Path { get; } = path;
}

/// <summary>Shows Windows' own property sheet for files and folders (version 12); the reply is <c>ok</c>.</summary>
public sealed class ShowPropertiesRequest(IReadOnlyList<string> paths) : CoreRequest("show_properties")
{
    /// <summary>Absolute paths; several get the shell's combined sheet.</summary>
    public IReadOnlyList<string> Paths { get; } = paths;
}

/// <summary>
/// Finds the rows of a listing whose names match patterns (version 12: the
/// pattern box, the same extension, quick search); the reply is <c>entry_matches</c>.
/// The core reads the names from its own section, as for <c>describe_entries</c>.
/// </summary>
public sealed class MatchEntriesRequest(ulong listingId, string patterns) : CoreRequest("match_entries")
{
    /// <summary>The listing, from <c>listing_opened</c>.</summary>
    public ulong ListingId { get; } = listingId;

    /// <summary>
    /// Total Commander's syntax: <c>*</c> and <c>?</c>, patterns separated by
    /// <c>;</c>, and a <c>|</c> before the ones to leave out; case is ignored.
    /// </summary>
    public string Patterns { get; } = patterns;

    /// <summary>Files only, no folders (the pattern box's "Include folders" off).</summary>
    public bool FilesOnly { get; init; }

    /// <summary>Only the first match at or after this row, wrapping to the start (quick search).</summary>
    public uint? FirstFrom { get; init; }
}

/// <summary>
/// Measures the files, folders and bytes under folders (version 12: the
/// Size column's measured folders); the reply is <c>measure_started</c>,
/// then <c>measure_progress</c> and <c>measure_finished</c> come as events.
/// </summary>
public sealed class MeasurePathsRequest(IReadOnlyList<string> paths) : CoreRequest("measure_paths")
{
    /// <summary>Absolute paths of folders (a file counts as itself).</summary>
    public IReadOnlyList<string> Paths { get; } = paths;
}

/// <summary>Stops a measure; the reply is <c>ok</c>, and <c>measure_finished</c> says it was cancelled.</summary>
public sealed class CancelMeasureRequest(ulong measureId) : CoreRequest("cancel_measure")
{
    /// <summary>The measure, from <c>measure_started</c>.</summary>
    public ulong MeasureId { get; } = measureId;
}

/// <summary>
/// Types paths at a shell's prompt, quoted for that shell, separated by
/// spaces and without Enter (version 12: Ctrl+P, Ctrl+Shift+Enter); the reply is <c>ok</c>.
/// </summary>
public sealed class TerminalTypePathsRequest(ulong sessionId, IReadOnlyList<string> paths) : CoreRequest("terminal_type_paths")
{
    /// <summary>The session.</summary>
    public ulong SessionId { get; } = sessionId;

    /// <summary>Absolute paths.</summary>
    public IReadOnlyList<string> Paths { get; } = paths;
}

/// <summary>Opens a shell in a pseudo-console (docs/terminal.md); the reply is <c>terminal_opened</c>.</summary>
public sealed class TerminalOpenRequest(ushort cols, ushort rows) : CoreRequest("terminal_open")
{
    /// <summary>Width in character cells.</summary>
    public ushort Cols { get; } = cols;

    /// <summary>Height in character cells.</summary>
    public ushort Rows { get; } = rows;

    /// <summary>A profile of <c>terminal.profiles</c>; absent: <c>terminal.defaultProfile</c>.</summary>
    public string? Profile { get; init; }

    /// <summary>The folder it starts in; absent: the user's profile folder.</summary>
    public string? Cwd { get; init; }
}

/// <summary>Tells a session its new size in cells; the reply is <c>ok</c>.</summary>
public sealed class TerminalResizeRequest(ulong sessionId, ushort cols, ushort rows) : CoreRequest("terminal_resize")
{
    /// <summary>The session.</summary>
    public ulong SessionId { get; } = sessionId;

    /// <summary>Width in character cells.</summary>
    public ushort Cols { get; } = cols;

    /// <summary>Height in character cells.</summary>
    public ushort Rows { get; } = rows;
}

/// <summary>Closes a session (a hang-up for the shell); the reply is <c>ok</c> once the shell ended.</summary>
public sealed class TerminalCloseRequest(ulong sessionId) : CoreRequest("terminal_close")
{
    /// <summary>The session.</summary>
    public ulong SessionId { get; } = sessionId;
}

/// <summary>Types the shell's own change-directory command, then Enter; the reply is <c>ok</c>.</summary>
public sealed class TerminalSyncCwdRequest(ulong sessionId, string path) : CoreRequest("terminal_sync_cwd")
{
    /// <summary>The session.</summary>
    public ulong SessionId { get; } = sessionId;

    /// <summary>An absolute path to a folder.</summary>
    public string Path { get; } = path;
}

/// <summary>Asks for every session; the reply is <c>terminal_sessions</c>.</summary>
public sealed class TerminalListRequest() : CoreRequest("terminal_list");

/// <summary>Searches files and folders by name (docs/indexer.md); the reply is <c>file_search_results</c>.</summary>
public sealed class SearchRequest(string query) : CoreRequest("search")
{
    /// <summary>What the user typed; the core ranks.</summary>
    public string Query { get; } = query;

    /// <summary>At most this many hits (default 100, at most 1,000).</summary>
    public uint? Limit { get; init; }

    /// <summary>Only hits under this folder; absent: every indexed volume.</summary>
    public string? Root { get; init; }
}

/// <summary>Asks whether the indexer answers, and for its volumes; the reply is <c>index_status</c>.</summary>
public sealed class IndexStatusRequest() : CoreRequest("index_status");

/// <summary>Asks for every installed plugin (docs/plugins.md); the reply is <c>plugins</c>.</summary>
public sealed class ListPluginsRequest() : CoreRequest("list_plugins");

/// <summary>Reads a plugin's folder again and starts it; the reply is <c>ok</c>.</summary>
public sealed class ReloadPluginRequest(string pluginId) : CoreRequest("reload_plugin")
{
    /// <summary>The plugin.</summary>
    public string PluginId { get; } = pluginId;
}

/// <summary>Turns a plugin on or off in the configuration; the reply is <c>ok</c>.</summary>
public sealed class SetPluginEnabledRequest(string pluginId, bool enabled) : CoreRequest("set_plugin_enabled")
{
    /// <summary>The plugin.</summary>
    public string PluginId { get; } = pluginId;

    /// <summary>On or off.</summary>
    public bool Enabled { get; } = enabled;
}

/// <summary>Grants capabilities to a plugin (the review dialog's "Allow"); the reply is <c>ok</c>.</summary>
public sealed class GrantCapabilitiesRequest(string pluginId, IReadOnlyList<string> capabilities) : CoreRequest("grant_capabilities")
{
    /// <summary>The plugin.</summary>
    public string PluginId { get; } = pluginId;

    /// <summary>The capabilities, such as <c>fs:read</c>.</summary>
    public IReadOnlyList<string> Capabilities { get; } = capabilities;
}

/// <summary>Renames one entry in its folder (version 8, "rename"); the reply is <c>ok</c>.</summary>
public sealed class RenameRequest(string path, string newName) : CoreRequest("rename")
{
    /// <summary>The entry, as an absolute path.</summary>
    public string Path { get; } = path;

    /// <summary>The new name, without a folder part.</summary>
    public string NewName { get; } = newName;
}

/// <summary>Asks for every valid theme in the themes folder (docs/themes.md); the reply is <c>themes</c>.</summary>
public sealed class ListThemesRequest() : CoreRequest("list_themes");

/// <summary>Asks for one whole theme; the reply is <c>theme</c>.</summary>
public sealed class GetThemeRequest() : CoreRequest("get_theme")
{
    /// <summary>The theme's ID; absent: the theme in effect now.</summary>
    public string? ThemeId { get; init; }
}

/// <summary>Asks for every installed Tool Extension (version 10); the reply is <c>tools</c>.</summary>
public sealed class ListToolsRequest() : CoreRequest("list_tools");

/// <summary>
/// Reads the marketplace index that <c>marketplace.index</c> names (version
/// 10); the reply is <c>marketplace_index</c>, in the index's order.
/// </summary>
public sealed class MarketplaceRefreshRequest() : CoreRequest("marketplace_refresh");

/// <summary>Searches the index read last; the reply is <c>marketplace_index</c>, best first.</summary>
public sealed class MarketplaceSearchRequest(string query) : CoreRequest("marketplace_search")
{
    /// <summary>Text to look for in the name, the ID or the publisher; empty for every item.</summary>
    public string Query { get; } = query;

    /// <summary>Only items of this kind (<see cref="ExtensionKinds"/>); absent: every kind.</summary>
    public string? Kind { get; init; }
}

/// <summary>
/// Downloads an extension from the index, checks its SHA-256 and installs it;
/// the reply is <c>ok</c> once it is in place. <c>install_progress</c> and
/// <c>install_finished</c> go to every client meanwhile.
/// </summary>
public sealed class InstallExtensionRequest(string extensionId) : CoreRequest("install_extension")
{
    /// <summary>The extension's ID in the index (not <c>id</c>, which is the request's own).</summary>
    public string ExtensionId { get; } = extensionId;

    /// <summary>The version; absent: the newest this core can run.</summary>
    public string? Version { get; init; }
}

/// <summary>Removes exactly the files an install put in place; the reply is <c>ok</c>.</summary>
public sealed class UninstallExtensionRequest(string extensionId) : CoreRequest("uninstall_extension")
{
    /// <summary>The extension's ID.</summary>
    public string ExtensionId { get; } = extensionId;
}
