using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AtlasWH3.Core.Assets;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Campaign.Props;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Audit;

/// <summary>One audit finding: what, where, how bad, and (when there is a safe one) the entity-edit op that fixes it.</summary>
public sealed record AuditFinding(string Check, string Severity, string Id, string Layer, string? Region, string? Model,
                                  double X, double Z, double Value, string Note, JsonObject? Fix = null);

/// <summary>
/// Campaign map audit over the project's layers (every layer the .terry lists, hidden ones included; orphan .layer
/// files are not exported, so they are skipped), the lf / sea heights, map.hex and the models.
/// <para>
/// Height rules: mountain models whose material uses the LF-offset shader store y as an offset above the terrain
/// (the vertex shader adds the lf height per vertex), so their base is y + scale·minY above the ground; every other
/// prop's y is absolute. Vanilla dlc07: 95% of LF-offset mountain bases sit ≥ 0.13 under the terrain, 6 of 502 float
/// more than 0.3.
/// </para>
/// </summary>
public sealed class MapAudit
{
    public const string Info = "info", Warning = "warning", Error = "error";

    public static readonly string[] AllChecks =
    [
        "floating-mountain", "buried-mountain", "buried-prop", "floating-prop", "prop-in-sea", "wrong-region",
        "on-city-footprint", "duplicate", "asset", "scale-outlier", "city", "road-river", "city-dressing",
    ];

    private readonly ProjectPaths _paths;
    private readonly EntityEditor _editor;
    private readonly ModelLibrary _models;
    private readonly Action<string> _log;
    private Raster<ushort>? _lf, _sea;
    private double _worldW, _worldH;
    private HexRegionLookup? _hex;
    private readonly Dictionary<string, ModelInfo?> _modelInfo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Model facts the checks need: bounds, horizontal radius, LF-offset share, load problem.</summary>
    public sealed record ModelInfo(float[] Bounds, float Radius, float TerrainOffset, string? Problem)
    {
        public float MinY => Bounds[1];
        public float MaxY => Bounds[4];
    }

    public List<AuditFinding> Findings { get; } = [];
    public List<string> Notes { get; } = [];
    /// <summary>Per category: (y − ground) offsets of absolute props, for calibration.</summary>
    public Dictionary<string, List<double>> HeightOffsets { get; } = [];

    public MapAudit(ProjectPaths paths, string? terryPath = null, Action<string>? log = null)
    {
        _paths = paths;
        _editor = new EntityEditor(paths, terryPath);
        _models = ModelLibrary.ForGame(paths, modPacks: paths.ModPacks);
        _log = log ?? (_ => { });
    }

    // ------------------------------------------------------------------ inputs

    private void LoadTerrain()
    {
        var project = _editor.Project;
        if (project.Find("LowFrequencyHeight") is { } lf && File.Exists(project.LayerTifPath(lf)))
        {
            _lf = TiffMap.ReadGray16(project.LayerTifPath(lf));
            _worldW = _lf.Width * CameraHeightmapStep.PixelSizeX;
            _worldH = _lf.Height * CameraHeightmapStep.PixelSizeZ;
        }
        else Notes.Add("no LowFrequencyHeight map: height checks skipped");
        if (project.Find("LowFrequencyHeightSea") is { } sea && File.Exists(project.LayerTifPath(sea)))
            _sea = TiffMap.ReadGray16(project.LayerTifPath(sea));
        _hex = HexRegionLookup.ForMap(_paths with { MapName = project.MapName }, out var why);
        if (_hex is null) Notes.Add("no map.hex region lookup (" + why + "): region / city checks skipped");
    }

    /// <summary>Terrain height (world y) at a world point, bilinear over the lf raster (row 0 = north).</summary>
    public double Ground(double x, double z) => Sample(_lf, x, z);

