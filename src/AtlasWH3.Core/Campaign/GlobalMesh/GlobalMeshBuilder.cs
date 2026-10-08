using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Models;

namespace AtlasWH3.Core.Campaign.GlobalMesh;

/// <summary>
/// Native replacement for BOB's global mesh build (tooldatabuilder FUN_180124ed0), following the decompiled
/// algorithm (docs/bob_re_global_mesh.md). With a <see cref="BobGlobalHeight"/> (the default "bob" geometry) the files
/// are identical to BOB's apart from the bytes it leaves uninitialised: its height query, skirts (<see cref="BobSkirts"/>),
/// bounds and compressed map (<see cref="BobHeightMap"/>). Without one, the earlier game-valid approximation below.
///  - the map is cut into square meshes of <c>cells = (int)(maxTiles · 0.125)</c> cells (2 cells per tile-map pixel),
///    visited z-row by z-row from the south-west; empty meshes are skipped and the rest numbered in that order
///  - vertex heights: <see cref="LfSampler"/> where a tile of the mesh kind covers the point, holes elsewhere
///  - flags (sea meshes pin every 4th row and column), normalised Sobel normals (up = 1/0.33), quads split [a,c,b] [c,d,b], <see cref="TriangleMerger"/> at
///    factor 0.9999, winding flipped, vertices renumbered in first-use order
///  - skirts: a double-sided quad 1.0 below every boundary edge next to a hole or the map edge (not on seams
///    between meshes)
///  - land meshes also get a (cells+1)² compressed map of the final surface: header (0, -50, 0, 0, max, 0), 0 = hole
/// </summary>
public sealed class GlobalMeshBuilder
{
    public const float MergeFactor = 0.9999f;
    public const float Hole = -20f;
    public const float SkirtDepth = 1f;

    public sealed record MeshResult(int Row, int Col, RigidModelV2 Model, Raster<ushort>? HeightMap, float[]? HeightHeader, int Triangles);

    private readonly TileCoverage _coverage;
    private readonly LfSampler _land, _sea;
    private readonly int _cells, _gridTotal, _meshesPerAxis;
    private readonly double _extent;
    private readonly BobGlobalHeight? _bob;
    /// <summary>BOB's grid step (FUN_18016b2f0): (max tiles · T′) / (2 · max tiles) in float32, T′ = the terrain's tile size.</summary>
    private readonly float _bobCell, _bobExtent;
    private readonly int _tilesWForPos, _tilesHForPos;
    private readonly float _bobProbe;
    private static int _gridTotalOf(int tilesW, int tilesH) => 2 * Math.Max(tilesW, tilesH);

    public int MeshesPerAxis => _meshesPerAxis;

    public GlobalMeshBuilder(TileCoverage coverage, LfSampler land, LfSampler sea, int tilesW, int tilesH, float tileSize,
        BobGlobalHeight? bob = null)
    {
        _bob = bob;
        (_tilesWForPos, _tilesHForPos) = (tilesW, tilesH);
        if (bob is not null)
        {
            _bobExtent = Math.Max(tilesW, tilesH) * bob.TileSize;
            _bobCell = _bobExtent / _gridTotalOf(tilesW, tilesH);
        }
        // DAT_1806b2c04: the neighbour probe offset (research: env override until read from BOB)
        _bobProbe = float.TryParse(Environment.GetEnvironmentVariable("ATLASWH3_GMESH_PROBE"), System.Globalization.CultureInfo.InvariantCulture, out var pr) ? pr : _bobCell;
        _coverage = coverage;
        _land = land;
        _sea = sea;
        var maxTiles = Math.Max(tilesW, tilesH);
        _cells = (int)(maxTiles * 0.125f);
        _gridTotal = 2 * maxTiles;
        _meshesPerAxis = (_gridTotal + _cells - 1) / _cells;
        _extent = (float)(maxTiles * tileSize);
    }

    /// <summary>Vertex position on the grid: BOB writes I · ext / (2 · max tiles) computed in double (bit-exact on its
    /// files), while its height queries use the float step <see cref="Coord"/>.</summary>
    private float Position(int index) => _bob is not null && (BobGlobalHeight.Variant & 256) == 0
        ? (float)(index * (double)(Math.Max(_tilesWForPos, _tilesHForPos) * _bob.TileSize) / _gridTotal) : Coord(index);

