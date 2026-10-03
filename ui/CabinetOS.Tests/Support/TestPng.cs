using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace CabinetOS.Tests.Support;

/// <summary>
/// Writes a real PNG of a given size (a colour gradient), for the tests that need a picture the shell can make a
/// thumbnail of and a browser can decode: no image library, only zlib and the PNG chunk layout.
/// </summary>
internal static class TestPng
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Writes a <paramref name="width"/> × <paramref name="height"/> RGB PNG to <paramref name="path"/>.</summary>
    public static void Write(string path, int width, int height)
    {
        using var file = File.Create(path);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bits per channel
        header[9] = 2; // RGB
        Chunk(file, "IHDR", header);
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + (width * 3)];
            for (var y = 0; y < height; y++)
            {
                row[0] = 0;
                for (var x = 0; x < width; x++)
                {
                    row[1 + (x * 3)] = (byte)(x * 255 / Math.Max(1, width - 1));
                    row[2 + (x * 3)] = (byte)(y * 255 / Math.Max(1, height - 1));
                    row[3 + (x * 3)] = 0x80;
                }
                zlib.Write(row);
            }
        }
        Chunk(file, "IDAT", raw.ToArray());
        Chunk(file, "IEND", []);
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes.Concat(data))
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        var sum = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(sum, crc ^ 0xFFFFFFFFu);
        stream.Write(sum);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }
}
