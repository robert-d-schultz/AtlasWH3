using System.Diagnostics;
using SkiaSharp;
using System.Text.Json;
using AtlasWH3.Core;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Exporters;
using AtlasWH3.Core.Operations;
using AtlasWH3.Core.Rendering;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Trees;
using AtlasWH3.Formats.Packs;

var paths = ProjectPaths.FromArgs(args, out args);
var command = args.Length > 0 ? args[0] : "info";

switch (command)
{
    case "info":
        Info(paths);
        break;
    case "trees-roundtrip":
        TreesRoundtrip(paths);
        break;
    case "find-textures":
        FindTextures(paths);
        break;
    case "render":
        Render(paths, args.Skip(1).ToArray());
        break;
    case "trees-test":
        TreesTest(paths);
        break;
    case "trees-decode":
        return TreesDecode(paths, args.Skip(1).ToArray());
    case "expand-test":
        ExpandTest(paths);
        break;
    case "export-ak":
        ExportAk(paths, args.Length > 1 ? args[1] : Path.Combine(paths.OutputRoot, "ak_test"));
        break;
    case "props-to-layers":
        PropsToLayers(paths, args.Skip(1).ToArray());
        break;
    case "compile-map":
        CompileMap(paths, args.Skip(1).ToArray());
        break;
    case "build-campaign":
        return BuildCampaign(paths, args.Skip(1).ToArray());
    case "diagnose-campaign":
        return DiagnoseCampaign(paths, args.Skip(1).ToArray());
    case "props-cells":
        PropsCells(paths, args.Skip(1).ToArray());
        break;
    case "gp-bodies":
        GpBodies(args.Skip(1).ToArray());
        break;
    case "gp-diff":
        GpDiff(args.Skip(1).ToArray());
        break;
    case "gp-body":
        GpBody(args.Skip(1).ToArray());
        break;
    case "gp-props-raw":
        GpPropsRaw(args.Skip(1).ToArray());
        break;
    case "gp-model-box":
        GpModelBox(args.Skip(1).ToArray());
        break;
    case "bmd-stats":
        BmdStats(paths);
        break;
    case "merge-pack":
        return MergePack(args.Skip(1).ToArray());
    case "make-pack":
        return MakePack(args.Skip(1).ToArray());
    case "props-dump":
        PropsDump(paths, args.Skip(1).ToArray());
        break;
    case "parity":
        return ParityCheck(args.Skip(1).ToArray());
    case "validate-tilemap":
        return ValidateTilemap(paths, args.Skip(1).ToArray());
    case var c when PropCommands.Names.Contains(c):
        return PropCommands.Run(paths, c, args.Skip(1).ToArray());
    case var c when LakeCommands.Names.Contains(c):
        return LakeCommands.Run(paths, c, args.Skip(1).ToArray());
    case var c when AuditCommands.Names.Contains(c):
        return AuditCommands.Run(paths, c, args.Skip(1).ToArray());
    case var c when AssetCommands.Names.Contains(c):
        return AssetCommands.Run(paths, c, args.Skip(1).ToArray());
    case var c when TerryCommands.Names.Contains(c):
        return TerryCommands.Run(paths, c, args.Skip(1).ToArray());
    case var c when TileCommands.Names.Contains(c):
        return TileCommands.Run(paths, c, args.Skip(1).ToArray());
    case var c when BuildCommands.Names.Contains(c):
        return BuildCommands.Run(paths, c, args.Skip(1).ToArray());
    case var c when AiPathfindingCommands.Names.Contains(c):
        return AiPathfindingCommands.Run(paths, c, args.Skip(1).ToArray());
    default:
        Console.WriteLine("Commands: info | trees-roundtrip | find-textures | render [mapX mapY scale width height]");
        Console.WriteLine("          props-to-layers [targetDir|ak] [shiftX shiftZ]");
        Console.WriteLine("          compile-map [--out <dir>]      BOB-less compiled terrain (pack layout)");
        Console.WriteLine("          build-campaign [--steps a,b] [--out <dir>] [--accept-tilemap code,..] [--fresh-trees] [--river-geometry bob|wide] [--global-mesh bob|native] [--json]   native replacement for BOB's campaign actions (--fresh-trees: compute every tree height, no reference reuse; --river-geometry wide: wider game-valid river water instead of BOB's; --global-mesh native: the earlier game-valid land/sea meshes instead of BOB's)");
        Console.WriteLine("          diagnose-campaign [--out <dir>] [--json]              per-step input check");
        Console.WriteLine("          build --project <file.atlaswh3> [--segments validate,compile,custom,pack,install] [--steps a,b] [--custom name,..]");
        Console.WriteLine("                [--out <dir>] [--pack-output <file>] [--json]      a project's build (as the GUI's Build window)");
        Console.WriteLine("          new-project <file.atlaswh3>                          project with the default build profile (global --map / --ak)");
        Console.WriteLine("          parity <builtDir> <referenceDir> [--mask-junk] [--json]");
        Console.WriteLine("          hlp-spd [--in <dir: pathfinding.ppd + map_data.esf>] [--out <dir>] [--compare <dir with reference hlp/spd>] [--only hlp|spd] [--legacy-stl]");
        Console.WriteLine("                                         campaign AI pathfinding data (hlp_data.esf / spd_data.esf) without the game");
        Console.WriteLine("          validate-tilemap [--tilemap <png>] [--climate-dir <dir>] [--db <_tile_database>] [--simulate] [--overlay <png>] [--tilemap-only] [--json]");
        Console.WriteLine("                                         pre-flight check of tile_map.png before Tilemap (exit 1 on errors)");
        Console.WriteLine("          validate-tilemap --pass-order <db|0-5|tile:<name>>   tile-matching diagnostics (candidate order, tile links)");
        Console.WriteLine("          trees-decode [--list <campaign_tree_list>] [--out <tree tif>]   compiled tree list -> AK CampaignTree map");
        Console.WriteLine("          tiles-info | tiles-get | tiles-edit --ops <json> [--dry-run|--force|--allow-warnings|--preview] | tiles-validate");
        Console.WriteLine("          tiles-preview | tiles-undo | tiles-checkpoint | tiles-rollback | tiles-history | tiles-replay | tiles-holes  [--tilemap <png>]");
        Console.WriteLine("          props-layers | props-list | props-get | props-models | props-ground | props-edit --ops <json>");
        Console.WriteLine("          props-undo | props-checkpoint <label> | props-rollback <label> | props-history | props-preview   (AK layer prop editing, JSON out)");
        Console.WriteLine("          entity-types | entity-type <type> | entity-project | entity-layers [--all] | entity-get <id>..");
        Console.WriteLine("          entity-query [--type a,b] [--layer l] [--where ECX.f~v].. [--rect x0,z0,x1,z1] [--full] | entity-edit --ops <json>");
        Console.WriteLine("          entity-undo | entity-checkpoint <label> | entity-rollback <label> | entity-history   (generic Terry editing; --project <.terry>)");
        Console.WriteLine("          prefab-list [--filter s] | prefab-get <key>   the project database's prefab library (place/expand/make via entity-edit ops)");
        Console.WriteLine("          lake-build [--project .terry] [--assets dir] [--hex map.hex --bounds minx,miny,maxx] [--layer l] [--apply]   raised lakes: one flat water mesh each + shore shaping");
        Console.WriteLine("          sea-carve --at x,z [--radius r] [--depth d] [--apply]   sea inlet with banks above sea level: carve below 0 so sea water shows, drop its discs");
        Console.WriteLine("          terrain-scale --at x,z [--radius r] --factor f [--apply]   scale an island/land mass's heights above sea level (props follow)");
        Console.WriteLine("          river-bed [--min-depth d] [--apply]   lower navigable rivers' sea-map bed to >= d under the water (pale water fix)");
        Console.WriteLine("          map-audit [--project .terry] [--out dir] [--checks a,b]   campaign map defects (heights, mountains, sea, regions, towns, duplicates, assets) + fix ops; --pack p for mod packs");
        Console.WriteLine("          asset-info <path> | asset-find <filter> [--ext e] | asset-preview <model> [--out png --size --yaw --pitch --lod]");
        Console.WriteLine("          asset-census [--ext .rigid_model_v2|.wsmodel|.material|.dds]   game assets (+ --mod-pack p, --asset-root dir)");
        Console.WriteLine("          terry-schema [--root <dir>].. [--out <json>]   regenerate the component schema from the kit's raw_data");
        Console.WriteLine("Global:   --ak <assembly kit root>  --map <map name>  --root <compiled root>");
        break;
}
return 0;

