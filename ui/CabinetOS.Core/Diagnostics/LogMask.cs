using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CabinetOS.Core.Diagnostics;

/// <summary>
/// What heavy mode writes of a payload: secrets masked as <c>"***"</c>, and at most
/// <see cref="PayloadCap"/> bytes per line, the rest cut and marked <c>truncated</c>
/// (docs/diagnostics.md, "Heavy mode"). The same rules as the core's
/// <c>cabinetos-diag</c> (<c>mask.rs</c>), so both processes hide the same things.
/// </summary>
public static class LogMask
{
    /// <summary>The most bytes of a request's or a reply's JSON one heavy line holds.</summary>
    public const int PayloadCap = 64 * 1024;

    /// <summary>What a masked value becomes.</summary>
    public const string Mask = "***";

    private static readonly HashSet<string> SecretFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "secret", "password", "passphrase", "api_key", "apikey", "token", "access_token",
        "refresh_token", "client_secret", "authorization", "x-api-key",
    };

    private static readonly HashSet<string> SecretHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "x-api-key", "cookie",
    };

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        // Log files are not embedded in HTML, so names in any script stay readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Masks the secrets in <paramref name="node"/>, in place: the <c>value</c> and
    /// <c>secret</c> of a message whose <c>type</c> is <c>secret</c> or starts with
    /// <c>secret_</c>; every field named like a secret (<c>secret</c>, <c>password</c>,
    /// <c>token</c>, <c>api_key</c>, <c>authorization</c>, …) at any depth; and the values of
    /// the headers <c>authorization</c>, <c>proxy-authorization</c>, <c>x-api-key</c> and
    /// <c>cookie</c> in a <c>headers</c> object, in a list of <c>[name, value]</c> pairs, or in
    /// <c>{"name": …, "value": …}</c> objects.
    /// </summary>
    public static void MaskSecrets(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                MaskObject(obj);
                break;
            case JsonArray items:
                foreach (var item in items)
                {
                    MaskSecrets(item);
                }
                break;
        }
    }

    /// <summary>
    /// The JSON text <paramref name="json"/> with its secrets masked, cut to at most
    /// <paramref name="cap"/> bytes. The flag says whether it was cut. Bytes that are not
    /// JSON are not written at all: a secret in them could not be found.
    /// </summary>
    public static (string Text, bool Truncated) MaskedJson(ReadOnlySpan<byte> json, int cap = PayloadCap)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return ($"(not JSON, {json.Length} bytes, not written)", false);
        }
        MaskSecrets(node);
        return CapText(node is null ? "null" : node.ToJsonString(Compact), cap);
    }

    /// <summary>
    /// Whether the environment variable <paramref name="name"/> holds a secret: a <c>CABINETOS_</c> variable
    /// whose name has <c>KEY</c>, <c>TOKEN</c>, <c>SECRET</c> or <c>PASSWORD</c> in it. Only a bundle
    /// writes the environment, and it masks these.
    /// </summary>
    public static bool IsSecretEnvironmentVariable(string name)
    {
        var upper = name.ToUpperInvariant();
        return upper.StartsWith("CABINETOS_", StringComparison.Ordinal)
            && (upper.Contains("KEY", StringComparison.Ordinal) || upper.Contains("TOKEN", StringComparison.Ordinal)
                || upper.Contains("SECRET", StringComparison.Ordinal) || upper.Contains("PASSWORD", StringComparison.Ordinal));
    }

    /// <summary>
    /// <paramref name="text"/> cut to at most <paramref name="cap"/> bytes of UTF-8, at a
    /// character boundary. The flag says whether it was cut.
    /// </summary>
    public static (string Text, bool Truncated) CapText(string text, int cap)
    {
        // A character is at most three UTF-16 bytes per UTF-8 byte, so short texts skip the encoder.
        if (text.Length <= cap / 3 || Encoding.UTF8.GetByteCount(text) <= cap)
        {
            return (text, false);
        }
        var bytes = Encoding.UTF8.GetBytes(text);
        var end = cap;
        while (end > 0 && (bytes[end] & 0xC0) == 0x80)
        {
            end--;
        }
        return (Encoding.UTF8.GetString(bytes, 0, end), true);
    }

    private static void MaskObject(JsonObject obj)
    {
        var secretMessage = obj["type"] is JsonValue kindNode && kindNode.TryGetValue<string>(out var kind)
            && (kind == "secret" || kind.StartsWith("secret_", StringComparison.Ordinal));
        var secretHeader = obj["name"] is JsonValue nameNode && nameNode.TryGetValue<string>(out var name) && SecretHeaders.Contains(name);
        // Values are replaced while walking, so the names are taken first.
        foreach (var key in obj.Select(pair => pair.Key).ToList())
        {
            if (SecretFields.Contains(key) || ((secretMessage || secretHeader) && key.Equals("value", StringComparison.OrdinalIgnoreCase)))
            {
                MaskValue(obj, key);
            }
            else if (key.Equals("headers", StringComparison.OrdinalIgnoreCase))
            {
                MaskHeaders(obj[key]);
            }
            else
            {
                MaskSecrets(obj[key]);
            }
        }
    }

    private static void MaskValue(JsonObject obj, string key)
    {
        if (obj[key] is not null)
        {
            obj[key] = JsonValue.Create(Mask);
        }
    }

    private static void MaskHeaders(JsonNode? headers)
    {
        switch (headers)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToList())
                {
                    if (SecretHeaders.Contains(key))
                    {
                        MaskValue(obj, key);
                    }
                    else
                    {
                        MaskSecrets(obj[key]);
                    }
                }
                break;
            case JsonArray items:
                foreach (var item in items)
                {
                    if (item is JsonArray { Count: 2 } pair && pair[0] is JsonValue first
                        && first.TryGetValue<string>(out var headerName) && SecretHeaders.Contains(headerName))
                    {
                        if (pair[1] is not null)
                        {
                            pair[1] = JsonValue.Create(Mask);
                        }
                    }
                    else
                    {
                        MaskSecrets(item);
                    }
                }
                break;
            default:
                MaskSecrets(headers);
                break;
        }
    }
}
