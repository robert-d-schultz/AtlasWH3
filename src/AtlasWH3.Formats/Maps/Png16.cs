using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace AtlasWH3.Formats.Maps;

/// <summary>16-bit greyscale PNG with optional tEXt chunks (camera_heightmap.png carries "height_scale").</summary>
public static class Png16
{
    public static void Write(string path, Raster<ushort> raster, IReadOnlyDictionary<string, string>? text = null) =>
        File.WriteAllBytes(path, Encode(raster, text));

    public static byte[] Encode(Raster<ushort> raster, IReadOnlyDictionary<string, string>? text = null)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, raster.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), raster.Height);
        ihdr[8] = 16; // bit depth
        ihdr[9] = 0;  // greyscale
        Chunk(ms, "IHDR", ihdr);

        foreach (var (key, value) in text ?? new Dictionary<string, string>())
            Chunk(ms, "tEXt", Encoding.Latin1.GetBytes(key + "\0" + value));

        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                var row = new byte[1 + raster.Width * 2];
                for (var y = 0; y < raster.Height; y++)
                {
                    row[0] = 0; // no filter
                    for (var x = 0; x < raster.Width; x++)
                        BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(1 + x * 2), raster[x, y]);
                    z.Write(row);
                }
            }
            Chunk(ms, "IDAT", raw.ToArray());
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = new Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc.GetCurrentHashAsUInt32());
        s.Write(c);
    }
}