// Runs the native campaign build. Exit code 0 only when every selected step succeeded.
static int BuildCampaign(ProjectPaths paths, string[] a)
{
    a = TakeOption(a, "--out", out var outDir);
    a = TakeOption(a, "--steps", out var steps);
    a = TakeOption(a, "--accept-tilemap", out var accept);
    a = TakeOption(a, "--river-geometry", out var riverGeometry);
    a = TakeOption(a, "--global-mesh", out var globalMesh);
    var freshTrees = TakeFlag(ref a, "--fresh-trees");
    var json = TakeFlag(ref a, "--json");
    var sw = Stopwatch.StartNew();
    var ctx = new CampaignBuildContext(paths, outDir, json ? Console.Error.WriteLine : Console.WriteLine)
    {
        AcceptedTileMapIssues = (accept ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(),
        ReuseTreeHeights = !freshTrees,
        RiverGeometry = riverGeometry ?? "bob",
        GlobalMeshGeometry = globalMesh ?? "bob",
    };
    var outcomes = new CampaignBuildPipeline().Run(ctx, steps?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    if (json)
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            map = paths.MapName, target = ctx.TargetRoot, seconds = sw.Elapsed.TotalSeconds,
            steps = outcomes.Select(o => new
            {
                step = o.Step, status = o.Status, problems = o.Problems,
                seconds = o.Result?.Elapsed.TotalSeconds, notes = o.Result?.Notes,
                written = o.Result?.Written.Select(f => new { path = Path.GetRelativePath(ctx.TargetRoot, f), bytes = new FileInfo(f).Length }),
            }),
        }, new JsonSerializerOptions { WriteIndented = true }));
    else
    {
        foreach (var o in outcomes)
        {
            Console.WriteLine($"{o.Step,-13} {o.Status,-8} {(o.Result is { } r ? $"{r.Elapsed.TotalSeconds,6:F1} s, {r.Written.Count} files" : "")}");
            foreach (var p in o.Problems) Console.WriteLine($"    ! {p}");
            foreach (var n in o.Result?.Notes ?? []) Console.WriteLine($"    note: {n}");
        }
        Console.WriteLine($"target {ctx.TargetRoot}, total {sw.Elapsed.TotalSeconds:F1} s");
    }
    return outcomes.All(o => o.Status == "ok") ? 0 : 1;
}

static int DiagnoseCampaign(ProjectPaths paths, string[] a)
{
    a = TakeOption(a, "--out", out var outDir);
    var json = TakeFlag(ref a, "--json");
    var ctx = new CampaignBuildContext(paths, outDir);
    var report = CampaignBuildPipeline.Diagnose(ctx);
    if (json)
        Console.WriteLine(JsonSerializer.Serialize(report.Select(r => new
        {
            step = r.Step, native = r.Native, ready = r.Problems.Count == 0, problems = r.Problems,
            replaces = CampaignBuildPipeline.Find(r.Step).ReplacesBobAction,
        }), new JsonSerializerOptions { WriteIndented = true }));
    else
        foreach (var r in report)
        {
            Console.WriteLine($"{r.Step,-13} {(r.Native ? "native " : "PENDING")} {(r.Problems.Count == 0 ? "ready" : "")}");
            foreach (var p in r.Problems) Console.WriteLine($"    ! {p}");
        }
    return 0;
}

