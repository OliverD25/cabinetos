using System.Buffers.Binary;

namespace CabinetOS.Tests.Support;

/// <summary>
/// Writes a real baseline TIFF (uncompressed RGB, one strip, a colour gradient) of a given size, for the tests that need
/// a picture Windows' codecs can draw and a browser cannot decode (Quick View's <c>quickview-render</c> path): no image
/// library, only the TIFF header and one directory.
/// </summary>
internal static class TestTiff
{
    /// <summary>Writes a <paramref name="width"/> × <paramref name="height"/> RGB TIFF to <paramref name="path"/>.</summary>
    public static void Write(string path, int width, int height)
    {
        const int entries = 9;
        const int directory = 8;
        var bitsAt = directory + 2 + (entries * 12) + 4;
        var pixelsAt = bitsAt + 8;
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = ((y * width) + x) * 3;
                pixels[at] = (byte)(x * 255 / Math.Max(1, width - 1));
                pixels[at + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                pixels[at + 2] = 0x80;
            }
        }
        var bytes = new byte[pixelsAt + pixels.Length];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), directory);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(directory), entries);
        var entry = directory + 2;
        // tag, type (3 = SHORT, 4 = LONG), count, value (or the offset of the values)
        foreach (var (tag, type, count, value) in new (ushort, ushort, uint, uint)[]
        {
            (256, 4, 1, (uint)width),          // ImageWidth
            (257, 4, 1, (uint)height),         // ImageLength
            (258, 3, 3, (uint)bitsAt),         // BitsPerSample: 8, 8, 8
            (259, 3, 1, 1),                    // Compression: none
            (262, 3, 1, 2),                    // PhotometricInterpretation: RGB
            (273, 4, 1, (uint)pixelsAt),       // StripOffsets
            (277, 3, 1, 3),                    // SamplesPerPixel
            (278, 4, 1, (uint)height),         // RowsPerStrip
            (279, 4, 1, (uint)pixels.Length),  // StripByteCounts
        })
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(entry), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(entry + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 4), count);
            // A SHORT value sits in the first two bytes of the field; a LONG or an offset fills it.
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 8), value);
            entry += 12;
        }
        for (var i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bitsAt + (i * 2)), 8);
        }
        pixels.CopyTo(bytes, pixelsAt);
        File.WriteAllBytes(path, bytes);
    }
}