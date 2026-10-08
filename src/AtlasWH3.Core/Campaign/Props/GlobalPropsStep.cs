using System.Diagnostics;
using System.Xml.Linq;
using AtlasWH3.Core.Campaign.Rivers;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Packs;
using AtlasWH3.Formats.Props;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign.Props;

/// <summary>global_props.bin from the exported AK layers (BOB "Terry file").</summary>
public sealed class GlobalPropsStep : ICampaignBuildStep
{
    public string Name => "global_props";
    public string ReplacesBobAction => "Terrain / Terry file (global_props.bin)";
    public IReadOnlyList<string> DependsOn => ["rasters", "rivers"];

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx)
    {
        var missing = new List<string>();
        if (!File.Exists(ctx.TerryFile)) missing.Add($"missing {ctx.TerryFile}");
        if (!File.Exists(ctx.OutFile("lf_height_map.compressed_map"))) missing.Add("missing lf_height_map.compressed_map (run step 'rasters')");
        if (!Directory.Exists(ctx.Paths.GameDataDir)) missing.Add($"missing game data folder {ctx.Paths.GameDataDir} (record templates, model bounds)");
        return missing;
    }

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var lf = CompressedMap.Read(ctx.OutFile("lf_height_map.compressed_map"));
        var worldW = lf.Raster.Width * RiversStep.WorldPerPixelX;
        var worldH = lf.Raster.Height * RiversStep.WorldPerPixelZ;
        ctx.Log("templates and model bounds from the game packs...");

        var project = TerryProject.Load(ctx.TerryFile);
        // BOB keeps a layer's region only if the map has it (campaign_map_regions); other layers go to the
        // non-playable region
        var regions = MapRegions(ctx);
        var notes = new List<string>();
        var layers = ExportedLayers(ctx.TerryFile)
            .Select(l => (Region: regions.Count == 0 || regions.Contains(l.Name) ? l.Name : NonPlayable, Path: project.LayerFilePath(l.Id)))
            .Where(l => File.Exists(l.Path))
            .ToList();
        if (regions.Count == 0) notes.Add("no campaign_map_regions rows for this map in EmpireDesignData; layer names used as regions");
        else notes.Add($"{layers.Count(l => l.Region == NonPlayable)} layers without a region on this map -> {NonPlayable}");
        // BOB puts every object in the map.hex region under its entity's position, whatever layer it came from
        var lookup = HexRegionLookup.ForMap(ctx.Paths, out var why);
        var builder = new GlobalPropsBuilder(PackSet.OpenVanilla(ctx.Paths.GameDataDir), worldW, worldH)
        {
            Prefabs = PrefabLibrary.ForKit(ctx.Paths.AssemblyKitRoot, "campaign"),
            QuadRoot = lookup?.QuadRoot,
            Debug = Environment.GetEnvironmentVariable("ATLASWH3_GP_TRACE") is { Length: > 0 } trace ? TraceTo(trace) : null,
        };
        if (lookup is null) notes.Add($"region by layer name only ({why})");
        else notes.Add("regions from map.hex at each object's position (as BOB)");
        ctx.Log($"{layers.Count} exported layers...");
        var entries = builder.Build(ctx.MapName, layers, lookup is null ? null : (x, z) => lookup.RegionAt(x, z) ?? NonPlayable);
        var outPath = ctx.OutFile("global_props.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllBytes(outPath, GlobalProps.Pack(entries));

        // summary by re-reading what was written
        var check = GlobalProps.Load(outPath).ReadRegions(ctx.MapName);
        notes.InsertRange(0, new[]
        {
            $"{layers.Count} layers -> {check.Count} regions, {entries.Count} bmd bodies",
            $"{check.Sum(r => r.Props.Count):N0} props, {check.Sum(r => r.Vfx.Count):N0} VFX, {check.Sum(r => r.PointLights.Count):N0} lights, " +
            $"{check.Sum(r => r.CompositeScenes.Count):N0} composite scenes, {check.Sum(r => r.Sounds.Count):N0} sounds, " +
            $"{check.Sum(r => r.LightProbes.Count):N0} light probes, {check.Sum(r => r.PolyMeshes.Count):N0} polygon meshes",
        });
        notes.AddRange(builder.Notes);
        return new StepResult(Name, [outPath], notes, sw.Elapsed);
    }

    public const string NonPlayable = "3k_main_reg_non_playable";

    /// <summary>Region keys of this map from raw_data\EmpireDesignData\campaign_map_regions.xml (empty if none).</summary>
    public static HashSet<string> MapRegions(CampaignBuildContext ctx)
    {
        var file = Path.Combine(ctx.Paths.AssemblyKitRoot, "raw_data", "EmpireDesignData", "campaign_map_regions.xml");
        if (!File.Exists(file)) return [];
        return XDocument.Load(file).Descendants("campaign_map_regions")
            .Where(e => (string?)e.Element("campaign_map") == ctx.MapName)
            .Select(e => (string?)e.Element("region") ?? "")
            .Where(r => r.Length > 0)
            .ToHashSet();
    }

    /// <summary>(layer name, id) of every layer file the .terry exports (ECLayerFile with ECLayerExport export="true").</summary>
    public static List<(string Name, string Id)> ExportedLayers(string terryPath) =>
        XDocument.Load(terryPath).Descendants("entity")
            .Where(e => e.Element("ECLayerFile") is not null && (string?)e.Element("ECLayerExport")?.Attribute("export") == "true")
            .Select(e => ((string?)e.Attribute("name") ?? "", (string)e.Attribute("id")!))
            .Where(l => l.Item1.Length > 0)
            .ToList();

    private static Action<string> TraceTo(string path)
    {
        var w = new StreamWriter(path) { AutoFlush = true };
        return w.WriteLine;
    }
}