    private double Sample(Raster<ushort>? r, double x, double z)
    {
        if (r is null) return 0;
        var col = Math.Clamp(x / _worldW * r.Width - 0.5, 0, r.Width - 1.001);
        var row = Math.Clamp((1 - z / _worldH) * r.Height - 0.5, 0, r.Height - 1.001);
        int c0 = (int)col, r0 = (int)row;
        double fx = col - c0, fz = row - r0;
        var v = r[c0, r0] * (1 - fx) * (1 - fz) + r[c0 + 1, r0] * fx * (1 - fz) + r[c0, r0 + 1] * (1 - fx) * fz + r[c0 + 1, r0 + 1] * fx * fz;
        return v * CameraHeightmapStep.HeightStep + CameraHeightmapStep.HeightOffset;
    }

    public ModelInfo? Model(string path)
    {
        if (_modelInfo.TryGetValue(path, out var m)) return m;
        try
        {
            var cpu = _models.Load(path);
            if (cpu is null) m = new ModelInfo(new float[6], 0, 0, _models.Source.Exists(path.Replace((char)92, '/')) ? "no meshes" : "not found");
            else
            {
                var b = cpu.Bounds;
                var radius = Math.Max(Math.Max(Math.Abs(b[0]), Math.Abs(b[3])), Math.Max(Math.Abs(b[2]), Math.Abs(b[5])));
                var offset = cpu.Lods.SelectMany(l => l).Select(x => x.TerrainOffset).DefaultIfEmpty(0).Max();
                m = new ModelInfo(b, radius, offset, cpu.Lods.Count == 0 || cpu.Lods[0].Count == 0 ? "no meshes" : null);
            }
        }
        catch (Exception ex) { m = new ModelInfo(new float[6], 0, 0, ex.Message); }
        return _modelInfo[path] = m;
    }

    private static string Category(string model)
    {
        var p = model.Replace('\\', '/').ToLowerInvariant();
        if (p.Contains("/mountains/")) return "mountain";
        if (p.Contains("/rocks/")) return "rock";
        if (p.Contains("/vegetation/")) return "tree";
        if (p.Contains("/settlements/")) return "building";
        return "other";
    }

    private static bool WaterModel(string model)
    {
        var p = model.ToLowerInvariant();
        return new[] { "port", "boat", "ship", "bridge", "water", "dock", "pier", "wharf", "lily", "lotus", "reed", "fish", "river", "sea", "jetty", "walkway", "scaffold" }
            .Any(p.Contains);
    }

    private sealed record Prop(TerryEntityData Entity, string Layer, string LayerId, string Model, double X, double Y, double Z,
                               double ScaleY, double ScaleXZ, ModelInfo Info);

    // ------------------------------------------------------------------ run

    public void Run(IReadOnlyCollection<string>? checks = null)
    {
        bool On(string c) => checks is null || checks.Count == 0 || checks.Contains(c);
        LoadTerrain();
        var props = new List<Prop>();
        var layers = _editor.ReadAll();
        _log($"{layers.Count} project layers");
        foreach (var (layer, entities) in layers)
            foreach (var e in entities)
            {
                if (e.Transform is not var (p, _, s)) continue;
                var model = e.Component("ECMesh")?["model_path"];
                if (string.IsNullOrEmpty(model)) continue;
                var info = Model(model)!;
                if (info.Problem is not null)
                {
                    // A model that loads with no meshes (vanilla water_lily_2) draws nothing: info; missing / broken: error.
                    if (On("asset")) Add("asset", info.Problem == "no meshes" ? Info : Error, e, layer.Name, null, model, p[0], p[2], 0, "model " + info.Problem);
                    continue;
                }
                props.Add(new Prop(e, layer.Name, layer.Id, model, p[0], p[1], p[2], s[1], Math.Max(Math.Abs(s[0]), Math.Abs(s[2])), info));
            }
        _log($"{props.Count} props with models");

        var mountains = props.Where(p => p.Info.TerrainOffset > 0).ToList();
        var allMountains = props.Where(p => Category(p.Model) == "mountain").ToList();
        if (_lf is not null)
        {
            if (On("floating-mountain") || On("buried-mountain")) CheckMountains(mountains, On);
            if (On("buried-prop") || On("floating-prop")) CheckPropHeights(props, allMountains, On);
            if (On("prop-in-sea") && _sea is not null) CheckSea(props);
        }
        if (_hex is not null)
        {
            if (On("wrong-region")) CheckRegions(props);
            if (On("on-city-footprint")) CheckCityFootprints(props);
            if (On("city") || On("road-river")) CheckHex(On);
            if (On("city-dressing")) CheckDressing(props);
        }
        if (On("duplicate")) CheckDuplicates(props);
        if (On("scale-outlier")) CheckScales(props);
    }

