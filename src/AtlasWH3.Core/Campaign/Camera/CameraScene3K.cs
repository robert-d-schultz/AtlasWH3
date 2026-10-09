using System.Buffers.Binary;
using AtlasWH3.Core.Campaign.Terrain;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Models;
using AtlasWH3.Formats.Packs;
using AtlasWH3.Formats.Props;

namespace AtlasWH3.Core.Campaign.Camera;

/// <summary>
/// Atlas3K's scene height for 3K's BOB camera height map (byte-identical on vanilla 3k_dlc07): global mesh blocks, tile
/// and prop height patches, and the tile terrain fallback (docs/native_campaign_build.md, camera_heightmap.png). WH3's
/// camera step no longer uses it (<see cref="CameraHeightmapStep"/>); the editors' ground height (<see
/// cref="Editing.GroundHeight"/>) still does until their WH3 port (Phase 5).
/// </summary>
public static class CameraScene3K
{
    /// <summary>Scene extents: x 0..tiles W · T, z 0..tiles H · T · 1.15476.</summary>
    public static float SceneWidth(int tilesW) => tilesW * 128f * (TileHfHeight.TileSize3K / 128f);
    public static float SceneDepth(int tilesH) => tilesH * 128f * (TileHfHeight.TileSize3K / 128f) * CameraHeightField.ZScale;

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
