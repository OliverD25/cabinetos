using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CabinetOS.Core.Protocol;

/// <summary>A message the core sends on its own; told from replies by <c>type</c>.</summary>
public abstract record CoreEvent;

/// <summary>A watched directory changed; a new listing is in a new section.</summary>
public sealed record ListingRefreshedEvent(
    ulong ListingId,
    ulong SectionHandle,
    ulong SectionSize,
    uint EntryCount,
    uint Generation,
    string Reason) : CoreEvent, ICarriesSection
{
    /// <inheritdoc/>
    [JsonIgnore]
    public SafeHandle? Section { get; set; }
}

/// <summary>A watched listing can no longer be kept current.</summary>
public sealed record ListingLostEvent(ulong ListingId, string Message) : CoreEvent;

/// <summary>The configuration changed; <see cref="Changed"/> lists dotted setting paths.</summary>
public sealed record ConfigChangedEvent(IReadOnlyList<string> Changed) : CoreEvent;

/// <summary>The configuration file has an error; the settings in effect stay.</summary>
public sealed record ConfigErrorEvent(uint? Line, uint? Column, string Message) : CoreEvent;

/// <summary>The compiled keymap changed.</summary>
public sealed record KeymapChangedEvent(KeymapData Keymap) : CoreEvent;

/// <summary>A job's state: <c>running</c>, <c>failed</c> with a message, …</summary>
public sealed record JobState(string Type, string? Message = null)
{
    public const string Queued = "queued";
    public const string Scanning = "scanning";
    public const string Running = "running";
    public const string Paused = "paused";
    public const string Completed = "completed";
    public const string CompletedWithErrors = "completed_with_errors";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";

    /// <summary>Whether no more events follow for the job.</summary>
    [JsonIgnore]
    public bool IsFinal => Type is Completed or CompletedWithErrors or Cancelled or Failed;
}

/// <summary>How far a job has come; at most 30 per second per job, and only when something changed.</summary>
public sealed record JobProgressEvent(
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
    double? ItemsPerSecond = null) : CoreEvent;

/// <summary>A job changed state; after a final state no more events follow for it.</summary>
public sealed record JobStateChangedEvent(ulong JobId, JobState State) : CoreEvent;

/// <summary>
/// What stopped a file (docs/jobs.md, "Conflicts"). Times are FILETIME ticks,
/// as in the listing section; each field belongs to the kinds that carry it.
/// </summary>
public sealed record ConflictKind(
    string Type,
    ulong? SourceSize = null,
    long? SourceModified = null,
    ulong? DestSize = null,
    long? DestModified = null,
    ulong? Size = null,
    uint? Code = null,
    string? Message = null)
{
    public const string FileExists = "file_exists";
    public const string AccessDenied = "access_denied";
    public const string SharingViolation = "sharing_violation";
    public const string PathTooLong = "path_too_long";
    public const string DiskFull = "disk_full";
    public const string SourceVanished = "source_vanished";
    public const string RecycleBinTooSmall = "recycle_bin_too_small";
    public const string Io = "io";
}

/// <summary>A file of a job waits for a decision (<c>resolve_conflict</c>); the rest of the job goes on.</summary>
public sealed record JobConflictEvent(ulong ConflictId, ulong JobId, ConflictKind Kind, string Source, string? Destination) : CoreEvent;

/// <summary>A plugin changed state; its commands may have changed with it.</summary>
public sealed record PluginStateChangedEvent(string PluginId, PluginState State) : CoreEvent;

/// <summary>
/// What a plugin sent with <c>emit</c>. <see cref="Payload"/> is the text the
/// plugin wrote, usually JSON (the core does not read it); see
/// <c>PluginEvents</c> for reading it.
/// </summary>
public sealed record PluginEventEvent(string PluginId, string Name, string Payload) : CoreEvent;

/// <summary>A preview was applied: its rows run as these jobs.</summary>
public sealed record PreviewAppliedEvent(string Preview, IReadOnlyList<ulong> Jobs) : CoreEvent;

/// <summary>A preview was dropped: cancelled, or not applied within ten minutes.</summary>
public sealed record PreviewCancelledEvent(string Preview) : CoreEvent;

