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

/// <summary>The listing a reply hands over in shared memory, as <c>listing_opened</c> describes one.</summary>
public sealed record OpenedListing(ulong ListingId, ulong SectionHandle, ulong SectionSize, uint EntryCount, uint Generation, ulong ElapsedUs);

/// <summary>
/// Reply to <c>open_preview</c> (and <c>preview_listing</c>): proposed changes
/// as a listing whose header has the preview flag (docs/ipc.md, "Previews").
/// </summary>
public sealed record PreviewOpenedReply(string Preview, string Title, OpenedListing Listing) : CoreReply, ICarriesSection
{
    /// <inheritdoc/>
    [JsonIgnore]
    public ulong SectionHandle => Listing.SectionHandle;

    /// <inheritdoc/>
    [JsonIgnore]
    public ulong SectionSize => Listing.SectionSize;

    /// <inheritdoc/>
    [JsonIgnore]
    public SafeHandle? Section { get; set; }
}

/// <summary>Reply to <c>preview_apply</c>: the jobs that run the rows, in order.</summary>
public sealed record JobsStartedReply(IReadOnlyList<ulong> Jobs) : CoreReply;

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

/// <summary>Reply to <c>list_volumes</c>: every drive letter's volume, in letter order.</summary>
public sealed record VolumesReply(IReadOnlyList<VolumeDetails> Volumes) : CoreReply;

/// <summary>Reply to <c>get_config</c>: the whole configuration, defaults included.</summary>
public sealed record ConfigReply(string Path, JsonElement Config) : CoreReply;

/// <summary>Reply to <c>get_value</c>: one setting in effect, in the file's own format.</summary>
public sealed record ValueReply(JsonElement Value) : CoreReply;

/// <summary>What the shell shows beside a name: its type name and the key of its icon.</summary>
public sealed record EntryDetail(string TypeName, string IconKey);

/// <summary>Reply to <c>describe_entries</c>: one detail per entry from <see cref="From"/> on.</summary>
public sealed record EntryDetailsReply(ulong ListingId, uint Generation, uint From, IReadOnlyList<EntryDetail> Details) : CoreReply;

/// <summary>Reply to <c>get_icon</c>: a PNG of <see cref="Size"/> pixels square, with alpha.</summary>
public sealed record IconReply(string Key, uint Size, string PngBase64) : CoreReply;

/// <summary>
/// Reply to <c>match_entries</c> (version 12): the matching rows of the
/// section of <see cref="Generation"/>, as <c>[start, count]</c> pairs.
/// </summary>
public sealed record EntryMatchesReply(ulong ListingId, uint Generation, IReadOnlyList<IReadOnlyList<ulong>> Ranges) : CoreReply;

/// <summary>Reply to <c>measure_paths</c> (version 12): the measure's ID; its progress comes as events.</summary>
public sealed record MeasureStartedReply(ulong MeasureId) : CoreReply;

/// <summary>Reply to <c>start_job</c>: the paths were checked and the job is queued.</summary>
public sealed record JobStartedReply(ulong JobId) : CoreReply;

/// <summary>A job as <c>list_jobs</c> describes it: its progress and what it works on.</summary>
public sealed record JobInfo(
    JobKind Kind,
    IReadOnlyList<string> Sources,
    string? Destination,
    ulong JobId,
    JobState State,
    ulong BytesDone,
    ulong BytesTotal,
    ulong FilesDone,
    ulong FilesTotal,
    ulong FilesSkipped,
    ulong FilesFailed,
    ulong ConflictsOpen,
    string? CurrentPath,
    ulong SpeedBps,
    ulong? EtaSeconds,
    ulong ElapsedMs,
    double? ItemsPerSecond = null)
{
    /// <summary>The progress part, as a <c>job_progress</c> event carries it.</summary>
    public JobProgressEvent ToProgress() => new(JobId, State, BytesDone, BytesTotal, FilesDone, FilesTotal,
        FilesSkipped, FilesFailed, ConflictsOpen, CurrentPath, SpeedBps, EtaSeconds, ElapsedMs, ItemsPerSecond);
}

/// <summary>Reply to <c>list_jobs</c>: every job, oldest first (the core keeps the last 100 finished ones).</summary>
public sealed record JobsReply(IReadOnlyList<JobInfo> Jobs) : CoreReply;

