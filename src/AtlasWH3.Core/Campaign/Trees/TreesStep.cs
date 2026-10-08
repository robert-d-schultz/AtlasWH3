using System.Diagnostics;
using AtlasWH3.Core.Campaign.GlobalMesh;
using AtlasWH3.Core.Campaign.Terrain;
using AtlasWH3.Formats.Packs;
using AtlasWH3.Core.Exporters;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;
using AtlasWH3.Formats.Trees;

namespace AtlasWH3.Core.Campaign.Trees;

/// <summary>
/// campaign_maps\&lt;map&gt;\display\trees\trees.campaign_tree_list from the AK CampaignTree map
/// (BOB "Campaign Trees", <see cref="CampaignTreeGenerator"/>).
/// Heights: a tree whose regenerated (x, z) is bit-identical to the reference list's tree on that hex keeps the
/// reference y (terrain unchanged there), so an unchanged map rebuilds byte for byte. Other trees take the lf height
/// in BOB's tile space (x, z / 1.15476 over tiles × tile size) plus the per-tile hf of river/road/canal tiles
/// (<see cref="TileHfHeight"/>, when a tile list exists): vanilla bit-exact ~61%, within 1e-5 on 99.87%.
/// </summary>
public sealed class TreesStep : ICampaignBuildStep
{
    /// <summary>BOB divides z by this before querying campaign terrain (QTU TileMapProcessed surface).</summary>
    public const float CampaignZScale = 1.15476f;

    public string Name => "trees";
    public string ReplacesBobAction => "Terrain / Campaign Trees";
    // tile_list: only orders the two when both are selected (the hf terrain reads the fresh tile list)
    public IReadOnlyList<string> DependsOn => ["rasters", "tile_list"];

    /// <summary>Keep the reference list's heights where a tree lands on exactly the same spot.</summary>
    public bool ReuseReferenceHeights { get; init; } = true;