// Pre-flight check of a campaign tile map and the inputs BOB's Tilemap reads with it. Exit 1 when there are errors.
static int ValidateTilemap(ProjectPaths paths, string[] a)
{
    a = TakeOption(a, "--pass-order", out var passOrder);
    if (passOrder != null)
    {
        var sim = new AtlasWH3.Core.Campaign.TileMapCheck.TileMatchSimulator(AtlasWH3.Core.Campaign.TileMapCheck.TileMapValidator.LoadDatabase(paths));
        if (passOrder.StartsWith("tile:"))
        {
            var db = AtlasWH3.Core.Campaign.TileMapCheck.TileMapValidator.LoadDatabase(paths);
            foreach (var t in db.Tiles.Where(t => t.File.Contains(passOrder[5..], StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"{t.File} {t.Name} set={t.TileSet} {t.Width}x{t.Height} mask='{t.Mask}' rot={t.RandomRotatable} vars={t.Variations.Count}");
                foreach (var x in t.LinkTargets) Console.WriteLine($"   target {x.TargetSet} ({x.X},{x.Y})");
                foreach (var l in t.Links) Console.WriteLine($"   link {l.LinkSet} ({l.X},{l.Y}) base ({l.BaseX},{l.BaseY}) entry={l.IsEntry} {l.Test}");
            }
            return 0;
        }
        foreach (var line in passOrder == "db" ? sim.DatabaseOrder : sim.PassOrder(int.Parse(passOrder))) Console.WriteLine(line);
        return 0;
    }
    a = TakeOption(a, "--tilemap", out var tileMap);
    a = TakeOption(a, "--climate-dir", out var climateDir);
    a = TakeOption(a, "--db", out var dbDir);
    a = TakeOption(a, "--overlay", out var overlay);
    a = TakeOption(a, "--simulation-csv", out var simulationCsv);
    var simulate = TakeFlag(ref a, "--simulate");
    var tileMapOnly = TakeFlag(ref a, "--tilemap-only");
    var json = TakeFlag(ref a, "--json");
    var sw = Stopwatch.StartNew();
    var report = AtlasWH3.Core.Campaign.TileMapCheck.TileMapValidator.Run(paths, new()
    {
        TileMap = tileMap, ClimateDir = climateDir, DatabaseDir = dbDir, Simulate = simulate || simulationCsv != null,
        TileMapOnly = tileMapOnly, SimulationCsv = simulationCsv, Log = Console.Error.WriteLine,
    });
    if (overlay != null && report.HexWidth > 0)
        AtlasWH3.Core.Campaign.TileMapCheck.TileMapValidator.WriteOverlay(report.TileMap, report, overlay);
    if (json)
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            map = report.Map, tileMap = report.TileMap, hexes = new[] { report.HexWidth, report.HexHeight },
            errors = report.Errors, warnings = report.Warnings, seconds = sw.Elapsed.TotalSeconds, overlay,
            findings = report.Findings, simulation = report.Simulation,
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    else
    {
        Console.WriteLine($"{report.TileMap}  {report.HexWidth}x{report.HexHeight} hexes");
        foreach (var f in report.Findings)
        {
            Console.WriteLine($"{f.Severity,-7} {f.Code,-20} {f.Message}");
            if (f.Hexes.Count > 0)
                Console.WriteLine($"        at {string.Join(" ", f.Hexes.Take(8).Select(h => $"({h[0]},{h[1]})"))}{(f.Count > 8 ? " ..." : "")}");
        }
        if (report.Simulation is { } s)
            Console.WriteLine($"simulation: {s.Placed:N0} tiles placed ({string.Join(", ", s.PlacedPerPass.Select(p => $"{p.Key} {p.Value:N0}"))}), {s.NoTilePoints:N0} points without a tile");
        Console.WriteLine($"{report.Errors} errors, {report.Warnings} warnings, {sw.Elapsed.TotalSeconds:F1} s");
    }
    return report.Errors > 0 ? 1 : 0;
}

static int ParityCheck(string[] a)
{
    var maskJunk = TakeFlag(ref a, "--mask-junk");
    var json = TakeFlag(ref a, "--json");
    if (a.Length < 2) { Console.Error.WriteLine("parity <builtDir> <referenceDir> [--mask-junk] [--json]"); return 2; }
    var results = Parity.Compare(a[0], a[1], maskJunk);
    if (json)
        Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    else
    {
        foreach (var r in results.Where(r => r.Status != "identical"))
            Console.WriteLine($"{r.Status,-12} {r.RelativePath}  built {r.BuiltSize:N0}  ref {r.ReferenceSize:N0}  first diff {r.FirstDiff}  bytes {r.DiffBytes:N0}");
        foreach (var g in results.GroupBy(r => r.Status)) Console.WriteLine($"{g.Key}: {g.Count()}");
    }
    return results.All(r => r.Status == "identical") ? 0 : 1;
}

// Dumps every prop / composite scene of a global_props.bin to CSV (for analysis scripts).
static void PropsDump(ProjectPaths paths, string[] a)
{
    var source = a.Length > 0 ? a[0] : paths.GlobalPropsBin;
    var target = a.Length > 1 ? a[1] : Path.Combine(paths.OutputRoot, "props_dump.csv");
    var gp = AtlasWH3.Formats.Props.GlobalProps.Load(source);
    var regions = gp.ReadRegions(paths.MapName);
    using (var nw = new StreamWriter(Path.ChangeExtension(target, ".nested.csv")))
        foreach (var (parent, child, m) in gp.NestedMatrices)
            nw.WriteLine($"{parent},{child},{string.Join(",", m.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}");
    using var w = new StreamWriter(target);
    w.WriteLine("kind,region,path,x,y,z,rx,ry,rz,sx,sy,sz,decal,tags,unseen_shroud,seen_shroud");
    var ci = System.Globalization.CultureInfo.InvariantCulture;
    string T(AtlasWH3.Formats.Props.PropTransform t) => string.Join(",", new double[] { t.X, t.Y, t.Z, t.RotX, t.RotY, t.RotZ, t.ScaleX, t.ScaleY, t.ScaleZ }.Select(v => v.ToString("R", ci)));
    foreach (var r in regions)
    {
        foreach (var p in r.Props)
            w.WriteLine($"prop,{r.Region},{p.Path},{T(p.Transform)},{(p.IsDecal ? 1 : 0)},\"{p.Tags}\",{(p.VisibleInUnseenShroud ? 1 : 0)},{(p.VisibleInSeenShroud ? 1 : 0)}");
        foreach (var c in r.CompositeScenes)
            w.WriteLine($"composite,{r.Region},{c.Path},{T(c.Transform)},0,\"{c.Tags}\",,");
    }
    if (File.Exists(paths.TreeList))
        foreach (var type in CampaignTreeList.Load(paths.TreeList).Types)
            foreach (var t in type.Instances)
                w.WriteLine($"tree,,{type.Name},{T(new AtlasWH3.Formats.Props.PropTransform(t.X, t.Y, t.Z))},0,\"\",,");
    Console.WriteLine(target);
}

// Per-body record counts of a global_props.bin: entry, bytes, nested, props, vfx, probes, lights, polys, sounds, scenes.
static void GpBodies(string[] a)
{
    var gp = AtlasWH3.Formats.Props.GlobalProps.Load(a[0]);
    using var w = new StreamWriter(a[1]);
    w.WriteLine("entry,bytes,nested,props,vfx,probes,lights,polys,sounds,scenes,types,seasons");
    foreach (var (name, body) in gp.Bodies())
    {
        var b = AtlasWH3.Formats.Props.BmdBody.Parse(body);
        w.WriteLine(string.Join(",", name, body.Length, b.Nested.Count, b.Props.Count, b.Vfx.Count, b.LightProbes.Count, b.PointLights.Count,
            b.PolyMeshes.Count, b.Sounds.Count, b.CompositeScenes.Count, "\"" + string.Join(" ", b.EnumTypes.Select(t => t.Name)) + "\"", "\"" + string.Join(" ", b.Seasons) + "\""));
    }
    Console.WriteLine(a[1]);
}

// Section-level diff of two global_props.bin files over their common entries: per section, equal / same records in another
// order / different records; plus prop path tables. Lists up to 12 example entries per class.
static void GpDiff(string[] a)
{
    var A = AtlasWH3.Formats.Props.GlobalProps.Load(a[0]).Bodies().ToDictionary(x => x.Name, x => x.Body);
    var B = AtlasWH3.Formats.Props.GlobalProps.Load(a[1]).Bodies().ToDictionary(x => x.Name, x => x.Body);
    var stats = new SortedDictionary<string, int>(); var examples = new Dictionary<string, List<string>>();
    void Count(string k, string entry) { stats[k] = stats.GetValueOrDefault(k) + 1; if (!examples.TryGetValue(k, out var l)) examples[k] = l = []; if (l.Count < 6) l.Add(entry.Split("bmd_objects.").Last()); }
    static string Cmp(List<byte[]> x, List<byte[]> y)
    {
        if (x.Count == y.Count && x.Zip(y).All(p => p.First.AsSpan().SequenceEqual(p.Second))) return "equal";
        var hx = x.Select(Convert.ToHexString).OrderBy(h => h, StringComparer.Ordinal).ToList();
        var hy = y.Select(Convert.ToHexString).OrderBy(h => h, StringComparer.Ordinal).ToList();
        return hx.SequenceEqual(hy) ? "reordered" : (x.Count == y.Count ? "differ" : "count differs");
    }
    foreach (var (name, ba) in A)
    {
        if (!B.TryGetValue(name, out var bb)) continue;
        if (ba.AsSpan().SequenceEqual(bb)) { Count("body identical", name); continue; }
        var x = AtlasWH3.Formats.Props.BmdBody.Parse(ba); var y = AtlasWH3.Formats.Props.BmdBody.Parse(bb);
        if (!x.Preamble.AsSpan().SequenceEqual(y.Preamble)) Count("preamble differs", name);
        if (!x.PropPaths.SequenceEqual(y.PropPaths)) Count(x.PropPaths.OrderBy(p => p).SequenceEqual(y.PropPaths.OrderBy(p => p)) ? "prop paths reordered" : "prop paths differ", name);
        foreach (var (sec, sx, sy) in new[] { ("nested", x.Nested, y.Nested), ("props", x.Props, y.Props), ("vfx", x.Vfx, y.Vfx), ("lights", x.PointLights, y.PointLights),
                                               ("sounds", x.Sounds, y.Sounds), ("scenes", x.CompositeScenes, y.CompositeScenes), ("probes", x.LightProbes, y.LightProbes), ("polys", x.PolyMeshes, y.PolyMeshes) })
        {
            var c = Cmp(sx, sy);
            if (c != "equal") Count($"{sec} {c}", name);
        }
    }
    foreach (var (k, v) in stats) Console.WriteLine($"{v,7} {k,-26} e.g. {string.Join(" ", examples[k])}");
}

// gp-props-raw <gp> <out.csv>: every prop record's entry, model path, x/z and its 9 matrix floats as raw hex.
static void GpModelBox(string[] a)
{
    // per-LOD mesh header bounds and vertex bounds of an .rigid_model_v2 (global_props quadtree box research)
    var rm = AtlasWH3.Formats.Models.RigidModel.Read(File.ReadAllBytes(a[0]));
    static string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "(" + BitConverter.SingleToInt32Bits(v).ToString("X8") + ")";
    for (var l = 0; l < rm.Lods.Count; l++)
        foreach (var m in rm.Lods[l].Meshes)
        {
            float[] lo = [float.MaxValue, float.MaxValue, float.MaxValue], hi = [float.MinValue, float.MinValue, float.MinValue];
            for (var i = 0; i < m.Positions.Length; i += 3)
                for (var k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], m.Positions[i + k]); hi[k] = Math.Max(hi[k], m.Positions[i + k]); }
            Console.WriteLine($"lod {l} hdr min {string.Join(",", m.BoundsMin.Select(F))} max {string.Join(",", m.BoundsMax.Select(F))} | vtx min {string.Join(",", lo.Select(F))} max {string.Join(",", hi.Select(F))} n {m.VertexCount}");
        }
}

