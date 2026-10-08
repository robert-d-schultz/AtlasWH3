using System.Diagnostics;
using AtlasWH3.Core.Exporters;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign;

/// <summary>campaign_maps\&lt;map&gt;\*lookup*.bmp → .tga, .dds and _minimap.tga (BOB "Texture / Convert lookup texture").</summary>
public sealed class LookupStep : ICampaignBuildStep
{
    public string Name => "lookup";
    public string ReplacesBobAction => "Texture / Convert lookup texture";
    public IReadOnlyList<string> DependsOn => [];

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx) =>
        Sources(ctx).Count > 0 ? [] : [$"no *lookup*.bmp in {ctx.Paths.AkWorkingCampaignMapDir} or {ctx.Paths.AkDesignCampaignMapDir}"];

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var written = new List<string>();
        Directory.CreateDirectory(ctx.CampaignMapOutDir);
        foreach (var bmp in Sources(ctx))
        {
            ctx.Log($"{Path.GetFileName(bmp)}...");
            var lookup = LookupTexture.FromBmp(bmp);
            var stem = Path.Combine(ctx.CampaignMapOutDir, Path.GetFileNameWithoutExtension(bmp));
            File.WriteAllBytes(stem + ".tga", lookup.ToTga());
            File.WriteAllBytes(stem + ".dds", lookup.ToDds());
            File.WriteAllBytes(stem + "_minimap.tga", lookup.Minimap().ToTga());
            written.AddRange([stem + ".tga", stem + ".dds", stem + "_minimap.tga"]);
        }
        return new StepResult(Name, written, [], sw.Elapsed);
    }

    /// <summary>Lookup bitmaps by file name; the working_data copy wins over EmpireDesignData.</summary>
    private static List<string> Sources(CampaignBuildContext ctx) =>
        new[] { ctx.Paths.AkWorkingCampaignMapDir, ctx.Paths.AkDesignCampaignMapDir }
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*lookup*.bmp"))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
}

/// <summary>A BOB action whose algorithm has not been reverse-engineered yet (Phase 2).</summary>
/// <param name="preflight">Optional check of the BOB action's own inputs, so <c>diagnose</c> reports them before BOB runs.</param>
public sealed class PendingStep(string name, string bobAction, string[] dependsOn, string whatIsMissing,
                                Func<CampaignBuildContext, IEnumerable<string>>? preflight = null) : ICampaignBuildStep
{
    public string Name => name;
    public string ReplacesBobAction => bobAction;
    public IReadOnlyList<string> DependsOn => dependsOn;
    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx) =>
        [$"not native yet: {whatIsMissing}", .. preflight?.Invoke(ctx) ?? []];
    public StepResult Run(CampaignBuildContext ctx) => throw new NotSupportedException($"Step '{name}' is not native yet: {whatIsMissing}");
}
