using AtlasWH3.Core.Campaign.AiPathfinding;
using AtlasWH3.Formats.Esf;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign.Props;

/// <summary>
/// The region of a campaign object: the region of the hex under the entity's ECTransform position, from map_data.esf
/// as the map's mod pack ships it (the regions the game reads; BOB read map.hex, which agrees hex for hex when both come
/// from one CAIME export: IEE all 1,552,000 hexes; Old World's map.hex was edited after its last export, 12 hexes).
/// Port of
/// bob_terrain FUN_18005fe10 with the hex geometry of EMPIREUTILITY::HEX_MAP_DATA_FILE_DATA::load (float maths, as
/// BOB): flat-top hexes of radius R = (2/3)·(maxx − minx)/(columns − 1) over the map's bounds, odd columns half a
/// hex north. Checked on main190: matches BOB's region for every one of 66,268 objects (docs/native_campaign_build.md).
/// </summary>
public sealed class HexRegionLookup
{
    private readonly int[] _region;
    private readonly IReadOnlyList<string> _names;
    private readonly float _minX, _minY, _maxX, _r, _h, _inv2H, _inv15R;

    /// <param name="region">Region index per hex (row-major, row 0 = south), -1 for none.</param>
    /// <param name="names">Region keys by index: land regions, then sea regions.</param>
    public HexRegionLookup(int width, int height, int[] region, IReadOnlyList<string> names, float minX, float minY, float maxX,
                           float maxY, MapHexFile? hex = null)
    {
        Width = width;
        Height = height;
        _region = region;
        _names = names;
        Hex = hex;
        MapBounds = (minX, minY, maxX, maxY);
        _minX = minX;
        _minY = minY;
        _maxX = maxX;
        _r = 0.6666667f / (width - 1f) * (maxX - minX);
        _h = _r * 0.8660254f;
        _inv2H = 1f / (_h + _h);
        _inv15R = 1f / (_r * 1.5f);
    }