static void GpPropsRaw(string[] a)
{
    using var w = new StreamWriter(a[1]);
    w.WriteLine("entry,path,x,z,m,flags");
    var ci = System.Globalization.CultureInfo.InvariantCulture;
    foreach (var (name, body) in AtlasWH3.Formats.Props.GlobalProps.Load(a[0]).Bodies())
    {
        var b = AtlasWH3.Formats.Props.BmdBody.Parse(body);
        foreach (var sc in b.CompositeScenes)
            w.WriteLine(string.Join(",", name, "scene:" + System.Text.Encoding.UTF8.GetString(sc, 52, BitConverter.ToUInt16(sc, 50)),
                BitConverter.ToSingle(sc, 38).ToString("R", ci), BitConverter.ToSingle(sc, 46).ToString("R", ci), "", Convert.ToHexString(sc, sc.Length - 12, 12)));
        foreach (var p in b.Props)
        {
            var path = b.PropPaths[(int)BitConverter.ToUInt32(p, 2)];
            w.WriteLine(string.Join(",", name, path, BitConverter.ToSingle(p, 60).ToString("R", ci), BitConverter.ToSingle(p, 68).ToString("R", ci),
                Convert.ToHexString(p, 24, 36), Convert.ToHexString(p, 72, 33)));
        }
    }
}

// One body of a global_props.bin: prop path table, then each prop record's path index, matrix translation (x, z) and
// raw bytes 0..24 (index, tags) and matrix, and the other sections' record counts; with a second file, where each
// record differs.
static void GpBody(string[] a)
{
    var body = AtlasWH3.Formats.Props.GlobalProps.Load(a[0]).Bodies().First(x => x.Name.EndsWith(a[1], StringComparison.Ordinal)).Body;
    var b = AtlasWH3.Formats.Props.BmdBody.Parse(body);
    Console.WriteLine($"types: {string.Join(" ", b.EnumTypes.Select(t => t.Name))} | seasons: {string.Join(" ", b.Seasons)}");
    for (var i = 0; i < b.PropPaths.Count; i++) Console.WriteLine($"  path[{i}] {b.PropPaths[i]}");
    foreach (var p in b.Props)
    {
        var idx = BitConverter.ToUInt32(p, 2);
        var x = BitConverter.ToSingle(p, 24 + 36); var z = BitConverter.ToSingle(p, 24 + 44);
        Console.WriteLine($"  prop idx {idx} x? {x:R} z? {z:R}  head {Convert.ToHexString(p, 0, 24)} m {Convert.ToHexString(p, 24, 48)}");
    }
    Console.WriteLine($"vfx {b.Vfx.Count} lights {b.PointLights.Count} scenes {b.CompositeScenes.Count} sounds {b.Sounds.Count} probes {b.LightProbes.Count} polys {string.Join(",", b.PolyMeshes.Select(x => x.Length))}");
    if (a.Length < 3) return;
    // gp-body <a> <entry> <b>: byte offsets where each section's records differ
    var o = AtlasWH3.Formats.Props.BmdBody.Parse(AtlasWH3.Formats.Props.GlobalProps.Load(a[2]).Bodies().First(x => x.Name.EndsWith(a[1], StringComparison.Ordinal)).Body);
    void Cmp(string what, List<byte[]> x, List<byte[]> y)
    {
        for (var i = 0; i < Math.Min(x.Count, y.Count); i++)
        {
            if (x[i].AsSpan().SequenceEqual(y[i])) continue;
            var offs = Enumerable.Range(0, Math.Min(x[i].Length, y[i].Length)).Where(k => x[i][k] != y[i][k]).ToList();
            Console.WriteLine($"  {what}[{i}] len {x[i].Length}/{y[i].Length} differ at {string.Join(",", offs.Take(24))}");
            foreach (var k in offs.Take(6)) Console.WriteLine($"      @{k}: {Convert.ToHexString(x[i], Math.Max(0, k - 4), Math.Min(12, x[i].Length - Math.Max(0, k - 4)))} | {Convert.ToHexString(y[i], Math.Max(0, k - 4), Math.Min(12, y[i].Length - Math.Max(0, k - 4)))}");
        }
        if (x.Count != y.Count) Console.WriteLine($"  {what} count {x.Count}/{y.Count}");
    }
    Cmp("prop", b.Props, o.Props); Cmp("vfx", b.Vfx, o.Vfx); Cmp("light", b.PointLights, o.PointLights);
    Cmp("scene", b.CompositeScenes, o.CompositeScenes); Cmp("sound", b.Sounds, o.Sounds);
}

