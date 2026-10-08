using System.Diagnostics;
using System.Xml.Linq;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign.AiPathfinding;

/// <summary>
/// campaign_maps\&lt;map&gt;\spd_data.esf and hlp_data.esf: the campaign AI's offline pathfinding data, which only the
/// game itself writes (CAI_PATHFINDER::reprocess_spd_data / CAI_TRANSITION_DATA::save_hlp_data during a startpos
/// build). Inputs: pathfinding.ppd and map_data.esf (CAIME / map data export) plus three DB values from the kit.
/// </summary>
public sealed class AiPathfindingStep : ICampaignBuildStep
{
    public string Name => "hlp_spd";
    public string ReplacesBobAction => "game startpos build: reprocess_spd_data / reprocess_hlp_data (no BOB action)";
    public IReadOnlyList<string> DependsOn => [];

    /// <summary>pathfinding.ppd / map_data.esf: the build output's campaign_maps folder first, else the kit's working_data.</summary>
    public static string? Input(CampaignBuildContext ctx, string file) =>
        new[] { Path.Combine(ctx.CampaignMapOutDir, file), Path.Combine(ctx.Paths.AkWorkingCampaignMapDir, file) }
            .FirstOrDefault(File.Exists);

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx)
    {
        var missing = new List<string>();
        foreach (var f in new[] { "pathfinding.ppd", "map_data.esf" })
            if (Input(ctx, f) is null) missing.Add($"missing {f} (in {ctx.CampaignMapOutDir} or {ctx.Paths.AkWorkingCampaignMapDir})");
        return missing;
    }

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        var written = new List<string>();
        var ppd = PathfindingPpd.Read(Input(ctx, "pathfinding.ppd")!);
        var regions = MapDataRegions.Read(Input(ctx, "map_data.esf")!);
        var settings = DbSettings(ctx.Paths, notes);
        ctx.Log($"grid {ppd.Width}x{ppd.Height}, {regions.Regions.Count} region slots, road {settings.RoadCost}, beach {settings.LandToSeaCost}/{settings.SeaToLandCost}");
        if (ppd.Width > SpdBuilder.SparseMapSize || ppd.Height > SpdBuilder.SparseMapSize)
            notes.Add($"map is {ppd.Width}x{ppd.Height}: the game's spd table (CAI_SPARSE_MAP<1024>) folds hexes beyond 1023 onto the last 32 columns/rows; written the same way");
        var grid = new CampaignPathGrid(ppd, regions, settings);
        var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Directory.CreateDirectory(ctx.CampaignMapOutDir);

        var t = Stopwatch.StartNew();
        var spd = SpdBuilder.Build(grid, timestamp, ctx.Log);
        var spdPath = Path.Combine(ctx.CampaignMapOutDir, "spd_data.esf");
        File.WriteAllBytes(spdPath, spd.ToBytes());
        written.Add(spdPath);
        ctx.Log($"spd_data.esf {spd.Width}x{spd.Height} in {t.Elapsed.TotalSeconds:F1} s");

        t.Restart();
        var hlp = HlpBuilder.Build(ppd, regions, settings, timestamp, ctx.Log);
        var hlpPath = Path.Combine(ctx.CampaignMapOutDir, "hlp_data.esf");
        File.WriteAllBytes(hlpPath, hlp.ToBytes());
        written.Add(hlpPath);
        ctx.Log($"hlp_data.esf {hlp.Nodes.Count} nodes in {t.Elapsed.TotalSeconds:F1} s");
        return new StepResult(Name, written, notes, sw.Elapsed);
    }

    /// <summary>Road cost (campaign_map_roads: lowest threshold among the campaigns played on this map) and the beach
    /// costs (campaign_variables) from the kit's raw_data\db; CA's defaults when the tables are missing.</summary>
    public static CampaignPathGrid.Settings DbSettings(ProjectPaths paths, List<string>? notes = null)
    {
        var db = Path.Combine(paths.AssemblyKitRoot, "raw_data", "db");
        var s = new CampaignPathGrid.Settings();
        try
        {
            var campaigns = new HashSet<string>();
            var playable = Path.Combine(db, "campaign_map_playable_areas.xml");
            if (File.Exists(playable))
                foreach (var r in XDocument.Load(playable).Root!.Elements("campaign_map_playable_areas"))
                    if ((string?)r.Element("mapname") == paths.MapName && (string?)r.Element("campaign_key") is { } key)
                        campaigns.Add(key);
            var roads = Path.Combine(db, "campaign_map_roads.xml");
            if (File.Exists(roads))
            {
                var best = XDocument.Load(roads).Root!.Elements("campaign_map_roads")
                    .Where(r => campaigns.Contains((string?)r.Element("campaign") ?? ""))
                    .OrderBy(r => float.Parse((string?)r.Element("threshold") ?? "0", System.Globalization.CultureInfo.InvariantCulture))
                    .FirstOrDefault();
                if (best is not null) s = s with { RoadCost = uint.Parse((string)best.Element("movement_cost")!) };
                else notes?.Add($"no campaign_map_roads row for {paths.MapName}'s campaigns ({string.Join(", ", campaigns)}); road cost {s.RoadCost}");
            }
            var vars = Path.Combine(db, "campaign_variables.xml");
            if (File.Exists(vars))
                foreach (var r in XDocument.Load(vars).Root!.Elements("campaign_variables"))
                {
                    var key = (string?)r.Element("variable_key");
                    var value = (string?)r.Element("value");
                    if (value is null) continue;
                    var v = (uint)float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    if (key == "pathfinding_land_to_sea_beach_transition_action_point_cost") s = s with { LandToSeaCost = v };
                    if (key == "pathfinding_sea_to_land_beach_transition_action_point_cost") s = s with { SeaToLandCost = v };
                }
        }
        catch (Exception e)
        {
            notes?.Add($"DB values unreadable ({e.Message}); CA defaults used");
        }
        return s;
    }
}
