using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Maps;

/// <summary>
/// tile_list.bin (root and global_map\ copy), FASTBIN0 version 2/1 (WH3) or 1/1 (3K):
///   u32 path count, paths (u16 length + ASCII, e.g. "terrain\tiles\campaign\generic\generic_1x1\"), in first-use order
///   u32 climate count, climate names (u16 length + ASCII), in first-use order (WH3: "default" only)
///   6 x f32: WH3 (0, 0, 0, 0, 500, 1.333); 3K (0, 0, 1, 1, 500, 1.333)
///   11 x i32: WH3 (0, W, H, hexW, hexH, 0, 0, A, B, W + 2, H + 2) with W x H the tile map (2 px per hex) and A, B
///             (−24, −24) on IEE, (−16, −24) on Old World; 3K (0, W/4, H/4, hexW, hexH, 0, 0, A, B, W/4, H/4) on the lf grid
///   u8 1, u32 record count, 21-byte records; version 2 ends with one more byte (0).
/// Record low/high heights are world units in WH3, normalised 0..1 in 3K.
/// </summary>
public sealed class TileList
{
    /// <summary>One placed tile. X/Y are tile-map (quarter lf) pixels with y = 0 the south row.</summary>
    public struct Record
    {
        public ushort Version;     // always 1
        public uint Path;          // index into Paths
        public byte Climate;       // index into Climates
        public ushort X, Y;
        public byte Orientation;   // one-hot 0x10/0x20/0x40/0x80, sometimes | 0x04
        public byte Flag;          // 07 in the root file; cleared to 00 for base tiles in global_map\
        public float LowHeight, HighHeight;
    }

    public const int RecordSize = 21;

    public List<string> Paths { get; } = [];
    public List<string> Climates { get; } = [];
    public float[] Floats { get; set; } = [0, 0, 1, 1, 500, 1.333f];
    public int[] Ints { get; set; } = new int[11];
    public byte Marker { get; set; } = 1;
    public List<Record> Records { get; } = [];
    /// <summary>FASTBIN0 major version: 2 = WH3 (the default), 1 = 3K.</summary>
    public ushort Version { get; set; } = 2;
    /// <summary>Version 2's last byte.</summary>
    public byte Trailer { get; set; }

    public static TileList Read(string path) => Read(File.ReadAllBytes(path));

    public static TileList Read(ReadOnlySpan<byte> b)
    {
        if (!b[..8].SequenceEqual("FASTBIN0"u8)) throw new InvalidDataException("tile_list.bin: not FASTBIN0.");
        var version = U16(b, 8);
        if (version is not (1 or 2) || U16(b, 10) != 1) throw new InvalidDataException($"tile_list.bin: unsupported version {version}/{U16(b, 10)}.");
        var list = new TileList { Version = version };
        var o = 12;
        ReadStrings(b, ref o, list.Paths);
        ReadStrings(b, ref o, list.Climates);
        for (var i = 0; i < 6; i++, o += 4) list.Floats[i] = BinaryPrimitives.ReadSingleLittleEndian(b[o..]);
        for (var i = 0; i < 11; i++, o += 4) list.Ints[i] = BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
        list.Marker = b[o++];
        var count = (int)U32(b, o);
        o += 4;
        var trailer = version >= 2 ? 1 : 0;
        if (b.Length - o != count * RecordSize + trailer)
            throw new InvalidDataException($"tile_list.bin: {b.Length - o} record bytes for {count} records.");
        if (trailer > 0) list.Trailer = b[^1];
        list.Records.Capacity = count;
        for (var i = 0; i < count; i++, o += RecordSize)
            list.Records.Add(new Record
            {
                Version = U16(b, o),
                Path = U32(b, o + 2),
                Climate = b[o + 6],
                X = U16(b, o + 7),
                Y = U16(b, o + 9),
                Orientation = b[o + 11],
                Flag = b[o + 12],
                LowHeight = BinaryPrimitives.ReadSingleLittleEndian(b[(o + 13)..]),
                HighHeight = BinaryPrimitives.ReadSingleLittleEndian(b[(o + 17)..]),
            });
        return list;
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("FASTBIN0"u8);
        w.Write(Version);
        w.Write((ushort)1);
        WriteStrings(w, Paths);
        WriteStrings(w, Climates);
        foreach (var f in Floats) w.Write(f);
        foreach (var v in Ints) w.Write(v);
        w.Write(Marker);
        w.Write(Records.Count);
        foreach (var r in Records)
        {
            w.Write(r.Version);
            w.Write(r.Path);
            w.Write(r.Climate);
            w.Write(r.X);
            w.Write(r.Y);
            w.Write(r.Orientation);
            w.Write(r.Flag);
            w.Write(r.LowHeight);
            w.Write(r.HighHeight);
        }
        if (Version >= 2) w.Write(Trailer);
        w.Flush();
        return ms.ToArray();
    }

    public void Write(string path) => File.WriteAllBytes(path, ToBytes());

    /// <summary>
    /// WH3's global_map\tile_list.bin (BOB "Global Tilemap"): the records whose path <paramref name="keep"/> accepts, in
    /// root order and otherwise unchanged (flag 07 included); the path table keeps the paths still used, in their root
    /// order; header, climates and trailer as the root's. BOB keeps the tiles of exclude_from_global_mesh sets (roads,
    /// cliffs, coasts): byte-identical on IEE and Old World. (3K's copy kept every record and cleared the flag of base
    /// tiles.)
    /// </summary>
    public TileList GlobalMapSubset(Func<string, bool> keep)
    {
        var subset = new TileList { Floats = (float[])Floats.Clone(), Ints = (int[])Ints.Clone(), Marker = Marker, Version = Version, Trailer = Trailer };
        subset.Climates.AddRange(Climates);
        var kept = Paths.Select(keep).ToArray();
        var used = new bool[Paths.Count];
        foreach (var r in Records) if (kept[r.Path]) used[r.Path] = true;
        var remap = new uint[Paths.Count];
        for (var i = 0; i < Paths.Count; i++)
            if (used[i])
            {
                remap[i] = (uint)subset.Paths.Count;
                subset.Paths.Add(Paths[i]);
            }
        foreach (var r in Records)
            if (kept[r.Path]) subset.Records.Add(r with { Path = remap[r.Path] });
        return subset;
    }

    /// <summary>Tile category (the folder after "campaign\") is a 3K base tile (generic, generic_sea, sea_coast,
    /// mountains_*), the ones whose flag 3K's global_map copy cleared.</summary>
    public static bool IsBaseTile(string path)
    {
        var parts = path.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.FindIndex(parts, p => p.Equals("campaign", StringComparison.OrdinalIgnoreCase));
        var category = i >= 0 && i + 1 < parts.Length ? parts[i + 1].ToLowerInvariant() : "";
        return category is "generic" or "generic_sea" or "sea_coast" || category.StartsWith("mountains", StringComparison.Ordinal);
    }

    private static void ReadStrings(ReadOnlySpan<byte> b, ref int o, List<string> into)
    {
        var n = (int)U32(b, o);
        o += 4;
        for (var i = 0; i < n; i++)
        {
            var len = U16(b, o);
            into.Add(Encoding.ASCII.GetString(b.Slice(o + 2, len)));
            o += 2 + len;
        }
    }

    private static void WriteStrings(BinaryWriter w, List<string> strings)
    {
        w.Write(strings.Count);
        foreach (var s in strings)
        {
            w.Write((ushort)s.Length);
            w.Write(Encoding.ASCII.GetBytes(s));
        }
    }

    private static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
}