    private void Add(string check, string severity, TerryEntityData e, string layer, string? region, string? model,
                     double x, double z, double value, string note, JsonObject? fix = null) =>
        Findings.Add(new AuditFinding(check, severity, e.Id, layer, region, model, x, z, value, note, fix));

    private string? RegionAt(double x, double z) => _hex?.RegionAt(x, z);

    private static JsonObject SetY(Prop p, double y) => new()
    {
        ["op"] = "set", ["id"] = p.Entity.Id, ["in_layer"] = p.LayerId,
        ["fields"] = new JsonObject { ["ECTransform.position"] = Position(p, y) },
    };

    private static JsonObject Delete(Prop p) => new() { ["op"] = "delete", ["id"] = p.Entity.Id, ["in_layer"] = p.LayerId };

    /// <summary>The position string with only y replaced (x and z as written in the layer).</summary>
    private static string Position(Prop p, double y)
    {
        var raw = p.Entity.Component("ECTransform")?["position"]?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var I = CultureInfo.InvariantCulture;
        return raw is { Length: 3 }
            ? $"{raw[0]} {y.ToString("F5", I)} {raw[2]}"
            : $"{p.X.ToString("F4", I)} {y.ToString("F5", I)} {p.Z.ToString("F4", I)}";
    }

    // ------------------------------------------------------------------ checks

    /// <summary>LF-offset mountains: floating (base &gt; 0.3 above the terrain) or buried (top ≤ 0.05 above it);
    /// floating ones resting on another mountain are reported as info without a fix.</summary>
    private void CheckMountains(List<Prop> mountains, Func<string, bool> on)
    {
        foreach (var m in mountains)
        {
            var off = m.Info.TerrainOffset;
            double baseY = m.Y + m.ScaleY * m.Info.MinY, top = m.Y + m.ScaleY * m.Info.MaxY, height = top - baseY;
            // With off < 1 the terrain is only partly added: compare against the remainder.
            var rest = (1 - off) * Ground(m.X, m.Z);
            baseY -= rest; top -= rest;
            if (on("floating-mountain") && baseY > 0.3)
            {
                var under = mountains.FirstOrDefault(o => !ReferenceEquals(o, m)
                    && Math.Sqrt((o.X - m.X) * (o.X - m.X) + (o.Z - m.Z) * (o.Z - m.Z)) < o.Info.Radius * o.ScaleXZ * 0.7
                    && o.Y + o.ScaleY * o.Info.MaxY >= baseY - 0.2);
                if (under is not null)
                    Add("floating-mountain", Info, m.Entity, m.Layer, RegionAt(m.X, m.Z), m.Model, m.X, m.Z, baseY, $"rests on mountain {under.Entity.Id}");
                else
                    Add("floating-mountain", Warning, m.Entity, m.Layer, RegionAt(m.X, m.Z), m.Model, m.X, m.Z, baseY,
                        $"base {baseY:F2} above the terrain", SetY(m, m.Y - baseY - 0.15 * height));
            }
            if (on("buried-mountain") && top <= 0.05)
                Add("buried-mountain", Warning, m.Entity, m.Layer, RegionAt(m.X, m.Z), m.Model, m.X, m.Z, top,
                    $"top {top:F2} relative to the terrain", SetY(m, m.Y - baseY - 0.15 * height));
        }
    }