/// <summary>Reply to <c>terminal_open</c>: the session and its byte pipe.</summary>
public sealed record TerminalOpenedReply(ulong SessionId, string Pipe, uint Pid) : CoreReply;

/// <summary>Whether a session's shell runs: <c>running</c>, or <c>exited</c> with its code.</summary>
public sealed record TerminalState(string Type, uint? Code = null)
{
    public const string Running = "running";
    public const string Exited = "exited";
}

/// <summary>One session, as <c>terminal_list</c> reports it.</summary>
public sealed record TerminalSessionInfo(
    ulong SessionId,
    string Profile,
    string Cwd,
    ushort Cols,
    ushort Rows,
    uint Pid,
    TerminalState State,
    string Pipe,
    bool Attached);

/// <summary>Reply to <c>terminal_list</c>, oldest first.</summary>
public sealed record TerminalSessionsReply(IReadOnlyList<TerminalSessionInfo> Sessions) : CoreReply;

/// <summary>One file or folder a search found.</summary>
public sealed record FileHit(string Path, string Kind, ulong? Frn = null)
{
    /// <summary>Whether it is a folder (a junction or a link to one included).</summary>
    [JsonIgnore]
    public bool IsFolder => Kind == "directory";
}

/// <summary>Reply to <c>search</c>: the hits, best first, and where they came from.</summary>
public sealed record FileSearchResultsReply(IReadOnlyList<FileHit> Hits, string Source, ulong TookUs, bool Complete) : CoreReply
{
    public const string FromIndex = "index";
    public const string FromWalk = "walk";
}

/// <summary>Where a volume's index is: <c>building</c>, <c>ready</c>, <c>rebuilding</c>, or <c>failed</c>.</summary>
public sealed record IndexState(string Type, string? Message = null);

/// <summary>One volume, as the indexer reports it.</summary>
public sealed record IndexVolumeStatus(string Letter, IndexState State, ulong Entries, ulong? BuiltInMs = null, ulong? JournalLag = null);

/// <summary>Reply to <c>index_status</c>.</summary>
public sealed record IndexStatusReply(bool Available, IReadOnlyList<IndexVolumeStatus> Volumes) : CoreReply;

/// <summary>One capability a plugin asks for, for the review dialog.</summary>
public sealed record CapabilityInfo(string Name, string Level, bool Granted, string Reason, IReadOnlyList<string>? Roots = null, IReadOnlyList<string>? Hosts = null);

/// <summary>
/// Where a plugin is: <c>loading</c>, <c>active</c>, <c>disabled</c>,
/// <c>needs_review</c> (with <see cref="Missing"/>), <c>failed</c> or
/// <c>crashed</c> (with <see cref="Message"/>; <see cref="AtMs"/> for a crash).
/// </summary>
public sealed record PluginState(string Type, IReadOnlyList<string>? Missing = null, string? Message = null, long? AtMs = null)
{
    public const string Loading = "loading";
    public const string Active = "active";
    public const string Disabled = "disabled";
    public const string NeedsReview = "needs_review";
    public const string Failed = "failed";
    public const string Crashed = "crashed";
}

/// <summary>One installed plugin.</summary>
public sealed record PluginInfo(
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    PluginState State,
    IReadOnlyList<CapabilityInfo> Capabilities,
    IReadOnlyList<string> Commands);

/// <summary>Reply to <c>list_plugins</c>.</summary>
public sealed record PluginsReply(IReadOnlyList<PluginInfo> Plugins) : CoreReply;

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

