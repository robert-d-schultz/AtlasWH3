using System.Diagnostics;
using AtlasWH3.Core.Campaign.TileMapCheck;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign;

/// <summary>
/// terrain\campaigns\&lt;map&gt;\tile_list.bin (BOB "Terrain / Tilemap"): <see cref="TileMatchSimulator"/> places the tiles
/// from tile_map.png and the climate map, then <see cref="TileListWriter"/> runs BOB's river flow pass, the lf min/max
/// heights (the .terry's lf maps, as the rasters step; the sea map for use_alt_lf tiles) and writes the BATTLE_TILE_MAP records.
/// The tile map validator's errors are reported as blocking problems first.
/// </summary>
public sealed class TileListStep : ICampaignBuildStep
{
    public string Name => "tile_list";
    public string ReplacesBobAction => "Terrain / Tilemap";
    public IReadOnlyList<string> DependsOn => ["rasters"];

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx)
    {
        var missing = new List<string>();
        foreach (var f in new[] { "lf_heights.tif", "lf_sea_heights.tif" })
            if (!File.Exists(Path.Combine(ctx.Paths.AkTerrainDir, f))) missing.Add($"missing {Path.Combine(ctx.Paths.AkTerrainDir, f)}");
        string tileMap;
        try { tileMap = TileMapInput(ctx, out _); }
        catch (Exception e) when (e is IOException or InvalidDataException) { missing.Add($"tile map ({ctx.Paths.TileMap.Describe(ctx.Paths)}): {e.Message}"); return missing; }
        if (!File.Exists(tileMap)) missing.Add($"missing {tileMap}");
        if (missing.Count > 0) return missing;
        missing.AddRange(TileMapValidator.Run(ctx.Paths, new TileMapCheckOptions { TileMap = tileMap }).Findings
            .Where(f => f.Severity == TileMapFinding.Error && !ctx.AcceptedTileMapIssues.Contains(f.Code))
            .Select(f => $"tile map {f.Code}: {f.Message}"));
        return missing;
    }

    /// <summary>The tile_map.png to build from (<see cref="ProjectPaths.TileMap"/>): a pack entry is extracted to
    /// &lt;target&gt;\_atlaswh3_inputs\&lt;map&gt; first.</summary>
    private static string TileMapInput(CampaignBuildContext ctx, out string note) =>
        ctx.Paths.TileMap.BuildInput(ctx.Paths, Path.Combine(ctx.TargetRoot, "_atlaswh3_inputs", ctx.MapName), out note);

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        var dir = ctx.Paths.AkTerrainDir;
        var db = TileMapValidator.LoadDatabase(ctx.Paths);
        var tileMapPath = TileMapInput(ctx, out var source);
        ctx.Log(source);
        notes.Add(source);
        var map = HexTileMap.Read(tileMapPath);
        ctx.Log("placing tiles...");
        var sim = new TileMatchSimulator(db) { Log = ctx.Log, Cancel = ctx.Cancel }.Run(map, TileMapValidator.ClimateIndices(map, dir, db));
        var byLocation = db.Tiles.GroupBy(t => t.Variations[0].Location, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var placed = sim.Tiles.Select(t => new PlacedTile(byLocation[t.Location], t.X, t.Y, t.Rotation, t.Climate, t.Layer));

        ctx.Log("flow, heights, records...");
        // Same height sources as the rasters step (the .terry's lf layers), so tile min/max match lf_height_map. The kit's
        // lf_heights.tif can be stale (main190: ranges_relief edits only the .height tif), which flattens mountain
        // tiles and sinks river water below the land mesh.
        var lf = HeightField.FromRaster(TiffMap.ReadGray16(LfTif(ctx, "LowFrequencyHeight", "lf_heights.tif")));
        var sea = HeightField.FromRaster(TiffMap.ReadGray16(LfTif(ctx, "LowFrequencyHeightSea", "lf_sea_heights.tif")));
        var list = TileListWriter.Build(db, map.PixelWidth, map.PixelHeight, placed, lf, sea, t => t.UseAltLf);

        Directory.CreateDirectory(ctx.TerrainOutDir);
        var path = ctx.OutFile("tile_list.bin");
        list.Write(path);
        notes.Add($"{list.Records.Count} records ({sim.Tiles.Count} placed), {list.Paths.Count} tiles used");
        if (sim.NoTile.Count > 0) notes.Add($"{sim.NoTile.Count} tile-map points got no tile (holes in game)");
        return new StepResult(Name, [path], notes, sw.Elapsed);
    }

    /// <summary>The .terry's map of <paramref name="type"/>, else the kit's <paramref name="fallback"/> tif.</summary>
    private static string LfTif(CampaignBuildContext ctx, string type, string fallback)
    {
        if (File.Exists(ctx.TerryFile))
        {
            var project = TerryProject.Load(ctx.TerryFile);
            if (project.Find(type) is { } layer && File.Exists(project.LayerTifPath(layer)))
                return project.LayerTifPath(layer);
        }
        return Path.Combine(ctx.Paths.AkTerrainDir, fallback);
    }
}
