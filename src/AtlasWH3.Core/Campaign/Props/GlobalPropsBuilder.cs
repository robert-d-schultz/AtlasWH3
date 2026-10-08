using AtlasWH3.Formats;
using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using AtlasWH3.Formats.Models;
using AtlasWH3.Formats.Packs;
using AtlasWH3.Formats.Props;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign.Props;

/// <summary>
/// Native global_props.bin from the AK layers (BOB "Terry file"), game-valid. Layout recovered from vanilla 3k_dlc07:
///  - each object's region is the map.hex region under its entity position (HexRegionLookup, as BOB), or its
///    layer's region when there is no map.hex; each object goes to the deepest quadtree cell that holds its bounds
///    (7 levels, each a row-major 2^L x 2^L grid over the world, row 0 = north; cell id = cells of shallower levels
///    + row·2^L + col) and to a season bucket 16 + bits (spring 1, summer 2, autumn 4, winter 8; no season mask = 31)
///  - bucket bodies bmd_objects.&lt;region&gt;.&lt;cell&gt;.&lt;bucket&gt;.bin hold the objects; cell bodies
///    bmd_objects.&lt;region&gt;.&lt;cell&gt;.bin reference their buckets (campaign mask 1); the root bmd_objects.bin
///    references every cell body with its region key
///  - records are built from vanilla's most common record of each type (from the game packs), with the fields the
///    layers define replaced: paths, transforms, tags, seasons, snow/destruction/shroud visibility, decal flags,
///    shadows, height patches, light and sound parameters
/// </summary>
public sealed class GlobalPropsBuilder
{
    public const string VanillaGlobalProps = "terrain/campaigns/3k_dlc07_main_map/global_props.bin";

    /// <param name="EntityX">The entity's ECTransform position, which BOB's region lookup uses (differs from X/Z for
    /// river models, placed at the origin, and polygon meshes).</param>
    private sealed record Obj(string Kind, double X, double Z, double Radius, string SeasonMask, Func<BmdBody, byte[]> Build, string? PropPath,
                              double? EntityX = null, double? EntityZ = null, int? Bucket = null, float[]? Box = null)
    {
        /// <summary>The entity's id (hex u64): BOB writes each body's records in ascending id order.</summary>
        public bool IsDecal { get; init; }

        public ulong Id { get; init; }

        public string Tags { get; init; } = "";

        /// <summary>Running entity number in layer read order (research trace).</summary>
        public int Seq { get; init; }
    }

    private readonly Templates _t;
    private int _seq;
    private readonly PackSet _packs;
    private readonly double _worldW, _worldH;
    public List<string> Notes { get; } = [];

    /// <summary>Campaign prefab library: Prefab instances in the layers are flattened into their entities (the game
    /// has no campaign prefab .bmd files for them to reference). Null = instances are skipped with a note.</summary>
    public PrefabLibrary? Prefabs { get; init; }

    /// <summary>Rectangle the bmd quadtree covers (minX, minZ, maxX, maxZ); BOB uses the map bounds' x range and the hex
    /// grid's z extent (<see cref="HexRegionLookup.QuadRoot"/>). Null = (0, 0, world width, world height).</summary>
    public (float X0, float Z0, float X1, float Z1)? QuadRoot { get; init; }

    /// <summary>Optional trace: one line per object written (entry, kind, entity id, season mask).</summary>
    public Action<string>? Debug { get; init; }

    /// <summary>River records reference river_N by the entity name (CA's shipped vanilla files) instead of BOB's numbering
    /// (<see cref="Rivers.RiverNumbering.Bob"/>, used whenever a region lookup is given). Keep in step with RiversStep.</summary>
    public bool RiverNumbersByName { get; init; }

    private Dictionary<string, int>? _riverNumbers;

    public GlobalPropsBuilder(PackSet packs, double worldWidth, double worldHeight)
    {
        _packs = packs;
        _worldW = worldWidth;
        _worldH = worldHeight;
        var vanilla = packs.TryRead(VanillaGlobalProps) ?? throw new FileNotFoundException("Vanilla global_props.bin not found in the game packs (templates).");
        _t = Templates.From(GlobalProps.Read(vanilla));
    }