    /// <summary>Absolute props: entirely under the ground (any category but water pieces), or rocks hanging clearly
    /// above it (not within a mountain's footprint).</summary>
    private void CheckPropHeights(List<Prop> props, List<Prop> mountains, Func<string, bool> on)
    {
        var grid = new SpatialGrid<Prop>(4);
        foreach (var m in mountains) grid.Add(m.X, m.Z, m);
        foreach (var p in props)
        {
            if (p.Info.TerrainOffset > 0) continue;
            var cat = Category(p.Model);
            var ground = Ground(p.X, p.Z);
            if (!HeightOffsets.TryGetValue(cat, out var list)) HeightOffsets[cat] = list = [];
            list.Add(p.Y - ground);
            double baseY = p.Y + p.ScaleY * p.Info.MinY, top = p.Y + p.ScaleY * p.Info.MaxY, height = Math.Max(1e-4, top - baseY);
            if (on("buried-prop") && top < ground - 0.2 && height > 0.01 && !WaterModel(p.Model))
                Add("buried-prop", Warning, p.Entity, p.Layer, RegionAt(p.X, p.Z), p.Model, p.X, p.Z, top - ground,
                    $"top {ground - top:F2} under the ground ({cat})", SetY(p, p.Y + (ground - baseY) - 0.05 * height));
            // Rocks only: trees also stand on mountain-tile pillars and other props (vanilla: thousands sit 1-11 above the lf).
            if (on("floating-prop") && cat == "rock" && !p.Layer.Contains("_sea_") && !p.Layer.Contains("riv_") && baseY > ground + Math.Max(0.15, 0.5 * height))
            {
                var onMountain = grid.Near(p.X, p.Z, 30).Any(m =>
                    Math.Sqrt((m.X - p.X) * (m.X - p.X) + (m.Z - p.Z) * (m.Z - p.Z)) < m.Info.Radius * m.ScaleXZ * 1.1);
                if (!onMountain)
                    Add("floating-prop", Warning, p.Entity, p.Layer, RegionAt(p.X, p.Z), p.Model, p.X, p.Z, baseY - ground,
                        $"base {baseY - ground:F2} above the ground ({cat})", SetY(p, p.Y + (ground - baseY) - 0.05 * height));
            }
        }
    }

    /// <summary>Land props (not boats, ports, bridges or water plants) standing where the sea surface is above the ground.</summary>
    private void CheckSea(List<Prop> props)
    {
        foreach (var p in props)
        {
            var cat = Category(p.Model);
            if (cat is not ("tree" or "building") || WaterModel(p.Model) || p.Layer.Contains("_sea_") || p.Layer.Contains("riv_")) continue;
            var ground = Ground(p.X, p.Z);
            var sea = Sample(_sea, p.X, p.Z);
            if (sea > ground + 0.2 && sea > 0)
                Add("prop-in-sea", Warning, p.Entity, p.Layer, RegionAt(p.X, p.Z), p.Model, p.X, p.Z, sea - ground,
                    $"{cat} under {sea - ground:F2} of water");
        }
    }

    /// <summary>Props of a region layer standing more than two hexes outside that region (BOB files objects by
    /// position, so the layer only matters for editing and tag gating: info only).</summary>
    private void CheckRegions(List<Prop> props)
    {
        var land = _hex!.Hex.LandRegions.ToHashSet(StringComparer.Ordinal);
        var spacing = HexSpacing();
        foreach (var p in props)
        {
            if (!land.Contains(p.Layer)) continue;
            var region = RegionAt(p.X, p.Z);
            if (region == p.Layer) continue;
            var near = false;
            for (var a = 0; a < 12 && !near; a++)
            {
                var ang = a * Math.PI / 6;
                near = RegionAt(p.X + Math.Cos(ang) * 2 * spacing, p.Z + Math.Sin(ang) * 2 * spacing) == p.Layer;
            }
            if (!near)
                Add("wrong-region", Info, p.Entity, p.Layer, region, p.Model, p.X, p.Z, 0, $"in {region ?? "no region"}, layer {p.Layer}");
        }
    }

