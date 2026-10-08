using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Maps;

/// <summary>
/// The parts of a campaign map.hex (raw_data\EmpireDesignData\campaign_maps\&lt;map&gt;\map.hex) the build needs:
/// grid size, the land and sea region lists, and each hex's region. Layout (research/hexmap.py): 4 × u32,
/// game and map strings (u32 length + bytes), 7 string lists (land regions, sea regions, ground types, water types,
/// climates, attrition, areas of interest), 2 tables (u32 count + 4 bytes each), u32 width, u32 height, then
/// width × height 16-byte records, row 0 = south. Record byte 0 bits 3-7 and byte 1 hold region index + 1 into
/// land regions followed by sea regions (0 = none). Byte 2 bits 4-7 hold the settlement slot + 1 (0 = none; slot 0 is
/// the region's main settlement, as research/guandu/hexfields.py and CAIME read it). The other per-hex fields follow
/// research/guandu/rebuild_hex.py unpack: byte 0 bits 0-1 terrain (0 land, 1 sea, 2 beach, 3 cliff), byte 2 bit 3
/// impassable, byte 3 bit 0 sprawl / bits 1-6 road edge mask / bit 7 bridge, byte 4 bits 0-5 river edge mask.
/// </summary>
public sealed class MapHexFile
{
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<string> LandRegions { get; }
    public IReadOnlyList<string> SeaRegions { get; }
    private readonly ushort[] _region;
    private readonly sbyte[] _slot;
    private readonly byte[] _records;

    private MapHexFile(int w, int h, List<string> land, List<string> sea, ushort[] region, sbyte[] slot, byte[] records)
    {
        _slot = slot;
        _records = records;
        Width = w;
        Height = h;
        LandRegions = land;
        SeaRegions = sea;
        _region = region;
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

    private byte Byte(int col, int row, int k) => _records[(row * Width + col) * 16 + k];

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

    public static MapHexFile Read(string path)
    {
        var b = File.ReadAllBytes(path);
        var o = 16;
        uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)); o += 4; return v; }
        string Str() { var n = (int)U32(); var s = Encoding.UTF8.GetString(b, o, n); o += n; return s; }
        Str();
        Str();
        var lists = new List<List<string>>();
        for (var l = 0; l < 7; l++)
        {
            var n = (int)U32();
            var list = new List<string>(n);
            for (var i = 0; i < n; i++) list.Add(Str());
            lists.Add(list);
        }
        for (var t = 0; t < 2; t++)
        {
            var n = (int)U32();
            o += 4 * n;
        }
        int w = (int)U32(), h = (int)U32();
        if (o + (long)w * h * 16 > b.Length) throw new InvalidDataException($"{path}: {w}x{h} records run past the end of the file");
        var region = new ushort[w * h];
        var slot = new sbyte[w * h];
        var records = b.AsSpan(o, w * h * 16).ToArray();
        for (var i = 0; i < region.Length; i++, o += 16)
        {
            region[i] = (ushort)(b[o] >> 3 & 0x1f | b[o + 1] << 5);
            slot[i] = (sbyte)((b[o + 2] >> 4) - 1);
        }
        return new MapHexFile(w, h, lists[0], lists[1], region, slot, records);
    }
}
