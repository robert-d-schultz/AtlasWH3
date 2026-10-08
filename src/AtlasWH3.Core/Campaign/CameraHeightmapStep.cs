using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using AtlasWH3.Core.Campaign.Camera;
using AtlasWH3.Core.Campaign.Terrain;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Models;
using AtlasWH3.Formats.Packs;
using AtlasWH3.Formats.Props;

namespace AtlasWH3.Core.Campaign;

/// <summary>
/// campaign_maps\&lt;map&gt;\camera_heightmap.png (BOB "Generate Camera Height Map", TOOLDATABUILDER::generate_camera_height_map),
/// reproduced from the decompiled tool and Frida dumps of BOB's own sample buffer:
///  - settings from raw_data\terrain\campaigns\rules.bob [Terrain]: cam_hmap_resolution_scale, cam_hmap_samples_per_wu,
///    cam_hmap_apply_blur, cam_hmap_blur_kernel, cam_hmap_standard_drv (BOB writes nothing useful without them);
///  - a (tiles W · res) × (tiles H · res) grid over the scene (x 0..W·T, z 0..H·T·1.15476); each cell is the max of the
///    scene height (<see cref="CameraHeightField"/>) over n × n points from the cell's min corner (n = ceil(extent ·
///    samples per unit); BOB runs the z count for x too) plus the cell centre, starting from −1;
///  - pixel = ceil(max(h / highest, 0) · 65535), PNG row 0 = the north edge; tEXt height_scale = "%f" of highest / 65535;
///    written as libpng does (<see cref="PngLib"/>).
/// </summary>
public sealed class CameraHeightmapStep : ICampaignBuildStep
{
    /// <summary>World units per lf pixel (x, z); the same on every 3K map (vanilla 595.1 / 7136, 541.786 / 5620).</summary>
    public const double PixelSizeX = 595.1 / 7136, PixelSizeZ = 541.78619 / 5620;
    /// <summary>Source u16 → world y (fitted exactly on the vanilla land meshes).</summary>
    public const double HeightStep = 0.000218712, HeightOffset = -3.12725;

    public string Name => "camera_heightmap";
    public string ReplacesBobAction => "Terrain / Generate Camera Height Map";
    public IReadOnlyList<string> DependsOn => ["global_props", "rivers", "tile_list"];