    /// <summary>(entry name, body) pairs in vanilla order: each cell's buckets, the cell, and the root last.</summary>
    /// <param name="regionAt">BOB's region for an entity position (HexRegionLookup); when it returns null, or is not given,
    /// the layer's region is used.</param>
    public List<(string Name, byte[] Body)> Build(string mapName, IEnumerable<(string Region, string LayerPath)> layers,
                                                  Func<double, double, string?>? regionAt = null)
    {
        var prefix = $"terrain/campaigns/{mapName}/bmd_objects";
        var layerList = layers.ToList();
        layers = layerList;
        _riverNumbers = regionAt is not null && !RiverNumbersByName
            ? Rivers.RiverNumbering.Bob(Rivers.RiverNumbering.Read(layerList.Select(l => l.LayerPath)), regionAt)
            : null;
        var entries = new List<(string, byte[])>();
        var root = BmdBody.Dynamic(_t.Framing);
        // several layers can end up in one region (e.g. every layer the map has no region for)
        var byRegion = new List<(string Region, List<Obj> Objects)>();
        foreach (var (region, layerPath) in layers)
        {
            var objects = ReadLayer(mapName, layerPath);
            foreach (var group in objects.GroupBy(o => regionAt?.Invoke(o.EntityX ?? o.X, o.EntityZ ?? o.Z) ?? region))
            {
                var existing = byRegion.FindIndex(r => r.Region == group.Key);
                if (existing >= 0) byRegion[existing].Objects.AddRange(group);
                else byRegion.Add((group.Key, group.ToList()));
            }
        }
        var cells = new List<(string Region, IGrouping<int, Obj> Cell)>();
        foreach (var (region, objects) in byRegion)
        {
            var outside = objects.Where(o => CellOf(o) < 0).ToList();
            if (outside.Count > 0) Notes.Add($"{outside.Count} objects in {region} reach outside the quadtree root: dropped (as BOB)");
            cells.AddRange(objects.Where(o => CellOf(o) >= 0).GroupBy(CellOf).Select(g => (region, g)));
        }
        // BOB's entry order: quadtree cells ascending; each cell keeps its regions in a CA_STD hash map keyed by region name,
        // filled as the scene is walked by ascending entity id, and is written in that map's list order (CA::murmur_hash
        // buckets, 1 -> 2b+1 growth: CaHash.HashMapOrder). Matches all 2,417 main190 cells (research/props/cell_map_order.py).
        var ordered = cells.GroupBy(c => c.Cell.Key).OrderBy(g => g.Key).SelectMany(g =>
        {
            var byName = g.ToDictionary(c => c.Region, StringComparer.Ordinal);
            return CaHash.HashMapOrder(g.OrderBy(c => c.Cell.Min(o => o.Id)).Select(c => c.Region)).Select(r => byName[r]);
        });
        foreach (var (region, cell) in ordered)
        {
            {
                var cellBody = BmdBody.Dynamic(_t.Framing);
                // bob_terrain FUN_1800660a0: only ECMesh and ECVFX entities are bucketed by season mask; composite scenes, lights,
                // sounds, probes and polygon meshes go to bucket 0 (file suffix 16) (checked against BOB 2026-10-04)
                foreach (var bucket in cell.GroupBy(o => o.Bucket ?? (o.Kind is "prop" or "vfx" ? Bucket(o.SeasonMask) : 16)).OrderBy(g => g.Key))
                {
                    var body = BmdBody.Dynamic(_t.Framing);
                    // BOB iterates the scene's entities by ascending id (every main190 body checked 2026-10-04)
                    var name = $"{prefix}.{region}.{cell.Key}.{bucket.Key}.bin";
                    // decals (ECDecal) come before the ECMesh props, each by id (2026-10-05: the 2 main190 bodies with both)
                    foreach (var o in bucket.OrderBy(o => KindRank(o.Kind)).ThenBy(o => o.IsDecal ? 0 : 1).ThenBy(o => o.Id))
                    {
                        Add(body, o);
                        Debug?.Invoke($"{name},{o.Kind},{o.Id:x15},\"{o.SeasonMask}\",\"{o.Tags}\",{o.Seq}");
                    }
                    entries.Add((name, body.ToBytes()));
                    cellBody.Nested.Add(BmdRecords.Nested(_t.Nested, name, 0, region));   // vanilla and BOB: 0 for every bucket
                }
                var cellName = $"{prefix}.{region}.{cell.Key}.bin";
                entries.Add((cellName, cellBody.ToBytes()));
                root.Nested.Add(BmdRecords.Nested(_t.RootNested, cellName, 0, region));
            }
        }
        entries.Add(($"{prefix}.bin", root.ToBytes()));
        return entries;
    }

    /// <summary>Order in which BOB registers a body's enum tags and season codes (preamble order) by object kind.</summary>
    private static int KindRank(string kind) => Environment.GetEnvironmentVariable("ATLASWH3_GP_RANK") is { Length: > 0 } r
        ? r.Split(',').ToList().IndexOf(kind) : kind switch { "scene" => 0, "light" => 1, "prop" => 2, "vfx" => 3, _ => 4 };