    /// <summary>How far (world units) a reference height may be from today's lf ground and still be reused.</summary>
    public const float ReuseTolerance = 0.08f;

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx)
    {
        var missing = new List<string>();
        if (!File.Exists(ctx.TerryFile)) missing.Add($"missing {ctx.TerryFile}");
        else if (TerryProject.Load(ctx.TerryFile).Find("CampaignTree") is null) missing.Add("no CampaignTree map in the .terry");
        foreach (var tsv in new[] { ctx.Paths.TreeIdsTsv, ctx.Paths.TreeVariantsTsv, ctx.Paths.SeasonsTsv })
            if (!File.Exists(tsv)) missing.Add($"missing {tsv}");
        return missing;
    }

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        var db = TreeDatabase.Load(ctx.Paths.TreeIdsTsv, ctx.Paths.TreeVariantsTsv, ctx.Paths.SeasonsTsv);

        var project = TerryProject.Load(ctx.TerryFile);
        var (map, palette) = TiffMap.ReadPalette8(project.LayerTifPath(project.Find("CampaignTree")!));
        var tileSize = GlobalMeshStep.TileSize;
        var grid = HexGrid.ForTreeMap(map.Width, map.Height, (float)(map.Width * (595.1 / 1784)));
        var colours = CampaignTreeGenerator.ReadTreeMap(map, palette, grid, AkExporter.NoTreeIndex);

        var reference = ReuseReferenceHeights && ctx.ReuseTreeHeights ? ReferenceTrees(ctx, grid, notes) : null;
        LfSampler? lf = null;
        var lfPath = ctx.OutFile("lf_height_map.compressed_map");
        if (File.Exists(lfPath))
            lf = new LfSampler(CompressedMap.Read(lfPath), map.Width * tileSize, map.Height * tileSize, tileSize);
        else notes.Add("no lf_height_map.compressed_map (run step 'rasters'); trees not in the reference list get y = 0");
        var terrain = lf is null || !UseTileHf ? null : TileHeights(ctx, notes);

        int reused = 0, sampled = 0;
        var list = CampaignTreeGenerator.Generate(colours, grid, db, (col, row, x, z) =>
        {
            var ground = terrain?.TreeHeight(x, z) ?? lf?.Height(x, z / CampaignZScale);
            // reuse only where the terrain is unchanged: same spot AND the reference y still on today's ground (lake
            // shaping / island flattening / terrain polish moved the ground under kept trees, 2026-10-04)
            if (reference != null && reference.TryGetValue((col, row), out var r) &&
                BitConverter.SingleToInt32Bits(r.X) == BitConverter.SingleToInt32Bits(x) &&
                BitConverter.SingleToInt32Bits(r.Z) == BitConverter.SingleToInt32Bits(z) &&
                (ground is null || Math.Abs(r.Y - ground.Value) <= ReuseTolerance))
            {
                reused++;
                return r.Y;
            }
            sampled++;
            return ground ?? 0f;
        });

        var path = Path.Combine(ctx.TargetRoot, TreeExporter.PackPath(ctx.MapName));
        list.Save(path);
        notes.Add($"{list.TotalInstances} trees of {list.Types.Count} types on a {grid.Columns}x{grid.Rows} hex grid; " +
                  $"heights: {reused} from the reference list, {sampled} computed ({(terrain is null ? "lf" : "lf + tile hf")})");
        if (list.TotalInstances == 0) notes.Add("the CampaignTree map has no tree colours: the list is empty");
        return new StepResult(Name, [path], notes, sw.Elapsed);
    }

    /// <summary>Add the per-tile hf of the tile list's tiles (rivers, roads, canals) to the lf height, as BOB does.</summary>
    public bool UseTileHf { get; init; } = true;

    /// <summary>BOB's tile-space terrain height (lf + per-tile hf) when a tile list and the game's tile database exist.</summary>
    private static TileHfHeight? TileHeights(CampaignBuildContext ctx, List<string> notes)
    {
        // BOB's Campaign Trees reads the tile list through the game's file system (the packed/compiled one), not the
        // kit's working copy: on vanilla the kit tile list (built from the kit tile map) moves 1,359 tree heights
        var tl = new[] { ctx.OutFile("tile_list.bin"),
                         Path.Combine(ctx.Paths.TerrainDir, "tile_list.bin"),
                         Path.Combine(ctx.Paths.AkWorkingDir, "terrain", "campaigns", ctx.MapName, "tile_list.bin") }.FirstOrDefault(File.Exists);
        if (tl is null || !Directory.Exists(ctx.Paths.GameDataDir))
        {
            notes.Add("no tile_list.bin or game data folder: tree heights are lf only (no river/road/canal hf)");
            return null;
        }
        var packs = PackSet.OpenVanilla(ctx.Paths.GameDataDir);
        var prefix = PackFile.Normalize(TileDatabase.Folder);
        var db = TileDatabase.Load(packs.Packs.SelectMany(p => p.Entries.Keys).Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Distinct().Select(k => packs.TryRead(k)).OfType<byte[]>());
        var lfMap = CompressedMap.Read(ctx.OutFile("lf_height_map.compressed_map"));
        notes.Add($"tree heights: lf + tile hf ({Path.GetFileName(Path.GetDirectoryName(tl))}/tile_list.bin)");
        // Campaign Trees' provider (qttoolutility FUN_18011f1d0): the tiles registered at the point's cell, highest
        // answering height (vanilla: all 205,767 trees bit-exact against BOB's own output)
        return new TileHfHeight(TileList.Read(tl), db, packs.TryRead, lfMap, GlobalMeshStep.TileSize) { BobCells = true };
    }

    /// <summary>The reference list's trees by hex (compiled root, then working_data), if it is on the same grid.</summary>
    private static Dictionary<(int, int), TreeInstance>? ReferenceTrees(CampaignBuildContext ctx, HexGrid grid, List<string> notes)
    {
        var path = new[] { ctx.Paths.TreeList, Path.Combine(ctx.Paths.AkWorkingDir, TreeExporter.PackPath(ctx.MapName)) }
            .FirstOrDefault(File.Exists);
        if (path is null) return null;
        var list = CampaignTreeList.Load(path);
        if (list.WorldWidth != grid.WorldWidth || list.WorldHeight != grid.WorldHeight)
        {
            notes.Add($"reference tree list {path} is {list.WorldWidth}x{list.WorldHeight}, not " +
                      $"{grid.WorldWidth}x{grid.WorldHeight}; heights not reused");
            return null;
        }
        var byHex = new Dictionary<(int, int), TreeInstance>();
        foreach (var t in list.Types.SelectMany(type => type.Instances))
            byHex.TryAdd(grid.HexAt(t.X, t.Z), t);
        return byHex;
    }
}
