using Json.Schema;

namespace CabinetOS.Tests.Support;

/// <summary>
/// The checked-in schemas, each loaded once: JsonSchema.Net registers a
/// schema by its <c>$id</c> and refuses to register the same one twice.
/// </summary>
internal static class Schemas
{
    private static readonly Lazy<JsonSchema> RequestSchema = new(() => JsonSchema.FromFile(Repo.ProtocolSchema("request.schema.json")));
    private static readonly Lazy<JsonSchema> ResponseSchema = new(() => JsonSchema.FromFile(Repo.ProtocolSchema("response.schema.json")));
    private static readonly Lazy<JsonSchema> EventSchema = new(() => JsonSchema.FromFile(Repo.ProtocolSchema("event.schema.json")));
    private static readonly Lazy<JsonSchema> ConfigSchema = new(() => JsonSchema.FromFile(Repo.ConfigSchema));
    private static readonly Lazy<JsonSchema> ToolSchema = new(() => JsonSchema.FromFile(Path.Combine(Repo.Tools, "tool.schema.json")));

    private static readonly Lazy<JsonSchema> QuickViewMessagesSchema = new(() => JsonSchema.FromFile(Path.Combine(Repo.Tools, "quickview-messages.schema.json")));

    /// <summary><c>sdk/tools/tool.schema.json</c>.</summary>
    public static JsonSchema Tool => ToolSchema.Value;

    /// <summary><c>sdk/tools/quickview-messages.schema.json</c>, the one description of the Quick View page messages.</summary>
    public static JsonSchema QuickViewMessages => QuickViewMessagesSchema.Value;

    /// <summary><c>sdk/protocol/request.schema.json</c>.</summary>
    public static JsonSchema Request => RequestSchema.Value;

    /// <summary><c>sdk/protocol/response.schema.json</c>.</summary>
    public static JsonSchema Response => ResponseSchema.Value;

    /// <summary><c>sdk/protocol/event.schema.json</c>.</summary>
    public static JsonSchema Event => EventSchema.Value;

    /// <summary><c>sdk/config/cabinetos.schema.json</c>.</summary>
    public static JsonSchema Config => ConfigSchema.Value;
}
