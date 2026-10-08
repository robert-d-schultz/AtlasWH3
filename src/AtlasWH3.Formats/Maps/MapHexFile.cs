using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Maps;

/// <summary>
/// A campaign map.hex (raw_data\EmpireDesignData\campaign_maps\&lt;map&gt;\map.hex, written by CAIME). Layout, checked
/// on WH3's v20 files (IEE, Old World) and 3K's v19:
///  - u32 major, u32 minor (0x13 = 3K, 0x14 = WH3), 8-byte timestamp when minor ≥ 0x12;
///  - game and map name (u32 length + bytes);
///  - string lists (u32 count, then strings): land regions, sea regions, land ground types, sea ground types,
///    climates (40 settlement climates in WH3), attritions, and when minor is 0x13 or 0x14 the areas of interest;
///  - minor 0x12 / 0x14: 3 × u32 (1, 0, 0 in WH3);
///  - land and sea colour pools (u32 count + 4 bytes each);
///  - u32 width, u32 height, width × height records (16 bytes; 8 for minor 0x0D), row 0 = south;
///  - minor 0x12 / 0x14: u32 (1), u32 byte count, then one bit per hex;
///  - a 4-byte checksum.
/// Record byte 0 bits 3-7 and byte 1 hold region index + 1 into land regions followed by sea regions (0 = none).
/// Byte 2 bits 4-7 hold the settlement slot + 1 (0 = none; slot 0 is the region's main settlement). Byte 0 bits 0-1
/// terrain (0 land, 1 sea, 2 beach, 3 cliff), byte 2 bit 3 impassable, byte 3 bit 0 sprawl / bits 1-6 road edge mask /
/// bit 7 bridge, byte 4 bits 0-5 river edge mask, bytes 5-6 ground type + 1 (byte 5 bits 4-7, byte 6 bits 0-2).
/// </summary>
public sealed class MapHexFile
{
    public int Version { get; }
    public string Game { get; }
    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<string> LandRegions { get; }
    public IReadOnlyList<string> SeaRegions { get; }
    public IReadOnlyList<string> LandGroundTypes { get; }
    public IReadOnlyList<string> SeaGroundTypes { get; }
    public IReadOnlyList<string> Climates { get; }
    public IReadOnlyList<string> Attritions { get; }
    public IReadOnlyList<string> AreasOfInterest { get; }
    /// <summary>The trailing one-bit-per-hex array of minor 0x12 / 0x14 files (meaning not known), or null.</summary>
    public byte[]? HexBits { get; }

    private readonly int _recordSize;
    private readonly ushort[] _region;
    private readonly sbyte[] _slot;
    private readonly byte[] _records;

    private MapHexFile(int version, string game, string name, int w, int h, List<List<string>> lists, int recordSize, byte[] records, byte[]? bits)
    {
        Version = version;
        Game = game;
        Name = name;
        Width = w;
        Height = h;
        (LandRegions, SeaRegions, LandGroundTypes, SeaGroundTypes, Climates, Attritions, AreasOfInterest) =
            (lists[0], lists[1], lists[2], lists[3], lists[4], lists[5], lists[6]);
        _recordSize = recordSize;
        _records = records;
        HexBits = bits;
        _region = new ushort[w * h];
        _slot = new sbyte[w * h];
        for (int i = 0, o = 0; i < _region.Length; i++, o += recordSize)
        {
            _region[i] = (ushort)(records[o] >> 3 & 0x1f | records[o + 1] << 5);
            _slot[i] = (sbyte)((records[o + 2] >> 4) - 1);
        }
    }

    /// <summary>Region key of hex (col, row), or null when the hex has none.</summary>
    public string? RegionAt(int col, int row)
    {
        var i = _region[row * Width + col];
        if (i == 0) return null;
        i--;
        return i < LandRegions.Count ? LandRegions[i] : i - LandRegions.Count < SeaRegions.Count ? SeaRegions[i - LandRegions.Count] : null;
    }

    /// <summary>Region index of hex (col, row) into land regions followed by sea regions, or -1.</summary>
    public int RegionIndexAt(int col, int row) => _region[row * Width + col] - 1;

    /// <summary>Region key for an index from <see cref="RegionIndexAt"/>.</summary>
    public string? RegionName(int index) =>
        index < 0 ? null : index < LandRegions.Count ? LandRegions[index] : index - LandRegions.Count < SeaRegions.Count ? SeaRegions[index - LandRegions.Count] : null;