    private void Add(BmdBody body, Obj o)
    {
        var record = o.Build(body);
        switch (o.Kind)
        {
            case "prop":
                var index = body.PropPaths.IndexOf(o.PropPath!);
                if (index < 0) { index = body.PropPaths.Count; body.PropPaths.Add(o.PropPath!); }
                BitConverter.TryWriteBytes(record.AsSpan(BmdRecords.PropPathIndex), (uint)index);
                body.Props.Add(record);
                break;
            case "vfx": body.Vfx.Add(record); break;
            case "light": body.PointLights.Add(record); break;
            case "scene": body.CompositeScenes.Add(record); break;
            case "sound": body.Sounds.Add(record); break;
            case "probe": body.LightProbes.Add(record); break;
            case "poly": body.PolyMeshes.Add(record); break;
        }
    }

    private int CellOf(Obj o)
    {
        if (o.Radius >= 1e8) return 0;                                         // rivers: the root
        var b = o.Box ?? [(float)o.X, (float)o.Z, (float)o.X, (float)o.Z];
        return BobCell(b[0], b[1], b[2], b[3]);
    }

    /// <summary>bob_terrain FUN_180064280 / FUN_18005f880: -1 when the box leaves the root (BOB skips the object), else
    /// the deepest of 7 levels reached by descending into the first child (NW, NE, SW, SE; row 0 = north) whose rectangle
    /// holds the box (min &gt;= child min, max &lt;= child max). Child rectangles split at (max - min) * 0.5 + min in float32
    /// (FUN_180060e10).</summary>
    public int BobCell(float x0, float z0, float x1, float z1)
    {
        var (rx0, rz0, rx1, rz1) = QuadRoot ?? (0f, 0f, (float)_worldW, (float)_worldH);
        if (x0 < rx0 || z0 < rz0 || x1 > rx1 || z1 > rz1) return -1;
        int row = 0, col = 0, level = 0;
        for (var l = 1; l <= 6; l++)
        {
            var mx = (rx1 - rx0) * 0.5f + rx0;
            var mz = (rz1 - rz0) * 0.5f + rz0;
            (float, float, float, float)[] kids = [(rx0, mz, mx, rz1), (mx, mz, rx1, rz1), (rx0, rz0, mx, mz), (mx, rz0, rx1, mz)];
            var k = -1;
            for (var i = 0; i < 4; i++)
            {
                var (a0, b0, a1, b1) = kids[i];
                if (x0 >= a0 && z0 >= b0 && x1 <= a1 && z1 <= b1) { k = i; break; }
            }
            if (k < 0) break;
            (rx0, rz0, rx1, rz1) = kids[k];
            row = row * 2 + (k >= 2 ? 1 : 0); col = col * 2 + (k & 1); level = l;
        }
        var first = 0;
        for (var l = 0; l < level; l++) first += 1 << (2 * l);
        return first + row * (1 << level) + col;
    }

    /// <summary>bob_terrain FUN_18005e050 for an ECMesh entity: the model AABB (a .wsmodel path, or a model that doesn't
    /// load, uses the default [-1, 1]^3), its 8 corners through the world matrix in float32 in BOB's order, x/z min/max.</summary>
    private float[] MeshBox(string modelPath, double[] m, (double X, double Y, double Z) p)
    {
        var (mn, mx) = ModelAabb(modelPath);
        float m0 = (float)m[0], m1 = (float)m[1], m2 = (float)m[2], m3 = (float)p.X;
        float m8 = (float)m[6], m9 = (float)m[7], m10 = (float)m[8], m11 = (float)p.Z;
        var minX = mn[0] * m0 + mn[1] * m1 + mn[2] * m2 + m3;
        var minZ = mn[0] * m8 + mn[1] * m9 + mn[2] * m10 + m11;
        float maxX = minX, maxZ = minZ;
        for (var c = 1; c < 8; c++)
        {
            float x = mn[0], y = mx[1], z = mx[2];
            switch (c)
            {
                case 1: x = mx[0]; y = mn[1]; z = mn[2]; break;
                case 2: x = mx[0]; z = mn[2]; break;
                case 3: z = mn[2]; break;
                case 4: y = mn[1]; break;
                case 5: x = mx[0]; y = mn[1]; break;
                case 6: x = mx[0]; break;
            }
            var wz = m9 * y + m8 * x + m10 * z + m11;
            var wx = m0 * x + m1 * y + m2 * z + m3;
            if (wx <= minX) minX = wx;
            if (wz <= minZ) minZ = wz;
            if (maxX <= wx) maxX = wx;
            if (maxZ <= wz) maxZ = wz;
        }
        return [minX, minZ, maxX, maxZ];
    }

