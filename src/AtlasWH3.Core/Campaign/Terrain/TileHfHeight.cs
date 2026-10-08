using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign.Terrain;

/// <summary>
/// BOB's campaign terrain height at a point in tile space (WARSCAPE::TERRAIN_RENDER_SETUP::get_height_worker,
/// warscape 0x370e20 / 0x371030), all float32 in BOB's order:
///  - tile instances are tried in tile_list record order; the first one that answers wins. A tile answers when the
///    point lies inside its rotated bounds (both edges inclusive), the sub-tile under the point is valid (masked tiles
///    only) and either that sub-tile is valid or its hf is non-zero.
///  - lf: u = x / (tilesW · T), v = 1 − z / (tilesH · T); FUN_18039eea0 bilinear on lf_height_map (corners int(fx),
///    int(fx)+1, int(fy)−1, int(fy)); height = (l · 1100) · f′ − f′ · 240, f′ = (1/25.6) · T (= 5/128 · T). These are
///    TERRAIN_RENDER_SETUP's own fields (+0x8c = 1100, +0x84 = 240, +0x324 = T, read from BOB's memory 2026-10-04):
///    the same value as (l · 5500) · f − f · 1200 with f = T/128, but rounded differently in float32.
///  - hf (get_high_frequency_height_new): the tile's hf_height_map.compressed_map sampled with the same bilinear at the
///    tile-local (u, v) (rotation 0x20: (1 − v, u), 0x40: (1 − u, 1 − v), 0x80: (v, 1 − u)), value = raw/65535 ·
///    (hi − lo) + lo, times f. Version-2 maps (lo/hi only, 3 junctions) are read like version 3. Tiles with only the
///    old hf_height_map.data (blockout cliffs, terrace farms ...) add 0 here; no vanilla tree lands on one.
///  - result = hf + lf; height_split returns (total − lf, lf) and the tree list stores their sum.
/// Vanilla 3k_dlc07: the lf part equals BOB's (Frida dump of "Campaign Trees") on 99.9995% of trees; see
/// docs/native_campaign_build.md for the per-tree totals against CA's shipped list.
/// </summary>
public sealed class TileHfHeight
{
    private const float K = 1f / 65535f;
    private readonly TileList _list;
    private readonly TileInfo?[] _tileOfPath;
    private readonly HfMap?[] _hfOfPath;
    private readonly List<int>?[] _cells;
    private readonly List<int>?[] _bobCells;      // ws_tile_instance_indices_at: a tile on its cells [X, X+w) x [Y, Y+h)
    private readonly int _tilesW, _tilesH;
    private readonly float _t, _invT, _f, _fLf, _maxX, _maxZ;
    private readonly HfMap _lf;

    private sealed record HfMap(ushort[] Data, int W, int H, float Lo, float Hi);

    /// <param name="readPack">reads a game file by internal path (null when missing)</param>
    public TileHfHeight(TileList list, IReadOnlyDictionary<string, TileInfo> db, Func<string, byte[]?> readPack,
                        CompressedMap.Map lf, float tileSize)
    {
        _list = list;
        _tilesW = list.Ints[1];
        _tilesH = list.Ints[2];
        _t = tileSize;
        _invT = 1f / tileSize;
        _f = 1f / 128f * tileSize;              // hf multiplier (TERRAIN_RENDER_SETUP +0x320)
        _fLf = 0.0390625f * tileSize;           // (1/25.6) · T, exact in float32: render_params scale · T (+0x324)
        _maxX = _tilesW * tileSize;
        _maxZ = _tilesH * tileSize;
        _lf = new HfMap(lf.Raster.Data, lf.Raster.Width, lf.Raster.Height, lf.Header[1], lf.Header[4]);

        _tileOfPath = new TileInfo?[list.Paths.Count];
        _hfOfPath = new HfMap?[list.Paths.Count];
        var cache = new Dictionary<string, HfMap?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Paths.Count; i++)
        {
            var key = TileDatabase.NormalisePath(list.Paths[i]);
            db.TryGetValue(key, out var tile);
            _tileOfPath[i] = tile;
            if (!cache.TryGetValue(key, out var hf))
            {
                hf = null;
                var bytes = readPack(key.Replace('\\', '/') + "hf_height_map.compressed_map");
                if (bytes is not null)
                {
                    try
                    {
                        var m = CompressedMap.Decode(bytes);
                        if (m.Header[1] != 0f || m.Header[4] != 0f)
                            hf = new HfMap(m.Raster.Data, m.Raster.Width, m.Raster.Height, m.Header[1], m.Header[4]);
                    }
                    catch (InvalidDataException) { }        // unreadable map: no hf
                }
                cache[key] = hf;
            }
            _hfOfPath[i] = hf;
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
        _bobCells = new List<int>[_tilesW * _tilesH];
        for (var r = 0; r < list.Records.Count; r++)
        {
            var rec = list.Records[r];
            var tile = _tileOfPath[rec.Path];
            if (tile is null) continue;
            var (w, h) = Size(tile, rec.Orientation);
            for (var y = rec.Y; y < Math.Min(rec.Y + h, _tilesH); y++)
                for (var x = rec.X; x < Math.Min(rec.X + w, _tilesW); x++)
                    (_bobCells[y * _tilesW + x] ??= []).Add(r);
        }
    }

