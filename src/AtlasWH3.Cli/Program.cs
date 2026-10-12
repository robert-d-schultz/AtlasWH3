using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AtlasWH3.Core;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Trees;
using AtlasWH3.Formats.Packs;

var paths = ProjectPaths.FromArgs(args, out args);
var command = args.Length > 0 ? args[0] : "help";

switch (command)
{
    case "help" or "--help" or "-h" or "/?":
        Help();
        break;
    case "version" or "--version":
        Console.WriteLine($"AtlasWH3 {Version()}");
        break;
    case "trees-roundtrip":
        TreesRoundtrip(paths);
        break;
    case "find-textures":
        FindTextures(paths);
        break;
    case "build-campaign":
        return BuildCampaign(paths, args.Skip(1).ToArray());
    case "diagnose-campaign":
        return DiagnoseCampaign(paths, args.Skip(1).ToArray());
    case "gp-model-box":
        GpModelBox(args.Skip(1).ToArray());
        break;
    case "merge-pack":
        return MergePack(args.Skip(1).ToArray());
    case "make-pack":
        return MakePack(args.Skip(1).ToArray());
    case "parity":
        return ParityCheck(paths, args.Skip(1).ToArray());
    case "validate-tilemap":
        return ValidateTilemap(paths, args.Skip(1).ToArray());
    case var c when PropCommands.Names.Contains(c):
        return PropCommands.Run(paths, c, args.Skip(1).ToArray());
    case var c when NormalCommands.Names.Contains(c):
        return NormalCommands.Run(paths, c, args.Skip(1).ToArray());
    case var c when Gp27Commands.Names.Contains(c):
        return Gp27Commands.Run(paths, c, args.Skip(1).ToArray());
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
    case var c when DbCommands.Names.Contains(c):
        return DbCommands.Run(paths, c, args.Skip(1).ToArray());
    case var c when BobCommands.Names.Contains(c):
        return BobCommands.Run(paths, c, args.Skip(1).ToArray());
    case var c when FixtureCommands.Names.Contains(c):
        return FixtureCommands.Run(paths, c, args.Skip(1).ToArray());
    default:
        Console.Error.WriteLine($"unknown command '{command}' (AtlasWH3.Cli help lists them)");
        return 2;
}
return 0;

