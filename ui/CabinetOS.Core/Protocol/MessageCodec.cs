using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace CabinetOS.Core.Protocol;

/// <summary>
/// One message read from the pipe. <see cref="Body"/> is a <see cref="CoreReply"/>
/// or a <see cref="CoreEvent"/>, or null when the type is one this build does
/// not know (a newer core) or the fields did not parse.
/// </summary>
public sealed record IncomingMessage(string? Id, string? Type, object? Body, bool IsEvent, string? Error)
{
    /// <summary>The trace of the user action the message belongs to, when it carries one.</summary>
    public string? Trace { get; init; }
}

/// <summary>Turns requests into JSON and JSON into replies and events.</summary>
public static class MessageCodec
{
    /// <summary>Every event type of protocol version 14; everything else is a reply.</summary>
    public static readonly FrozenSet<string> EventTypes = FrozenSet.ToFrozenSet(
    [
        "measure_progress",
        "measure_finished",
        "listing_refreshed",
        "listing_lost",
        "config_changed",
        "config_error",
        "keymap_changed",
        "job_progress",
        "job_conflict",
        "job_state_changed",
        "plugin_state_changed",
        "plugin_crashed",
        "plugin_event",
        "terminal_exited",
        "volumes_changed",
        "theme_changed",
        "install_progress",
        "install_finished",
        "tools_changed",
        "preview_applied",
        "preview_cancelled",
        "update_state_changed",
        "update_progress",
    ]);

    private static readonly FrozenDictionary<string, JsonTypeInfo> Known = new Dictionary<string, JsonTypeInfo>
    {
        ["pong"] = ProtocolJson.Default.PongReply,
        ["ok"] = ProtocolJson.Default.OkReply,
        ["error"] = ProtocolJson.Default.ErrorReply,
        ["welcome"] = ProtocolJson.Default.WelcomeReply,
        ["listing_opened"] = ProtocolJson.Default.ListingOpenedReply,
        ["volume_info"] = ProtocolJson.Default.VolumeInfoReply,
        ["volumes"] = ProtocolJson.Default.VolumesReply,
        ["config"] = ProtocolJson.Default.ConfigReply,
        ["keymap"] = ProtocolJson.Default.KeymapReply,
        ["commands"] = ProtocolJson.Default.CommandsReply,
        ["search_results"] = ProtocolJson.Default.SearchResultsReply,
        ["command_routed"] = ProtocolJson.Default.CommandRoutedReply,
        ["command_result"] = ProtocolJson.Default.CommandResultReply,
        ["value"] = ProtocolJson.Default.ValueReply,
        ["job_started"] = ProtocolJson.Default.JobStartedReply,
        ["jobs"] = ProtocolJson.Default.JobsReply,
        ["terminal_opened"] = ProtocolJson.Default.TerminalOpenedReply,
        ["terminal_sessions"] = ProtocolJson.Default.TerminalSessionsReply,
        ["file_search_results"] = ProtocolJson.Default.FileSearchResultsReply,
        ["index_status"] = ProtocolJson.Default.IndexStatusReply,
        ["plugins"] = ProtocolJson.Default.PluginsReply,
        ["entry_details"] = ProtocolJson.Default.EntryDetailsReply,
        ["icon"] = ProtocolJson.Default.IconReply,
        ["themes"] = ProtocolJson.Default.ThemesReply,
        ["theme"] = ProtocolJson.Default.ThemeReply,
        ["tools"] = ProtocolJson.Default.ToolsReply,
        ["marketplace_index"] = ProtocolJson.Default.MarketplaceIndexReply,
        ["log_bundle"] = ProtocolJson.Default.LogBundleReply,
        ["workspace_info"] = ProtocolJson.Default.WorkspaceInfoReply,
        ["listing_refreshed"] = ProtocolJson.Default.ListingRefreshedEvent,
        ["listing_lost"] = ProtocolJson.Default.ListingLostEvent,
        ["config_changed"] = ProtocolJson.Default.ConfigChangedEvent,
        ["config_error"] = ProtocolJson.Default.ConfigErrorEvent,
        ["keymap_changed"] = ProtocolJson.Default.KeymapChangedEvent,
        ["job_progress"] = ProtocolJson.Default.JobProgressEvent,
        ["job_state_changed"] = ProtocolJson.Default.JobStateChangedEvent,
        ["job_conflict"] = ProtocolJson.Default.JobConflictEvent,
        ["plugin_state_changed"] = ProtocolJson.Default.PluginStateChangedEvent,
        ["plugin_crashed"] = ProtocolJson.Default.PluginCrashedEvent,
        ["plugin_event"] = ProtocolJson.Default.PluginEventEvent,
        ["preview_applied"] = ProtocolJson.Default.PreviewAppliedEvent,
        ["preview_cancelled"] = ProtocolJson.Default.PreviewCancelledEvent,
        ["preview_opened"] = ProtocolJson.Default.PreviewOpenedReply,
        ["jobs_started"] = ProtocolJson.Default.JobsStartedReply,
        ["terminal_exited"] = ProtocolJson.Default.TerminalExitedEvent,
        ["volumes_changed"] = ProtocolJson.Default.VolumesChangedEvent,
        ["theme_changed"] = ProtocolJson.Default.ThemeChangedEvent,
        ["install_progress"] = ProtocolJson.Default.InstallProgressEvent,
        ["install_finished"] = ProtocolJson.Default.InstallFinishedEvent,
        ["tools_changed"] = ProtocolJson.Default.ToolsChangedEvent,
        ["entry_matches"] = ProtocolJson.Default.EntryMatchesReply,
        ["measure_started"] = ProtocolJson.Default.MeasureStartedReply,
        ["measure_progress"] = ProtocolJson.Default.MeasureProgressEvent,
        ["measure_finished"] = ProtocolJson.Default.MeasureFinishedEvent,
        ["update_state"] = ProtocolJson.Default.UpdateStateReply,
        ["update_state_changed"] = ProtocolJson.Default.UpdateStateChangedEvent,
        ["update_progress"] = ProtocolJson.Default.UpdateProgressEvent,
    }.ToFrozenDictionary();

