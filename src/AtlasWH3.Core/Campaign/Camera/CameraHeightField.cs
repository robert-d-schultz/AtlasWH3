using System.Buffers.Binary;
using AtlasWH3.Core.Campaign.Terrain;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Models;
using AtlasWH3.Formats.Props;

namespace AtlasWH3.Core.Campaign.Camera;

/// <summary>
/// The scene height BOB's "Generate Camera Height Map" samples (warscape FUN_18034cf20, the campaign scene's height
/// provider), all float32 in BOB's order. For a world point (x, z):
///  - P = the highest height patch under the point (FUN_180350320): river height patches, the props of the tiles'
///    bmd_data.bin and the global props that have a height patch. A patch answers when the point is inside its world
///    AABB and, through its inverse matrix, inside its model's local bounds (all edges inclusive); value =
///    |column 1| · sample + translation y, sample = the max of three corners of the patch map (FUN_18039f140).
///    Patches whose AABB is not inside the scene quadtree's root are never stored and so never answer.
///  - G = the global mesh (FUN_180350620) at (x, z / 1.15476): the first land_mesh block, in file-name order, whose
///    bounds (the model's bounds at 0xC0) contain the point; its compressed map sampled bilinearly with invalid
///    corners filled (FUN_18039f3e0). An invalid sample (−50), or no block, falls back to the tiles: the highest
///    get_height over the answering tile instances the scene quadtree reaches at the point (<see cref="TileQuadtree"/>,
///    FUN_180350820), 0 if none.
///  - height = max(G, P).
/// Reverse-engineered with Frida dumps of BOB's objects and sample values (research/bob_re/frida_camera*.js).
/// </summary>
public sealed class CameraHeightField
{
    public const float ZScale = 1.15476f;
    public const float Invalid = -50f;
    private const float K = 1f / 65535f;
    private const float PatchCell = 2f;

    public sealed record HeightMap(ushort[] Data, int W, int H, float Lo, float Hi)
    {
        public static HeightMap From(CompressedMap.Map m) => new(m.Raster.Data, m.Raster.Width, m.Raster.Height, m.Header[1], m.Header[4]);
        public float Value(int x, int y) => Data[y * W + x] * K * (Hi - Lo) + Lo;
    }

    /// <summary>A global mesh block: land_mesh_N bounds (tile space: x, z / 1.15476) and its height map.</summary>
    public sealed record Block(string Name, float MinX, float MinZ, float MaxX, float MaxZ, HeightMap Map);

    /// <summary>A height patch object: world AABB, local bounds, row-major 4x4 world matrix and its inverse.</summary>
    public sealed record Patch(string Source, float[] Aabb, float[] Local, float[] M, float[] Inv, HeightMap Map)
    {
        public float Scale { get; } = MathF.Sqrt(M[5] * M[5] + M[1] * M[1] + M[9] * M[9]);
    }

    public IReadOnlyList<Block> Blocks { get; }
    public IReadOnlyList<Patch> Patches { get; }
    /// <summary>Patches the quadtree drops (AABB not inside the root).</summary>
    public int DroppedPatches { get; }
    private readonly TileHfHeight? _tiles;
    private readonly Func<int, float, float, bool> _fallbackTile;
    private readonly int[][] _patchCells;
    private readonly int _pcw, _pch;
    private readonly float _pcx0, _pcz0;

    public CameraHeightField(IReadOnlyList<Block> blocks, IReadOnlyList<Patch> patches, float[] root, TileHfHeight? tiles,
                             Func<int, float, float, bool>? fallbackTile = null, bool filter = true)
    {
        Blocks = blocks.OrderBy(b => b.Name, StringComparer.Ordinal).ToList();
        var kept = patches.Where(p => !filter || p.Aabb[0] >= root[0] && p.Aabb[1] >= root[1] && p.Aabb[2] <= root[2] && p.Aabb[3] <= root[3]).ToList();
        DroppedPatches = patches.Count - kept.Count;
        Patches = kept;
        _tiles = tiles;
        _fallbackTile = fallbackTile ?? ((_, _, _) => true);

        _pcx0 = root[0];
        _pcz0 = root[1];
        _pcw = (int)MathF.Ceiling((root[2] - root[0]) / PatchCell) + 1;
        _pch = (int)MathF.Ceiling((root[3] - root[1]) / PatchCell) + 1;
        var cells = new List<int>?[_pcw * _pch];
        for (var i = 0; i < kept.Count; i++)
        {
            var a = kept[i].Aabb;
            int x0 = CellX(a[0]), x1 = CellX(a[2]), z0 = CellZ(a[1]), z1 = CellZ(a[3]);
            for (var cz = z0; cz <= z1; cz++)
                for (var cx = x0; cx <= x1; cx++)
                    (cells[cz * _pcw + cx] ??= []).Add(i);
        }
        _patchCells = cells.Select(c => c?.ToArray() ?? []).ToArray();
    }

