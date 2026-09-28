using System.Security.Cryptography;

namespace CabinetOS.Core.Diagnostics;

/// <summary>
/// Request IDs: ULIDs, 128-bit values that sort by creation time, written as
/// 26 Crockford base32 characters (docs/ipc.md, docs/diagnostics.md). The UI
/// creates one per request, so an action can be followed from the UI's log
/// into the core's (Article 12).
/// </summary>
public static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>A new ULID for the current time.</summary>
    public static string NewId() => NewId(DateTimeOffset.UtcNow);

    /// <summary>A new ULID for <paramref name="time"/>, with 80 random bits.</summary>
    public static string NewId(DateTimeOffset time)
    {
        Span<byte> bytes = stackalloc byte[16];
        var ms = (ulong)time.ToUnixTimeMilliseconds() & 0xFFFF_FFFF_FFFF;
        for (var i = 0; i < 6; i++)
        {
            bytes[i] = (byte)(ms >> (40 - (8 * i)));
        }
        RandomNumberGenerator.Fill(bytes[6..]);
        return Encode(bytes);
    }

    /// <summary>Writes 16 bytes (big-endian) as 26 base32 characters.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 16)
        {
            throw new ArgumentException("a ULID has 16 bytes", nameof(bytes));
        }
        UInt128 value = 0;
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
        }
        return string.Create(26, value, static (chars, v) =>
        {
            // 26 characters hold 130 bits, so the first carries only the top 3.
            for (var i = 0; i < 26; i++)
            {
                chars[i] = Alphabet[(int)((v >> (125 - (5 * i))) & 31)];
            }
        });
    }

    /// <summary>
    /// Whether <paramref name="text"/> is a ULID the core accepts: 26 base32
    /// characters in either case, the first one 0–7.
    /// </summary>
    public static bool IsValid(string? text)
    {
        if (text is null || text.Length != 26 || text[0] < '0' || text[0] > '7')
        {
            return false;
        }
        foreach (var c in text)
        {
            if (Alphabet.IndexOf(char.ToUpperInvariant(c)) < 0)
            {
                return false;
            }
        }
        return true;
    }
}