    /// <summary>Settlement slot of hex (col, row): 0 = the region's main settlement, -1 = none.</summary>
    public int SlotAt(int col, int row) => _slot[row * Width + col];

    /// <summary>Byte <paramref name="k"/> of hex (col, row)'s record (row 0 = south).</summary>
    public byte Byte(int col, int row, int k) => _records[(row * Width + col) * _recordSize + k];

    /// <summary>0 land, 1 sea, 2 beach, 3 cliff.</summary>
    public int TerrainAt(int col, int row) => Byte(col, row, 0) & 3;
    public bool ImpassableAt(int col, int row) => (Byte(col, row, 2) >> 3 & 1) != 0;
    /// <summary>Part of a settlement's sprawl (with the slot hexes, the town footprint).</summary>
    public bool SprawlAt(int col, int row) => (Byte(col, row, 3) & 1) != 0;
    /// <summary>6-bit mask of the hex edges a road leaves through.</summary>
    public int RoadAt(int col, int row) => Byte(col, row, 3) >> 1 & 63;
    public bool BridgeAt(int col, int row) => (Byte(col, row, 3) >> 7 & 1) != 0;
    /// <summary>6-bit mask of the hex edges a river runs along.</summary>
    public int RiverAt(int col, int row) => Byte(col, row, 4) & 63;
    /// <summary>The main settlement slot or its sprawl.</summary>
    public bool TownAt(int col, int row) => SlotAt(col, row) >= 0 || SprawlAt(col, row);
    /// <summary>Ground type index into land ground types followed by sea ground types, or -1.</summary>
    public int GroundTypeAt(int col, int row) => (Byte(col, row, 5) >> 4 | (Byte(col, row, 6) & 7) << 4) - 1;
    /// <summary>Hex (col, row)'s bit of <see cref="HexBits"/> (false when the file has none).</summary>
    public bool HexBitAt(int col, int row) => HexBits is { } b && (b[(row * Width + col) >> 3] >> ((row * Width + col) & 7) & 1) != 0;

    public static MapHexFile Read(string path)
    {
        try { return Read(File.ReadAllBytes(path)); }
        catch (InvalidDataException e) { throw new InvalidDataException($"{path}: {e.Message}", e); }
    }

    public static MapHexFile Read(byte[] b)
    {
        var o = 0;
        uint U32()
        {
            if (o + 4 > b.Length) throw new InvalidDataException($"truncated at offset {o}");
            var v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
            o += 4;
            return v;
        }
        string Str() { var n = (int)U32(); var s = Encoding.UTF8.GetString(b, o, n); o += n; return s; }
        List<string> List() { var n = (int)U32(); var list = new List<string>(n); for (var i = 0; i < n; i++) list.Add(Str()); return list; }

        U32();
        var minor = (int)U32();
        if (minor is not (0x0D or 0x12 or 0x13 or 0x14)) throw new NotSupportedException($"map.hex version 0x{minor:X} is not supported (0x0D, 0x12-0x14)");
        if (minor >= 0x12) o += 8;
        var game = Str();
        var name = Str();
        var lists = new List<List<string>>();
        for (var l = 0; l < 6; l++) lists.Add(List());
        lists.Add(minor is 0x13 or 0x14 ? List() : []);
        if (minor is 0x12 or 0x14) o += 12;
        for (var t = 0; t < 2; t++)
        {
            var colours = (int)U32();   // (not "o += 4 * U32()": that adds to o as it was before the read)
            o += 4 * colours;
        }
        int w = (int)U32(), h = (int)U32();
        var size = minor == 0x0D ? 8 : 16;
        if (o + (long)w * h * size > b.Length) throw new InvalidDataException($"{w}x{h} records run past the end of the file");
        var records = b.AsSpan(o, w * h * size).ToArray();
        o += w * h * size;
        byte[]? bits = null;
        if (minor is 0x12 or 0x14)
        {
            U32();
            var n = (int)U32();
            if (o + n > b.Length) throw new InvalidDataException("the per-hex bit array runs past the end of the file");
            bits = b.AsSpan(o, n).ToArray();
        }
        return new MapHexFile(minor, game, name, w, h, lists, size, records, bits);
    }
}