    /// <summary>The same scene with other patch objects (all kept: no root filter).</summary>
    public CameraHeightField WithPatches(IReadOnlyList<Patch> patches) =>
        new(Blocks, patches, [_pcx0, _pcz0, _pcx0 + (_pcw - 1) * PatchCell, _pcz0 + (_pch - 1) * PatchCell], _tiles, _fallbackTile, filter: false);

    private int CellX(float x) => Math.Clamp((int)MathF.Floor((x - _pcx0) / PatchCell), 0, _pcw - 1);
    private int CellZ(float z) => Math.Clamp((int)MathF.Floor((z - _pcz0) / PatchCell), 0, _pch - 1);

    /// <summary>FUN_18034cf20: the scene height at a world point.</summary>
    public float Height(float x, float z) => Height(x, z, null);

    /// <summary>The scene height ignoring the patches <paramref name="skip"/> picks (e.g. a prop's own height patch
    /// when seating that prop).</summary>
    public float Height(float x, float z, Func<Patch, bool>? skip)
    {
        var p = PatchHeight(x, z, skip);
        var zt = z / ZScale;
        var g = GlobalMeshHeight(x, zt);
        if (g == float.MinValue)
        {
            var qz = zt * ZScale;                               // FUN_180350820 queries the tree at z' · 1.15476
            g = _tiles?.MaxHeight(x, zt, r => _fallbackTile(r, x, qz)) ?? 0f;
        }
        return g <= p ? p : g;
    }

    /// <summary>FUN_180350320: the highest patch at the point, −FLT_MAX when none answers.</summary>
    public float PatchHeight(float x, float z) => PatchHeight(x, z, null);

    private float PatchHeight(float x, float z, Func<Patch, bool>? skip)
    {
        var best = float.MinValue;
        foreach (var i in _patchCells[CellZ(z) * _pcw + CellX(x)])
        {
            var o = Patches[i];
            if (skip is not null && skip(o)) continue;
            var a = o.Aabb;
            if (!(a[0] <= x && x <= a[2] && a[1] <= z && z <= a[3])) continue;
            var inv = o.Inv;
            var lx = z * inv[2] + x * inv[0] + inv[3];
            var lz = z * inv[10] + x * inv[8] + inv[11];
            var l = o.Local;
            if (!(l[0] <= lx && lx <= l[2] && l[1] <= lz && lz <= l[3])) continue;
            var u = (lx - l[0]) / (l[2] - l[0]);
            var v = (lz - l[1]) / (l[3] - l[1]);
            var h = SamplePatch(o.Map, u, v);
            if (h == Invalid) continue;
            var value = o.Scale * h + o.M[7];
            if (best < value) best = value;
        }
        return best;
    }

    /// <summary>FUN_180350620 at a tile-space point: −FLT_MAX when no block contains it or its sample is invalid.</summary>
    public float GlobalMeshHeight(float x, float zt)
    {
        foreach (var b in Blocks)
        {
            if (!(b.MinX <= x && b.MinZ <= zt && x <= b.MaxX && zt <= b.MaxZ)) continue;
            var v = (zt - b.MinZ) / (b.MaxZ - b.MinZ);
            var u = (x - b.MinX) / (b.MaxX - b.MinX);
            var h = SampleMesh(b.Map, u, v);
            return Invalid < h ? h : float.MinValue;
        }
        return float.MinValue;
    }

    /// <summary>FUN_18039f140: the max of the corners (x0, y−1), (x1, y−1), (x1, y).</summary>
    public static float SamplePatch(HeightMap m, float u, float v)
    {
        var x0 = MathF.Truncate(m.W * u);
        var y0 = MathF.Truncate(m.H * v);
        float maxC = m.W - 1, maxR = m.H - 1;
        int Cx(float a) => (int)Math.Clamp(a, 0f, maxC);
        int Cy(float a) => (int)Math.Clamp(a, 0f, maxR);
        var v1 = m.Value(Cx(x0), Cy(y0 - 1f));
        var v2 = m.Value(Cx(x0 + 1f), Cy(y0 - 1f));
        var v4 = m.Value(Cx(x0 + 1f), Cy(y0));
        var r = v2 <= v1 ? v1 : v2;
        return r <= v4 ? v4 : r;
    }

