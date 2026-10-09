using System.Diagnostics;
using AtlasWH3.Core.Campaign.TileMapCheck;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign;

/// <summary>
/// terrain\campaigns\&lt;map&gt;\tile_list.bin, tile_mask.dds and patch_mask.dds (BOB "Tilemap"): <see cref="TileMatchSimulator"/> places
/// the tiles from tile_map.png on the campaign tile database of the game packs, then <see cref="TileListWriter"/> runs
/// BOB's river flow pass, the low/high heights and writes the records and the masks. The heights come from the .terry's composited Height and
/// HeightSea maps, the ones the heightmaps step compiles: BOB reads them from the installed pack (the heightmap →
/// tilemap pack round trip), this step from the sources. The tile map validator's errors are blocking problems.
/// </summary>
public sealed class TileListStep : ICampaignBuildStep
{
    public string Name => "tile_list";
    public string ReplacesBobAction => "Tilemap";
    public IReadOnlyList<string> DependsOn => [];

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx)
    {
        var missing = new List<string>();
        if (!File.Exists(ctx.TerryFile)) missing.Add($"missing {ctx.TerryFile}");
        else
        {
            var project = TerryProject.Load(ctx.TerryFile);
            foreach (var type in new[] { "Height", "HeightSea" })
            {
                if (project.Find(type) is not { } map) { missing.Add($"no {type} map in the .terry"); continue; }
                try { TerrainComposite.Inputs(project, map); }
                catch (FileNotFoundException e) { missing.Add(e.Message); }
            }
        }
        string tileMap;
        try { tileMap = TileMapInput(ctx, out _); }
        catch (Exception e) when (e is IOException or InvalidDataException) { missing.Add($"tile map ({ctx.Paths.TileMap.Describe(ctx.Paths)}): {e.Message}"); return missing; }
        if (!File.Exists(tileMap)) missing.Add($"missing {tileMap}");
        if (missing.Count > 0) return missing;
        missing.AddRange(TileMapValidator.Run(ctx.Paths, new TileMapCheckOptions { TileMap = tileMap, TileMapOnly = true }).Findings
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
        var db = TileMapValidator.LoadDatabase(ctx.Paths);
        var tileMapPath = TileMapInput(ctx, out var source);
        ctx.Log(source);
        notes.Add(source);
        var map = HexTileMap.Read(tileMapPath);
        ctx.Log("placing tiles...");
        var sim = new TileMatchSimulator(db) { Log = ctx.Log, Cancel = ctx.Cancel }
            .Run(map, TileMapValidator.ClimateIndices(map, Path.GetDirectoryName(Path.GetFullPath(tileMapPath))!, db));
        var byLocation = db.Tiles.GroupBy(t => t.Variations[0].Location, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var placed = sim.Tiles.Select(t => new PlacedTile(byLocation[t.Location], t.X, t.Y, t.Rotation, t.Climate, t.Layer)).ToList();

        ctx.Log("heights...");
        var project = TerryProject.Load(ctx.TerryFile);
        var land = HeightField.FromRaster(TerrainComposite.Heights(project, "Height"));
        var sea = HeightField.FromRaster(TerrainComposite.Heights(project, "HeightSea"));
        ctx.Cancel.ThrowIfCancellationRequested();
        ctx.Log("flow, records...");
        var list = TileListWriter.Build(db, map.PixelWidth, map.PixelHeight, placed, land, sea, out var tileMask, out var patchMask);

        Directory.CreateDirectory(ctx.TerrainOutDir);
        var path = ctx.OutFile("tile_list.bin");
        list.Write(path);
        var maskPath = ctx.OutFile("tile_mask.dds");
        File.WriteAllBytes(maskPath, tileMask);
        var patchPath = ctx.OutFile("patch_mask.dds");
        File.WriteAllBytes(patchPath, patchMask);
        notes.Add($"{list.Records.Count} records ({sim.Tiles.Count} placed), {list.Paths.Count} tiles used");
        if (sim.NoTile.Count > 0) notes.Add($"{sim.NoTile.Count} tile-map points got no tile (holes in game)");
        return new StepResult(Name, [path, maskPath, patchPath], notes, sw.Elapsed);
    }
}