/// <summary>
/// What a plugin command asks the user for before it runs (protocol 13,
/// <c>list_commands</c>): the window shows a prompt with this title and
/// placeholder and passes the text as <c>input</c>.
/// </summary>
public sealed record CommandInput(string? Title = null, string? Placeholder = null);

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
    bool Immutable,
    CommandInput? Input = null);

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
    public const string AlreadyExists = "already_exists";
    public const string NoSuchListing = "no_such_listing";
    public const string NoSuchJob = "no_such_job";
    public const string NoSuchConflict = "no_such_conflict";
    public const string InvalidResolution = "invalid_resolution";
    public const string NoSuchPlugin = "no_such_plugin";
    public const string PluginError = "plugin_error";
    public const string NoSuchSession = "no_such_session";
    public const string UnknownProfile = "unknown_profile";
    public const string SpawnFailed = "spawn_failed";
    public const string UnknownCommand = "unknown_command";
    public const string NotImplemented = "not_implemented";
    public const string InvalidKeys = "invalid_keys";
    public const string KeybindingConflict = "keybinding_conflict";
    public const string ImmutableBinding = "immutable_binding";
    public const string ConfigError = "config_error";
    public const string Io = "io";
    public const string NoSuchTheme = "no_such_theme";
    public const string NoSuchExtension = "no_such_extension";
    public const string MarketplaceError = "marketplace_error";
    public const string HashMismatch = "hash_mismatch";
    public const string Incompatible = "incompatible";
}

/// <summary>A tint laid over the Mica backdrop: a <c>#RRGGBB</c> colour and how much of it covers the backdrop.</summary>
public sealed record MicaTint(string Tint, double Opacity);

/// <summary>
/// The colours of the window a theme sets (docs/themes.md, "The format").
/// A colour is <c>#RRGGBB</c> or <c>#RRGGBBAA</c>. The theme's own keys are
/// camelCase, unlike the protocol's.
/// </summary>
public sealed record ThemePalette(
    [property: JsonPropertyName("textPrimary")] string TextPrimary,
    [property: JsonPropertyName("textSecondary")] string TextSecondary,
    [property: JsonPropertyName("textTertiary")] string TextTertiary,
    [property: JsonPropertyName("textDisabled")] string TextDisabled,
    [property: JsonPropertyName("layerFill")] string LayerFill,
    [property: JsonPropertyName("layerStroke")] string LayerStroke,
    [property: JsonPropertyName("layerStrokeActive")] string LayerStrokeActive,
    [property: JsonPropertyName("controlFill")] string ControlFill,
    [property: JsonPropertyName("controlFillHover")] string ControlFillHover,
    [property: JsonPropertyName("acrylicTint")] string AcrylicTint,
    [property: JsonPropertyName("terminalBackground")] string TerminalBackground,
    [property: JsonPropertyName("folderIcon")] string FolderIcon,
    [property: JsonPropertyName("folderIconFront")] string FolderIconFront,
    [property: JsonPropertyName("fileTypeColors")] IReadOnlyDictionary<string, string> FileTypeColors,
    [property: JsonPropertyName("permissionLow")] string PermissionLow,
    [property: JsonPropertyName("permissionMedium")] string PermissionMedium,
    [property: JsonPropertyName("permissionHigh")] string PermissionHigh);

/// <summary>The terminal's colours: the default text, the scheme's background, the cursor, and the 16 ANSI colours.</summary>
public sealed record ThemeTerminal(string Foreground, string Background, string Cursor, IReadOnlyList<string> Ansi);

/// <summary>
/// Which elements of the window a theme shows (docs/themes.md, "Chrome").
/// Each one left out is off, as in the default theme. The theme's own keys
/// are camelCase, unlike the protocol's.
/// </summary>
public sealed record ThemeChrome(
    [property: JsonPropertyName("fkeyBar")] bool? FkeyBar = null,
    [property: JsonPropertyName("rowStripes")] bool? RowStripes = null,
    [property: JsonPropertyName("hairlines")] bool? Hairlines = null);

/// <summary>
/// A colour theme (docs/themes.md): what <c>get_theme</c> answers and
/// <c>theme_changed</c> carries. A null <see cref="Accent"/> follows the
/// Windows accent colour; a null <see cref="Mica"/> shows plain Mica.
/// <see cref="Metrics"/> (named sizes, camelCase as in the file) and
/// <see cref="Chrome"/> came with the theme format's version 2; null keeps
/// the default look's sizes and elements.
/// </summary>
public sealed record ColorTheme(
    string Id,
    string Name,
    string Author,
    string Version,
    string Kind,
    string? Accent,
    MicaTint? Mica,
    ThemePalette Palette,
    ThemeTerminal Terminal,
    string? Attribution = null,
    IReadOnlyDictionary<string, double>? Metrics = null,
    ThemeChrome? Chrome = null)
{
    public const string Dark = "dark";
    public const string Light = "light";

    /// <summary>Light or dark as Windows is set (protocol 11); the window decides which.</summary>
    public const string System = "system";

    /// <summary>Whether it is a theme for light mode. A <see cref="System"/> theme is not: the window asks Windows.</summary>
    [JsonIgnore]
    public bool IsLight => Kind == Light;

    /// <summary>Whether it follows Windows' light or dark mode (kind <see cref="System"/>).</summary>
    [JsonIgnore]
    public bool FollowsSystemMode => Kind == System;
}

