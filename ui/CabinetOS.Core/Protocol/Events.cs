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
public sealed record JobState(string Type, string? Message);

/// <summary>How far a job has come. Parsed only; the jobs UI comes later.</summary>
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

/// <summary>A plugin changed state; its commands may have changed with it.</summary>
public sealed record PluginStateChangedEvent(string PluginId, JsonElement State) : CoreEvent;

/// <summary>A plugin trapped; its commands are gone. Parsed only.</summary>
public sealed record PluginCrashedEvent(string PluginId, string Message) : CoreEvent;

/// <summary>A terminal session's shell exited. Parsed only; the terminal pane comes later.</summary>
public sealed record TerminalExitedEvent(ulong SessionId, uint ExitCode) : CoreEvent;