    /// <summary>The request as UTF-8 JSON, <c>id</c> and <c>type</c> first.</summary>
    public static byte[] Encode(CoreRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(request, request.GetType(), ProtocolJson.Default);

    /// <summary>
    /// Reads one frame. Unknown types are returned with a null body, never
    /// thrown. Throws <see cref="JsonException"/> only when the frame is not
    /// a JSON object at all.
    /// </summary>
    public static IncomingMessage Decode(ReadOnlySpan<byte> frame)
    {
        var (id, trace, type) = Peek(frame);
        var isEvent = type is not null && EventTypes.Contains(type);
        if (type is null || !Known.TryGetValue(type, out var info))
        {
            return new IncomingMessage(id, type, null, isEvent, null) { Trace = trace };
        }
        try
        {
            return new IncomingMessage(id, type, JsonSerializer.Deserialize(frame, info), isEvent, null) { Trace = trace };
        }
        catch (JsonException error)
        {
            return new IncomingMessage(id, type, null, isEvent, error.Message) { Trace = trace };
        }
    }

    /// <summary>
    /// The top-level <c>id</c> and <c>type</c>, wherever they stand in the
    /// object, without building a document.
    /// </summary>
    public static (string? Id, string? Type) PeekIdAndType(ReadOnlySpan<byte> frame)
    {
        var (id, _, type) = Peek(frame);
        return (id, type);
    }

    /// <summary>
    /// The top-level <c>id</c>, <c>trace</c> and <c>type</c>, wherever they
    /// stand in the object, without building a document.
    /// </summary>
    public static (string? Id, string? Trace, string? Type) Peek(ReadOnlySpan<byte> frame)
    {
        var reader = new Utf8JsonReader(frame);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("a message must be a JSON object");
        }
        string? id = null;
        string? trace = null;
        string? type = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isId = reader.ValueTextEquals("id"u8);
            var isTrace = !isId && reader.ValueTextEquals("trace"u8);
            var isType = !isId && !isTrace && reader.ValueTextEquals("type"u8);
            reader.Read();
            if (reader.TokenType != JsonTokenType.String)
            {
                reader.Skip();
            }
            else if (isId)
            {
                id = reader.GetString();
            }
            else if (isTrace)
            {
                trace = reader.GetString();
            }
            else if (isType)
            {
                type = reader.GetString();
            }
        }
        return (id, trace, type);
    }
}