// The version from Directory.Build.props, without the +commit suffix.
static string Version() =>
    (typeof(BuildCommands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

static void Help()
{
    Console.WriteLine($"AtlasWH3 {Version()}: Total War: WARHAMMER III campaign map builds without BOB.");
    Console.WriteLine();
    Console.WriteLine("Build");
    Console.WriteLine("  new-project <file.atlaswh3>              a project with the default build profile (from --map, --ak, --pack)");
    Console.WriteLine("  build --project <file.atlaswh3> [--segments validate,compile,custom,pack,install] [--steps a,b]");
    Console.WriteLine("        [--custom name,..] [--out <dir>] [--pack-output <file>] [--json]");
    Console.WriteLine("                                           a project's build, as the app's Build window runs it");
    Console.WriteLine("  build-campaign [--steps a,b] [--out <dir>] [--accept-tilemap code,..] [--patch-mask fitted|vanilla]");
    Console.WriteLine("        [--devastated <map>|none] [--lookup-last <region,..|file.txt>] [--json]");
    Console.WriteLine("                                           the compile steps alone, without a project");
    Console.WriteLine("  diagnose-campaign [--out <dir>] [--json]  every step's input check");
    Console.WriteLine("  validate-tilemap [--tilemap <png>] [--climate-dir <dir>] [--db <_tile_database>] [--simulate]");
    Console.WriteLine("        [--overlay <png>] [--tilemap-only] [--json]");
    Console.WriteLine("                                           pre-flight check of tile_map.png (exit 1 on errors)");
    Console.WriteLine("  hlp-spd [--in <dir>] [--out <dir>] [--compare <dir>] [--only hlp|spd]");
    Console.WriteLine("                                           hlp_data.esf and spd_data.esf from pathfinding.ppd and map_data.esf");
    Console.WriteLine("  setup [--db <dir>] [--compiled <dir>] [--maps]   first-run data from the game install");
    Console.WriteLine();
    Console.WriteLine("Packs");
    Console.WriteLine("  make-pack <root folder> <out.pack> [--exclude substring]..");
    Console.WriteLine("  merge-pack <base.pack> <override root> <out.pack> [--exclude substring].. [--replace-dir pack/folder/]..");
    Console.WriteLine();
    Console.WriteLine("Editing the kit's sources (preview; JSON out)");
    Console.WriteLine("  tiles-info | tiles-get | tiles-edit --ops <json> [--dry-run|--force|--allow-warnings|--preview]");
    Console.WriteLine("  tiles-validate | tiles-preview | tiles-undo | tiles-checkpoint | tiles-rollback | tiles-history");
    Console.WriteLine("  tiles-replay | tiles-holes | tiles-simulate | tiles-errors | tiles-fix   [--tilemap <png>]");
    Console.WriteLine("  props-layers | props-list | props-get | props-models | props-ground | props-edit --ops <json>");
    Console.WriteLine("  props-undo | props-checkpoint <label> | props-rollback <label> | props-history | props-preview");
    Console.WriteLine("  entity-types | entity-type <type> | entity-project | entity-layers [--all] | entity-get <id>..");
    Console.WriteLine("  entity-query [--type a,b] [--layer l] [--where ECX.f~v].. [--rect x0,z0,x1,z1] [--full]");
    Console.WriteLine("  entity-edit --ops <json> | entity-undo | entity-checkpoint <label> | entity-rollback <label>");
    Console.WriteLine("  entity-history   (entity-*: --project <.terry>, default the campaign map's)");
    Console.WriteLine("  prefab-list [--filter s] | prefab-get <key>");
    Console.WriteLine("  asset-info <path> | asset-find <filter> [--ext e] | asset-preview <model> [--out png --size --yaw --pitch --lod]");
    Console.WriteLine("  asset-census [--ext .rigid_model_v2|.wsmodel|.material|.dds] | asset-check   (--mod-pack p, --asset-root dir)");
    Console.WriteLine();
    Console.WriteLine("Parity and research");
    Console.WriteLine("  parity <builtDir> <referenceDir>|--fixture <name> [--mask-junk] [--json]");
    Console.WriteLine("  fixture-freeze <name> --maps a,b [--packs p,q] [--replace] | fixture-list | fixture-check [name]");
    Console.WriteLine("  fixture-delete <name>   (fixture-*: --store <dir>, default atlaswh3_fixtures beside the kit)");
    Console.WriteLine("  bob-actions | bob-scratch --maps a,b | bob-run --maps a,b   BOB headless, in an isolated copy of the kit");
    Console.WriteLine("  db-check | db-dump <table> [--limit n] | db-schema-snapshot [--ron <schema_wh3.ron>] [--out <json>]");
    Console.WriteLine("  terry-schema [--root <dir>].. [--out <json>]   the component schema from the kit's raw_data");
    Console.WriteLine("  esf-roundtrip <file.esf>.. | trees-roundtrip | find-textures | gp-model-box <rigid_model_v2>");
    Console.WriteLine($"  {string.Join(" | ", Gp27Commands.Names)} | {string.Join(" | ", NormalCommands.Names)}");
    Console.WriteLine();
    Console.WriteLine("Global options: --ak <assembly kit root>  --map <map name>  --root <compiled root>  --pack <mod pack> (repeatable)");
    Console.WriteLine("help | version");
}

// Runs the native campaign build. Exit code 0 only when every selected step succeeded.
static int BuildCampaign(ProjectPaths paths, string[] a)
{
    a = TakeOption(a, "--out", out var outDir);
    a = TakeOption(a, "--steps", out var steps);
    a = TakeOption(a, "--accept-tilemap", out var accept);
    a = TakeOption(a, "--patch-mask", out var patchMask);
    a = TakeOption(a, "--devastated", out var devastated);
    a = TakeOption(a, "--lookup-last", out var lookupLast);
    var json = TakeFlag(ref a, "--json");
    var sw = Stopwatch.StartNew();
    var ctx = new CampaignBuildContext(paths, outDir, json ? Console.Error.WriteLine : Console.WriteLine)
    {
        AcceptedTileMapIssues = (accept ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(),
        PatchMask = patchMask is null ? PatchMaskMode.Fitted : Enum.Parse<PatchMaskMode>(patchMask, ignoreCase: true),
        DevastatedMap = CampaignBuildContext.DevastatedSetting(devastated),
        LookupLastRegions = (lookupLast ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
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

static int ParityCheck(ProjectPaths paths, string[] a)
{
    var maskJunk = TakeFlag(ref a, "--mask-junk");
    var json = TakeFlag(ref a, "--json");
    if (Array.IndexOf(a, "--fixture") is var f and >= 0 && f + 1 < a.Length)
    {
        var fixture = FixtureCommands.Open(FixtureCommands.Store(paths, a), a[f + 1]);
        a = [.. a.Where((_, i) => i != f && i != f + 1), Path.Combine(fixture.MapsRoot, "working_data")];
    }
    if (a.Length < 2) { Console.Error.WriteLine("parity <builtDir> <referenceDir>|--fixture <name> [--mask-junk] [--json]"); return 2; }
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

// gp-model-box <rigid_model_v2>: per-LOD mesh header bounds and vertex bounds (global_props quadtree box research).
static void GpModelBox(string[] a)
{
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
