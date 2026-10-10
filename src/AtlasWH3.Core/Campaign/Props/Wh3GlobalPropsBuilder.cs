using System.Globalization;
using System.Xml.Linq;
using AtlasWH3.Formats;
using AtlasWH3.Formats.Models;
using AtlasWH3.Formats.Packs;
using AtlasWH3.Formats.Props;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign.Props;

/// <summary>
/// WH3's global_props.bin and global_props_sound.bin from the map's layers (BOB "Terry file"). The container is 3K's
/// (docs/native_campaign_build.md, "global_props.bin"): each object goes to the map.hex region under it and the deepest
/// quadtree cell holding its box; bucket bodies bmd_objects.&lt;region&gt;.&lt;cell&gt;.&lt;bucket&gt;.bin hold the
/// records, cell bodies bmd_objects.&lt;region&gt;.&lt;cell&gt;.bin reference their buckets, and the root
/// bmd_objects.bin references every cell body. What WH3 changes (read off BOB's IEE and Old World output, 2026-10-09):
///  - bodies are BMD v27 (<see cref="Bmd27Body"/>), with no per-body preamble;
///  - buckets are 16 × prefab_types value + 15 (props and VFX) or + 0 (everything else): an object goes to the bucket
///    of every culture in its culture_mask (BASE, value 1, when it has none; NONE, value 0, for a culture with no
///    prefab_types row), and the cell's reference to a bucket carries that culture's bit;
///  - props whose model draws with an add_terrain_height material (the mountains) get a body each: buckets 640 + n;
///  - sound emitters go to global_props_sound.bin instead (bucket 16 bodies only, no cell or root bodies), with the
///    culture mask in the record.
/// </summary>
public sealed class Wh3GlobalPropsBuilder
{
    private sealed class Obj
    {
        public required string Kind;
        public ulong Id;
        /// <summary>The entity's own id inside its prefab (0 for a layer entity).</summary>
        public ulong InnerId;
        public int Sub;
        /// <summary>Region lookup point (the entity's world position).</summary>
        public float X, Z;
        /// <summary>x0, z0, x1, z1 for the quadtree cell; null = the point (X, Z).</summary>
        public float[]? Box;
        public bool RootCell;
        public required IReadOnlyList<int> Cultures;
        public bool OnePerBody;
        public string What = "";
        public float[]? DefaultBox;
        public required Action<Bmd27Body> Add;
    }

    /// <summary>A world transform: row-major 3x3 rotation-scale and a position, in float as BOB keeps it.</summary>
    private readonly record struct Xf(float[] R, float X, float Y, float Z, (float X, float Y, float Z, float W) Q)
    {
        public static Xf Of(XElement? t)
        {
            var p = Floats((string?)t?.Attribute("position") ?? "0 0 0");
            var r = Floats((string?)t?.Attribute("rotation") ?? "0 0 0");
            var s = Floats((string?)t?.Attribute("scale") ?? "1 1 1");
            var m = QtuTransform.MatrixWh3(r[0], r[1], r[2], s[0], s[1], s[2]);
            return new Xf(m.Select(v => (float)v).ToArray(), p[0] + 0f, p[1] + 0f, p[2] + 0f, QtuTransform.QuaternionWh3(r[0], r[1], r[2]));
        }

        /// <summary>This transform applied after <paramref name="child"/> (a prefab instance around an inner entity), the
        /// way BOB flattens prefabs (<see cref="QtuTransform.ComposeWh3"/>).</summary>
        public Xf Then(Xf child)
        {
            var (r, x, y, z, q) = QtuTransform.ComposeWh3(R, X, Y, Z, child.R, child.X, child.Y, child.Z);
            return new Xf(r, x, y, z, q);
        }

        /// <summary>A point in entity space to the world, summed as BOB does: ((t + r2·z) + r1·y) + r0·x (all 25,227 IEE
        /// terrain-hole vertices).</summary>
        public (float X, float Y, float Z) Apply(float x, float y, float z) =>
            (X + R[2] * z + R[1] * y + R[0] * x, Y + R[5] * z + R[4] * y + R[3] * x, Z + R[8] * z + R[7] * y + R[6] * x);

        /// <summary>The record layout: the three columns, then the position.</summary>
        public float[] Record() => [R[0], R[3], R[6], R[1], R[4], R[7], R[2], R[5], R[8], X, Y, Z];
    }

    private readonly PackSet _packs;
    private readonly PrefabLibrary _prefabs;
    private readonly IReadOnlyDictionary<string, int> _cultures;
    private readonly string _mapName;
    private readonly (float X0, float Z0, float X1, float Z1) _root;
    private readonly Func<float, float, string> _regionAt;
    private readonly List<Obj> _objects = [];
    private readonly List<Obj> _sounds = [];
    private int _sub;

    public List<string> Notes { get; } = [];

    /// <summary>Optional trace: one line per placed object (kind, entity id, identity, x, z, box, cell, region).</summary>
    public Action<string>? Trace { get; init; }

