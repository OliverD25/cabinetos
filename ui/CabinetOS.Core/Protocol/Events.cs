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
    ulong ElapsedMs) : CoreEvent;

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

/// <summary>A plugin trapped; its commands are gone.</summary>
public sealed record PluginCrashedEvent(string PluginId, string Message) : CoreEvent;

/// <summary>A drive letter came or went; the list is what <c>list_volumes</c> would answer now.</summary>
public sealed record VolumesChangedEvent(IReadOnlyList<VolumeDetails> Volumes) : CoreEvent;

/// <summary>A terminal session's shell exited; the session stays listed until closed.</summary>
public sealed record TerminalExitedEvent(ulong SessionId, uint ExitCode) : CoreEvent;
