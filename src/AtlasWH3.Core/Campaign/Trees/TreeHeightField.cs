using System.Globalization;
using System.Text;
using System.Xml.Linq;
using AtlasWH3.Core.Campaign.Props;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Models;
using AtlasWH3.Formats.Packs;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign.Trees;

/// <summary>
/// The height BOB's Campaign Trees gives a tree: QTU::TerrainSurface::height for a campaign project (qttoolutility,
/// disassembled 2026-10-08), all float32 in BOB's order:
///  - height_split asks the project's height provider (vtable 0x5fec10, +0x70 = 0x1370f0) for (hf, lf). For a campaign
///    map that is the nearest full_logic_map texel: z' = z / 1.15476, u = x / width, v = z' / depth, texel
///    (trunc(w · u), trunc(h · v)) with row 0 the south edge, value raw · (1/65535) · (hi − lo) + lo (warscape
///    COMPRESSED_MAP::value_as_float), 0 outside [0, 1]; hf = 0. width is the .terry's world_width (1367.4 on Old
///    World, where map_data.esf says 1367.396: 99.96% of its trees bit-exact against 95.9%), depth = h · (width / w),
///    i.e. square texels in tile space.
///  - sample_height_patches(x, z, lf) (0x12f980): the highest height patch under the point, else lf (see
///    <see cref="Patch"/>); y = that + hf.
/// The patches are every entity of every layer file with ECPropHeightPatch apply_height_patch (and not
/// for_camera_height_map_only), visible, exported or not (terrain_surface_for, 0x12ffa0). BOB looks their files up
/// under working_data; here they come from the packs (vanilla plus the map's mod packs), so the user no longer has to
/// export them (campaign_tools height_patches.py).
/// </summary>
public sealed class TreeHeightField
{
    public const float ZScale = 1.15476f;

    private readonly ushort[] _logic;
    private readonly int _w, _h;
    private readonly float _lo, _range, _width, _depth;
    private readonly List<Patch>[] _cells;
    private readonly float _cellSize;
    private readonly int _cellsX, _cellsZ;

    public IReadOnlyList<Patch> Patches { get; }

    /// <param name="logic">full_logic_map.compressed_map (row 0 = south).</param>
    /// <param name="worldWidth">The project's world_width (.terry), else the map's width (map_data.esf).</param>
    public TreeHeightField(CompressedMap.Map logic, float worldWidth, IReadOnlyList<Patch> patches)
    {
        _logic = logic.Raster.Data;
        _w = logic.Raster.Width;
        _h = logic.Raster.Height;
        _lo = logic.Header[1];
        _range = logic.Header[4] - logic.Header[1];
        _width = worldWidth;
        _depth = _h * (worldWidth / _w);
        Patches = patches;

        // a coarse grid over the patches' world boxes, so a tree only tests the patches near it
        _cellSize = 8f;
        _cellsX = (int)(worldWidth / _cellSize) + 1;
        _cellsZ = (int)(_depth * ZScale / _cellSize) + 1;
        _cells = new List<Patch>[_cellsX * _cellsZ];
        foreach (var p in patches)
            for (var cz = Cell(p.AabbMinZ, _cellsZ); cz <= Cell(p.AabbMaxZ, _cellsZ); cz++)
            for (var cx = Cell(p.AabbMinX, _cellsX); cx <= Cell(p.AabbMaxX, _cellsX); cx++)
                (_cells[cz * _cellsX + cx] ??= []).Add(p);
    }

    private int Cell(float v, int count) => Math.Clamp((int)MathF.Floor(v / _cellSize), 0, count - 1);

    /// <summary>TerrainSurface::height at a world point.</summary>
    public float Height(float x, float z)
    {
        var lf = Terrain(x, z);
        var result = lf;
        if (x < 0 || z < 0) return result;
        var cx = (int)(x / _cellSize);
        var cz = (int)(z / _cellSize);
        if (cx >= _cellsX || cz >= _cellsZ || _cells[cz * _cellsX + cx] is not { } near) return result;
        foreach (var p in near)
            if (p.Sample(x, z, lf) is { } h && h > result) result = h;
        return result;
    }

    /// <summary>The provider's terrain height (lf): the nearest full_logic_map texel in tile space.</summary>
    public float Terrain(float x, float z)
    {
        var u = x / _width;
        var v = z / ZScale / _depth;
        if (!(u >= 0f && v >= 0f && u <= 1f && v <= 1f)) return 0f;
        var c = Math.Min((int)(_w * u), _w - 1);
        var r = Math.Min((int)(_h * v), _h - 1);
        return _logic[r * _w + c] * (1f / 65535f) * _range + _lo;
    }