    private float Coord(int index) => _bob is not null ? index * _bobCell : (float)(index * _extent / _gridTotal);

    /// <summary>BOB's height-query coordinate (FUN_18016b2f0): (i + i0) · (ext / total) with the step in double,
    /// ext = (float)(max tiles · T′), rounded to float once (fits its frida_gheight3 call dump on every point;
    /// the float step is 1 ulp off on ~13% of rows).</summary>
    private float QueryCoord(int local, int origin) => _bob is not null && (BobGlobalHeight.Variant & 4096) == 0
        ? (float)((double)(local + origin) * ((double)_bobExtent / _gridTotal)) : Coord(origin + local);

    public MeshResult? Build(int row, int col, MeshKind kind)
    {
        var n = _cells + 1;
        var i0 = col * _cells;
        var j0 = row * _cells;
        var x = new float[n * n];
        var y = new float[n * n];
        var z = new float[n * n];
        var flags = new byte[n * n];
        var sampler = kind == MeshKind.Land ? _land : _sea;

        // validity of every grid point of this mesh plus a one-cell border, queried once
        var m = n + 2;
        var validGrid = new bool[m * m];
        var bobHeight = _bob is null ? null : new float[m * m];
        for (var j = -1; j <= n; j++)
            for (var i = -1; i <= n; i++)
            {
                int gi = i0 + i, gj = j0 + j;
                if (_bob is not null)
                {
                    var h = _bob.Height(QueryCoord(i, i0), QueryCoord(j, j0), kind == MeshKind.Sea);
                    bobHeight![(j + 1) * m + i + 1] = h;
                    validGrid[(j + 1) * m + i + 1] = h != Hole;
                    continue;
                }
                validGrid[(j + 1) * m + i + 1] = gi >= 0 && gj >= 0 && gi <= _gridTotal && gj <= _gridTotal
                                                 && _coverage.Covered(Coord(gi), Coord(gj), kind);
            }
        bool Valid(int gi, int gj)
        {
            int li = gi - i0 + 1, lj = gj - j0 + 1;
            return li >= 0 && lj >= 0 && li < m && lj < m && validGrid[lj * m + li];
        }

        // research: BOB's own grid for this mesh (export_bob_grids.py) replaces heights and interior validity
        float[]? bobGrid = null;
        if (Environment.GetEnvironmentVariable("ATLASWH3_GMESH_BOB_GRIDS") is { Length: > 0 } gridDir
            && File.Exists(Path.Combine(gridDir, $"{kind}_{row}_{col}.bob.bin")))
        {
            var raw = File.ReadAllBytes(Path.Combine(gridDir, $"{kind}_{row}_{col}.bob.bin"));
            bobGrid = new float[raw.Length / 4];
            Buffer.BlockCopy(raw, 0, bobGrid, 0, raw.Length);
            for (var j = 0; j < n; j++)
                for (var i = 0; i < n; i++)
                {
                    validGrid[(j + 1) * m + i + 1] = bobGrid[j * n + i] != Hole;
                    if (bobHeight is not null) bobHeight[(j + 1) * m + i + 1] = bobGrid[j * n + i];
                }
        }

        var any = false;
        for (var j = 0; j < n; j++)
        for (var i = 0; i < n; i++)
        {
            var k = j * n + i;
            int gi = i0 + i, gj = j0 + j;
            x[k] = Position(gi);
            z[k] = Position(gj);
            flags[k] = 2;
            if (!Valid(gi, gj)) { y[k] = Hole; continue; }
            any = true;
            y[k] = bobGrid is not null ? bobGrid[k] : bobHeight is not null ? bobHeight[(j + 1) * m + i + 1] : sampler.Height(x[k], z[k]);
            byte f = 2;
            // BOB (FUN_18016b2f0 / FUN_1801471b0) probes the 8 points at ±d around the vertex's query position
            var qx = QueryCoord(i, i0);
            var qz = QueryCoord(j, j0);
            var sea = kind == MeshKind.Sea;
            bool Probe(float px, float pz) => _bob!.Height(px, pz, sea) != Hole;
            if (_bob is not null && (BobGlobalHeight.Variant & 512) == 0)
            {
                var d = _bobProbe;
                if (!(Probe(qx - d, qz) && Probe(qx, qz - d) && Probe(d + qx, qz) && Probe(qx, d + qz) && Probe(qx - d, qz - d)
                      && Probe(d + qx, qz - d) && Probe(qx - d, d + qz) && Probe(d + qx, d + qz))) f = 0;
            }
            else
                for (var dj = -1; dj <= 1 && f == 2; dj++)
                    for (var di = -1; di <= 1; di++)
                        if ((di != 0 || dj != 0) && !Valid(gi + di, gj + dj)) { f = 0; break; }
            if (i == 0 || j == 0 || i == n - 1 || j == n - 1)
            {
                var count = _bob is not null && (BobGlobalHeight.Variant & 512) == 0
                    ? (Probe(qx, qz - _bobProbe) ? 1 : 0) + (Probe(qx, _bobProbe + qz) ? 1 : 0) + (Probe(qx - _bobProbe, qz) ? 1 : 0) + (Probe(_bobProbe + qx, qz) ? 1 : 0)
                    : (Valid(gi, gj - 1) ? 1 : 0) + (Valid(gi, gj + 1) ? 1 : 0) + (Valid(gi - 1, gj) ? 1 : 0) + (Valid(gi + 1, gj) ? 1 : 0);
                f = 0;
                if (count == 3)
                {
                    var onX = i == 0 || i == n - 1;
                    if (((j != 0 && j != n - 1) || (i & 31) != 0) && (!onX || (j & 31) != 0)) f = 3;
                }
            }
            // sea meshes keep a 4-cell lattice (BOB's option flag in the grid lambda)
            if (kind == MeshKind.Sea && ((i & 3) == 0 || (j & 3) == 0)) f = 0;
            flags[k] = f;
        }
        if (!any) return null;
        if (Environment.GetEnvironmentVariable("ATLASWH3_GMESH_DUMP") is { Length: > 0 } dump)   // research: grids vs BOB's
        {
            Directory.CreateDirectory(dump);
            var bytes = new byte[n * n * 4];
            Buffer.BlockCopy(y, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(Path.Combine(dump, $"{kind}_{row}_{col}.y.bin"), bytes);
            File.WriteAllBytes(Path.Combine(dump, $"{kind}_{row}_{col}.flags.bin"), flags);
        }

        var normals = SobelNormals(y, n);
        var triangles = new List<int[]>();
        for (var j = 0; j < n - 1; j++)
            for (var i = 0; i < n - 1; i++)
            {
                int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                if (y[a] == Hole || y[b] == Hole || y[c] == Hole || y[d] == Hole) continue;
                triangles.Add([a, c, b]);
                triangles.Add([c, d, b]);
            }
        if (triangles.Count == 0) return null;
        var inputTriangles = triangles.Count;

        var merged = new TriangleMerger(x, y, z, normals, flags)
        {
            Trace = Environment.GetEnvironmentVariable("ATLASWH3_GMESH_TRACE") is { } tr && tr.Split(',').Contains($"{kind}_{row}_{col}")
                    && Environment.GetEnvironmentVariable("ATLASWH3_GMESH_DUMP") is { Length: > 0 } td
                ? TraceWriter(Path.Combine(td, $"{kind}_{row}_{col}.trace.txt")) : null,
        }.Run(triangles, float.TryParse(Environment.GetEnvironmentVariable("ATLASWH3_GMESH_FACTOR"), System.Globalization.CultureInfo.InvariantCulture, out var fac) ? fac : MergeFactor);
        if (Environment.GetEnvironmentVariable("ATLASWH3_GMESH_DUMP") is { Length: > 0 } dumpDir)
        {
            var flat = merged.SelectMany(t => t).Select(v => (uint)v).ToArray();
            var bytes = new byte[flat.Length * 4];
            Buffer.BlockCopy(flat, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(Path.Combine(dumpDir, $"{kind}_{row}_{col}.merged.bin"), bytes);
            var nb = new byte[normals.Length * 4];
            Buffer.BlockCopy(normals, 0, nb, 0, nb.Length);
            File.WriteAllBytes(Path.Combine(dumpDir, $"{kind}_{row}_{col}.normals.bin"), nb);
        }
        foreach (var t in merged) (t[0], t[1]) = (t[1], t[0]);

        // first-use renumbering
        var remap = new Dictionary<int, int>();
        var positions = new List<(float X, float Y, float Z)>();
        var indices = new List<int>();
        foreach (var t in merged)
            foreach (var q in t)
            {
                if (!remap.TryGetValue(q, out var id))
                {
                    id = remap.Count;
                    remap.Add(q, id);
                    positions.Add((x[q], y[q], z[q]));
                }
                indices.Add(id);
            }

        if (_bob is not null && (BobGlobalHeight.Variant & 8192) == 0)
            BobSkirts(n, i0, j0, y, flags, remap, kind == MeshKind.Sea, positions, indices);
        else
            AddSkirts(merged, n, i0, j0, x, y, z, Valid, positions, indices);
        // MESH_SPLITTER: one mesh per chunk of under 65,000 vertices, all in the file's LOD 0
        RigidModelV2? model = null;
        foreach (var (chunkPositions, chunkIndices) in SplitMesh(positions, indices))
        {
            var part = RigidModelV2.NewTerrainTile(kind == MeshKind.Sea);
            part.Vertices = RigidModelV2.PackPositions(chunkPositions);
            part.Indices = [.. chunkIndices.Select(i => checked((ushort)i))];
            if (_bob is not null)   // FUN_180124ed0 after MESH_SPLITTER: (1/total) · i0 · ext and ((cells + i0) / total) · ext
                part.SetTileBounds(1f / _gridTotal * i0 * _bobExtent, 1f / _gridTotal * j0 * _bobExtent,
                    ((float)_cells + i0) / _gridTotal * _bobExtent, ((float)_cells + j0) / _gridTotal * _bobExtent);
            else part.SetTileBounds(Coord(i0), Coord(j0), Coord(i0 + _cells), Coord(j0 + _cells));
            if (model is null) model = part;
            else model.MoreMeshes.Add(part);
        }

        Raster<ushort>? heights = null;
        float[]? header = null;
        if (kind == MeshKind.Land)
            (heights, header) = _bob is not null && (BobGlobalHeight.Variant & 16384) == 0
                ? BobHeightMap(positions, indices, model!.Bounds, n) : RasteriseSurface(merged, n, x, y, z);
        return new MeshResult(row, col, model!, heights, header, inputTriangles);
    }

    /// <summary>The vertex limit of BOB's MESH_SPLITTER.</summary>
    public const int SplitVertices = 65000;

    /// <summary>BOB's MESH_SPLITTER (tooldatabuilder FUN_1800d0f80): walk the triangles in order, renumbering vertices by
    /// first use into the current chunk; after a whole triangle, once the chunk has 65,000 vertices or more, start a new
    /// chunk (fresh numbering) with the next triangle. A mesh under the limit comes back unchanged (its vertices are
    /// already in first-use order).</summary>
    public static IEnumerable<(List<(float X, float Y, float Z)> Positions, List<int> Indices)> SplitMesh(
        List<(float X, float Y, float Z)> positions, List<int> indices)
    {
        if (positions.Count < SplitVertices)
        {
            yield return (positions, indices);
            yield break;
        }
        var remap = new Dictionary<int, int>();
        var chunkPositions = new List<(float, float, float)>();
        var chunkIndices = new List<int>();
        for (var t = 0; t + 2 < indices.Count; t += 3)
        {
            for (var k = 0; k < 3; k++)
            {
                var v = indices[t + k];
                if (!remap.TryGetValue(v, out var id))
                {
                    id = chunkPositions.Count;
                    remap.Add(v, id);
                    chunkPositions.Add(positions[v]);
                }
                chunkIndices.Add(id);
            }
            if (chunkPositions.Count >= SplitVertices && t + 3 < indices.Count)
            {
                yield return (chunkPositions, chunkIndices);
                remap = [];
                chunkPositions = [];
                chunkIndices = [];
            }
        }
        yield return (chunkPositions, chunkIndices);
    }

    private static Action<string> TraceWriter(string path)
    {
        File.WriteAllText(path, "");
        return line => File.AppendAllText(path, line + "\n");
    }

    private static float[] SobelNormals(float[] h, int n)
    {
        int[] k = [1, 0, -1, 2, 0, -2, 1, 0, -1];
        var normals = new float[n * n * 3];
        const float up = 1f / 0.33f;
        for (var j = 0; j < n; j++)
            for (var i = 0; i < n; i++)
            {
                float gx = 0, gy = 0;
                for (var r = 0; r < 3; r++)
                    for (var c = 0; c < 3; c++)
                    {
                        var w = k[r * 3 + c];
                        gx += h[Math.Clamp(j + r - 1, 0, n - 1) * n + Math.Clamp(i + c - 1, 0, n - 1)] * w;
                        // FUN_180134370: same row-by-row traversal as gx, transposed kernel
                        gy += h[Math.Clamp(j + r - 1, 0, n - 1) * n + Math.Clamp(i + c - 1, 0, n - 1)] * k[c * 3 + r];
                    }
                gx /= 8f;
                gy /= 8f;
                var inv = 1f / MathF.Sqrt(gx * gx + gy * gy + up * up);
                var o = (j * n + i) * 3;
                normals[o] = gx * inv;
                normals[o + 1] = gy * inv;
                normals[o + 2] = up * inv;
            }
        return normals;
    }

    /// <summary>BOB's skirts (FUN_180124ed0 after VERTEX_LIST_CLEANER): for every kept grid vertex with flag 0 or 3, walk
    /// +x and +z over vertices the cleaner dropped to the next kept one; if the edge's midpoint ±0.01 across it is a
    /// hole, add a double-sided quad 1.0 deep. Positions are the height-query coordinates.</summary>
    private void BobSkirts(int n, int i0, int j0, float[] y, byte[] flags, Dictionary<int, int> kept, bool sea,
        List<(float, float, float)> positions, List<int> indices)
    {
        const float probe = 0.01f, half = 0.5f;
        float H(float px, float pz) => _bob!.Height(px, pz, sea);
        bool Kept(int k) => kept.ContainsKey(k);
        static bool Edge(byte f) => f == 0 || f == 3;
        void Quad(float ax, float ay, float az, float bx, float by, float bz)
        {
            var s = positions.Count;
            positions.Add((ax, ay, az));
            positions.Add((bx, by, bz));
            positions.Add((ax, ay - SkirtDepth, az));
            positions.Add((bx, by - SkirtDepth, bz));
            foreach (var q in (ReadOnlySpan<int>)[0, 1, 2, 2, 1, 3, 1, 0, 2, 1, 2, 3]) indices.Add(s + q);
        }
        for (var row = 0; row < n; row++)
            for (var col = 0; col < n; col++)
            {
                var idx = row * n + col;
                if (!Kept(idx) || !Edge(flags[idx])) continue;
                float qx = QueryCoord(col, i0), qz = QueryCoord(row, j0), yv = y[idx];
                // +x: to the next kept vertex (n: a hole or an open map edge on the way)
                var kx = 1;
                if (col + 1 < n)
                    for (var k = 1; ; )
                    {
                        float px = QueryCoord(col + k, i0), pz = QueryCoord(row, j0);
                        if (H(px, pz) == Hole) { kx = n; break; }
                        float mz = (pz - qz) * half + qz, mx = (px - qx) * half + qx;
                        float hp = H(mx, mz + probe), hm = H(mx, mz - probe);
                        if ((row == 0 && hp == Hole) || (row == n - 1 && hm == Hole)) { kx = n; break; }
                        kx = k;
                        if (Kept(row * n + col + k)) break;
                        kx = ++k;
                        if (n <= k + col) break;
                    }
                // +z: likewise, but a hole on the way ends the run at the last dropped vertex
                var kz = 1;
                if (row + 1 < n)
                    for (int k = 1, prev = 0; ; )
                    {
                        float px = QueryCoord(col, i0), pz = QueryCoord(row + k, j0);
                        if (H(px, pz) == Hole) { kz = prev == 0 ? n : prev; break; }
                        float mx = (px - qx) * half + qx, mz = (pz - qz) * half + qz;
                        float hp = H(mx + probe, mz), hm = H(mx - probe, mz);
                        if ((col == 0 && hp == Hole) || (col == n - 1 && hm == Hole)) { kz = prev == 0 ? n : prev; break; }
                        kz = k;
                        if (Kept((row + k) * n + col)) break;
                        prev = k;
                        kz = ++k;
                        if (n <= k + row) break;
                    }
                var e = col + kx;
                if (e < n)
                {
                    float px = QueryCoord(e, i0), pz = QueryCoord(row, j0);
                    float mz = (pz - qz) * half + qz, mx = (px - qx) * half + qx;
                    if (Edge(flags[row * n + e]) && (H(mx, mz + probe) == Hole || H(mx, mz - probe) == Hole))
                        Quad(qx, yv, qz, px, y[row * n + e], pz);
                }
                e = row + kz;
                if (e < n)
                {
                    float px = QueryCoord(col, i0), pz = QueryCoord(e, j0);
                    float mx = (px - qx) * half + qx, mz = (pz - qz) * half + qz;
                    if (Edge(flags[e * n + col]) && (H(mx + probe, mz) == Hole || H(mx - probe, mz) == Hole))
                        Quad(qx, yv, qz, px, y[e * n + col], pz);
                }
            }
    }

    /// <summary>Double-sided vertical quads under boundary edges that border a hole or the map edge.</summary>
    private void AddSkirts(List<int[]> surface, int n, int i0, int j0, float[] x, float[] y, float[] z,
        Func<int, int, bool> valid, List<(float, float, float)> positions, List<int> indices)
    {
        var edgeUse = new Dictionary<(int, int), int>();
        foreach (var t in surface)
            for (var e = 0; e < 3; e++)
            {
                int a = t[e], b = t[(e + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
            }
        foreach (var t in surface)
            for (var e = 0; e < 3; e++)
            {
                int a = t[e], b = t[(e + 1) % 3];
                if (edgeUse[a < b ? (a, b) : (b, a)] != 1) continue;
                if (OnSeam(a, b, n, i0, j0, valid)) continue;
                var s = positions.Count;
                positions.Add((x[a], y[a], z[a]));
                positions.Add((x[b], y[b], z[b]));
                positions.Add((x[a], y[a] - SkirtDepth, z[a]));
                positions.Add((x[b], y[b] - SkirtDepth, z[b]));
                foreach (var q in (ReadOnlySpan<int>)[0, 1, 2, 2, 1, 3, 1, 0, 2, 1, 2, 3]) indices.Add(s + q);
            }
    }

    /// <summary>A boundary edge on the mesh perimeter whose outside neighbour quad is solid ground in the next mesh.</summary>
    private static bool OnSeam(int a, int b, int n, int i0, int j0, Func<int, int, bool> valid)
    {
        int ai = a % n, aj = a / n, bi = b % n, bj = b / n;
        (int di, int dj) outward;
        if (ai == bi && (ai == 0 || ai == n - 1)) outward = (ai == 0 ? -1 : 1, 0);
        else if (aj == bj && (aj == 0 || aj == n - 1)) outward = (0, aj == 0 ? -1 : 1);
        else return false;
        return valid(i0 + ai + outward.di, j0 + aj + outward.dj) && valid(i0 + bi + outward.di, j0 + bj + outward.dj);
    }

    /// <summary>BOB's land_mesh_N.compressed_map (FUN_180124ed0 → WARSCAPE::rasterise_max_heights, as for river height
    /// patches): the final mesh (skirts included) mapped to an n × n field over the model's box, max height per pixel,
    /// −50 where nothing covers; u16 = trunc((h − lo) / (hi − lo) · 65535), header (0, lo, 0, 0, hi, 0).</summary>
    private static (Raster<ushort>, float[]) BobHeightMap(List<(float X, float Y, float Z)> positions, List<int> indices,
        float[] bounds, int size)
    {
        const float invalid = -50f;
        var field = new float[size * size];
        Array.Fill(field, invalid);
        var count = positions.Count;
        var px = new float[count];
        var py = new float[count];
        var vy = new float[count];
        for (var i = 0; i < count; i++)
        {
            var (vx, h, vz) = positions[i];
            var u = (vx - bounds[0]) / (bounds[3] - bounds[0]) * size;
            var v = (vz - bounds[2]) / (bounds[5] - bounds[2]) * size;
            vy[i] = h;
            px[i] = h * 0f + u * 1f + v * 0f + 0f;
            py[i] = h * 0f + u * 0f + v * 1f + 0f;
        }
        for (var q = 0; q + 2 < indices.Count; q += 3)
        {
            int ia = indices[q], ib = indices[q + 1], ic = indices[q + 2];
            float axp = px[ia], ayp = py[ia], bxp = px[ib], byp = py[ib], cxp = px[ic], cyp = py[ic];
            var x0 = Math.Min((int)axp, Math.Min((int)bxp, (int)cxp)) - 1;
            var x1 = Math.Max((int)axp, Math.Max((int)bxp, (int)cxp)) + 1;
            var y0 = Math.Min((int)ayp, Math.Min((int)byp, (int)cyp)) - 1;
            var y1 = Math.Max((int)ayp, Math.Max((int)byp, (int)cyp)) + 1;
            if (x1 < 0 || size < x0 || y1 < 0 || size < y0) continue;
            x0 = Math.Max(x0, 0); y0 = Math.Max(y0, 0); x1 = Math.Min(x1, size); y1 = Math.Min(y1, size);
            var area = MathF.Abs((cxp - axp) * (byp - ayp) - (cyp - ayp) * (bxp - axp));
            for (var j = y0; j < y1; j++)
            {
                float fy = j;
                for (var i = x0; i < x1; i++)
                {
                    float fx = i;
                    if (!Rivers.BobRiver.Inside(fx, fy, axp, ayp, bxp, byp, cxp, cyp)) continue;
                    var inv = 2f / area;
                    var wc = MathF.Abs((fy - ayp) * (bxp - axp) - (fx - axp) * (byp - ayp)) * 0.5f * inv;
                    var wb = MathF.Abs((fy - ayp) * (cxp - axp) - (fx - axp) * (cyp - ayp)) * 0.5f * inv;
                    var wa = 1f - wb - wc;
                    var h = vy[ib] * wb + vy[ia] * wa + vy[ic] * wc;
                    ref var cell = ref field[j * size + i];
                    if (!(h <= cell)) cell = h;
                }
            }
        }
        float lo = field.Min(), hi = field.Max();
        var scale = 1f / (lo == hi ? 1f : hi - lo);
        var raster = new Raster<ushort>(size, size);
        for (var k = 0; k < field.Length; k++) raster.Data[k] = (ushort)(int)((field[k] - lo) * scale * 65535f);
        return (raster, [0, lo, 0, 0, hi, 0]);
    }

    /// <summary>Heights of the final surface at every grid point (barycentric), 0 where no triangle covers it.</summary>
    private static (Raster<ushort>, float[]) RasteriseSurface(List<int[]> surface, int n, float[] x, float[] y, float[] z)
    {
        var h = new float[n * n];
        var covered = new bool[n * n];
        var cell = x[1] - x[0];
        foreach (var t in surface)
        {
            int a = t[0], b = t[1], c = t[2];
            int ia = a % n, ja = a / n, ib = b % n, jb = b / n, ic = c % n, jc = c / n;
            var area = (double)(ib - ia) * (jc - ja) - (double)(ic - ia) * (jb - ja);
            if (area == 0) continue;
            for (var j = Math.Min(ja, Math.Min(jb, jc)); j <= Math.Max(ja, Math.Max(jb, jc)); j++)
                for (var i = Math.Min(ia, Math.Min(ib, ic)); i <= Math.Max(ia, Math.Max(ib, ic)); i++)
                {
                    var u = ((double)(ib - i) * (jc - j) - (double)(ic - i) * (jb - j)) / area;
                    var v = ((double)(ic - i) * (ja - j) - (double)(ia - i) * (jc - j)) / area;
                    var w = 1 - u - v;
                    if (u < -1e-9 || v < -1e-9 || w < -1e-9) continue;
                    var k = j * n + i;
                    h[k] = (float)(u * y[a] + v * y[b] + w * y[c]);
                    covered[k] = true;
                }
        }
        var max = float.MinValue;
        for (var k = 0; k < h.Length; k++) if (covered[k]) max = Math.Max(max, h[k]);
        const float lo = -50f;
        var raster = new Raster<ushort>(n, n);
        for (var k = 0; k < h.Length; k++)
            if (covered[k]) raster.Data[k] = (ushort)Math.Clamp(Math.Round((h[k] - lo) / (max - lo) * 65535), 1, 65535);
        return (raster, [0, lo, 0, 0, max, 0]);
    }
}