// Every prop with the bmd it sits in (for working out BOB's cell / bucket assignment).
static void PropsCells(ProjectPaths paths, string[] a)
{
    var source = a.Length > 0 ? a[0] : paths.GlobalPropsBin;
    var target = a.Length > 1 ? a[1] : Path.Combine(paths.OutputRoot, "props_cells.csv");
    var gp = AtlasWH3.Formats.Props.GlobalProps.Load(source);
    var ci = System.Globalization.CultureInfo.InvariantCulture;
    using var w = new StreamWriter(target);
    w.WriteLine("region,bmd,path,x,y,z,sx,decal,snow_in,snow_out,destr_in,destr_out,seasons,tags,unseen,seen");
    foreach (var (region, _, objects) in gp.ReadBmdsSeparately(paths.MapName))
        for (var i = 0; i < objects.Props.Count; i++)
        {
            var p = objects.Props[i];
            var t = p.Transform;
            w.WriteLine(string.Join(",", region, objects.PropSources[i], p.Path, t.X.ToString("R", ci), t.Y.ToString("R", ci), t.Z.ToString("R", ci),
                t.ScaleX.ToString("R", ci), p.IsDecal ? 1 : 0, p.VisibleInsideSnow ? 1 : 0, p.VisibleOutsideSnow ? 1 : 0,
                p.VisibleInsideDestruction ? 1 : 0, p.VisibleOutsideDestruction ? 1 : 0, "\"" + p.Seasons + "\"", "\"" + p.Tags + "\"",
                p.VisibleInUnseenShroud ? 1 : 0, p.VisibleInSeenShroud ? 1 : 0));
        }
    // the other object kinds, same columns (path = vfx / scene / sound name, empty flags), for cell and bucket comparisons
    using var o = new StreamWriter(Path.ChangeExtension(target, ".other.csv"));
    o.WriteLine("kind,region,bmd,path,x,y,z,seasons,tags");
    string R(float v) => v.ToString("R", ci);
    foreach (var (region, bmd, objects) in gp.ReadBmdsSeparately(paths.MapName))
    {
        foreach (var v in objects.Vfx) o.WriteLine(string.Join(",", "vfx", region, bmd, v.Name, R(v.Transform.X), R(v.Transform.Y), R(v.Transform.Z), "\"" + v.Seasons + "\"", "\"" + v.Tags + "\""));
        foreach (var c in objects.CompositeScenes) o.WriteLine(string.Join(",", "scene", region, bmd, c.Path, R(c.Transform.X), R(c.Transform.Y), R(c.Transform.Z), "\"" + c.Seasons + "\"", "\"" + c.Tags + "\""));
        foreach (var l in objects.PointLights) o.WriteLine(string.Join(",", "light", region, bmd, "light", R(l.Transform.X), R(l.Transform.Y), R(l.Transform.Z), "\"" + l.Seasons + "\"", "\"" + l.Tags + "\""));
        foreach (var s in objects.Sounds) o.WriteLine(string.Join(",", "sound", region, bmd, s.Name, R(s.Transform.X), R(s.Transform.Y), R(s.Transform.Z), "\"\"", "\"\""));
        foreach (var pr in objects.LightProbes) o.WriteLine(string.Join(",", "probe", region, bmd, "probe", R(pr.Transform.X), R(pr.Transform.Y), R(pr.Transform.Z), "\"\"", "\"\""));
        foreach (var pm in objects.PolyMeshes) o.WriteLine(string.Join(",", "poly", region, bmd, pm.Material, R(pm.Transform.X), R(pm.Transform.Y), R(pm.Transform.Z), "\"\"", "\"\""));
    }
    Console.WriteLine(target);
}

// Distinct preambles, tag flag/mask pairs and the most common record shapes in global_props.bin (writer templates).
static void BmdStats(ProjectPaths paths)
{
    var gp = AtlasWH3.Formats.Props.GlobalProps.Load(paths.GlobalPropsBin);
    var preambles = new Dictionary<string, int>();
    var tagPairs = new Dictionary<(ulong, ulong), int>();
    var shapes = new Dictionary<string, Dictionary<string, int>>();
    void Count(string kind, byte[] rec, int from, int len)
    {
        var key = Convert.ToHexString(rec, from, Math.Min(len, rec.Length - from));
        if (!shapes.TryGetValue(kind, out var d)) shapes[kind] = d = new();
        d[key] = d.GetValueOrDefault(key) + 1;
    }
    foreach (var (name, body) in gp.Bodies())
    {
        var b = AtlasWH3.Formats.Props.BmdBody.Parse(body);
        var pk = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(b.Preamble));
        preambles[pk] = preambles.GetValueOrDefault(pk) + 1;
        foreach (var p in b.Props)
        {
            var f = BitConverter.ToUInt64(p, 8); var m = BitConverter.ToUInt64(p, 16);
            tagPairs[(f, m)] = tagPairs.GetValueOrDefault((f, m)) + 1;
            Count("prop head 72..105 (flags)", p, 72, 33);
            var sl = BitConverter.ToUInt16(p, 105);
            Count("prop string", p, 105, 2 + sl);
            Count("prop tail", p, 107 + sl, 24);
        }
        foreach (var r in b.Nested) Count("nested (tail 30)", r, r.Length - 30, 30);
        Count("after nested", b.AfterNested, 0, 28);
        Count("after vfx", b.AfterVfx, 0, 26);
        Count("emitters+area", b.EmittersAndPlayableArea, 0, 29);
        Count("trailing", b.Trailing, 0, b.Trailing.Length);
    }
    Console.WriteLine($"bodies {gp.Entries.Count}, distinct preambles {preambles.Count}: {string.Join(", ", preambles.Values.OrderDescending().Take(8))}");
    Console.WriteLine("tag (flags, mask) top: " + string.Join("  ", tagPairs.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"({kv.Key.Item1:x},{kv.Key.Item2:x})x{kv.Value}")));
    foreach (var (kind, d) in shapes)
        Console.WriteLine($"{kind}: {d.Count} distinct; top " + string.Join("  ", d.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key}x{kv.Value}")));
}