    /// <summary>
    /// One height patch as TerrainSurface::add_height_patch (0x12f090) stores it: the x/z part of the entity's world
    /// matrix (columns 0 and 2 of rows 0 and 2, translation x and z) inverted by 0xfea70 into world → model space, the
    /// patch's model bounds (its compressed map's header x/z), scale y = |matrix column 1|, position y, and whether a
    /// material has add_terrain_height.
    /// Sampling (sample_height_patches): inside the world box and the model bounds (edges inclusive),
    /// f = (l − min) / (max − min) · size per axis, texel trunc(f) (bilinear towards +1, clamped to the last texel),
    /// v ≥ 0 only (raw 0 = −50 = no patch), height v · sy + py, plus lf if add_terrain_height.
    /// </summary>
    public sealed class Patch
    {
        public required string Model { get; init; }
        public float ScaleY, PosY, A, B, C, D, Tx, Tz, MinX, MinZ, MaxX, MaxZ;
        public float AabbMinX, AabbMinZ, AabbMaxX, AabbMaxZ;
        public bool AddTerrainHeight;
        public required float[] Values { get; init; }
        public int W, H;

        public static Patch Create(string model, double[] m, float px, float py, float pz, CompressedMap.Map map, bool add)
        {
            float r00 = (float)m[0], r01 = (float)m[1], r02 = (float)m[2], r11 = (float)m[4], r20 = (float)m[6], r21 = (float)m[7],
                r22 = (float)m[8];
            if (px == 0f) px = 0f;
            if (py == 0f) py = 0f;
            if (pz == 0f) pz = 0f;
            // 0x135bc0: scale y, translation y, then the inverse of (r00, r02, r20, r22 | px, pz) (0xfea70)
            var inv = 1f / (r00 * r22 - r02 * r20);
            float ia = inv * r22, ib = -(inv * r02), ic = -(inv * r20), id = inv * r00;
            float lo = map.Header[1], range = map.Header[4] - map.Header[1];
            var raw = map.Raster.Data;
            var values = new float[raw.Length];
            for (var i = 0; i < raw.Length; i++) values[i] = raw[i] * (1f / 65535f) * range + lo;
            var p = new Patch
            {
                Model = model, Values = values, W = map.Raster.Width, H = map.Raster.Height,
                ScaleY = MathF.Sqrt(r11 * r11 + r01 * r01 + r21 * r21), PosY = py,
                A = ia, B = ib, C = ic, D = id, Tx = -(ib * pz + ia * px), Tz = -(id * pz + ic * px),
                MinX = map.Header[0], MinZ = map.Header[2], MaxX = map.Header[3], MaxZ = map.Header[5],
                AddTerrainHeight = add,
            };
            // world box of the model bounds (aabb_transformed_xz), only a prefilter: the model-bounds test decides
            float[] xs = new float[4], zs = new float[4];
            var k = 0;
            foreach (var lx in new[] { p.MinX, p.MaxX })
            foreach (var lz in new[] { p.MinZ, p.MaxZ })
            {
                xs[k] = r00 * lx + r02 * lz + px;
                zs[k++] = r20 * lx + r22 * lz + pz;
            }
            (p.AabbMinX, p.AabbMaxX, p.AabbMinZ, p.AabbMaxZ) = (xs.Min(), xs.Max(), zs.Min(), zs.Max());
            return p;
        }

        /// <summary>The patch height at a world point, or null where the patch does not answer.</summary>
        public float? Sample(float x, float z, float lf)
        {
            if (x < AabbMinX || x > AabbMaxX || z < AabbMinZ || z > AabbMaxZ) return null;
            var lx = z * B + x * A + Tx;
            var lz = z * D + x * C + Tz;
            if (lx < MinX || lx > MaxX || lz < MinZ || lz > MaxZ) return null;
            var fx = (lx - MinX) / (MaxX - MinX) * W;
            var fz = (lz - MinZ) / (MaxZ - MinZ) * H;
            int ix = (int)fx, iz = (int)fz;
            fx -= ix;
            fz -= iz;
            if (ix < 0 || iz < 0 || ix >= W || iz >= H) return null;
            int x1 = Math.Min(ix + 1, W - 1), z1 = Math.Min(iz + 1, H - 1);
            float s1 = Values[iz * W + ix], s2 = Values[iz * W + x1], s3 = Values[z1 * W + ix], s4 = Values[z1 * W + x1];
            var ax = MathF.Max(fx, 0f);
            float far;
            if (ax > 1f) { ax = 1f; far = s4; }
            else far = (s4 - s3) * ax + s3;
            var near = (s2 - s1) * ax + s1;
            var az = MathF.Max(fz, 0f);
            if (az > 1f) az = 1f;
            var v = (far - near) * az + near;
            if (!(v >= 0f)) return null;
            var h = v * ScaleY + PosY;
            return AddTerrainHeight ? h + lf : h;
        }
    }

