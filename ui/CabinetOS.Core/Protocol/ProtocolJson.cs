using System.Text.Json.Serialization;

namespace CabinetOS.Core.Protocol;

/// <summary>
/// Source-generated JSON metadata for every message the UI sends or reads:
/// snake_case names as on the wire, absent optional fields left out.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PingRequest))]
[JsonSerializable(typeof(ShutdownRequest))]
[JsonSerializable(typeof(HelloRequest))]
[JsonSerializable(typeof(ListDirectoryRequest))]
[JsonSerializable(typeof(CloseListingRequest))]
[JsonSerializable(typeof(VolumeInfoRequest))]
[JsonSerializable(typeof(ListVolumesRequest))]
[JsonSerializable(typeof(GetConfigRequest))]
[JsonSerializable(typeof(GetKeymapRequest))]
[JsonSerializable(typeof(ListCommandsRequest))]
[JsonSerializable(typeof(SearchCommandsRequest))]
[JsonSerializable(typeof(ExecuteCommandRequest))]
[JsonSerializable(typeof(SetKeybindingRequest))]
[JsonSerializable(typeof(ResetKeybindingRequest))]
[JsonSerializable(typeof(PongReply))]
[JsonSerializable(typeof(OkReply))]
[JsonSerializable(typeof(ErrorReply))]
[JsonSerializable(typeof(WelcomeReply))]
[JsonSerializable(typeof(ListingOpenedReply))]
[JsonSerializable(typeof(VolumeInfoReply))]
[JsonSerializable(typeof(VolumesReply))]
[JsonSerializable(typeof(ConfigReply))]
[JsonSerializable(typeof(KeymapReply))]
[JsonSerializable(typeof(CommandsReply))]
[JsonSerializable(typeof(SearchResultsReply))]
[JsonSerializable(typeof(CommandRoutedReply))]
[JsonSerializable(typeof(CommandResultReply))]
[JsonSerializable(typeof(ListingRefreshedEvent))]
[JsonSerializable(typeof(ListingLostEvent))]
[JsonSerializable(typeof(ConfigChangedEvent))]
[JsonSerializable(typeof(ConfigErrorEvent))]
[JsonSerializable(typeof(KeymapChangedEvent))]
[JsonSerializable(typeof(JobProgressEvent))]
[JsonSerializable(typeof(PluginStateChangedEvent))]
[JsonSerializable(typeof(PluginCrashedEvent))]
[JsonSerializable(typeof(TerminalExitedEvent))]
internal sealed partial class ProtocolJson : JsonSerializerContext;