/// <summary>
/// What the theme picker shows of a theme (<c>list_themes</c>): no palette.
/// <see cref="Mica"/> came with protocol 11; a core before it leaves it
/// out, and null also means plain Mica. <see cref="HasMetrics"/> says the
/// theme sets sizes (a density preset such as Commander Compact); a core
/// before the theme format's version 2 leaves it out.
/// </summary>
public sealed record ThemeInfo(string Id, string Name, string Author, string Version, string Kind, string? Accent = null, MicaTint? Mica = null,
    bool HasMetrics = false);

/// <summary>Reply to <c>list_themes</c>: every valid theme, by ID.</summary>
public sealed record ThemesReply(IReadOnlyList<ThemeInfo> Themes) : CoreReply;

/// <summary>Reply to <c>get_theme</c>.</summary>
public sealed record ThemeReply(ColorTheme Theme) : CoreReply;

/// <summary>An installed Tool Extension, as the core lists it: <c>tools\&lt;id&gt;\tool.json</c> and its folder.</summary>
public sealed record ToolInfo(string Id, string Name, string Version, string Author, string Description, string Dir);

/// <summary>Reply to <c>list_tools</c>: every installed tool, by ID.</summary>
public sealed record ToolsReply(IReadOnlyList<ToolInfo> Tools) : CoreReply;

/// <summary>What an extension of the marketplace is.</summary>
public static class ExtensionKinds
{
    public const string Plugin = "plugin";
    public const string Theme = "theme";
    public const string Tool = "tool";
}

/// <summary>Who publishes an extension. <see cref="Verified"/> is shown, not checked (docs/marketplace.md, trust rule 8).</summary>
public sealed record MarketAuthor(string Name, bool Verified = false, string? Url = null);

/// <summary>What users think of an extension: 0 to 5 stars, and how many ratings that is.</summary>
public sealed record MarketRating(double Average, ulong Count);

/// <summary>Where an extension's download is, and the SHA-256 it must have.</summary>
public sealed record MarketDownload(string Url, string Sha256);

/// <summary>A capability a plugin asks for, as the index lists it; the core adds the level.</summary>
public sealed record MarketCapability(string Name, string Reason, string? Level = null, IReadOnlyList<string>? Roots = null, IReadOnlyList<string>? Hosts = null);

/// <summary>
/// One extension as the core offers it (docs/marketplace.md, "The index"):
/// the newest version this core can run, one item per extension since
/// protocol 11. These are the index's own keys, camelCase like a theme's;
/// <see cref="InstalledVersion"/> is the core's addition, the version the
/// marketplace installed, absent when it installed none.
/// </summary>
public sealed record MarketItem(
    string Id,
    string Kind,
    string Name,
    MarketAuthor Author,
    string Version,
    string Description,
    ulong Size,
    MarketDownload Download,
    JsonElement Manifest,
    [property: JsonPropertyName("minCoreVersion")] string MinCoreVersion,
    string License,
    string Long = "",
    MarketRating? Rating = null,
    ulong? Installs = null,
    IReadOnlyList<MarketCapability>? Capabilities = null,
    [property: JsonPropertyName("installedVersion")] string? InstalledVersion = null);

/// <summary>
/// Reply to <c>marketplace_refresh</c> (the index's order) and
/// <c>marketplace_search</c> (best first): the items, the index's file or URL,
/// and when it was read or found unchanged (ms since 1970, UTC).
/// </summary>
public sealed record MarketplaceIndexReply(IReadOnlyList<MarketItem> Items, string Source, ulong FetchedAtMs) : CoreReply;