    /// <summary>
    /// The height patches of a Terry project's layer files, their models, maps and materials read from
    /// <paramref name="packs"/>. A model whose patch is not in the packs is skipped (listed in <paramref name="notes"/>).
    /// </summary>
    public static List<Patch> LoadPatches(TerryProject project, PackSet packs, List<string> notes)
    {
        var models = new Dictionary<string, (CompressedMap.Map Map, bool Add)?>(StringComparer.OrdinalIgnoreCase);
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var patches = new List<Patch>();
        int nested = 0, cameraOnly = 0;
        foreach (var layer in project.Layers().Where(l => l.IsFile))
        {
            var path = project.LayerFilePath(layer.Id);
            if (!File.Exists(path)) continue;
            foreach (var e in LayerDocument.Load(path).AllEntityElements)
            {
                var hp = e.Element("ECPropHeightPatch");
                if (hp is null || (string?)hp.Attribute("apply_height_patch") != "true") continue;
                if ((string?)hp.Attribute("for_camera_height_map_only") == "true") { cameraOnly++; continue; }
                // an entity inside a group would need its parents' transforms; none of the fixtures has one
                if (e.Parent?.Name != "entities") { nested++; continue; }
                var model = (string?)e.Element("ECMesh")?.Attribute("model_path");
                var t = e.Element("ECTransform");
                if (model is null || t is null) continue;
                if (!models.TryGetValue(model, out var info)) models[model] = info = LoadModel(model, packs);
                if (info is not { } m) { missing.Add(model); continue; }
                var p = LayerDocument.ReadVector((string)t.Attribute("position")!);
                var r = LayerDocument.ReadVector((string)t.Attribute("rotation")!);
                var s = LayerDocument.ReadVector((string)t.Attribute("scale")!);
                var matrix = QtuTransform.Matrix((float)r[0], (float)r[1], (float)r[2], (float)s[0], (float)s[1], (float)s[2],
                    (float)p[0], (float)p[1], (float)p[2]);
                patches.Add(Patch.Create(model, matrix, (float)p[0], (float)p[1], (float)p[2], m.Map, m.Add));
            }
        }
        notes.Add($"height patches: {patches.Count} props, {models.Values.Count(v => v is not null)} models" +
                  (cameraOnly > 0 ? $", {cameraOnly} camera-only skipped" : ""));
        if (nested > 0) notes.Add($"{nested} height-patched props inside groups are not applied (not supported yet)");
        if (missing.Count > 0) notes.Add($"no height patch in the packs for {missing.Count} model(s): {string.Join(", ", missing.Take(8))}" +
                                         (missing.Count > 8 ? ", ..." : ""));
        return patches;
    }

    /// <summary>A prop model's patch (the .compressed_map next to its rigid_model_v2) and whether one of its wsmodel's
    /// materials sets add_terrain_height (read from the .xml.material, the file the packs carry for every material).</summary>
    private static (CompressedMap.Map Map, bool Add)? LoadModel(string model, PackSet packs)
    {
        var geometry = model;
        var materials = new List<string>();
        if (model.EndsWith(".wsmodel", StringComparison.OrdinalIgnoreCase))
        {
            if (packs.TryRead(model) is not { } ws || RigidModelGeometry.WsModelGeometryPath(ws) is not { } g) return null;
            geometry = g;
            try
            {
                materials = XDocument.Parse(Encoding.UTF8.GetString(ws).TrimStart('﻿')).Descendants("material")
                    .Select(m => m.Value.Trim()).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (System.Xml.XmlException) { }
        }
        if (packs.TryRead(geometry + ".compressed_map") is not { } bytes) return null;
        var add = materials.Any(m => AddsTerrainHeight(packs.TryRead(m)));
        return (CompressedMap.Decode(bytes), add);
    }

    private static bool AddsTerrainHeight(byte[]? material)
    {
        if (material is null) return false;
        try
        {
            var param = XDocument.Parse(Encoding.UTF8.GetString(material).TrimStart('﻿')).Descendants("param")
                .FirstOrDefault(p => (string?)p.Element("name") == "add_terrain_height");
            return param is not null && float.TryParse((string?)param.Element("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                   && v != 0f;
        }
        catch (System.Xml.XmlException) { return false; }
    }
}