    /// <summary>
    /// Campaign Trees' provider (qttoolutility FUN_18011f1d0): the cell is int(inverse world transform · point) of
    /// the WORLD point (x, z), its tile instances (ws_tile_instance_indices_at) are each asked get_height at (x, z /
    /// 1.15476), and the HIGHEST answering height wins (0 when none answers); lf comes from that tile.
    /// Inverse transform coefficients: (ix, iz) with cell = (int(x · ix), int(z · iz)).
    /// </summary>
    public bool BobCells { get; init; }
    public float CellScaleX { get; init; } = float.NaN;
    public float CellScaleZ { get; init; } = float.NaN;

    /// <summary>Tree height at the campaign world point as BOB's Campaign Trees provider computes it.</summary>
    public float TreeHeight(float x, float zWorld)
    {
        var z = zWorld / 1.15476f;
        if (!BobCells) return Height(x, z);
        if (x < 0f || x > _maxX || z < 0f || z > _maxZ) return 0f;
        var ix = float.IsNaN(CellScaleX) ? _invT : CellScaleX;
        var iz = float.IsNaN(CellScaleZ) ? 1f / (_t * 1.15476f) : CellScaleZ;
        int cx = (int)(x * ix), cy = (int)(zWorld * iz);
        if (cx < 0 || cy < 0 || cx >= _tilesW || cy >= _tilesH || _bobCells[cy * _tilesW + cx] is not { } cell) return 0f;
        var lf = Lf(x, z);
        var tx = _invT * x;
        var ty = _invT * z;
        var best = float.MinValue;
        var bestLf = 0f;
        foreach (var r in cell)
        {
            if (!TileHeight(r, x, z, tx, ty, lf, out var hf)) continue;
            var h = hf + lf;
            if (best < h) { best = h; bestLf = lf; }
        }
        if (best == float.MinValue) return 0f;
        return (best - bestLf) + bestLf;
    }

    private static (int W, int H) Size(TileInfo t, byte orientation) =>
        (orientation & 0xF0) is 0x20 or 0x80 ? (t.Height, t.Width) : (t.Width, t.Height);

    /// <summary>FUN_18039eea0 + COMPRESSED_MAP::value_float.</summary>
    private static float Sample(HfMap m, float u, float v)
    {
        var fx = m.W * u;
        var fy = m.H * v;
        var fx0 = MathF.Floor(fx);
        var fy0 = MathF.Floor(fy);
        float xi = (int)fx, yi = (int)fy;
        float maxC = m.W - 1, maxR = m.H - 1;
        static float Clamp(float a, float hi) => a < 0f ? 0f : a > hi ? hi : a;
        float Value(float c, float r)
        {
            var raw = m.Data[(int)Clamp(r, maxR) * m.W + (int)Clamp(c, maxC)];
            return raw * K * (m.Hi - m.Lo) + m.Lo;
        }
        var a = Value(xi, yi - 1f);
        var b = Value(xi + 1f, yi - 1f);
        var top = (b - a) * (fx - fx0) + a;
        var c = Value(xi, yi);
        var d = Value(xi + 1f, yi);
        var bot = (d - c) * (fx - fx0) + c;
        return (bot - top) * (fy - fy0) + top;
    }

    /// <summary>lf height at a tile-space point (x, z already divided by the campaign z scale).</summary>
    public float Lf(float x, float z)
    {
        var u = (x - 0f) / (_maxX - 0f);
        var v = 1f - (z - 0f) / (_maxZ - 0f);
        var l = Sample(_lf, u, v);
        return l * 1100f * _fLf - _fLf * 240f;
    }

