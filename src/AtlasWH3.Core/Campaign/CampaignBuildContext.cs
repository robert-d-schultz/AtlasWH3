namespace AtlasWH3.Core.Campaign;

/// <summary>Where a native campaign build reads its sources and writes its loose output. The target root is laid out
/// like working_data / a pack: <c>terrain\campaigns\&lt;map&gt;\…</c> and <c>campaign_maps\&lt;map&gt;\…</c>, so pointing it at
/// the assembly kit's working_data replaces BOB's output in place.</summary>
public sealed class CampaignBuildContext
{
    public ProjectPaths Paths { get; }
    public string TargetRoot { get; }
    public Action<string> Log { get; }
    /// <summary>Tile-map pre-flight error codes the user accepts for this build (e.g. layout.mesh_columns); the
    /// tile_list step reports them as accepted instead of blocking.</summary>
    public IReadOnlySet<string> AcceptedTileMapIssues { get; init; } = new HashSet<string>();
    /// <summary>Cancels the build; long steps check it between rows / meshes, the pipeline between steps.</summary>
    public CancellationToken Cancel { get; init; }

    /// <summary>trees step: keep the reference list's heights where a tree is unchanged (false = compute every height).</summary>
    public bool ReuseTreeHeights { get; init; } = true;

    /// <summary>rivers step: "bob" = BOB's own river meshes and height patches (identical to BOB's files apart from the
    /// bytes BOB leaves uninitialised); "wide" = the wider game-valid water that also covers the land-mesh river holes.</summary>
    public string RiverGeometry { get; init; } = "bob";

    public CampaignBuildContext(ProjectPaths paths, string? targetRoot = null, Action<string>? log = null)
    {
        Paths = paths;
        TargetRoot = targetRoot ?? System.IO.Path.Combine(paths.OutputRoot, "compiled", paths.MapName);
        Log = log ?? (_ => { });
    }

    public string MapName => Paths.MapName;
    public string TerrainOutDir => System.IO.Path.Combine(TargetRoot, "terrain", "campaigns", MapName);
    public string CampaignMapOutDir => System.IO.Path.Combine(TargetRoot, "campaign_maps", MapName);
    public string TerryFile => System.IO.Path.Combine(Paths.AkTerrainDir, MapName + ".terry");

    public string OutFile(params string[] relative) =>
        System.IO.Path.Combine([TerrainOutDir, .. relative]);
}

/// <summary>Result of one build step.</summary>
public sealed record StepResult(string Step, IReadOnlyList<string> Written, IReadOnlyList<string> Notes, TimeSpan Elapsed);

/// <summary>One former BOB action, reimplemented natively.</summary>
public interface ICampaignBuildStep
{
    /// <summary>Short id used by the CLI / MCP (<c>rasters</c>, <c>global_map</c>, …).</summary>
    string Name { get; }
    /// <summary>The BOB action this replaces.</summary>
    string ReplacesBobAction { get; }
    /// <summary>Steps whose output this one reads.</summary>
    IReadOnlyList<string> DependsOn { get; }
    /// <summary>Missing inputs, as messages; empty when the step can run.</summary>
    IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx);
    StepResult Run(CampaignBuildContext ctx);
}