    /// <summary>Hex (col, row) holding world point (x, z).</summary>
    public (int Col, int Row) HexAt(float x, float z)
    {
        var fc = (x - _minX + _r) * _inv15R;
        var col = (int)fc;
        var fz = z - _minY;
        if ((col & 1) == 0) fz += _h;
        var row = (int)(fz * _inv2H);
        var t = (fc - col) * 3f;
        if (t < 1f)
        {
            // near the slanted edges: the point may belong to the column on the left
            var u = fz * _inv2H - row - 0.5f;
            var parity = col & 1;
            if (u + u < t)
            {
                if (!(u * -2f < t)) { row += parity - 1; col--; }
            }
            else { row += parity; col--; }
        }
        return (Math.Clamp(col, 0, Width - 1), Math.Clamp(row, 0, Height - 1));
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>The map bounds the lookup is over: map_data.esf's (<see cref="EsfHeaderBounds"/>).</summary>
    public (float MinX, float MinY, float MaxX, float MaxY) MapBounds { get; }
    /// <summary>The map.hex the lookup was made from (<see cref="ForMapHex"/>), else null.</summary>
    public MapHexFile? Hex { get; }

    /// <summary>Region index of hex (col, row), -1 for none.</summary>
    public int RegionIndexOfHex(int col, int row) => _region[row * Width + col];

    /// <summary>Region key of hex (col, row), or null.</summary>
    public string? RegionOfHex(int col, int row) => RegionName(RegionIndexOfHex(col, row));

    public string? RegionName(int index) => index >= 0 && index < _names.Count ? _names[index] : null;

    /// <summary>The rectangle BOB's bmd quadtree covers (bob_terrain FUN_180060e10 root): the map bounds' x range and the
    /// hex grid's z extent, (rows + 0.5) x row step from minY (main190: 0..986.051 x 0..873.7957; fitted to BOB's
    /// level-6 cells 2026-10-04).</summary>
    public (float X0, float Z0, float X1, float Z1) QuadRoot => (_minX, _minY, _maxX, _minY + (Height + 0.5f) * (_h + _h));

    /// <summary>World centre of hex (col, row) (the inverse of <see cref="HexAt"/>: columns 1.5 R apart, even
    /// columns half a hex south).</summary>
    public (float X, float Z) HexCentre(int col, int row) =>
        (_minX + col * 1.5f * _r, _minY + (row + 0.5f) * (_h + _h) - ((col & 1) == 0 ? _h : 0));

    /// <summary>Region index (land then sea, see <see cref="MapHexFile.RegionIndexAt"/>) at a world point, -1 none.</summary>
    public int RegionIndexAt(double x, double z)
    {
        var (c, r) = HexAt((float)x, (float)z);
        return RegionIndexOfHex(c, r);
    }

    public string? RegionAt(double x, double z) => RegionName(RegionIndexAt(x, z));

    /// <summary>The lookup for a map, from its map_data.esf as its mod pack ships it: the per-hex regions
    /// (MASKED_REGIONS_DATA) and the map bounds BOB uses, the two vec2 values at the file's head (CAIME writes them from
    /// the campaign_map_playable_areas row; Old World's row carries IEE's 1068.11 where its map_data.esf has 1367.396,
    /// and BOB and the game follow the ESF). Null, with the reason, when the file is missing.</summary>
    public static HexRegionLookup? ForMap(ProjectPaths paths, out string reason)
    {
        if (MapData(paths, out reason) is not var (data, b)) return null;
        var regions = new int[data.AreaMap.Length];
        for (var i = 0; i < regions.Length; i++)
            regions[i] = (data.AreaMap[i] & MapDataRegions.RegionMask) is var r && r < data.Regions.Count ? r : -1;
        return new HexRegionLookup(data.Width, data.Height, regions, data.Regions.Select(r => r.Key).ToList(), b.MinX, b.MinY, b.MaxX, b.MaxY);
    }

    /// <summary>The lookup from the kit's map.hex (the Scene editor's region overlay, which also needs its settlement
    /// slots), over map_data.esf's bounds.</summary>
    public static HexRegionLookup? ForMapHex(ProjectPaths paths, out string reason)
    {
        var hexPath = Path.Combine(paths.AkDesignCampaignMapDir, "map.hex");
        if (!File.Exists(hexPath)) { reason = $"no {hexPath}"; return null; }
        if (MapData(paths, out reason) is not var (_, b)) return null;
        var hex = MapHexFile.Read(hexPath);
        var regions = new int[hex.Width * hex.Height];
        for (var r = 0; r < hex.Height; r++)
            for (var c = 0; c < hex.Width; c++) regions[r * hex.Width + c] = hex.RegionIndexAt(c, r);
        return new HexRegionLookup(hex.Width, hex.Height, regions, [.. hex.LandRegions, .. hex.SeaRegions], b.MinX, b.MinY, b.MaxX, b.MaxY, hex);
    }

    private static (MapDataRegions Data, (float MinX, float MinY, float MaxX, float MaxY) Bounds)? MapData(ProjectPaths paths, out string reason)
    {
        if (GameSetup.CampaignMapFile(paths, paths.MapName, "map_data.esf") is not { } esf)
        {
            reason = GameSetup.CampaignMapFileMissing(paths, paths.MapName, "map_data.esf");
            return null;
        }
        if (EsfHeaderBounds(esf.Data) is not { } b) { reason = $"map_data.esf in {Path.GetFileName(esf.Pack)} has no map bounds"; return null; }
        reason = "";
        return (MapDataRegions.Read(EsfTree.Read(esf.Data)), b);
    }

    /// <summary>The first two vec2 values (ESF type 0x0c) of map_data.esf: (min x, min y), (max x, max y).</summary>
    public static (float MinX, float MinY, float MaxX, float MaxY)? EsfHeaderBounds(byte[] b)
    {
        var n = Math.Min(b.Length, 4096);
        for (var i = 0; i + 18 <= n; i++)
        {
            if (b[i] != 0x0c || b[i + 9] != 0x0c) continue;
            float x0 = BitConverter.ToSingle(b, i + 1), y0 = BitConverter.ToSingle(b, i + 5);
            float x1 = BitConverter.ToSingle(b, i + 10), y1 = BitConverter.ToSingle(b, i + 14);
            if (float.IsFinite(x0) && float.IsFinite(y1) && x1 > x0 && y1 > y0 && Math.Abs(x0) < 1e5f && Math.Abs(x1) < 1e5f && Math.Abs(y1) < 1e5f)
                return (x0, y0, x1, y1);
        }
        return null;
    }
}