    private readonly Dictionary<string, (float[] Min, float[] Max)> _aabb = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>QTU::WarscapeModelData::aabb: the union of the rmv2 mesh header boxes over every LOD (matches BOB's cells
    /// better than LOD0 vertices); .wsmodel paths and unreadable models give [-1, 1]^3 (FUN_18005e050 skips the model load
    /// for the "wsmodel" suffix).</summary>
    private (float[] Min, float[] Max) ModelAabb(string path)
    {
        if (_aabb.TryGetValue(path, out var hit)) return hit;
        (float[], float[]) box = ([-1f, -1f, -1f], [1f, 1f, 1f]);
        if (!path.EndsWith("wsmodel", StringComparison.Ordinal) && _packs.TryRead(path.ToLowerInvariant()) is { } bytes)
        {
            try
            {
                var rm = AtlasWH3.Formats.Models.RigidModel.Read(bytes);
                var meshes = rm.Lods.SelectMany(l => l.Meshes).ToList();
                // a model whose vertex stride doesn't fit its declared format (water_lily_1/3: format 12, stride 20) fails
                // to load in BOB, which then boxes it as [-1, 1]^3 (Frida on FUN_18005e050, 2026-10-05)
                if (meshes.Any(q => q.PositionsOnly)) meshes = [];
                if (meshes.Count > 0)
                    box = ([meshes.Min(q => q.BoundsMin[0]), meshes.Min(q => q.BoundsMin[1]), meshes.Min(q => q.BoundsMin[2])],
                           [meshes.Max(q => q.BoundsMax[0]), meshes.Max(q => q.BoundsMax[1]), meshes.Max(q => q.BoundsMax[2])]);
            }
            catch (Exception) { }
        }
        _aabb[path] = box;
        return box;
    }

    /// <summary>Deepest quadtree cell whose rectangle contains the object's circle.</summary>
    public int Cell(double x, double z, double radius)
    {
        for (var level = 6; level >= 1; level--)
        {
            var n = 1 << level;
            double cw = _worldW / n, ch = _worldH / n;
            int c0 = (int)Math.Floor((x - radius) / cw), c1 = (int)Math.Floor((x + radius) / cw);
            int r0 = (int)Math.Floor((_worldH - (z + radius)) / ch), r1 = (int)Math.Floor((_worldH - (z - radius)) / ch);
            if (c0 != c1 || r0 != r1 || c0 < 0 || r0 < 0 || c0 >= n || r0 >= n) continue;
            var first = 0;
            for (var l = 0; l < level; l++) first += 1 << (2 * l);
            return first + r0 * n + c0;
        }
        return 0;
    }

    /// <summary>Season bucket: 16 + spring 1 + summer 2 + autumn 4 + winter 8; no season mask = 31.</summary>
    public static int Bucket(string seasonMask)
    {
        var names = seasonMask.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0) return 31;
        var bits = 0;
        foreach (var n in names)
            bits |= n switch { "season_spring" => 1, "season_summer" => 2, "season_autumn" => 4, "season_winter" => 8, _ => 0 };
        return 16 + bits;
    }

