using System.Diagnostics;
using AtlasWH3.Core.Exporters;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign;

/// <summary>lf height / sea height maps (.compressed_map + .dds) and climate_map.cm.</summary>
public sealed class RastersStep : ICampaignBuildStep
{
    public string Name => "rasters";
    public string ReplacesBobAction => "Terrain / Height map compressed + Height map DDS (land, sea), climate map";
    public IReadOnlyList<string> DependsOn => [];

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx) =>
        File.Exists(ctx.TerryFile) ? [] : [$"missing {ctx.TerryFile}"];

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var result = new CompiledTerrainExporter(ctx.Paths).ExportRasters(ctx.TargetRoot, ctx.Log);
        return new StepResult(Name, result.Written, result.Notes, sw.Elapsed);
    }
}

/// <summary>
/// global_map\: global_blend.dds, texture_arrays.xml and the tile_list.bin copy (part of BOB's Global Mesh action).
///  - global_blend.dds byte 0 = the blend TIF's palette index, byte 1 = climate_map.cm repeated 4x4
///  - texture_arrays.xml is the same for every 3K map
///  - global_map\tile_list.bin = the root tile_list.bin with base-tile flags cleared (<see cref="TileList.ToGlobalMapCopy"/>)
/// </summary>
public sealed class GlobalMapStep : ICampaignBuildStep
{
    public string Name => "global_map";
    public string ReplacesBobAction => "Terrain / Global Mesh (global_map\\ part)";
    public IReadOnlyList<string> DependsOn => ["rasters", "tile_list"];

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx)
    {
        var missing = new List<string>();
        if (!File.Exists(ctx.TerryFile)) missing.Add($"missing {ctx.TerryFile}");
        if (!File.Exists(ctx.OutFile("climate_map.cm"))) missing.Add("missing climate_map.cm (run step 'rasters')");
        if (TextureArraysSource(ctx) is null) missing.Add("no texture_arrays.xml to copy (vanilla root or working_data)");
        return missing;
    }

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var written = new List<string>();
        var notes = new List<string>();
        var outDir = ctx.OutFile("global_map");
        Directory.CreateDirectory(outDir);

        ctx.Log("global_blend.dds...");
        var project = TerryProject.Load(ctx.TerryFile);
        var blendMap = project.Find("BlendCampaign") ?? throw new InvalidDataException("No BlendCampaign map in the .terry");
        var (group, _) = TiffMap.ReadPalette8(project.LayerTifPath(blendMap));
        var climate = CompressedMap.Read(ctx.OutFile("climate_map.cm")).Raster;
        if (climate.Width * 4 != group.Width || climate.Height * 4 != group.Height)
            notes.Add($"blend {group.Width}x{group.Height} is not 4x climate_map.cm {climate.Width}x{climate.Height}; climate clamped at the edges");
        var climateFull = new Raster<byte>(group.Width, group.Height);
        for (var y = 0; y < group.Height; y++)
        for (var x = 0; x < group.Width; x++)
            climateFull[x, y] = (byte)climate.GetClamped(x / 4, y / 4);
        var blendPath = Path.Combine(outDir, "global_blend.dds");
        TerrainDds.WriteBlend(blendPath, group, climateFull);
        written.Add(blendPath);

        var arrays = Path.Combine(outDir, "texture_arrays.xml");
        var arraysSource = TextureArraysSource(ctx)!;
        // building into the kit's own working_data: the source is the output file itself
        if (!string.Equals(Path.GetFullPath(arraysSource), Path.GetFullPath(arrays), StringComparison.OrdinalIgnoreCase))
            File.Copy(arraysSource, arrays, true);
        written.Add(arrays);

        var rootTiles = new[] { ctx.OutFile("tile_list.bin"),
                                Path.Combine(ctx.Paths.AkWorkingDir, "terrain", "campaigns", ctx.MapName, "tile_list.bin") }
            .FirstOrDefault(File.Exists);
        if (rootTiles is not null)
        {
            ctx.Log("global_map\\tile_list.bin...");
            var copy = Path.Combine(outDir, "tile_list.bin");
            TileList.Read(rootTiles).ToGlobalMapCopy().Write(copy);
            written.Add(copy);
        }
        else notes.Add("no root tile_list.bin (output or working_data); global_map\\tile_list.bin not written");
        return new StepResult(Name, written, notes, sw.Elapsed);
    }

    private static string? TextureArraysSource(CampaignBuildContext ctx) =>
        new[] { ctx.Paths.TextureArraysXml,
                Path.Combine(ctx.Paths.AkWorkingDir, "terrain", "campaigns", ctx.MapName, "global_map", "texture_arrays.xml") }
            .FirstOrDefault(File.Exists);
}

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
