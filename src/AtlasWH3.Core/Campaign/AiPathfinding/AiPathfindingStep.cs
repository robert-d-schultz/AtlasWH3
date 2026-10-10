using System.Diagnostics;
using System.Globalization;
using AtlasWH3.Formats.Db;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign.AiPathfinding;

/// <summary>
/// campaign_maps\&lt;map&gt;\spd_data.esf and hlp_data.esf (WH3 v1): the campaign AI's offline pathfinding data, which
/// otherwise only the game itself writes. Inputs: pathfinding.ppd and map_data.esf (CAIME / map data export) plus three
/// DB values from the map's mod packs and the vanilla db (docs/hlp_spd.md).
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
        var grid = new CampaignPathGrid(ppd, regions, settings);
        var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Directory.CreateDirectory(ctx.CampaignMapOutDir);

        var t = Stopwatch.StartNew();
        var spd = SpdBuilder.Build(grid, regions, timestamp, ctx.Log);
        var spdPath = Path.Combine(ctx.CampaignMapOutDir, "spd_data.esf");
        File.WriteAllBytes(spdPath, spd.ToBytes());
        written.Add(spdPath);
        ctx.Log($"spd_data.esf {spd.Width}x{spd.Height} in {t.Elapsed.TotalSeconds:F1} s");

        t.Restart();
        var hlp = HlpBuilder.Build(ppd, regions, settings, timestamp, ctx.Log, spd: spd);
        var hlpPath = Path.Combine(ctx.CampaignMapOutDir, "hlp_data.esf");
        File.WriteAllBytes(hlpPath, hlp.ToBytes());
        written.Add(hlpPath);
        ctx.Log($"hlp_data.esf {hlp.Nodes.Count} nodes in {t.Elapsed.TotalSeconds:F1} s");
        return new StepResult(Name, written, notes, sw.Elapsed);
    }

    /// <summary>Road cost (campaign_map_roads: the lowest threshold's movement_cost among the campaigns of this map's
    /// campaign_map_playable_areas rows) and the beach costs (campaign_variables), from the map's mod packs and the
    /// vanilla db packs (a higher-priority pack winning), as the game reads them; CA's defaults when a row is missing.
    /// (The kit's raw_data\db is not used: its combi rows name campaign wh3_main_combi_old, which has no road rows.)</summary>
    public static CampaignPathGrid.Settings DbSettings(ProjectPaths paths, List<string>? notes = null)
    {
        var s = new CampaignPathGrid.Settings();
        if (!Directory.Exists(paths.GameDataDir))
        {
            notes?.Add($"no game data folder ({paths.GameDataDir}); CA's default road and beach costs");
            return s;
        }
        var packs = GameSetup.OpenWithLinked(paths.GameDataDir, paths.ModPacks, n => n.StartsWith("db", StringComparison.OrdinalIgnoreCase));
        var campaigns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, row) in DbBinaryTable.PackRows(packs, "campaign_map_playable_areas_tables"))
            if (string.Equals((string?)t.Get(row, "mapname"), paths.MapName, StringComparison.OrdinalIgnoreCase) && t.Get(row, "campaign_key") is string key)
                campaigns.Add(key);
        var roads = new Dictionary<string, (float Threshold, uint Cost)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, row) in DbBinaryTable.PackRows(packs, "campaign_map_roads_tables"))
            if (t.Get(row, "key") is string key && !roads.ContainsKey(key) && t.Get(row, "campaign") is string campaign && campaigns.Contains(campaign))
                roads[key] = (Convert.ToSingle(t.Get(row, "threshold"), CultureInfo.InvariantCulture), Convert.ToUInt32(t.Get(row, "movement_cost"), CultureInfo.InvariantCulture));
        if (roads.Count > 0) s = s with { RoadCost = roads.Values.MinBy(r => r.Threshold).Cost };
        else notes?.Add($"no campaign_map_roads row for {paths.MapName}'s campaigns ({string.Join(", ", campaigns)}); road cost {s.RoadCost}");
        var seen = new HashSet<string>();
        foreach (var (t, row) in DbBinaryTable.PackRows(packs, "campaign_variables_tables"))
        {
            if (t.Get(row, "variable_key") is not string key || !seen.Add(key) || t.Get(row, "value") is not { } value) continue;
            var v = (uint)Convert.ToSingle(value, CultureInfo.InvariantCulture);
            if (key == "pathfinding_land_to_sea_beach_transition_action_point_cost") s = s with { LandToSeaCost = v };
            if (key == "pathfinding_sea_to_land_beach_transition_action_point_cost") s = s with { SeaToLandCost = v };
        }
        return s;
    }
}
