using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign.GlobalMesh;

/// <summary>Which global mesh a tile category feeds.</summary>
public enum MeshKind { Land, Sea }

/// <summary>
/// Whether a world point is covered by a placed tile of the given kind (BOB's global-mesh height query,
/// FUN_18016ae30 + TERRAIN_RENDER_SETUP::get_height_worker):
///  - tile-map coordinate = world / tile size, y = 0 south; a record covers [x, x+w] x [y, y+h] (w/h swapped for
///    orientation 0x20/0x80), and the sub-tile index, rotated by the orientation, must be valid in the tile's mask
///    (a point on the far edge falls outside the tile)
///  - if no tile covers the point, four diagonal probes at ±0.001 are tried
/// Tile categories are classified from vanilla 3k_dlc07 coverage: river/road/canal tiles are excluded (they carry
/// their own geometry), sea tiles use the sea height map, sea_coast feeds both meshes.
/// </summary>
public sealed class TileCoverage
{
    private readonly TileList _list;
    private readonly TileInfo?[] _tileOfPath;
    private readonly (bool Land, bool Sea)[] _kindOfPath;
    private readonly List<int>[] _cells;
    private readonly int _tilesW, _tilesH;
    private readonly float _tileSize;
    private const float Probe = 0.001f;

    public TileCoverage(TileList list, IReadOnlyDictionary<string, TileInfo> db, float tileSize, out List<string> missing)
    {
        _list = list;
        _tileSize = tileSize;
        _tilesW = list.Ints[1];
        _tilesH = list.Ints[2];
        missing = [];
        _tileOfPath = new TileInfo?[list.Paths.Count];
        _kindOfPath = new (bool, bool)[list.Paths.Count];
        for (var i = 0; i < list.Paths.Count; i++)
        {
            db.TryGetValue(TileDatabase.NormalisePath(list.Paths[i]), out var tile);
            if (tile is null) missing.Add(list.Paths[i]);
            _tileOfPath[i] = tile;
            _kindOfPath[i] = Classify(list.Paths[i]);
        }

        _cells = new List<int>[_tilesW * _tilesH];
        for (var r = 0; r < list.Records.Count; r++)
        {
            var rec = list.Records[r];
            var tile = _tileOfPath[rec.Path];
            if (tile is null) continue;
            var (w, h) = Size(tile, rec.Orientation);
            for (var y = rec.Y; y <= Math.Min(rec.Y + h, _tilesH - 1); y++)
                for (var x = rec.X; x <= Math.Min(rec.X + w, _tilesW - 1); x++)
                    (_cells[y * _tilesW + x] ??= []).Add(r);
        }
    }

    /// <summary>(feeds land, feeds sea) for a tile folder path.</summary>
    public static (bool Land, bool Sea) Classify(string path)
    {
        var parts = path.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.FindIndex(parts, p => p.Equals("campaign", StringComparison.OrdinalIgnoreCase));
        var category = i >= 0 && i + 1 < parts.Length ? parts[i + 1].ToLowerInvariant() : "";
        if (category.StartsWith("river_mouth", StringComparison.Ordinal)) return (false, true);
        if (category.StartsWith("river", StringComparison.Ordinal) || category.StartsWith("roads", StringComparison.Ordinal)
            || category.StartsWith("canal", StringComparison.Ordinal)) return (false, false);
        if (category == "sea_coast") return (true, true);
        if (category == "generic_sea" || category.StartsWith("blockout_cliff", StringComparison.Ordinal)) return (false, true);
        return (true, false);
    }

    private static (int W, int H) Size(TileInfo tile, byte orientation) =>
        (orientation & 0xF0) is 0x20 or 0x80 ? (tile.Height, tile.Width) : (tile.Width, tile.Height);

    /// <summary>True when a tile of <paramref name="kind"/> covers the point, trying the diagonal probes if needed.</summary>
    public bool Covered(float x, float z, MeshKind kind) =>
        CoveredAt(x, z, kind) ||
        CoveredAt(x + Probe, z + Probe, kind) || CoveredAt(x - Probe, z + Probe, kind) ||
        CoveredAt(x + Probe, z - Probe, kind) || CoveredAt(x - Probe, z - Probe, kind);

    private bool CoveredAt(float x, float z, MeshKind kind)
    {
        var tx = x / _tileSize;
        var ty = z / _tileSize;
        if (tx < 0 || ty < 0) return false;
        var cx = (int)tx;
        var cy = (int)ty;
        if (cx >= _tilesW || cy >= _tilesH) return false;
        var cell = _cells[cy * _tilesW + cx];
        if (cell is null) return false;
        foreach (var r in cell)
        {
            var rec = _list.Records[r];
            var kinds = _kindOfPath[rec.Path];
            if (kind == MeshKind.Land ? !kinds.Land : !kinds.Sea) continue;
            var tile = _tileOfPath[rec.Path]!;
            var (w, h) = Size(tile, rec.Orientation);
            if (tx < rec.X || tx > rec.X + w || ty < rec.Y || ty > rec.Y + h) continue;
            var i = (int)(tx - rec.X);
            var j = (int)(ty - rec.Y);
            int col, row;
            switch (rec.Orientation & 0xF0)
            {
                case 0x20: row = i; col = tile.Width - j - 1; break;
                case 0x40: row = tile.Height - j - 1; col = tile.Width - i - 1; break;
                case 0x80: row = tile.Height - i - 1; col = j; break;
                default: row = j; col = i; break;
            }
            if (tile.SubtileValid(col, tile.Height - row - 1)) return true;
        }
        return false;
    }
}