    /// <summary>Tree height at a tile-space point: (total − lf) + lf, total = hf · f + lf of the first answering tile.</summary>
    public float Height(float x, float z) => Split(x, z) is var (hf, lf) ? hf + lf : 0f;

    /// <summary>height_split: (total − lf, lf).</summary>
    public (float Hf, float Lf) Split(float x, float z)
    {
        // get_height_worker returns 0 (and no tile answers) outside the terrain bounds (+0x340..+0x34c = 0, 0, maxX,
        // maxZ): jittered trees past the map edge get height 0 (213 trees on vanilla)
        if (x < 0f || x > _maxX || z < 0f || z > _maxZ) return (0f, 0f);
        var lf = Lf(x, z);
        var tx = _invT * x;
        var ty = _invT * z;
        int cx = (int)tx, cy = (int)ty;
        // no answering tile -> get_height_worker returns 0 for the whole height (BOB's dump: (0, 0) on vanilla trees in
        // tile holes / on invalid unmasked sub-tiles with zero hf)
        if (cx < 0 || cy < 0 || cx >= _tilesW || cy >= _tilesH) return (0f, 0f);
        var cell = _cells[cy * _tilesW + cx];
        if (cell is null) return (0f, 0f);
        foreach (var r in cell)
            if (TileHeight(r, x, z, tx, ty, lf, out var hf))
            {
                var total = hf + lf;
                return (total - lf, lf);
            }
        return (0f, 0f);
    }

    /// <summary>
    /// The camera height map's terrain fallback (warscape FUN_180350820): the highest get_height (hf + lf) over the
    /// tiles under the point that answer, only the record indices <paramref name="include"/> accepts (BOB: instance
    /// flag 0x100); 0 when none answers. Tile-space point as <see cref="Split"/>.
    /// </summary>
    public float MaxHeight(float x, float z, Func<int, bool> include)
    {
        if (x < 0f || x > _maxX || z < 0f || z > _maxZ) return 0f;
        var tx = _invT * x;
        var ty = _invT * z;
        int cx = (int)tx, cy = (int)ty;
        if (cx < 0 || cy < 0 || cx >= _tilesW || cy >= _tilesH || _cells[cy * _tilesW + cx] is not { } cell) return 0f;
        var lf = Lf(x, z);
        var best = float.MinValue;
        var any = false;
        foreach (var r in cell)
        {
            if (!include(r) || !TileHeight(r, x, z, tx, ty, lf, out var hf)) continue;
            var h = hf + lf;
            if (best < h) best = h;
            any = true;
        }
        return any ? best : 0f;
    }

    /// <summary>get_height_worker for one tile record: false when the tile doesn't answer at the point.</summary>
    private bool TileHeight(int r, float x, float z, float tx, float ty, float lf, out float hf)
    {
        hf = 0f;
        var rec = _list.Records[r];
        var tile = _tileOfPath[rec.Path]!;
        var rot = rec.Orientation & 0xF0;
        var (w, h) = Size(tile, rec.Orientation);
        float x0 = rec.X, y0 = rec.Y, x1 = rec.X + w, y1 = rec.Y + h;
        if (tx < x0 || x1 < tx || ty < y0 || y1 < ty) return false;
        int ix = (int)(tx - x0), iy = (int)(ty - y0), col = ix, row = iy;
        switch (rot)
        {
            case 0x20: col = tile.Width - iy - 1; row = ix; break;
            case 0x40: col = tile.Width - ix - 1; row = tile.Height - iy - 1; break;
            case 0x80: col = iy; row = tile.Height - ix - 1; break;
        }
        var valid = tile.SubtileValid(col, tile.Height - row - 1);
        if (tile.Mask.Length > 0 && !valid) return false;
        if (x < 0f || x > _maxX || z < 0f || z > _maxZ) return false;
        var a = (tx - x0) / (x1 - x0);
        var b = (ty - y0) / (y1 - y0);
        float u = a, v = b;
        switch (rot)
        {
            case 0x10: break;
            case 0x20: u = 1f - b; v = a; break;
            case 0x40: u = 1f - a; v = 1f - b; break;
            case 0x80: u = b; v = 1f - a; break;
            default: u = 0f; v = 0f; break;
        }
        var hfMap = _hfOfPath[rec.Path];
        hf = hfMap is null ? 0f : Sample(hfMap, u, v) * _f;
        return valid || hf != 0f;
    }
}