    /// <param name="cultures">prefab_types: culture key → value.</param>
    /// <param name="root">The quadtree's root rectangle (<see cref="HexRegionLookup.QuadRoot"/>).</param>
    public Wh3GlobalPropsBuilder(PackSet packs, PrefabLibrary prefabs, IReadOnlyDictionary<string, int> cultures, string mapName,
                                 (float X0, float Z0, float X1, float Z1) root, Func<float, float, string> regionAt)
    {
        _packs = packs;
        _prefabs = prefabs;
        _cultures = cultures;
        _mapName = mapName;
        _root = root;
        _regionAt = regionAt;
    }

    /// <summary>prefab_types (culture → value) from the kit's raw_data DB export.</summary>
    public static Dictionary<string, int> ReadCultures(string akRoot)
    {
        var file = new[] { Path.Combine(akRoot, "raw_data", "db", "prefab_types.xml"), Path.Combine(akRoot, "raw_data", "EmpireDesignData", "prefab_types.xml") }
            .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("prefab_types.xml not found in the kit's raw_data");
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in XDocument.Load(file).Descendants("prefab_types"))
            if ((string?)row.Element("culture") is { Length: > 0 } c && c != "*" && int.TryParse((string?)row.Element("value"), out var v))
                result.TryAdd(c, v);
        return result;
    }

    public void AddLayer(string layerPath)
    {
        var root = XDocument.Load(layerPath).Root!;
        var entities = root.Element("entities")?.Elements("entity").ToList() ?? [];
        var hidden = HiddenMembers(root, entities);
        foreach (var e in entities)
        {
            if (e.Element("ECLayer") is not null) continue;
            var id = ulong.TryParse((string?)e.Attribute("id"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;
            if (hidden.Contains((string?)e.Attribute("id") ?? "")) continue;
            Entity(e, id, Xf.Of(e.Element("ECTransform")), 0);
        }
    }

    private readonly Dictionary<string, HashSet<string>> _prefabHidden = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The entities of a prefab that a non-exported group or file layer hides (marienburg_big's "houses").</summary>
    private HashSet<string> PrefabHidden(PrefabDefinition def)
    {
        if (_prefabHidden.TryGetValue(def.Path, out var cached)) return cached;
        var hidden = new HashSet<string>();
        foreach (var layer in TerryProject.Load(def.Path).Layers())
        {
            if (layer.FilePath is not { } file || !File.Exists(file)) continue;
            XElement root;
            try { root = XDocument.Load(file).Root!; }
            catch (System.Xml.XmlException) { continue; }
            var entities = root.Element("entities")?.Elements("entity").ToList() ?? [];
            if (!layer.Export) hidden.UnionWith(entities.Select(e => (string?)e.Attribute("id") ?? ""));
            else hidden.UnionWith(HiddenMembers(root, entities));
        }
        return _prefabHidden[def.Path] = hidden;
    }

    /// <summary>Ids owned (through Logical associations, transitively) by an ECLayer with export="false".</summary>
    internal static HashSet<string> HiddenMembers(XElement root, List<XElement> entities)
    {
        var groups = entities.Where(e => e.Element("ECLayer") is not null).ToDictionary(e => (string)e.Attribute("id")!, e => e);
        var members = new Dictionary<string, List<string>>();
        foreach (var from in root.Element("associations")?.Element("Logical")?.Elements("from") ?? [])
            members[(string)from.Attribute("id")!] = from.Elements("to").Select(t => (string)t.Attribute("id")!).ToList();
        var hidden = new HashSet<string>();
        void Hide(string id)
        {
            if (!hidden.Add(id)) return;
            foreach (var m in members.GetValueOrDefault(id) ?? []) Hide(m);
        }
        foreach (var (id, g) in groups)
            if ((string?)g.Element("ECLayer")!.Attribute("export") == "false") Hide(id);
        return hidden;
    }

    private void Entity(XElement e, ulong id, Xf world, int depth)
    {
        var innerId = depth == 0 ? 0 : ulong.TryParse((string?)e.Attribute("id"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var iv) ? iv : 0;
        if (e.Element("ECPrefab") is { } prefab)
        {
            var key = (string?)prefab.Attribute("key") ?? "";
            if (depth > PrefabExpander.MaxDepth) return;
            if (_prefabs.Load(key) is not { } def) { Notes.Add($"prefab '{key}' not found in {_prefabs.Root}; skipped"); return; }
            var overrides = prefab.Elements("override").Where(o => o.Attribute("id") is not null)
                .GroupBy(o => (string)o.Attribute("id")!).ToDictionary(g => g.Key, g => g.Last());
            var hidden = PrefabHidden(def);
            foreach (var (inner, _) in def.Entities)
            {
                if (hidden.Contains((string?)inner.Attribute("id") ?? "")) continue;
                var copy = inner;
                if (overrides.TryGetValue((string?)inner.Attribute("id") ?? "", out var o))
                {
                    copy = new XElement(inner);
                    foreach (var oc in o.Elements())
                        if (copy.Element(oc.Name) is { } target)
                            foreach (var a in oc.Attributes()) target.SetAttributeValue(a.Name, a.Value);
                }
                Entity(copy, id, world.Then(Xf.Of(copy.Element("ECTransform"))), depth + 1);
            }
            return;
        }

        var cp = e.Element("ECCampaignProperties");
        bool Cp(string name, bool fallback) => cp?.Attribute(name) is { } a ? (string)a == "true" : fallback;
        byte B(bool v) => v ? (byte)1 : (byte)0;
        var cultures = Cultures((string?)cp?.Attribute("culture_mask") ?? "");
        var vis = e.Element("ECVisibilitySettingsCampaign");
        var flags = new Bmd27Flags(B((string?)vis?.Attribute("visible_in_tactical_view") == "true"),
                                   B((string?)vis?.Attribute("visible_in_tactical_view_only") == "true"));
        var shroud = Cp("visible_in_shroud", false);
        var shroudOnly = Cp("visible_in_shroud_only", false);
        var noCulling = Cp("no_culling", false);
        Obj Add(string kind, Action<Bmd27Body> add, float[]? box = null) =>
            AddObj(new Obj { Kind = kind, Id = id, InnerId = innerId, Sub = _sub++, X = world.X, Z = world.Z, Box = box, Cultures = cultures, Add = add });

        if (e.Element("ECRiver") is not null)
        {
            // models/river_<entity id>, boxed by the mesh the rivers step bakes (no mesh: the root cell). The mesh's
            // heights are relative to the spline's first point, so BOB raises the prop by that point's height (Old
            // World's river 19261f91ac8e913: y 0.757 -> 0)
            var model = $"terrain/campaigns/{_mapName}/models/river_{(string?)e.Attribute("id")}.wsmodel";
            var spline = Rivers.Wh3River.Read(e);
            var placed = world;
            if (spline is not null)
            {
                var (px, py, pz) = world.Apply(0, spline.BaseHeight, 0);
                placed = world with { X = px, Y = py, Z = pz };
            }
            var baked = spline is null ? null : Rivers.Wh3River.Build(spline);
            var box = baked is null ? null : MeshBox(baked.Bounds[..3], baked.Bounds[3..], placed);
            var o = AddObj(new Obj
            {
                Kind = "prop", Id = id, InnerId = innerId, Sub = _sub++, X = placed.X, Z = placed.Z, Box = box, Cultures = cultures,
                Add = b => { var r = PropRecord(b, model, placed, false, Cp, flags, shroud, shroudOnly, noCulling, null, e); r.B2 = 0; b.Props.Add(r); },
            });
            o.RootCell = box is null;
            return;
        }
        if (e.Element("ECDecal") is { } decal)
        {
            var model = (string?)decal.Attribute("model_path") ?? "";
            if (model.Length > 0)
                Add("decal", b => b.Props.Add(PropRecord(b, model, world, true, Cp, flags, shroud, shroudOnly, noCulling, decal, e)));
            return;
        }
        if (e.Element("ECMesh") is { } mesh)
        {
            var model = (string?)mesh.Attribute("model_path") ?? "";
            if (model.Length == 0) return;
            var info = Model(model);
            var o = Add("prop", b => b.Props.Add(PropRecord(b, model, world, false, Cp, flags, shroud, shroudOnly, noCulling, null, e, info)),
                MeshBox(info.Min, info.Max, world));
            o.What = model;
            o.DefaultBox = MeshBox([-1, -1, -1], [1, 1, 1], world);
            o.OnePerBody = info.TerrainHeight;
            return;
        }
        if (e.Element("ECVFX") is { } vfx)
        {
            // the world matrix times the effect's scale as a full 3x3 product (its +0 terms turn a -0 into +0, as BOB)
            var vs = vfx.Attribute("scale") is { } va ? float.Parse((string)va, CultureInfo.InvariantCulture) : 1f;
            var scaled = new float[9];
            for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++)
                    scaled[i * 3 + j] = world.R[i * 3] * (j == 0 ? vs : 0f) + world.R[i * 3 + 1] * (j == 1 ? vs : 0f) + world.R[i * 3 + 2] * (j == 2 ? vs : 0f);
            var vfxWorld = world with { R = scaled };
            Add("vfx", b => b.Vfx.Add(new Bmd27Vfx
            {
                Name = (string?)vfx.Attribute("vfx") ?? "", Transform = vfxWorld.Record(), Instance = (string?)vfx.Attribute("instance_name") ?? "",
                Flags = flags, Autoplay = B((string?)vfx.Attribute("autoplay") != "false"), VisibleInShroud = B(shroud),
                NotShroudOnly = B(!shroudOnly), NoCulling = B(noCulling),
            }));
            return;
        }
        if (e.Element("ECPointLight") is { } light)
        {
            float A(string n, float d) => light.Attribute(n) is { } a ? float.Parse((string)a, CultureInfo.InvariantCulture) : d;
            var c = Floats((string?)light.Attribute("colour") ?? "255 255 255 255");
            var speed = Floats((string?)light.Attribute("animation_speed_scale") ?? "0 0");
            var r = world.R;
            var avg = (MathF.Sqrt(r[3] * r[3] + r[0] * r[0] + r[6] * r[6]) + MathF.Sqrt(r[4] * r[4] + r[1] * r[1] + r[7] * r[7])
                       + MathF.Sqrt(r[5] * r[5] + r[2] * r[2] + r[8] * r[8])) * 0.33333334f;
            var radius = A("radius", 1) * avg;
            var half = radius * 0.5f;
            Add("light", b => b.Lights.Add(new Bmd27Light
            {
                X = world.X, Y = world.Y, Z = world.Z, Radius = radius,
                R = c[0] * (1f / 255f), G = c[1] * (1f / 255f), B = c[2] * (1f / 255f), ColourScale = A("colour_scale", 1),
                Animation = (string?)light.Attribute("animation_type") switch { "LAT_RADIUS_SIN" => 1, "LAT_RADIUS_SIN_SIN" => 2, _ => 0 },
                AnimationScale1 = speed.ElementAtOrDefault(0), AnimationScale2 = speed.ElementAtOrDefault(1),
                ColourMin = A("colour_min", 0), RandomOffset = A("random_offset", 0), Falloff = (string?)light.Attribute("falloff_type") ?? "",
                LightProbesOnly = B((string?)light.Attribute("for_light_probes_only") == "true"), Flags = flags,
            }), [world.X - half, world.Z - half, world.X + half, world.Z + half]);
            return;
        }
        if (e.Element("ECSpotLight") is { } spot)
        {
            float A(string n, float d) => spot.Attribute(n) is { } a ? float.Parse((string)a, CultureInfo.InvariantCulture) : d;
            var c = Floats((string?)spot.Attribute("colour") ?? "255 255 255 255");
            var intensity = A("intensity", 1);
            // the rotation of the decomposed world matrix (not the layer's quaternion: its sign can differ); angles in
            // radians; the length through the z column's length over the z scale
            var ((qx, qy, qz, qw), _, _, s2) = QtuTransform.Decompose(world.R);
            var column = MathF.Sqrt(world.R[5] * world.R[5] + world.R[2] * world.R[2] + world.R[8] * world.R[8]);
            Add("spot", b => b.Spots.Add(new Bmd27Spot
            {
                X = world.X, Y = world.Y, Z = world.Z, Rotation = [qx, qy, qz, qw], Length = A("length", 1) * (column / s2),
                InnerAngle = A("inner_angle", 0) * 3.14159274f / 180f, OuterAngle = A("outer_angle", 0) * 3.14159274f / 180f,
                R = c[0] * (1f / 255f) * intensity, G = c[1] * (1f / 255f) * intensity, B = c[2] * (1f / 255f) * intensity,
                Falloff = A("falloff", 0), Gobo = (string?)spot.Attribute("gobo") ?? "", Volumetric = B((string?)spot.Attribute("volumetric") == "true"),
                Flags = flags,
            }));
            return;
        }
        if (e.Element("ECCompositeScene") is { } scene)
        {
            Add("scene", b => b.Scenes.Add(new Bmd27Scene
            {
                Transform = world.Record(), Path = (string?)scene.Attribute("path") ?? "", Autoplay = 1,   // 1 even for autoplay="false" (IEE's 6)
                VisibleInShroud = B(shroud), NoCulling = B(noCulling), ScriptId = (string?)scene.Attribute("script_id") ?? "",
                NotShroudOnly = B(!shroudOnly), TacticalView = flags.TacticalView, TacticalViewOnly = flags.TacticalViewOnly,
            }));
            return;
        }
        if (e.Element("ECLightProbe") is { } probe)
        {
            var sphere = e.Element("ECDoubleSphere");
            float S(string n, float d) => sphere?.Attribute(n) is { } a ? float.Parse((string)a, CultureInfo.InvariantCulture) : d;
            Add("probe", b => b.Probes.Add(new Bmd27Probe
            {
                X = world.X, Y = world.Y, Z = world.Z, OuterRadius = S("outer_radius", 1), InnerRadius = S("inner_radius", 1),
                Primary = B((string?)probe.Attribute("primary") == "true"),
            }));
            return;
        }
        if (e.Element("ECPolygonMesh") is { } poly)
        {
            var local = Outline(e);
            if (local.Count < 3) return;
            var outline = local.Select(p => (p.X, 0f, p.Z)).ToList();
            var indices = Triangulate(local);
            // tactical-view-only counts only with tactical view on; without it the byte after visible_in_shroud is 0 (the 2
            // such Old World polygons; IEE's 19 with both set keep both)
            var hiddenOnMap = flags.TacticalViewOnly == 1 && flags.TacticalView == 0;
            Add("poly", b => b.Polys.Add(new Bmd27Poly
            {
                Vertices = outline, Indices = indices, Material = (string?)poly.Attribute("material") ?? "",
                Flags = hiddenOnMap ? flags with { TacticalViewOnly = 0 } : flags,
                Transform = world.Record(), VisibleInShroud = B(shroud), Unknown2 = B(!hiddenOnMap),
            }));
            return;
        }
        if (e.Element("ECTerrainHole") is not null)
        {
            // triangulated in entity space (164 of Old World's 166 holes as BOB), then placed
            var local = Outline(e);
            if (local.Count < 3) return;
            var outline = local.Select(p => world.Apply(p.X, 0, p.Z)).ToList();
            var indices = Triangulate(local);
            // every triangle is boxed by the whole hole (BOB puts all of a hole's triangles in one cell)
            float[] box = [outline.Min(p => p.X), outline.Min(p => p.Z), outline.Max(p => p.X), outline.Max(p => p.Z)];
            for (var k = 0; k + 2 < indices.Count; k += 3)
            {
                var (a, bb, cc) = (outline[indices[k]], outline[indices[k + 1]], outline[indices[k + 2]]);
                Add("hole", b => b.Holes.Add(new Bmd27Hole { Vertices = [a.X, a.Y, a.Z, bb.X, bb.Y, bb.Z, cc.X, cc.Y, cc.Z], Flags = flags }), box);
            }
            return;
        }
        if (e.Element("ECSoundMarker") is { } sound)
        {
            var key = (string?)sound.Attribute("key") ?? "";
            var cloud = e.Element("ECPointCloud");
            var sphere = e.Element("ECSphere");
            var line = e.Element("ECPolyline3D") ?? e.Element("ECPolyline");
            var points = new List<(float X, float Y, float Z)>();
            string shape;
            if (cloud is not null)
            {
                shape = "SST_MULTI_POINT";
                foreach (var p in cloud.Descendants("point"))
                    points.Add(world.Apply(F(p, "x"), F(p, "y"), F(p, "z")));
            }
            else if (line is not null)
            {
                // an ECPolyline3D's points in order (IEE's 1,932 line-list emitters)
                shape = "SST_LINE_LIST";
                foreach (var p in line.Descendants("point"))
                    points.Add(world.Apply(F(p, "x"), p.Attribute("z") is null ? 0 : F(p, "y"), p.Attribute("z") is null ? F(p, "y") : F(p, "z")));
            }
            else
            {
                shape = sphere is null ? "SST_POINT" : "SST_SPHERE";
                points.Add((world.X, world.Y, world.Z));
            }
            var mask = SoundMask(cultures, (string?)cp?.Attribute("culture_mask") ?? "");
            var r = world.R;
            var radius = sphere is null ? 0f : float.Parse((string?)sphere.Attribute("radius") ?? "0", CultureInfo.InvariantCulture);
            _sounds.Add(new Obj
            {
                Kind = "sound", Id = id, Sub = _sub++, X = world.X, Z = world.Z, Cultures = [1],
                Add = b => b.Sounds.Add(new Bmd27Sound
                {
                    Name = key, Shape = shape, Points = points, Radius = radius, CultureMask = mask, Axes = QtuTransform.AxesWh3(world.Q),
                }),
            });
        }
    }

    private Obj AddObj(Obj o)
    {
        _objects.Add(o);
        return o;
    }

    private Bmd27Prop PropRecord(Bmd27Body b, string model, Xf world, bool isDecal, Func<string, bool, bool> cp, Bmd27Flags flags,
        bool shroud, bool shroudOnly, bool noCulling, XElement? decal, XElement e, ModelData? info = null)
    {
        byte B(bool v) => v ? (byte)1 : (byte)0;
        float D(string n, float d) => decal?.Attribute(n) is { } a ? float.Parse((string)a, CultureInfo.InvariantCulture) : d;
        var rs = e.Element("ECMeshRenderSettings");
        var hp = (string?)e.Element("ECPropHeightPatch")?.Attribute("apply_height_patch") == "true";
        return new Bmd27Prop
        {
            PathIndex = b.PathIndex(model), Transform = world.Record(), Decal = B(isDecal),
            VisibleInsideSnow = B(cp("visible_inside_snow_region", true)), VisibleOutsideSnow = B(cp("visible_outside_snow_region", true)),
            VisibleInsideDestruction = B(cp("visible_inside_destruction_region", true)),
            VisibleOutsideDestruction = B(cp("visible_outside_destruction_region", true)),
            Animated = B(info?.Skinned == true),
            DecalParallaxScale = D("parallax_scale", 0), DecalTiling = D("tiling", 0), Flags = flags,
            B0 = B(shroud),
            B1 = B(isDecal && (string?)decal!.Attribute("apply_to_terrain") != "false"),
            B2 = B(isDecal ? (string?)decal!.Attribute("apply_to_objects") == "true" : (string?)rs?.Attribute("receive_decals") != "false"),
            B3 = B(isDecal && (string?)decal!.Attribute("render_above_snow") == "true"),
            CastShadow = 1, NoCulling = B(noCulling),
            HasHeightPatch = B(hp), ApplyHeightPatch = B(hp), IncludeInFog = B((string?)rs?.Attribute("render_into_skydome_fog") == "true"),
            NotShroudOnly = B(!shroudOnly), DynamicShadows = B(info?.DynamicShadows == true), UsesTerrainVertexOffset = B(info?.TerrainHeight == true),
            BlendEdgesWithTerrain = rs?.Attribute("blend_edges_with_terrain") is { } be ? float.Parse((string)be, CultureInfo.InvariantCulture) : 0,
            DecalFadeStart = D("fade_start", 0.875f), DecalAngleThreshold = D("angle_threshold", 90),
            DecalMirrorX = B((string?)decal?.Attribute("mirror_x") == "true"), DecalMirrorZ = B((string?)decal?.Attribute("mirror_z") == "true"),
            DecalLayer = (int)D("layer", 0), DecalTilingAlpha = B((string?)decal?.Attribute("tiling_alpha_enabled") == "true"),
        };
    }

    // ---------------------------------------------------------------- cultures

    /// <summary>prefab_types values of a culture_mask: BASE (1) when empty, NONE (0) for a culture without a row.</summary>
    private IReadOnlyList<int> Cultures(string mask)
    {
        var names = mask.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0) return [1];
        return names.Select(n => _cultures.TryGetValue(n, out var v) ? v : 0).Order().ToList();
    }

    private static ulong Bit(int value) => 1UL << ((value - 1) & 63);

    /// <summary>A sound emitter's culture mask: the bits of its known cultures (a culture without a prefab_types row adds
    /// none, unlike the buckets' NONE), 0 when it names none.</summary>
    private static ulong SoundMask(IReadOnlyList<int> values, string mask) =>
        mask.Trim().Length == 0 ? 0 : values.Where(v => v > 0).Aggregate(0UL, (m, v) => m | Bit(v));

    // ---------------------------------------------------------------- models

    /// <summary>What the builder knows of a prop model: its box (the union of every LOD's mesh header bounds, or
    /// [-1, 1]^3), whether a material adds the terrain height (the mountains: a body each and uses_terrain_vertex_offset),
    /// whether a material uses the emissive campaign mountain shader (BOB sets use_dynamic_shadows on those: all 8 such
    /// models on IEE and Old World, no other), and whether the RMV2 has a skeleton (BOB's "animated": Old World's ice sheets; also a mod's skinned model, which BOB leaves static because it reads skeletons from the game's own packs only).</summary>
    public sealed record ModelData(float[] Min, float[] Max, bool TerrainHeight, bool DynamicShadows, bool Skinned);

    private readonly Dictionary<string, ModelData> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _missingModels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Model paths the layers reference that no pack has (or that don't read): boxed as [-1, 1]^3, as BOB does.
    /// Usually a mistake (the model's pack isn't linked), but a map can name a model another mod will supply.</summary>
    public IReadOnlyCollection<string> MissingModels { get { lock (_models) return [.. _missingModels]; } }

    /// <summary>What <see cref="Model"/> derives for a model path (research and the gp27-model command).</summary>
    public ModelData ModelInfo(string path) => Model(path);

    private ModelData Model(string path)
    {
        lock (_models)
            if (_models.TryGetValue(path, out var hit)) return hit;
        float[] min = [-1, -1, -1], max = [1, 1, 1];
        bool terrainHeight = false, emissive = false, skinned = false, found = false;
        var geometry = path;
        if (path.EndsWith(".wsmodel", StringComparison.OrdinalIgnoreCase) && _packs.TryRead(path.ToLowerInvariant()) is { } ws)
        {
            try
            {
                var file = WsModelFile.Parse(ws);
                geometry = file.Geometry;
                foreach (var m in file.Materials.Values.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var bytes = _packs.TryRead(m.ToLowerInvariant());
                    terrainHeight |= Trees.TreeHeightField.AddsTerrainHeight(bytes);
                    if (bytes is not null)
                        try { emissive |= MaterialFile.Parse(bytes).Shader.Contains("rigid_campaign_mountain_emissive.xml", StringComparison.OrdinalIgnoreCase); }
                        catch (System.Xml.XmlException) { }
                }
            }
            catch (System.Xml.XmlException) { }
        }
        if (_packs.TryRead(geometry.ToLowerInvariant()) is { } rmv2)
        {
            try
            {
                var rm = RigidModel.Read(rmv2);
                // BOB's "animated": the RMV2 has a skeleton
                skinned = rm.Skeleton.Length > 0;
                var meshes = rm.Lods.SelectMany(l => l.Meshes).ToList();
                if (meshes.Count > 0)
                {
                    found = true;
                    min = [meshes.Min(q => q.BoundsMin[0]), meshes.Min(q => q.BoundsMin[1]), meshes.Min(q => q.BoundsMin[2])];
                    max = [meshes.Max(q => q.BoundsMax[0]), meshes.Max(q => q.BoundsMax[1]), meshes.Max(q => q.BoundsMax[2])];
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
        }
        var result = new ModelData(min, max, terrainHeight, emissive, skinned);
        lock (_models)
        {
            _models[path] = result;
            if (!found) _missingModels.Add(path);
        }
        return result;
    }

    /// <summary>The model box's 8 corners through the world matrix, x/z min/max (3K's bob_terrain FUN_18005e050).</summary>
    private static float[] MeshBox(float[] mn, float[] mx, Xf w)
    {
        float m0 = w.R[0], m1 = w.R[1], m2 = w.R[2], m3 = w.X, m8 = w.R[6], m9 = w.R[7], m10 = w.R[8], m11 = w.Z;
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

    // ---------------------------------------------------------------- layout

    /// <summary>The deepest of 7 quadtree levels whose cell holds the box (-1 outside the root).</summary>
    public int Cell(float x0, float z0, float x1, float z1)
    {
        var (rx0, rz0, rx1, rz1) = _root;
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

    private int CellOf(Obj o)
    {
        if (o.RootCell) return 0;
        var b = o.Box ?? [o.X, o.Z, o.X, o.Z];
        return Cell(b[0], b[1], b[2], b[3]);
    }

    private static int KindRank(string kind) => kind == "decal" ? 0 : 1;

    /// <summary>global_props.bin's entries: per cell (ascending) and region (CA hash-map order), its bucket bodies in
    /// ascending order and then the cell body; the root last.</summary>
    public List<(string Name, byte[] Body)> BuildProps()
    {
        var prefix = $"terrain/campaigns/{_mapName}/bmd_objects";
        var placed = new List<(int Cell, string Region, int Bucket, Obj O)>();
        var dropped = 0;
        foreach (var o in _objects)
        {
            var cell = CellOf(o);
            var region = _regionAt(o.X, o.Z);
            Trace?.Invoke(FormattableString.Invariant($"{o.Kind},{o.Id:x}/{o.InnerId:x}/{o.Sub},{o.What},{o.X},{o.Z},{(o.Box is null ? "" : string.Join(" ", o.Box))},{cell},{region},{(o.DefaultBox is { } d ? Cell(d[0], d[1], d[2], d[3]) : -2)}"));
            if (cell < 0) { dropped++; continue; }
            if (o.OnePerBody) { placed.Add((cell, region, -1, o)); continue; }
            var low = o.Kind is "prop" or "vfx" ? 15 : 0;
            foreach (var v in o.Cultures) placed.Add((cell, region, 16 * v + low, o));
        }
        if (dropped > 0) Notes.Add($"{dropped} objects reach outside the quadtree root: dropped (as BOB)");
        // a sound emitter's (region, cell) gets an empty bucket-16 body here too (its records go to global_props_sound.bin)
        foreach (var o in _sounds)
            if (CellOf(o) is var cell and >= 0)
                placed.Add((cell, _regionAt(o.X, o.Z), 16, new Obj { Kind = "sound", Id = o.Id, Sub = o.Sub, Cultures = [1], Add = _ => { } }));

        var entries = new List<(string, byte[])>();
        var root = new Bmd27Body();
        foreach (var cellGroup in placed.GroupBy(p => p.Cell).OrderBy(g => g.Key))
        {
            var byRegion = cellGroup.GroupBy(p => p.Region).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var order = CaHash.HashMapOrder(byRegion.OrderBy(kv => kv.Value.Min(p => p.O.Id)).Select(kv => kv.Key));
            foreach (var region in order)
            {
                var items = byRegion[region];
                var buckets = items.Where(p => p.Bucket >= 0).GroupBy(p => p.Bucket).OrderBy(g => g.Key)
                    .Select(g => (g.Key, g.Select(p => p.O).ToList())).ToList();
                var n = 0;
                foreach (var o in items.Where(p => p.Bucket < 0).Select(p => p.O).OrderBy(o => o.Id).ThenBy(o => o.Sub))
                    buckets.Add((640 + n++, [o]));
                var cellBody = new Bmd27Body();
                foreach (var (bucket, objs) in buckets)
                {
                    var body = new Bmd27Body();
                    foreach (var o in objs.OrderBy(o => KindRank(o.Kind)).ThenBy(o => o.Id).ThenBy(o => o.Sub)) o.Add(body);
                    var name = $"{prefix}.{region}.{cellGroup.Key}.{bucket}.bin";
                    entries.Add((name, body.ToBytes()));
                    cellBody.Nested.Add(new Bmd27Nested { Name = name, CultureMask = bucket >= 640 ? 1 : Bit(bucket / 16), Region = region });
                }
                var cellName = $"{prefix}.{region}.{cellGroup.Key}.bin";
                entries.Add((cellName, cellBody.ToBytes()));
                root.Nested.Add(new Bmd27Nested { Name = cellName, Region = region });
            }
        }
        entries.Add(($"{prefix}.bin", root.ToBytes()));
        return entries;
    }

    /// <summary>global_props_sound.bin's entries: one bucket-16 body per (cell, region), no cell or root bodies.</summary>
    public List<(string Name, byte[] Body)> BuildSound()
    {
        var prefix = $"terrain/campaigns/{_mapName}/bmd_objects";
        var entries = new List<(string, byte[])>();
        var placed = _sounds.Select(o => (Cell: CellOf(o), Region: _regionAt(o.X, o.Z), O: o)).Where(p => p.Cell >= 0).ToList();
        foreach (var cellGroup in placed.GroupBy(p => p.Cell).OrderBy(g => g.Key))
        {
            var byRegion = cellGroup.GroupBy(p => p.Region).ToDictionary(g => g.Key, g => g.Select(p => p.O).ToList(), StringComparer.Ordinal);
            foreach (var region in CaHash.HashMapOrder(byRegion.OrderBy(kv => kv.Value.Min(o => o.Id)).Select(kv => kv.Key)))
            {
                var body = new Bmd27Body { AfterLights = Bmd27Body.AfterLightsSound };
                foreach (var o in byRegion[region].OrderBy(o => o.Id).ThenBy(o => o.Sub)) o.Add(body);
                entries.Add(($"{prefix}.{region}.{cellGroup.Key}.16.bin", body.ToBytes()));
            }
        }
        return entries;
    }

    public int ObjectCount => _objects.Count;
    public int SoundCount => _sounds.Count;

    // ---------------------------------------------------------------- geometry helpers

    /// <summary>An ECPolyline's points in entity space: (x, 0, y).</summary>
    private static List<(float X, float Z)> Outline(XElement e) =>
        e.Element("ECPolyline")?.Descendants("point").Select(p => (F(p, "x"), F(p, "y"))).ToList() ?? [];

    private static float F(XElement p, string name) => float.Parse((string?)p.Attribute(name) ?? "0", CultureInfo.InvariantCulture);

    private static float[] Floats(string s) =>
        s.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();

    /// <summary>
    /// BOB's ear clipping of a polygon outline (x, z), in float. The scan starts at slot 1; an ear is clipped when it turns
    /// the polygon's way and no other remaining vertex is inside it, and the scan stays on the same slot; any slot past the
    /// end goes back to 1. Each triangle is written clockwise ((next, ear, previous) for a counter-clockwise outline,
    /// (previous, ear, next) otherwise), and the last three vertices as the ear at the current slot. Fitted on all 1,253
    /// polygon meshes of BOB's IEE and Old World global_props.bin: 1,252 identical (2026-10-09; the other is one where
    /// BOB stops with seven vertices left).
    /// </summary>
    private static List<ushort> Triangulate(List<(float X, float Z)> pts)
    {
        var idx = Enumerable.Range(0, pts.Count).ToList();
        double area = 0;
        for (var i = 0; i < pts.Count; i++) { var p = pts[i]; var q = pts[(i + 1) % pts.Count]; area += (double)p.X * q.Z - (double)q.X * p.Z; }
        var ccw = area > 0;
        var result = new List<ushort>();
        int slot = 1, misses = 0;
        void Emit(int a, int b, int c) => result.AddRange(ccw ? [(ushort)c, (ushort)b, (ushort)a] : [(ushort)a, (ushort)b, (ushort)c]);
        while (idx.Count > 3)
        {
            if (misses >= idx.Count) return result;
            int a = idx[(slot - 1 + idx.Count) % idx.Count], b = idx[slot], c = idx[(slot + 1) % idx.Count];
            var cross = (pts[b].X - pts[a].X) * (pts[c].Z - pts[a].Z) - (pts[b].Z - pts[a].Z) * (pts[c].X - pts[a].X);
            if (!ccw) cross = -cross;
            if (cross > 0 && !idx.Any(k => k != a && k != b && k != c && InTri(pts[k], pts[a], pts[b], pts[c])))
            {
                Emit(a, b, c);
                idx.RemoveAt(slot);
                misses = 0;
            }
            else { slot++; misses++; }
            if (slot >= idx.Count) slot = 1;
        }
        Emit(idx[(slot + 2) % 3], idx[slot], idx[(slot + 1) % 3]);
        return result;

        static bool InTri((float X, float Z) p, (float X, float Z) a, (float X, float Z) b, (float X, float Z) c)
        {
            float d1 = (p.X - b.X) * (a.Z - b.Z) - (a.X - b.X) * (p.Z - b.Z);
            float d2 = (p.X - c.X) * (b.Z - c.Z) - (b.X - c.X) * (p.Z - c.Z);
            float d3 = (p.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (p.Z - a.Z);
            return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
        }
    }
}
