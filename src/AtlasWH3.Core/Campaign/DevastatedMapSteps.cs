using System.Diagnostics;

namespace AtlasWH3.Core.Campaign;

/// <summary>
/// The devastated project's tile list, global map and masks, built into the cache for
/// <see cref="DevastationPiecesStep"/> (<see cref="DevastationPiecesStep.BuildDevastated"/>). Nothing of the main map's
/// build goes in, so it runs from the start of a build, beside the main map's steps.
/// </summary>
public sealed class DevastatedTilesStep : ICampaignBuildStep
{
    public string Name => "devastated_tiles";
    public string ReplacesBobAction => "Devastation pieces (the devastated project's tile list, global map and masks)";
    public IReadOnlyList<string> DependsOn => [];

    /// <summary>The devastated project's steps this one builds.</summary>
    public static readonly string[] Steps = ["tile_list", "global_map", "masks"];

    /// <summary>The sub-steps check their own inputs; their problems are notes, as in devastation_pieces.</summary>
    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx) => [];

    public StepResult Run(CampaignBuildContext ctx) => DevastatedHeightsStep.Build(this, ctx, Steps);
}

/// <summary>
/// The devastated project's heightmaps and trees, built into the cache for <see cref="DevastationPiecesStep"/>. After
/// the main map's heightmaps: full_height_map.dds copies the blocks that are the main map's from its file
/// (<see cref="CampaignBuildContext.HeightMapReference"/>).
/// </summary>
public sealed class DevastatedHeightsStep : ICampaignBuildStep
{
    public string Name => "devastated_heights";
    public string ReplacesBobAction => "Devastation pieces (the devastated project's heightmaps and trees)";
    public IReadOnlyList<string> DependsOn => ["heightmaps"];

    /// <summary>The devastated project's steps this one builds.</summary>
    public static readonly string[] Steps = ["heightmaps", "trees"];

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx) => [];

    public StepResult Run(CampaignBuildContext ctx) => Build(this, ctx, Steps);

    internal static StepResult Build(ICampaignBuildStep step, CampaignBuildContext ctx, IReadOnlyList<string> steps)
    {
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        if (DevastationPiecesStep.DevastatedMapName(ctx) is { } devastated)
            DevastationPiecesStep.BuildDevastated(ctx, devastated, steps, notes);
        else notes.Add("no devastated project: nothing to build");
        // the cache is not build output: nothing of it goes into a pack
        return new StepResult(step.Name, [], notes, sw.Elapsed);
    }
}
