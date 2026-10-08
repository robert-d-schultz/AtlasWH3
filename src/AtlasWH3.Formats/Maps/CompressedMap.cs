using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Maps;

/// <summary>
/// CA's FASTBIN0 "TABLE_INDEXED" 16-bit raster (lf_height_map.compressed_map, lf_sea_height_map.compressed_map,
/// climate_map.cm, height_patches/*.compressed_map, global_meshes/land_mesh_N.compressed_map).
///
/// Layout (little endian): "FASTBIN0", u16 version (3), u32 width, u32 height, u32 tileW, u32 tileH,
/// 6 x f32 header, u16 13 + "TABLE_INDEXED", u32 n + u32 offsets[n], u32 n + u16 sizes[n], u32 dataLength, tiles.
/// Tiles are row-major (row 0 = north); edge tiles are full size, padded by repeating the edge pixels.
/// Tile encodings (first byte = mode):
///   palette     mode m &lt; 128: (m+1) u16 values in first-appearance order, then indices of ceil(log2(m+1)) bits
///   base+delta  mode 128+k: u16 minimum, then (k+1)-bit deltas
///   raw         mode 143, 256 u16 values (size 513)
/// All bit streams are packed LSB first.
///
/// <see cref="Encode"/> reproduces BOB's output byte for byte (verified on every vanilla 3k_dlc07 file). The
/// choice rule: one value is a constant palette; otherwise palette (only if &lt;= 127 values) or base+delta (only
/// if &lt;= 13 bits), whichever needs fewer bits per pixel, base+delta winning ties; raw otherwise.
/// </summary>
public static class CompressedMap
{
    private const string Magic = "FASTBIN0";
    private const string TableName = "TABLE_INDEXED";
    private const int RawMode = 143;

    public sealed record Map(Raster<ushort> Raster, float[] Header, ushort Version, int TileWidth, int TileHeight);

    public static Map Read(string path) => Decode(File.ReadAllBytes(path));