/// <summary>A plugin trapped; its commands are gone.</summary>
public sealed record PluginCrashedEvent(string PluginId, string Message) : CoreEvent;

/// <summary>A drive letter came or went; the list is what <c>list_volumes</c> would answer now.</summary>
public sealed record VolumesChangedEvent(IReadOnlyList<VolumeDetails> Volumes) : CoreEvent;

/// <summary>A terminal session's shell exited; the session stays listed until closed.</summary>
public sealed record TerminalExitedEvent(ulong SessionId, uint ExitCode) : CoreEvent;

/// <summary>A terminal session's mode changed (<c>terminal_set_mode</c> from any client): <c>locked</c> or <c>linked</c>.</summary>
public sealed record TerminalModeChangedEvent(ulong SessionId, string Mode) : CoreEvent;

/// <summary>
/// A terminal session's shell reported a new current folder: its prompt hook prints it at each prompt, and the
/// core sends this when it changed (a <c>cd</c> of the user's own, or a linked shell following its pane).
/// </summary>
public sealed record TerminalFolderChangedEvent(ulong SessionId, string Folder) : CoreEvent;

/// <summary>The theme in effect changed (<c>ui.theme</c>, or its file was saved); it comes whole.</summary>
public sealed record ThemeChangedEvent(ColorTheme Theme) : CoreEvent;

/// <summary>How far the download of an <c>install_extension</c> has come: at most 30 a second, and one at the end.</summary>
public sealed record InstallProgressEvent(string ExtensionId, ulong Bytes, ulong Total) : CoreEvent;

/// <summary>
/// An <c>install_extension</c> ended, whichever client asked: whether it is
/// installed now, and what happened. <see cref="InstalledVersion"/> (still
/// protocol 11, a later addition) is the version the marketplace's record
/// holds after it: the new one, the one from before a failed update, or null
/// when none is installed (or the core is older).
/// </summary>
public sealed record InstallFinishedEvent(string ExtensionId, bool Ok, string Message, string? InstalledVersion = null) : CoreEvent;

/// <summary>A Tool Extension was installed or removed; the list is what <c>list_tools</c> would answer now.</summary>
public sealed record ToolsChangedEvent(IReadOnlyList<ToolInfo> Tools) : CoreEvent;

/// <summary>How far a measure of one path has come (version 12): at most 30 a second.</summary>
public sealed record MeasureProgressEvent(ulong MeasureId, string Path, ulong Files, ulong Folders, ulong Bytes) : CoreEvent;

/// <summary>One path's total: <see cref="Unreadable"/> folders could not be read and are not in it.</summary>
public sealed record MeasureResult(string Path, ulong Files, ulong Folders, ulong Bytes, ulong Unreadable);

/// <summary>A measure ended (version 12): every path's total, or what it had when it was cancelled.</summary>
public sealed record MeasureFinishedEvent(ulong MeasureId, IReadOnlyList<MeasureResult> Results, bool Cancelled) : CoreEvent;

/// <summary>
/// The updater moved on (protocol 14): a check, a download, a swap or a snooze, the daily check's
/// included. The same fields as <c>update_state</c>.
/// </summary>
[JsonConverter(typeof(UpdateStateChangedEventConverter))]
public sealed record UpdateStateChangedEvent(UpdateStatus Status) : CoreEvent;

/// <summary>Reads <c>update_state_changed</c> as <see cref="UpdateStateChangedEvent"/>.</summary>
public sealed class UpdateStateChangedEventConverter : FlatUpdateStatusConverter<UpdateStateChangedEvent>
{
    private protected override UpdateStateChangedEvent Wrap(UpdateStatus status) => new(status);

    private protected override UpdateStatus Unwrap(UpdateStateChangedEvent message) => message.Status;
}

/// <summary>
/// How far an update's download has come (protocol 14): at most 4 a second, and one when it is complete.
/// <see cref="Total"/> is the zip's size as <c>latest.json</c> gives it; the speed is since the download began.
/// </summary>
public sealed record UpdateProgressEvent(string Version, ulong Bytes, ulong Total, ulong BytesPerSecond) : CoreEvent;
