using System.Diagnostics;
using AtlasWH3.Core.Exporters;
using AtlasWH3.Formats.Esf;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Packs;
using AtlasWH3.Formats.Terry;
using AtlasWH3.Formats.Trees;

namespace AtlasWH3.Core.Campaign.Trees;

/// <summary>
/// campaign_maps\&lt;map&gt;\display\trees\trees.campaign_tree_list (WH3 v4), BOB's "Campaign Trees":
///  - the hex grid from map_data.esf's bounds and the composited CampaignTree map (2 px per hex; 255 = empty);
///  - tree ids from campaign_tree_ids in the packs, the map's mod packs first (mods add their own trees);
///  - placement, ids and rotations by <see cref="CampaignTreeGenerator"/>;
///  - heights by <see cref="TreeHeightField"/>: the nearest full_logic_map texel, raised by the height patches of the
///    layers' props (read from the packs, not working_data).
/// Inputs are loose files and packs, so there is no pack round trip: BOB's action read the logic map through the
/// game's file system, i.e. from the installed pack.
/// </summary>
public sealed class TreesStep : ICampaignBuildStep
{
    /// <summary>Palette index Terry leaves where no layer painted a tree.</summary>
    public const byte EmptyIndex = 255;

    public string Name => "trees";
    public string ReplacesBobAction => "Campaign Trees";
    public IReadOnlyList<string> DependsOn => ["heightmaps"];

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx)
    {
        var missing = new List<string>();
        if (!File.Exists(ctx.TerryFile)) missing.Add($"missing {ctx.TerryFile}");
        else
        {
            var project = TerryProject.Load(ctx.TerryFile);
            if (project.Find("CampaignTree") is not { } map) missing.Add("no CampaignTree map in the .terry");
            else
                try { TerrainComposite.Inputs(project, map); }
                catch (FileNotFoundException e) { missing.Add(e.Message); }
        }
        if (MapDataPath(ctx) is null)
            missing.Add($"missing {Path.Combine(ctx.Paths.AkWorkingDir, "campaign_maps", ctx.CampaignMapName, "map_data.esf")} (CAIME's output)");
        if (!Directory.Exists(ctx.Paths.GameDataDir)) missing.Add($"missing game data folder {ctx.Paths.GameDataDir}");
        return missing;
    }

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        var project = TerryProject.Load(ctx.TerryFile);
        var bounds = MapDataBounds.Read(MapDataPath(ctx)!);
        var (map, palette) = TerrainComposite.Indexed(project, "CampaignTree");
        if (palette is null) throw new InvalidDataException("the CampaignTree map has no palette");
        var grid = HexGrid.ForTreeMap(map.Width, map.Height, bounds.Width);
        if (MathF.Abs(grid.WorldHeight - bounds.Height) > 1e-3f)
            notes.Add($"the {grid.Columns}x{grid.Rows} tree grid is {grid.WorldHeight} deep, map_data.esf says {bounds.Height}");

        ctx.Log("tree tables and height patches (packs)...");
        var packs = GameSetup.OpenWithLinked(ctx.Paths.GameDataDir, ctx.Paths.ModPacks);
        var db = TreeDatabase.FromPacks(packs);
        var colours = CampaignTreeGenerator.ReadTreeMap(map, palette, grid, EmptyIndex);
        ReportUnknownColours(colours, db, ctx.Paths.ModPacks, notes);

        var logicPath = LogicMapPath(ctx);
        if (logicPath is null) throw new FileNotFoundException("no full_logic_map.compressed_map (run step 'heightmaps')");
        if (!logicPath.StartsWith(ctx.TargetRoot, StringComparison.OrdinalIgnoreCase)) notes.Add($"logic map from {logicPath}");
        var patches = TreeHeightField.LoadPatches(project, packs, notes);
        ctx.Cancel.ThrowIfCancellationRequested();
        ctx.Log("placing trees...");
        // the terrain provider's bounds are the project's world_width, not map_data.esf's (Old World: 1367.4 vs 1367.396)
        var field = new TreeHeightField(CompressedMap.Read(logicPath), project.WorldWidth ?? bounds.Width, patches);
        var list = CampaignTreeGenerator.Generate(colours, grid, db, (_, _, x, z) => field.Height(x, z));

        var path = Path.Combine(ctx.TargetRoot, TreeExporter.PackPath(ctx.MapName));
        list.Save(path);
        notes.Add($"{list.TotalInstances} trees of {list.Types.Count} types on a {grid.Columns}x{grid.Rows} hex grid " +
                  $"({db.Ids.Count} tree ids)");
        return new StepResult(Name, [path], notes, sw.Elapsed);
    }

    /// <summary>map_data.esf: the build output's, else the kit's working_data (CAIME writes it there); of
    /// <see cref="CampaignBuildContext.CampaignMapName"/>, the main map for a devastated project.</summary>
    public static string? MapDataPath(CampaignBuildContext ctx) =>
        new[] { Path.Combine(ctx.TargetRoot, "campaign_maps", ctx.CampaignMapName, "map_data.esf"),
                Path.Combine(ctx.Paths.AkWorkingDir, "campaign_maps", ctx.CampaignMapName, "map_data.esf") }
            .FirstOrDefault(File.Exists);

    /// <summary>full_logic_map.compressed_map: this build's (step heightmaps), else the kit's working_data.</summary>
    public static string? LogicMapPath(CampaignBuildContext ctx) =>
        new[] { ctx.OutFile("full_logic_map.compressed_map"),
                Path.Combine(ctx.Paths.AkWorkingDir, "terrain", "campaigns", ctx.MapName, "full_logic_map.compressed_map") }
            .FirstOrDefault(File.Exists);

    /// <summary>Painted colours that no campaign_tree_ids row has: BOB places no tree there either, usually because the mod
    /// pack that defines them is not linked.</summary>
    private static void ReportUnknownColours(int[] colours, TreeDatabase db, IReadOnlyList<string> modPacks, List<string> notes)
    {
        var known = db.ColourGroups();
        var unknown = colours.Where(c => c != CampaignTreeGenerator.NoTree && !known.ContainsKey((uint)c & 0xFFFFFF))
            .GroupBy(c => c).OrderByDescending(g => g.Count()).ToList();
        if (unknown.Count == 0) return;
        notes.Add($"{unknown.Sum(g => g.Count())} hexes have a tree colour no campaign_tree_ids row has, so they get no tree: " +
                  string.Join(", ", unknown.Take(8).Select(g => $"#{g.Key:X6} ({g.Count()})")) + (unknown.Count > 8 ? ", ..." : "") +
                  (modPacks.Count == 0 ? ". No mod pack is linked: link the pack that defines these trees (--pack)" : ""));
    }
}