    public static Map Decode(ReadOnlySpan<byte> b)
    {
        if (Encoding.ASCII.GetString(b[..8]) != Magic)
            throw new InvalidDataException("Not a FASTBIN0 file.");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(b[8..]);
        var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[10..]);
        var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[14..]);
        var tw = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[18..]);
        var th = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[22..]);
        var header = new float[6];
        int pos;
        if (version >= 3)
        {
            for (var i = 0; i < 6; i++)
                header[i] = BinaryPrimitives.ReadSingleLittleEndian(b[(26 + 4 * i)..]);
            pos = 50;
        }
        else
        {
            // version 2 (3 river / roads_tracks junction hf maps in terrain2.pack): only lo and hi; kept at the
            // version-3 positions (header[1] = lo, header[4] = hi) so readers treat both alike
            header[1] = BinaryPrimitives.ReadSingleLittleEndian(b[26..]);
            header[4] = BinaryPrimitives.ReadSingleLittleEndian(b[30..]);
            pos = 34;
        }
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(b[pos..]);
        if (Encoding.ASCII.GetString(b.Slice(pos + 2, nameLength)) != TableName)
            throw new InvalidDataException("Only TABLE_INDEXED compressed maps are supported.");
        pos += 2 + nameLength;

        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[pos..]);
        var offsets = new int[count];
        for (var i = 0; i < count; i++)
            offsets[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[(pos + 4 + 4 * i)..]);
        pos += 4 + 4 * count;
        var sizeCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[pos..]);
        var sizes = new int[sizeCount];
        for (var i = 0; i < sizeCount; i++)
            sizes[i] = BinaryPrimitives.ReadUInt16LittleEndian(b[(pos + 4 + 2 * i)..]);
        pos += 4 + 2 * sizeCount;
        pos += 4; // data length
        var data = b[pos..];

        var cols = (width + tw - 1) / tw;
        var raster = new Raster<ushort>(width, height);
        var tile = new ushort[tw * th];
        for (var t = 0; t < count; t++)
        {
            DecodeTile(data.Slice(offsets[t], sizes[t]), tile);
            var (row, col) = Math.DivRem(t, cols);
            for (var y = 0; y < th; y++)
            {
                var ry = row * th + y;
                if (ry >= height) break;
                for (var x = 0; x < tw; x++)
                {
                    var rx = col * tw + x;
                    if (rx < width) raster[rx, ry] = tile[y * tw + x];
                }
            }
        }
        return new Map(raster, header, version, tw, th);
    }

    public static void Write(string path, Raster<ushort> raster, float[] header, ushort version = 3, int tileSize = 16) =>
        File.WriteAllBytes(path, Encode(raster, header, version, tileSize));

    public static byte[] Encode(Raster<ushort> raster, float[] header, ushort version = 3, int tileSize = 16)
    {
        if (header.Length != 6) throw new ArgumentException("header needs 6 floats", nameof(header));
        var cols = (raster.Width + tileSize - 1) / tileSize;
        var rows = (raster.Height + tileSize - 1) / tileSize;
        var tiles = new byte[cols * rows][];
        var samples = new ushort[tileSize * tileSize];
        for (var row = 0; row < rows; row++)
        for (var col = 0; col < cols; col++)
        {
            for (var y = 0; y < tileSize; y++)
            for (var x = 0; x < tileSize; x++)
                samples[y * tileSize + x] = raster.GetClamped(col * tileSize + x, row * tileSize + y);
            tiles[row * cols + col] = EncodeTile(samples);
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes(Magic));
        w.Write(version);
        w.Write((uint)raster.Width);
        w.Write((uint)raster.Height);
        w.Write((uint)tileSize);
        w.Write((uint)tileSize);
        foreach (var f in header) w.Write(f);
        w.Write((ushort)TableName.Length);
        w.Write(Encoding.ASCII.GetBytes(TableName));
        w.Write((uint)tiles.Length);
        uint offset = 0;
        foreach (var t in tiles) { w.Write(offset); offset += (uint)t.Length; }
        w.Write((uint)tiles.Length);
        foreach (var t in tiles) w.Write((ushort)t.Length);
        w.Write(offset);
        foreach (var t in tiles) w.Write(t);
        w.Flush();
        return ms.ToArray();
    }

    private static void DecodeTile(ReadOnlySpan<byte> d, ushort[] tile)
    {
        var n = tile.Length;
        if (d.Length == 1 + 2 * n)
        {
            for (var i = 0; i < n; i++) tile[i] = BinaryPrimitives.ReadUInt16LittleEndian(d[(1 + 2 * i)..]);
            return;
        }
        int mode = d[0];
        if (mode >= 128)
        {
            var bits = mode - 127;
            var baseValue = BinaryPrimitives.ReadUInt16LittleEndian(d[1..]);
            var reader = new BitReader(d[3..]);
            for (var i = 0; i < n; i++) tile[i] = (ushort)(baseValue + reader.Read(bits));
            return;
        }
        var count = mode + 1;
        var paletteBits = BitsFor(count);
        var palette = new ushort[count];
        for (var i = 0; i < count; i++) palette[i] = BinaryPrimitives.ReadUInt16LittleEndian(d[(1 + 2 * i)..]);
        if (paletteBits == 0) { Array.Fill(tile, palette[0]); return; }
        var idx = new BitReader(d[(1 + 2 * count)..]);
        for (var i = 0; i < n; i++) tile[i] = palette[idx.Read(paletteBits)];
    }

    private static byte[] EncodeTile(ushort[] t)
    {
        // palette in first-appearance order
        var palette = new List<ushort>();
        var index = new Dictionary<ushort, int>();
        ushort min = ushort.MaxValue, max = 0;
        foreach (var v in t)
        {
            if (!index.ContainsKey(v)) { index[v] = palette.Count; palette.Add(v); }
            if (v < min) min = v;
            if (v > max) max = v;
        }
        if (palette.Count == 1)
            return [0, (byte)palette[0], (byte)(palette[0] >> 8)];

        var paletteBits = BitsFor(palette.Count);
        var deltaBits = BitsFor(max - min + 1);
        var usePalette = palette.Count <= 127;
        var useDelta = deltaBits <= 13;
        if (usePalette && useDelta)
        {
            if (deltaBits <= paletteBits) usePalette = false; else useDelta = false;
        }

        if (useDelta)
        {
            var bw = new BitWriter(3 + (t.Length * deltaBits + 7) / 8);
            bw.WriteByte((byte)(127 + deltaBits));
            bw.WriteUInt16(min);
            foreach (var v in t) bw.Write(v - min, deltaBits);
            return bw.ToArray();
        }
        if (usePalette)
        {
            var bw = new BitWriter(1 + 2 * palette.Count + (t.Length * paletteBits + 7) / 8);
            bw.WriteByte((byte)(palette.Count - 1));
            foreach (var p in palette) bw.WriteUInt16(p);
            foreach (var v in t) bw.Write(index[v], paletteBits);
            return bw.ToArray();
        }
        var raw = new byte[1 + 2 * t.Length];
        raw[0] = RawMode;
        for (var i = 0; i < t.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(1 + 2 * i), t[i]);
        return raw;
    }

    /// <summary>Bits needed to index <paramref name="count"/> distinct values (0 for one value).</summary>
    private static int BitsFor(int count)
    {
        var bits = 0;
        while ((1 << bits) < count) bits++;
        return bits;
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private long _bit;

        public int Read(int bits)
        {
            var value = 0;
            for (var i = 0; i < bits; i++, _bit++)
                value |= ((_data[(int)(_bit >> 3)] >> (int)(_bit & 7)) & 1) << i;
            return value;
        }
    }

    private sealed class BitWriter(int capacity)
    {
        private readonly byte[] _buffer = new byte[capacity];
        private long _bit;

        public void WriteByte(byte value) => Write(value, 8);
        public void WriteUInt16(ushort value) => Write(value, 16);

        public void Write(int value, int bits)
        {
            for (var i = 0; i < bits; i++, _bit++)
                if (((value >> i) & 1) != 0) _buffer[_bit >> 3] |= (byte)(1 << (int)(_bit & 7));
        }

        public byte[] ToArray() => _buffer;
    }
}