    /// <summary>FUN_18039f3e0: bilinear over (x0, y0)..(x1, y1), invalid corners replaced by the first valid of
    /// A (x0, y1), C (x0, y0), B (x1, y1), D (x1, y0); −50 when all four are invalid.</summary>
    public static float SampleMesh(HeightMap m, float u, float v)
    {
        var fx = m.W * u;
        var fy = m.H * v;
        var tx = fx - MathF.Floor(fx);
        var ty = fy - MathF.Floor(fy);
        var x0 = MathF.Truncate(fx);
        var y0 = MathF.Truncate(fy);
        float maxC = m.W - 1, maxR = m.H - 1;
        int Cx(float a) => (int)Math.Clamp(a, 0f, maxC);
        int Cy(float a) => (int)Math.Clamp(a, 0f, maxR);
        var a = m.Value(Cx(x0), Cy(y0 + 1f));
        var b = m.Value(Cx(x0 + 1f), Cy(y0 + 1f));
        var c = m.Value(Cx(x0), Cy(y0));
        var d = m.Value(Cx(x0 + 1f), Cy(y0));
        if (a == Invalid && b == Invalid && c == Invalid && d == Invalid) return Invalid;
        var fill = a != Invalid ? a : c != Invalid ? c : b != Invalid ? b : d;
        if (a == Invalid) a = fill;
        if (b == Invalid) b = fill;
        if (c == Invalid) c = fill;
        if (d == Invalid) d = fill;
        var r0 = (d - c) * tx + c;
        var r1 = (b - a) * tx + a;
        return (r0 - r1) * ty + r1;
    }

    // ---- building the objects ----

    /// <summary>The 6-float bounds of a rigid_model_v2's first LOD's first mesh (min x, y, z, max x, y, z).</summary>
    public static float[] ModelBounds(ReadOnlySpan<byte> rmv2)
    {
        var mesh = (int)BinaryPrimitives.ReadUInt32LittleEndian(rmv2[0x98..]);
        var b = new float[6];
        for (var i = 0; i < 6; i++) b[i] = BinaryPrimitives.ReadSingleLittleEndian(rmv2[(mesh + 0x18 + 4 * i)..]);
        return b;
    }

    /// <summary>A patch from a world matrix (row-major 4x4), the model's bounds and its height map.</summary>
    public static Patch MakePatch(string source, float[] m, float[] modelBounds, HeightMap map)
    {
        float[] local = [modelBounds[0], modelBounds[2], modelBounds[3], modelBounds[5]];
        return new Patch(source, Aabb(m, modelBounds), local, m, Inverse(m), map);
    }

    /// <summary>River height patch: identity transform, local bounds = world bounds.</summary>
    public static Patch RiverPatch(string source, float minX, float minZ, float maxX, float maxZ, HeightMap map)
    {
        float[] id = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        float[] b = [minX, minZ, maxX, maxZ];
        return new Patch(source, b, b, id, id, map);
    }

    /// <summary>World x/z bounds of the model's 3D box (min x, y, z, max x, y, z) through the matrix, over its 8 corners
    /// in warscape FUN_18034a210's order: x = x·m0 + y·m1 + z·m2 + m3, z = x·m8 + y·m9 + z·m10 + m11.</summary>
    public static float[] Aabb(float[] m, float[] b)
    {
        float X(float x, float y, float z) => x * m[0] + y * m[1] + z * m[2] + m[3];
        float Z(float x, float y, float z) => x * m[8] + y * m[9] + z * m[10] + m[11];
        float x0 = X(b[0], b[1], b[2]), x1 = x0, z0 = Z(b[0], b[1], b[2]), z1 = z0;
        for (var corner = 1; corner < 8; corner++)
        {
            float x = (corner & 1) != 0 ? b[3] : b[0], y = (corner & 2) != 0 ? b[4] : b[1], z = (corner & 4) != 0 ? b[5] : b[2];
            float wx = X(x, y, z), wz = Z(x, y, z);
            if (wx <= x0) x0 = wx;
            if (x1 <= wx) x1 = wx;
            if (wz <= z0) z0 = wz;
            if (z1 <= wz) z1 = wz;
        }
        return [x0, z0, x1, z1];
    }