    /// <summary>BOB's CAMERA_HEIGHT_MAP_SETTINGS (rules.bob [Terrain] cam_hmap_*).</summary>
    public sealed record Settings(float ResolutionScale, int SamplesPerUnit, bool ApplyBlur, int BlurKernel, float StandardDeviation, bool FromRules);

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx)
    {
        var missing = new List<string>();
        if (!Directory.Exists(ctx.Paths.GameDataDir)) missing.Add($"missing game data folder {ctx.Paths.GameDataDir}");
        return missing;
    }

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        var settings = ReadSettings(Path.Combine(ctx.Paths.AssemblyKitRoot, "raw_data", "terrain", "campaigns", "rules.bob"));
        if (!settings.FromRules)
            notes.Add("rules.bob has no cam_hmap_* settings (BOB then writes no usable map): using resolution 1, 4 samples per unit, no blur");
        if (settings.ApplyBlur) notes.Add("cam_hmap_apply_blur is on: BOB's blur is not ported, the map is written unblurred");

        ctx.Log("scene...");
        var field = BuildField(ctx, new GameFiles(ctx), notes, out var tilesW, out var tilesH);

        var w = (int)MathF.Ceiling(tilesW * settings.ResolutionScale);
        var h = (int)MathF.Ceiling(tilesH * settings.ResolutionScale);
        ctx.Log($"sampling {w}x{h}...");
        var cells = Sample(field, w, h, SceneWidth(tilesW), SceneDepth(tilesH), settings.SamplesPerUnit);

        var highest = float.MinValue;
        foreach (var c in cells) if (highest < c) highest = c;
        var (raster, scale) = Encode(cells, w, h, highest);
        Directory.CreateDirectory(ctx.CampaignMapOutDir);
        var outPath = Path.Combine(ctx.CampaignMapOutDir, "camera_heightmap.png");
        File.WriteAllBytes(outPath, PngLib.Encode16(raster, new Dictionary<string, string> { ["height_scale"] = scale }));
        notes.Add($"{w}x{h}, highest sampled height {highest:F6} (height_scale {scale})");
        return new StepResult(Name, [outPath], notes, sw.Elapsed);
    }

    /// <summary>Scene extents: x 0..tiles W · T, z 0..tiles H · T · 1.15476.</summary>
    public static float SceneWidth(int tilesW) => tilesW * 128f * (TileHfHeight.TileSize3K / 128f);
    public static float SceneDepth(int tilesH) => tilesH * 128f * (TileHfHeight.TileSize3K / 128f) * CameraHeightField.ZScale;

    public static Settings ReadSettings(string rulesBob)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(rulesBob))
            foreach (var line in File.ReadAllLines(rulesBob))
                if (line.Split('=', 2) is [var k, var v] && k.Trim().StartsWith("cam_hmap_", StringComparison.OrdinalIgnoreCase))
                    values[k.Trim()] = v.Trim();
        float F(string k, float d) => values.TryGetValue(k, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : d;
        bool B(string k) => values.TryGetValue(k, out var v) && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1");
        var fromRules = values.ContainsKey("cam_hmap_resolution_scale") && values.ContainsKey("cam_hmap_samples_per_wu");
        return new Settings(F("cam_hmap_resolution_scale", 1f), (int)F("cam_hmap_samples_per_wu", 4f), B("cam_hmap_apply_blur"),
            (int)F("cam_hmap_blur_kernel", 0f), F("cam_hmap_standard_drv", 0f), fromRules);
    }

    /// <summary>The per-cell maxima in BOB's order (row j = z index, south first).</summary>
    public static float[] Sample(CameraHeightField field, int w, int h, float sceneW, float sceneD, int samplesPerUnit)
    {
        var stepX = sceneW / w;
        var stepZ = sceneD / h;
        var halfX = stepX * 0.5f;
        var halfZ = stepZ * 0.5f;
        var cells = new float[w * h];
        Parallel.For(0, h, j =>
        {
            var cz = j * stepZ;
            var minZ = cz - halfZ;
            var maxZ = cz + halfZ;
            var dz = maxZ - minZ;
            var nz = (int)MathF.Ceiling(dz * samplesPerUnit);
            var sz = dz / nz;
            for (var u = 0; u < w; u++)
            {
                var cx = u * stepX;
                var minX = cx - halfX;
                var maxX = cx + halfX;
                var dx = maxX - minX;
                var nx = (int)MathF.Ceiling(dx * samplesPerUnit);
                var sx = dx / nx;
                var best = -1f;
                var z = minZ;
                for (var a = 0; a < nz; a++)
                {
                    var x = minX;
                    for (var b = 0; b < nz; b++)                // BOB: the inner loop also runs the z count
                    {
                        var v = field.Height(x, z);
                        if (best < v) best = v;
                        x += sx;
                    }
                    z += sz;
                }
                var centre = field.Height((maxX - minX) * 0.5f + minX, (maxZ - minZ) * 0.5f + minZ);
                if (best < centre) best = centre;
                cells[j * w + u] = best;
            }
        });
        return cells;
    }

    /// <summary>16-bit pixels (row 0 = north) and the height_scale text.</summary>
    public static (Raster<ushort> Raster, string Scale) Encode(float[] cells, int w, int h, float highest)
    {
        var raster = new Raster<ushort>(w, h);
        for (var j = 0; j < h; j++)
            for (var u = 0; u < w; u++)
            {
                var r = cells[j * w + u] / highest;
                raster.Data[(h - 1 - j) * w + u] = (ushort)MathF.Ceiling(MathF.Max(r, 0f) * 65535f);
            }
        var scale = ((double)(highest * (1f / 65535f))).ToString("F6", CultureInfo.InvariantCulture);
        return (raster, scale);
    }

    /// <summary>Row-major 3x3 rotation * scale from the stored Blender-style XYZ Euler angles (degrees) and scale:
    /// R = Rz * Ry * Rx, column i scaled by scale i (inverse of <see cref="PropTransform.FromColumns"/>).</summary>
    public static double[] Matrix(PropTransform t)
    {
        const double rad = Math.PI / 180;
        double ci = Math.Cos(t.RotX * rad), si = Math.Sin(t.RotX * rad);
        double cj = Math.Cos(t.RotY * rad), sj = Math.Sin(t.RotY * rad);
        double ch = Math.Cos(t.RotZ * rad), sh = Math.Sin(t.RotZ * rad);
        double cc = ci * ch, cs = ci * sh, sc = si * ch, ss = si * sh;
        // Blender eul_to_mat3: mat[col][row]
        double[,] col =
        {
            { cj * ch, cj * sh, -sj },
            { sj * sc - cs, sj * ss + cc, cj * si },
            { sj * cc + ss, sj * cs - sc, cj * ci },
        };
        double[] s = [t.ScaleX, t.ScaleY, t.ScaleZ];
        var m = new double[9];
        for (var row = 0; row < 3; row++)
            for (var c = 0; c < 3; c++)
                m[row * 3 + c] = col[c, row] * s[c];
        return m;
    }

    /// <summary>What BOB's scene loads: the build output (loose files under the target root) first, then the game packs.</summary>
    private sealed class GameFiles(CampaignBuildContext ctx)
    {
        public PackSet Packs { get; } = PackSet.OpenVanilla(ctx.Paths.GameDataDir);

        private string Loose(string internalPath) =>
            Path.Combine(ctx.TargetRoot, internalPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));

        public byte[]? Read(string internalPath) =>
            File.Exists(Loose(internalPath)) ? File.ReadAllBytes(Loose(internalPath)) : Packs.TryRead(internalPath);

        /// <summary>File names in a folder of the build output, else of the packs.</summary>
        public List<string> List(string folder)
        {
            var loose = Loose(folder);
            if (Directory.Exists(loose)) return Directory.EnumerateFiles(loose).Select(Path.GetFileName).OfType<string>().ToList();
            var prefix = PackFile.Normalize(folder.TrimEnd('/') + "/");
            return Packs.Packs.SelectMany(p => p.Entries.Keys)
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.IndexOf('\\', prefix.Length) < 0)
                .Select(k => k[prefix.Length..]).Distinct().ToList();
        }
    }

    /// <summary>The scene's height objects: global mesh blocks, height patches (rivers, tile props, global props) and the
    /// tile terrain for the fallback. <paramref name="globalProps"/> false leaves the map's own props' patches out (the
    /// ground props stand on, for seating them).</summary>
    public static CameraHeightField BuildField(CampaignBuildContext ctx, List<string> notes, out int tilesW, out int tilesH, bool globalProps = true) =>
        BuildField(ctx, new GameFiles(ctx), notes, out tilesW, out tilesH, globalProps);

    private static CameraHeightField BuildField(CampaignBuildContext ctx, GameFiles fs, List<string> notes, out int tilesW, out int tilesH,
                                                bool includeGlobalProps = true)
    {
        var dir = $"terrain/campaigns/{ctx.MapName}/";
        var maps = new Dictionary<string, CameraHeightField.HeightMap?>(StringComparer.OrdinalIgnoreCase);
        CameraHeightField.HeightMap? Map(string path)
        {
            if (maps.TryGetValue(path, out var m)) return m;
            try { m = fs.Read(path) is { } b ? CameraHeightField.HeightMap.From(CompressedMap.Decode(b)) : null; }
            catch (InvalidDataException) { m = null; }
            return maps[path] = m;
        }

        // tiles: the fallback terrain and the tile props
        var tl = TileList.Read(fs.Read(dir + "tile_list.bin") ?? throw new InvalidOperationException("no tile_list.bin in the build output or the packs"));
        tilesW = tl.Ints[1];
        tilesH = tl.Ints[2];
        var tileSize = TileHfHeight.TileSize3K;
        var prefix = PackFile.Normalize(TileDatabase.Folder);
        var db = TileDatabase.Load(fs.Packs.Packs.SelectMany(p => p.Entries.Keys).Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Distinct().Select(k => fs.Packs.TryRead(k)).OfType<byte[]>());
        var lf = fs.Read(dir + "lf_height_map.compressed_map") ?? throw new InvalidOperationException("no lf_height_map.compressed_map");
        var tiles = new TileHfHeight(tl, db, fs.Packs.TryRead, CompressedMap.Decode(lf), tileSize);

        // global mesh blocks
        var blocks = new List<CameraHeightField.Block>();
        foreach (var name in fs.List(dir + "global_meshes").Where(n => n.StartsWith("land_mesh_", StringComparison.OrdinalIgnoreCase)
                                                                      && n.EndsWith(".rigid_model_v2", StringComparison.OrdinalIgnoreCase)))
        {
            var stem = name[..^".rigid_model_v2".Length];
            var model = fs.Read(dir + "global_meshes/" + name);
            if (model is null || Map(dir + "global_meshes/" + stem + ".compressed_map") is not { } map) continue;
            float B(int i) => BinaryPrimitives.ReadSingleLittleEndian(model.AsSpan(0xC0 + 4 * i));
            blocks.Add(new CameraHeightField.Block(stem, B(0), B(2), B(3), B(5), map));
        }

        // height patches
        var patches = new List<CameraHeightField.Patch>();
        var models = new Dictionary<string, (float[] Bounds, CameraHeightField.HeightMap Map)?>(StringComparer.OrdinalIgnoreCase);
        (float[] Bounds, CameraHeightField.HeightMap Map)? Model(string path)
        {
            if (models.TryGetValue(path, out var r)) return r;
            r = null;
            var geometry = path;
            if (path.EndsWith(".wsmodel", StringComparison.OrdinalIgnoreCase))
                geometry = fs.Read(path) is { } ws ? RigidModelGeometry.WsModelGeometryPath(ws) ?? "" : "";
            if (geometry.Length > 0 && Map(geometry + ".compressed_map") is { } map && fs.Read(geometry) is { } rm)
                r = (CameraHeightField.ModelBounds(rm), map);
            return models[path] = r;
        }

        var rivers = 0;
        if (fs.Read(dir + "height_patches/rivers.height_patch_collection") is { } hpc)
            foreach (var p in HeightPatchCollection.Read(hpc).Patches)
                if (Map(p.Path.Replace("//", "/")) is { } map)
                {
                    patches.Add(CameraHeightField.RiverPatch(p.Path, p.MinX, p.MinZ, p.MaxX, p.MaxZ, map));
                    rivers++;
                }

        // the props of each placed tile's bmd_data.bin, lifted onto the terrain under them
        var tileProps = 0;
        var bmds = new Dictionary<string, List<(string Path, float[] Raw)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in tl.Records)
        {
            var key = TileDatabase.NormalisePath(tl.Paths[(int)rec.Path]);
            if (!db.TryGetValue(key, out var tile)) continue;
            if (!bmds.TryGetValue(key, out var props))
            {
                props = [];
                if (fs.Packs.TryRead(key.Replace('\\', '/') + "bmd_data.bin") is { } b)
                {
                    var ro = GlobalProps.ReadBody(b);
                    for (var i = 0; i < ro.Props.Count && i < ro.PropMatrices.Count; i++)
                        if (ro.Props[i].HasHeightPatch && Model(ro.Props[i].Path) is not null) props.Add((ro.Props[i].Path, ro.PropMatrices[i]));
                }
                bmds[key] = props;
            }
            foreach (var (path, raw) in props)
            {
                var (bounds, map) = Model(path)!.Value;
                var m = CameraHeightField.TileProp(rec, tile.Width, tile.Height, tileSize, raw);
                m[7] += tiles.Height(m[3], m[11] / CameraHeightField.ZScale);
                patches.Add(CameraHeightField.MakePatch(path, m, bounds, map));
                tileProps++;
            }
        }

        var globalProps = 0;
        if (includeGlobalProps && fs.Read(dir + "global_props.bin") is { } gpb)
            foreach (var region in GlobalProps.Read(gpb).ReadRegions(ctx.MapName))
                for (var i = 0; i < region.Props.Count && i < region.PropMatrices.Count; i++)
                {
                    var p = region.Props[i];
                    if (!p.HasHeightPatch || Model(p.Path) is not { } md) continue;
                    patches.Add(CameraHeightField.MakePatch(p.Path, CameraHeightField.FromStored(region.PropMatrices[i]), md.Bounds, md.Map));
                    globalProps++;
                }

        // the scene quadtree is built over (−1, −1)..(scene width, scene depth); a patch whose AABB leaves that box is
        // never stored (18 giant mountain patches on vanilla). The root's final bounds grow with the tiles (595.18524 on
        // vanilla, read from BOB's memory)
        float[] root = [-1f, -1f, SceneWidth(tilesW), SceneDepth(tilesH)];
        // fallback tiles: the quadtree holds the global_map\tile_list.bin records with flag bit 0 (instance flag 0x100)
        var flags = fs.Read(dir + "global_map/tile_list.bin") is { } gml && TileList.Read(gml) is { } g && g.Records.Count == tl.Records.Count ? g : tl;
        (int, int)? Size(int r)
        {
            var rec = tl.Records[r];
            if (!db.TryGetValue(TileDatabase.NormalisePath(tl.Paths[(int)rec.Path]), out var t)) return null;
            return (t.Width, t.Height);
        }
        var customs = new Dictionary<uint, float[]?>();
        float[]? Custom(int r)
        {
            var path = tl.Records[r].Path;
            if (customs.TryGetValue(path, out var c)) return c;
            c = null;
            var folder = TileDatabase.NormalisePath(tl.Paths[(int)path]).Replace('\\', '/');
            if (fs.Packs.TryRead(folder + "custom_mesh.wsmodel") is { } ws && RigidModelGeometry.WsModelGeometryPath(ws) is { } geo
                && fs.Packs.TryRead(geo) is { } rm)
            {
                try
                {
                    float[] b = [float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue];
                    foreach (var m in RigidModel.Read(rm).Lods[0].Meshes)
                        for (var i = 0; i < 3; i++) { b[i] = MathF.Min(b[i], m.BoundsMin[i]); b[3 + i] = MathF.Max(b[3 + i], m.BoundsMax[i]); }
                    c = b;
                }
                catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentOutOfRangeException) { }
            }
            return customs[path] = c;
        }
        var tree = new TileQuadtree(tl, Size, r => (flags.Records[r].Flag & 1) != 0, Custom, tileSize, SceneWidth(tilesW), SceneDepth(tilesH));
        var field = new CameraHeightField(blocks, patches, root, tiles, tree.Reaches);
        notes.Add($"{blocks.Count} global mesh blocks; height patches: {rivers} river, {tileProps} tile prop, {globalProps} global prop " +
                  $"({field.DroppedPatches} outside the scene, ignored as BOB does)");
        return field;
    }
}