    private List<Obj> ReadLayer(string mapName, string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root!;
        var entities = root.Element("entities")?.Elements("entity").ToList() ?? [];
        // tags: tag-layer entities (ECLayerExportTags) own their members through the Logical association; a tag layer
        // can itself sit inside another tag layer, whose tags then apply too
        var TagsFor = LayerDocument.TagResolver(root);

        // Prefab instances become the entities they stand for, with the instance's tags added to their own.
        var flat = new List<(XElement Entity, string Tags)>();
        foreach (var e in entities)
        {
            if (e.Element("ECLayer") is not null) continue;
            var tagsOf = TagsFor((string?)e.Attribute("id") ?? "");
            if (PrefabExpander.KeyOf(e) is not { } key) { flat.Add((e, tagsOf)); continue; }
            if (Prefabs is null) { Notes.Add($"prefab instance '{key}' in {Path.GetFileName(path)} skipped (no prefab library)"); continue; }
            var missing = new List<string>();
            var expanded = PrefabExpander.Expand(e, Prefabs, recursive: true, missing);
            foreach (var m in missing.Distinct()) Notes.Add($"prefab '{m}' (in {Path.GetFileName(path)}) not found in {Prefabs.Root}; skipped");
            flat.AddRange(expanded.Select(x => (x.Entity, PrefabExpander.Union(tagsOf, x.Tags))));
        }

        var objects = new List<Obj>();
        foreach (var (e, tags) in flat)
        {
            var before = objects.Count;
            _seq++;
            ReadEntity(e, tags);
            var id = ulong.TryParse((string?)e.Attribute("id"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;
            for (var i = before; i < objects.Count; i++) objects[i] = objects[i] with { Id = id, Tags = tags, Seq = _seq };
        }
        return objects;

        void ReadEntity(XElement e, string tags)
        {
            var tr = Transform(e.Element("ECTransform"));
            var cp = e.Element("ECCampaignProperties");
            var seasons = (string?)cp?.Attribute("season_mask") ?? "";
            bool Cp(string name, bool def) => cp?.Attribute(name) is { } a ? (string)a == "true" : def;

            if (e.Element("ECRiver") is not null)
            {
                var name = (string?)e.Attribute("name") ?? "river_0";
                var number = _riverNumbers is not null && _riverNumbers.TryGetValue(name, out var bobNumber)
                    ? bobNumber : Rivers.RiverNumbering.ByName(name, 0);
                var riverPath = $"terrain/campaigns/{mapName}/models/river_{number}.wsmodel";
                // vanilla and BOB: river flag set, cast shadow on, season bucket 16
                var river = PropObj(riverPath, (0, 0, 0), Identity, 1e9, tags, seasons, decal: false, applyToTerrain: true,
                    applyToObjects: false, Cp, castShadow: true, hasHp: false, applyHp: false);
                objects.Add(river with
                {
                    Build = b => { var rec = river.Build(b); rec[BmdRecords.PropRiver] = 1; return rec; },
                    EntityX = tr.Position.X, EntityZ = tr.Position.Z, Bucket = 16,
                });
                return;
            }
            if (e.Element("ECPropMesh") is not null || e.Element("ECDecal") is not null)
            {
                var decal = e.Element("ECDecal");
                var model = (string?)(decal ?? e.Element("ECMesh"))?.Attribute("model_path") ?? "";
                if (model.Length == 0) return;
                var rs = e.Element("ECMeshRenderSettings");
                var hp = e.Element("ECPropHeightPatch");
                // cell by position only: BOB's main190 cells match a radius-0 point for 92% of objects; the model radius
                // put mountain props in much coarser cells than BOB's, and the game didn't draw them (checked in game)
                var propObj = PropObj(model, tr.Position, tr.Matrix, 0, tags, seasons, decal is not null,
                    (string?)decal?.Attribute("apply_to_terrain") != "false", (string?)decal?.Attribute("apply_to_objects") == "true", Cp,
                    (string?)rs?.Attribute("cast_shadow") != "false", (string?)hp?.Attribute("has_height_patch") == "true",
                    (string?)hp?.Attribute("apply_height_patch") == "true");
                // BOB bounds (FUN_18005e050): ECMesh entities use the transformed model box; decals (no ECMesh) a point
                // FUN_1800660a0: an entity without ECMesh (a decal) isn't season-bucketed: bucket 16
                objects.Add(decal is null && e.Element("ECMesh") is not null ? propObj with { Box = MeshBox(model, tr.Matrix, tr.Position) }
                    : e.Element("ECMesh") is null ? propObj with { Bucket = 16, Build = decal is null ? propObj.Build : b => DecalExtras(propObj.Build(b), decal) } : propObj);
                return;
            }
            if (e.Element("ECVFX") is { } vfx)
            {
                var name = (string?)vfx.Attribute("vfx") ?? "";
                var instance = (string?)vfx.Attribute("instance_name") ?? "";
                objects.Add(new Obj("vfx", tr.Position.X, tr.Position.Z, 0, seasons, b =>
                {
                    var (f, m) = b.EncodeTags(tags);
                    return BmdRecords.Vfx(_t.Vfx, name, tr.Matrix, tr.Position, instance, b.EncodeSeasons(seasons), f, m);
                }, null));
                return;
            }
            if (e.Element("ECPointLight") is { } light)
            {
                var c = Floats((string?)light.Attribute("colour") ?? "255 255 255 255");
                var speed = Floats((string?)light.Attribute("animation_speed_scale") ?? "0 0");
                var anim = (string?)light.Attribute("animation_type") switch { "LAT_RADIUS_SIN" => (byte)1, "LAT_RADIUS_SIN_SIN" => (byte)2, _ => (byte)0 };
                float A(string n, float d) => light.Attribute(n) is { } a ? float.Parse((string)a, CultureInfo.InvariantCulture) : d;
                var falloff = (string?)light.Attribute("falloff_type") ?? "";
                var probesOnly = (string?)light.Attribute("for_light_probes_only") == "true";
                // FUN_18005e050: radius x (sum of the 3 matrix column lengths / 3) x 0.5 around the position
                var mm = tr.Matrix;
                float M(int k) => (float)mm[k];
                // column norms with rows in BOB's order 1, 0, 2 (row-major world matrix)
                var avg = (MathF.Sqrt(M(3) * M(3) + M(0) * M(0) + M(6) * M(6)) + MathF.Sqrt(M(4) * M(4) + M(1) * M(1) + M(7) * M(7))
                           + MathF.Sqrt(M(5) * M(5) + M(2) * M(2) + M(8) * M(8))) * 0.33333334f;
                var lr = A("radius", 1) * avg * 0.5f;                  // the record's radius is radius x avg too (as BOB)
                float lx = (float)tr.Position.X, lz = (float)tr.Position.Z;
                objects.Add(new Obj("light", tr.Position.X, tr.Position.Z, 0, seasons, b =>
                {
                    var (f, m) = b.EncodeTags(tags);
                    return BmdRecords.PointLight(_t.Light, tr.Position, A("radius", 1) * avg, (c[0] * (1f / 255f), c[1] * (1f / 255f), c[2] * (1f / 255f)),
                        A("colour_scale", 1), anim, speed.ElementAtOrDefault(0), speed.ElementAtOrDefault(1), A("colour_min", 0),
                        A("random_offset", 0), falloff, probesOnly, f, m, b.EncodeSeasons(seasons));
                }, null) { Box = [lx - lr, lz - lr, lx + lr, lz + lr] });
                return;
            }
            if (e.Element("ECCompositeScene") is { } scene)
            {
                var path2 = (string?)scene.Attribute("path") ?? "";
                var autoplay = (string?)scene.Attribute("autoplay") != "false";
                objects.Add(new Obj("scene", tr.Position.X, tr.Position.Z, 0, seasons, b =>
                {
                    var (f, m) = b.EncodeTags(tags);
                    var rec = BmdRecords.CompositeScene(_t.Scene, tr.Matrix, tr.Position, path2, f, m, b.EncodeSeasons(seasons));
                    rec[^1] = autoplay ? (byte)1 : (byte)0;                 // last byte: ECCompositeScene autoplay (as BOB)
                    return rec;
                }, null));
                return;
            }
            if (e.Element("ECSoundMarker") is { } sound)
            {
                var key = (string?)sound.Attribute("key") ?? "";
                var cloud = e.Element("ECPointCloud");
                var sphere = e.Element("ECSphere");
                var pts = cloud is null ? [tr.Position]
                    : cloud.Descendants("point").Select(p => (Pt(tr.Position.X, p, "x"), Pt(tr.Position.Y, p, "y"), Pt(tr.Position.Z, p, "z"))).ToList();
                // vanilla uses SST_POINT, SST_MULTI_POINT and SST_LINE_LIST (the last doesn't survive in Terry layers)
                var shape = cloud is not null ? "SST_MULTI_POINT" : "SST_POINT";
                float? radius = sphere is null ? null : float.Parse((string?)sphere.Attribute("radius") ?? "0", CultureInfo.InvariantCulture);
                var template = shape == "SST_MULTI_POINT" ? _t.SoundMulti : _t.SoundPoint;
                objects.Add(new Obj("sound", tr.Position.X, tr.Position.Z, 0, "", _ => BmdRecords.Sound(template, key, shape, pts, radius), null));
                return;
            }
            if (e.Element("ECLightProbe") is not null)
            {
                var radius = float.Parse((string?)e.Element("ECSphere")?.Attribute("radius") ?? "1", CultureInfo.InvariantCulture);
                objects.Add(new Obj("probe", tr.Position.X, tr.Position.Z, 0, "", _ => BmdRecords.LightProbe(_t.Probe, tr.Position, radius), null));
                return;
            }
            if (e.Element("ECPolygonMesh") is { } poly && _t.Poly is not null)
            {
                var material = (string?)poly.Attribute("material") ?? "";
                var outline = e.Descendants("point").Select(p => (A3(p, "x"), A3(p, "y"))).ToList();
                if (outline.Count < 3) return;
                var vertices = outline.Select(p => (p.Item1, tr.Position.Y, p.Item2)).ToList();
                var indices = Triangulate(outline);
                // quadtree cell: BOB boxes a polygon mesh as the point at its entity transform (main190: position 0,0 -> the
                // south-west level-6 cell 5397), not its outline (2026-10-05)
                objects.Add(new Obj("poly", tr.Position.X, tr.Position.Z, 0, "",
                    _ => BmdRecords.PolyMesh(_t.Poly, vertices, indices, material), null, tr.Position.X, tr.Position.Z));
            }
        }

        Obj PropObj(string model, (double X, double Y, double Z) position, double[] matrix, double radius, string tags, string seasons,
            bool decal, bool applyToTerrain, bool applyToObjects, Func<string, bool, bool> cp, bool castShadow, bool hasHp, bool applyHp) =>
            new("prop", position.X, position.Z, radius, seasons, b =>
            {
                var (f, m) = b.EncodeTags(tags);
                var rec = BmdRecords.Prop(decal ? _t.Decal : _t.Prop, 0, f, m, matrix, position, decal,
                    cp("visible_inside_snow_region", true), cp("visible_outside_snow_region", true),
                    cp("visible_inside_destruction_region", true), cp("visible_outside_destruction_region", true),
                    b.EncodeSeasons(seasons), cp("visible_in_seen_shroud", true), cp("visible_in_unseen_shroud", false),
                    castShadow, hasHp, applyHp);
                if (decal)
                {
                    rec[BmdRecords.PropApplyToTerrain] = applyToTerrain ? (byte)1 : (byte)0;   // decal bytes checked against BOB 2026-10-05
                    rec[BmdRecords.PropApplyToObjects] = applyToObjects ? (byte)1 : (byte)0;
                }
                if (model.Contains("_anim", StringComparison.OrdinalIgnoreCase)) rec[BmdRecords.PropAnimated] = 1;
                return rec;
            }, model) { IsDecal = decal };
    }

    /// <summary>Decal parallax scale (bytes 80..83) and render above snow (byte 103) from the ECDecal, as BOB.</summary>
    private static byte[] DecalExtras(byte[] rec, XElement decal)
    {
        var parallax = float.Parse((string?)decal.Attribute("parallax_scale") ?? "0", CultureInfo.InvariantCulture);
        BitConverter.TryWriteBytes(rec.AsSpan(BmdRecords.PropDecalParallaxScale), parallax);
        rec[BmdRecords.PropRenderAboveSnow] = (string?)decal.Attribute("render_above_snow") == "true" ? (byte)1 : (byte)0;
        return rec;
    }

    private static readonly double[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];

    private static (double[] Matrix, (double X, double Y, double Z) Position, double MaxScale) Transform(XElement? t)
    {
        var p = Floats((string?)t?.Attribute("position") ?? "0 0 0");
        var r = Floats((string?)t?.Attribute("rotation") ?? "0 0 0");
        var s = Floats((string?)t?.Attribute("scale") ?? "1 1 1");
        // BOB's float matrix bit for bit (QTU::ECTransform quaternion path)
        var matrix = QtuTransform.Matrix(r[0], r[1], r[2], s[0], s[1], s[2], p[0], p[1], p[2]);
        // ECTransform::update_transform's translation is (position + delta) + pivot terms: a -0 component comes out +0
        return (matrix, (p[0] + 0f, p[1] + 0f, p[2] + 0f), Math.Max(Math.Abs(s[0]), Math.Max(Math.Abs(s[1]), Math.Abs(s[2]))));
    }

    private static float[] Floats(string s) =>
        s.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();

    private static double A3(XElement p, string name) => double.Parse((string?)p.Attribute(name) ?? "0", CultureInfo.InvariantCulture);

    /// <summary>A point-cloud point in world space, added in float32.</summary>
    private static double Pt(double origin, XElement p, string name) =>
        (float)origin + float.Parse((string?)p.Attribute(name) ?? "0", CultureInfo.InvariantCulture);


    /// <summary>Ear-clipping triangulation of a simple polygon (x, z outline), in BOB's order: the scan starts at
    /// vertex 1 and stays on the same slot after clipping an ear (the next vertex), each ear written (next, ear,
    /// previous), the last three vertices as (V2, V1, V0). Byte-exact on the main190 polygon mesh (2026-10-05).</summary>
    private static List<ushort> Triangulate(List<(double X, double Z)> pts)
    {
        var idx = Enumerable.Range(0, pts.Count).ToList();
        double Area() { double a = 0; for (var i = 0; i < idx.Count; i++) { var p = pts[idx[i]]; var q = pts[idx[(i + 1) % idx.Count]]; a += p.X * q.Z - q.X * p.Z; } return a; }
        if (Area() < 0) idx.Reverse();
        var result = new List<ushort>();
        var slot = 1;
        var misses = 0;
        while (idx.Count > 3 && misses < idx.Count)
        {
            slot %= idx.Count;
            int a = idx[(slot + idx.Count - 1) % idx.Count], b = idx[slot], c = idx[(slot + 1) % idx.Count];
            var cross = (pts[b].X - pts[a].X) * (pts[c].Z - pts[a].Z) - (pts[b].Z - pts[a].Z) * (pts[c].X - pts[a].X);
            if (cross > 0 && !idx.Any(k => k != a && k != b && k != c && InTri(pts[k], pts[a], pts[b], pts[c])))
            {
                result.AddRange([(ushort)c, (ushort)b, (ushort)a]);
                idx.RemoveAt(slot);
                misses = 0;
            }
            else { slot++; misses++; }
        }
        if (idx.Count == 3) result.AddRange([(ushort)idx[2], (ushort)idx[1], (ushort)idx[0]]);
        return result;

        static bool InTri((double X, double Z) p, (double X, double Z) a, (double X, double Z) b, (double X, double Z) c)
        {
            double d1 = (p.X - b.X) * (a.Z - b.Z) - (a.X - b.X) * (p.Z - b.Z);
            double d2 = (p.X - c.X) * (b.Z - c.Z) - (b.X - c.X) * (p.Z - c.Z);
            double d3 = (p.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (p.Z - a.Z);
            return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
        }
    }

    /// <summary>Most common vanilla record of each kind, plus a framing body whose preamble lists every enum type.</summary>
    private sealed record Templates(BmdBody Framing, byte[] Prop, byte[] Decal, byte[] Vfx, byte[] Light, byte[] Scene,
        byte[] SoundPoint, byte[] SoundMulti, byte[] Probe, byte[]? Poly, byte[] Nested, byte[] RootNested)
    {
        public static Templates From(GlobalProps vanilla)
        {
            var bodies = vanilla.Bodies().Select(b => (b.Name, Body: BmdBody.Parse(b.Body))).ToList();
            byte[] MostCommon(IEnumerable<byte[]> records, Func<byte[], string> shape) =>
                records.GroupBy(shape).OrderByDescending(g => g.Count()).First().First();
            var props = bodies.SelectMany(b => b.Body.Props).ToList();
            string PropShape(byte[] r) => Convert.ToHexString(r, 72, 33) + Convert.ToHexString(r, BmdRecords.PropHeadSize, r.Length - BmdRecords.PropHeadSize);
            var prop = MostCommon(props.Where(r => r[BmdRecords.PropDecal] == 0), PropShape);
            var decal = MostCommon(props.Where(r => r[BmdRecords.PropDecal] == 1), PropShape);
            byte[] First(Func<BmdBody, IEnumerable<byte[]>> pick, Func<byte[], bool>? where = null) =>
                bodies.SelectMany(b => pick(b.Body)).First(r => where?.Invoke(r) ?? true);
            bool HasShape(byte[] r, string shape) => System.Text.Encoding.UTF8.GetString(r).Contains(shape, StringComparison.Ordinal);

            // every enum type, values merged in first-seen order
            var types = new List<(string Name, List<string> Values)>();
            foreach (var (_, body) in bodies)
                foreach (var (name, first, count) in body.EnumTypes)
                {
                    var t = types.FirstOrDefault(x => x.Name == name);
                    if (t.Values is null) types.Add(t = (name, []));
                    foreach (var v in body.EnumValues.Skip(first).Take(count)) if (!t.Values.Contains(v)) t.Values.Add(v);
                }
            var framing = BmdBody.WithEnumTypes(bodies.First(b => b.Body.Props.Count > 0).Body,
                types.Select(t => (t.Name, (IReadOnlyList<string>)t.Values)).ToList());
            var root = bodies.Single(b => b.Name.EndsWith("/bmd_objects.bin", StringComparison.Ordinal)).Body;
            var cellNested = bodies.Where(b => !b.Name.EndsWith("/bmd_objects.bin", StringComparison.Ordinal)).SelectMany(b => b.Body.Nested).First();
            return new Templates(framing, prop, decal, First(b => b.Vfx), First(b => b.PointLights), First(b => b.CompositeScenes),
                First(b => b.Sounds, r => HasShape(r, "SST_POINT") && !HasShape(r, "SST_MULTI_POINT")), First(b => b.Sounds, r => HasShape(r, "SST_MULTI_POINT")),
                First(b => b.LightProbes), bodies.SelectMany(b => b.Body.PolyMeshes).FirstOrDefault(), cellNested, root.Nested[0]);
        }
    }
}