    private double HexSpacing()
    {
        var (x0, z0) = _hex!.HexCentre(10, 10);
        var (x1, z1) = _hex.HexCentre(10, 11);
        return Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
    }

    /// <summary>
    /// research/main190/town_props_scan.py: a town is its slot and sprawl hexes. Mountain / rock meshes must not reach
    /// (radius × scale × 0.7) within one hex of a town hex; vegetation must not stand within two hexes of one or on
    /// a land road hex. Stock regions (3k_*) are dressed by CA on purpose: info; others warning.
    /// </summary>
    private void CheckCityFootprints(List<Prop> props)
    {
        var hex = _hex!.Hex;
        var spacing = HexSpacing();
        var towns = new SpatialGrid<(double X, double Z)>(2);
        for (var r = 0; r < hex.Height; r++)
            for (var c = 0; c < hex.Width; c++)
                if (hex.TownAt(c, r))
                {
                    var (x, z) = _hex.HexCentre(c, r);
                    towns.Add(x, z, (x, z));
                }
        foreach (var p in props)
        {
            var cat = Category(p.Model);
            string? why = null;
            if (cat is "mountain" or "rock")
            {
                var reach = p.Info.Radius * p.ScaleXZ * 0.7 + spacing;
                if (towns.Near(p.X, p.Z, reach).Any(t => Math.Sqrt((t.X - p.X) * (t.X - p.X) + (t.Z - p.Z) * (t.Z - p.Z)) < reach))
                    why = $"{cat} reaches within a hex of a town";
            }
            else if (cat == "tree")
            {
                var (c, r) = _hex.HexAt((float)p.X, (float)p.Z);
                if (towns.Near(p.X, p.Z, 2 * spacing).Any(t => Math.Sqrt((t.X - p.X) * (t.X - p.X) + (t.Z - p.Z) * (t.Z - p.Z)) < 2 * spacing))
                    why = "tree within two hexes of a town";
                else if (hex.RoadAt(c, r) > 0 && hex.TerrainAt(c, r) == 0) why = "tree on a road hex";
            }
            if (why is null) continue;
            var region = RegionAt(p.X, p.Z);
            var stock = region?.StartsWith("3k_", StringComparison.Ordinal) == true;
            Add("on-city-footprint", stock ? Info : Warning, p.Entity, p.Layer, region, p.Model, p.X, p.Z, 0, why,
                cat is "tree" or "rock" ? Delete(p) : null);
        }
    }

    /// <summary>map.hex: land regions without a main settlement slot or with it off plain land; road edges along a
    /// river edge on a hex without the bridge bit (info: rivers on 3K mostly cross as water hexes).</summary>
    private void CheckHex(Func<string, bool> on)
    {
        var hex = _hex!.Hex;
        var slot0 = new Dictionary<int, List<(int C, int R)>>();
        for (var r = 0; r < hex.Height; r++)
            for (var c = 0; c < hex.Width; c++)
            {
                var i = hex.RegionIndexAt(c, r);
                if (hex.SlotAt(c, r) == 0 && i >= 0)
                {
                    if (!slot0.TryGetValue(i, out var l)) slot0[i] = l = [];
                    l.Add((c, r));
                }
                if (on("road-river") && (hex.RoadAt(c, r) & hex.RiverAt(c, r)) != 0 && !hex.BridgeAt(c, r))
                {
                    var (x, z) = _hex.HexCentre(c, r);
                    Findings.Add(new AuditFinding("road-river", Info, $"hex {c},{r}", "", hex.RegionName(i), null, x, z,
                        hex.RoadAt(c, r) & hex.RiverAt(c, r), "road edge along a river edge without a bridge"));
                }
            }
        if (!on("city")) return;
        for (var i = 0; i < hex.LandRegions.Count; i++)
        {
            var name = hex.LandRegions[i];
            if (name.Contains("non_playable")) continue;
            if (!slot0.TryGetValue(i, out var hexes))
            {
                Findings.Add(new AuditFinding("city", Warning, name, name, name, null, 0, 0, 0, "region has no main settlement slot"));
                continue;
            }
            var bad = hexes.Where(h => hex.TerrainAt(h.C, h.R) != 0 || hex.ImpassableAt(h.C, h.R)).ToList();
            if (bad.Count > 0)
            {
                // Vanilla ports have a few settlement hexes on the coast: warn only when most of the town is off plain land.
                var (x, z) = _hex.HexCentre(bad[0].C, bad[0].R);
                Findings.Add(new AuditFinding("city", bad.Count * 2 > hexes.Count ? Warning : Info, name, name, name, null, x, z, bad.Count,
                    $"{bad.Count} of {hexes.Count} settlement hexes not on passable plain land"));
            }
        }
    }

