using System.Diagnostics;
using AtlasWH3.Core.Campaign.Terrain;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Models;

namespace AtlasWH3.Core.Campaign.Rivers;

/// <summary>models\river_N.* from the ECRiverSpline entities of the AK layers.</summary>
public sealed class RiversStep : ICampaignBuildStep
{
    /// <summary>World units per lf pixel along x and z in the props/hex world (vanilla 595.1 / 7136, 541.786 / 5620).</summary>
    public const double WorldPerPixelX = 595.1 / 7136, WorldPerPixelZ = 541.78619 / 5620;

    public string Name => "rivers";
    public string ReplacesBobAction => "Terrain / Terry file (models\\river_N)";
    public IReadOnlyList<string> DependsOn => ["rasters"];

    /// <summary>Number river_N by the entity names (CA's shipped vanilla files) instead of BOB's numbering. Keep in step
    /// with GlobalPropsBuilder.RiverNumbersByName.</summary>
    public bool RiverNumbersByName { get; init; }

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx)
    {
        var missing = new List<string>();
        // only the wide geometry samples the terrain (terrain_relative splines) and sizes the world uv from the lf map
        if (Wide(ctx) && !File.Exists(ctx.OutFile("lf_height_map.compressed_map"))) missing.Add("missing lf_height_map.compressed_map (run step 'rasters')");
        if (!Directory.Exists(ctx.Paths.AkTerrainDir)) missing.Add($"missing {ctx.Paths.AkTerrainDir}");
        else if (RiverLayers(ctx).Count == 0) missing.Add("no layer with ECRiverSpline entities in the AK map folder");
        return missing;
    }

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        float worldW = 0, worldH = 0;
        Func<double, double, double>? terrain = null;
        void LoadTerrain()
        {
            if (terrain is not null) return;
            var lf = CompressedMap.Read(ctx.OutFile("lf_height_map.compressed_map"));
            worldW = (float)(lf.Raster.Width * WorldPerPixelX);
            worldH = (float)(lf.Raster.Height * WorldPerPixelZ);
            // terrain height for terrain_relative splines: props-world z maps onto the square-pixel terrain grid
            var terrainH = lf.Raster.Height / 4 * TileHfHeight.TileSize3K;
            var sampler = new LfSampler(lf, lf.Raster.Width / 4 * TileHfHeight.TileSize3K, terrainH, TileHfHeight.TileSize3K);
            var h = worldH;
            terrain = (x, z) => sampler.Height((float)x, (float)(z * terrainH / h));
        }

        var rivers = RiverLayers(ctx).SelectMany(RiverBuilder.ReadLayer).OrderBy(r => r.Number).ToList();
        // BOB's river_N numbering (by region, RiverNumbering.Bob) whenever the region lookup is available, as global_props
        var why = "";
        if (!RiverNumbersByName && Props.HexRegionLookup.ForMap(ctx.Paths, out why) is { } lookup)
        {
            var numbers = RiverNumbering.Bob(RiverNumbering.Read(RiverLayers(ctx)),
                (x, z) => lookup.RegionAt(x, z) ?? Props.GlobalPropsStep.NonPlayable);
            rivers = rivers.Select(r => numbers.TryGetValue(r.Name, out var n) ? r with { Number = n } : r).OrderBy(r => r.Number).ToList();
            notes.Add("river_N numbered as BOB (by region)");
        }
        else notes.Add("river_N numbered by entity name (vanilla files)" + (RiverNumbersByName ? "" : $"; no region lookup ({why})"));
        var duplicates = rivers.GroupBy(r => r.Number).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0) throw new InvalidDataException($"river numbers used twice (entity names river_N): {string.Join(", ", duplicates)}");

        var models = ctx.OutFile("models");
        Directory.CreateDirectory(models);
        foreach (var old in Directory.EnumerateFiles(models, "river_*"))
            File.Delete(old);

        var written = new List<string>();
        var bob = !Wide(ctx);
        var bounds = bob ? BobRiver.MapBounds(ctx.Paths) : null;
        if (bob && bounds is null)
        {
            bob = false;
            notes.Add("no campaign_map_playable_areas row for the map: wide (game-valid) river geometry instead of BOB's");
        }
        notes.Add(bob ? "river geometry: BOB's (identical to BOB's files apart from its uninitialised bytes)"
                      : "river geometry: wide (game-valid, covers the land-mesh river holes)");
        foreach (var river in rivers)
        {
            RigidModelV2 model;
            if (bob)
            {
                if (river.Points.Count < 2) { notes.Add($"{river.Name}: fewer than 2 spline points, skipped"); continue; }
                var raw = BobRiver.BuildRaw(BobRiver.BuildSpline(river), BobRiver.RiverPointsInOrder(river).Select(p => (float)p.Width).ToList(), bounds!.Value);
                model = BobRiver.ToModel(raw);
            }
            else
            {
                LoadTerrain();
                var sections = RiverBuilder.Sample(river, terrain);
                if (sections.Count < 2) { notes.Add($"{river.Name}: fewer than 2 cross-sections, skipped"); continue; }
                model = RiverBuilder.BuildModel(sections, worldW, worldH);
            }
            var mesh = Path.Combine(models, $"river_{river.Number}.wsmodel.rigid_model_v2");
            model.Write(mesh);
            var wsmodel = Path.Combine(models, $"river_{river.Number}.wsmodel");
            File.WriteAllText(wsmodel, WsModel.River(ctx.MapName, river.Number, river.Material, bob ? "\n" : "\r\n"));
            written.AddRange([mesh, wsmodel]);
        }
        notes.Add($"{rivers.Count} rivers");
        return new StepResult(Name, written, notes, sw.Elapsed);
    }

    private static bool Wide(CampaignBuildContext ctx) => ctx.RiverGeometry.Equals("wide", StringComparison.OrdinalIgnoreCase);

    private static List<string> RiverLayers(CampaignBuildContext ctx) =>
        Directory.EnumerateFiles(ctx.Paths.AkTerrainDir, "*.layer")
            .Where(f => File.ReadLines(f).Take(4000).Any(l => l.Contains("<ECRiverSpline", StringComparison.Ordinal)))
            .ToList();
}