// Packs every file under <root> (paths relative to it) into an uncompressed PFH5 mod pack, then re-reads it.
static int MakePack(string[] a)
{
    if (a.Length < 2) { Console.Error.WriteLine("make-pack <root folder> <out.pack> [--exclude substring]..."); return 2; }
    var excludes = new List<string>();
    for (var i = 2; i + 1 < a.Length; i += 2) if (a[i] == "--exclude") excludes.Add(a[i + 1]);
    var s = AtlasWH3.Core.Build.PackBuilder.New(a[1], AtlasWH3.Core.Build.PackBuilder.FromFolder(a[0], excludes));
    Console.WriteLine($"{a[1]}: {s.Files} files, {s.Bytes:N0} bytes; re-read: all present {s.Verified}");
    return s.Verified ? 0 : 1;
}

// A copy of <base.pack> with every file under <override root> replacing (or adding to) the base's. Base files inside
// a --replace-dir folder that the override doesn't have are dropped (e.g. stale generated meshes).
static int MergePack(string[] a)
{
    if (a.Length < 3) { Console.Error.WriteLine("merge-pack <base.pack> <override root> <out.pack> [--exclude substring]... [--replace-dir pack/folder/]..."); return 2; }
    var excludes = new List<string>();
    var replaceDirs = new List<string>();
    for (var i = 3; i + 1 < a.Length; i += 2)
        if (a[i] == "--exclude") excludes.Add(a[i + 1]);
        else if (a[i] == "--replace-dir") replaceDirs.Add(a[i + 1]);
    var s = AtlasWH3.Core.Build.PackBuilder.Merge(a[0], a[2], AtlasWH3.Core.Build.PackBuilder.FromFolder(a[1], excludes), replaceDirs);
    Console.WriteLine($"{a[2]}: {s.Files} files ({s.Kept} kept from {Path.GetFileName(a[0])}, {s.Replaced} replaced, " +
                      $"{s.Added} added, {s.Dropped} stale dropped); re-read: all present {s.Verified}");
    return s.Verified ? 0 : 1;
}