    /// <summary>A region layer's dressing (median prop position) more than three hexes from the region's city. Info only:
    /// vanilla region layers are not centred on their city (160 such layers on dlc07).</summary>
    private void CheckDressing(List<Prop> props)
    {
        var hex = _hex!.Hex;
        var spacing = HexSpacing();
        var cities = new Dictionary<string, (double X, double Z, int N)>(StringComparer.Ordinal);
        for (var r = 0; r < hex.Height; r++)
            for (var c = 0; c < hex.Width; c++)
                if (hex.SlotAt(c, r) == 0 && hex.RegionName(hex.RegionIndexAt(c, r)) is { } name)
                {
                    var (x, z) = _hex.HexCentre(c, r);
                    var t = cities.GetValueOrDefault(name);
                    cities[name] = (t.X + x, t.Z + z, t.N + 1);
                }
        foreach (var group in props.Where(p => Category(p.Model) is "building" or "other").GroupBy(p => p.Layer))
        {
            if (!cities.TryGetValue(group.Key, out var city)) continue;
            double cx = city.X / city.N, cz = city.Z / city.N;
            var xs = group.Select(p => p.X).OrderBy(v => v).ToList();
            var zs = group.Select(p => p.Z).OrderBy(v => v).ToList();
            double mx = xs[xs.Count / 2], mz = zs[zs.Count / 2];
            var d = Math.Sqrt((mx - cx) * (mx - cx) + (mz - cz) * (mz - cz));
            if (d > 3 * spacing)
                Findings.Add(new AuditFinding("city-dressing", Info, group.Key, group.Key, group.Key, null, mx, mz, d / spacing,
                    $"{group.Count()} dressing props centred {d / spacing:F1} hexes from the city at {cx:F1},{cz:F1}"));
        }
    }

