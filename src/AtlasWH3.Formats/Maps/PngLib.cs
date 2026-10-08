using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace AtlasWH3.Formats.Maps;

/// <summary>
/// 16-bit greyscale PNG written as BOB's IMAGE_LOADER (libpng) writes camera_heightmap.png: IHDR, tEXt chunks, IDAT
/// split in 8192-byte chunks, IEND. Each row takes libpng's adaptive filter (the lowest sum of |signed byte| over
/// none, sub, up, average, paeth; ties to the earlier filter); the stream is zlib level 6 with Z_FILTERED
/// (<see cref="ZlibDeflate"/>). Checked byte for byte against BOB's vanilla camera_heightmap.png.
/// </summary>
public static class PngLib
{
    public static byte[] Encode16(Raster<ushort> raster, IReadOnlyDictionary<string, string>? text = null)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, raster.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), raster.Height);
        ihdr[8] = 16;
        ihdr[9] = 0;
        Chunk(ms, "IHDR", ihdr);
        foreach (var (key, value) in text ?? new Dictionary<string, string>())
            Chunk(ms, "tEXt", Encoding.Latin1.GetBytes(key + "\0" + value));

        var stride = raster.Width * 2;
        var filtered = new byte[(stride + 1) * raster.Height];
        var prev = new byte[stride];
        var cur = new byte[stride];
        var trial = new byte[5][];
        for (var f = 0; f < 5; f++) trial[f] = new byte[stride];
        for (var y = 0; y < raster.Height; y++)
        {
            for (var x = 0; x < raster.Width; x++) BinaryPrimitives.WriteUInt16BigEndian(cur.AsSpan(x * 2), raster[x, y]);
            var best = 0;
            var bestSum = long.MaxValue;
            for (var f = 0; f < 5; f++)
            {
                var sum = Filter(f, cur, prev, trial[f]);
                if (sum < bestSum) { bestSum = sum; best = f; }
            }
            var o = y * (stride + 1);
            filtered[o] = (byte)best;
            trial[best].CopyTo(filtered, o + 1);
            (prev, cur) = (cur, prev);
        }

        var z = ZlibDeflate.Compress(filtered, level: 6, filteredStrategy: true);
        for (var o = 0; o < z.Length; o += 8192)
            Chunk(ms, "IDAT", z.AsSpan(o, Math.Min(8192, z.Length - o)).ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>Filters a row (2 bytes per pixel) and returns libpng's heuristic sum.</summary>
    private static long Filter(int f, byte[] cur, byte[] prev, byte[] dst)
    {
        long sum = 0;
        for (var i = 0; i < cur.Length; i++)
        {
            int a = i >= 2 ? cur[i - 2] : 0, b = prev[i], c = i >= 2 ? prev[i - 2] : 0;
            var p = f switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) >> 1,
                _ => Paeth(a, b, c),
            };
            var v = (byte)(cur[i] - p);
            dst[i] = v;
            sum += v < 128 ? v : 256 - v;
        }
        return sum;
    }

    private static int Paeth(int a, int b, int c)
    {
        int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - 2 * c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
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