static bool TakeFlag(ref string[] a, string name)
{
    var found = a.Contains(name, StringComparer.OrdinalIgnoreCase);
    a = a.Where(s => !s.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
    return found;
}

static void CompileMap(ProjectPaths paths, string[] a)
{
    a = TakeOption(a, "--out", out var outDir);
    var sw = Stopwatch.StartNew();
    var result = new CompiledTerrainExporter(paths).ExportRasters(outDir, Console.WriteLine);
    foreach (var note in result.Notes) Console.WriteLine("note: " + note);
    foreach (var file in result.Written) Console.WriteLine($"  {new FileInfo(file).Length,12:N0}  {Path.GetRelativePath(result.TargetDir, file)}");
    Console.WriteLine($"wrote {result.Written.Count} files to {result.TargetDir} in {sw.Elapsed.TotalSeconds:F1} s");
}

/// <summary>Removes "name value" from args (anywhere in the list) and returns the value, or null if absent.</summary>
static string[] TakeOption(string[] a, string name, out string? value)
{
    value = null;
    var i = Array.FindIndex(a, s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (i < 0) return a;
    if (i + 1 >= a.Length) throw new ArgumentException($"{name} needs a value");
    value = a[i + 1];
    return a.Take(i).Concat(a.Skip(i + 2)).ToArray();
}

static void Render(ProjectPaths paths, string[] a)
{
    var sw = Stopwatch.StartNew();
    var terrain = TerrainData.LoadVanilla(paths);
    var textures = TerrainTextureSet.Load(paths, terrain.TextureArrays, Console.WriteLine);
    Console.WriteLine($"loaded in {sw.ElapsedMilliseconds} ms");
    var renderer = new TerrainRenderer(terrain, textures);

    double ox = 0, oy = 0, scale = 4;
    int w = 1784, h = 1405;
    if (a.Length >= 5)
    {
        ox = double.Parse(a[0]); oy = double.Parse(a[1]); scale = double.Parse(a[2]);
        w = int.Parse(a[3]); h = int.Parse(a[4]);
    }
    var pixels = new uint[w * h];
    sw.Restart();
    renderer.Render(new Viewport(ox, oy, scale), w, h, pixels);
    Console.WriteLine($"rendered {w}x{h} in {sw.ElapsedMilliseconds} ms");

    var outPath = Path.Combine(paths.OutputRoot, "previews", $"render_{ox}_{oy}_{scale}.png");
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
    System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()).CopyTo(bmp.GetPixelSpan());
    using var data = bmp.Encode(SKEncodedImageFormat.Png, 90);
    using (var fs = File.Create(outPath)) data.SaveTo(fs);
    Console.WriteLine(outPath);
}

static void Info(ProjectPaths paths)
{
    var sw = Stopwatch.StartNew();
    var height = TerrainDds.ReadL16(paths.HeightMapDds);
    Console.WriteLine($"lf_height_map    {height.Width}x{height.Height}  min {height.Data.Min()} max {height.Data.Max()}  ({sw.ElapsedMilliseconds} ms)");
    var sea = TerrainDds.ReadL16(paths.SeaHeightMapDds);
    Console.WriteLine($"lf_sea_height    {sea.Width}x{sea.Height}  min {sea.Data.Min()} max {sea.Data.Max()}");
    var (group, extra) = TerrainDds.ReadBlend(paths.BlendDds);
    Console.WriteLine($"global_blend     {group.Width}x{group.Height}  groups used {group.Data.Distinct().Count()}  extra values {string.Join(",", extra.Data.Distinct().OrderBy(v => v))}");
    var arrays = TextureArrays.Load(paths.TextureArraysXml);
    Console.WriteLine($"texture groups   {arrays.Groups.Count}");
    var trees = CampaignTreeList.Load(paths.TreeList);
    Console.WriteLine($"trees            {trees.Types.Count} types, {trees.TotalInstances} instances, world {trees.WorldWidth}x{trees.WorldHeight}");
    var db = TreeDatabase.Load(paths.TreeIdsTsv, paths.TreeVariantsTsv);
    Console.WriteLine($"tree db          {db.Ids.Count} ids, {db.Variants.Count} variants");
    var missing = trees.Types.Where(t => !db.Ids.ContainsKey(t.Name)).Select(t => t.Name).ToList();
    Console.WriteLine($"tree types not in db: {(missing.Count == 0 ? "none" : string.Join(", ", missing))}");

    // Height-mapping sanity check: sampled heightmap vs tree y.
    var coords = new WorldCoords(trees.WorldWidth, trees.WorldHeight);
    double sumErr = 0; var n = 0;
    foreach (var inst in trees.Types.SelectMany(t => t.Instances).Take(20000))
    {
        var (c, r) = coords.ToPixel(inst.X, inst.Z, height.Width, height.Height);
        var h = height.GetClamped((int)c, (int)r);
        sumErr += Math.Abs(HeightScale.ToWorld(h) - inst.Y); n++;
    }
    Console.WriteLine($"mean |tree.y - heightmap| over {n} trees: {sumErr / n:F3}");
    Console.WriteLine($"total {sw.ElapsedMilliseconds} ms");
}

static void TreesTest(ProjectPaths paths)
{
    var terrain = TerrainData.LoadVanilla(paths);
    var trees = CampaignTreeList.Load(paths.TreeList);
    var db = TreeDatabase.Load(paths.TreeIdsTsv, paths.TreeVariantsTsv);
    var log = new AtlasWH3.Core.Editing.TreeEditLog();
    var textures = TerrainTextureSet.Load(paths, terrain.TextureArrays);
    var overlay = new TreeOverlay(trees, db);
    var renderer = new TerrainRenderer(terrain, textures);
    var view = new Viewport(3300, 2300, 0.6);
    const int w = 1000, h = 700;
    void Snapshot(string name)
    {
        var px = new uint[w * h];
        renderer.Render(view, w, h, px);
        overlay.Draw(view, w, h, px, terrain.Width, terrain.HeightPx, terrain.Coords);
        SavePng(Path.Combine(paths.OutputRoot, "previews", name), px, w, h);
    }

    var before = trees.TotalInstances;
    Snapshot("trees_before.png");

    var brush = new AtlasWH3.Core.Editing.TreeBrush(trees, db, log)
    {
        Mode = AtlasWH3.Core.Editing.TreeBrushMode.Scatter, Species = "bamboo_1", Radius = 60, Strength = 0.8, Density = 8,
    };
    brush.Begin(terrain, 3450, 2450);
    for (var x = 3450; x <= 3750; x += 15) brush.Dab(x, 2450 + (x - 3450) / 3.0);
    var scatter = brush.End();
    var afterScatter = trees.TotalInstances;

    brush.Mode = AtlasWH3.Core.Editing.TreeBrushMode.Erase;
    brush.Radius = 70; brush.Strength = 1;
    brush.Begin(terrain, 3500, 2650);
    brush.Dab(3500, 2650);
    brush.End();
    var afterErase = trees.TotalInstances;
    Console.WriteLine($"trees: {before:N0} -> scatter {afterScatter:N0} (+{afterScatter - before}) -> erase {afterErase:N0} (-{afterScatter - afterErase})");
    Snapshot("trees_after.png");

    var compiled = TreeExporter.WriteCompiled(trees, Path.Combine(paths.OutputRoot, "trees_test"), paths.MapName);
    var reread = CampaignTreeList.Load(compiled);
    Console.WriteLine($"compiled list: {compiled} ({new FileInfo(compiled).Length:N0} bytes), re-read {reread.TotalInstances:N0} trees, types {reread.Types.Count}");

    var target = Path.Combine(paths.OutputRoot, "ak_trees_test");
    if (Directory.Exists(target)) Directory.Delete(target, true);
    var export = new AkExporter(paths).Export(terrain, null, target, _ => { }, new TreeExportInput(trees, db, log));
    foreach (var note in export.Notes) Console.WriteLine("note: " + note);
}

static void ExpandTest(ProjectPaths paths)
{
    var target = Path.Combine(paths.OutputRoot, "ak_expand_test");
    if (Directory.Exists(target)) Directory.Delete(target, true);

    var terrain = TerrainData.LoadVanilla(paths);
    var trees = CampaignTreeList.Load(paths.TreeList);
    var firstTree = trees.Types[0].Instances[0];
    var pending = new PendingExpansion();
    var result = ExpandCanvas.Apply(terrain, trees, new HexPadding(60, 50, 0, 0));
    pending.Add(result);
    Console.WriteLine($"expanded to {terrain.Width}x{terrain.HeightPx}, world {result.NewWorldWidth:F3}x{result.NewWorldHeight:F3}, shift x {result.ShiftX:F3} z {result.ShiftZ:F3}");
    var movedTree = trees.Types[0].Instances[0];
    Console.WriteLine($"tree 0: ({firstTree.X:F3},{firstTree.Z:F3}) -> ({movedTree.X:F3},{movedTree.Z:F3})");

    // The tree must still sit on the same terrain pixel after the shift.
    var (c0, r0) = WorldCoords.Vanilla3K.ToPixel(firstTree.X, firstTree.Z, 7136, 5620);
    var (c1, r1) = terrain.Coords.ToPixel(movedTree.X, movedTree.Z, terrain.Width, terrain.HeightPx);
    Console.WriteLine($"tree pixel before ({c0:F2},{r0:F2}) after ({c1:F2},{r1:F2}) expected ({c0 + 480:F2},{r0 + 400:F2})");

    var export = new AkExporter(paths).Export(terrain, pending, target, Console.WriteLine);
    foreach (var note in export.Notes) Console.WriteLine("note: " + note);
    Console.WriteLine($"written {export.Written.Count} files");

    // Second export must not shift layers again.
    var again = new AkExporter(paths).Export(terrain, pending, target, _ => { });
    foreach (var note in again.Notes) Console.WriteLine("2nd export note: " + note);

    Console.WriteLine(pending.DescribeDbChanges(paths.MapName));
    var renderer = new TerrainRenderer(terrain, TerrainTextureSet.Load(paths, terrain.TextureArrays));
    var w = terrain.Width / 5; var h = terrain.HeightPx / 5;
    var pixels = new uint[w * h];
    renderer.Render(new Viewport(0, 0, 5), w, h, pixels);
    new TreeOverlay(trees, null).Draw(new Viewport(0, 0, 5), w, h, pixels, terrain.Width, terrain.HeightPx, terrain.Coords);
    SavePng(Path.Combine(paths.OutputRoot, "previews", "expanded.png"), pixels, w, h);
}

static void SavePng(string outPath, uint[] pixels, int w, int h)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
    System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()).CopyTo(bmp.GetPixelSpan());
    using var data = bmp.Encode(SKEncodedImageFormat.Png, 90);
    using var fs = File.Create(outPath);
    data.SaveTo(fs);
    Console.WriteLine(outPath);
}