    /// <summary>
    /// warscape's 4x4 inverse (inlined in FUN_18034a210), float32: the determinant expanded along column 0 into 3x3
    /// minors (each expanded along its column 0 into 2x2 determinants d·a − b·c), the adjugate built the same way,
    /// times 1 / det. A singular matrix is returned unchanged.
    /// </summary>
    public static float[] Inverse(float[] m)
    {
        var minor = new float[9];
        var det = 0f;
        for (var i = 0; i < 4; i++)
        {
            var e = m[i * 4];
            if ((i & 1) != 0) e = -e;
            Minor(m, minor, i, 0);
            det = det + Det3(minor) * e;
        }
        if (MathF.Abs(det) < 1e-12f) return (float[])m.Clone();
        var inv = 1f / det;
        var adj = new float[16];
        for (var c = 0; c < 4; c++)
            for (var r = 0; r < 4; r++)
            {
                Minor(m, minor, r, c);
                adj[c * 4 + r] = Det3(minor) * (((r + c) & 1) != 0 ? -1f : 1f);
            }
        for (var k = 0; k < 16; k++) adj[k] *= inv;
        return adj;
    }

    private static void Minor(float[] m, float[] dst, int skipRow, int skipCol)
    {
        var n = 0;
        for (var r = 0; r < 4; r++)
        {
            if (r == skipRow) continue;
            for (var c = 0; c < 4; c++)
                if (c != skipCol) dst[n++] = m[r * 4 + c];
        }
    }

    /// <summary>3x3 determinant expanded along column 0, summed in BOB's order.</summary>
    private static float Det3(float[] b)
    {
        var d = 0f;
        Span<float> l = stackalloc float[4];
        for (var k = 0; k < 3; k++)
        {
            var e = b[k * 3];
            if ((k & 1) != 0) e = -e;
            var n = 0;
            for (var r = 0; r < 3; r++)
            {
                if (r == k) continue;
                l[n * 2] = b[r * 3 + 1];
                l[n * 2 + 1] = b[r * 3 + 2];
                n++;
            }
            d = d + (l[3] * l[0] - l[1] * l[2]) * e;
        }
        return d;
    }

    /// <summary>Row-major 4x4 from a stored 4x3 (three 3x3 columns, then the position).</summary>
    public static float[] FromStored(float[] raw)
    {
        var m = new float[16];
        for (var c = 0; c < 3; c++)
            for (var r = 0; r < 3; r++)
                m[r * 4 + c] = raw[c * 3 + r];
        m[3] = raw[9]; m[7] = raw[10]; m[11] = raw[11]; m[15] = 1;
        return m;
    }

    /// <summary>
    /// A tile bmd prop's world matrix: warscape get_tile_transform (tile space 128 units per cell, the record's
    /// south-west corner, scale (s, s, s · 1.15476) with s = T / 128) times the prop's stored matrix in bmd units
    /// (5 tile units each), then lifted by the terrain height under it. All 10,827 vanilla tile-prop matrices match
    /// BOB's objects bit for bit (Frida dump).
    /// </summary>
    public static float[] TileProp(TileList.Record rec, int tileW, int tileH, float tileSize, float[] raw)
    {
        var s = tileSize / 128f;
        var sz = s * ZScale;
        float px = rec.X * 128f, pz = rec.Y * 128f, w = tileW * 128f, h = tileH * 128f;
        // tile 3x4 (row-major, translation in column 3); the z translation is scaled by 1.15476 after s, as BOB
        var t = new float[12];
        switch (rec.Orientation & 0xF0)
        {
            case 0x20: t[2] = s; t[3] = px * s; t[5] = s; t[8] = -sz; t[11] = (w + pz) * s * ZScale; break;
            case 0x40: t[0] = -s; t[3] = (w + px) * s; t[5] = s; t[10] = -sz; t[11] = (h + pz) * s * ZScale; break;
            case 0x80: t[2] = -s; t[3] = (h + px) * s; t[5] = s; t[8] = sz; t[11] = pz * s * ZScale; break;
            default: t[0] = s; t[3] = px * s; t[5] = s; t[10] = sz; t[11] = pz * s * ZScale; break;
        }
        // BOB: the tile matrix times 5 (bmd units), then the prop's: element (t · 5) · p, translation (t · 5) · l + t₃
        var p = FromStored(raw);
        var m = new float[16];
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 4; c++)
                for (var k = 0; k < 3; k++)
                    if (t[r * 4 + k] != 0f) m[r * 4 + c] = t[r * 4 + k] * 5f * p[k * 4 + c];
            m[r * 4 + 3] += t[r * 4 + 3];
        }
        m[15] = 1;
        return m;
    }
}