    /// <summary>The same model, layer and transform (within 1e-3) more than once: every copy after the first.</summary>
    private void CheckDuplicates(List<Prop> props)
    {
        static string K(double v) => Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);
        foreach (var g in props.GroupBy(p =>
                 {
                     var t = p.Entity.Transform!.Value;
                     return $"{p.LayerId}|{p.Model.ToLowerInvariant()}|{K(p.X)}|{K(p.Y)}|{K(p.Z)}|{K(t.Rotation[0])}|{K(t.Rotation[1])}|{K(t.Rotation[2])}|{K(t.Scale[0])}|{K(t.Scale[1])}|{K(t.Scale[2])}";
                 }).Where(g => g.Count() > 1))
            foreach (var p in g.Skip(1))
                Add("duplicate", Info, p.Entity, p.Layer, RegionAt(p.X, p.Z), p.Model, p.X, p.Z, g.Count(),
                    $"same model and transform as {g.First().Entity.Id}", Delete(p));
    }

    /// <summary>A model's scale more than 3× away from that model's median scale on this map (needs ≥ 10 uses).</summary>
    private void CheckScales(List<Prop> props)
    {
        foreach (var g in props.GroupBy(p => p.Model.ToLowerInvariant()).Where(g => g.Count() >= 10))
        {
            var sorted = g.Select(p => p.ScaleXZ).OrderBy(v => v).ToList();
            var median = sorted[sorted.Count / 2];
            if (median <= 0) continue;
            foreach (var p in g)
                if (p.ScaleXZ > median * 3 || p.ScaleXZ < median / 3)
                    Add("scale-outlier", Info, p.Entity, p.Layer, RegionAt(p.X, p.Z), p.Model, p.X, p.Z, p.ScaleXZ / median,
                        $"scale {p.ScaleXZ:F3} vs median {median:F3}");
        }
    }

    // ------------------------------------------------------------------ output

    /// <summary>findings.csv, summary.json and one fix_&lt;check&gt;.json per check that has fixes.</summary>
    public void Write(string dir)
    {
        Directory.CreateDirectory(dir);
        var I = CultureInfo.InvariantCulture;
        static string Q(string? s) => s is null ? "" : s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder("check,severity,id,layer,region,model,x,z,value,note,has_fix\n");
        foreach (var f in Findings.OrderBy(f => f.Check).ThenByDescending(f => f.Value))
            sb.Append($"{f.Check},{f.Severity},{Q(f.Id)},{Q(f.Layer)},{Q(f.Region)},{Q(f.Model)},{f.X.ToString("F2", I)},{f.Z.ToString("F2", I)},{f.Value.ToString("F3", I)},{Q(f.Note)},{(f.Fix is null ? 0 : 1)}\n");
        File.WriteAllText(Path.Combine(dir, "findings.csv"), sb.ToString());
        foreach (var g in Findings.Where(f => f.Fix is not null).GroupBy(f => f.Check))
            File.WriteAllText(Path.Combine(dir, $"fix_{g.Key}.json"), new JsonArray(g.Select(f => (JsonNode)f.Fix!.DeepClone()).ToArray()).ToJsonString());
        File.WriteAllText(Path.Combine(dir, "summary.json"), Summary().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public JsonObject Summary()
    {
        var checks = new JsonObject();
        foreach (var g in Findings.GroupBy(f => f.Check).OrderBy(g => g.Key))
            checks[g.Key] = new JsonObject
            {
                ["total"] = g.Count(),
                ["error"] = g.Count(f => f.Severity == Error), ["warning"] = g.Count(f => f.Severity == Warning), ["info"] = g.Count(f => f.Severity == Info),
                ["fixable"] = g.Count(f => f.Fix is not null),
            };
        var heights = new JsonObject();
        foreach (var (cat, list) in HeightOffsets)
        {
            if (list.Count == 0) continue;
            var s = list.OrderBy(v => v).ToList();
            double P(double q) => Math.Round(s[(int)Math.Clamp(q * (s.Count - 1), 0, s.Count - 1)], 3);
            heights[cat] = new JsonObject { ["n"] = s.Count, ["p1"] = P(0.01), ["p5"] = P(0.05), ["median"] = P(0.5), ["p95"] = P(0.95), ["p99"] = P(0.99) };
        }
        return new JsonObject
        {
            ["project"] = _editor.TerryPath, ["checks"] = checks, ["y_minus_ground"] = heights,
            ["notes"] = new JsonArray(Notes.Select(n => (JsonNode)n).ToArray()),
        };
    }
}

/// <summary>Bucketed points for radius queries.</summary>
internal sealed class SpatialGrid<T>(double cell)
{
    private readonly Dictionary<(int, int), List<(double X, double Z, T V)>> _cells = [];

    public void Add(double x, double z, T v)
    {
        var k = ((int)Math.Floor(x / cell), (int)Math.Floor(z / cell));
        if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = [];
        l.Add((x, z, v));
    }

    public IEnumerable<T> Near(double x, double z, double radius)
    {
        int c0 = (int)Math.Floor((x - radius) / cell), c1 = (int)Math.Floor((x + radius) / cell);
        int r0 = (int)Math.Floor((z - radius) / cell), r1 = (int)Math.Floor((z + radius) / cell);
        for (var c = c0; c <= c1; c++)
            for (var r = r0; r <= r1; r++)
                if (_cells.TryGetValue((c, r), out var l))
                    foreach (var p in l) yield return p.V;
    }
}