static void ExportAk(ProjectPaths paths, string target)
{
    var sw = Stopwatch.StartNew();
    var terrain = TerrainData.LoadVanilla(paths);
    var result = new AkExporter(paths).Export(terrain, null, target, Console.WriteLine);
    Console.WriteLine($"exported {result.Written.Count} files to {result.TargetDir} in {sw.ElapsedMilliseconds} ms");

    // Verify: re-read what was written and compare with the in-memory rasters and the existing AK sources.
    var project = AtlasWH3.Formats.Terry.TerryProject.Load(Path.Combine(target, paths.MapName + ".terry"));
    var heightTif = Path.Combine(target, Path.GetFileName(project.LayerTifPath(project.Find("LowFrequencyHeight")!)));
    var blendTif = Path.Combine(target, Path.GetFileName(project.LayerTifPath(project.Find("BlendCampaign")!)));
    var h = TiffMap.ReadGray16(heightTif);
    Console.WriteLine($"height re-read identical: {h.Data.AsSpan().SequenceEqual(terrain.Height.Data)}");
    var (b, _) = TiffMap.ReadPalette8(blendTif);
    Console.WriteLine($"blend re-read identical: {b.Data.AsSpan().SequenceEqual(terrain.BlendGroup.Data)}");

    var akHeight = TiffMap.ReadGray16(Path.Combine(paths.AkTerrainDir, Path.GetFileName(heightTif)));
    var diffs = 0; var maxDiff = 0;
    for (var i = 0; i < akHeight.Data.Length; i++)
    {
        var d = Math.Abs(akHeight.Data[i] - terrain.Height.Data[i]);
        if (d > 0) { diffs++; maxDiff = Math.Max(maxDiff, d); }
    }
    Console.WriteLine($"vs current AK height tif: {diffs:N0} pixels differ, max diff {maxDiff}");
    var (akBlend, _) = TiffMap.ReadPalette8(Path.Combine(paths.AkTerrainDir, Path.GetFileName(blendTif)));
    Console.WriteLine($"vs current AK blend tif identical: {akBlend.Data.AsSpan().SequenceEqual(terrain.BlendGroup.Data)}");
}

// Regenerates the AK region layers from vanilla global_props.bin with their building/settlement tags.
// Default target is output\props_layers (dry run); "ak" writes into the assembly kit folder after a backup.
static void PropsToLayers(ProjectPaths paths, string[] a)
{
    var sw = Stopwatch.StartNew();
    var target = a.Length > 0 ? a[0] : Path.Combine(paths.OutputRoot, "props_layers");
    if (target == "ak") target = paths.AkTerrainDir;
    (double, double)? shift = a.Length >= 3 ? (double.Parse(a[1]), double.Parse(a[2])) : null;
    var result = new PropLayerExporter(paths).Export(target, shift, Console.WriteLine);
    foreach (var (tags, count) in result.TaggedByTags.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"{count,7:N0}  {tags}");
    foreach (var note in result.Notes) Console.WriteLine("note: " + note);
    Console.WriteLine($"wrote {result.Written.Count} layers to {result.TargetDir} in {sw.ElapsedMilliseconds} ms" +
                      (result.BackupDir != null ? $" (backup: {result.BackupDir})" : ""));
}

// Reverses a compiled trees.campaign_tree_list into the AK CampaignTree map BOB would need to make it, and checks
// that regenerating from that map (with the list's own heights) gives the same bytes.
static int TreesDecode(ProjectPaths paths, string[] a)
{
    a = TakeOption(a, "--list", out var listPath);
    a = TakeOption(a, "--out", out var outPath);
    listPath ??= paths.TreeList;
    var db = TreeDatabase.Load(paths.TreeIdsTsv, paths.TreeVariantsTsv, paths.SeasonsTsv);
    var original = File.ReadAllBytes(listPath);
    var list = CampaignTreeList.Read(original);
    // The grid is fixed by the world width and the hex count; the hex count follows from the world height.
    var columns = (int)MathF.Round(list.WorldWidth / (595.1f / 1784f)) / 2;
    var probe = new AtlasWH3.Core.Campaign.Trees.HexGrid(columns, 1, list.WorldWidth);
    var rows = (int)MathF.Round(list.WorldHeight / probe.RowStep - 0.5f);
    var grid = probe with { Rows = rows };
    if (grid.WorldHeight != list.WorldHeight)
        Console.WriteLine($"warning: grid {columns}x{rows} gives height {grid.WorldHeight}, the list says {list.WorldHeight}");
    var (colours, heights) = AtlasWH3.Core.Campaign.Trees.CampaignTreeGenerator.Decode(list, grid, db);
    var rebuilt = AtlasWH3.Core.Campaign.Trees.CampaignTreeGenerator.Generate(colours, grid, db,
        (col, row, _, _) => heights[row * grid.Columns + col]).ToBytes();
    var same = original.AsSpan().SequenceEqual(rebuilt);
    Console.WriteLine($"{list.TotalInstances:N0} trees, {list.Types.Count} types, hex grid {grid.Columns}x{grid.Rows}");
    Console.WriteLine(same ? $"regenerated list: byte-identical ({original.Length:N0} bytes)"
                           : $"regenerated list: DIFFERS ({original.Length:N0} vs {rebuilt.Length:N0} bytes)");
    outPath ??= Path.Combine(paths.OutputRoot, "trees_decoded", $"{paths.MapName}.tree.tif");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    var (map, palette) = AtlasWH3.Core.Campaign.Trees.CampaignTreeGenerator.WriteTreeMap(
        colours, grid, grid.Columns * 2, grid.Rows * 2 + 1, db, AkExporter.NoTreeIndex);
    TiffMap.WritePalette8(outPath, map, palette, lzw: true);
    Console.WriteLine($"tree map {map.Width}x{map.Height} -> {outPath}");
    return same ? 0 : 1;
}

static void TreesRoundtrip(ProjectPaths paths)
{
    var original = File.ReadAllBytes(paths.TreeList);
    var rewritten = CampaignTreeList.Read(original).ToBytes();
    Console.WriteLine(original.AsSpan().SequenceEqual(rewritten)
        ? $"OK: byte-identical ({original.Length:N0} bytes)"
        : $"MISMATCH: original {original.Length:N0} bytes, rewritten {rewritten.Length:N0} bytes");
}

static void FindTextures(ProjectPaths paths)
{
    var arrays = TextureArrays.Load(paths.TextureArraysXml);
    var set = PackSet.OpenVanilla(paths.GameDataDir, n => n.StartsWith("terrain", StringComparison.OrdinalIgnoreCase) || n.StartsWith("data", StringComparison.OrdinalIgnoreCase));
    foreach (var g in arrays.Groups)
    {
        var owner = set.FindOwner(g.BaseColour);
        var entry = owner?.Entries[PackFile.Normalize(g.BaseColour)];
        Console.WriteLine($"{g.Index,2} {g.Name,-16} {(owner == null ? "MISSING" : Path.GetFileName(owner.SourcePath) + (entry!.IsCompressed ? " (compressed)" : ""))}");
    }
}
