using System.Globalization;
using System.Xml.Linq;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign.Props;

/// <summary>
/// BOB's region for a campaign object: the map.hex region under the entity's ECTransform position. Port of
/// bob_terrain FUN_18005fe10 with the hex geometry of EMPIREUTILITY::HEX_MAP_DATA_FILE_DATA::load (float maths, as
/// BOB): flat-top hexes of radius R = (2/3)·(maxx − minx)/(columns − 1) over the map's bounds, odd columns half a
/// hex north. Checked on main190: matches BOB's region for every one of 66,268 objects (docs/native_campaign_build.md).
/// </summary>
public sealed class HexRegionLookup
{
    private readonly MapHexFile _hex;
    private readonly float _minX, _minY, _maxX, _r, _h, _inv2H, _inv15R;

    public HexRegionLookup(MapHexFile hex, float minX, float minY, float maxX)
    {
        _hex = hex;
        _minX = minX;
        _minY = minY;
        _maxX = maxX;
        _r = 0.6666667f / (hex.Width - 1f) * (maxX - minX);
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
        return (Math.Clamp(col, 0, _hex.Width - 1), Math.Clamp(row, 0, _hex.Height - 1));
    }

    public MapHexFile Hex => _hex;

    /// <summary>The rectangle BOB's bmd quadtree covers (bob_terrain FUN_180060e10 root): the map bounds' x range and the
    /// hex grid's z extent, (rows + 0.5) x row step from minY (main190: 0..986.051 x 0..873.7957; fitted to BOB's
    /// level-6 cells 2026-10-04).</summary>
    public (float X0, float Z0, float X1, float Z1) QuadRoot => (_minX, _minY, _maxX, _minY + (_hex.Height + 0.5f) * (_h + _h));

    /// <summary>World centre of hex (col, row) (the inverse of <see cref="HexAt"/>: columns 1.5 R apart, even
    /// columns half a hex south).</summary>
    public (float X, float Z) HexCentre(int col, int row) =>
        (_minX + col * 1.5f * _r, _minY + (row + 0.5f) * (_h + _h) - ((col & 1) == 0 ? _h : 0));

    /// <summary>Region index (land then sea, see <see cref="MapHexFile.RegionIndexAt"/>) at a world point, -1 none.</summary>
    public int RegionIndexAt(double x, double z)
    {
        var (c, r) = HexAt((float)x, (float)z);
        return _hex.RegionIndexAt(c, r);
    }

    public string? RegionAt(double x, double z)
    {
        var (c, r) = HexAt((float)x, (float)z);
        return _hex.RegionAt(c, r);
    }

    /// <summary>The lookup for a map: its map.hex and the bounds of its campaign_map_playable_areas row in
    /// EmpireDesignData (BOB takes the bounds from map_data.esf, which CAIME writes from that row). Null when either
    /// is missing.</summary>
    public static HexRegionLookup? ForMap(ProjectPaths paths, out string reason)
    {
        var hexPath = Path.Combine(paths.AkDesignCampaignMapDir, "map.hex");
        var areas = Path.Combine(paths.AssemblyKitRoot, "raw_data", "EmpireDesignData", "campaign_map_playable_areas.xml");
        if (!File.Exists(hexPath)) { reason = $"no {hexPath}"; return null; }
        if (!File.Exists(areas)) { reason = $"no {areas}"; return null; }
        var row = XDocument.Load(areas).Descendants("campaign_map_playable_areas")
            .FirstOrDefault(e => (string?)e.Element("mapname") == paths.MapName);
        if (row is null) { reason = $"no campaign_map_playable_areas row for {paths.MapName}"; return null; }
        float F(string n) => float.Parse((string?)row.Element(n) ?? "0", CultureInfo.InvariantCulture);
        reason = "";
        var (minX, minY, maxX) = (F("minx"), F("miny"), F("maxx"));
        // BOB reads the bounds from the compiled map_data.esf, whose floats can be an ulp off the XML's decimal text
        // (main190: 986.05096 vs float(986.051)), which moves hex edges by an ulp (2026-10-05, one boundary prop)
        if (EsfBounds(Path.Combine(paths.AkWorkingCampaignMapDir, "map_data.esf"), minX, minY, maxX) is { } esf)
            (minX, minY, maxX) = esf;
        return new HexRegionLookup(MapHexFile.Read(hexPath), minX, minY, maxX);
    }

    /// <summary>The map bounds in map_data.esf's header: two vec2 values (ESF type 0x0c) holding (min x, min y) and
    /// (max x, max y), accepted only when they are within 0.01 of the playable-area row.</summary>
    internal static (float MinX, float MinY, float MaxX)? EsfBounds(string path, float minX, float minY, float maxX)
    {
        if (!File.Exists(path)) return null;
        var b = new byte[4096];
        int n;
        using (var s = File.OpenRead(path)) n = s.Read(b, 0, b.Length);
        for (var i = 0; i + 18 <= n; i++)
        {
            if (b[i] != 0x0c || b[i + 9] != 0x0c) continue;
            float x0 = BitConverter.ToSingle(b, i + 1), y0 = BitConverter.ToSingle(b, i + 5), x1 = BitConverter.ToSingle(b, i + 10);
            if (Math.Abs(x0 - minX) <= 0.01f && Math.Abs(y0 - minY) <= 0.01f && Math.Abs(x1 - maxX) <= 0.01f) return (x0, y0, x1);
        }
        return null;
    }
}
